using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Static class handling all file I/O for levels, saves, autosaves,
/// thumbnails, and campaign manifests.
///
/// File layout
/// -----------
/// StreamingAssets/Levels/               ← built-in level files (read-only)
/// persistentDataPath/
///   CustomLevels/                       ← user/editor levels (read-write)
///   Campaigns/
///     slot_N/
///       manifest.json                   ← master index / branch tree
///       autosave.json                   ← single overwritten autosave
///       autosave.png                    ← autosave thumbnail
///       branch_X/
///         save_0000.json
///         save_0000.png                 ← per-save thumbnail
///         save_0001.json
///         save_0001.png
///
/// Save behaviour summary
/// ----------------------
/// Autosave          → always overwrites autosave.json (no branching)
/// Save (tip)        → overwrites the current branch-tip file in place
/// Save (mid-branch) → creates a new branch from the loaded save point
/// Save As (tip)     → appends a new save at the end of the current branch
/// Save As (mid)     → same as Save for now (until UI chooser is built)
///
/// Thumbnail generation
/// --------------------
/// Call CaptureThumbnail() from a MonoBehaviour coroutine (needs end-of-frame).
/// Pass the resulting Texture2D to the relevant Write* method.
/// </summary>
public static class SaveLoadSystem
{
    // ── Json settings ──────────────────────────────────────────────────

    private static readonly JsonSerializerSettings LevelSettings = new()
    {
        Formatting           = Formatting.Indented,
        NullValueHandling    = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Include,
        Converters           = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    private static readonly JsonSerializerSettings SaveSettings = new()
    {
        Formatting           = Formatting.None,
        NullValueHandling    = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Include,
        Converters           = { new Newtonsoft.Json.Converters.StringEnumConverter() },
    };

    // ── Path helpers ───────────────────────────────────────────────────

    public static string CampaignRoot =>
        Path.Combine(Application.persistentDataPath, "Campaigns");

    public static string CustomLevelsRoot =>
        Path.Combine(Application.persistentDataPath, "CustomLevels");

    public static string SlotPath(int slot) =>
        Path.Combine(CampaignRoot, $"slot_{slot}");

    public static string ManifestPath(int slot) =>
        Path.Combine(SlotPath(slot), "manifest.json");

    public static string AutosavePath(int slot) =>
        Path.Combine(SlotPath(slot), "autosave.json");

    public static string AutosaveThumbnailPath(int slot) =>
        Path.Combine(SlotPath(slot), "autosave.png");

    public static string BranchPath(int slot, string branchId) =>
        Path.Combine(SlotPath(slot), branchId);

    public static string SaveFilePath(int slot, string branchId, int saveIndex) =>
        Path.Combine(BranchPath(slot, branchId), $"save_{saveIndex:D4}.json");

    public static string ThumbnailPath(int slot, string branchId, int saveIndex) =>
        Path.Combine(BranchPath(slot, branchId), $"save_{saveIndex:D4}.png");

    private static string LevelPath(string levelId)
    {
        string custom = Path.Combine(CustomLevelsRoot, $"{levelId}.json");
        if (File.Exists(custom)) return custom;
        return Path.Combine(Application.streamingAssetsPath, "Levels", $"{levelId}.json");
    }

    // ── Level I/O ──────────────────────────────────────────────────────

    public static LevelData LoadLevel(string levelId)
    {
        string path = LevelPath(levelId);
        if (!File.Exists(path))
        {
            Debug.LogError($"[SaveLoadSystem] Level not found: {path}");
            return null;
        }
        try
        {
            var data = JsonConvert.DeserializeObject<LevelData>(
                File.ReadAllText(path), LevelSettings);
            Debug.Log($"[SaveLoadSystem] Loaded level '{levelId}'.");
            return data;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] Failed to parse level '{levelId}': {e.Message}");
            return null;
        }
    }

