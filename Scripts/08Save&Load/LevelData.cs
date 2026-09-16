using System.Collections.Generic;

/// <summary>
/// Describes one faction's participation in a mission.
/// Stored in LevelData.factions so the level file drives which factions
/// are active, how many AI opponents exist, and their starting colours.
/// </summary>
[System.Serializable]
public class FactionSetup
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID factionId;
    public string    displayName;    // e.g. "Player", "The Dark Legion"
    // Colour stored as hex string e.g. "#4488FF" for readability in level files.
    public string    colourHex = "#FFFFFF";
    public bool      isHuman;        // true = local player, false = AI
    public int       startingGold;   // per-faction starting gold (overrides level default if > 0)
}

/// <summary>
/// Schema for a level definition file.
/// Authored by the level editor (or by hand) and shipped in StreamingAssets/Levels/.
/// User-created levels live in persistentDataPath/CustomLevels/.
///
/// This is a template — it defines the starting state of a mission.
/// It is never modified during gameplay.
/// </summary>
[System.Serializable]
public class LevelData
{
    // ── Identity ───────────────────────────────────────────────────────
    public string levelId;          // unique key, matches filename without extension
    public string displayName;      // shown in UI
    public string description;      // flavour text / briefing

    // ── Factions ───────────────────────────────────────────────────────
    // All factions active in this mission.
    // DigSelectionManager reads this to spawn one controller per faction.
    public System.Collections.Generic.List<FactionSetup> factions = new();

    // ── Starting conditions ────────────────────────────────────────────
    public int startingGold = 500;   // default gold for factions that do not override

    // ── Grid ──────────────────────────────────────────────────────────
    public GridSaveData grid;

    // ── Future extension slots ─────────────────────────────────────────
    // public List<EntitySpawnData>  startingEntities;
    // public List<ObjectiveData>    objectives;
    // public OverworldLevelMetaData overworldMeta;
}

/// <summary>
/// Serialisable grid state. Shared between LevelData and SaveData.
/// Cells are stored as a flat array; index = y * width + x.
/// </summary>
[System.Serializable]
public class GridSaveData
{
    public int              width;
    public int              height;
    public List<CellSaveData> cells; // flat, length = width * height

    public GridSaveData() { }

    public GridSaveData(int w, int h)
    {
        width  = w;
        height = h;
        cells  = new List<CellSaveData>(w * h);
    }

    public CellSaveData GetCell(int x, int y) => cells[y * width + x];
    public void         SetCell(int x, int y, CellSaveData data) =>
        cells[y * width + x] = data;
}

/// <summary>
/// Serialisable game state (gold, score, etc.).
/// Kept separate from GridSaveData so each concern can evolve independently.
/// </summary>
[System.Serializable]
public class GameStateSaveData
{
    // Legacy single-wallet field — kept for backwards compatibility.
    // New saves also populate factionGold.
    public int currentGold;

    // Per-faction gold balances keyed by FactionID.
    // Null in saves predating the wallet refactor — handled gracefully on load.
    [Newtonsoft.Json.JsonProperty(ItemConverterType = typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public System.Collections.Generic.Dictionary<FactionID, int> factionGold;

    // ── Future extension slots ─────────────────────────────────────────
    // public int   score;
    // public float missionElapsedTime;
}
