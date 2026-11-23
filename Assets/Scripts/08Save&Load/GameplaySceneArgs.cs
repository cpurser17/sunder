/// <summary>
/// Static handoff class that carries arguments from the Overworld Scene
/// (or Title Scene) into the Gameplay Scene.
///
/// Set these fields before calling SceneManager.LoadScene("Gameplay"),
/// then read them in GameManager2D.Start().
/// Clear() is called by GameManager2D after reading so stale args
/// don't persist into subsequent scene loads.
///
/// For in-editor Play mode testing without going through the full scene
/// flow, leave these null — GameManager2D falls back to its Inspector
/// defaults.
/// </summary>
public static class GameplaySceneArgs
{
    // ── Slot / branch / save ───────────────────────────────────────────
    public static int    SlotIndex    = 0;
    public static string BranchId     = "branch_0";
    public static int    SaveIndex    = -1;     // -1 = start fresh from level file

    // ── Level to load ──────────────────────────────────────────────────
    public static string LevelId      = null;   // null = use Inspector default
    // Play the level file as saved, even if its id is a campaign mission's
    // (the level editor's play-test). Otherwise a campaign id plays the
    // campaign's packed copy.
    public static bool   LevelFromFile = false;

    // ── Where the mission was started from (Finish returns there) ──────
    public static MissionOrigin Origin = MissionOrigin.None;
    // A campaign mission: which campaign and mission (for its result).
    public static string CampaignId    = null;
    public static string MissionId     = null;
    // The faction the human player plays, replacing the level's own choice
    // (playing one faction's campaign as another). Null = the level's.
    public static string PlayerFactionId = null;

    // ── Mode ──────────────────────────────────────────────────────────
    public static bool   IsNewGame    = true;   // false = load existing save
    public static bool   LoadAutosave = false;  // true = load autosave.json

    // ── Clear after reading ────────────────────────────────────────────
    public static void Clear()
    {
        SlotIndex    = 0;
        BranchId     = "branch_0";
        SaveIndex    = -1;
        LevelId      = null;
        LevelFromFile = false;
        Origin       = MissionOrigin.None;
        CampaignId   = null;
        MissionId    = null;
        PlayerFactionId = null;
        IsNewGame    = true;
        LoadAutosave = false;
    }
}

/// <summary>Which screen started a mission — the results screen's Finish goes back there.</summary>
public enum MissionOrigin { None, Campaign, Skirmish, LevelEditor }

/// <summary>
/// What the Gameplay scene hands back when a mission is finished: where it
/// came from and how it went. Read (and cleared) by the scene Finish loads —
/// the Campaign scene records the result; the level editor reopens the level.
/// </summary>
public static class MissionReturn
{
    public static MissionOrigin Origin;
    public static string        CampaignId;
    public static MissionResult Result;
    public static string        LevelId;

    public static bool Pending => Result != null;

    public static void Clear()
    {
        Origin = MissionOrigin.None;
        CampaignId = null;
        Result = null;
        LevelId = null;
    }
}

/// <summary>Scene names, as in Build Settings.</summary>
public static class SceneNames
{
    public const string Start       = "Start";
    public const string Campaign    = "Campaign";
    public const string Gameplay    = "Gameplay";
    public const string LevelEditor = "LevelEditor";
    /// <summary>Not built yet — skirmish games return to Start until it is.</summary>
    public const string Skirmish    = "Skirmish";
}
