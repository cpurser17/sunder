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
/// own defaultX fields, purely as a missing-data safety net — with a warning,
/// since it usually means the level's contentFactionId or the GameManager2D's
/// Faction Registry isn't set up.
///
/// On each faction's tick, eligibility is narrowed by four independent gates
/// — population headroom, mission design (GameManager2D.AllowedMinionIds),
/// rooms built, and research completed — then one survivor is picked by
/// weighted random, the same pattern WorkerTaskManager uses for job selection.
/// When nothing qualifies, the reason is logged (see ReportNothingEligible)
/// — once per change of reason, not every tick.
///
/// The interval between summons is base +/- random jitter, then scaled by
/// the faction's research multiplier, so different factions — and a single
/// faction over the course of a mission, as research completes — summon at
/// different rates without any of it being hardcoded.
///
/// Every minion spawns from the one minionTemplate prefab (unless its
/// definition sets its own prefab override); MinionController.Initialise
/// then applies the definition's token, movement, stats and behaviour to it.
/// WorkerSpawner spawns workers from the same template (see Template).
/// </summary>
public class MinionSummoner : MonoBehaviour
{
    public static MinionSummoner Instance { get; private set; }

    /// <summary>The shared minion prefab. WorkerSpawner uses it for workers too.</summary>
    public GameObject Template => minionTemplate;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Template")]
    [Tooltip("Shared prefab every minion spawns from — portal creatures and " +
             "WorkerSpawner's workers alike: GridAgent, MinionController, the " +
             "behaviours and a SpriteRenderer child for the token. Build it with " +
             "Sunder > Create Minion Template Prefab. A MinionDefinition's own " +
             "prefab, if set, overrides this.")]
    [SerializeField] private GameObject minionTemplate;

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
        /// <summary>Why the last tick found nobody eligible; null after a successful pick.</summary>
        public string LastBlockReason;
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
            if (def == null)
                Debug.LogWarning($"[MinionSummoner] Seat {setup.factionId} has no FactionDefinition " +
                                 "(check the level's contentFactionId and GameManager2D's Faction " +
                                 "Registry) — summoning from this component's Default Roster instead.");
            else if (def.roster.Count == 0)
                Debug.LogWarning($"[MinionSummoner] {def.factionContentId}'s roster is empty (run " +
                                 "Sunder > Import Minion Data) — summoning from the Default Roster instead.");
            var state = new FactionState
            {
                Rng                = new System.Random(GameManager2D.Instance.DeriveFactionSeed(setup.factionId, "MinionSummoner")),
                Roster              = def != null && def.roster.Count > 0 ? def.roster : defaultRoster,
                PopulationLimit     = def != null && def.populationLimit > 0 ? def.populationLimit : defaultPopulationLimit,
                BaseSummonInterval  = def != null && def.baseSummonInterval > 0f ? def.baseSummonInterval : defaultBaseSummonInterval,
                Jitter              = def != null ? def.summonIntervalJitter : defaultSummonIntervalJitter,
            };
            state.NextSummonTime = Time.time + NextInterval(state, setup.factionId);
            _states[setup.factionId] = state;
        }
    }

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

        if (_candidates.Count == 0)
        {
            ReportNothingEligible(faction, state);
            return null;
        }

        _weights.Clear();
        float total = 0f;
        foreach (var def in _candidates)
        {
            float w = Mathf.Max(0f, def.summonWeight);
            _weights.Add(w);
            total += w;
        }
        if (total <= 0f)
        {
            ReportBlocked(faction, state, $"all {_candidates.Count} eligible minion(s) have SummonWeight 0", true);
            return null;
        }
        state.LastBlockReason = null;

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
        if (!def.summonable)                         return false;
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

    private bool HasRequiredRooms(FactionID faction, MinionDefinition def) =>
        FirstMissingRoom(faction, def) == null;

    private bool HasRequiredResearch(FactionID faction, MinionDefinition def) =>
        FirstMissingResearch(faction, def) == null;

    /// <summary>The first required room the faction lacks, or null if it has them all.</summary>
    private MinionDefinition.RoomRequirement? FirstMissingRoom(FactionID faction, MinionDefinition def)
    {
        if (def.requiredRooms.Count == 0) return null;

        var rooms = gridManager.GetRoomsForFaction(faction);
        foreach (var required in def.requiredRooms)
        {
            bool found = false;
            foreach (var room in rooms)
                if (room.TileType == required.roomType && room.Cells.Count >= required.minTiles)
                { found = true; break; }
            if (!found) return required;
        }
        return null;
    }

    /// <summary>The first required research the faction hasn't completed, or null.</summary>
    private string FirstMissingResearch(FactionID faction, MinionDefinition def)
    {
        if (def.requiredResearchIds.Count == 0) return null;

        var research = GameManager2D.Instance?.GetResearch(faction);
        foreach (var id in def.requiredResearchIds)
            if (research == null || !research.HasResearch(id)) return id;
        return null;
    }

    // ── Diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Explains a tick where no roster minion qualified, by the first gate
    /// each one failed, e.g. "AI1 couldn't summon: 0 of 19 eligible — 15
    /// need rooms (Library, Lair of 9+ tiles), 4 need research (Labyrinths)".
    /// A full population on its own is normal and isn't logged.
    /// </summary>
    private void ReportNothingEligible(FactionID faction, FactionState state)
    {
        int summonable = 0, population = 0, level = 0, rooms = 0, research = 0;
        var missingRooms    = new SortedSet<string>();
        var missingResearch = new SortedSet<string>();

        foreach (var def in state.Roster)
        {
            if (def == null || !def.summonable) continue;
            summonable++;

            if (state.Population + def.populationCost > state.PopulationLimit) { population++; continue; }
            if (!LevelAllows(def)) { level++; continue; }

            var room = FirstMissingRoom(faction, def);
            if (room.HasValue)
            {
                rooms++;
                missingRooms.Add(room.Value.minTiles > 0
                    ? $"{room.Value.roomType} of {room.Value.minTiles}+ tiles"
                    : room.Value.roomType.ToString());
                continue;
            }

            string id = FirstMissingResearch(faction, def);
            if (id != null) { research++; missingResearch.Add(id); }
        }

        if (summonable == 0)
        {
            ReportBlocked(faction, state, state.Roster.Count == 0
                ? "the roster is empty"
                : $"none of the {state.Roster.Count} roster minion(s) are summonable", true);
            return;
        }

        var parts = new List<string>();
        if (population > 0) parts.Add($"{population} won't fit the population limit");
        if (level      > 0) parts.Add($"{level} not allowed by this level's allowedMinionIds");
        if (rooms      > 0) parts.Add($"{rooms} need rooms ({string.Join(", ", missingRooms)})");
        if (research   > 0) parts.Add($"{research} need research ({string.Join(", ", missingResearch)})");

        bool onlyPopulation = population == summonable;
        ReportBlocked(faction, state, $"0 of {summonable} eligible — {string.Join(", ", parts)}", !onlyPopulation);
    }

    /// <summary>Logs a reason only when it differs from the last one for this faction.</summary>
    private static void ReportBlocked(FactionID faction, FactionState state, string reason, bool log)
    {
        if (reason == state.LastBlockReason) return;
        state.LastBlockReason = reason;
        if (log) Debug.LogWarning($"[MinionSummoner] {faction} couldn't summon: {reason}.");
    }

    private void Spawn(FactionID faction, FactionState state, MinionDefinition def, Portal portal)
    {
        var prefab = def.prefab != null ? def.prefab : minionTemplate;
        if (prefab == null)
        {
            Debug.LogError($"[MinionSummoner] No minion template assigned, and {def.minionId} has no prefab override.");
            return;
        }

        var go  = Instantiate(prefab, portal.SpawnPoint(faction), Quaternion.identity);
        go.name = $"{def.minionId}_{faction}_{state.Population}";

        var minion = go.GetComponent<MinionController>();
        if (minion == null)
        {
            Debug.LogError($"[MinionSummoner] {prefab.name} is missing MinionController — " +
                           "rebuild it with Sunder > Create Minion Template Prefab.");
            Destroy(go);
            return;
        }

        minion.Initialise(faction, def, 1, MinionController.SpawnSource.Portal);
        state.Population += def.populationCost;

        Debug.Log($"[MinionSummoner] Summoned {def.minionId} for {faction}. " +
                  $"Population {state.Population}/{state.PopulationLimit}.");
    }

    // ── Population bookkeeping ─────────────────────────────────────────

    /// <summary>
    /// Frees a population slot for the given faction — called whenever a
    /// creature leaves its population for any reason: death or abandoning
    /// (MinionController.Die) or converting to another faction (MinionController.ConvertTo).
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
