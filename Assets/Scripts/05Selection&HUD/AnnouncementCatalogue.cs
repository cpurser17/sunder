using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every announcement the game can give — generated from the Announcements
/// workbook by Sunder > Import Announcements (AnnouncementImporter), never
/// edited by hand. Lives in a Resources folder, so the Announcer finds it on
/// its own.
///
/// Each announcement has one or more lines (variants). When it's given, one
/// line is picked at random by weight — e.g. two everyday takes at 49.5
/// each and a rare one at 1 (a 1-in-100 chance). Each line has its text and
/// audio per language; anything missing in the current language falls back
/// to the default language.
/// </summary>
[CreateAssetMenu(menuName = "Sunder/Announcement Catalogue", fileName = "AnnouncementCatalogue")]
public class AnnouncementCatalogue : ScriptableObject
{
    /// <summary>Resources path the Announcer loads from when none is assigned.</summary>
    public const string ResourcePath = "Announcements/AnnouncementCatalogue";

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
    public class Line
    {
        public string variant = "1";
        [Tooltip("Relative chance of this line being picked among the announcement's lines.")]
        public float  weight  = 1f;
        public List<LocalText> texts = new();
        public List<LocalClip> clips = new();

        public string TextFor(string language, string fallback)
        {
            string any = null;
            foreach (var t in texts)
            {
                if (t == null || string.IsNullOrEmpty(t.text)) continue;
                if (t.language == language) return t.text;
                if (t.language == fallback || any == null) any = t.text;
            }
            return any;
        }

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
    public class Announcement
    {
        public string id;
        public Announcer.Priority priority = Announcer.Priority.Normal;
        [Tooltip("Seconds before this announcement can be given to the same player again.")]
        public float cooldown = 30f;
        public List<Line> lines = new();

        /// <summary>One line picked by weight (equal chances if no weights are set).</summary>
        public Line Pick(float random01)
        {
            if (lines.Count == 0) return null;
            float total = 0f;
            foreach (var l in lines) total += Mathf.Max(0f, l.weight);
            if (total <= 0f) return lines[Mathf.Min(lines.Count - 1, (int)(random01 * lines.Count))];

            float roll = random01 * total;
            foreach (var l in lines)
            {
                roll -= Mathf.Max(0f, l.weight);
                if (roll < 0f) return l;
            }
            return lines[lines.Count - 1];
        }
    }

    [Tooltip("Language used when the chosen one has no text or audio for a line.")]
    public string defaultLanguage = "en";
    public List<Announcement> announcements = new();
}
