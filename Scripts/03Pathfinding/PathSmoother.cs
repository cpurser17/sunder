using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Turns the cell sequence A* produced into a continuous any-angle path.
///
/// This is line-of-sight string pulling, NOT the funnel algorithm. Funnel is
/// constrained to the corridor of cells it was given, and on a grid a diagonal
/// step shares only a corner point rather than an edge — so funnel ends up
/// pinned to those corner points and only partially straightens a staircase.
/// LOS string pulling ignores the corridor entirely and asks a purely geometric
/// question, so an open room collapses to a single straight segment at whatever
/// angle the geometry wants.
///
/// The algorithm is greedy: from the current anchor, reach as far along the path
/// as line of sight allows, emit that point, repeat. In cluttered geometry this
/// can settle a few percent longer than the true optimum, which is invisible in
/// practice and irrelevant in corridors where walls dictate the route anyway.
///
/// Radius handling: the sight test is a THICK line. A segment is only clear if
/// no blocked tile lies within the token's radius of it, so a smoothed path
/// rounds corners at exactly the radius distance and never scrapes a wall.
/// </summary>
public class PathSmoother
{
    private readonly GridManager2D _grid;

    public PathSmoother(GridManager2D grid)
    {
        _grid = grid;
    }

    // ── Smoothing ──────────────────────────────────────────────────────

    /// <summary>
    /// Collapses a cell path into world-space waypoints.
    ///
    /// startWorld is the agent's actual position rather than its cell centre,
    /// so the first segment is correct even when the agent is standing
    /// off-centre inside its tile.
    ///
    /// The returned list excludes the start point and ends at the final cell
    /// centre (or endWorld if supplied).
    /// </summary>
    public List<Vector3> Smooth(Vector3 startWorld, List<GridCell> cellPath,
                                TraversalCapability capability, float radius,
                                FactionID faction  = FactionID.Unaligned,
                                Vector3?  endWorld = null)
    {
        var result = new List<Vector3>();
        if (cellPath == null || cellPath.Count == 0) return result;

        // Raw points: every cell centre, with an optional precise final target.
        var raw = new List<Vector3>(cellPath.Count + 1);
        for (int i = 0; i < cellPath.Count; i++)
            raw.Add(CentreOf(cellPath[i]));

        if (endWorld.HasValue)
        {
            // Replace the last centre with the exact requested point when the
            // two are in the same cell — otherwise append it.
            if (raw.Count > 0 && SameCell(raw[^1], endWorld.Value))
                raw[^1] = endWorld.Value;
            else
                raw.Add(endWorld.Value);
        }

        Vector3 anchor = startWorld;
        int     i2     = 0;

        while (i2 < raw.Count)
        {
            // Reach as far ahead as we can still see from the anchor.
            int furthest = i2;
            for (int j = raw.Count - 1; j >= i2; j--)
            {
                if (HasLineOfSight(anchor, raw[j], capability, radius, faction))
                {
                    furthest = j;
                    break;
                }
            }

            result.Add(raw[furthest]);
            anchor = raw[furthest];

            // Guarantee progress even if a sight test fails unexpectedly.
            i2 = furthest + 1;
        }

        return result;
    }

    // ── Thick-line sight test ──────────────────────────────────────────

    /// <summary>
    /// True if a token of the given radius can travel in a straight line from
    /// a to b without any part of it entering a blocked tile.
    ///
    /// Candidates are gathered by walking the segment and dilating outward by
    /// the radius, then each blocked candidate gets an exact segment-to-square
    /// distance test. Exactness matters here: an approximation that measured to
    /// tile centres would misjudge corners, which is the one place this has to
    /// be right.
    /// </summary>
    public bool HasLineOfSight(Vector3 a, Vector3 b,
                               TraversalCapability capability, float radius,
                               FactionID faction = FactionID.Unaligned)
    {
        float cell = _grid.CellSize;

        // Ring of cells around the segment that could possibly be within radius.
        // Minimum of 1 so half-cell sampling below cannot skip a tile.
        int ring = Mathf.Max(1, Mathf.CeilToInt(radius / cell));

        var checkedCells = new HashSet<(int, int)>();

        // Walk the segment at half-cell steps — with ring >= 1 this cannot miss
        // a tile the segment passes through.
        float length = Vector3.Distance(a, b);
        int   steps  = Mathf.Max(1, Mathf.CeilToInt(length / (cell * 0.5f)));

        for (int s = 0; s <= steps; s++)
        {
            Vector3 p = Vector3.Lerp(a, b, s / (float)steps);
            if (!_grid.WorldToCell(p, out int cx, out int cy)) return false;

            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            {
                int nx = cx + dx;
                int ny = cy + dy;

                if (!checkedCells.Add((nx, ny))) continue;

                var c = _grid.GetCell(nx, ny);

                // Off-grid is solid.
                bool blocked = c == null ||
                               !TraversalRules.CanPathOn(c.TileType, capability, faction);
                if (!blocked) continue;

                if (SegmentSquareDistance(a, b, nx, ny) < radius) return false;
            }
        }

        return true;
    }

