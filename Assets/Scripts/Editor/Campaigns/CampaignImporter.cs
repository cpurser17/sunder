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
/// recording yet just shows its subtitles.
///
/// Each mission's level is linked from
///
///     Assets/Levels/Campaigns/&lt;CampaignID&gt;/&lt;LevelId&gt;.json
///
/// — a level saved in the level editor, renamed and moved there. Linked
/// levels are packed with the game, and the campaign only ever plays these
/// (never a custom level of the same id). The importer checks each level's
/// own id and that its human player plays the campaign's faction.
///
/// Re-import after adding levels or recordings, or editing the workbook.
/// </summary>
public static class CampaignImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/Campaigns.xlsx";
    private const string AudioRoot           = "Assets/Audio/Endonymy";
    private const string LevelsRoot          = "Assets/Levels/Campaigns";
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
                mission.level = LinkLevel(campaign, mission, warnings);
                if (mission.level == null) noLevel++;

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

        WarnAboutStrayLevels(data, warnings);

        var catalogue = LoadOrCreateCatalogue();
        catalogue.defaultLanguage = data.Languages.Count > 0 ? data.Languages[0] : "en";
        catalogue.campaigns.Clear();
        catalogue.campaigns.AddRange(data.Campaigns);
        catalogue.factionRegistry = FindFactionRegistry(warnings);
        catalogue.ForgetLookups();

        EditorUtility.SetDirty(catalogue);
        AssetDatabase.SaveAssets();
        Report(workbookPath, data.Campaigns.Count, missions, lines, recorded, unrecorded, noLevel, warnings);
    }

    private static FactionRegistry FindFactionRegistry(List<string> warnings)
    {
        var guids = AssetDatabase.FindAssets("t:FactionRegistry");
        if (guids.Length == 0) { warnings.Add("No Faction Registry asset found — the New Game screen can't name factions."); return null; }
        if (guids.Length > 1) warnings.Add($"{guids.Length} Faction Registry assets — using {AssetDatabase.GUIDToAssetPath(guids[0])}.");
        return AssetDatabase.LoadAssetAtPath<FactionRegistry>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    /// <summary>The mission's level file, checked: its own id, and that its human player plays this campaign's faction.</summary>
    private static TextAsset LinkLevel(CampaignCatalogue.Campaign campaign, CampaignCatalogue.Mission mission, List<string> warnings)
    {
        string path  = $"{LevelsRoot}/{campaign.id}/{mission.levelId}.json";
        var    asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        if (asset == null) return null;

        var level = SaveLoadSystem.ParseLevel(asset.text, mission.levelId);
        if (level == null)
        {
            warnings.Add($"{campaign.id} {mission.id}: {path} isn't a level file the game can read — not linked.");
            return null;
        }
        if (!string.Equals(level.levelId, mission.levelId, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"{campaign.id} {mission.id}: {path} was saved as '{level.levelId}' — fine to play, but its id inside " +
                         $"doesn't match its name (open it in the level editor and set its id to {mission.levelId}).");

        var human = level.factions?.Find(f => f != null && f.isHuman);
        if (human == null)
            warnings.Add($"{campaign.id} {mission.id}: {path} has no human player.");
        else if (!string.Equals(human.contentFactionId, campaign.id, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"{campaign.id} {mission.id}: the human player in {path} plays " +
                         $"'{(string.IsNullOrEmpty(human.contentFactionId) ? "no faction" : human.contentFactionId)}', not {campaign.id}.");
        return asset;
    }

    /// <summary>Level files in the campaign folders that no mission uses — usually a misnamed or misplaced file.</summary>
    private static void WarnAboutStrayLevels(CampaignWorkbookParser data, List<string> warnings)
    {
        if (!AssetDatabase.IsValidFolder(LevelsRoot)) return;
        foreach (var guid in AssetDatabase.FindAssets("t:TextAsset", new[] { LevelsRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string folder = Path.GetFileName(Path.GetDirectoryName(path));
            string id     = Path.GetFileNameWithoutExtension(path);
            var campaign  = data.Campaigns.Find(c => string.Equals(c.id, folder, StringComparison.OrdinalIgnoreCase));
            if (campaign == null)
                warnings.Add($"{path}: not in a campaign's folder (Assets/Levels/Campaigns/<CampaignID>/) — not used.");
            else if (!campaign.missions.Exists(m => string.Equals(m.levelId, id, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"{path}: no {campaign.id} mission has LevelId '{id}' — not used.");
        }
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
        sb.AppendLine($"  {missions - noLevel} level(s) linked; {noLevel} mission(s) with no level in {LevelsRoot}/<CampaignID>/ yet");
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
            $"Levels linked: {missions - noLevel}  (missions without one yet: {noLevel})\nWarnings: {warnings.Count}\n\n" +
            "Full details are in the Console.", "OK");
    }
}
