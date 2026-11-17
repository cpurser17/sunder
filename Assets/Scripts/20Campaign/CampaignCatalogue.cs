using System.Collections.Generic;
using UnityEngine;

/// <summary>Which narration a line is: when the mission is chosen, after a win, or after a loss.</summary>
public enum MissionMoment { Intro, Outro, Defeat }

/// <summary>A campaign's main line of missions, or an optional challenge.</summary>
public enum MissionKind { Story, Challenge }

/// <summary>
/// Every Underworld campaign, its missions and their narration (endonyms) —
/// generated from the Campaigns workbook by Sunder > Import Campaigns
/// (CampaignImporter), never edited by hand. Lives in a Resources folder, so
/// the Campaign scene finds it with Load() and no wiring.
///
/// Text and audio are per language; anything missing in the language asked
/// for falls back to defaultLanguage (the workbook's first language).
/// </summary>
[CreateAssetMenu(menuName = "Sunder/Campaign Catalogue", fileName = "CampaignCatalogue")]
public class CampaignCatalogue : ScriptableObject
{
    /// <summary>Resources path the catalogue is loaded from.</summary>
    public const string ResourcePath = "Campaigns/CampaignCatalogue";

    [System.Serializable]
    public class LocalText
    {
        public string language;
        [TextArea] public string text;
    }

    [System.Serializable]
    public class LocalClip
    {
        public string    language;
        public AudioClip clip;
    }

    [System.Serializable]
    public class NarrationLine
    {
        public MissionMoment   moment;
        [Tooltip("The narration, per language. Shown as subtitles a sentence at a time; | forces a break.")]
        public List<LocalText> dialogue = new();
        [Tooltip("Optional subtitle start times in seconds, per language, e.g. \"0, 6.5, 12.2\".")]
        public List<LocalText> cues     = new();
        public List<LocalClip> clips    = new();

        public string    DialogueFor(string language, string fallback) => TextIn(dialogue, language, fallback);
        public string    CuesFor(string language, string fallback)     => TextIn(cues, language, fallback);
        public AudioClip ClipFor(string language, string fallback)
        {
            AudioClip backup = null;
            foreach (var c in clips)
            {
                if (c == null || c.clip == null) continue;
                if (c.language == language) return c.clip;
                if (c.language == fallback) backup = c.clip;
            }
            return backup;
        }
    }

    [System.Serializable]
    public class Mission
    {
        public string      id;
        public MissionKind kind = MissionKind.Story;
        [Tooltip("The level file the mission plays.")]
        public string      levelId;
        [Tooltip("Unlock rule (see UnlockRule). Blank = available as soon as the campaign is.")]
        public string      requires;
        [Tooltip("The place's name before the mission, per language (PreName_<lang>).")]
        public List<LocalText> preNames  = new();
        [Tooltip("Its name once conquered, per language (PostName_<lang>). Blank = it keeps its pre-mission name.")]
        public List<LocalText> postNames = new();
        public List<NarrationLine> lines = new();

        public NarrationLine Line(MissionMoment moment)
        {
            foreach (var l in lines) if (l != null && l.moment == moment) return l;
            return null;
        }

        /// <summary>The mission's name before it's won: its PreName, else its id.</summary>
        public string NameFor(string language, string fallback) =>
            TextIn(preNames, language, fallback) is { Length: > 0 } name ? name : id;

        /// <summary>What the place is called once conquered: its PostName, else its PreName.</summary>
        public string ConqueredNameFor(string language, string fallback) =>
            TextIn(postNames, language, fallback) is { Length: > 0 } name ? name : NameFor(language, fallback);
    }

    [System.Serializable]
    public class Campaign
    {
        [Tooltip("01U … 26U — the Underworld faction's id.")]
        public string id;
        public int    order;
        [Tooltip("Unlock rule for the campaign itself. Blank = available from the start.")]
        public string requires;
        public List<LocalText> names    = new();
        public List<Mission>   missions = new();

        public string NameFor(string language, string fallback) =>
            TextIn(names, language, fallback) is { Length: > 0 } name ? name : id;

        public Mission Find(string missionId)
        {
            foreach (var m in missions)
                if (m != null && string.Equals(m.id, missionId, System.StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
    }

    public string         defaultLanguage = "en";
    public List<Campaign> campaigns       = new();

    public Campaign Find(string campaignId)
    {
        foreach (var c in campaigns)
            if (c != null && string.Equals(c.id, campaignId, System.StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    private static CampaignCatalogue _loaded;

    /// <summary>The imported catalogue from Resources, or null if Sunder > Import Campaigns hasn't been run.</summary>
    public static CampaignCatalogue Load()
    {
        if (_loaded == null) _loaded = Resources.Load<CampaignCatalogue>(ResourcePath);
        return _loaded;
    }

    /// <summary>The text in the language asked for, else the fallback language, else any.</summary>
    private static string TextIn(List<LocalText> texts, string language, string fallback)
    {
        string any = null, backup = null;
        foreach (var t in texts)
        {
            if (t == null || string.IsNullOrEmpty(t.text)) continue;
            if (t.language == language) return t.text;
            if (t.language == fallback) backup = t.text;
            any ??= t.text;
        }
        return backup ?? any;
    }
}
