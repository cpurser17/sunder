using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

/// <summary>
/// Minimal read-only .xlsx reader for the minion data importer — no plugins.
/// An .xlsx is a zip of XML parts; this reads the workbook's sheet list,
/// shared strings, defined names and each sheet's cell values.
///
/// Formula cells return the value Excel last calculated and saved, so the
/// file must have been saved from Excel (or similar) after editing. The file
/// is opened with sharing enabled, so it can stay open in Excel while importing.
/// </summary>
public class XlsxReader
{
    public class Sheet
    {
        public string Name;
        /// <summary>Row index (0-based) → column index (0-based) → text.</summary>
        public readonly SortedDictionary<int, Dictionary<int, string>> Cells = new();

        public string Get(int row, int col) =>
            Cells.TryGetValue(row, out var r) && r.TryGetValue(col, out var v) ? v : null;

        public int MaxColumn => Cells.Count == 0 ? -1 : Cells.Values.Max(r => r.Count == 0 ? -1 : r.Keys.Max());
    }

    /// <summary>
    /// A sheet read as a table: one header row, then one record per non-empty
    /// row keyed by header text. Header matching ignores case and spaces, so
    /// "Display Name" and "DisplayName" are the same column.
    /// </summary>
    public class Table
    {
        public string SheetName;
        public List<string> Headers = new();
        public List<Row> Rows = new();
    }

    public class Row
    {
        public string SheetName;
        /// <summary>1-based, as Excel shows it — for error messages.</summary>
        public int ExcelRow;
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Trimmed text of a column, or null if blank/missing.</summary>
        public string this[string header] =>
            Values.TryGetValue(NormaliseHeader(header), out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        public bool Has(string header) => this[header] != null;
    }

    public readonly List<Sheet> Sheets = new();
    /// <summary>Workbook-level defined names → their reference text, e.g. MaxLevel → LevelFormula!$B$20.</summary>
    public readonly Dictionary<string, string> DefinedNames = new(StringComparer.OrdinalIgnoreCase);

    public static XlsxReader Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var reader = new XlsxReader();
        reader.Read(zip);
        return reader;
    }

    public Sheet GetSheet(string name) =>
        Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string NormaliseHeader(string header) =>
        header == null ? "" : new string(header.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// Reads a sheet as a table whose header row is the first row with
    /// <paramref name="firstHeader"/> in column A (so notes above the table
    /// are skipped). Returns null if no such row exists.
    /// </summary>
    public static Table ReadTable(Sheet sheet, string firstHeader)
    {
        int headerRow = -1;
        foreach (var row in sheet.Cells)
            if (string.Equals(NormaliseHeader(sheet.Get(row.Key, 0)), NormaliseHeader(firstHeader),
                              StringComparison.OrdinalIgnoreCase))
            { headerRow = row.Key; break; }
        if (headerRow < 0) return null;

        var table   = new Table { SheetName = sheet.Name };
        var columns = new Dictionary<int, string>();
        foreach (var cell in sheet.Cells[headerRow].OrderBy(c => c.Key))
        {
            if (string.IsNullOrWhiteSpace(cell.Value)) continue;
            string h = NormaliseHeader(cell.Value);
            columns[cell.Key] = h;
            table.Headers.Add(h);
        }

        foreach (var row in sheet.Cells.Where(r => r.Key > headerRow))
        {
            var record = new Row { SheetName = sheet.Name, ExcelRow = row.Key + 1 };
            bool any = false;
            foreach (var cell in row.Value)
            {
                if (!columns.TryGetValue(cell.Key, out var h)) continue;
                record.Values[h] = cell.Value;
                any |= !string.IsNullOrWhiteSpace(cell.Value);
            }
            if (any) table.Rows.Add(record);
        }
        return table;
    }

    /// <summary>Resolves a defined name that points at a single cell (e.g. MaxLevel).</summary>
    public string GetDefinedValue(string name)
    {
        if (!DefinedNames.TryGetValue(name, out var reference)) return null;
        int bang = reference.LastIndexOf('!');
        if (bang < 0) return null;

        string sheetName = reference.Substring(0, bang).Trim('\'').Replace("''", "'");
        string address   = reference.Substring(bang + 1).Replace("$", "");
        var sheet = GetSheet(sheetName);
        if (sheet == null || !TryParseAddress(address, out int row, out int col)) return null;
        return sheet.Get(row, col);
    }

    // ── Parsing ────────────────────────────────────────────────────────

    private void Read(ZipArchive zip)
    {
        var sharedStrings = new List<string>();
        var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            var doc = LoadXml(ssEntry);
            foreach (var si in doc.Root.Elements().Where(e => e.Name.LocalName == "si"))
                sharedStrings.Add(TextOf(si));
        }

        var rels = new Dictionary<string, string>();
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relsEntry != null)
            foreach (var rel in LoadXml(relsEntry).Root.Elements())
                rels[(string)rel.Attribute("Id")] = (string)rel.Attribute("Target");

