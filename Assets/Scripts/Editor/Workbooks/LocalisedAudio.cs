using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Finds recordings in language folders for the importers (announcer and
/// campaign narration):
///
///     &lt;root&gt;/&lt;language&gt;/&lt;name&gt;.wav           e.g. en/04U_M20_Intro.wav
///     &lt;root&gt;/&lt;language&gt;/&lt;name&gt;_&lt;language&gt;.wav  e.g. en/04U_M20_Intro_en.wav
///
/// Either form works — the folder says the language; the suffix is optional,
/// to keep files identifiable outside their folder. A suffix naming another
/// language folder (fr/…_en.wav) is reported and the file skipped, as is a
/// second recording of the same line in one folder.
/// </summary>
public static class LocalisedAudio
{
    /// <summary>Language folder → recording name (without any _&lt;language&gt; suffix) → clip.</summary>
    public static Dictionary<string, Dictionary<string, AudioClip>> Index(string root, List<string> warnings)
    {
        var result = new Dictionary<string, Dictionary<string, AudioClip>>(StringComparer.OrdinalIgnoreCase);
        if (!AssetDatabase.IsValidFolder(root)) return result;

        var languages = new List<string>();
        foreach (var folder in AssetDatabase.GetSubFolders(root))
            languages.Add(Path.GetFileName(folder).ToLowerInvariant());

        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { root }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string rel  = path.Substring(root.Length).TrimStart('/');
            int slash   = rel.IndexOf('/');
            if (slash <= 0)
            {
                warnings?.Add($"{path}: not in a language folder (e.g. {root}/en/) — skipped.");
                continue;
            }

            string lang = rel.Substring(0, slash).ToLowerInvariant();
            string name = Path.GetFileNameWithoutExtension(path);

            string own = "_" + lang;
            if (name.Length > own.Length && name.EndsWith(own, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - own.Length);
            else
            {
                string other = languages.Find(l => l != lang && name.Length > l.Length + 1 &&
                                                   name.EndsWith("_" + l, StringComparison.OrdinalIgnoreCase));
                if (other != null)
                {
                    warnings?.Add($"{path}: named for '{other}' but in the '{lang}' folder — skipped.");
                    continue;
                }
            }

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            if (!result.TryGetValue(lang, out var byName))
                result[lang] = byName = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            if (byName.ContainsKey(name))
            {
                warnings?.Add($"{path}: '{lang}' already has a recording for {name} — skipped (one per line).");
                continue;
            }
            byName[name] = clip;
        }
        return result;
    }
}
