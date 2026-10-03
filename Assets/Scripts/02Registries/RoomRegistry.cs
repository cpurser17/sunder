using System;
using System.Collections.Generic;

/// <summary>
/// Flood-fill room detection for functional owned rooms only.
///
/// A tile type is registered if its TileDefinition has isRoom set (every
/// room in the RoomData workbook, plus Bridge). Tunnel, Wall, Heart, Portal,
/// environmental and liquid tiles are skipped.
///
/// Two adjacent cells form the same room only if they share both
/// TileType AND Owner (FactionID). This means a Player Treasury and an AI1
/// Treasury are tracked as separate rooms even if adjacent.
///
/// Each room's efficiency (see RoomEfficiency) is measured here, so it is
/// always current: any tile change — building a wall included — rebakes.
///
/// Call Rebake() after grid changes (GridManager2D debounces this).
/// </summary>
public class RoomRegistry
{
    public IReadOnlyList<DungeonRoom> Rooms => _rooms;
    public event Action<IReadOnlyList<DungeonRoom>> OnRebakeComplete;

    /// <summary>Connectivity analysis — updated after every room rebake.</summary>
    public ConnectivityRegistry Connectivity { get; private set; }

    /// <summary>Faction territory analysis — updated after every room rebake.</summary>
    public TerritoryRegistry Territory { get; private set; }

    /// <summary>Per-capability wall-distance map, used for token radius fitting.</summary>
    public ClearanceMap Clearance { get; private set; }

    private readonly List<DungeonRoom> _rooms = new();
    private readonly GridCell[,]       _grid;
    private readonly int               _width;
    private readonly int               _height;

    private static readonly (int dx, int dy)[] Neighbours =
        { (0,1),(0,-1),(1,0),(-1,0) };

    private readonly TileRegistry _tileRegistry;

    public RoomRegistry(GridCell[,] grid, int width, int height,
                        TileRegistry tileRegistry, float cellSize = 1f)
    {
        _grid         = grid;
        _width        = width;
        _height       = height;
        _tileRegistry = tileRegistry;
        Clearance     = new ClearanceMap(grid, width, height, cellSize);
        Connectivity  = new ConnectivityRegistry(grid, width, height, Clearance);
        Territory     = new TerritoryRegistry(grid, width, height);
    }

    // ── Rebake ─────────────────────────────────────────────────────────

    public void Rebake()
    {
        _rooms.Clear();

        bool[,] visited = new bool[_width, _height];
        int nextId = 0;

        // Clear every cell first. (Clearing inside the loop below would wipe
        // the ids flood-fill had already given cells further along.)
        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
            _grid[x, y].RoomId = -1;

        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
        {
            if (visited[x, y]) continue;
            var def = _tileRegistry.GetDefinition(_grid[x, y].TileType);
            if (def == null || !def.isRoom) continue;

            var cell = _grid[x, y];
            var room = new DungeonRoom(nextId++, cell.TileType, cell.Owner, def);
            FloodFill(x, y, cell.TileType, cell.Owner, visited, room);
            room.SetMeasurement(Measure(room));
            _rooms.Add(room);
        }

        Clearance.Rebake();          // must precede Connectivity — it reads this
        Connectivity.Rebake();
        Territory.Rebake(_tileRegistry);
        OnRebakeComplete?.Invoke(_rooms);
    }

    // ── Flood-fill ─────────────────────────────────────────────────────

    private void FloodFill(int startX, int startY,
                           TileType targetType, FactionID targetOwner,
                           bool[,] visited, DungeonRoom room)
    {
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        visited[startX, startY] = true;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            room.AddCell(_grid[cx, cy]);

            foreach (var (dx, dy) in Neighbours)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;
                if (visited[nx, ny]) continue;

                var neighbour = _grid[nx, ny];
                // Must match both tile type AND owner to be the same room.
                if (neighbour.TileType != targetType) continue;
                if (neighbour.Owner    != targetOwner) continue;

                visited[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }
    }

    private RoomEfficiency.Measurement Measure(DungeonRoom room)
    {
        var tiles = new List<(int x, int y)>(room.Cells.Count);
        foreach (var c in room.Cells) tiles.Add((c.X, c.Y));

        return RoomEfficiency.Measure(tiles,
            (x, y) => InBounds(x, y) && _grid[x, y].RoomId == room.RoomId,
            (x, y) => InBounds(x, y) && _grid[x, y].TileType == TileType.Wall
                                     && _grid[x, y].Owner    == room.Owner);
    }

    private bool InBounds(int x, int y) => x >= 0 && x < _width && y >= 0 && y < _height;

    // ── Query helpers ──────────────────────────────────────────────────

    public DungeonRoom GetRoomForCell(GridCell cell)
    {
        if (cell.RoomId < 0) return null;
        foreach (var room in _rooms)
            if (room.RoomId == cell.RoomId) return room;
        return null;
    }

    /// <summary>All rooms belonging to a specific faction.</summary>
    public List<DungeonRoom> GetRoomsForFaction(FactionID faction)
    {
        var result = new List<DungeonRoom>();
        foreach (var room in _rooms)
            if (room.Owner == faction) result.Add(room);
        return result;
    }

    /// <summary>All rooms of a specific type across all factions.</summary>
    public List<DungeonRoom> GetRoomsOfType(TileType type)
    {
        var result = new List<DungeonRoom>();
        foreach (var room in _rooms)
            if (room.TileType == type) result.Add(room);
        return result;
    }

    /// <summary>
    /// BFS from a starting cell to find the nearest passable non-liquid cell.
    /// Used as a fallback when a unit is stranded on a liquid tile.
    /// </summary>
    public GridCell FindNearestPassable(int startX, int startY,
                                        TileRegistry registry)
    {
        var visited = new bool[_width, _height];
        var queue   = new Queue<(int x, int y)>();
        queue.Enqueue((startX, startY));
        visited[startX, startY] = true;

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            var cell = _grid[cx, cy];
            var traversal = registry.GetTraversal(cell.TileType);

            if (traversal == TraversalType.Normal)
                return cell;

            foreach (var (dx, dy) in Neighbours)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (nx < 0 || nx >= _width || ny < 0 || ny >= _height) continue;
                if (visited[nx, ny]) continue;
                visited[nx, ny] = true;
                queue.Enqueue((nx, ny));
            }
        }

        return null; // no passable cell found (shouldn't happen on a valid map)
    }
}