    public static bool SaveLevel(LevelData data)
    {
        EnsureDirectory(CustomLevelsRoot);
        string path = Path.Combine(CustomLevelsRoot, $"{data.levelId}.json");
        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(data, LevelSettings));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] Failed to save level: {e.Message}");
            return false;
        }
    }

    // ── Autosave ───────────────────────────────────────────────────────

    /// <summary>
    /// Overwrites the single autosave file for the slot.
    /// Always writes to autosave.json — no branching, no index increment.
    /// Updates the manifest autosaveEntry.
    /// </summary>
    public static bool WriteAutosave(SaveData data, Texture2D thumbnail = null)
    {
        data.saveDateTime = DateTime.UtcNow.ToString("o");
        data.saveIndex    = -1;
        data.branchId     = null;

        var manifest = LoadOrCreateManifest(data.slotIndex);
        string path  = AutosavePath(data.slotIndex);
        EnsureDirectory(SlotPath(data.slotIndex));

        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(data, SaveSettings));
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] Autosave write failed: {e.Message}");
            return false;
        }

        // Save thumbnail if provided.
        string thumbPath = null;
        if (thumbnail != null)
        {
            thumbPath = AutosaveThumbnailPath(data.slotIndex);
            WriteThumbnail(thumbnail, thumbPath);
        }

        // Update manifest autosave entry.
        manifest.autosaveEntry = BuildSaveEntry(data, null, -1,
                                                thumbPath, isAutosave: true);
        manifest.lastPlayed    = data.saveDateTime;
        WriteManifest(data.slotIndex, manifest);

        Debug.Log($"[SaveLoadSystem] Autosave written for slot {data.slotIndex}.");
        return true;
    }

    public static SaveData LoadAutosave(int slot)
    {
        string path = AutosavePath(slot);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[SaveLoadSystem] No autosave found for slot {slot}.");
            return null;
        }
        return DeserializeSave(path);
    }

    // ── Branched save: Append (Save As / tip Save) ─────────────────────

    /// <summary>
    /// Appends a new save file at the end of the current branch.
    /// Used for:
    ///   • "Save As" at any position in the branch.
    ///   • "Save" when the loaded file IS the branch tip.
    ///   • A new game's first manual save.
    /// Returns the new save index, or -1 on failure.
    /// </summary>
    public static int AppendSave(SaveData data, Texture2D thumbnail = null)
    {
        data.saveDateTime = DateTime.UtcNow.ToString("o");
        var manifest  = LoadOrCreateManifest(data.slotIndex);
        var branch    = EnsureBranchExists(manifest, data.branchId, data.slotIndex);
        int index     = branch.saveCount;
        data.saveIndex = index;

        string path = SaveFilePath(data.slotIndex, data.branchId, index);
        EnsureDirectory(Path.GetDirectoryName(path));

        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(data, SaveSettings));
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] AppendSave failed: {e.Message}");
            return -1;
        }

        string thumbPath = null;
        if (thumbnail != null)
        {
            thumbPath = ThumbnailPath(data.slotIndex, data.branchId, index);
            WriteThumbnail(thumbnail, thumbPath);
        }

        branch.saveCount++;
        manifest.UpsertSaveEntry(
            BuildSaveEntry(data, data.branchId, index, thumbPath));
        manifest.activeBranchId   = data.branchId;
        manifest.activeSaveIndex  = index;
        manifest.activeIsAutosave = false;
        manifest.lastPlayed       = data.saveDateTime;
        WriteManifest(data.slotIndex, manifest);

        Debug.Log($"[SaveLoadSystem] Appended save slot={data.slotIndex} " +
                  $"branch={data.branchId} index={index}.");
        return index;
    }

    // ── Overwrite: replace an existing save in place ───────────────────

    /// <summary>
    /// Overwrites a specific existing save file in place.
    /// Used for "Save" when the loaded file IS the branch tip.
    /// Returns true on success.
    /// </summary>
    public static bool OverwriteSave(SaveData data, Texture2D thumbnail = null)
    {
        data.saveDateTime = DateTime.UtcNow.ToString("o");
        string path = SaveFilePath(data.slotIndex, data.branchId, data.saveIndex);

        if (!File.Exists(path))
        {
            Debug.LogWarning($"[SaveLoadSystem] OverwriteSave: file not found at {path}. " +
                             "Caller should fall back to AppendSave.");
            return false;
        }

        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(data, SaveSettings));
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] OverwriteSave failed: {e.Message}");
            return false;
        }

        string thumbPath = null;
        if (thumbnail != null)
        {
            thumbPath = ThumbnailPath(data.slotIndex, data.branchId, data.saveIndex);
            WriteThumbnail(thumbnail, thumbPath);
        }

        var manifest = LoadOrCreateManifest(data.slotIndex);
        manifest.UpsertSaveEntry(
            BuildSaveEntry(data, data.branchId, data.saveIndex, thumbPath));
        manifest.lastPlayed = data.saveDateTime;
        WriteManifest(data.slotIndex, manifest);

        Debug.Log($"[SaveLoadSystem] Overwrote save slot={data.slotIndex} " +
                  $"branch={data.branchId} index={data.saveIndex}.");
        return true;
    }

    // ── Branch fork ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new branch forking from parentSaveIndex in parentBranchId.
    /// Copies the parent save into the new branch as save_0000.
    /// Returns the new branch ID, or null on failure.
    /// </summary>
    public static string ForkBranch(int slot, string parentBranchId,
                                    int parentSaveIndex, string label = "")
    {
        var manifest  = LoadOrCreateManifest(slot);
        string newId  = manifest.NextBranchId();
        EnsureDirectory(BranchPath(slot, newId));

        // Load the fork-point save and copy it as the new branch root.
        var parentSave = LoadSave(slot, parentBranchId, parentSaveIndex);
        if (parentSave == null) return null;

        parentSave.branchId  = newId;
        parentSave.saveIndex = 0;
        parentSave.saveDateTime = DateTime.UtcNow.ToString("o");

        string destPath = SaveFilePath(slot, newId, 0);
        try
        {
            File.WriteAllText(destPath,
                JsonConvert.SerializeObject(parentSave, SaveSettings));
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] ForkBranch copy failed: {e.Message}");
            return null;
        }

        // Copy thumbnail if available.
        string srcThumb  = ThumbnailPath(slot, parentBranchId, parentSaveIndex);
        string destThumb = ThumbnailPath(slot, newId, 0);
        string thumbRef  = null;
        if (File.Exists(srcThumb))
        {
            File.Copy(srcThumb, destThumb, overwrite: true);
            thumbRef = destThumb;
        }

        // Register new branch and its root save entry.
        string created = DateTime.UtcNow.ToString("o");
        manifest.branches.Add(new BranchEntry
        {
            branchId        = newId,
            parentBranchId  = parentBranchId,
            parentSaveIndex = parentSaveIndex,
            label           = string.IsNullOrEmpty(label)
                                ? $"Branch from {parentBranchId}[{parentSaveIndex}]"
                                : label,
            createdDateTime = created,
            saveCount       = 1,
        });

        manifest.UpsertSaveEntry(
            BuildSaveEntry(parentSave, newId, 0, thumbRef));
        manifest.activeBranchId  = newId;
        manifest.activeSaveIndex = 0;
        manifest.lastPlayed      = created;
        WriteManifest(slot, manifest);

        Debug.Log($"[SaveLoadSystem] Forked branch '{newId}' in slot {slot} " +
                  $"from {parentBranchId}[{parentSaveIndex}].");
        return newId;
    }

    // ── Load ───────────────────────────────────────────────────────────

    public static SaveData LoadSave(int slot, string branchId, int saveIndex)
    {
        string path = SaveFilePath(slot, branchId, saveIndex);
        if (!File.Exists(path))
        {
            Debug.LogError($"[SaveLoadSystem] Save not found: {path}");
            return null;
        }
        return DeserializeSave(path);
    }

    public static SaveData LoadLatestSave(int slot)
    {
        var manifest = LoadManifest(slot);
        if (manifest == null || manifest.activeSaveIndex < 0) return null;

        if (manifest.activeIsAutosave) return LoadAutosave(slot);
        return LoadSave(slot, manifest.activeBranchId, manifest.activeSaveIndex);
    }

    // ── Thumbnail ──────────────────────────────────────────────────────

    /// <summary>
    /// Encodes and writes a Texture2D as PNG.
    /// Call CaptureThumbnail() in a coroutine (WaitForEndOfFrame) to get
    /// the Texture2D, then pass it to a Write* method.
    /// </summary>
    public static void WriteThumbnail(Texture2D tex, string fullPath)
    {
        try
        {
            File.WriteAllBytes(fullPath, tex.EncodeToPNG());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveLoadSystem] Thumbnail write failed: {e.Message}");
        }
    }

    /// <summary>
    /// Loads a saved thumbnail from disk as a Texture2D ready for UI display.
    /// Returns null if the file is missing.
    /// </summary>
    public static Texture2D LoadThumbnail(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            var tex = new Texture2D(2, 2);
            tex.LoadImage(bytes);
            return tex;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveLoadSystem] Thumbnail load failed: {e.Message}");
            return null;
        }
    }

    // ── Manifest I/O ──────────────────────────────────────────────────

    public static CampaignManifest LoadManifest(int slot)
    {
        string path = ManifestPath(slot);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonConvert.DeserializeObject<CampaignManifest>(
                File.ReadAllText(path), SaveSettings);
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] Manifest read failed slot={slot}: {e.Message}");
            return null;
        }
    }

    public static CampaignManifest InitialiseSlot(int slot)
    {
        EnsureDirectory(SlotPath(slot));
        EnsureDirectory(BranchPath(slot, "branch_0"));

        var manifest = new CampaignManifest
        {
            slotIndex      = slot,
            activeBranchId = "branch_0",
            activeSaveIndex = -1,
            lastPlayed     = DateTime.UtcNow.ToString("o"),
        };
        manifest.branches.Add(new BranchEntry
        {
            branchId        = "branch_0",
            parentBranchId  = null,
            parentSaveIndex = -1,
            label           = "Main",
            createdDateTime = DateTime.UtcNow.ToString("o"),
            saveCount       = 0,
        });

        WriteManifest(slot, manifest);
        Debug.Log($"[SaveLoadSystem] Initialised campaign slot {slot}.");
        return manifest;
    }

    private static CampaignManifest LoadOrCreateManifest(int slot) =>
        LoadManifest(slot) ?? InitialiseSlot(slot);

    public static void WriteManifest(int slot, CampaignManifest manifest)
    {
        EnsureDirectory(SlotPath(slot));
        File.WriteAllText(ManifestPath(slot),
            JsonConvert.SerializeObject(manifest, LevelSettings));
    }

    // ── Grid conversion ────────────────────────────────────────────────

    public static GridSaveData CaptureGrid(GridManager2D grid)
    {
        var data = new GridSaveData(grid.Width, grid.Height);
        for (int i = 0; i < grid.Width * grid.Height; i++) data.cells.Add(null);
        for (int x = 0; x < grid.Width;  x++)
        for (int y = 0; y < grid.Height; y++)
            data.SetCell(x, y, CellSaveData.From(grid.GetCell(x, y)));
        return data;
    }

    public static void ApplyGrid(GridManager2D grid, GridSaveData data)
    {
        var cells = new CellSaveData[data.width, data.height];
        for (int x = 0; x < data.width;  x++)
        for (int y = 0; y < data.height; y++)
            cells[x, y] = data.GetCell(x, y);
        grid.Initialise(data.width, data.height, cells);
    }

    // ── Utility ────────────────────────────────────────────────────────

    public static List<CampaignManifest> LoadAllManifests()
    {
        var result = new List<CampaignManifest>();
        for (int i = 0; i < 10; i++)
        {
            var m = LoadManifest(i);
            if (m != null) result.Add(m);
        }
        return result;
    }

    public static List<string> GetAvailableLevelIds()
    {
        var ids = new List<string>();
        string builtIn = Path.Combine(Application.streamingAssetsPath, "Levels");
        if (Directory.Exists(builtIn))
            foreach (var f in Directory.GetFiles(builtIn, "*.json"))
                ids.Add(Path.GetFileNameWithoutExtension(f));
        if (Directory.Exists(CustomLevelsRoot))
            foreach (var f in Directory.GetFiles(CustomLevelsRoot, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (!ids.Contains(id)) ids.Add(id);
            }
        return ids;
    }

    // ── Private helpers ────────────────────────────────────────────────

    private static SaveData DeserializeSave(string path)
    {
        try
        {
            return JsonConvert.DeserializeObject<SaveData>(
                File.ReadAllText(path), SaveSettings);
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveLoadSystem] Failed to parse save at {path}: {e.Message}");
            return null;
        }
    }

    private static BranchEntry EnsureBranchExists(CampaignManifest manifest,
                                                   string branchId, int slot)
    {
        var branch = manifest.GetBranch(branchId);
        if (branch != null) return branch;

        // Branch missing from manifest — create a root entry for it.
        Debug.LogWarning($"[SaveLoadSystem] Branch '{branchId}' missing from manifest. " +
                         "Creating entry.");
        branch = new BranchEntry
        {
            branchId        = branchId,
            parentBranchId  = null,
            parentSaveIndex = -1,
            label           = branchId,
            createdDateTime = DateTime.UtcNow.ToString("o"),
            saveCount       = 0,
        };
        manifest.branches.Add(branch);
        EnsureDirectory(BranchPath(slot, branchId));
        return branch;
    }

    private static SaveEntry BuildSaveEntry(SaveData data, string branchId,
                                            int saveIndex, string thumbPath,
                                            bool isAutosave = false) =>
        new SaveEntry
        {
            branchId      = branchId,
            saveIndex     = saveIndex,
            dateTime      = data.saveDateTime,
            levelId       = data.levelId,
            displayName   = data.displayName,
            goldAtSave    = data.gameState?.currentGold ?? 0,
            thumbnailPath = thumbPath != null
                              ? Path.GetRelativePath(SlotPath(data.slotIndex), thumbPath)
                              : null,
            isMissing     = false,
            isAutosave    = isAutosave,
        };

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }
}
