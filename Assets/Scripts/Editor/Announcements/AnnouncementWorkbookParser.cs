using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reads the Announcements workbook into plain records, validating as it
/// goes. Knows nothing about assets — AnnouncementImporter builds the
/// catalogue. Problems become warnings naming the sheet and row.
///
///   Announcements  one row per announcement: Id, Priority, Cooldown
///                  (Description and anything else is for you, ignored)
///   Lines          one row per line: Id, Variant, Weight, then the text in
///                  each language — one Text_&lt;code&gt; column per language
///                  (Text_en, Text_fr…)
/// </summary>
public class AnnouncementWorkbookParser : WorkbookParser
{
    public class LineRecord
    {
        public string Variant;
        public float  Weight;
        /// <summary>Language code → text.</summary>
        public Dictionary<string, string> Texts = new(StringComparer.OrdinalIgnoreCase);
    }

    public class AnnouncementRecord
    {
        public string Id;
        public Announcer.Priority Priority;
        public float Cooldown;
        public List<LineRecord> Lines = new();
    }

    public readonly List<AnnouncementRecord> Announcements = new();
    /// <summary>Every language with a Text_ column, in sheet order.</summary>
    public readonly List<string> Languages = new();

    public static AnnouncementWorkbookParser Parse(XlsxReader book)
    {
        var p = new AnnouncementWorkbookParser();
        p.ReadAnnouncements(book);
        p.ReadLines(book);

        foreach (var a in p.Announcements.Where(a => a.Lines.Count == 0))
            p.Warnings.Add($"{a.Id} has no rows on the Lines sheet — it will show its id as text.");
        return p;
    }

    private void ReadAnnouncements(XlsxReader book)
    {
        var sheet = book.GetSheet("Announcements");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "Id") : null;
        if (table == null) { Warnings.Add("No Announcements sheet (with an Id header) — nothing imported."); return; }

        foreach (var row in table.Rows)
        {
            string id = row["Id"];
            if (id == null) continue;
            if (Announcements.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
            { Warn(row, $"Duplicate Id {id} — skipped (first kept)."); continue; }

            Announcements.Add(new AnnouncementRecord
            {
                Id       = id,
                Priority = ParseEnum(row, "Priority", Announcer.Priority.Normal),
                Cooldown = Math.Max(0f, Float(row, "Cooldown", 30f)),
            });
        }
    }

    private void ReadLines(XlsxReader book)
    {
        var sheet = book.GetSheet("Lines");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "Id") : null;
        if (table == null) { Warnings.Add("No Lines sheet (with an Id header) — announcements have no text."); return; }

        foreach (var h in table.Headers)
            if (h.StartsWith("Text_", StringComparison.OrdinalIgnoreCase) && h.Length > 5)
                Languages.Add(h.Substring(5).ToLowerInvariant());
        if (Languages.Count == 0) Warnings.Add("Lines sheet has no Text_<language> column (e.g. Text_en).");

        foreach (var row in table.Rows)
        {
            string id = row["Id"];
            if (id == null) continue;
            var a = Announcements.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (a == null) { Warn(row, $"Id {id} isn't on the Announcements sheet — line skipped."); continue; }

            string variant = row["Variant"] ?? (a.Lines.Count + 1).ToString();
            if (a.Lines.Any(l => string.Equals(l.Variant, variant, StringComparison.OrdinalIgnoreCase)))
            { Warn(row, $"{id} already has a variant {variant} — skipped."); continue; }

            var line = new LineRecord { Variant = variant, Weight = Math.Max(0f, Float(row, "Weight", 1f)) };
            foreach (var lang in Languages)
            {
                string text = row[$"Text_{lang}"];
                if (text != null) line.Texts[lang] = text;
            }
            if (line.Texts.Count == 0) Warn(row, $"{id} variant {variant} has no text in any language.");
            a.Lines.Add(line);
        }
    }
}
