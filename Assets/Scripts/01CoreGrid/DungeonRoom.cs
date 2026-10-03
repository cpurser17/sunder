using System.Collections.Generic;

/// <summary>
/// Represents a single contiguous room — a group of adjacent cells sharing
/// the same TileType AND the same FactionID owner.
///
/// Only tile types whose TileDefinition has isRoom set are registered (see
/// RoomRegistry). Efficiency and Capacity are recomputed on every rebake.
/// </summary>
public class DungeonRoom
{
    public int            RoomId   { get; }
    public TileType       TileType { get; }
    public FactionID      Owner    { get; }
    public List<GridCell> Cells    { get; } = new();
    public TileDefinition Definition { get; }

    /// <summary>Shape and wall measurements behind Efficiency (see RoomEfficiency).</summary>
    public RoomEfficiency.Measurement Measurement { get; private set; }

    /// <summary>
    /// How well-built the room is: multiplies its capacity and the effect it
    /// has on minions (sleep, training, research…). 1 = this room type's
    /// ordinary rate; see RoomEfficiency for the formula.
    /// </summary>
    public float Efficiency { get; private set; } = 1f;

    /// <summary>
    /// What the room holds — gold for a Treasury, beds for a Lair, chickens
    /// for a Hatchery: tiles × capacityPerTile, × Efficiency when the room
    /// type's capacity scales with it, rounded down.
    /// </summary>
    public int Capacity
    {
        get
        {
            if (Definition == null) return 0;
            float capacity = Cells.Count * Definition.capacityPerTile;
            if (Definition.capacityScalesWithEfficiency) capacity *= Efficiency;
            return (int)(capacity + 0.0001f); // tolerance: 9 × 500 × 0.68 must give 3060, not 3059
        }
    }

    public int SellValue(TileRegistry registry) =>
        Cells.Count * registry.GetSellValue(TileType);

    public DungeonRoom(int id, TileType type, FactionID owner, TileDefinition definition = null)
    {
        RoomId     = id;
        TileType   = type;
        Owner      = owner;
        Definition = definition;
    }

    /// <summary>Called by RoomRegistry once the room's cells are all added.</summary>
    public void SetMeasurement(RoomEfficiency.Measurement measurement)
    {
        Measurement = measurement;
        Efficiency  = Definition != null
            ? RoomEfficiency.Efficiency(measurement, Definition.baseEfficiency,
                                        Definition.shapeWeight, Definition.wallWeight)
            : 1f;
    }

    public void AddCell(GridCell cell)
    {
        Cells.Add(cell);
        cell.RoomId = RoomId;
    }
}
