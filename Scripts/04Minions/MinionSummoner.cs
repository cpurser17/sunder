using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Timer-driven minion summoning for every faction.
///
/// One instance of this component exists in the scene, ever. Once the grid
/// exists it reads GameManager2D.ActiveFactions and, for each active seat,
/// resolves that seat's FactionDefinition (via GameManager2D.GetFactionDefinition
/// — assigned per level through FactionSetup.contentFactionId) to get its
/// roster, population limit, and summon pacing. There is no per-faction
/// prefab or scene object to hand-place, and no shared roster either — every
/// faction's content is bespoke, authored on its own FactionDefinition asset.
/// A seat with no FactionDefinition assigned falls back to this component's
/// own defaultX fields, purely as a missing-data safety net.
///
/// On each faction's tick, eligibility is narrowed by four independent gates
/// — population headroom, mission design (GameManager2D.AllowedMinionIds),
/// rooms built, and research completed — then one survivor is picked by
/// weighted random, the same pattern ImpTaskManager uses for job selection.
///
/// The interval between summons is base +/- random jitter, then scaled by
/// the faction's research multiplier, so different factions — and a single
/// faction over the course of a mission, as research completes — summon at
/// different rates without any of it being hardcoded.
/// </summary>
public class MinionSummoner : MonoBehaviour
{
    public static MinionSummoner Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Roster (fallback)")]
    [Tooltip("Used only for a seat with no FactionDefinition assigned.")]
    [SerializeField] private List<MinionDefinition> defaultRoster = new();

    [Header("Population (fallback)")]
    [SerializeField] private int defaultPopulationLimit = 10;

    [Header("Timing (fallback)")]
    [Tooltip("Average seconds between summons.")]
    [SerializeField] private float defaultBaseSummonInterval = 30f;
    [Tooltip("Random offset applied to each interval, in seconds. X = min, Y = max.")]
    [SerializeField] private Vector2 defaultSummonIntervalJitter = new(-5f, 5f);

    // ── Runtime ────────────────────────────────────────────────────────

    private class FactionState
    {
        public System.Random Rng;
        public List<MinionDefinition> Roster;
        public int   PopulationLimit;
        public float BaseSummonInterval;
        public Vector2 Jitter;
        public float NextSummonTime;
        public int   Population;
    }

    private readonly Dictionary<FactionID, FactionState> _states = new();

