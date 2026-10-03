using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Sunder > Import Room Data: reads the RoomData workbook and writes each
/// row onto that room's TileDefinition — name, description, cost, colour,
/// placement rules, capacity and efficiency weights. A room with no
/// TileDefinition yet gets one, and every room is added to each TileRegistry.
///
/// Only the workbook's columns are written. The 3D prefab and offsets,
/// category, traversal and hit points stay as set on the asset, so art can
/// be assigned by hand without an import undoing it.
/// </summary>
public static class RoomDataImporter
{
    private const string DefaultWorkbookPath = "Assets/Data/RoomData.xlsx";
    private const string WorkbookPathPref    = "Sunder.RoomDataImporter.WorkbookPath";
    private const string DefaultAssetFolder  = "Assets/Prefabs/TileDefinitionAssets";

    private static string WorkbookPath
    {
        get => EditorPrefs.GetString(WorkbookPathPref, DefaultWorkbookPath);
        set => EditorPrefs.SetString(WorkbookPathPref, value);
    }

    [MenuItem("Sunder/Import Room Data", priority = 10)]
    public static void ImportFromMenu()
    {
        if (!File.Exists(WorkbookPath) && !ChooseWorkbook()) return;
        Import(WorkbookPath);
    }

    [MenuItem("Sunder/Choose Room Data Workbook...", priority = 11)]
    public static void ChooseWorkbookFromMenu()
    {
        if (ChooseWorkbook() &&
            EditorUtility.DisplayDialog("Room Data", $"Import from {WorkbookPath} now?", "Import", "Later"))
            Import(WorkbookPath);
    }

    private static bool ChooseWorkbook()
    {
        string start  = File.Exists(WorkbookPath) ? Path.GetDirectoryName(Path.GetFullPath(WorkbookPath)) : Application.dataPath;
        string chosen = EditorUtility.OpenFilePanel("Choose room data workbook", start, "xlsx");
        if (string.IsNullOrEmpty(chosen)) return false;

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
            EditorUtility.DisplayDialog("Room Data", $"Couldn't read {workbookPath}:\n\n{e.Message}", "OK");
            return;
        }

        var data     = RoomWorkbookParser.Parse(book);
        var warnings = new List<string>(data.Warnings);

        var registries = AssetDatabase.FindAssets("t:TileRegistry")
            .Select(g => AssetDatabase.LoadAssetAtPath<TileRegistry>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(r => r != null).ToList();
        if (registries.Count == 0)
            warnings.Add("No TileRegistry asset found — rooms were updated but not registered anywhere.");

        var existing = FindDefinitions(warnings);
        string folder = registries.Count > 0
            ? Path.GetDirectoryName(AssetDatabase.GetAssetPath(registries[0])).Replace('\\', '/')
            : DefaultAssetFolder;

        int created = 0, updated = 0, registered = 0;
        foreach (var room in data.Rooms)
        {
            if (!existing.TryGetValue(room.Type, out var def))
            {
                def = CreateDefinition(room, folder, existing);
                existing[room.Type] = def;
                created++;
            }
            else updated++;

            Apply(room, def);
            EditorUtility.SetDirty(def);

            foreach (var registry in registries)
                if (AddToRegistry(registry, def)) registered++;
        }

        // Rooms on assets that the workbook no longer mentions keep their old settings.
        var listed = new HashSet<TileType>(data.Rooms.Select(r => r.Type));
        foreach (var pair in existing)
            if (pair.Value.isRoom && !listed.Contains(pair.Key))
                warnings.Add($"{pair.Key} is a room on its TileDefinition but has no row on the Rooms sheet — left as is.");

        AssetDatabase.SaveAssets();
        Report(workbookPath, created, updated, registered, warnings);
    }

    private static Dictionary<TileType, TileDefinition> FindDefinitions(List<string> warnings)
    {
        var map = new Dictionary<TileType, TileDefinition>();
        foreach (var guid in AssetDatabase.FindAssets("t:TileDefinition"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var def = AssetDatabase.LoadAssetAtPath<TileDefinition>(path);
            if (def == null) continue;
            if (map.TryGetValue(def.tileType, out var other))
            {
                warnings.Add($"Two TileDefinitions for {def.tileType}: {AssetDatabase.GetAssetPath(other)} and {path} — updating the first.");
                continue;
            }
            map[def.tileType] = def;
        }
        return map;
    }

    /// <summary>
    /// A new room starts as owned, walkable floor, borrowing the Tunnel's 3D
    /// prefab (the Portal's for non-room structures) until it has art.
    /// </summary>
    private static TileDefinition CreateDefinition(RoomWorkbookParser.RoomRecord room, string folder,
                                                   Dictionary<TileType, TileDefinition> existing)
    {
        var def = ScriptableObject.CreateInstance<TileDefinition>();
        def.tileType      = room.Type;
        def.category      = TileCategory.Owned;
        def.traversalType = TraversalType.Normal;

        var lookAlike = room.IsRoom ? TileType.Tunnel : TileType.Portal;
        if (existing.TryGetValue(lookAlike, out var source) && source != null)
            def.prefab3D = source.prefab3D;

        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Tile{room.Type}.asset");
        AssetDatabase.CreateAsset(def, path);
        return def;
    }

    private static void Apply(RoomWorkbookParser.RoomRecord room, TileDefinition def)
    {
        def.tileName          = room.DisplayName;
        def.description       = room.Description;
        def.isRoom            = room.IsRoom;
        def.playerBuildable   = room.PlayerBuildable;
        def.buyCost           = room.BuyCost;
        def.sellValue         = room.SellValue;
        def.placesOnTunnel    = room.PlacesOn == RoomWorkbookParser.Placement.Tunnel;
        def.placesOnLiquid    = room.PlacesOn == RoomWorkbookParser.Placement.Liquid;
        def.requiresAdjacency = room.RequiresAdjacency;
        def.isIndestructible  = room.Indestructible;
        def.capacityPerTile   = room.CapacityPerTile;
        def.capacityScalesWithEfficiency = room.CapacityScalesWithEfficiency;
        def.baseEfficiency    = room.BaseEfficiency;
        def.shapeWeight       = room.ShapeWeight;
        def.wallWeight        = room.WallWeight;
        if (room.Colour != null)
            def.gridColour = new Color(room.Colour[0], room.Colour[1], room.Colour[2], 1f);
    }

    /// <summary>Adds the definition to the registry's list if missing. Returns true if added.</summary>
    private static bool AddToRegistry(TileRegistry registry, TileDefinition def)
    {
        var so   = new SerializedObject(registry);
        var list = so.FindProperty("definitions");
        for (int i = 0; i < list.arraySize; i++)
            if (list.GetArrayElementAtIndex(i).objectReferenceValue == def) return false;

        list.InsertArrayElementAtIndex(list.arraySize);
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = def;
        so.ApplyModifiedPropertiesWithoutUndo();
        return true;
    }

    private static void Report(string workbookPath, int created, int updated, int registered, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[RoomData] Imported {workbookPath}");
        sb.AppendLine($"  Rooms: {created} created, {updated} updated; {registered} added to a TileRegistry");
        if (warnings.Count > 0)
        {
            sb.AppendLine($"  {warnings.Count} warning(s):");
            foreach (var w in warnings) sb.AppendLine($"    {w}");
            Debug.LogWarning(sb.ToString());
        }
        else Debug.Log(sb.ToString());

        EditorUtility.DisplayDialog("Room Data",
            $"Rooms: {created} created, {updated} updated\nAdded to TileRegistry: {registered}\nWarnings: {warnings.Count}" +
            "\n\nFull details are in the Console.", "OK");
    }
}
