using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runs a level's gates (GatePlacement): each floats over its tile, and when
/// one of its deployments' triggers fires it sends out that squad — minions
/// of the gate's team, at the gate. A deployment can repeat every N minutes,
/// up to a maximum number of times (or forever). A gate whose deployments
/// are all done disappears.
///
/// What spawned squads then do is up to their team (later, the AI brains):
/// for now they're ordinary minions of that team.
///
/// Scene setup: none — GameManager2D adds one and calls Begin.
/// </summary>
public class GateSystem : MonoBehaviour
{
    public static GateSystem Instance { get; private set; }

    [SerializeField] private FactionID localPlayer = FactionID.Player;
    [Tooltip("Height the gate floats at above its tile, in tiles.")]
    [SerializeField] private float hoverHeight = 0.9f;
    [Tooltip("Seconds between trigger checks.")]
    [SerializeField, Min(0.05f)] private float checkInterval = 0.25f;

    private class Deployment
    {
        public GateDeployment Data;
        public int   TimesFired;
        public float NextAt = -1f;

        public bool CanRepeat =>
            Data.repeatMinutes > 0f && (!Data.maxRepeats.HasValue || TimesFired - 1 < Data.maxRepeats.Value);
        public bool Finished => TimesFired > 0 && !CanRepeat;
    }

    private class Gate
    {
        public GatePlacement Data;
        public readonly List<Deployment> Deployments = new();
        public GameObject Visual;
        public bool Gone;
    }

    private readonly List<Gate> _gates = new();
    private readonly Dictionary<string, Deployment> _byId = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<FactionID> _fallen = new();
    private float _seconds;
    private float _nextCheck;
    private bool  _begun;