    // Scratch buffers, reused to keep per-tick allocation down.
    private readonly List<MinionDefinition> _candidates = new();
    private readonly List<float>            _weights    = new();

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => Instance = this;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        GameManager2D.OnWalletsReady -= Setup;
    }

    private void Start()
    {
        if (gridManager.Width > 0) Setup();
        else GameManager2D.OnWalletsReady += Setup;
    }

    private void Setup()
    {
        GameManager2D.OnWalletsReady -= Setup;

        _states.Clear();
        foreach (var setup in GameManager2D.Instance.ActiveFactions)
        {
            var def   = GameManager2D.Instance.GetFactionDefinition(setup.factionId);
            var state = new FactionState
            {
                Rng                = new System.Random(StableSeed(setup.factionId)),
                Roster              = def != null && def.roster.Count > 0 ? def.roster : defaultRoster,
                PopulationLimit     = def != null && def.populationLimit > 0 ? def.populationLimit : defaultPopulationLimit,
                BaseSummonInterval  = def != null && def.baseSummonInterval > 0f ? def.baseSummonInterval : defaultBaseSummonInterval,
                Jitter              = def != null ? def.summonIntervalJitter : defaultSummonIntervalJitter,
            };
            state.NextSummonTime = Time.time + NextInterval(state, setup.factionId);
            _states[setup.factionId] = state;
        }
    }

    /// <summary>
    /// Deterministic per-faction seed, so runs stay reproducible without
    /// needing an override entry just to get a different random stream than
    /// another faction.
    /// </summary>
    private static int StableSeed(FactionID faction) => 12345 + (int)faction * 977;

    private void Update()
    {
        foreach (var pair in _states)
        {
            FactionID    faction = pair.Key;
            FactionState state   = pair.Value;

            if (Time.time < state.NextSummonTime) continue;
            state.NextSummonTime = Time.time + NextInterval(state, faction);
            TrySummon(faction, state);
        }
    }

    private float NextInterval(FactionState state, FactionID faction)
    {
        float jitter = (float)(state.Rng.NextDouble() *
            (state.Jitter.y - state.Jitter.x) + state.Jitter.x);

        float multiplier = GameManager2D.Instance?.GetResearch(faction)?.SummonIntervalMultiplier ?? 1f;
        return Mathf.Max(1f, (state.BaseSummonInterval + jitter) * multiplier);
    }

    // ── Summoning ──────────────────────────────────────────────────────

    private void TrySummon(FactionID faction, FactionState state)
    {
        var portal = Portal.Instance;
        if (portal == null || !portal.IsReady(faction)) return;

        var chosen = PickMinion(faction, state);
        if (chosen == null) return;

        Spawn(faction, state, chosen, portal);
    }

    private MinionDefinition PickMinion(FactionID faction, FactionState state)
    {
        _candidates.Clear();
        foreach (var def in state.Roster)
            if (def != null && IsEligible(faction, state, def)) _candidates.Add(def);

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

        float roll = (float)state.Rng.NextDouble() * total;
        for (int i = 0; i < _candidates.Count; i++)
        {
            roll -= _weights[i];
            if (roll <= 0f) return _candidates[i];
        }
        return _candidates[^1];
    }

    private bool IsEligible(FactionID faction, FactionState state, MinionDefinition def)
    {
        if (state.Population + def.populationCost > state.PopulationLimit) return false;
        if (!LevelAllows(def))                      return false;
        if (!HasRequiredRooms(faction, def))         return false;
        if (!HasRequiredResearch(faction, def))      return false;
        return true;
    }

    private bool LevelAllows(MinionDefinition def)
    {
        var allowed = GameManager2D.Instance?.AllowedMinionIds;
        return allowed == null || allowed.Count == 0 || allowed.Contains(def.minionId);
    }

    private bool HasRequiredRooms(FactionID faction, MinionDefinition def)
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

    private bool HasRequiredResearch(FactionID faction, MinionDefinition def)
    {
        if (def.requiredResearchIds.Count == 0) return true;

        var research = GameManager2D.Instance?.GetResearch(faction);
        if (research == null) return false;

        foreach (var id in def.requiredResearchIds)
            if (!research.HasResearch(id)) return false;
        return true;
    }

    private void Spawn(FactionID faction, FactionState state, MinionDefinition def, Portal portal)
    {
        if (def.prefab == null)
        {
            Debug.LogError($"[MinionSummoner] {def.minionId} has no prefab assigned.");
            return;
        }

        var go  = Instantiate(def.prefab, portal.SpawnPoint(faction), Quaternion.identity);
        go.name = $"{def.minionId}_{faction}_{state.Population}";

        var creature = go.GetComponent<CreatureController>();
        if (creature == null)
        {
            Debug.LogError($"[MinionSummoner] {def.minionId} prefab is missing CreatureController.");
            Destroy(go);
            return;
        }

        creature.Initialise(faction, def);
        state.Population += def.populationCost;

        Debug.Log($"[MinionSummoner] Summoned {def.minionId} for {faction}. " +
                  $"Population {state.Population}/{state.PopulationLimit}.");
    }

    // ── Population bookkeeping ─────────────────────────────────────────

    /// <summary>
    /// Frees a population slot for the given faction — called whenever a
    /// creature leaves its population for any reason: death (CreatureController.Die)
    /// or converting to another faction (CreatureController.ConvertTo).
    /// </summary>
    public void NotifyCreatureRemoved(FactionID faction, MinionDefinition def)
    {
        if (!_states.TryGetValue(faction, out var state)) return;
        int cost = def != null ? def.populationCost : 1;
        state.Population = Mathf.Max(0, state.Population - cost);
    }

    /// <summary>
    /// Claims a population slot for the given faction without going through
    /// Spawn — used when a creature joins a faction any way other than being
    /// freshly summoned, e.g. converting from another faction.
    /// </summary>
    public void NotifyCreatureJoined(FactionID faction, MinionDefinition def)
    {
        if (!_states.TryGetValue(faction, out var state)) return;
        state.Population += def != null ? def.populationCost : 1;
    }

    public int Population(FactionID faction) =>
        _states.TryGetValue(faction, out var s) ? s.Population : 0;

    public int PopulationLimit(FactionID faction) =>
        _states.TryGetValue(faction, out var s) ? s.PopulationLimit : 0;
}