        var workbook = LoadXml(zip.GetEntry("xl/workbook.xml") ??
                               throw new InvalidDataException("Not an .xlsx workbook (no xl/workbook.xml)."));

        foreach (var dn in workbook.Descendants().Where(e => e.Name.LocalName == "definedName"))
            if (dn.Attribute("localSheetId") == null)
                DefinedNames[(string)dn.Attribute("name")] = dn.Value;

        foreach (var s in workbook.Descendants().Where(e => e.Name.LocalName == "sheet"))
        {
            string relId = s.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
            if (relId == null || !rels.TryGetValue(relId, out var target)) continue;

            string entryPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
            var entry = zip.GetEntry(entryPath);
            if (entry == null) continue; // chart sheets etc.

            var sheet = new Sheet { Name = (string)s.Attribute("name") };
            ReadSheet(LoadXml(entry), sheet, sharedStrings);
            Sheets.Add(sheet);
        }
    }

    private static void ReadSheet(XDocument doc, Sheet sheet, List<string> sharedStrings)
    {
        var sheetData = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData == null) return;

        int nextRow = 0;
        foreach (var rowEl in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
        {
            int rowIndex = int.TryParse((string)rowEl.Attribute("r"), out int r) ? r - 1 : nextRow;
            nextRow = rowIndex + 1;

            int nextCol = 0;
            foreach (var c in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
            {
                int col = TryParseAddress((string)c.Attribute("r"), out _, out int cc) ? cc : nextCol;
                nextCol = col + 1;

                string value = CellValue(c, sharedStrings);
                if (value == null) continue;

                if (!sheet.Cells.TryGetValue(rowIndex, out var cells))
                    sheet.Cells[rowIndex] = cells = new Dictionary<int, string>();
                cells[col] = value;
            }
        }
    }

    private static string CellValue(XElement c, List<string> sharedStrings)
    {
        string type = (string)c.Attribute("t");
        string v    = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;

        switch (type)
        {
            case "s":
                return int.TryParse(v, out int i) && i >= 0 && i < sharedStrings.Count ? sharedStrings[i] : null;
            case "inlineStr":
                var inline = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                return inline != null ? TextOf(inline) : null;
            case "b":
                return v == "1" ? "TRUE" : v == "0" ? "FALSE" : null;
            case "e":
                return null; // #N/A, #DIV/0! etc. — treat as blank
            default: // "n", "str" (formula string), "d" (ISO date) or absent
                return v;
        }
    }

    /// <summary>Concatenated text of a shared/inline string, including rich-text runs but not phonetic hints.</summary>
    private static string TextOf(XElement si) =>
        string.Concat(si.Descendants()
            .Where(e => e.Name.LocalName == "t" && e.Ancestors().All(a => a.Name.LocalName != "rPh"))
            .Select(e => e.Value));

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    /// <summary>"B20" → row 19, col 1 (both 0-based).</summary>
    public static bool TryParseAddress(string address, out int row, out int col)
    {
        row = col = -1;
        if (string.IsNullOrEmpty(address)) return false;

        int i = 0, c = 0;
        while (i < address.Length && char.IsLetter(address[i]))
            c = c * 26 + (char.ToUpperInvariant(address[i++]) - 'A' + 1);
        if (c == 0 || !int.TryParse(address.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out int r))
            return false;

        row = r - 1;
        col = c - 1;
        return true;
    }
}
