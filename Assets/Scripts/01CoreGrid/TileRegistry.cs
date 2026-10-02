using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds all TileDefinitions and provides O(1) lookup by TileType.
/// Also exposes per-faction colour tinting for owned tiles.
///
/// Faction tint colours are applied on top of the base gridColour
/// for owned tile types, so the grid shows both tile type and ownership.
/// Create a single instance via Assets > Create > Dungeon2D > TileRegistry.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/TileRegistry", fileName = "TileRegistry")]
public class TileRegistry : ScriptableObject
{
    [System.Serializable]
    public struct FactionColourEntry
    {
        public FactionID  faction;
        public Color      colour;
    }

    [SerializeField] private List<TileDefinition>    definitions    = new();
    [SerializeField] private List<FactionColourEntry> factionColours = new();

    private Dictionary<TileType,   TileDefinition> _defMap;
    private Dictionary<FactionID,  Color>          _factionMap;

    public void Initialise()
    {
        _defMap     = new Dictionary<TileType,  TileDefinition>();
        _factionMap = new Dictionary<FactionID, Color>();

        foreach (var def in definitions)
            _defMap[def.tileType] = def;

        foreach (var entry in factionColours)
            _factionMap[entry.faction] = entry.colour;
    }

    // ── Definition lookups ─────────────────────────────────────────────

    public TileDefinition GetDefinition(TileType type) =>
        _defMap.TryGetValue(type, out var def) ? def : null;

    public TileCategory  GetCategory(TileType  type) =>
        GetDefinition(type)?.category     ?? TileCategory.Environmental;

    public TraversalType GetTraversal(TileType type) =>
        GetDefinition(type)?.traversalType ?? TraversalType.Impassable;

    public int  GetBuyCost(TileType  type) => GetDefinition(type)?.buyCost   ?? 0;
    public int  GetSellValue(TileType type) => GetDefinition(type)?.sellValue ?? 0;

    public string GetTileName(TileType type) =>
        GetDefinition(type)?.tileName ?? type.ToString();

    public bool IsIndestructible(TileType type) =>
        GetDefinition(type)?.isIndestructible ?? false;

    public bool RequiresAdjacency(TileType type) =>
        GetDefinition(type)?.requiresAdjacency ?? false;
    public bool PlacesOnLiquid(TileType type) =>
        GetDefinition(type)?.placesOnLiquid ?? false;
    public bool PlacesOnTunnel(TileType type) =>
        GetDefinition(type)?.placesOnTunnel ?? false;

    // ── Colour lookups ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the display colour for a cell, incorporating faction tinting
    /// for owned tiles.
    /// </summary>
    public Color GetColour(TileType type, FactionID owner = FactionID.Unaligned)
    {
        var def = GetDefinition(type);
        if (def == null) return Color.magenta;

        // Environmental and Liquid tiles are never tinted by faction.
        if (def.category != TileCategory.Owned) return def.gridColour;

        // Owned tiles: lerp between tile colour and faction colour so both
        // the tile type and ownership are visible at a glance.
        if (_factionMap.TryGetValue(owner, out Color factionCol))
            return Color.Lerp(def.gridColour, factionCol, 0.45f);

        return def.gridColour;
    }
}
