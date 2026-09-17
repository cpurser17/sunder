using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One faction's Dungeon Heart — the win/lose condition. Destroying it
/// eliminates the faction that owns it.
///
/// The footprint (TileType.Heart, owned by this faction) is authored
/// directly in the level's grid data and discovered here once the grid
/// exists, same as Portal. Unlike Portal, it has no special per-cell mining
/// behaviour, and HP is tracked once for the whole structure rather than per
/// cell, since "a 3x3 block with 9 separate HP pools" isn't a meaningful
/// distinction for a single object.
///
/// The footprint itself is ordinary walkable floor — see TraversalRules.
/// The crystal at its centre is a purely physical obstacle: a GridAgent in
/// static mode (see GridAgent.isStatic), spawned here once the footprint's
/// geometric centre is known, so minions are softly pushed clear of it by
/// the same separation system that already keeps them off each other,
/// rather than the grid refusing to route through the tile.
///
/// Every newly summoned minion (see MinionSummoner / CreatureController)
/// must reach a cell inside this footprint before it is considered part of
/// the faction — FindApproachCell is what CreatureController paths to.
/// </summary>
public class DungeonHeart : MonoBehaviour
{
    // ── Per-faction registry ───────────────────────────────────────────
    private static readonly Dictionary<FactionID, DungeonHeart> _registry = new();

    public static DungeonHeart GetForFaction(FactionID faction) =>
        _registry.TryGetValue(faction, out var h) ? h : null;

    public static IReadOnlyDictionary<FactionID, DungeonHeart> All => _registry;

    /// <summary>Fired once, the moment a faction's heart HP reaches 0.</summary>
    public static event System.Action<FactionID> OnFactionEliminated;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Health")]
    [Tooltip("Total hit points for the whole heart structure. Reaching 0 eliminates the faction.")]
    [SerializeField] private int maxHitPoints = 1000;

    [Header("Crystal")]
    [Tooltip("Physical obstacle spawned at the footprint's centre once discovered. " +
             "Must carry a GridAgent with Static Obstacle ticked, so it is sized " +
             "like a token and registered for separation but never moves.")]
    [SerializeField] private GameObject crystalPrefab;

    // ── Runtime ────────────────────────────────────────────────────────
    private readonly List<GridCell> _footprint = new();
    private GridPathfinder _pathfinder;
    private GameObject _crystalInstance;
    private GridCell   _crystalCell;
    private int   _currentHP;
    private bool  _footprintReady;
    private bool  _eliminated;
    private int?  _pendingRestoreHP;

    public FactionID Faction        => faction;
    public int       MaxHitPoints   => maxHitPoints;
    public int       CurrentHP      => _currentHP;
    public bool      IsReady        => _footprintReady;
    public IReadOnlyList<GridCell> Footprint => _footprint;

    /// <summary>Fired whenever the heart takes damage. Args: currentHP, maxHP.</summary>
    public event System.Action<int, int> OnDamaged;

