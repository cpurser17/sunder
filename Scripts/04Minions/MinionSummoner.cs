using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Timer-driven minion summoning for one faction.
///
/// Every faction's roster is a list of MinionDefinitions it MIGHT summon. On
/// each tick, eligibility is narrowed by four independent gates — population
/// headroom, mission design (GameManager2D.AllowedMinionIds), rooms built,
/// and research completed — then one survivor is picked by weighted random,
/// the same pattern ImpTaskManager uses for job selection.
///
/// The interval between summons is base +/- random jitter, then scaled by
/// the faction's research multiplier, so different factions — and a single
/// faction over the course of a mission, as research completes — summon at
/// different rates without any of it being hardcoded.
/// </summary>
public class MinionSummoner : MonoBehaviour
{
    // ── Per-faction registry ───────────────────────────────────────────
    private static readonly Dictionary<FactionID, MinionSummoner> _registry = new();

    public static MinionSummoner GetForFaction(FactionID faction) =>
        _registry.TryGetValue(faction, out var s) ? s : null;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Roster")]
    [Tooltip("Every minion type this faction could ever summon, subject to the gates below.")]
    [SerializeField] private List<MinionDefinition> roster = new();

    [Header("Population")]
    [SerializeField] private int populationLimit = 10;

    [Header("Timing")]
    [Tooltip("Average seconds between summons.")]
    [SerializeField] private float baseSummonInterval = 30f;
    [Tooltip("Random offset applied to each interval, in seconds. X = min, Y = max.")]
    [SerializeField] private Vector2 summonIntervalJitter = new(-5f, 5f);
    [Tooltip("Seed for interval/pick randomness — reproducible, like ImpTaskManager's.")]
    [SerializeField] private int randomSeed = 54321;

    // ── Runtime ────────────────────────────────────────────────────────
    private System.Random _rng;
    private float _nextSummonTime;
    private int   _population;

    // Scratch buffers, reused to keep per-tick allocation down.
    private readonly List<MinionDefinition> _candidates = new();
    private readonly List<float>            _weights    = new();

    public FactionID Faction         => faction;
    public int       Population      => _population;
    public int       PopulationLimit => populationLimit;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _registry[faction] = this;
        _rng = new System.Random(randomSeed);
    }

    private void OnDestroy()
    {
        if (_registry.TryGetValue(faction, out var s) && s == this)
            _registry.Remove(faction);
    }

    private void Start() => _nextSummonTime = Time.time + NextInterval();

    private void Update()
    {
        if (Time.time < _nextSummonTime) return;
        _nextSummonTime = Time.time + NextInterval();
        TrySummon();
    }

    private float NextInterval()
    {
        float jitter = (float)(_rng.NextDouble() *
            (summonIntervalJitter.y - summonIntervalJitter.x) + summonIntervalJitter.x);

        float multiplier = GameManager2D.Instance?.GetResearch(faction)?.SummonIntervalMultiplier ?? 1f;
        return Mathf.Max(1f, (baseSummonInterval + jitter) * multiplier);
    }

    // ── Summoning ──────────────────────────────────────────────────────

    private void TrySummon()
    {
        var portal = Portal.GetForFaction(faction);
        if (portal == null || !portal.IsReady) return;

        var chosen = PickMinion();
        if (chosen == null) return;

        Spawn(chosen, portal);
    }

    private MinionDefinition PickMinion()
    {
        _candidates.Clear();
        foreach (var def in roster)
            if (def != null && IsEligible(def)) _candidates.Add(def);

        if (_candidates.Count == 0) return null;

        _weights.Clear();
        float total = 0f;
        foreach (var def in _candidates)
        {
            float w = Mathf.Max(0f, def.summonWeight);
            _weights.Add(w);
            total += w;
        }
        if (total <= 0f) return null;

        float roll = (float)_rng.NextDouble() * total;
        for (int i = 0; i < _candidates.Count; i++)
        {
            roll -= _weights[i];
            if (roll <= 0f) return _candidates[i];
        }
        return _candidates[^1];
    }

    private bool IsEligible(MinionDefinition def)
    {
        if (_population + def.populationCost > populationLimit) return false;
        if (!LevelAllows(def))            return false;
        if (!HasRequiredRooms(def))        return false;
        if (!HasRequiredResearch(def))     return false;
        return true;
    }

    private bool LevelAllows(MinionDefinition def)
    {
        var allowed = GameManager2D.Instance?.AllowedMinionIds;
        return allowed == null || allowed.Count == 0 || allowed.Contains(def.minionId);
    }

    private bool HasRequiredRooms(MinionDefinition def)
    {
        if (def.requiredRoomTypes.Count == 0) return true;

        var rooms = gridManager.GetRoomsForFaction(faction);
        foreach (var requiredType in def.requiredRoomTypes)
        {
            bool found = false;
            foreach (var room in rooms)
                if (room.TileType == requiredType) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private bool HasRequiredResearch(MinionDefinition def)
    {
        if (def.requiredResearchIds.Count == 0) return true;

        var research = GameManager2D.Instance?.GetResearch(faction);
        if (research == null) return false;

        foreach (var id in def.requiredResearchIds)
            if (!research.HasResearch(id)) return false;
        return true;
    }

    private void Spawn(MinionDefinition def, Portal portal)
    {
        if (def.prefab == null)
        {
            Debug.LogError($"[MinionSummoner] {def.minionId} has no prefab assigned.");
            return;
        }

        var go  = Instantiate(def.prefab, portal.SpawnPoint, Quaternion.identity);
        go.name = $"{def.minionId}_{faction}_{_population}";

        var creature = go.GetComponent<CreatureController>();
        if (creature == null)
        {
            Debug.LogError($"[MinionSummoner] {def.minionId} prefab is missing CreatureController.");
            Destroy(go);
            return;
        }

        creature.Initialise(faction, def);
        _population += def.populationCost;

        Debug.Log($"[MinionSummoner] Summoned {def.minionId} for {faction}. " +
                  $"Population {_population}/{populationLimit}.");
    }

    // ── Population bookkeeping ─────────────────────────────────────────

    /// <summary>Called by CreatureController.Die() to free its population slot.</summary>
    public void NotifyCreatureDied(MinionDefinition def)
    {
        int cost = def != null ? def.populationCost : 1;
        _population = Mathf.Max(0, _population - cost);
    }
}
