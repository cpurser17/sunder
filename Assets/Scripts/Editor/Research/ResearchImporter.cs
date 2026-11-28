using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sunder > Import Research: reads the Research workbook and rebuilds the
/// ResearchCatalogue in Resources, where the game finds it with no wiring.
///
/// Icons are matched by name from
///
///     Assets/Art/Research/&lt;Icon&gt;.png   (any image format)
///
/// — imported as sprites automatically if they aren't already. A node with
/// no icon yet shows a placeholder in the game.
///
/// Every faction's web is put together and checked: links, Replaces,
/// effects (that each room, spell, minion, trap or door named exists, and
/// each stat can be changed), Requires rules, nodes no Start node leads to,
/// and nodes drawn on top of each other. Re-import after editing the
/// workbook or adding icons.
/// </summary>
public static class ResearchImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/Research.xlsx";
    private const string IconRoot            = "Assets/Art/Research";
    private const string OutputPath          = "Assets/Resources/" + ResearchCatalogue.ResourcePath + ".asset";

    /// <summary>Closer than this (in rings) and two nodes overlap on screen.</summary>
    private const float MinSpacing = 0.35f;

    [MenuItem("Sunder/Import Research", priority = 15)]
    public static void ImportFromMenu()
    {
        if (!File.Exists(DefaultWorkbookPath))
        {
            EditorUtility.DisplayDialog("Research", $"No workbook at {DefaultWorkbookPath}.", "OK");
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
            EditorUtility.DisplayDialog("Research", $"Couldn't read {workbookPath}:\n\n{e.Message}", "OK");
            return;
        }

        var data     = ResearchWorkbookParser.Parse(book);
        var warnings = new List<string>(data.Warnings);

        var catalogue = LoadOrCreateCatalogue();
        catalogue.defaultLanguage = data.Languages.Count > 0 ? data.Languages[0] : "en";
        catalogue.nodes.Clear();
        catalogue.nodes.AddRange(data.Nodes);

        int missingIcons = LinkIcons(catalogue, warnings);
        int factions = CheckWebs(catalogue, warnings);

        EditorUtility.SetDirty(catalogue);
        AssetDatabase.SaveAssets();
        Report(workbookPath, catalogue.nodes.Count, factions, missingIcons, warnings);
    }

    // ── Icons ──────────────────────────────────────────────────────────

    private static int LinkIcons(ResearchCatalogue catalogue, List<string> warnings)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AssetDatabase.IsValidFolder(IconRoot))
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { IconRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                byName[Path.GetFileNameWithoutExtension(path)] = path;
            }

        int missing = 0;
        var missingNames = new List<string>();
        foreach (var node in catalogue.nodes)
        {
            node.icon = null;
            if (string.IsNullOrEmpty(node.iconName)) { missing++; continue; }
            if (!byName.TryGetValue(node.iconName, out string path)) { missing++; missingNames.Add(node.iconName); continue; }
            node.icon = LoadSprite(path);
            if (node.icon == null) warnings.Add($"{node.id}: {path} couldn't be loaded as a sprite.");
        }
        if (missingNames.Count > 0)
            warnings.Add($"No image yet in {IconRoot}/ for: {string.Join(", ", missingNames.Distinct())} (placeholders shown).");
        return missing;
    }

    private static Sprite LoadSprite(string path)
    {
        var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite != null) return sprite;
        if (AssetImporter.GetAtPath(path) is TextureImporter importer && importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType      = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // ── Checks ─────────────────────────────────────────────────────────

    /// <summary>Builds and checks each faction's web. Returns how many factions were checked.</summary>
    private static int CheckWebs(ResearchCatalogue catalogue, List<string> warnings)
    {
        var registry = FindAsset<FactionRegistry>(warnings, "Faction Registry");
        var spells   = Resources.Load<SpellCatalogue>(SpellCatalogue.ResourcePath);
        var traps    = FindAsset<TrapDoorRegistry>(null, "Trap/Door Registry");

        var factionIds = new List<string>();
        if (registry != null)
            foreach (var def in registry.Definitions)
                if (def != null && !string.IsNullOrEmpty(def.factionContentId)) factionIds.Add(def.factionContentId);
        foreach (var node in catalogue.nodes)
            foreach (var f in node.factions)
                if (!factionIds.Contains(f, StringComparer.OrdinalIgnoreCase))
                {
                    if (registry != null) warnings.Add($"{node.id}: faction {f} isn't in the Faction Registry.");
                    factionIds.Add(f);
                }
        if (factionIds.Count == 0) factionIds.Add("");   // no registry: check the shared web alone

        var seen = new HashSet<string>();
        void Add(string w) { if (seen.Add(w)) warnings.Add(w); }

        foreach (var node in catalogue.nodes)
        {
            if (!string.IsNullOrEmpty(node.replaces) && catalogue.Find(node.replaces) == null)
                Add($"{node.id}: replaces {node.replaces}, which isn't a node.");
            if (!string.IsNullOrEmpty(node.replaces) && node.factions.Count == 0)
                Add($"{node.id}: replaces {node.replaces} for every faction — set Factions, or just edit {node.replaces}.");

            var rule = UnlockRule.Parse(node.requires);
            foreach (var keyword in rule.KeywordsNotFor(research: true))
                Add($"{node.id}: Requires uses {keyword}(…), which is about campaign progress — it never holds during a mission.");
            foreach (var id in rule.ResearchReferenced())
                if (catalogue.Find(id) == null) Add($"{node.id}: Requires names {id}, which isn't a node.");

            foreach (var text in node.effects)
            {
                var effect = ResearchEffect.Parse(text, out _);
                if (effect != null) CheckEffect(node, effect, registry, spells, traps, Add);
            }
        }

        foreach (var faction in factionIds)
        {
            var webWarnings = new List<string>();
            var web = ResearchWeb.Build(catalogue, faction, webWarnings);
            foreach (var w in webWarnings) Add(w);
            if (web.Nodes.Count == 0) continue;
            string who = faction.Length > 0 ? faction : "The web";

            if (!web.Nodes.Exists(n => n.Start))
                Add($"{who}: no Start node, so nothing can be researched.");
            else
            {
                var unreachable = web.Unreachable();
                if (unreachable.Count > 0)
                    Add($"{who}: no route from a Start node to {string.Join(", ", unreachable.Select(n => n.Id))} — " +
                        "link them in, or they can never be researched.");
            }

            for (int i = 0; i < web.Nodes.Count; i++)
                for (int j = i + 1; j < web.Nodes.Count; j++)
                    if (Vector2.Distance(Place(web.Nodes[i]), Place(web.Nodes[j])) < MinSpacing)
                        Add($"{who}: {web.Nodes[i].Id} and {web.Nodes[j].Id} sit on top of each other — move one (Angle/Radius).");
        }
        return factionIds.Count(f => f.Length > 0);
    }

    private static Vector2 Place(ResearchWeb.WebNode n)
    {
        float a = n.Angle * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * n.Radius;
    }

    private static void CheckEffect(ResearchCatalogue.Node node, ResearchEffect e, FactionRegistry registry,
                                    SpellCatalogue spells, TrapDoorRegistry traps, Action<string> warn)
    {
        UnlockKind kind; string id;
        if (e.isUnlock) { kind = e.unlockKind; id = e.unlockId; }
        else if (e.targetId == "*" || e.target == ModifierTarget.Faction) return;
        else { kind = e.target == ModifierTarget.Spell ? UnlockKind.Spell : e.target == ModifierTarget.Room ? UnlockKind.Room : UnlockKind.Minion; id = e.targetId; }

        string problem = kind switch
        {
            UnlockKind.Room   => Enum.TryParse(id, true, out TileType _) ? null : $"there's no room type {id}",
            UnlockKind.Spell  => spells == null ? null : spells.spells.Exists(s => string.Equals(s.id, id, StringComparison.OrdinalIgnoreCase)) ? null : $"there's no spell {id} in the SpellData import",
            UnlockKind.Minion => registry == null ? null : AnyFactionHasMinion(registry, node, id) ? null : $"no faction with this node has a minion {id}",
            UnlockKind.Trap   => traps == null ? null : traps.GetDefinition(TrapDoorKind.Trap, id) != null ? null : $"there's no trap {id}",
            UnlockKind.Door   => traps == null ? null : traps.GetDefinition(TrapDoorKind.Door, id) != null ? null : $"there's no door {id}",
            _ => null,
        };
        if (problem != null) warn($"{node.id}: {e} — {problem}.");
    }

    private static bool AnyFactionHasMinion(FactionRegistry registry, ResearchCatalogue.Node node, string minionId)
    {
        foreach (var def in registry.Definitions)
            if (def != null && node.IsFor(def.factionContentId) && def.FindMinion(minionId) != null) return true;
        return false;
    }

    private static T FindAsset<T>(List<string> warnings, string label) where T : UnityEngine.Object
    {
        var guids = AssetDatabase.FindAssets("t:" + typeof(T).Name);
        if (guids.Length == 0) { warnings?.Add($"No {label} asset found — those names weren't checked."); return null; }
        return AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    // ── Asset ──────────────────────────────────────────────────────────

    private static ResearchCatalogue LoadOrCreateCatalogue()
    {
        var existing = AssetDatabase.LoadAssetAtPath<ResearchCatalogue>(OutputPath);
        if (existing != null) return existing;

        EnsureFolder(Path.GetDirectoryName(OutputPath).Replace('\\', '/'));
        var created = ScriptableObject.CreateInstance<ResearchCatalogue>();
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

    private static void Report(string path, int nodes, int factions, int missingIcons, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Research] Imported {path}");
        sb.AppendLine($"  {nodes} node(s), checked for {factions} faction(s); {missingIcons} without an icon yet");
        if (warnings.Count > 0)
        {
            sb.AppendLine($"  {warnings.Count} warning(s):");
            foreach (var w in warnings) sb.AppendLine($"    {w}");
            Debug.LogWarning(sb.ToString());
        }
        else Debug.Log(sb.ToString());

        EditorUtility.DisplayDialog("Research",
            $"Nodes: {nodes}\nFactions checked: {factions}\nWithout an icon yet: {missingIcons}\nWarnings: {warnings.Count}\n\n" +
            "Full details are in the Console.", "OK");
    }
}
