using System;
using System.Collections.Generic;

/// <summary>
/// Flood-fills contiguous blocks of owned tiles, grouped by faction only —
/// tile type is ignored, so a Tunnel adjacent to a RoomA adjacent to a Bridge
/// is one territory as long as the owner matches.
///
/// This is distinct from the other two registries:
///   RoomRegistry         groups by TileType + Owner — "which rooms exist"
///   TerritoryRegistry    groups by Owner            — "what land do I hold"
///   ConnectivityRegistry groups by passability      — "who can reach where"
///
/// Territory is not connectivity: an enemy Tunnel is walkable by your minions
/// (same connected area) but is not your territory.
///
/// Uses ORTHOGONAL adjacency. Territory is a contiguity question, not a movement
/// one, and two owned blocks touching only at a corner cannot be walked between
/// under the no-corner-cutting rule — so they are correctly separate holdings.
///
/// Answers questions like "did selling that bridge split my dungeon in two",
/// which is the trigger for supply, morale or upkeep mechanics later.
/// </summary>
public class TerritoryRegistry
{
    public event Action<TerritoryRegistry> OnRebakeComplete;

    private readonly GridCell[,] _grid;
    private readonly int         _width;
    private readonly int         _height;

    private int[,] _territoryIds;
    private readonly List<Territory> _territories = new();

    public IReadOnlyList<Territory> Territories => _territories;

    public TerritoryRegistry(GridCell[,] grid, int width, int height)
    {
        _grid   = grid;
        _width  = width;
        _height = height;
    }

    // ── Queries ────────────────────────────────────────────────────────

    /// <summary>Territory ID of a cell, or -1 if unowned.</summary>
    public int GetTerritoryId(GridCell cell)
    {
        if (cell == null || _territoryIds == null) return -1;
        return _territoryIds[cell.X, cell.Y];
    }

    public Territory GetTerritory(GridCell cell)
    {
        int id = GetTerritoryId(cell);
        foreach (var t in _territories)
            if (t.TerritoryId == id) return t;
        return null;
    }

    /// <summary>True if both cells belong to the same contiguous holding.</summary>
    public bool SameTerritory(GridCell a, GridCell b)
    {
        int idA = GetTerritoryId(a);
        return idA >= 0 && idA == GetTerritoryId(b);
    }

    public List<Territory> GetTerritoriesForFaction(FactionID faction)
    {
        var result = new List<Territory>();
        foreach (var t in _territories)
            if (t.Owner == faction) result.Add(t);
        return result;
    }

    /// <summary>
    /// Total owned tiles held by a faction across all its territories.
    /// Useful for scoring and upkeep.
    /// </summary>
    public int TotalTilesHeld(FactionID faction)
    {
        int n = 0;
        foreach (var t in _territories)
            if (t.Owner == faction) n += t.Cells.Count;
        return n;
    }

    // ── Rebake ─────────────────────────────────────────────────────────

    public void Rebake(TileRegistry tileRegistry)
    {
        _territories.Clear();
        _territoryIds = new int[_width, _height];

        var visited = new bool[_width, _height];

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
            _territoryIds[x, y] = -1;

        int nextId = 0;

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
        {
            if (visited[x, y]) continue;
            if (!IsOwned(_grid[x, y], tileRegistry)) continue;

            var territory = new Territory(nextId, _grid[x, y].Owner);
            FloodFill(x, y, _grid[x, y].Owner, territory, visited, tileRegistry);
            _territories.Add(territory);
            nextId++;
        }

        OnRebakeComplete?.Invoke(this);
    }

    private void FloodFill(int startX, int startY, FactionID owner,
                           Territory territory, bool[,] visited,
                           TileRegistry tileRegistry)
    {
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        visited[startX, startY] = true;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();

            territory.AddCell(_grid[cx, cy]);
            _territoryIds[cx, cy] = territory.TerritoryId;

            foreach (var (dx, dy) in TraversalRules.Orthogonal)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;
                if (visited[nx, ny]) continue;

                var next = _grid[nx, ny];
                if (!IsOwned(next, tileRegistry)) continue;
                if (next.Owner != owner)          continue;

                visited[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }
    }

    private static bool IsOwned(GridCell cell, TileRegistry tileRegistry) =>
        cell.Owner != FactionID.Unaligned &&
        tileRegistry.GetCategory(cell.TileType) == TileCategory.Owned;
}

/// <summary>One contiguous block of tiles held by a single faction.</summary>
public class Territory
{
    public int            TerritoryId { get; }
    public FactionID      Owner       { get; }
    public List<GridCell> Cells       { get; } = new();

    public Territory(int id, FactionID owner)
    {
        TerritoryId = id;
        Owner       = owner;
    }

    public void AddCell(GridCell cell) => Cells.Add(cell);

    /// <summary>Count of cells of a given type within this territory.</summary>
    public int CountOfType(TileType type)
    {
        int n = 0;
        foreach (var c in Cells) if (c.TileType == type) n++;
        return n;
    }
}
