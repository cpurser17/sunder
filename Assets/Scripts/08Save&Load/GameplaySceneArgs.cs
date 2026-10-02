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
        IsNewGame    = true;
        LoadAutosave = false;
    }
}
