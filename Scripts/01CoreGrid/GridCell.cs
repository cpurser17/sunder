/// <summary>
/// Pure data class for one logical grid cell. No Unity objects.
///
/// UnderlyingType — set when a Bridge is placed, records the Water or Lava
///                  tile beneath so selling the bridge can restore it.
///                  For all non-bridge tiles this equals TileType.
///
/// Owner          — FactionID.Unaligned for all environmental and liquid tiles.
///                  Set to the placing faction for Tunnel, Wall, Rooms, Bridge.
///
/// CurrentHP      — -1 when not being worked. Set by ImpController when work begins.
/// WealthRemaining — remaining harvestable wealth for Gold/Gem tiles.
/// </summary>
public class GridCell
{
    public int       X              { get; }
    public int       Y              { get; }
    public TileType  TileType       { get; private set; } = TileType.Stone;
    public TileType  UnderlyingType { get; private set; } = TileType.Stone;
    public FactionID Owner          { get; private set; } = FactionID.Unaligned;
    public int       RoomId         { get; set; } = -1;

    // ── Imp working state ──────────────────────────────────────────────
    /// <summary>Remaining HP. -1 = not currently being worked.</summary>
    public int CurrentHP        { get; private set; } = -1;

    // ── Wealth (Gold/Gem tiles only) ───────────────────────────────────
    /// <summary>Remaining harvestable wealth. Set from TileDefinition.wealthCapacity.</summary>
    public int WealthRemaining  { get; private set; } = 0;

    public GridCell(int x, int y)
    {
        X = x;
        Y = y;
    }

    // ── Tile type setters ──────────────────────────────────────────────

    /// <summary>Standard tile change — underlying type tracks the new type.</summary>
    public void SetTileTypeInternal(TileType type, FactionID owner = FactionID.Unaligned)
    {
        TileType       = type;
        UnderlyingType = type;
        Owner          = owner;
    }

    /// <summary>Bridge placement — UnderlyingType retains the liquid type.</summary>
    public void PlaceBridgeInternal(FactionID owner)
    {
        UnderlyingType = TileType;
        TileType       = TileType.Bridge;
        Owner          = owner;
    }

    /// <summary>
    /// Restores a cell exactly as saved, including a Bridge's underlying
    /// liquid type. Unlike PlaceBridgeInternal — which derives UnderlyingType
    /// from whatever this cell's TileType currently is, correct for placing a
    /// NEW bridge over existing liquid at runtime — a freshly constructed
    /// GridCell has no prior state to derive from, so loading a saved Bridge
    /// through PlaceBridgeInternal silently lost its underlying liquid type.
    /// </summary>
    public void LoadFromSave(TileType tileType, TileType underlyingType, FactionID owner)
    {
        TileType       = tileType;
        UnderlyingType = underlyingType;
        Owner          = owner;
    }

    /// <summary>Sell a bridge — restores the underlying liquid tile.</summary>
    public void RemoveBridgeInternal()
    {
        TileType       = UnderlyingType;
        UnderlyingType = TileType;
        Owner          = FactionID.Unaligned;
    }

    /// <summary>Transfer ownership without changing tile type.</summary>
    public void SetOwnerInternal(FactionID owner) => Owner = owner;

    // ── HP management ──────────────────────────────────────────────────

    /// <summary>Initialises HP when an imp starts working this tile.</summary>
    public void InitialiseHP(int maxHP) => CurrentHP = maxHP;

    /// <summary>
    /// Applies damage. Returns true if the tile is destroyed (HP reaches 0).
    /// Returns false if the tile is not being worked (HP == -1).
    /// </summary>
    public bool ApplyDamage(int damage)
    {
        if (CurrentHP < 0) return false;
        CurrentHP = System.Math.Max(0, CurrentHP - damage);
        return CurrentHP <= 0;
    }

    /// <summary>Resets HP when an imp abandons the job.</summary>
    public void ResetHP() => CurrentHP = -1;

    // ── Wealth management ──────────────────────────────────────────────

    /// <summary>Initialises wealth for Gold/Gem tiles.</summary>
    public void InitialiseWealth(int capacity) => WealthRemaining = capacity;

    /// <summary>
    /// Extracts wealth equal to damage dealt, capped by the imp's remaining
    /// carry space and the tile's remaining wealth. Returns amount collected.
    /// </summary>
    public int ExtractWealth(int damage, int impCarrySpace)
    {
        int collected   = System.Math.Min(damage, impCarrySpace);
        collected       = System.Math.Min(collected, WealthRemaining);
        WealthRemaining = System.Math.Max(0, WealthRemaining - collected);
        return collected;
    }
}
