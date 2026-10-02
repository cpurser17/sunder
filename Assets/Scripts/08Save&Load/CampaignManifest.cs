using System;
using System.Collections.Generic;

/// <summary>
/// Master index for one campaign save slot.
/// Stored at persistentDataPath/Campaigns/slot_N/manifest.json.
///
/// Acts as the source of truth for the branching save tree, independently
/// of whether individual save files exist on disk. If a save file is
/// deleted or corrupted, its SaveEntry remains here with isMissing = true
/// so the UI can display a "missing file" placeholder while preserving
/// the overall tree structure.
///
/// SaveEntry records are lightweight — they carry only what the loading
/// screen needs to display (timestamp, level, gold, thumbnail path).
/// The full game state lives in the individual save files.
/// </summary>
[Serializable]
public class CampaignManifest
{
    // ── Slot identity ──────────────────────────────────────────────────
    public int    slotIndex;
    public string lastPlayed;           // ISO 8601

    // ── Active session pointers ────────────────────────────────────────
    public string activeBranchId   = "branch_0";
    public int    activeSaveIndex  = -1;       // -1 = no save yet
    public bool   activeIsAutosave = false;

    // ── Branch tree ────────────────────────────────────────────────────
    public List<BranchEntry> branches   = new();

    // ── Save index (all saves ever recorded for this slot) ────────────
    public List<SaveEntry>   saveEntries = new();

    // ── Autosave entry (singleton per slot) ───────────────────────────
    public SaveEntry autosaveEntry;         // null = no autosave yet

    // ── Branch helpers ─────────────────────────────────────────────────

    public BranchEntry GetBranch(string branchId)
    {
        foreach (var b in branches)
            if (b.branchId == branchId) return b;
        return null;
    }

    public string NextBranchId()
    {
        int i = 0;
        while (GetBranch($"branch_{i}") != null) i++;
        return $"branch_{i}";
    }

    public int SaveCountInBranch(string branchId) =>
        GetBranch(branchId)?.saveCount ?? 0;

    /// <summary>
    /// Returns true if saveIndex is the most recent save in its branch.
    /// </summary>
    public bool IsBranchTip(string branchId, int saveIndex)
    {
        var branch = GetBranch(branchId);
        if (branch == null) return false;
        return saveIndex == branch.saveCount - 1;
    }

    // ── SaveEntry helpers ──────────────────────────────────────────────

    public SaveEntry GetSaveEntry(string branchId, int saveIndex)
    {
        foreach (var e in saveEntries)
            if (e.branchId == branchId && e.saveIndex == saveIndex) return e;
        return null;
    }

    public void UpsertSaveEntry(SaveEntry entry)
    {
        for (int i = 0; i < saveEntries.Count; i++)
        {
            if (saveEntries[i].branchId  == entry.branchId &&
                saveEntries[i].saveIndex == entry.saveIndex)
            {
                saveEntries[i] = entry;
                return;
            }
        }
        saveEntries.Add(entry);
    }

    /// <summary>
    /// Validates each save entry against the file system and marks
    /// missing files. Call this when building the UI tree.
    /// </summary>
    public void RefreshMissingFlags(System.Func<SaveEntry, bool> fileExistsCheck)
    {
        foreach (var e in saveEntries)
            e.isMissing = !fileExistsCheck(e);

        if (autosaveEntry != null)
            autosaveEntry.isMissing = !fileExistsCheck(autosaveEntry);
    }
}

// ── Branch entry ───────────────────────────────────────────────────────────

/// <summary>
/// Describes one branch in the save tree.
/// </summary>
[Serializable]
public class BranchEntry
{
    public string branchId;
    public string parentBranchId;      // null for the root branch
    public int    parentSaveIndex;     // -1 for root
    public string label;               // player-facing e.g. "Chose alliance"
    public string createdDateTime;
    public int    saveCount;           // number of save files in this branch
}

// ── Save entry ─────────────────────────────────────────────────────────────

/// <summary>
/// Lightweight metadata record for one save file, stored in the manifest
/// so the loading screen can display all saves without deserialising them.
/// </summary>
[Serializable]
public class SaveEntry
{
    public string branchId;
    public int    saveIndex;           // -1 for autosave
    public string dateTime;            // ISO 8601
    public string levelId;
    public string displayName;         // short human-readable description
    public int    goldAtSave;
    public string thumbnailPath;       // relative to slot folder; null = no thumbnail
    public bool   isMissing;           // true if file not found on disk
    public bool   isAutosave;

    // ── Future ─────────────────────────────────────────────────────────
    // public int   score;
    // public float missionTime;
}
