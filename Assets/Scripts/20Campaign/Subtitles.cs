using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Turns a line of narration into subtitles, a sentence at a time, timed to
/// its recording.
///
/// Splitting: after . ! ? or … (and any closing quote or bracket) followed by
/// a space, and wherever the text has a |, which is removed. Timing: the
/// Cues column's start times if it gives one per subtitle; otherwise each
/// subtitle gets a share of the recording in proportion to its length.
/// With no recording, the line lasts as long as it takes to read.
/// </summary>
public static class Subtitles
{
    public struct Cue
    {
        public string Text;
        public float  Start, End;
    }

    /// <summary>Characters read per second when there's no recording to time against.</summary>
    public const float ReadingCharsPerSecond = 15f;
    /// <summary>Shortest time a subtitle stays up with no recording.</summary>
    public const float MinSecondsUnrecorded = 1.5f;

    /// <summary>The dialogue's subtitles, in order.</summary>
    public static List<string> Split(string dialogue)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(dialogue)) return result;

        foreach (var part in dialogue.Split('|'))
        {
            var sb = new StringBuilder();
            for (int i = 0; i < part.Length; i++)
            {
                char c = part[i];
                sb.Append(c);
                if (c != '.' && c != '!' && c != '?' && c != '…') continue;

                // Keep closing quotes/brackets, and further ! ? . with the sentence.
                while (i + 1 < part.Length && "\"'”’)]!?.…".IndexOf(part[i + 1]) >= 0) sb.Append(part[++i]);
                if (i + 1 < part.Length && !char.IsWhiteSpace(part[i + 1])) continue;   // e.g. 3.5 or "e.g."

                Add(result, sb);
            }
            Add(result, sb);
        }
        return result;
    }

    private static void Add(List<string> list, StringBuilder sb)
    {
        string s = sb.ToString().Trim();
        if (s.Length > 0) list.Add(s);
        sb.Clear();
    }

    /// <summary>Start times from a Cues cell ("0, 6.5, 12.2"), or null if blank or not all numbers in order.</summary>
    public static List<float> ParseCues(string cues)
    {
        if (string.IsNullOrWhiteSpace(cues)) return null;
        var list = new List<float>();
        foreach (var raw in cues.Split(',', ';'))
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) || t < 0f) return null;
            if (list.Count > 0 && t < list[list.Count - 1]) return null;
            list.Add(t);
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Times the dialogue's subtitles. clipSeconds is the recording's length
    /// (0 if none); cues the Cues cell. Cues are used only when there's one
    /// per subtitle.
    /// </summary>
    public static List<Cue> Time(string dialogue, float clipSeconds, string cues)
    {
        var sentences = Split(dialogue);
        var timed     = new List<Cue>(sentences.Count);
        if (sentences.Count == 0) return timed;

        var starts = ParseCues(cues);
        if (starts != null && starts.Count == sentences.Count)
        {
            float end = clipSeconds > starts[starts.Count - 1]
                ? clipSeconds
                : starts[starts.Count - 1] + ReadingSeconds(sentences[sentences.Count - 1]);
            for (int i = 0; i < sentences.Count; i++)
                timed.Add(new Cue { Text = sentences[i], Start = starts[i], End = i + 1 < starts.Count ? starts[i + 1] : end });
            return timed;
        }

        if (clipSeconds > 0f)
        {
            int chars = 0;
            foreach (var s in sentences) chars += s.Length;
            float t = 0f;
            foreach (var s in sentences)
            {
                float share = clipSeconds * s.Length / chars;
                timed.Add(new Cue { Text = s, Start = t, End = t + share });
                t += share;
            }
            return timed;
        }

        float at = 0f;
        foreach (var s in sentences)
        {
            float d = ReadingSeconds(s);
            timed.Add(new Cue { Text = s, Start = at, End = at + d });
            at += d;
        }
        return timed;
    }

    public static float ReadingSeconds(string sentence) =>
        System.Math.Max(MinSecondsUnrecorded, sentence.Length / ReadingCharsPerSecond);
}
