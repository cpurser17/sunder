using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every faction's summoning portals — the tiles every minion except workers
/// (see MinionSummoner) spawns on. Workers are placed by WorkerSpawner
/// instead. Purely spawn points: no HP, and not part of the win/lose
/// condition, unlike DungeonHeart.
///
/// One instance of this component exists in the scene, ever. Once the grid
/// exists it scans for every TileType.Portal cell and indexes them by owning
/// faction — there is no per-faction prefab or scene object to hand-place.
///
/// Portals change hands: an unowned (Unaligned) portal, or an enemy's,
/// touching a faction's territory is claimed by its workers like any other
/// tile (WorkerTaskManager). The index follows every ownership change, so a
/// faction that captures a portal starts summoning through it at once, and
/// one that loses its last portal stops. With several, minions arrive at
/// the first found.
/// </summary>
public class Portal : MonoBehaviour
{
    public static Portal Instance { get; private set; }

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    // ── Runtime ────────────────────────────────────────────────────────
    private readonly Dictionary<FactionID, List<GridCell>> _cells = new();
    private bool _ready;
    private bool _subscribed;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => Instance = this;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        GameManager2D.OnWalletsReady -= DiscoverAll;
        if (_subscribed && gridManager != null) gridManager.OnTileChanged -= OnTileChanged;
    }

    private void Start()
    {
        if (gridManager.Width > 0) DiscoverAll();
        else GameManager2D.OnWalletsReady += DiscoverAll;
    }

    private void DiscoverAll()
    {
        GameManager2D.OnWalletsReady -= DiscoverAll;
        if (!_subscribed) { gridManager.OnTileChanged += OnTileChanged; _subscribed = true; }

        Reindex();

        foreach (var setup in GameManager2D.Instance.ActiveFactions)
            if (!_cells.ContainsKey(setup.factionId))
                Debug.LogWarning($"[Portal] No Portal tile found for faction {setup.factionId}.");

        _ready = true;
    }

    private void Reindex()
    {
        _cells.Clear();
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell == null || cell.TileType != TileType.Portal) continue;
            if (!_cells.TryGetValue(cell.Owner, out var list)) _cells[cell.Owner] = list = new List<GridCell>();
            list.Add(cell);
        }
    }

    /// <summary>A portal claimed, captured or otherwise changed: re-index who owns which.</summary>
    private void OnTileChanged(GridCell cell)
    {
        if (cell.TileType == TileType.Portal || WasPortal(cell)) Reindex();
    }

    private bool WasPortal(GridCell cell)
    {
        foreach (var list in _cells.Values)
            if (list.Contains(cell)) return true;
        return false;
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// <summary>True if the faction owns at least one portal.</summary>
    public bool IsReady(FactionID faction) =>
        _ready && _cells.TryGetValue(faction, out var list) && list.Count > 0;

    /// <summary>The faction's (first) portal tile, or null if it has none.</summary>
    public GridCell CellOf(FactionID faction) =>
        _ready && _cells.TryGetValue(faction, out var list) && list.Count > 0 ? list[0] : null;

    public Vector3 SpawnPoint(FactionID faction)
    {
        var cell = CellOf(faction);
        return cell != null ? gridManager.CellToWorld(cell.X, cell.Y) : Vector3.zero;
    }
}
