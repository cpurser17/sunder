using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sunder > Import Announcements: reads the Announcements workbook and
/// rebuilds the AnnouncementCatalogue in Resources, where the Announcer
/// finds it with no wiring.
///
/// Audio is matched by name — put recordings at
///
///     Assets/Audio/Announcer/&lt;language&gt;/&lt;Id&gt;_&lt;Variant&gt;.wav   (any audio format)
///
/// e.g. Assets/Audio/Announcer/en/LairTooSmall_1.wav, LairTooSmall_2.wav,
/// LairTooSmall_Rare.ogg. A line with no recording yet just shows its text.
/// Re-import after adding recordings or editing the workbook.
/// </summary>
public static class AnnouncementImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/Announcements.xlsx";
    private const string AudioRoot           = "Assets/Audio/Announcer";
    private const string OutputPath          = "Assets/Resources/" + AnnouncementCatalogue.ResourcePath + ".asset";

    [MenuItem("Sunder/Import Announcements", priority = 12)]
    public static void ImportFromMenu()
    {
        if (!File.Exists(DefaultWorkbookPath))
        {
            EditorUtility.DisplayDialog("Announcements", $"No workbook at {DefaultWorkbookPath}.", "OK");
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
            EditorUtility.DisplayDialog("Announcements", $"Couldn't read {workbookPath}:\n\n{e.Message}", "OK");
            return;
        }

        var data     = AnnouncementWorkbookParser.Parse(book);
        var warnings = new List<string>(data.Warnings);
        var clips    = FindClips();

        var catalogue = LoadOrCreateCatalogue();
        catalogue.defaultLanguage = data.Languages.Count > 0 ? data.Languages[0] : "en";
        catalogue.announcements.Clear();

        int lines = 0, found = 0, missing = 0;
        foreach (var a in data.Announcements)
        {
            var entry = new AnnouncementCatalogue.Announcement { id = a.Id, priority = a.Priority, cooldown = a.Cooldown };
            foreach (var l in a.Lines)
            {
                var line = new AnnouncementCatalogue.Line { variant = l.Variant, weight = l.Weight };
                foreach (var pair in l.Texts)
                    line.texts.Add(new AnnouncementCatalogue.LocalText { language = pair.Key, text = pair.Value });

                foreach (var lang in data.Languages.Union(clips.Keys))
                {
                    if (clips.TryGetValue(lang, out var byName) && byName.TryGetValue($"{a.Id}_{l.Variant}", out var clip))
                    {
                        line.clips.Add(new AnnouncementCatalogue.LocalClip { language = lang, clip = clip });
                        found++;
                    }
                    else if (l.Texts.ContainsKey(lang)) missing++;
                }
                entry.lines.Add(line);
                lines++;
            }
            catalogue.announcements.Add(entry);
        }

        // Announcements the game gives that the workbook hasn't got yet.
        foreach (var (id, _, _, _) in Announcer.BuiltIn)
            if (!data.Announcements.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"{id} is used by the game but isn't in the workbook — it uses built-in placeholder text.");

        EditorUtility.SetDirty(catalogue);
        AssetDatabase.SaveAssets();
        Report(workbookPath, data.Announcements.Count, lines, found, missing, warnings);
    }

    /// <summary>Language folder → clip name → clip, from Assets/Audio/Announcer/&lt;language&gt;/.</summary>
    private static Dictionary<string, Dictionary<string, AudioClip>> FindClips()
    {
        var result = new Dictionary<string, Dictionary<string, AudioClip>>(StringComparer.OrdinalIgnoreCase);
        if (!AssetDatabase.IsValidFolder(AudioRoot)) return result;

        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { AudioRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string rel  = path.Substring(AudioRoot.Length).TrimStart('/');
            int slash   = rel.IndexOf('/');
            if (slash <= 0) continue;   // must sit in a language folder

            string lang = rel.Substring(0, slash).ToLowerInvariant();
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            if (!result.TryGetValue(lang, out var byName))
                result[lang] = byName = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            byName[Path.GetFileNameWithoutExtension(path)] = clip;
        }
        return result;
    }

    private static AnnouncementCatalogue LoadOrCreateCatalogue()
    {
        var existing = AssetDatabase.LoadAssetAtPath<AnnouncementCatalogue>(OutputPath);
        if (existing != null) return existing;

        EnsureFolder(Path.GetDirectoryName(OutputPath).Replace('\\', '/'));
        var created = ScriptableObject.CreateInstance<AnnouncementCatalogue>();
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

    private static void Report(string path, int announcements, int lines, int clips, int missing, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Announcements] Imported {path}");
        sb.AppendLine($"  {announcements} announcement(s), {lines} line(s); {clips} recording(s) found, {missing} not recorded yet");
        if (warnings.Count > 0)
        {
            sb.AppendLine($"  {warnings.Count} warning(s):");
            foreach (var w in warnings) sb.AppendLine($"    {w}");
            Debug.LogWarning(sb.ToString());
        }
        else Debug.Log(sb.ToString());

        EditorUtility.DisplayDialog("Announcements",
            $"Announcements: {announcements}\nLines: {lines}\nRecordings found: {clips}  (not recorded yet: {missing})\n" +
            $"Warnings: {warnings.Count}\n\nFull details are in the Console.", "OK");
    }
}
