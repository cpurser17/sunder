using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walls only stand while their owner holds the floor beside them. When a
/// tile changes, every wall it touches (and the tile itself, if it's a wall)
/// is rechecked: a wall with no orthogonal neighbour that is walkable floor
/// owned by the wall's owner reverts to unowned Stone, ready to be dug or
/// reinforced by whoever holds the ground next to it now.
///
/// Support has to come from floor, not other walls — otherwise a ring of
/// walls around a lost room would keep each other standing forever. This
/// mirrors where a wall can be built: reinforcing needs owned floor next to
/// the Stone (see WorkerTaskManager.IsReinforceable).
///
/// Applies to Neutral (Unaligned) walls too, which the level editor can
/// paint: they stand while Neutral floor is next to them, and drop to Stone
/// once that floor is claimed.
///
/// Reacts to changes only — a level that loads with a free-standing wall
/// keeps it until something next to it changes. Checks are queued and run
/// in LateUpdate rather than inside OnTileChanged, so a wall never changes
/// in the middle of another system's tile write (a worker completing a
/// claim, a room capture flipping many tiles).
///
/// Gameplay only: GameManager2D adds it. The level editor has none, so
/// walls painted there are left exactly as painted.
/// </summary>
public class WallDecay : MonoBehaviour
{
    [SerializeField] private GridManager2D gridManager;

    private readonly HashSet<GridCell> _pending = new();
    private readonly List<GridCell>    _batch   = new();

    private void Awake()
    {
        if (gridManager == null)
            gridManager = GameManager2D.Instance != null
                ? GameManager2D.Instance.Grid
                : FindAnyObjectByType<GridManager2D>();
    }

    private void OnEnable()
    {
        if (gridManager == null) return;
        gridManager.OnTileChanged += OnTileChanged;
        gridManager.OnGridBuilt   += OnGridBuilt;
    }

    private void OnDisable()
    {
        if (gridManager == null) return;
        gridManager.OnTileChanged -= OnTileChanged;
        gridManager.OnGridBuilt   -= OnGridBuilt;
    }

    // A (re)built grid announces every cell as changed; that's a load, not
    // ground changing hands, and its cells are about to be replaced anyway.
    private void OnGridBuilt() => _pending.Clear();

    private void OnTileChanged(GridCell cell)
    {
        if (cell.TileType == TileType.Wall) _pending.Add(cell);
        foreach (var (dx, dy) in TraversalRules.Orthogonal)
        {
            var n = gridManager.GetCell(cell.X + dx, cell.Y + dy);
            if (n != null && n.TileType == TileType.Wall) _pending.Add(n);
        }
    }

    private void LateUpdate()
    {
        if (_pending.Count == 0) return;

        // Copy first: reverting a wall fires OnTileChanged, which queues the
        // walls beside it for next frame. Walls never support walls, so those
        // just pass the same check again and stand — no cascade.
        _batch.Clear();
        _batch.AddRange(_pending);
        _pending.Clear();

        foreach (var wall in _batch)
        {
            if (wall.TileType != TileType.Wall) continue;               // changed since it was queued
            if (gridManager.GetCell(wall.X, wall.Y) != wall) continue;  // grid rebuilt since
            if (IsSupported(wall)) continue;

            gridManager.SetTileType(wall, TileType.Stone, FactionID.Unaligned);
        }
    }

    /// <summary>True if an orthogonal neighbour is walkable floor owned by the wall's owner.</summary>
    private bool IsSupported(GridCell wall) =>
        gridManager.HasAdjacentMatch(wall.X, wall.Y,
            n => n.Owner == wall.Owner &&
                 gridManager.GetCategory(n.TileType) == TileCategory.Owned &&
                 TraversalRules.CanPathOn(n.TileType, TraversalCapability.LandOnly, wall.Owner));
}
