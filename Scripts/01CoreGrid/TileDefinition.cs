using UnityEngine;

// ── Tile categories ────────────────────────────────────────────────────────
public enum TileCategory
{
    Environmental,  // Bedrock, Stone, Cave, Gold, Gem — never owned, no buy/sell UI
    Liquid,         // Water, Lava                     — never owned, bridge placement target
    Owned,          // Tunnel, Wall, Rooms, Bridge      — faction ownership, full UI
}

// ── Traversal types (used by NavMesh baking and grid pathfinding) ──────────
public enum TraversalType
{
    Impassable,  // Bedrock, Stone, Gold, Gem, enemy Wall
    Normal,      // Cave, Tunnel, Rooms, Bridge
    Liquid,      // Water, Lava — impassable unless a Bridge occupies the cell
    Hazard,      // Lava specifically — damages units that cross (applied on top of Liquid)
}

// ── Tile types ─────────────────────────────────────────────────────────────
public enum TileType
{
    // Environmental
    Bedrock = 0,   // indestructible border / obstacle
    Stone   = 1,   // impassable; imp-minable → Cave
    Cave    = 2,   // passable, unowned; imp-claimable → Tunnel

    // Liquid
    Water   = 3,   // environmental, blocks movement; bridge target
    Lava    = 4,   // environmental, hazard; bridge target

    // Owned
    Tunnel  = 5,   // base claimed floor; buy target for rooms
    Wall    = 6,   // imp-reinforced perimeter; faction-owned, not buy/sellable via UI
    RoomA   = 7,
    RoomB   = 8,
    RoomC   = 9,
    Bridge  = 10,  // built over Water/Lava; owned; sells back to underlying liquid

    // Environmental (resource)
    Gold    = 11,  // impassable; imp-minable for currency → Cave on depletion
    Gem     = 12,  // impassable; imp-harvestable for currency; indestructible
}

// ── TileDefinition ScriptableObject ───────────────────────────────────────
[CreateAssetMenu(menuName = "Dungeon2D/TileDefinition", fileName = "NewTileDefinition")]
public class TileDefinition : ScriptableObject
{
    [Header("Identity")]
    public TileType      tileType;
    public string        tileName;
    public TileCategory  category;
    public TraversalType traversalType;

    [Header("Visuals")]
    [Tooltip("Colour shown on the 2D grid for the Unaligned faction. " +
             "Owned tile colours are tinted per-faction at runtime.")]
    public Color gridColour = Color.grey;

    [Header("Economy")]
    [Tooltip("Gold cost to buy one cell. 0 = not buyable via UI.")]
    public int buyCost   = 0;
    [Tooltip("Gold returned when selling one cell. 0 = not sellable via UI.")]
    public int sellValue = 0;

    [Header("Imp Interaction")]
    [Tooltip("True for Bedrock and Gem: imps cannot mine or destroy this tile.")]
    public bool isIndestructible = false;

    [Tooltip("Max hit points before this tile is destroyed. " +
             "Ignored if isIndestructible is true.")]
    public int  maxHitPoints     = 0;

    [Tooltip("For Gold and Gem: total wealth available to harvest.")]
    public int  wealthCapacity   = 0;

    [Tooltip("Damage per second an imp deals to this tile.")]
    public float impDamagePerSecond = 20f;

    [Header("Hazard")]
    [Tooltip("Damage per second dealt to a minion standing here that cannot "
             + "safely path on this tile. Water low, Lava high. 0 for safe tiles.")]
    public float hazardDamagePerSecond = 0f;

    [Header("Placement Rules")]
    [Tooltip("True for Bridge: placement requires adjacency to an owned tile.")]
    public bool requiresAdjacency = false;
    [Tooltip("True for Bridge: can only be placed on Liquid tiles.")]
    public bool placesOnLiquid    = false;
    [Tooltip("True for Rooms: can only be placed on owned Tunnel tiles.")]
    public bool placesOnTunnel    = false;

    [Header("3D Asset")]
    public GameObject prefab3D;

    [Tooltip("Offset applied to the asset position relative to the cell centre. "  +
             "Use Y to raise the asset if its pivot is not at floor level.")]
    public Vector3 positionOffset = Vector3.zero;

    [Tooltip("Rotation applied to the asset on spawn. "  +
             "Useful if the model was exported facing the wrong axis.")]
    public Vector3 rotationOffset = Vector3.zero;

    [Tooltip("Uniform scale applied to the asset on spawn. "  +
             "Adjust if the model was not exported at 1 unit = 1 metre.")]
    public float scaleMultiplier = 1f;
}
