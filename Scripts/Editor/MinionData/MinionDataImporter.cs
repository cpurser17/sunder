using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sunder > Import Minion Data: reads the MinionData workbook and creates or
/// updates one MinionDefinition per faction per minion, one AbilityDefinition
/// per ability, and each FactionDefinition's roster.
///
/// Assets are matched by path (built from FactionID + MinionID / AbilityID)
/// and updated in place, so their GUIDs — and every reference to them —
/// survive re-imports. Everything on a generated asset is overwritten from
/// the workbook except MinionDefinition.prefab, the per-minion prefab
/// override, which is only ever set by hand.
///
/// Nothing is deleted: generated assets with no matching row any more are
/// listed in the report for you to remove once nothing uses them.
/// </summary>
public static class MinionDataImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/MinionData.xlsx";
    private const string WorkbookPathPref    = "Sunder.MinionDataImporter.WorkbookPath";
    private const string GeneratedRoot       = "Assets/Data/Generated";
    private const string MinionsRoot         = GeneratedRoot + "/Minions";
    private const string AbilitiesRoot       = GeneratedRoot + "/Abilities";
    private const string FactionsRoot        = GeneratedRoot + "/Factions";

    /// <summary>Token convention: Assets/Tokens/01U/Token_01U_C1.png</summary>
    private static string TokenPath(string faction, string minion) => $"Assets/Tokens/{faction}/Token_{faction}_{minion}.png";

    private static string MinionPath(string faction, string minion) => $"{MinionsRoot}/{faction}/{faction}_{minion}.asset";
    private static string AbilityPath(string abilityId)             => $"{AbilitiesRoot}/{abilityId}.asset";

    // ── Menu ───────────────────────────────────────────────────────────

    private static string WorkbookPath
    {
        get => EditorPrefs.GetString(WorkbookPathPref, DefaultWorkbookPath);
        set => EditorPrefs.SetString(WorkbookPathPref, value);
    }

    [MenuItem("Sunder/Import Minion Data", priority = 0)]
    public static void ImportFromMenu()
    {
        if (!File.Exists(WorkbookPath) && !ChooseWorkbook()) return;
        Import(WorkbookPath);
    }

    [MenuItem("Sunder/Choose Minion Data Workbook...", priority = 1)]
    public static void ChooseWorkbookFromMenu()
    {
        if (ChooseWorkbook() &&
            EditorUtility.DisplayDialog("Minion Data", $"Import from {WorkbookPath} now?", "Import", "Later"))
            Import(WorkbookPath);
    }

    private static bool ChooseWorkbook()
    {
        string start = File.Exists(WorkbookPath) ? Path.GetDirectoryName(Path.GetFullPath(WorkbookPath)) : Application.dataPath;
        string chosen = EditorUtility.OpenFilePanel("Choose minion data workbook", start, "xlsx");
        if (string.IsNullOrEmpty(chosen)) return false;

        // Store project-relative when inside the project, so it works on any machine.
        string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/') + "/";
        chosen = chosen.Replace('\\', '/');
        WorkbookPath = chosen.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? chosen.Substring(project.Length) : chosen;
        return true;
    }

    // ── Import ─────────────────────────────────────────────────────────

    public static void Import(string workbookPath)
    {
        XlsxReader book;
        try { book = XlsxReader.Load(workbookPath); }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Minion Data", $"Couldn't read {workbookPath}:\n\n{e.Message}", "OK");
            return;
        }

        var data     = MinionWorkbookParser.Parse(book);
        var warnings = new List<string>(data.Warnings);
        var report   = new Report();

        try
        {
            AssetDatabase.StartAssetEditing();
            try { PrepareTokens(data, warnings, report); }
            finally { AssetDatabase.StopAssetEditing(); }

            var abilities = ImportAbilities(data, report);
            var minions   = ImportMinions(data, abilities, warnings, report);
            ImportRelations(data, minions);
            UpdateFactions(data, minions, warnings, report);
            FindOrphans(data, report);

            AssetDatabase.SaveAssets();
        }
        finally { EditorUtility.ClearProgressBar(); }

        LogReport(workbookPath, data, warnings, report);
    }

    private class Report
    {
        public int MinionsCreated, MinionsUpdated, AbilitiesCreated, AbilitiesUpdated, TokensConverted;
        public readonly List<string> FactionsCreated = new();
        public readonly List<string> RostersUpdated  = new();
        public readonly List<string> Orphans         = new();
    }

    /// <summary>
    /// Token PNGs import as plain textures by default. Switch any the data
    /// uses to Sprite so they can go on a SpriteRenderer — batched, since each
    /// reimport is slow.
    /// </summary>
    private static void PrepareTokens(MinionWorkbookParser data, List<string> warnings, Report report)
    {
        foreach (var m in data.Minions)
        {
            string path = TokenPath(m.FactionId, m.MinionId);
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
            {
                warnings.Add($"{m.FactionId} {m.MinionId}: no token at {path}.");
                continue;
            }
            if (importer.textureType == TextureImporterType.Sprite) continue;

            importer.textureType      = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            report.TokensConverted++;
        }
    }

    private static Dictionary<string, AbilityDefinition> ImportAbilities(MinionWorkbookParser data, Report report)
    {
        var map = new Dictionary<string, AbilityDefinition>(StringComparer.OrdinalIgnoreCase);
        EnsureFolder(AbilitiesRoot);

        for (int i = 0; i < data.Abilities.Count; i++)
        {
            var a = data.Abilities[i];
            EditorUtility.DisplayProgressBar("Importing abilities", a.Id, (float)i / data.Abilities.Count);

            var asset = LoadOrCreate<AbilityDefinition>(AbilityPath(a.Id), out bool created);
            if (created) report.AbilitiesCreated++; else report.AbilitiesUpdated++;

            asset.abilityId       = a.Id;
            asset.displayName     = a.DisplayName;
            asset.description     = a.Description;
            asset.kind            = a.Kind;
            asset.targeting       = a.Targeting;
            asset.delivery        = a.Delivery;
            asset.dealsDamage     = a.DamageType.HasValue;
            asset.damageType      = a.DamageType ?? default;
            asset.basePower       = a.BasePower;
            asset.scalingStat     = a.ScalingStat;
            asset.scalingFactor   = a.ScalingFactor;
            asset.range           = a.Range;
            asset.areaRadius      = a.AreaRadius;
            asset.cooldown        = a.Cooldown;
            asset.castTime        = a.CastTime;
            asset.statusEffect    = a.StatusEffect;
            asset.statusChance    = a.StatusChance;
            asset.statusDuration  = a.StatusDuration;
            asset.statusMagnitude = a.StatusMagnitude;
            EditorUtility.SetDirty(asset);
            map[a.Id] = asset;
        }
        return map;
    }

    private static Dictionary<string, MinionDefinition> ImportMinions(
        MinionWorkbookParser data, Dictionary<string, AbilityDefinition> abilities, List<string> warnings, Report report)
    {
        var map = new Dictionary<string, MinionDefinition>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < data.Minions.Count; i++)
        {
            var m = data.Minions[i];
            EditorUtility.DisplayProgressBar("Importing minions", $"{m.FactionId} {m.DisplayName}", (float)i / data.Minions.Count);

            EnsureFolder($"{MinionsRoot}/{m.FactionId}");
            var def = LoadOrCreate<MinionDefinition>(MinionPath(m.FactionId, m.MinionId), out bool created);
            if (created) report.MinionsCreated++; else report.MinionsUpdated++;

            def.factionId           = m.FactionId;
            def.minionId            = m.MinionId;
            def.displayName         = m.DisplayName;
            def.token               = AssetDatabase.LoadAssetAtPath<Sprite>(TokenPath(m.FactionId, m.MinionId));
            def.stance              = m.Stance;
            def.tier                = m.Tier;
            def.movement            = m.Movement;
            def.damageTaken         = new List<MinionDefinition.DamageMultiplier>(m.DamageTaken);
            def.researchPreference  = m.ResearchPreference;
            def.trainPreference     = m.TrainPreference;
            def.buildPreference     = m.BuildPreference;
            def.prayPreference      = m.PrayPreference;
            def.torturePreference   = m.TorturePreference;
            def.summonable          = m.Summonable;
            def.summonWeight        = m.SummonWeight;
            def.populationCost      = m.PopulationCost;
            def.requiredRooms       = new List<MinionDefinition.RoomRequirement>(m.RequiredRooms);
            def.requiredResearchIds = new List<string>(m.RequiredResearch);
            def.maxLevel            = data.MaxLevel;
            def.stats               = new List<StatCurve>(m.Stats);
            def.abilities           = m.Abilities
                .Select(a => new MinionDefinition.AbilityUnlock
                {
                    gainLevel = a.GainLevel,
                    ability   = abilities.TryGetValue(a.AbilityId, out var ab) ? ab : null,
                })
                .ToList();
            // def.prefab deliberately untouched — hand-set override only.

            warnings.AddRange(MinionWorkbookParser.ApplyExtras(def, m));

            EditorUtility.SetDirty(def);
            map[m.Key] = def;
        }
        return map;
    }

    /// <summary>Second pass: relations point at other minion assets, which now all exist.</summary>
    private static void ImportRelations(MinionWorkbookParser data, Dictionary<string, MinionDefinition> minions)
    {
        foreach (var m in data.Minions)
        {
            var def = minions[m.Key];
            def.relations = m.Relations
                .Select(r => new MinionDefinition.MinionRelation
                {
                    other   = minions[MinionWorkbookParser.MinionKey(m.FactionId, r.OtherMinionId)],
                    feeling = r.Feeling,
                })
                .ToList();
            EditorUtility.SetDirty(def);
        }
    }

    /// <summary>
    /// Each faction's roster becomes its summonable minions, in sheet order.
    /// A faction with no FactionDefinition yet gets one (with default
    /// population/timing to tune by hand), added to every FactionRegistry.
    /// </summary>
    private static void UpdateFactions(MinionWorkbookParser data, Dictionary<string, MinionDefinition> minions,
                                       List<string> warnings, Report report)
    {
        var existing = AssetDatabase.FindAssets("t:FactionDefinition")
            .Select(g => AssetDatabase.LoadAssetAtPath<FactionDefinition>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(f => f != null && !string.IsNullOrEmpty(f.factionContentId))
            .GroupBy(f => f.factionContentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var registries = AssetDatabase.FindAssets("t:FactionRegistry")
            .Select(g => AssetDatabase.LoadAssetAtPath<FactionRegistry>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(r => r != null).ToList();

        foreach (var group in data.Minions.GroupBy(m => m.FactionId, StringComparer.OrdinalIgnoreCase))
        {
            string factionId = group.Key;
            if (!existing.TryGetValue(factionId, out var factions))
            {
                EnsureFolder(FactionsRoot);
                var created = LoadOrCreate<FactionDefinition>($"{FactionsRoot}/{factionId}.asset", out _);
                created.factionContentId = factionId;
                if (string.IsNullOrEmpty(created.displayName)) created.displayName = factionId;
                factions = new List<FactionDefinition> { created };
                report.FactionsCreated.Add(factionId);

                if (registries.Count == 0)
                    warnings.Add($"Created FactionDefinition {factionId}, but no FactionRegistry exists to add it to.");
                foreach (var registry in registries) AddToRegistry(registry, created);
            }

            var roster = group.Where(m => m.Summonable).Select(m => minions[m.Key]).ToList();
            foreach (var faction in factions)
            {
                faction.roster = new List<MinionDefinition>(roster);
                EditorUtility.SetDirty(faction);
            }
            report.RostersUpdated.Add($"{factionId} ({roster.Count} summonable)");
        }
    }

    private static void AddToRegistry(FactionRegistry registry, FactionDefinition faction)
    {
        var so   = new SerializedObject(registry);
        var list = so.FindProperty("definitions");
        for (int i = 0; i < list.arraySize; i++)
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == faction) return;

        list.InsertArrayElementAtIndex(list.arraySize);
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = faction;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void FindOrphans(MinionWorkbookParser data, Report report)
    {
        var minionPaths  = new HashSet<string>(data.Minions.Select(m => MinionPath(m.FactionId, m.MinionId)), StringComparer.OrdinalIgnoreCase);
        var abilityPaths = new HashSet<string>(data.Abilities.Select(a => AbilityPath(a.Id)), StringComparer.OrdinalIgnoreCase);

        if (AssetDatabase.IsValidFolder(MinionsRoot))
            foreach (var guid in AssetDatabase.FindAssets("t:MinionDefinition", new[] { MinionsRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!minionPaths.Contains(path)) report.Orphans.Add(path);
            }
        if (AssetDatabase.IsValidFolder(AbilitiesRoot))
            foreach (var guid in AssetDatabase.FindAssets("t:AbilityDefinition", new[] { AbilitiesRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!abilityPaths.Contains(path)) report.Orphans.Add(path);
            }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static T LoadOrCreate<T>(string path, out bool created) where T : ScriptableObject
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        created = asset == null;
        if (created)
        {
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
        }
        return asset;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    private static void LogReport(string workbookPath, MinionWorkbookParser data, List<string> warnings, Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[MinionData] Imported {workbookPath}");
        sb.AppendLine($"  Minions: {report.MinionsCreated} created, {report.MinionsUpdated} updated" +
                      (data.SkippedBlankRows > 0 ? $" ({data.SkippedBlankRows} rows with no Display Name skipped)" : ""));
        sb.AppendLine($"  Abilities: {report.AbilitiesCreated} created, {report.AbilitiesUpdated} updated");
        sb.AppendLine($"  Max level: {data.MaxLevel}");
        if (report.TokensConverted > 0) sb.AppendLine($"  Tokens switched to Sprite: {report.TokensConverted}");
        if (report.RostersUpdated.Count > 0) sb.AppendLine($"  Rosters: {string.Join(", ", report.RostersUpdated)}");
        if (report.FactionsCreated.Count > 0) sb.AppendLine($"  New FactionDefinitions: {string.Join(", ", report.FactionsCreated)}");
        if (report.Orphans.Count > 0)
        {
            sb.AppendLine($"  {report.Orphans.Count} generated asset(s) no longer in the workbook (not deleted):");
            foreach (var o in report.Orphans) sb.AppendLine($"    {o}");
        }
        if (warnings.Count > 0)
        {
            sb.AppendLine($"  {warnings.Count} warning(s):");
            foreach (var w in warnings) sb.AppendLine($"    {w}");
        }

        if (warnings.Count > 0 || report.Orphans.Count > 0) Debug.LogWarning(sb.ToString());
        else Debug.Log(sb.ToString());

        EditorUtility.DisplayDialog("Minion Data",
            $"Minions: {report.MinionsCreated} created, {report.MinionsUpdated} updated\n" +
            $"Abilities: {report.AbilitiesCreated} created, {report.AbilitiesUpdated} updated\n" +
            $"Warnings: {warnings.Count}" + (report.Orphans.Count > 0 ? $"\nNo longer in workbook: {report.Orphans.Count}" : "") +
            "\n\nFull details are in the Console.", "OK");
    }
}
