using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A* pathfinding over the logical grid. Replaces Unity NavMesh entirely.
///
/// Deterministic: identical inputs always produce an identical path, on every
/// machine. That property is what makes lockstep multiplayer viable later.
///
/// Movement rules come from TraversalRules — the same rules ConnectivityRegistry
/// bakes with — so a connectivity "yes" guarantees a path exists.
///
/// Two modes:
///   Normal  — routes only over CanPathOn tiles. Standard movement.
///   Escape  — routes over CanOccupy tiles (allowUnsafe: true). Used when a
///             minion has been pushed into a hazard and must wade out.
///
/// Costs: orthogonal = 10, diagonal = 14 (≈10√2). Integer costs keep the
/// arithmetic exact and the ordering stable.
/// </summary>
public class GridPathfinder
{
    private const int CostOrthogonal = 10;
    private const int CostDiagonal   = 14;

    private readonly GridManager2D _grid;

    public GridPathfinder(GridManager2D grid)
    {
        _grid = grid;
    }

    /// <summary>
    /// Clearance data for the current grid. Read fresh each call so a
    /// mid-path rebake is picked up immediately.
    /// </summary>
    private ClearanceMap Clearance => _grid.Clearance;

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds a path from start to goal. Returns null if no route exists.
    /// The returned list includes the goal but excludes the start cell.
    /// </summary>
    /// <param name="allowUnsafe">
    /// When true, routes over any occupiable tile including hazards. Use for
    /// escape routing only.
    /// </param>
    public List<GridCell> FindPath(GridCell start, GridCell goal,
                                   TraversalCapability capability,
                                   FactionID faction     = FactionID.Unaligned,
                                   bool      allowUnsafe = false,
                                   float     agentRadius = 0f)
    {
        if (start == null || goal == null) return null;
        if (start == goal) return new List<GridCell>();
        if (!IsEnterable(goal, capability, faction, allowUnsafe, agentRadius))
            return null;

        var open     = new List<Node>();
        var openMap  = new Dictionary<GridCell, Node>();
        var closed   = new HashSet<GridCell>();

        var startNode = new Node(start, 0, Heuristic(start, goal), null);
        open.Add(startNode);
        openMap[start] = startNode;

        while (open.Count > 0)
        {
            Node current = PopLowest(open);
            openMap.Remove(current.Cell);
            closed.Add(current.Cell);

            if (current.Cell == goal)
                return Reconstruct(current);

            foreach (var (dx, dy) in TraversalRules.AllDirections)
            {
                var neighbour = _grid.GetCell(current.Cell.X + dx, current.Cell.Y + dy);
                if (neighbour == null)             continue;
                if (closed.Contains(neighbour))    continue;
                if (!IsEnterable(neighbour, capability, faction, allowUnsafe, agentRadius))
                    continue;
                if (!DiagonalAllowed(current.Cell, dx, dy, capability, faction,
                                     allowUnsafe, agentRadius))
                    continue;

                int stepCost = (dx != 0 && dy != 0) ? CostDiagonal : CostOrthogonal;
                int newG     = current.G + stepCost;

                if (openMap.TryGetValue(neighbour, out Node existing))
                {
                    if (newG < existing.G)
                    {
                        existing.G      = newG;
                        existing.Parent = current;
                    }
                }
                else
                {
                    var node = new Node(neighbour, newG, Heuristic(neighbour, goal), current);
                    open.Add(node);
                    openMap[neighbour] = node;
                }
            }
        }

        return null; // no route
    }