    /// <summary>Seconds of mission time the gates have been running (saved with the game).</summary>
    public float Seconds => _seconds;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        DungeonHeart.OnFactionEliminated += OnHeartFell;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        DungeonHeart.OnFactionEliminated -= OnHeartFell;
    }

    private void OnHeartFell(FactionID faction) => _fallen.Add(faction);

    // ── Setting up ─────────────────────────────────────────────────────

    /// <summary>The level is loaded: its gates appear, their clocks start.</summary>
    public void Begin(LevelData level)
    {
        foreach (var g in _gates) if (g.Visual != null) Destroy(g.Visual);
        _gates.Clear();
        _byId.Clear();
        _fallen.Clear();
        _seconds   = 0f;
        _nextCheck = 0f;
        _begun     = true;

        if (level?.gates == null) return;
        foreach (var data in level.gates)
        {
            if (data == null) continue;
            var gate = new Gate { Data = data };
            foreach (var d in data.deployments ?? new List<GateDeployment>())
            {
                if (d == null) continue;
                var dep = new Deployment { Data = d };
                gate.Deployments.Add(dep);
                if (!string.IsNullOrEmpty(d.id) && !_byId.ContainsKey(d.id)) _byId[d.id] = dep;
            }
            gate.Visual = BuildVisual(gate);
            _gates.Add(gate);
        }
    }

    // ── Running ────────────────────────────────────────────────────────

    private void Update()
    {
        if (!_begun) return;
        _seconds += Time.deltaTime;
        if (Time.time < _nextCheck) return;
        _nextCheck = Time.time + checkInterval;

        foreach (var gate in _gates)
        {
            if (gate.Gone) continue;
            foreach (var dep in gate.Deployments)
            {
                bool due = dep.TimesFired == 0 ? TriggerMet(dep.Data.trigger)
                                                : dep.CanRepeat && _seconds >= dep.NextAt;
                if (due) Fire(gate, dep);
            }
            if (gate.Deployments.TrueForAll(d => d.Finished)) Close(gate);
        }
        UpdateFog();
    }

    private bool TriggerMet(DeploymentTrigger t)
    {
        if (t == null) return false;
        switch (t.kind)
        {
            case DeploymentTriggerKind.AfterTime:
                return _seconds >= t.minutes * 60f;
            case DeploymentTriggerKind.SquadDefeated:
                return !string.IsNullOrEmpty(t.deploymentId) && _byId.TryGetValue(t.deploymentId, out var other) &&
                       other.TimesFired > 0 && !SquadAlive(other);
            case DeploymentTriggerKind.TilesClaimed:
                return MatchStats.Instance != null && MatchStats.Instance.Get(t.team, MissionStats.TerritoryClaimed) >= t.tiles;
            case DeploymentTriggerKind.HeartDestroyed:
                return t.team == FactionID.Unaligned ? _fallen.Count > 0 : _fallen.Contains(t.team);
            default:
                return false;
        }
    }

    /// <summary>Any member of the deployment's latest squad still alive and on the gate's side.</summary>
    private bool SquadAlive(Deployment dep)
    {
        var gate = _gates.Find(g => g.Deployments.Contains(dep));
        foreach (var m in MinionController.All)
            if (m != null && m.IsAlive && m.SquadId == dep.Data.id && (gate == null || m.Faction == gate.Data.factionId))
                return true;
        return false;
    }

    private void Fire(Gate gate, Deployment dep)
    {
        dep.TimesFired++;
        dep.NextAt = _seconds + Mathf.Max(0f, dep.Data.repeatMinutes) * 60f;

        int spawned = 0;
        foreach (var member in dep.Data.squad)
            for (int i = 0; i < Mathf.Max(1, member.count); i++)
                if (Spawn(gate, dep, member)) spawned++;

        Debug.Log($"[GateSystem] {gate.Data.id} sent {dep.Data.id} ({spawned} minion(s)) " +
                  $"for {gate.Data.factionId} — {dep.Data.trigger?.Describe()}" +
                  (dep.TimesFired > 1 ? $", repeat {dep.TimesFired - 1}." : "."));
    }

    private bool Spawn(Gate gate, Deployment dep, SquadMember member)
    {
        var gm   = GameManager2D.Instance;
        var grid = gm != null ? gm.Grid : null;
        var def  = gm != null && gm.Factions != null ? gm.Factions.FindMinion(member.contentFactionId, member.minionId) : null;
        if (grid == null || def == null)
        {
            Debug.LogWarning($"[GateSystem] {dep.Data.id}: no minion {member.contentFactionId} {member.minionId} — skipped.");
            return false;
        }
        var prefab = def.prefab != null ? def.prefab
                   : MinionSummoner.Instance != null ? MinionSummoner.Instance.Template : null;
        if (prefab == null) { Debug.LogError("[GateSystem] No minion template to spawn from."); return false; }

        Vector2 jitter = Random.insideUnitCircle * 0.25f * grid.CellSize;
        Vector3 at = grid.CellToWorld(gate.Data.x, gate.Data.y) + new Vector3(jitter.x, 0f, jitter.y);
        var go = Instantiate(prefab, at, Quaternion.identity);
        go.name = $"{def.minionId}_{gate.Data.factionId}_{dep.Data.id}";
        if (!go.TryGetComponent(out MinionController minion)) { Destroy(go); return false; }

        minion.Initialise(gate.Data.factionId, def, Mathf.Max(1, member.level), MinionController.SpawnSource.Gate);
        minion.JoinSquad(dep.Data.id);
        if (member.isLord) minion.MarkAsLord();
        return true;
    }

    /// <summary>Its last deployment is done: the gate goes.</summary>
    private void Close(Gate gate)
    {
        gate.Gone = true;
        if (gate.Visual != null) Destroy(gate.Visual);
        Debug.Log($"[GateSystem] {gate.Data.id} has closed — its last deployment is done.");
    }

    // ── Looks ──────────────────────────────────────────────────────────

    /// <summary>A placeholder: two crossed rings in the team's colour, turning slowly above the tile.</summary>
    private GameObject BuildVisual(Gate gate)
    {
        var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (grid == null) return null;
        float cell = grid.CellSize;
        Color colour = grid.Tiles.GetFactionColour(gate.Data.factionId);

        var root = new GameObject($"Gate_{gate.Data.id}");
        root.transform.SetParent(transform, false);
        root.transform.position = grid.CellToWorld(gate.Data.x, gate.Data.y) + Vector3.up * hoverHeight * cell;
        root.AddComponent<GateSpin>();

        for (int i = 0; i < 2; i++)
        {
            var ring = new GameObject("Ring").AddComponent<LineRenderer>();
            ring.transform.SetParent(root.transform, false);
            ring.transform.localRotation = Quaternion.Euler(0f, i * 90f, 0f);
            ring.loop          = true;
            ring.useWorldSpace = false;
            ring.startWidth    = ring.endWidth = 0.05f * cell;
            ring.material      = new Material(Shader.Find("Unlit/Color")) { color = colour };
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            const int segments = 48;
            ring.positionCount = segments;
            for (int s = 0; s < segments; s++)
            {
                float a = s * Mathf.PI * 2f / segments;
                ring.SetPosition(s, new Vector3(Mathf.Cos(a) * 0.4f * cell, Mathf.Sin(a) * 0.4f * cell, 0f));
            }
        }
        return root;
    }

    /// <summary>An enemy's gate shows only where the local player can see.</summary>
    private void UpdateFog()
    {
        var fog = FogOfWar.Instance;
        var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        foreach (var gate in _gates)
        {
            if (gate.Gone || gate.Visual == null) continue;
            bool shown = fog == null || gate.Data.factionId == localPlayer ||
                         (grid != null && fog.IsVisible(localPlayer, grid.GetCell(gate.Data.x, gate.Data.y)));
            if (gate.Visual.activeSelf != shown) gate.Visual.SetActive(shown);
        }
    }

    // ── Save / load ────────────────────────────────────────────────────

    public List<GateSaveData> Capture()
    {
        var list = new List<GateSaveData>();
        foreach (var gate in _gates)
        {
            var save = new GateSaveData { gateId = gate.Data.id, gone = gate.Gone };
            foreach (var d in gate.Deployments)
                save.deployments.Add(new DeploymentSaveData
                {
                    deploymentId = d.Data.id,
                    timesFired   = d.TimesFired,
                    nextAt       = d.TimesFired > 0 ? d.NextAt : -1f,
                });
            list.Add(save);
        }
        return list;
    }

    /// <summary>Puts back a saved mission's gate progress and clock (after Begin with the same level).</summary>
    public void Restore(List<GateSaveData> saved, float seconds, IEnumerable<FactionID> fallenHearts)
    {
        _seconds = Mathf.Max(0f, seconds);
        if (fallenHearts != null) foreach (var f in fallenHearts) _fallen.Add(f);
        if (saved == null) return;
        foreach (var s in saved)
        {
            var gate = _gates.Find(g => g.Data.id == s.gateId);
            if (gate == null) continue;
            foreach (var d in s.deployments)
            {
                var dep = gate.Deployments.Find(x => x.Data.id == d.deploymentId);
                if (dep == null) continue;
                dep.TimesFired = Mathf.Max(0, d.timesFired);
                dep.NextAt     = d.nextAt;
            }
            if (s.gone) Close(gate);
        }
    }
}

/// <summary>Turns a gate's rings slowly.</summary>
public class GateSpin : MonoBehaviour
{
    private void Update() => transform.Rotate(0f, 30f * Time.deltaTime, 0f, Space.World);
}
