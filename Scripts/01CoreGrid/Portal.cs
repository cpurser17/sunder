using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One faction's summoning portal — a single tile every minion except imps
/// (see MinionSummoner) spawns on. Purely a spawn point: it has no HP and
/// isn't part of the win/lose condition, unlike DungeonHeart.
///
/// Like DungeonHeart, its cell is authored directly in the level's grid data
/// (TileType.Portal, owned by this faction) and discovered here once the
/// grid exists, rather than being placed procedurally at runtime.
/// </summary>
public class Portal : MonoBehaviour
{
    // ── Per-faction registry ───────────────────────────────────────────
    private static readonly Dictionary<FactionID, Portal> _registry = new();

    public static Portal GetForFaction(FactionID faction) =>
        _registry.TryGetValue(faction, out var p) ? p : null;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridCell _cell;

    public FactionID Faction => faction;
    public GridCell  Cell    => _cell;
    public bool      IsReady => _cell != null;
    public Vector3   SpawnPoint => gridManager.CellToWorld(_cell.X, _cell.Y);

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => _registry[faction] = this;

    private void OnDestroy()
    {
        if (_registry.TryGetValue(faction, out var p) && p == this)
            _registry.Remove(faction);
        GameManager2D.OnWalletsReady -= DiscoverCell;
    }

    private void Start()
    {
        if (gridManager.Width > 0) DiscoverCell();
        else GameManager2D.OnWalletsReady += DiscoverCell;
    }

    private void DiscoverCell()
    {
        GameManager2D.OnWalletsReady -= DiscoverCell;

        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell.TileType == TileType.Portal && cell.Owner == faction)
            {
                _cell = cell;
                return;
            }
        }

        Debug.LogWarning($"[Portal] No Portal tile found for faction {faction}.");
    }
}