    /// <summary>
    /// Breadth-first search outward from start for the nearest cell that is
    /// SAFE under the given capability (i.e. CanPathOn and not hazardous).
    ///
    /// Traverses occupiable tiles so a minion stranded in lava can wade out.
    /// Returns null only if no safe cell is reachable at all.
    /// </summary>
    public GridCell FindNearestSafeCell(GridCell start,
                                        TraversalCapability capability,
                                        FactionID faction     = FactionID.Unaligned,
                                        float     agentRadius = 0f)
    {
        if (start == null) return null;

        if (IsSafe(start, capability, faction, agentRadius)) return start;

        var visited = new HashSet<GridCell> { start };
        var queue   = new Queue<GridCell>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();

            foreach (var (dx, dy) in TraversalRules.AllDirections)
            {
                var next = _grid.GetCell(cell.X + dx, cell.Y + dy);
                if (next == null || visited.Contains(next)) continue;
                if (!TraversalRules.CanOccupy(next.TileType, capability, faction)) continue;
                if (!DiagonalAllowed(cell, dx, dy, capability, faction,
                                     allowUnsafe: true, agentRadius))
                    continue;

                if (IsSafe(next, capability, faction, agentRadius)) return next;

                visited.Add(next);
                queue.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>
    /// Breadth-first outward search for the nearest cell satisfying a predicate,
    /// measured by travel distance rather than straight-line distance.
    ///
    /// This is the difference between "the closest treasury as the crow flies"
    /// and "the treasury I can actually reach soonest". Picking by straight line
    /// sends an imp toward a tile that may be on the far side of a wall, and if
    /// no route exists at all the move simply fails.
    ///
    /// One search finds it, rather than running A* against every candidate.
    /// </summary>
    public GridCell FindNearestMatching(GridCell start,
                                        System.Func<GridCell, bool> predicate,
                                        TraversalCapability capability,
                                        FactionID faction     = FactionID.Unaligned,
                                        float     agentRadius = 0f)
    {
        if (start == null || predicate == null) return null;
        if (predicate(start)) return start;

        var visited = new HashSet<GridCell> { start };
        var queue   = new Queue<GridCell>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();

            foreach (var (dx, dy) in TraversalRules.AllDirections)
            {
                var next = _grid.GetCell(cell.X + dx, cell.Y + dy);
                if (next == null || visited.Contains(next)) continue;
                if (!IsEnterable(next, capability, faction, false, agentRadius)) continue;
                if (!DiagonalAllowed(cell, dx, dy, capability, faction, false, agentRadius))
                    continue;

                if (predicate(next)) return next;

                visited.Add(next);
                queue.Enqueue(next);
            }
        }

        return null;
    }

    /// <summary>True if the cell is routable and not damaging for this capability.</summary>
    public bool IsSafe(GridCell cell, TraversalCapability capability,
                       FactionID faction     = FactionID.Unaligned,
                       float     agentRadius = 0f)
    {
        if (cell == null) return false;
        if (!TraversalRules.CanPathOn(cell.TileType, capability, faction)) return false;
        if (TraversalRules.IsHazardousFor(cell.TileType, capability)) return false;

        if (agentRadius > 0f)
        {
            var clearance = Clearance;
            if (clearance != null && !clearance.Fits(cell, capability, agentRadius))
                return false;
        }
        return true;
    }

    // ── Internals ──────────────────────────────────────────────────────

    private bool IsEnterable(GridCell cell, TraversalCapability cap,
                             FactionID faction, bool allowUnsafe,
                             float agentRadius)
    {
        bool passable = allowUnsafe
            ? TraversalRules.CanOccupy(cell.TileType, cap, faction)
            : TraversalRules.CanPathOn(cell.TileType, cap, faction);

        if (!passable) return false;
        if (agentRadius <= 0f) return true;

        // The token must fit here without its edge reaching a wall.
        var clearance = Clearance;
        return clearance == null || clearance.Fits(cell, cap, agentRadius);
    }

    /// <summary>
    /// Applies the no-corner-cutting rule using the two orthogonal
    /// intermediates between the current cell and the diagonal target.
    /// </summary>
    private bool DiagonalAllowed(GridCell from, int dx, int dy,
                                 TraversalCapability cap, FactionID faction,
                                 bool allowUnsafe, float agentRadius)
    {
        if (dx == 0 || dy == 0) return true;

        var orthoA = _grid.GetCell(from.X + dx, from.Y);
        var orthoB = _grid.GetCell(from.X,      from.Y + dy);

        // Off-grid intermediates count as solid.
        if (orthoA == null || orthoB == null) return false;

        if (!TraversalRules.IsDiagonalLegal(
                dx, dy, orthoA.TileType, orthoB.TileType, cap, allowUnsafe, faction))
            return false;

        // The token sweeps across the shared corner, so both intermediates
        // must also have room for it — not merely be passable.
        if (agentRadius > 0f)
        {
            var clearance = Clearance;
            if (clearance != null &&
                (!clearance.Fits(orthoA, cap, agentRadius) ||
                 !clearance.Fits(orthoB, cap, agentRadius)))
                return false;
        }

        return true;
    }

    /// <summary>Octile distance — admissible for 8-way movement with these costs.</summary>
    private static int Heuristic(GridCell a, GridCell b)
    {
        int dx = Mathf.Abs(a.X - b.X);
        int dy = Mathf.Abs(a.Y - b.Y);
        int min = Mathf.Min(dx, dy);
        int max = Mathf.Max(dx, dy);
        return CostDiagonal * min + CostOrthogonal * (max - min);
    }

    private static Node PopLowest(List<Node> open)
    {
        int best = 0;
        for (int i = 1; i < open.Count; i++)
        {
            if (open[i].F < open[best].F ||
                (open[i].F == open[best].F && open[i].H < open[best].H))
                best = i;
        }
        Node node = open[best];
        open.RemoveAt(best);
        return node;
    }

    private static List<GridCell> Reconstruct(Node node)
    {
        var path = new List<GridCell>();
        while (node.Parent != null)
        {
            path.Add(node.Cell);
            node = node.Parent;
        }
        path.Reverse();
        return path;
    }

    private class Node
    {
        public readonly GridCell Cell;
        public int      G;
        public readonly int H;
        public Node     Parent;
        public int      F => G + H;

        public Node(GridCell cell, int g, int h, Node parent)
        {
            Cell = cell; G = g; H = h; Parent = parent;
        }
    }
}
