using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reads the Research workbook's Web sheet into research nodes — one row
/// per node, ResearchID required, every other column optional (see the
/// workbook's ReadMe). Knows nothing about assets: ResearchImporter links
/// icons and checks the web. Problems become warnings naming the row.
/// </summary>
public class ResearchWorkbookParser : WorkbookParser
{
    public readonly List<ResearchCatalogue.Node> Nodes = new();
    public readonly List<string> Languages = new();

    public static ResearchWorkbookParser Parse(XlsxReader book)
    {
        var p = new ResearchWorkbookParser();
        p.ReadWeb(book);
        return p;
    }

    private void ReadWeb(XlsxReader book)
    {
        var sheet = book.GetSheet("Web");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "ResearchID") : null;
        if (table == null) { Warnings.Add("No Web sheet (with a ResearchID header) — nothing imported."); return; }

        foreach (var h in table.Headers)
            foreach (var prefix in new[] { "Name_", "Description_" })
                if (h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && h.Length > prefix.Length)
                {
                    string lang = h.Substring(prefix.Length).ToLowerInvariant();
                    if (!Languages.Contains(lang)) Languages.Add(lang);
                }

        foreach (var row in table.Rows)
        {
            string id = row["ResearchID"];
            if (id == null) continue;
            if (Nodes.Any(n => string.Equals(n.id, id, StringComparison.OrdinalIgnoreCase)))
            { Warn(row, $"Duplicate ResearchID {id} — skipped (first kept)."); continue; }

            bool hasAngle = row.Has("Angle"), hasRadius = row.Has("Radius");
            if (hasAngle != hasRadius) Warn(row, $"{id} has only one of Angle and Radius — it needs both (or neither, to take a replaced node's place).");
            var node = new ResearchCatalogue.Node
            {
                id           = id,
                names        = Texts(row, "Name_"),
                descriptions = Texts(row, "Description_"),
                factions     = SplitList(row["Factions"]),
                replaces     = row["Replaces"],
                start        = Bool(row, "Start", false),
                links        = SplitList(row["Links"]),
                requires     = row["Requires"],
                cost         = Math.Max(0f, Float(row, "Cost", 100f)),
                hasPosition  = hasAngle && hasRadius,
                angle        = Float(row, "Angle", 0f),
                radius       = Math.Max(0f, Float(row, "Radius", 0f)),
                iconName     = row["Icon"],
                effects      = ResearchEffect.SplitCell(row["Effects"]),
                autoWeight   = Math.Max(0f, Float(row, "AutoWeight", 1f)),
                announcement = row["Announcement"],
            };
            if (!node.hasPosition && string.IsNullOrEmpty(node.replaces))
                Warn(row, $"{id} has no Angle and Radius — it sits at the centre.");
            if (node.cost <= 0f) Warn(row, $"{id} costs nothing — it completes the moment it's chosen.");
            Nodes.Add(node);
        }
    }

    private static List<ResearchCatalogue.LocalText> Texts(XlsxReader.Row row, string prefix)
    {
        var list = new List<ResearchCatalogue.LocalText>();
        foreach (var pair in row.Values)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || pair.Key.Length <= prefix.Length) continue;
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            list.Add(new ResearchCatalogue.LocalText { language = pair.Key.Substring(prefix.Length).ToLowerInvariant(), text = pair.Value.Trim() });
        }
        return list;
    }
}
