using UnityEngine;
using UnityEngine.Serialization;

// ── Tile categories ────────────────────────────────────────────────────────
public enum TileCategory
{
    Environmental,  // Bedrock, Stone, Cave, Gold, Gem — never owned, no buy/sell UI
    Liquid,         // Water, Lava                     — never owned, bridge placement target
    Owned,          // Tunnel, Wall, rooms, Bridge, Heart, Portal — faction ownership
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
// Stored as numbers in Unity assets and as names in level/save JSON, so:
// append new types at the end, never reorder, and when renaming one add the
// old name to TileTypeNames.Legacy so existing JSON still loads.
//
// What a type IS (room or not, buildable, efficiency weights) lives on its
// TileDefinition, filled from the RoomData workbook — never in code lists.
public enum TileType
{
    // Environmental
    Bedrock = 0,   // indestructible border / obstacle
    Stone   = 1,   // impassable; worker-minable → Cave
    Cave    = 2,   // passable, unowned; worker-claimable → Tunnel

    // Liquid
    Water   = 3,   // environmental, blocks movement; bridge target
    Lava    = 4,   // environmental, hazard; bridge target

    // Owned
    Tunnel  = 5,   // base claimed floor; buy target for rooms
    Wall    = 6,   // worker-reinforced perimeter; faction-owned, not buy/sellable via UI
    Treasury = 7,  // gold storage; where minions collect pay        (was RoomA)
    Lair     = 8,  // minions claim a bed here and sleep              (was RoomB)
    Hatchery = 9,  // chickens for hungry minions                     (was RoomC)
    Bridge  = 10,  // built over Water/Lava; owned; sells back to underlying liquid

    // Environmental (resource)
    Gold    = 11,  // impassable; worker-minable for currency → Cave on depletion
    Gem     = 12,  // impassable; worker-harvestable for currency; indestructible

    // Owned (structures)
    Heart   = 13,  // 3x3 win/lose structure; impassable, not buy/sellable via UI
    Portal  = 14,  // 1x1 minion spawn point; passable, not buy/sellable via UI

    // Rooms (functionality arrives in phases — see the RoomData workbook)
    TrainingRoom   = 15,  // minions train to gain experience, at a cost
    Library        = 16,  // research
    GuardPost      = 17,  // minions dropped here defend and patrol it
    Workshop       = 18,  // traps and doors are built here
    Prison         = 19,  // captured enemy minions are held here
    TortureChamber = 20,  // converts enemy minions by repeated injury
    Barracks       = 21,  // groups minions into squads
    Shrine         = 22,  // prayer and sacrifice
    Graveyard      = 23,  // bodies are brought here
    ScavengerRoom  = 24,  // steals research or minions from enemies
    HeroGate       = 25,  // non-player structure that summons heroes
}

/// <summary>
/// Name lookups for TileType that also accept names a type used to have,
/// so level files, saves and workbooks written before a rename still load.
/// </summary>
public static class TileTypeNames
{
    /// <summary>Old name → current type. Add an entry whenever a TileType is renamed.</summary>
    public static readonly System.Collections.Generic.IReadOnlyDictionary<string, TileType> Legacy =
        new System.Collections.Generic.Dictionary<string, TileType>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["RoomA"] = TileType.Treasury,
            ["RoomB"] = TileType.Lair,
            ["RoomC"] = TileType.Hatchery,
        };

    /// <summary>Current or legacy name (case-insensitive) → type. Numbers are rejected.</summary>
    public static bool TryParse(string text, out TileType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string name = text.Trim();

        foreach (TileType t in System.Enum.GetValues(typeof(TileType)))
            if (string.Equals(t.ToString(), name, System.StringComparison.OrdinalIgnoreCase))
            { type = t; return true; }

        return Legacy.TryGetValue(name, out type);
    }
}

// ── TileDefinition ScriptableObject ───────────────────────────────────────
[CreateAssetMenu(menuName = "Dungeon2D/TileDefinition", fileName = "NewTileDefinition")]
public class TileDefinition : ScriptableObject
{
    [Header("Identity")]
    public TileType      tileType;
    public string        tileName;
    [TextArea] public string description;
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

    [Header("Worker Interaction")]
    [Tooltip("True for Bedrock and Gem: workers cannot mine or destroy this tile.")]
    public bool isIndestructible = false;

    [Tooltip("Max hit points before this tile is destroyed. " +
             "Ignored if isIndestructible is true.")]
    public int  maxHitPoints     = 0;

    [Tooltip("For Gold and Gem: total wealth available to harvest.")]
    public int  wealthCapacity   = 0;

    [Tooltip("Damage per second a worker deals to this tile.")]
    [FormerlySerializedAs("impDamagePerSecond")]
    public float workerDamagePerSecond = 20f;

    [Header("Hazard")]
    [Tooltip("Damage per second dealt to a minion standing here that cannot "
             + "safely path on this tile. Water low, Lava high. 0 for safe tiles.")]
    public float hazardDamagePerSecond = 0f;

    [Header("Room")]
    [Tooltip("Forms rooms: contiguous same-type, same-owner cells are tracked as one " +
             "DungeonRoom (with an efficiency score), sellable via the UI, and count " +
             "as dungeon floor. Set from the RoomData workbook.")]
    public bool isRoom = false;
    [Tooltip("Gets a build button in the HUD. False for Heart, Portal and Hero Gate.")]
    public bool playerBuildable = false;
    [Tooltip("Slot in the HUD footer's Rooms grid, 1-9 — also the hotkey: Tab, then row, " +
             "then column. 0 = no fixed slot (placed after the others, no hotkey).")]
    [Range(0, 9)] public int buttonRow    = 0;
    [Range(0, 9)] public int buttonColumn = 0;
    [Tooltip("What one tile holds at 100% efficiency — gold for a Treasury, beds for a " +
             "Lair, chickens for a Hatchery. 0 = no capacity. See DungeonRoom.Capacity.")]
    public float capacityPerTile = 0f;
    [Tooltip("True: capacity grows with efficiency (Treasury gold). False: capacity is " +
             "physical (one bed per Lair tile) and efficiency only speeds up the effect.")]
    public bool capacityScalesWithEfficiency = true;

    [Header("Room efficiency (see RoomEfficiency)")]
    [Tooltip("Efficiency of the worst possible layout: a sprawling, unwalled room.")]
    public float baseEfficiency = 1f;
    [Tooltip("Added at a perfectly compact rectangle (3x3, 4x4…), scaled down for sprawl.")]
    public float shapeWeight = 0f;
    [Tooltip("Added when every edge of the room is your own reinforced wall.")]
    public float wallWeight = 0f;

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