    /// <summary>Fired when a creature finishes reporting for duty here.</summary>
    public event System.Action<CreatureController> OnCreatureReported;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _registry[faction] = this;
        _pathfinder = new GridPathfinder(gridManager);
        _currentHP  = maxHitPoints;
    }

    private void OnDestroy()
    {
        if (_registry.TryGetValue(faction, out var h) && h == this)
            _registry.Remove(faction);
        GameManager2D.OnWalletsReady -= DiscoverFootprint;
    }

    private void Start()
    {
        if (gridManager.Width > 0) DiscoverFootprint();
        else GameManager2D.OnWalletsReady += DiscoverFootprint;
    }

    private void DiscoverFootprint()
    {
        GameManager2D.OnWalletsReady -= DiscoverFootprint;

        _footprint.Clear();
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell.TileType == TileType.Heart && cell.Owner == faction)
                _footprint.Add(cell);
        }

        if (_footprint.Count == 0)
            Debug.LogWarning($"[DungeonHeart] No Heart tiles found for faction {faction}.");
        else
            SpawnCrystal();

        _currentHP        = _pendingRestoreHP ?? maxHitPoints;
        _pendingRestoreHP = null;
        _eliminated        = _currentHP <= 0;
        _footprintReady    = true;
    }

    /// <summary>
    /// Places the crystal at the footprint's geometric centre — the average
    /// of its cells' world positions, rather than a re-derived grid cell, so
    /// it sits correctly even if a level's Heart footprint isn't a perfect
    /// odd-sized square with a single centre cell.
    /// </summary>
    private void SpawnCrystal()
    {
        if (crystalPrefab == null)
        {
            Debug.LogWarning($"[DungeonHeart] {faction} has no crystalPrefab assigned.");
            return;
        }

        Vector3 sum = Vector3.zero;
        foreach (var cell in _footprint)
            sum += gridManager.CellToWorld(cell.X, cell.Y);

        Vector3 centre = sum / _footprint.Count;
        _crystalInstance = Instantiate(crystalPrefab, centre, Quaternion.identity, transform);

        // Excluded from FindApproachCell below — a destination sitting under
        // the crystal's own collision radius could leave a creature
        // permanently oscillating just outside arriveTolerance, never
        // registering as arrived.
        if (gridManager.WorldToCell(centre, out int cx, out int cy))
            _crystalCell = gridManager.GetCell(cx, cy);

        var agent = _crystalInstance.GetComponent<GridAgent>();
        if (agent == null || !agent.IsStatic)
            Debug.LogWarning($"[DungeonHeart] {faction}'s crystalPrefab should carry a " +
                             "GridAgent with Static Obstacle ticked, or it will try to path/move.");
    }

    // ── Damage / elimination ────────────────────────────────────────────

    public void TakeDamage(int amount)
    {
        if (_eliminated || amount <= 0) return;

        _currentHP = Mathf.Max(0, _currentHP - amount);
        OnDamaged?.Invoke(_currentHP, maxHitPoints);

        if (_currentHP <= 0)
        {
            _eliminated = true;
            OnFactionEliminated?.Invoke(faction);
        }
    }

    // ── Save / load ──────────────────────────────────────────────────────

    /// <summary>
    /// Restores HP from a save. Safe to call before the footprint has been
    /// discovered — GameManager2D and DungeonHeart both key off
    /// OnWalletsReady, so their relative firing order isn't guaranteed; the
    /// value is cached and applied as soon as DiscoverFootprint runs.
    /// </summary>
    public void RestoreHP(int hp)
    {
        if (_footprintReady)
        {
            _currentHP  = Mathf.Clamp(hp, 0, maxHitPoints);
            _eliminated = _currentHP <= 0;
        }
        else
        {
            _pendingRestoreHP = hp;
        }
    }

    public static void RestoreAllFromSave(IReadOnlyDictionary<FactionID, int> savedHP)
    {
        if (savedHP == null) return;
        foreach (var pair in savedHP)
            if (_registry.TryGetValue(pair.Key, out var heart))
                heart.RestoreHP(pair.Value);
    }

    // ── Reporting for duty ───────────────────────────────────────────────

    /// <summary>
    /// Nearest walkable cell inside this heart's footprint. The footprint is
    /// ordinary floor, so a minion can walk right in — the crystal at its
    /// centre deflects it via separation as it gets close, rather than the
    /// pathfinder needing to route around a "hole" in the tile grid. Null if
    /// the footprint hasn't been discovered yet or nothing reachable is inside it.
    /// </summary>
    public GridCell FindApproachCell(GridCell from, TraversalCapability capability, float radius)
    {
        if (!_footprintReady || from == null) return null;
        return _pathfinder.FindNearestMatching(from, IsHeartCell, capability, faction, radius);
    }

    private bool IsHeartCell(GridCell cell) =>
        cell.TileType == TileType.Heart && cell.Owner == faction && cell != _crystalCell;

    /// <summary>Called by CreatureController once it arrives and joins the faction.</summary>
    public void NotifyReported(CreatureController creature) =>
        OnCreatureReported?.Invoke(creature);
}
