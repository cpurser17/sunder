/// <summary>
/// Identifies which faction owns a tile or asset.
/// Unaligned (0) means unclaimed — visible to all, capturable by the first faction to reach it.
/// Extend with additional AI factions as needed.
/// </summary>
public enum FactionID
{
    Unaligned = 0,
    Player    = 1,
    AI1       = 2,
    AI2       = 3,
    AI3       = 4,
}
