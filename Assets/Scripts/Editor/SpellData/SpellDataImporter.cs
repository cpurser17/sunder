using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sunder > Import Spell Data: reads the SpellData workbook and rebuilds the
/// SpellCatalogue in Resources, where SpellSystem finds it with no wiring.
/// Re-import after editing the workbook.
/// </summary>
public static class SpellDataImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/SpellData.xlsx";
    private const string OutputPath          = "Assets/Resources/" + SpellCatalogue.ResourcePath + ".asset";

    [MenuItem("Sunder/Import Spell Data", priority = 13)]
    public static void ImportFromMenu()
    {
        if (!File.Exists(DefaultWorkbookPath))
        {
            EditorUtility.DisplayDialog("Spell Data", $"No workbook at {DefaultWorkbookPath}.", "OK");
            return;
        }
        Import(DefaultWorkbookPath);
    }

    public static void Import(string workbookPath)
    {
        XlsxReader book;
        try { book = XlsxReader.Load(workbookPath); }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Spell Data", $"Couldn't read {workbookPath}:\n\n{e.Message}", "OK");
            return;
        }

        var data      = SpellWorkbookParser.Parse(book);
        var warnings  = new List<string>(data.Warnings);
        var catalogue = LoadOrCreateCatalogue();
        catalogue.spells.Clear();
        catalogue.spells.AddRange(data.Spells);

        if (!data.Spells.Exists(s => string.Equals(s.id, "SummonWorker", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("No SummonWorker row — players can't summon workers.");

        EditorUtility.SetDirty(catalogue);
        AssetDatabase.SaveAssets();
        Report(workbookPath, data.Spells.Count, warnings);
    }

    private static SpellCatalogue LoadOrCreateCatalogue()
    {
        var existing = AssetDatabase.LoadAssetAtPath<SpellCatalogue>(OutputPath);
        if (existing != null) return existing;

        EnsureFolder(Path.GetDirectoryName(OutputPath).Replace('\\', '/'));
        var created = ScriptableObject.CreateInstance<SpellCatalogue>();
        AssetDatabase.CreateAsset(created, OutputPath);
        return created;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static void Report(string path, int spells, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Spell Data] Imported {path}");
        sb.AppendLine($"  {spells} spell(s)");
        if (warnings.Count > 0)
        {
            sb.AppendLine($"  {warnings.Count} warning(s):");
            foreach (var w in warnings) sb.AppendLine($"    {w}");
            Debug.LogWarning(sb.ToString());
        }
        else Debug.Log(sb.ToString());

        EditorUtility.DisplayDialog("Spell Data",
            $"Spells: {spells}\nWarnings: {warnings.Count}\n\nFull details are in the Console.", "OK");
    }
}
