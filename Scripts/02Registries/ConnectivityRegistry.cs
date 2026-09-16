using System;
using System.Collections.Generic;

/// <summary>
/// Flood-fills the grid once per TraversalCapability, producing an area ID map
/// for each. Two cells sharing an area ID are mutually reachable by a minion of
/// that capability.
///
/// CRITICAL: uses TraversalRules — the same rules GridPathfinder uses, including
/// the 8-directional no-corner-cutting check. This guarantees that if
/// AreConnected returns true, FindPath will succeed. Never let these diverge.
///
/// Baked from CanPathOn, not CanOccupy: connectivity answers "where would this
/// minion willingly go", which is what job assignment needs. Escape routing does
/// its own search from the minion's position and needs no precomputed areas.
///
/// Size tiers: a token also needs ROOM, so connectivity is baked per
/// (capability x size tier). Without this, AreConnected would answer for the
/// smallest possible token and lie to anything larger — reintroducing exactly
/// the pathing/connectivity divergence we left NavMesh to avoid.
/// Tiers come from GridManager2D.agentRadiusTiers; one tier is the norm.
///
/// Doors: when implemented, passability becomes faction-dependent and this will
/// need baking per (capability, faction) pair too. TraversalRules already
/// accepts a faction argument for that reason.
/// </summary>
public class ConnectivityRegistry
{
    public event Action<ConnectivityRegistry> OnRebakeComplete;

    private readonly GridCell[,] _grid;
    private readonly int         _width;
    private readonly int         _height;

    // Keyed by (capability, tier index).
    private readonly Dictionary<(TraversalCapability, int), int[,]> _areaMaps   = new();
    private readonly Dictionary<(TraversalCapability, int), int>    _areaCounts = new();

    private readonly ClearanceMap _clearance;
    private float[] _radiusTiers = { 0f };

    /// <summary>Radii each tier was baked for, ascending.</summary>
    public float[] RadiusTiers => _radiusTiers;

    public ConnectivityRegistry(GridCell[,] grid, int width, int height,
                                ClearanceMap clearance)
    {
        _grid      = grid;
        _width     = width;
        _height    = height;
        _clearance = clearance;
    }

    /// <summary>
    /// Sets the token radii to bake for. Sorted ascending; a 0 tier is always
    /// present so radius-agnostic queries still work.
    /// </summary>
    public void SetRadiusTiers(float[] tiers)
    {
        var list = new List<float> { 0f };
        if (tiers != null)
            foreach (float t in tiers)
                if (t > 0f && !list.Contains(t)) list.Add(t);
        list.Sort();
        _radiusTiers = list.ToArray();
    }

