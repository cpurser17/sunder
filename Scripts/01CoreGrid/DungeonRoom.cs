using System.Collections.Generic;

/// <summary>
/// Represents a single contiguous room — a group of adjacent cells sharing
/// the same TileType AND the same FactionID owner.
///
/// Only RoomA/B/C and Bridge tiles are registered. Tunnel, Wall,
/// environmental, and liquid tiles are excluded from the registry.
/// </summary>
public class DungeonRoom
{
    public int            RoomId   { get; }
    public TileType       TileType { get; }
    public FactionID      Owner    { get; }
    public List<GridCell> Cells    { get; } = new();

    public int SellValue(TileRegistry registry) =>
        Cells.Count * registry.GetSellValue(TileType);

    public DungeonRoom(int id, TileType type, FactionID owner)
    {
        RoomId   = id;
        TileType = type;
        Owner    = owner;
    }

    public void AddCell(GridCell cell)
    {
        Cells.Add(cell);
        cell.RoomId = RoomId;
    }
}
