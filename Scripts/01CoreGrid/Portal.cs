using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every faction's summoning portal — the single tile every minion except
/// imps (see MinionSummoner) spawns on. Purely a spawn point: it has no HP
/// and isn't part of the win/lose condition, unlike DungeonHeart.
///
/// One instance of this component exists in the scene, ever. Once the grid
/// exists it scans for every TileType.Portal cell and indexes it by owning
/// faction — there is no per-faction prefab or scene object to hand-place,
/// a faction gets a portal as long as the level's grid data paints one for it.
/// </summary>
public class Portal : MonoBehaviour
{
    public static Portal Instance { get; private set; }

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    // ── Runtime ────────────────────────────────────────────────────────
    private readonly Dictionary<FactionID, GridCell> _cells = new();
    private bool _ready;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => Instance = this;

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

        _cells.Clear();
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell.TileType == TileType.Portal) _cells[cell.Owner] = cell;
        }

        foreach (var setup in GameManager2D.Instance.ActiveFactions)
            if (!_cells.ContainsKey(setup.factionId))
                Debug.LogWarning($"[Portal] No Portal tile found for faction {setup.factionId}.");

        _ready = true;
    }

    // ── Queries ────────────────────────────────────────────────────────

    public bool IsReady(FactionID faction) => _ready && _cells.ContainsKey(faction);

    public Vector3 SpawnPoint(FactionID faction) =>
        gridManager.CellToWorld(_cells[faction].X, _cells[faction].Y);
}
