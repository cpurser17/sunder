using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mission-scoped bootstrapper for the Gameplay Scene.
/// No DontDestroyOnLoad — lives and dies with the Gameplay Scene.
///
/// Manages one FactionWallet per active faction. Use GetWallet(FactionID)
/// to access any faction's gold from other systems.
///
/// Save routing
/// ------------
/// Autosave()   → always overwrites autosave.json
/// Save()       → overwrite autosave / overwrite tip / fork mid-branch
/// SaveAs()     → append to current branch / same as Save mid-branch for now
/// </summary>
public class GameManager2D : MonoBehaviour
{
    public static GameManager2D Instance { get; private set; }

    /// <summary>
    /// Fired after all faction wallets have been created and initialised.
    /// HUDController2D subscribes to this to safely connect the gold display.
    /// </summary>
    public static event System.Action OnWalletsReady;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Core Systems")]
    [SerializeField] private GridManager2D         gridManager;
    [SerializeField] private SelectionController2D selectionController;
    [SerializeField] private HUDController2D       hudController;
    [SerializeField] private TileVisualizer3D      tileVisualizer;

    [Header("Editor Fallback")]
    [Tooltip("Level ID used when entering Play mode directly without a scene flow.")]
    [SerializeField] private string fallbackLevelId  = "level_01";
    [SerializeField] private int    fallbackWidth    = 20;
    [SerializeField] private int    fallbackHeight   = 20;
    [Tooltip("Starting gold used when no level file is found.")]
    [SerializeField] private int    fallbackGold     = 500;

    // ── Runtime session state ──────────────────────────────────────────
    private int           _activeSlot;
    private string        _activeBranchId;
    private int           _activeSaveIndex;
    private string        _activeLevelId;
    private bool          _loadedFromAutosave;
    private bool          _loadedFromBranchTip;
    private List<FactionSetup> _activeFactions = new();

    // ── Wallets ────────────────────────────────────────────────────────
    private readonly Dictionary<FactionID, FactionWallet> _wallets = new();

    // ── Research ───────────────────────────────────────────────────────
    private readonly Dictionary<FactionID, FactionResearchState> _research = new();

    // ── Minions ────────────────────────────────────────────────────────
    /// <summary>Mission-wide summon restriction from the active level. Empty = no restriction.</summary>
    public List<string> AllowedMinionIds { get; private set; } = new();

    // ── Accessors ──────────────────────────────────────────────────────
    public GridManager2D         Grid           => gridManager;
    public SelectionController2D Selection      => selectionController;
    public TileVisualizer3D      TileVisualizer => tileVisualizer;
    public int                   ActiveSlot     => _activeSlot;
    public string                ActiveBranchId => _activeBranchId;
    public int                   ActiveSaveIndex => _activeSaveIndex;
    public List<FactionSetup>    ActiveFactions  => _activeFactions;

    /// <summary>
    /// Returns the wallet for the specified faction, or null if not active.
    /// </summary>
    public FactionWallet GetWallet(FactionID faction) =>
        _wallets.TryGetValue(faction, out var w) ? w : null;

    /// <summary>Convenience accessor for the local player's wallet.</summary>
    public FactionWallet PlayerWallet => GetWallet(FactionID.Player);

