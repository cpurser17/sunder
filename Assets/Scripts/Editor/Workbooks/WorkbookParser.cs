using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Shared cell reading for the data-workbook importers (MinionData, RoomData).
/// Every helper falls back to a default rather than failing, and records a
/// warning naming the sheet and row — so one typo never blocks a whole import.
/// </summary>
public abstract class WorkbookParser
{
    public readonly List<string> Warnings = new();

    protected void Warn(XlsxReader.Row row, string message) =>
        Warnings.Add(row != null ? $"{row.SheetName} row {row.ExcelRow}: {message}" : message);

    // ── Cell parsing helpers ───────────────────────────────────────────

    protected float Float(XlsxReader.Row row, string header, float fallback)
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryFloat(text, out float v)) return v;
        Warn(row, $"{header} '{text}' isn't a number — using {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    protected int Int(XlsxReader.Row row, string header, int fallback)
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryFloat(text, out float v) && Math.Abs(v - Math.Round(v)) < 1e-4) return (int)Math.Round(v);
        Warn(row, $"{header} '{text}' isn't a whole number — using {fallback}.");
        return fallback;
    }

    protected bool Bool(XlsxReader.Row row, string header, bool fallback)
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryBool(text, out bool v)) return v;
        Warn(row, $"{header} '{text}' isn't TRUE or FALSE — using {fallback.ToString().ToUpperInvariant()}.");
        return fallback;
    }

    protected T ParseEnum<T>(XlsxReader.Row row, string header, T fallback) where T : struct, Enum
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryEnum(text, out T v)) return v;
        Warn(row, $"{header} '{text}' isn't one of: {string.Join(", ", System.Enum.GetNames(typeof(T)))} — using {fallback}.");
        return fallback;
    }

    protected static bool TryEnum<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        // Names only — Enum.TryParse would also accept "3" or "1,2".
        string name = System.Enum.GetNames(typeof(T)).FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        return name != null && System.Enum.TryParse(name, out value);
    }

    protected static bool TryFloat(string text, out float value) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    protected static bool TryBool(string text, out bool value)
    {
        switch (text.Trim().ToUpperInvariant())
        {
            case "TRUE": case "YES": case "Y": case "1": value = true;  return true;
            case "FALSE": case "NO": case "N": case "0": value = false; return true;
            default: value = false; return false;
        }
    }

    protected static List<string> SplitList(string text) =>
        text == null ? new List<string>()
                     : text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
}
