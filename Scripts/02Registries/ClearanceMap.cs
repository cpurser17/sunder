using UnityEngine;

/// <summary>
/// For every cell, the distance from that cell's centre to the nearest
/// impassable tile — in world units. A token of radius r can occupy a cell
/// only where clearance >= r, which is what keeps its edge off walls.
///
/// One map per TraversalCapability, because "impassable" differs by capability:
/// water blocks a LandOnly token but is open ground to a LandWater one.
///
/// Distances are EXACT, not approximated. The distance from a cell centre to a
/// blocked cell is measured to the nearest point of that cell's square, so a
/// diagonally adjacent wall correctly reports ~0.707 cells rather than the
/// 1.414 a naive centre-to-centre search would give. Getting this wrong lets
/// tokens clip corners.
///
/// Search is bounded by maxSearchRadius: beyond that a cell simply reports the
/// cap, since no token is that large. Keeps the transform O(W*H*k) with small k.
/// </summary>
public class ClearanceMap
{
    private readonly GridCell[,] _grid;
    private readonly int         _width;
    private readonly int         _height;
    private readonly float       _cellSize;

    private readonly System.Collections.Generic.Dictionary<TraversalCapability, float[,]>
        _maps = new();

    /// <summary>
    /// Largest clearance the map will report, in world units. Cells further
    /// from any wall than this are capped. Raise only if you add minions with
    /// a radius approaching it.
    /// </summary>
    public float MaxSearchRadius { get; }

    public ClearanceMap(GridCell[,] grid, int width, int height,
                        float cellSize, float maxSearchRadius = 3f)
    {
        _grid           = grid;
        _width          = width;
        _height         = height;
        _cellSize       = cellSize;
        MaxSearchRadius = maxSearchRadius;
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// <summary>
    /// Clearance at a cell in world units, or 0 if the cell is itself blocked.
    /// </summary>
    public float GetClearance(GridCell cell, TraversalCapability capability)
    {
        if (cell == null) return 0f;
        if (!_maps.TryGetValue(capability, out var map)) return 0f;
        return map[cell.X, cell.Y];
    }

    /// <summary>
    /// True if a token of the given radius fits at this cell without its edge
    /// reaching a wall.
    /// </summary>
    public bool Fits(GridCell cell, TraversalCapability capability, float radius) =>
        GetClearance(cell, capability) >= radius;

    // ── Rebake ─────────────────────────────────────────────────────────

    public void Rebake()
    {
        foreach (TraversalCapability cap in
                 System.Enum.GetValues(typeof(TraversalCapability)))
            RebakeCapability(cap);
    }

    private void RebakeCapability(TraversalCapability capability)
    {
        var map = new float[_width, _height];

        // How many cells out we must look to reach MaxSearchRadius.
        int ring = Mathf.CeilToInt(MaxSearchRadius / _cellSize) + 1;

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
        {
            // A blocked cell has no clearance at all.
            if (!TraversalRules.CanOccupy(_grid[x, y].TileType, capability))
            {
                map[x, y] = 0f;
                continue;
            }

            map[x, y] = NearestBlockedDistance(x, y, capability, ring);
        }

        _maps[capability] = map;
    }

    /// <summary>
    /// Exact distance from cell (x,y)'s centre to the nearest blocked tile,
    /// searching outward ring by ring and stopping as soon as no closer result
    /// is possible.
    /// </summary>
    private float NearestBlockedDistance(int x, int y,
                                         TraversalCapability capability,
                                         int maxRing)
    {
        float best = MaxSearchRadius;

        for (int r = 1; r <= maxRing; r++)
        {
            // The closest any cell in this ring could possibly be. Once that
            // exceeds our best result, no further ring can improve it.
            float ringFloor = (r - 0.5f) * _cellSize;
            if (ringFloor >= best) break;

            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                // Perimeter of this ring only — inner cells were already checked.
                if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;

                int nx = x + dx;
                int ny = y + dy;

                bool blocked;
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height)
                    blocked = true;   // off-grid counts as solid
                else
                    blocked = !TraversalRules.CanOccupy(
                        _grid[nx, ny].TileType, capability);

                if (!blocked) continue;

                float d = BoxDistance(dx, dy);
                if (d < best) best = d;
            }
        }

        return Mathf.Min(best, MaxSearchRadius);
    }

    /// <summary>
    /// Distance from a cell centre to the nearest point of the square occupied
    /// by the cell (dx, dy) away. Orthogonal neighbour = half a cell; diagonal
    /// neighbour = half a cell on both axes, i.e. ~0.707 cells.
    /// </summary>
    private float BoxDistance(int dx, int dy)
    {
        float half = _cellSize * 0.5f;
        float ax = Mathf.Max(0f, Mathf.Abs(dx) * _cellSize - half);
        float ay = Mathf.Max(0f, Mathf.Abs(dy) * _cellSize - half);
        return Mathf.Sqrt(ax * ax + ay * ay);
    }
}