    /// <summary>
    /// Index of the smallest tier that still accommodates this radius.
    /// A radius above every tier uses the largest — so add a tier when you
    /// introduce a minion bigger than any existing one.
    /// </summary>
    private int TierFor(float radius)
    {
        for (int i = 0; i < _radiusTiers.Length; i++)
            if (_radiusTiers[i] >= radius) return i;
        return _radiusTiers.Length - 1;
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// <summary>Area ID of a cell, or -1 if impassable for this capability.</summary>
    public int GetAreaId(GridCell cell, TraversalCapability capability,
                         float agentRadius = 0f)
    {
        if (cell == null) return -1;
        var key = (capability, TierFor(agentRadius));
        if (!_areaMaps.TryGetValue(key, out var map)) return -1;
        return map[cell.X, cell.Y];
    }

    /// <summary>
    /// True if a minion of this capability can travel from a to b.
    /// O(1) — this is the cheap pre-check before committing to a full A*.
    /// </summary>
    public bool AreConnected(GridCell a, GridCell b, TraversalCapability capability,
                             float agentRadius = 0f)
    {
        int idA = GetAreaId(a, capability, agentRadius);
        int idB = GetAreaId(b, capability, agentRadius);
        return idA >= 0 && idA == idB;
    }

    /// <summary>Number of disconnected regions for a capability.</summary>
    public int AreaCount(TraversalCapability capability, float agentRadius = 0f) =>
        _areaCounts.TryGetValue((capability, TierFor(agentRadius)), out int n) ? n : 0;

    /// <summary>All cells sharing the given area ID.</summary>
    public List<GridCell> GetCellsInArea(int areaId, TraversalCapability capability,
                                         float agentRadius = 0f)
    {
        var result = new List<GridCell>();
        var key = (capability, TierFor(agentRadius));
        if (areaId < 0 || !_areaMaps.TryGetValue(key, out var map)) return result;

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
            if (map[x, y] == areaId) result.Add(_grid[x, y]);

        return result;
    }

    // ── Rebake ─────────────────────────────────────────────────────────

    public void Rebake()
    {
        foreach (TraversalCapability cap in Enum.GetValues(typeof(TraversalCapability)))
        for (int tier = 0; tier < _radiusTiers.Length; tier++)
            RebakeCapability(cap, tier);

        OnRebakeComplete?.Invoke(this);
    }

    private void RebakeCapability(TraversalCapability capability, int tier)
    {
        float radius = _radiusTiers[tier];
        var map     = new int[_width, _height];
        var visited = new bool[_width, _height];

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
            map[x, y] = -1;

        int nextId = 0;

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
        {
            if (visited[x, y]) continue;
            if (!Walkable(_grid[x, y], capability, radius)) continue;

            FloodFill(x, y, capability, radius, nextId, map, visited);
            nextId++;
        }

        _areaMaps[(capability, tier)]   = map;
        _areaCounts[(capability, tier)] = nextId;
    }

    private void FloodFill(int startX, int startY, TraversalCapability capability,
                           float radius, int areaId, int[,] map, bool[,] visited)
    {
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        visited[startX, startY] = true;
        map[startX, startY]     = areaId;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();

            foreach (var (dx, dy) in TraversalRules.AllDirections)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;
                if (visited[nx, ny]) continue;
                if (!Walkable(_grid[nx, ny], capability, radius)) continue;
                if (!DiagonalAllowed(cx, cy, dx, dy, capability, radius)) continue;

                visited[nx, ny] = true;
                map[nx, ny]     = areaId;
                queue.Enqueue((nx, ny));
            }
        }
    }

    /// <summary>
    /// Mirrors GridPathfinder.DiagonalAllowed. Both must apply the identical
    /// corner rule or connectivity will disagree with pathing.
    /// </summary>
    /// <summary>
    /// Passable AND roomy enough. Mirrors GridPathfinder.IsEnterable.
    /// </summary>
    private bool Walkable(GridCell cell, TraversalCapability capability, float radius)
    {
        if (!TraversalRules.CanPathOn(cell.TileType, capability)) return false;
        if (radius <= 0f) return true;
        return _clearance == null || _clearance.Fits(cell, capability, radius);
    }

    /// <summary>
    /// Mirrors GridPathfinder.DiagonalAllowed, clearance check included.
    /// These two must stay identical or connectivity will disagree with pathing.
    /// </summary>
    private bool DiagonalAllowed(int x, int y, int dx, int dy,
                                 TraversalCapability capability, float radius)
    {
        if (dx == 0 || dy == 0) return true;

        int ax = x + dx, ay = y;
        int bx = x,      by = y + dy;

        if (ax < 0 || ax >= _width || ay < 0 || ay >= _height) return false;
        if (bx < 0 || bx >= _width || by < 0 || by >= _height) return false;

        if (!TraversalRules.IsDiagonalLegal(
                dx, dy,
                _grid[ax, ay].TileType,
                _grid[bx, by].TileType,
                capability,
                useOccupyRules: false))
            return false;

        if (radius > 0f && _clearance != null)
        {
            if (!_clearance.Fits(_grid[ax, ay], capability, radius) ||
                !_clearance.Fits(_grid[bx, by], capability, radius))
                return false;
        }

        return true;
    }
}
