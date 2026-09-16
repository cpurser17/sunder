/// <summary>
/// Schema for a gameplay save file.
/// Written during play to persistentDataPath/Campaigns/slot_N/branch_X/save_NNNN.json.
///
/// References the level it was started from (levelId) so the game can display
/// which mission this save belongs to and fall back gracefully if needed.
///
/// Extension slots are null-safe on deserialisation — missing fields in older
/// saves default to null/zero, so new fields can be added without breaking
/// existing saves.
/// </summary>
[System.Serializable]
public class SaveData
{
    // ── Versioning ─────────────────────────────────────────────────────
    public int    saveVersion = 1;  // increment if schema changes require migration

    // ── Identity ───────────────────────────────────────────────────────
    public string levelId;          // which level file this save originated from
    public string displayName;      // short description shown in loading screen
    public string saveDateTime;     // ISO 8601, set on write

    // ── Branch tracking ────────────────────────────────────────────────
    public int    slotIndex;        // campaign slot (0–9)
    public string branchId;        // e.g. "branch_0"
    public int    saveIndex;        // position within the branch (0-based)

    // ── Core game data ─────────────────────────────────────────────────
    public GridSaveData      grid;
    public GameStateSaveData gameState;

    // ── Future extension slots ─────────────────────────────────────────
    // Declare as nullable classes so old saves that lack the field
    // deserialise to null rather than throwing.
    // public EntitySaveData      entities;
    // public PlacedItemSaveData  placedItems;
    // public OverworldSaveData   overworld;
}
