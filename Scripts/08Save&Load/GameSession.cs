using UnityEngine;

/// <summary>
/// Lightweight persistent singleton that survives scene transitions.
/// Carries only what needs to cross scene boundaries:
///   • Which campaign slot / branch / save is active.
///   • The outcome of the last completed mission (for the Overworld Scene).
///
/// This is intentionally thin. Heavy gameplay state (grid, wallet, minions)
/// is owned by scene-scoped managers and rebuilt on each scene load.
///
/// Attach to a persistent "GameSession" GameObject in the Title Scene
/// (or whichever scene loads first). DontDestroyOnLoad keeps it alive.
/// </summary>
public class GameSession : MonoBehaviour
{
    public static GameSession Instance { get; private set; }

    // ── Active session ─────────────────────────────────────────────────
    public int    ActiveSlot      { get; private set; } = 0;
    public string ActiveBranchId  { get; private set; } = "branch_0";
    public int    ActiveSaveIndex { get; private set; } = -1;
    public string ActiveLevelId   { get; private set; }

    // ── Last mission result (read by Overworld Scene) ──────────────────
    public MissionResult LastMissionResult { get; private set; }

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Called by the Overworld Scene before entering a mission.
    /// Sets up GameplaySceneArgs and records the active slot/branch.
    /// </summary>
    public void StartMission(int slot, string branchId, int saveIndex,
                             string levelId, bool isNewGame)
    {
        ActiveSlot      = slot;
        ActiveBranchId  = branchId;
        ActiveSaveIndex = saveIndex;
        ActiveLevelId   = levelId;

        GameplaySceneArgs.SlotIndex  = slot;
        GameplaySceneArgs.BranchId   = branchId;
        GameplaySceneArgs.SaveIndex  = saveIndex;
        GameplaySceneArgs.LevelId    = levelId;
        GameplaySceneArgs.IsNewGame  = isNewGame;
    }

    /// <summary>
    /// Called by GameManager2D when a mission completes.
    /// The Overworld Scene reads LastMissionResult on load.
    /// </summary>
    public void RecordMissionResult(MissionResult result)
    {
        LastMissionResult = result;
    }
}

/// <summary>
/// Data passed back to the Overworld Scene after a mission ends.
/// Extend as campaign mechanics are added.
/// </summary>
[System.Serializable]
public class MissionResult
{
    public string  levelId;
    public bool    success;
    public int     goldEarned;

    // ── Future ─────────────────────────────────────────────────────────
    // public int     score;
    // public float   completionTime;
    // public List<string> unlockedProvinces;
}
