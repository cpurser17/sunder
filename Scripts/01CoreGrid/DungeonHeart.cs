using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every faction's Dungeon Heart — the win/lose condition. Destroying one
/// eliminates the faction that owns it.
///
/// One instance of this component exists in the scene, ever. Once the grid
/// exists it reads GameManager2D.ActiveFactions (itself sourced from the
/// level file) and creates one logical heart per active faction, discovering
/// each one's footprint (TileType.Heart, owned by that faction) from the
/// grid in a single scan. There is no per-faction prefab or scene object to
/// hand-place — a faction gets a heart as long as the level's grid data
/// paints one for it, and adding a faction to a level is enough on its own.
///
/// The footprint itself is ordinary walkable floor — see TraversalRules.
/// The crystal at its centre is a purely physical obstacle: a GridAgent in
/// static mode (see GridAgent.isStatic), spawned once each footprint's
/// geometric centre is known, so minions are softly pushed clear of it by
/// the same separation system that already keeps them off each other,
/// rather than the grid refusing to route through the tile.
///
/// Every newly summoned minion (see MinionSummoner / CreatureController)
/// must reach a cell inside its faction's footprint before it is considered
/// part of the faction — FindApproachCell is what CreatureController paths to.
/// </summary>
public class DungeonHeart : MonoBehaviour
{
    public static DungeonHeart Instance { get; private set; }

    /// <summary>Fired once, the moment a faction's heart HP reaches 0.</summary>
    public static event System.Action<FactionID> OnFactionEliminated;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Health (fallback)")]
    [Tooltip("Used only for a seat with no FactionDefinition assigned, or whose " +
             "FactionDefinition leaves maxHeartHitPoints at 0.")]
    [SerializeField] private int defaultMaxHitPoints = 1000;

    [Header("Crystal (fallback)")]
    [Tooltip("Used only for a seat with no FactionDefinition assigned, or whose " +
             "FactionDefinition leaves crystalPrefab empty. Must carry a GridAgent " +
             "with Static Obstacle ticked, so it is sized like a token and registered " +
             "for separation but never moves.")]
    [SerializeField] private GameObject crystalPrefab;

    // ── Runtime ────────────────────────────────────────────────────────

    private class HeartState
    {
        public readonly List<GridCell> Footprint = new();
        public GridCell CrystalCell;
        public int MaxHitPoints;
        public int CurrentHP;
        public bool Eliminated;
    }

    private readonly Dictionary<FactionID, HeartState> _hearts             = new();
    private readonly Dictionary<FactionID, int>        _pendingRestoreHP   = new();
    private GridPathfinder _pathfinder;
    private bool _ready;

    /// <summary>Fired whenever a heart takes damage. Args: faction, currentHP, maxHP.</summary>
    public event System.Action<FactionID, int, int> OnDamaged;

