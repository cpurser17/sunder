using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sunder > Import Campaigns: reads the Campaigns workbook and rebuilds the
/// CampaignCatalogue in Resources, where the Campaign scene finds it with no
/// wiring (CampaignCatalogue.Load).
///
/// Narration audio is matched by name — one recording per line:
///
///     Assets/Audio/Endonymy/&lt;language&gt;/&lt;CampaignID&gt;_&lt;MissionID&gt;_&lt;Moment&gt;.wav   (any audio format)
///
/// e.g. Assets/Audio/Endonymy/en/04U_M20_Intro.wav. A line with no
/// recording yet just shows its subtitles. Re-import after adding
/// recordings or editing the workbook.
/// </summary>
public static class CampaignImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/Campaigns.xlsx";
    private const string AudioRoot           = "Assets/Audio/Endonymy";
    private const string LevelsRoot          = "Assets/StreamingAssets/Levels";
    private const string OutputPath          = "Assets/Resources/" + CampaignCatalogue.ResourcePath + ".asset";

    [MenuItem("Sunder/Import Campaigns", priority = 14)]
    public static void ImportFromMenu()
    {
        if (!File.Exists(DefaultWorkbookPath))
        {
            EditorUtility.DisplayDialog("Campaigns", $"No workbook at {DefaultWorkbookPath}.", "OK");
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
            EditorUtility.DisplayDialog("Campaigns", $"Couldn't read {workbookPath}:\n\n{e.Message}", "OK");
            return;
        }

        var data     = CampaignWorkbookParser.Parse(book);
        var warnings = new List<string>(data.Warnings);
        var clips    = FindClips();

        int missions = 0, lines = 0, recorded = 0, unrecorded = 0, noLevel = 0;
        foreach (var campaign in data.Campaigns)
            foreach (var mission in campaign.missions)
            {
                missions++;
                if (!File.Exists(Path.Combine(LevelsRoot, mission.levelId + ".json"))) noLevel++;

                foreach (var line in mission.lines)
                {
                    lines++;
                    string clipName = $"{campaign.id}_{mission.id}_{line.moment}";
                    foreach (var lang in Languages(data, clips))
                    {
                        if (clips.TryGetValue(lang, out var byName) && byName.TryGetValue(clipName, out var clip))
                        {
                            line.clips.Add(new CampaignCatalogue.LocalClip { language = lang, clip = clip });
                            recorded++;
                        }
                        else if (line.dialogue.Exists(d => d.language == lang)) unrecorded++;
                    }
                }
            }

        var catalogue = LoadOrCreateCatalogue();
        catalogue.defaultLanguage = data.Languages.Count > 0 ? data.Languages[0] : "en";
        catalogue.campaigns.Clear();
        catalogue.campaigns.AddRange(data.Campaigns);

        EditorUtility.SetDirty(catalogue);
        AssetDatabase.SaveAssets();
        Report(workbookPath, data.Campaigns.Count, missions, lines, recorded, unrecorded, noLevel, warnings);
    }

    private static IEnumerable<string> Languages(CampaignWorkbookParser data, Dictionary<string, Dictionary<string, AudioClip>> clips)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in data.Languages) if (seen.Add(l)) yield return l;
        foreach (var l in clips.Keys)     if (seen.Add(l)) yield return l;
    }

    /// <summary>Language folder → clip name → clip, from Assets/Audio/Endonymy/&lt;language&gt;/.</summary>
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

    private static CampaignCatalogue LoadOrCreateCatalogue()
    {
        var existing = AssetDatabase.LoadAssetAtPath<CampaignCatalogue>(OutputPath);
        if (existing != null) return existing;

        EnsureFolder(Path.GetDirectoryName(OutputPath).Replace('\\', '/'));
        var created = ScriptableObject.CreateInstance<CampaignCatalogue>();
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

    private static void Report(string path, int campaigns, int missions, int lines, int recorded,
                               int unrecorded, int noLevel, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Campaigns] Imported {path}");
        sb.AppendLine($"  {campaigns} campaign(s), {missions} mission(s), {lines} narration line(s) written");
        sb.AppendLine($"  {recorded} recording(s) found, {unrecorded} line(s) not recorded yet");
        sb.AppendLine($"  {noLevel} mission(s) whose level file isn't in {LevelsRoot} yet");
        if (warnings.Count > 0)
        {
            sb.AppendLine($"  {warnings.Count} warning(s):");
            foreach (var w in warnings) sb.AppendLine($"    {w}");
            Debug.LogWarning(sb.ToString());
        }
        else Debug.Log(sb.ToString());

        EditorUtility.DisplayDialog("Campaigns",
            $"Campaigns: {campaigns}\nMissions: {missions}\nNarration lines: {lines}\n" +
            $"Recordings found: {recorded}  (not recorded yet: {unrecorded})\n" +
            $"Missions without a level file yet: {noLevel}\nWarnings: {warnings.Count}\n\n" +
            "Full details are in the Console.", "OK");
    }
}