    // ── Geometry ───────────────────────────────────────────────────────

    /// <summary>
    /// Exact distance on the XZ plane from segment ab to the square occupied by
    /// cell (cx, cy). Zero if the segment crosses the square.
    ///
    /// For a convex shape the minimum is attained either at a shape corner
    /// (measured to the segment) or at a segment endpoint (measured to the
    /// shape), so checking both cases covers it.
    /// </summary>
    private float SegmentSquareDistance(Vector3 a, Vector3 b, int cx, int cy)
    {
        Vector3 centre = _grid.CellToWorld(cx, cy);
        float   half   = _grid.CellSize * 0.5f;

        Vector2 min = new(centre.x - half, centre.z - half);
        Vector2 max = new(centre.x + half, centre.z + half);

        Vector2 p0 = new(a.x, a.z);
        Vector2 p1 = new(b.x, b.z);

        if (SegmentIntersectsBox(p0, p1, min, max)) return 0f;

        float best = float.MaxValue;

        // Box corners measured against the segment.
        best = Mathf.Min(best, PointSegmentDistance(new Vector2(min.x, min.y), p0, p1));
        best = Mathf.Min(best, PointSegmentDistance(new Vector2(max.x, min.y), p0, p1));
        best = Mathf.Min(best, PointSegmentDistance(new Vector2(min.x, max.y), p0, p1));
        best = Mathf.Min(best, PointSegmentDistance(new Vector2(max.x, max.y), p0, p1));

        // Segment endpoints measured against the box.
        best = Mathf.Min(best, PointBoxDistance(p0, min, max));
        best = Mathf.Min(best, PointBoxDistance(p1, min, max));

        return best;
    }

    private static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab  = b - a;
        float   len = ab.sqrMagnitude;
        if (len < 1e-8f) return Vector2.Distance(p, a);

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len);
        return Vector2.Distance(p, a + ab * t);
    }

    private static float PointBoxDistance(Vector2 p, Vector2 min, Vector2 max)
    {
        float dx = Mathf.Max(0f, Mathf.Max(min.x - p.x, p.x - max.x));
        float dy = Mathf.Max(0f, Mathf.Max(min.y - p.y, p.y - max.y));
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Slab test — does segment ab cross the axis-aligned box?</summary>
    private static bool SegmentIntersectsBox(Vector2 a, Vector2 b,
                                             Vector2 min, Vector2 max)
    {
        Vector2 d    = b - a;
        float   tMin = 0f;
        float   tMax = 1f;

        for (int axis = 0; axis < 2; axis++)
        {
            float origin = axis == 0 ? a.x   : a.y;
            float dir    = axis == 0 ? d.x   : d.y;
            float lo     = axis == 0 ? min.x : min.y;
            float hi     = axis == 0 ? max.x : max.y;

            if (Mathf.Abs(dir) < 1e-8f)
            {
                // Parallel to this slab — must already lie inside it.
                if (origin < lo || origin > hi) return false;
                continue;
            }

            float t1 = (lo - origin) / dir;
            float t2 = (hi - origin) / dir;
            if (t1 > t2) (t1, t2) = (t2, t1);

            tMin = Mathf.Max(tMin, t1);
            tMax = Mathf.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        return true;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private Vector3 CentreOf(GridCell cell) => _grid.CellToWorld(cell.X, cell.Y);

    private bool SameCell(Vector3 a, Vector3 b) =>
        _grid.WorldToCell(a, out int ax, out int ay) &&
        _grid.WorldToCell(b, out int bx, out int by) &&
        ax == bx && ay == by;
}