    /// <summary>Fired when a creature finishes reporting for duty at its faction's heart.</summary>
    public event System.Action<FactionID, CreatureController> OnCreatureReported;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        Instance     = this;
        _pathfinder  = new GridPathfinder(gridManager);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        GameManager2D.OnWalletsReady -= DiscoverAll;
    }

    private void Start()
    {
        if (gridManager.Width > 0) DiscoverAll();
        else GameManager2D.OnWalletsReady += DiscoverAll;
    }

    private void DiscoverAll()
    {
        GameManager2D.OnWalletsReady -= DiscoverAll;

        _hearts.Clear();
        foreach (var setup in GameManager2D.Instance.ActiveFactions)
            _hearts[setup.factionId] = new HeartState { MaxHitPoints = MaxHitPointsFor(setup.factionId) };

        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell.TileType == TileType.Heart && _hearts.TryGetValue(cell.Owner, out var state))
                state.Footprint.Add(cell);
        }

        foreach (var pair in _hearts)
        {
            FactionID faction = pair.Key;
            HeartState state  = pair.Value;

            state.CurrentHP = _pendingRestoreHP.TryGetValue(faction, out int hp)
                ? Mathf.Clamp(hp, 0, state.MaxHitPoints)
                : state.MaxHitPoints;
            state.Eliminated = state.CurrentHP <= 0;

            if (state.Footprint.Count == 0)
                Debug.LogWarning($"[DungeonHeart] No Heart tiles found for faction {faction}.");
            else
                SpawnCrystal(faction, state);
        }

        _pendingRestoreHP.Clear();
        _ready = true;
    }

    private int MaxHitPointsFor(FactionID faction)
    {
        var def = GameManager2D.Instance?.GetFactionDefinition(faction);
        return def != null && def.maxHeartHitPoints > 0 ? def.maxHeartHitPoints : defaultMaxHitPoints;
    }

    private GameObject CrystalPrefabFor(FactionID faction)
    {
        var def = GameManager2D.Instance?.GetFactionDefinition(faction);
        return def != null && def.crystalPrefab != null ? def.crystalPrefab : crystalPrefab;
    }

    /// <summary>
    /// Places the crystal at the footprint's geometric centre — the average
    /// of its cells' world positions, rather than a re-derived grid cell, so
    /// it sits correctly even if a level's Heart footprint isn't a perfect
    /// odd-sized square with a single centre cell.
    /// </summary>
    private void SpawnCrystal(FactionID faction, HeartState state)
    {
        var prefab = CrystalPrefabFor(faction);
        if (prefab == null)
        {
            Debug.LogWarning($"[DungeonHeart] No crystalPrefab for faction {faction}.");
            return;
        }

        Vector3 sum = Vector3.zero;
        foreach (var cell in state.Footprint)
            sum += gridManager.CellToWorld(cell.X, cell.Y);

        Vector3 centre = sum / state.Footprint.Count;
        Instantiate(prefab, centre, Quaternion.identity, transform);

        // Excluded from FindApproachCell below — a destination sitting under
        // the crystal's own collision radius could leave a creature
        // permanently oscillating just outside arriveTolerance, never
        // registering as arrived.
        if (gridManager.WorldToCell(centre, out int cx, out int cy))
            state.CrystalCell = gridManager.GetCell(cx, cy);

        var agent = prefab.GetComponent<GridAgent>();
        if (agent == null || !agent.IsStatic)
            Debug.LogWarning($"[DungeonHeart] {faction}'s crystalPrefab should carry a " +
                             "GridAgent with Static Obstacle ticked, or it will try to path/move.");
    }

    // ── Queries ────────────────────────────────────────────────────────

    public bool IsReady(FactionID faction) =>
        _ready && _hearts.TryGetValue(faction, out var s) && s.Footprint.Count > 0;

    public int CurrentHP(FactionID faction) =>
        _hearts.TryGetValue(faction, out var s) ? s.CurrentHP : 0;

    public int MaxHitPoints(FactionID faction) =>
        _hearts.TryGetValue(faction, out var s) ? s.MaxHitPoints : 0;

    // ── Damage / elimination ─────────────────────────────────────────────

    public void TakeDamage(FactionID faction, int amount)
    {
        if (!_hearts.TryGetValue(faction, out var state) || state.Eliminated || amount <= 0) return;

        state.CurrentHP = Mathf.Max(0, state.CurrentHP - amount);
        OnDamaged?.Invoke(faction, state.CurrentHP, state.MaxHitPoints);

        if (state.CurrentHP <= 0)
        {
            state.Eliminated = true;
            OnFactionEliminated?.Invoke(faction);
        }
    }

    // ── Save / load ──────────────────────────────────────────────────────

    public Dictionary<FactionID, int> SnapshotHP()
    {
        var result = new Dictionary<FactionID, int>();
        foreach (var pair in _hearts) result[pair.Key] = pair.Value.CurrentHP;
        return result;
    }

    /// <summary>
    /// Restores HP from a save. Safe to call before the grid scan has run —
    /// GameManager2D and DungeonHeart both key off OnWalletsReady, so their
    /// relative firing order isn't guaranteed; values are cached and applied
    /// to each faction's HeartState as soon as DiscoverAll creates it.
    /// </summary>
    public void RestoreAll(IReadOnlyDictionary<FactionID, int> savedHP)
    {
        if (savedHP == null) return;

        if (!_ready)
        {
            foreach (var pair in savedHP) _pendingRestoreHP[pair.Key] = pair.Value;
            return;
        }

        foreach (var pair in savedHP)
        {
            if (!_hearts.TryGetValue(pair.Key, out var state)) continue;
            state.CurrentHP  = Mathf.Clamp(pair.Value, 0, state.MaxHitPoints);
            state.Eliminated = state.CurrentHP <= 0;
        }
    }

    // ── Reporting for duty ───────────────────────────────────────────────

    /// <summary>
    /// Nearest walkable cell inside the given faction's heart footprint. The
    /// footprint is ordinary floor, so a minion can walk right in — the
    /// crystal at its centre deflects it via separation as it gets close,
    /// rather than the pathfinder needing to route around a "hole" in the
    /// tile grid. Null if that faction has no heart yet, or nothing
    /// reachable is inside it.
    /// </summary>
    public GridCell FindApproachCell(FactionID faction, GridCell from,
                                     TraversalCapability capability, float radius)
    {
        if (from == null || !_hearts.TryGetValue(faction, out var state) || state.Footprint.Count == 0)
            return null;

        return _pathfinder.FindNearestMatching(
            from, cell => IsHeartCell(faction, state, cell), capability, faction, radius);
    }

    private static bool IsHeartCell(FactionID faction, HeartState state, GridCell cell) =>
        cell.TileType == TileType.Heart && cell.Owner == faction && cell != state.CrystalCell;

    /// <summary>Called by CreatureController once it arrives and joins the faction.</summary>
    public void NotifyReported(FactionID faction, CreatureController creature) =>
        OnCreatureReported?.Invoke(faction, creature);
}
