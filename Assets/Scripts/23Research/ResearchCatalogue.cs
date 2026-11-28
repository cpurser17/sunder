using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The research web — every node, for every faction — generated from the
/// Research workbook by Sunder > Import Research (ResearchImporter), never
/// edited by hand. Lives in a Resources folder, so the game finds it with
/// Load() and no wiring.
///
/// Nodes sit on a polar layout: Angle in degrees (0 = top, clockwise) and
/// Radius in rings (0 = the centre). A node can be researched once it's
/// linked to a node already researched (links work both ways), or if it's a
/// Start node, and its Requires rule holds. Factions share one web; a node
/// with Factions set is only in those factions' webs, and a node with
/// Replaces takes another node's place (and links) for its factions. See
/// ResearchWeb for how a faction's web is put together.
/// </summary>
[CreateAssetMenu(menuName = "Sunder/Research Catalogue", fileName = "ResearchCatalogue")]
public class ResearchCatalogue : ScriptableObject
{
    /// <summary>Resources path the catalogue is loaded from.</summary>
    public const string ResourcePath = "Research/ResearchCatalogue";

    [System.Serializable]
    public class LocalText
    {
        public string language;
        [TextArea] public string text;
    }

    [System.Serializable]
    public class Node
    {
        public string id;
        public List<LocalText> names        = new();
        public List<LocalText> descriptions = new();
        [Tooltip("Content faction ids that have this node, e.g. 04U. Empty = every faction.")]
        public List<string> factions = new();
        [Tooltip("For its factions, this node takes the place of that node (its position too, unless it has its own).")]
        public string replaces;
        [Tooltip("Open from the start of every mission, with nothing researched.")]
        public bool   start;
        [Tooltip("Neighbouring node ids. Links work both ways — listing one on either row is enough.")]
        public List<string> links = new();
        [Tooltip("Extra rule on top of the links (UnlockRule), e.g. Research(Alchemy) & Room(Library, 25). Blank = none.")]
        public string requires;
        [Tooltip("Research points to complete it.")]
        public float  cost = 100f;
        public bool   hasPosition = true;
        [Tooltip("Degrees, 0 = top, clockwise.")]
        public float  angle;
        [Tooltip("Rings from the centre. 0 = the centre.")]
        public float  radius;
        public string iconName;
        public Sprite icon;
        [Tooltip("What completing it does — see ResearchEffect, e.g. Unlock(Room, Workshop) or Stat(Minion:*, Health, +10%).")]
        public List<string> effects = new();
        [Tooltip("How keen the game is to pick it when the queue is empty. Higher = sooner. 0 = never picked automatically.")]
        public float  autoWeight = 1f;
        [Tooltip("Optional announcement id played when it's completed, instead of the general one.")]
        public string announcement;

        public bool IsFor(string contentFactionId)
        {
            if (factions == null || factions.Count == 0) return true;
            foreach (var f in factions)
                if (string.Equals(f, contentFactionId, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string NameFor(string language, string fallback) =>
            TextIn(names, language, fallback) is { Length: > 0 } name ? name : id;

        public string DescriptionFor(string language, string fallback) => TextIn(descriptions, language, fallback) ?? "";
    }

    public string     defaultLanguage = "en";
    public List<Node> nodes = new();

    public Node Find(string id)
    {
        foreach (var n in nodes)
            if (n != null && string.Equals(n.id, id, System.StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    private static ResearchCatalogue _loaded;

    /// <summary>The imported catalogue, or null if Sunder > Import Research hasn't been run.</summary>
    public static ResearchCatalogue Load()
    {
        if (_loaded == null) _loaded = Resources.Load<ResearchCatalogue>(ResourcePath);
        return _loaded;
    }

    private static string TextIn(List<LocalText> texts, string language, string fallback)
    {
        if (texts == null) return null;
        string backup = null;
        foreach (var t in texts)
        {
            if (t == null || string.IsNullOrEmpty(t.text)) continue;
            if (t.language == language) return t.text;
            if (t.language == fallback) backup = t.text;
        }
        return backup;
    }
}
