using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Reads the RoomData workbook's Rooms sheet into plain records, validating
/// as it goes. Knows nothing about assets — RoomDataImporter writes the
/// records onto TileDefinitions. Problems become warnings naming the sheet and
/// row, with a sensible default, so one typo never blocks the rest.
/// </summary>
public class RoomWorkbookParser : WorkbookParser
{
    public enum Placement { Tunnel, Liquid, None }

    public class RoomRecord
    {
        public TileType Type;
        public string   DisplayName, Description, Location;
        public bool     IsRoom, PlayerBuildable, RequiresAdjacency, Indestructible, CapacityScalesWithEfficiency;
        public int      BuyCost, SellValue;
        /// <summary>0–1 RGB, or null if the Colour cell was blank or invalid (asset keeps its colour).</summary>
        public float[]  Colour;
        public Placement PlacesOn;
        public float    CapacityPerTile, BaseEfficiency, ShapeWeight, WallWeight;
        /// <summary>HUD grid slot (1-9), 0 = none. Null when the sheet has no ButtonRow/ButtonColumn columns.</summary>
        public int?     ButtonRow, ButtonColumn;
    }

    public readonly List<RoomRecord> Rooms = new();

    public static RoomWorkbookParser Parse(XlsxReader book)
    {
        var p     = new RoomWorkbookParser();
        var sheet = book.GetSheet("Rooms");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "RoomID") : null;
        if (table == null)
        {
            p.Warnings.Add("No Rooms sheet (with a RoomID header) — nothing imported.");
            return p;
        }

        bool hasSlots = table.Headers.Contains("ButtonRow", StringComparer.OrdinalIgnoreCase) &&
                        table.Headers.Contains("ButtonColumn", StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows) p.ReadRow(row, hasSlots);
        p.CheckButtonSlots(hasSlots);
        return p;
    }

    private void ReadRow(XlsxReader.Row row, bool hasSlots)
    {
        string id = row["RoomID"];
        if (id == null) { Warn(row, "No RoomID — skipped."); return; }
        if (!TileTypeNames.TryParse(id, out TileType type))
        {
            Warn(row, $"RoomID '{id}' isn't a TileType — add it to the TileType list in TileDefinition.cs first. Skipped.");
            return;
        }
        if (TileTypeNames.Legacy.ContainsKey(id.Trim()))
            Warn(row, $"RoomID '{id}' is an old name — read as {type}; update the sheet.");
        if (Rooms.Any(r => r.Type == type))
        {
            Warn(row, $"{type} appears twice — this row skipped (first one kept).");
            return;
        }

        var r = new RoomRecord
        {
            Type              = type,
            Location          = $"{row.SheetName} row {row.ExcelRow}",
            DisplayName       = row["DisplayName"] ?? type.ToString(),
            Description       = row["Description"] ?? "",
            IsRoom            = Bool(row, "IsRoom", true),
            PlayerBuildable   = Bool(row, "PlayerBuildable", false),
            BuyCost           = Int(row, "BuyCost", 0),
            SellValue         = Int(row, "SellValue", 0),
            PlacesOn          = ParseEnum(row, "PlacesOn", Placement.Tunnel),
            RequiresAdjacency = Bool(row, "RequiresAdjacency", false),
            Indestructible    = Bool(row, "Indestructible", false),
            CapacityPerTile   = Float(row, "CapacityPerTile", 0f),
            CapacityScalesWithEfficiency = Bool(row, "CapacityScalesWithEfficiency", true),
            BaseEfficiency    = Float(row, "BaseEfficiency", 1f),
            ShapeWeight       = Float(row, "ShapeWeight", 0f),
            WallWeight        = Float(row, "WallWeight", 0f),
            Colour            = ParseColour(row),
        };

        if (r.PlayerBuildable && r.PlacesOn == Placement.None)
            Warn(row, $"{type} is PlayerBuildable but PlacesOn is None — it can't be placed anywhere.");
        if (r.PlayerBuildable && r.BuyCost <= 0)
            Warn(row, $"{type} is PlayerBuildable with a BuyCost of {r.BuyCost}.");
        if (r.SellValue > r.BuyCost && r.BuyCost > 0)
            Warn(row, $"{type} sells for more ({r.SellValue}) than it costs ({r.BuyCost}).");
        if (r.CapacityPerTile < 0) { Warn(row, "CapacityPerTile is negative — using 0."); r.CapacityPerTile = 0; }

        if (hasSlots)
        {
            r.ButtonRow    = Slot(row, "ButtonRow");
            r.ButtonColumn = Slot(row, "ButtonColumn");
            if ((r.ButtonRow == 0) != (r.ButtonColumn == 0))
            {
                Warn(row, $"{type} has only one of ButtonRow/ButtonColumn — needs both; given no slot.");
                r.ButtonRow = r.ButtonColumn = 0;
            }
        }

        Rooms.Add(r);
    }

    /// <summary>A grid slot 1-9; blank = 0 (no slot).</summary>
    private int Slot(XlsxReader.Row row, string header)
    {
        int value = Int(row, header, 0);
        if (value >= 0 && value <= 9) return value;
        Warn(row, $"{header} {value} is outside 1-9 (one digit per hotkey) — given no slot.");
        return 0;
    }

    /// <summary>Every buildable room should have its own slot.</summary>
    private void CheckButtonSlots(bool hasSlots)
    {
        if (!hasSlots)
        {
            Warnings.Add("Rooms sheet has no ButtonRow/ButtonColumn columns — HUD slots on the tile definitions left as they are.");
            return;
        }

        var taken = new Dictionary<(int, int), TileType>();
        foreach (var r in Rooms)
        {
            if (!r.PlayerBuildable) continue;
            if (r.ButtonRow == 0)
            {
                Warnings.Add($"{r.Location}: {r.Type} is buildable but has no ButtonRow/ButtonColumn — it goes after the others, with no hotkey.");
                continue;
            }
            var slot = (r.ButtonRow.Value, r.ButtonColumn.Value);
            if (taken.TryGetValue(slot, out var other))
                Warnings.Add($"{r.Location}: {r.Type} and {other} both use row {slot.Item1}, column {slot.Item2} — the hotkey picks {other}.");
            else taken[slot] = r.Type;
        }
    }

    /// <summary>"DD8800" or "#DD8800" → 0–1 RGB.</summary>
    private float[] ParseColour(XlsxReader.Row row)
    {
        string text = row["Colour"];
        if (text == null) return null;

        string hex = text.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return new[] { (rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f };

        Warn(row, $"Colour '{text}' isn't a 6-digit hex colour like DD8800 — colour left unchanged.");
        return null;
    }
}
