using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reads the SpellData workbook's Spells sheet into SpellDefinitions,
/// validating as it goes. Knows nothing about assets — SpellDataImporter
/// builds the catalogue. Problems become warnings naming the row.
///
/// One row per spell. Id is required; every other column falls back to a
/// default when blank or missing, so new columns can be added to the code
/// before the workbook has them. Description and anything else unknown is
/// for you and ignored (Description is kept as the spell's tooltip text).
/// </summary>
public class SpellWorkbookParser : WorkbookParser
{
    public readonly List<SpellDefinition> Spells = new();

    public static SpellWorkbookParser Parse(XlsxReader book)
    {
        var p = new SpellWorkbookParser();
        p.ReadSpells(book);
        return p;
    }

    private void ReadSpells(XlsxReader book)
    {
        var sheet = book.GetSheet("Spells");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "Id") : null;
        if (table == null) { Warnings.Add("No Spells sheet (with an Id header) — nothing imported."); return; }

        var slots = new Dictionary<(int, int), string>();
        foreach (var row in table.Rows)
        {
            string id = row["Id"];
            if (id == null) continue;
            if (Spells.Any(s => string.Equals(s.id, id, StringComparison.OrdinalIgnoreCase)))
            { Warn(row, $"Duplicate Id {id} — skipped (first kept)."); continue; }

            var spell = new SpellDefinition
            {
                id               = id,
                displayName      = row["Name"] ?? id,
                effect           = row["Effect"] ?? id,
                description      = row["Description"] ?? "",
                targeting        = ParseEnum(row, "Targeting", SpellTargeting.Tile),
                castOn           = ParseEnum(row, "CastOn",    SpellCastOn.Visible),
                cost             = Math.Max(0, Int(row, "Cost", 0)),
                costPerLevel     = Int(row, "CostPerLevel", 0),
                maxLevel         = Math.Max(1, Int(row, "MaxLevel", 1)),
                chargeSeconds    = Math.Max(0.05f, Float(row, "ChargeSeconds", 0.5f)),
                cooldown         = Math.Max(0f, Float(row, "Cooldown", 0f)),
                power            = Float(row, "Power", 0f),
                powerPerLevel    = Float(row, "PowerPerLevel", 0f),
                radius           = Math.Max(0f, Float(row, "Radius", 0f)),
                radiusPerLevel   = Float(row, "RadiusPerLevel", 0f),
                duration         = Math.Max(0f, Float(row, "Duration", 0f)),
                durationPerLevel = Float(row, "DurationPerLevel", 0f),
                damageType       = ParseEnum(row, "DamageType", DamageType.Lightning),
                upkeepGold       = Math.Max(0, Int(row, "UpkeepGold", 0)),
                upkeepInterval   = Math.Max(0.5f, Float(row, "UpkeepInterval", 10f)),
                buttonRow        = Int(row, "ButtonRow", 0),
                buttonColumn     = Int(row, "ButtonColumn", 0),
            };

            if (spell.buttonRow > 0 && spell.buttonColumn > 0)
            {
                var slot = (spell.buttonRow, spell.buttonColumn);
                if (slots.TryGetValue(slot, out var other))
                    Warn(row, $"{id} shares button slot {slot.Item1}·{slot.Item2} with {other}.");
                else slots[slot] = id;
            }

            if (SpellEffect.Find(spell.EffectId) == null)
                Warn(row, $"{id}: no effect script \"{spell.EffectId}\" yet — imported, but it gets no button.");

            Spells.Add(spell);
        }
    }
}
