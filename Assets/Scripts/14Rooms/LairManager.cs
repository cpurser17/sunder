using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Beds in Lairs. Each Lair tile holds one bed; a minion claims a free tile
/// the first time it needs to sleep, and from then on that bed is its own.
///
/// Rules
/// -----
///   One bed per tile, one per minion. Workers never sleep, so never have one.
///   A bed lasts until the minion dies, leaves or changes side, or its tile
///   stops being one of the minion's faction's Lair tiles (sold, captured).
///   Losing a bed that way adds a little anger, but the minion doesn't go
///   looking for a new one until it next wants to sleep (SleepBehaviour).
///   A minion that can't reach its own bed when it wants to sleep claims a
///   free one it can reach; the old one disappears.
///   No free bed anywhere it can reach: it sleeps on the floor, and the
///   player is told the lair is too small (Announcer, "LairTooSmall") — or
///   that they need one, if they have none ("NoLair").
///
/// Bed visuals: the minion type's bedPrefab (MinionDefinition) on its tile,
/// or a placeholder block until those assets exist.
///
/// Scene setup: none — GameManager2D adds one if the scene has none. Add it
/// yourself to tune anger or the placeholder bed. The "lair too small" text
/// and audio live in the Announcer's catalogue.
/// </summary>
public class LairManager : MonoBehaviour
{
    public static LairManager Instance { get; private set; }

    [Header("Rooms")]
    [SerializeField] private TileType lairType = TileType.Lair;

    [Header("Anger (placeholder)")]
    [Tooltip("Lasting anger (0-1) added when a minion's bed is sold or captured from under it.")]
    [SerializeField, Range(0f, 1f)] private float lostBedAnger = 0.05f;

    [Header("Placeholder bed")]
    [SerializeField] private Color placeholderColour = new(0.55f, 0.35f, 0.25f, 1f);
    [Tooltip("Placeholder size as a fraction of a cell (width, height, depth).")]
    [SerializeField] private Vector3 placeholderSize = new(0.6f, 0.08f, 0.4f);

    [Header("Dependencies")]
    [Tooltip("Leave empty to use GameManager2D's grid.")]
    [SerializeField] private GridManager2D gridManager;

    private readonly Dictionary<GridCell, MinionController> _claims  = new();
    private readonly Dictionary<MinionController, GridCell> _beds    = new();
    private readonly Dictionary<GridCell, GameObject>       _visuals = new();
    private bool _subscribed;

    public TileType LairType => lairType;

    private GridManager2D Grid
    {
        get
        {
            if (gridManager == null && GameManager2D.Instance != null) gridManager = GameManager2D.Instance.Grid;
            if (gridManager != null && !_subscribed)
            {
                gridManager.OnTileChanged += HandleTileChanged;
                _subscribed = true;
            }
            return gridManager;
        }
    }

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Start() => _ = Grid;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_subscribed && gridManager != null) gridManager.OnTileChanged -= HandleTileChanged;
    }

    // ── Queries ────────────────────────────────────────────────────────

    public bool IsLair(GridCell cell) => cell != null && cell.TileType == lairType;

    /// <summary>A Lair tile of this faction's with no bed on it.</summary>
    public bool IsFreeBedSpace(GridCell cell, FactionID faction) =>
        IsLair(cell) && cell.Owner == faction && !_claims.ContainsKey(cell);

    /// <summary>The minion's bed, or null if it has none.</summary>
    public GridCell BedOf(MinionController minion) =>
        minion != null && _beds.TryGetValue(minion, out var cell) ? cell : null;

    /// <summary>
    /// How restful the bed's Lair is: its room's efficiency (1 = this room
    /// type's ordinary rate), or 1 while the room is still being measured.
    /// </summary>
    public float RestEfficiency(GridCell bed)
    {
        var grid = Grid;
        var room = bed != null && bed.RoomId >= 0 && grid != null ? grid.GetRoomForCell(bed) : null;
        return room != null && room.TileType == bed.TileType ? room.Efficiency : 1f;
    }

    // ── Claiming ───────────────────────────────────────────────────────

    /// <summary>Gives the minion a bed on this tile, giving up any bed it had. False if the tile isn't free.</summary>
    public bool Claim(MinionController minion, GridCell cell)
    {
        if (minion == null || !IsFreeBedSpace(cell, minion.Faction)) return false;

        Release(minion);
        _claims[cell]  = minion;
        _beds[minion]  = cell;
        _visuals[cell] = CreateBed(minion, cell);
        return true;
    }

    /// <summary>Removes the minion's bed, if any (it died, left, changed side, or moved bed).</summary>
    public void Release(MinionController minion)
    {
        if (minion == null || !_beds.TryGetValue(minion, out var cell)) return;
        _beds.Remove(minion);
        RemoveBedAt(cell);
    }

    /// <summary>
    /// A minion found no bed: the faction's player hears they need a lair
    /// (none built) or that it's too small (none free it can reach).
    /// </summary>
    public void NotifyNoBed(FactionID faction) =>
        Announcer.Announce(faction, HasLair(faction) ? "LairTooSmall" : "NoLair");

    /// <summary>True if the faction owns any Lair tiles.</summary>
    public bool HasLair(FactionID faction)
    {
        var grid = Grid;
        if (grid == null) return false;
        foreach (var room in grid.Rooms)
            if (room.TileType == lairType && room.Owner == faction) return true;
        return false;
    }

    // ── Grid events ────────────────────────────────────────────────────

    /// <summary>A bed whose tile is no longer its owner's Lair is gone. The minion finds out when it next sleeps.</summary>
    private void HandleTileChanged(GridCell cell)
    {
        if (!_claims.TryGetValue(cell, out var minion)) return;
        if (minion != null && IsLair(cell) && cell.Owner == minion.Faction) return;

        RemoveBedAt(cell);
        if (minion != null)
        {
            _beds.Remove(minion);
            minion.OnBedLost(lostBedAnger);
        }
    }

    // ── Visuals ────────────────────────────────────────────────────────

    private void RemoveBedAt(GridCell cell)
    {
        _claims.Remove(cell);
        if (_visuals.TryGetValue(cell, out var visual) && visual != null) Destroy(visual);
        _visuals.Remove(cell);
    }

    private GameObject CreateBed(MinionController minion, GridCell cell)
    {
        var grid = Grid;
        if (grid == null) return null;

        Vector3 position = grid.CellToWorld(cell.X, cell.Y);
        var prefab = minion.Definition != null ? minion.Definition.bedPrefab : null;
        if (prefab != null)
        {
            var bed = Instantiate(prefab, position, Quaternion.identity, transform);
            bed.name = $"Bed_{minion.name}";
            return bed;
        }

        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Destroy(block.GetComponent<Collider>());
        block.name = $"Bed_{minion.name}";
        block.transform.SetParent(transform, false);
        Vector3 size = placeholderSize * grid.CellSize;
        block.transform.localScale = size;
        block.transform.position   = position + new Vector3(0f, size.y * 0.5f, 0f);
        var r = block.GetComponent<Renderer>();
        if (r != null) r.material.color = placeholderColour;
        return block;
    }
}
