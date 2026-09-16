using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One faction's Dungeon Heart — the win/lose condition. Destroying it
/// eliminates the faction that owns it.
///
/// The heart has no special per-cell mining behaviour; its footprint is a
/// static, indestructible-to-imps Heart tile block authored directly in the
/// level's grid data (TileType.Heart, owned by this faction), discovered here
/// once the grid exists. HP is tracked once for the whole structure rather
/// than per cell, since "a 3x3 block with 9 separate HP pools" isn't a
/// meaningful distinction for a single object.
///
/// Every newly summoned minion (see MinionSummoner / CreatureController)
/// must reach a cell bordering this footprint before it is considered part
/// of the faction — FindApproachCell is what CreatureController paths to.
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

    // ── Runtime ────────────────────────────────────────────────────────
    private readonly List<GridCell> _footprint = new();
    private GridPathfinder _pathfinder;
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

        _currentHP        = _pendingRestoreHP ?? maxHitPoints;
        _pendingRestoreHP = null;
        _eliminated        = _currentHP <= 0;
        _footprintReady    = true;
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
    /// Nearest cell orthogonally touching this heart's footprint that a
    /// minion of the given capability/radius can actually stand on. Null if
    /// the footprint hasn't been discovered yet or nothing reachable borders it.
    /// </summary>
    public GridCell FindApproachCell(GridCell from, TraversalCapability capability, float radius)
    {
        if (!_footprintReady || from == null) return null;
        return _pathfinder.FindNearestMatching(from, IsApproachCell, capability, faction, radius);
    }

    private bool IsApproachCell(GridCell cell) =>
        gridManager.HasAdjacentMatch(cell.X, cell.Y,
            n => n.TileType == TileType.Heart && n.Owner == faction);

    /// <summary>Called by CreatureController once it arrives and joins the faction.</summary>
    public void NotifyReported(CreatureController creature) =>
        OnCreatureReported?.Invoke(creature);
}