    /// <summary>Returns the research state for the specified faction, or null if not active.</summary>
    public FactionResearchState GetResearch(FactionID faction) =>
        _research.TryGetValue(faction, out var r) ? r : null;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        if (Instance == null) Instance = this;
    }

    private void Start()
    {
        gridManager.OnRebakeComplete += OnRebakeComplete;
        DungeonHeart.OnFactionEliminated += OnFactionEliminated;
        LoadFromArgs();
    }

    private void OnDestroy()
    {
        if (gridManager != null)
            gridManager.OnRebakeComplete -= OnRebakeComplete;
        DungeonHeart.OnFactionEliminated -= OnFactionEliminated;
    }

    // ── Wallet management ──────────────────────────────────────────────

    private void InitialiseWallets(List<FactionSetup> factions, int levelDefaultGold)
    {
        // Destroy any wallets/research from a previous load.
        foreach (var w in _wallets.Values)
            if (w != null) Destroy(w.gameObject);
        _wallets.Clear();

        foreach (var r in _research.Values)
            if (r != null) Destroy(r.gameObject);
        _research.Clear();

        if (factions == null || factions.Count == 0)
        {
            // Fallback: single player wallet.
            CreateWallet(FactionID.Player, levelDefaultGold);
            CreateResearch(FactionID.Player);
            OnWalletsReady?.Invoke();
            return;
        }

        foreach (var setup in factions)
        {
            int gold = setup.startingGold > 0 ? setup.startingGold : levelDefaultGold;
            CreateWallet(setup.factionId, gold);
            CreateResearch(setup.factionId);
        }

        OnWalletsReady?.Invoke();
    }

    private void CreateWallet(FactionID faction, int startingGold)
    {
        var go     = new GameObject($"Wallet_{faction}");
        go.transform.SetParent(transform, false);
        var wallet = go.AddComponent<FactionWallet>();
        wallet.Initialise(faction, startingGold);
        _wallets[faction] = wallet;
    }

    private void CreateResearch(FactionID faction)
    {
        var go       = new GameObject($"Research_{faction}");
        go.transform.SetParent(transform, false);
        var research = go.AddComponent<FactionResearchState>();
        research.Initialise(faction);
        _research[faction] = research;
    }

    // ── Scene entry ────────────────────────────────────────────────────

    private void LoadFromArgs()
    {
        _activeSlot      = GameplaySceneArgs.SlotIndex;
        _activeBranchId  = GameplaySceneArgs.BranchId;
        _activeSaveIndex = GameplaySceneArgs.SaveIndex;
        _activeLevelId   = GameplaySceneArgs.LevelId ?? fallbackLevelId;
        bool isNewGame   = GameplaySceneArgs.IsNewGame;
        bool loadAuto    = GameplaySceneArgs.LoadAutosave;

        GameplaySceneArgs.Clear();
        EnsureSlotInitialised();

        if (isNewGame || _activeSaveIndex < 0)
            StartNewGame(_activeLevelId);
        else if (loadAuto)
            LoadAutosave();
        else
            ResumeFromSave(_activeSlot, _activeBranchId, _activeSaveIndex);
    }

    private void EnsureSlotInitialised()
    {
        var manifest = SaveLoadSystem.LoadManifest(_activeSlot);
        if (manifest == null)
        {
            SaveLoadSystem.InitialiseSlot(_activeSlot);
            Debug.Log($"[GameManager2D] Initialised slot {_activeSlot}.");
            return;
        }
        if (manifest.GetBranch(_activeBranchId) == null)
        {
            Debug.LogWarning($"[GameManager2D] Branch '{_activeBranchId}' missing. " +
                             "Falling back to branch_0.");
            _activeBranchId = "branch_0";
        }
    }

    // ── Load paths ─────────────────────────────────────────────────────

    private void StartNewGame(string levelId)
    {
        _loadedFromAutosave  = false;
        _loadedFromBranchTip = false;
        _activeSaveIndex     = -1;

        var level = SaveLoadSystem.LoadLevel(levelId);
        if (level == null)
        {
            Debug.LogWarning($"[GameManager2D] Level '{levelId}' not found. " +
                             $"Using empty {fallbackWidth}x{fallbackHeight} grid.");
            _activeLevelId    = string.IsNullOrEmpty(levelId) ? "unknown" : levelId;
            _activeFactions   = new List<FactionSetup>();
            AllowedMinionIds  = new List<string>();
            gridManager.Initialise(fallbackWidth, fallbackHeight);
            InitialiseWallets(null, fallbackGold);
            return;
        }

        _activeLevelId   = level.levelId;
        _activeFactions  = level.factions        ?? new List<FactionSetup>();
        AllowedMinionIds = level.allowedMinionIds ?? new List<string>();
        SaveLoadSystem.ApplyGrid(gridManager, level.grid);
        InitialiseWallets(_activeFactions, level.startingGold);

        Debug.Log($"[GameManager2D] Started '{level.displayName}' " +
                  $"with {_activeFactions.Count} faction(s).");
    }

    private void LoadAutosave()
    {
        var save = SaveLoadSystem.LoadAutosave(_activeSlot);
        if (save == null) { StartNewGame(_activeLevelId); return; }

        _loadedFromAutosave  = true;
        _loadedFromBranchTip = false;
        _activeLevelId       = save.levelId;
        _activeSaveIndex     = -1;

        LoadLevelMeta(_activeLevelId);
        SaveLoadSystem.ApplyGrid(gridManager, save.grid);
        RestoreWalletsFromSave(save);
        Debug.Log($"[GameManager2D] Loaded autosave for slot {_activeSlot}.");
    }

    private void ResumeFromSave(int slot, string branchId, int saveIndex)
    {
        var save = SaveLoadSystem.LoadSave(slot, branchId, saveIndex);
        if (save == null) { StartNewGame(_activeLevelId); return; }

        _loadedFromAutosave = false;
        _activeLevelId      = save.levelId;
        _activeSaveIndex    = saveIndex;

        var manifest = SaveLoadSystem.LoadManifest(slot);
        _loadedFromBranchTip = manifest != null &&
                               manifest.IsBranchTip(branchId, saveIndex);

        LoadLevelMeta(_activeLevelId);
        SaveLoadSystem.ApplyGrid(gridManager, save.grid);
        RestoreWalletsFromSave(save);

        Debug.Log($"[GameManager2D] Resumed slot={slot} branch={branchId} " +
                  $"index={saveIndex} (tip={_loadedFromBranchTip}).");
    }

    /// <summary>
    /// Re-reads mission config (active factions, allowed minions) from the
    /// originating level file. A save only stores game STATE, not mission
    /// design, so resuming one has to go back to the level for this — without
    /// it, _activeFactions previously stayed at whatever StartNewGame last
    /// left it (empty on a cold resume), silently dropping every AI faction's
    /// wallet on load.
    /// </summary>
    private void LoadLevelMeta(string levelId)
    {
        var level = SaveLoadSystem.LoadLevel(levelId);
        _activeFactions  = level?.factions        ?? new List<FactionSetup>();
        AllowedMinionIds = level?.allowedMinionIds ?? new List<string>();
    }

    /// <summary>
    /// Restores wallet balances from a save file.
    /// Falls back to the level default if a faction has no saved gold.
    /// </summary>
    private void RestoreWalletsFromSave(SaveData save)
    {
        // Re-initialise wallets from faction setup (gold set below).
        InitialiseWallets(_activeFactions, 0);

        if (save.gameState == null) return;

        // Restore per-faction gold. If save predates per-faction wallets,
        // currentGold goes to the Player wallet as a safe fallback.
        if (save.gameState.factionGold != null)
        {
            foreach (var pair in save.gameState.factionGold)
            {
                if (_wallets.TryGetValue(pair.Key, out var wallet))
                    wallet.SetGold(pair.Value);
            }
        }
        else
        {
            // Legacy single-wallet save — give gold to Player.
            GetWallet(FactionID.Player)?.SetGold(save.gameState.currentGold);
        }

        DungeonHeart.Instance?.RestoreAll(save.gameState.factionHeartHP);
    }

    // ── Save routing ───────────────────────────────────────────────────

    public void Save(string displayName = "", Texture2D thumbnail = null)
    {
        if (_loadedFromAutosave)
        {
            PerformAutosave(thumbnail);
            return;
        }
        if (_activeSaveIndex < 0 || _loadedFromBranchTip)
        {
            if (_activeSaveIndex >= 0 && _loadedFromBranchTip)
                PerformOverwrite(displayName, thumbnail);
            else
                PerformAppend(displayName, thumbnail);
            return;
        }
        PerformFork(displayName, thumbnail);
    }

    public void SaveAs(string displayName = "", Texture2D thumbnail = null)
    {
        if (_loadedFromAutosave)
        {
            PerformAppend(displayName, thumbnail);
            return;
        }
        if (_activeSaveIndex >= 0 && !_loadedFromBranchTip)
        {
            Debug.Log("[GameManager2D] SaveAs mid-branch: behaving as Save " +
                      "until UI chooser is implemented.");
            Save(displayName, thumbnail);
            return;
        }
        PerformAppend(displayName, thumbnail);
    }

    public void Autosave(Texture2D thumbnail = null) => PerformAutosave(thumbnail);

    // ── Private save implementations ───────────────────────────────────

    private void PerformAutosave(Texture2D thumbnail)
    {
        SaveLoadSystem.WriteAutosave(BuildSaveData("Autosave"), thumbnail);
    }

    private void PerformOverwrite(string displayName, Texture2D thumbnail)
    {
        bool ok = SaveLoadSystem.OverwriteSave(BuildSaveData(displayName), thumbnail);
        if (ok)
        {
            _loadedFromBranchTip = true;
            Debug.Log($"[GameManager2D] Overwrote save index {_activeSaveIndex}.");
            return;
        }

        // Target file was missing — AppendSave picks a fresh index and
        // PerformAppend keeps our session state (index/branch-tip/autosave
        // flags) in sync with it, instead of silently drifting from the
        // manifest's real active save.
        Debug.LogWarning("[GameManager2D] Overwrite target missing; appending instead.");
        PerformAppend(displayName, thumbnail);
    }

    private void PerformAppend(string displayName, Texture2D thumbnail)
    {
        int index = SaveLoadSystem.AppendSave(BuildSaveData(displayName), thumbnail);
        if (index >= 0)
        {
            _activeSaveIndex    = index;
            _loadedFromBranchTip = true;
            _loadedFromAutosave  = false;
        }
        Debug.Log($"[GameManager2D] Appended save index {index}.");
    }

    private void PerformFork(string displayName, Texture2D thumbnail)
    {
        string newBranch = SaveLoadSystem.ForkBranch(
            _activeSlot, _activeBranchId, _activeSaveIndex,
            $"Fork at {_activeBranchId}[{_activeSaveIndex}]");

        if (newBranch == null) return;

        _activeBranchId      = newBranch;
        _activeSaveIndex     = 0;
        _loadedFromBranchTip = true;

        PerformAppend(displayName, thumbnail);
        Debug.Log($"[GameManager2D] Forked to branch '{newBranch}'.");
    }

    private SaveData BuildSaveData(string displayName) => new SaveData
    {
        levelId     = _activeLevelId,
        displayName = string.IsNullOrEmpty(displayName)
                        ? $"{_activeLevelId} — {DateTime.Now:HH:mm dd/MM/yy}"
                        : displayName,
        slotIndex   = _activeSlot,
        branchId    = _activeBranchId,
        saveIndex   = _activeSaveIndex,
        grid        = SaveLoadSystem.CaptureGrid(gridManager),
        gameState   = BuildGameStateSaveData(),
    };

    private GameStateSaveData BuildGameStateSaveData()
    {
        var data = new GameStateSaveData();

        // Populate per-faction gold dictionary.
        data.factionGold = new Dictionary<FactionID, int>();
        foreach (var pair in _wallets)
            data.factionGold[pair.Key] = pair.Value.Gold;

        // Populate per-faction Dungeon Heart HP.
        data.factionHeartHP = DungeonHeart.Instance != null
            ? DungeonHeart.Instance.SnapshotHP()
            : new Dictionary<FactionID, int>();

        // Keep currentGold as the player's gold for backwards compatibility.
        data.currentGold = GetWallet(FactionID.Player)?.Gold ?? 0;

        return data;
    }

    // ── Rebake callback ────────────────────────────────────────────────

    private void OnRebakeComplete(IReadOnlyList<DungeonRoom> rooms)
    {
        Debug.Log($"[GameManager2D] Rebake — {rooms.Count} room(s).");
        // TODO: 3DAssetLayer.Refresh(rooms)
        // TODO: MinimapRenderer.Refresh(rooms)
        // TODO: NavMesh rebake
    }

    private void OnFactionEliminated(FactionID faction)
    {
        Debug.Log($"[GameManager2D] Faction {faction} eliminated — Dungeon Heart destroyed.");
        // TODO: win/loss screen, AI shutdown, session end.
    }
}
