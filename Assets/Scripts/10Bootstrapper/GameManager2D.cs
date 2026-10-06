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
    [SerializeField] private FactionRegistry       factionRegistry;

    [Header("Editor Fallback")]
    [Tooltip("Level ID used when entering Play mode directly without a scene flow.")]
    [SerializeField] private string fallbackLevelId  = "level_01";
    [SerializeField] private int    fallbackWidth    = 20;
    [SerializeField] private int    fallbackHeight   = 20;
    [Tooltip("Starting gold used when no level file is found.")]
    [SerializeField] private int    fallbackGold     = 500;

    [Header("Determinism")]
    [Tooltip("Master random seed override for a NEW game (ignored on resume, which " +
             "always restores the seed from the save). 0 = generate one randomly. " +
             "Set nonzero to pin it for reproducible testing.")]
    [SerializeField] private int debugFixedSeed = 0;

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

    // ── Faction content ────────────────────────────────────────────────
    // Which FactionDefinition each seat is playing, resolved from
    // FactionSetup.contentFactionId whenever _activeFactions is (re)loaded.
    private readonly Dictionary<FactionID, FactionDefinition> _factionDefinitions = new();

    // ── Determinism ────────────────────────────────────────────────────
    /// <summary>
    /// This match's master random seed. Set once in StartNewGame — or restored
    /// from a save on resume, never regenerated — and used to derive every other
    /// random stream in the game (see DeriveFactionSeed), so any client that
    /// agrees on this one value agrees on every derived stream too.
    /// </summary>
    public int MasterSeed { get; private set; }

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

    /// <summary>Returns which FactionDefinition the given seat is playing, or null if unassigned.</summary>
    public FactionDefinition GetFactionDefinition(FactionID faction) =>
        _factionDefinitions.TryGetValue(faction, out var def) ? def : null;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        if (Instance == null) Instance = this;

        if (factionRegistry != null) factionRegistry.Initialise();

        // The Keeper's hand needs no scene wiring, so make sure one exists
        // rather than relying on it being placed by hand.
        if (FindAnyObjectByType<KeeperHand>() == null)
            gameObject.AddComponent<KeeperHand>();

        // Likewise the gold economy: Treasury storage and gold piles, and payday.
        if (FindAnyObjectByType<TreasuryManager>() == null)
            gameObject.AddComponent<TreasuryManager>();
        if (FindAnyObjectByType<PaydaySystem>() == null)
            gameObject.AddComponent<PaydaySystem>();

        // Lair beds, and warnings to human players ("Your lair is too small").
        if (FindAnyObjectByType<LairManager>() == null)
            gameObject.AddComponent<LairManager>();
        if (FindAnyObjectByType<Announcer>() == null)
            gameObject.AddComponent<Announcer>();
        if (FindAnyObjectByType<HatcheryManager>() == null)
            gameObject.AddComponent<HatcheryManager>();
        if (FindAnyObjectByType<RoomWorkManager>() == null)
            gameObject.AddComponent<RoomWorkManager>();
        if (FindAnyObjectByType<MinionHealthBars>() == null)
            gameObject.AddComponent<MinionHealthBars>();
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

        // A freshly loaded grid has no gold piles yet (a save restores them after),
        // and no chickens.
        TreasuryManager.Instance?.ResetState();
        HatcheryManager.Instance?.ResetState();

        if (factions == null || factions.Count == 0)
        {
            // Fallback: single player wallet.
            CreateWallet(FactionID.Player, levelDefaultGold);
            CreateResearch(FactionID.Player);
            Announcer.Instance?.SetHumanPlayers(new[] { FactionID.Player });
            OnWalletsReady?.Invoke();
            return;
        }

        var humans = new List<FactionID>();
        foreach (var setup in factions)
        {
            int gold = setup.startingGold > 0 ? setup.startingGold : levelDefaultGold;
            CreateWallet(setup.factionId, gold);
            CreateResearch(setup.factionId);
            if (setup.isHuman) humans.Add(setup.factionId);
        }
        if (humans.Count == 0) humans.Add(FactionID.Player);
        Announcer.Instance?.SetHumanPlayers(humans);

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

        // loadAuto must be checked before the saveIndex<0 fallback below —
        // an autosave load doesn't need a specific SaveIndex (it's one fixed
        // file per slot, keyed by AutosavePath(slot) alone), and SaveIndex
        // defaults to -1. Checking saveIndex<0 first meant a caller asking
        // to load the autosave silently got a new game instead unless it
        // also happened to set a non-negative SaveIndex for no real reason.
        if (isNewGame)
            StartNewGame(_activeLevelId);
        else if (loadAuto)
            LoadAutosave();
        else if (_activeSaveIndex < 0)
            StartNewGame(_activeLevelId);
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
        MasterSeed           = debugFixedSeed != 0 ? debugFixedSeed : GenerateRandomMasterSeed();

        var level = SaveLoadSystem.LoadLevel(levelId);
        if (level == null)
        {
            Debug.LogWarning($"[GameManager2D] Level '{levelId}' not found. " +
                             $"Using empty {fallbackWidth}x{fallbackHeight} grid.");
            _activeLevelId    = string.IsNullOrEmpty(levelId) ? "unknown" : levelId;
            _activeFactions   = new List<FactionSetup>();
            AllowedMinionIds  = new List<string>();
            RebuildFactionDefinitions();
            gridManager.Initialise(fallbackWidth, fallbackHeight);
            InitialiseWallets(null, fallbackGold);
            return;
        }

        _activeLevelId   = level.levelId;
        _activeFactions  = level.factions        ?? new List<FactionSetup>();
        AllowedMinionIds = level.allowedMinionIds ?? new List<string>();
        RebuildFactionDefinitions();
        SaveLoadSystem.ApplyGrid(gridManager, level.grid);
        InitialiseWallets(_activeFactions, level.startingGold);
        SpawnPlacedMinions(level.minions);

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
        RestoreMasterSeed(save);

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
        RestoreMasterSeed(save);

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
    /// Spawns the minions the level editor placed, from MinionSummoner's
    /// shared template, as already part of their team (SpawnSource.Placed —
    /// they don't count against a summoning population). New games only:
    /// saves don't record minions yet, so a resumed game has none either way.
    /// </summary>
    private void SpawnPlacedMinions(List<MinionPlacement> placements)
    {
        if (placements == null || placements.Count == 0) return;

        var template = MinionSummoner.Instance != null ? MinionSummoner.Instance.Template : null;
        int spawned  = 0;
        foreach (var p in placements)
        {
            var def = factionRegistry != null
                ? factionRegistry.FindMinion(p.contentFactionId, p.minionId)
                : null;
            if (def == null)
            {
                Debug.LogWarning($"[GameManager2D] Placed minion {p.contentFactionId} {p.minionId} " +
                                 "not found in the Faction Registry — skipped.");
                continue;
            }

            var prefab = def.prefab != null ? def.prefab : template;
            if (prefab == null)
            {
                Debug.LogError("[GameManager2D] No minion template (MinionSummoner) to spawn placed minions from.");
                return;
            }

            Vector3 pos = gridManager.transform.position +
                          new Vector3(p.x * gridManager.CellSize, 0f, p.y * gridManager.CellSize);
            var go = Instantiate(prefab, pos, Quaternion.identity);
            go.name = $"{def.minionId}_{p.factionId}_placed{spawned}";

            if (!go.TryGetComponent(out MinionController minion))
            {
                Debug.LogError($"[GameManager2D] {prefab.name} is missing MinionController.");
                Destroy(go);
                continue;
            }
            minion.Initialise(p.factionId, def, Mathf.Max(1, p.level), MinionController.SpawnSource.Placed);
            spawned++;
        }

        Debug.Log($"[GameManager2D] Spawned {spawned}/{placements.Count} placed minion(s).");
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
        RebuildFactionDefinitions();
    }

    /// <summary>
    /// Resolves each active seat's FactionSetup.contentFactionId through
    /// factionRegistry, so GetFactionDefinition(seat) is ready before
    /// DungeonHeart/MinionSummoner read it off OnWalletsReady.
    /// </summary>
    private void RebuildFactionDefinitions()
    {
        _factionDefinitions.Clear();
        if (factionRegistry == null) return;

        foreach (var setup in _activeFactions)
        {
            if (string.IsNullOrEmpty(setup.contentFactionId)) continue;

            var def = factionRegistry.GetDefinition(setup.contentFactionId);
            if (def != null)
                _factionDefinitions[setup.factionId] = def;
            else
                Debug.LogWarning($"[GameManager2D] No FactionDefinition found for " +
                                 $"contentFactionId '{setup.contentFactionId}' (seat {setup.factionId}).");
        }
    }

    /// <summary>
    /// Restores MasterSeed from a save rather than regenerating it, so resuming
    /// reproduces the same derived random streams as the run being resumed. A
    /// save from before this system existed has no seed on file (masterSeed == 0)
    /// and falls back to a fresh random one — those old saves had no guaranteed
    /// determinism to preserve in the first place.
    /// </summary>
    private void RestoreMasterSeed(SaveData save)
    {
        MasterSeed = save.gameState != null && save.gameState.masterSeed != 0
            ? save.gameState.masterSeed
            : GenerateRandomMasterSeed();
    }

    /// <summary>
    /// Non-deterministic on purpose — used only to pick a NEW match's master
    /// seed. Once chosen it is persisted, so every derived random stream (and
    /// every subsequent resume) is fully deterministic from this one value.
    /// </summary>
    private static int GenerateRandomMasterSeed() =>
        BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0);

    /// <summary>
    /// Deterministically derives a per-faction, per-system seed from MasterSeed.
    /// Systems that each just used MasterSeed directly would end up with
    /// correlated random streams (and factions sharing MasterSeed alone would
    /// roll identically); folding in the faction and a purpose tag keeps every
    /// stream independent while still reproducing identically for a given
    /// MasterSeed — which is what makes this safe for lockstep multiplayer.
    /// </summary>
    public int DeriveFactionSeed(FactionID faction, string purpose)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + MasterSeed;
            hash = hash * 31 + (int)faction;
            foreach (char c in purpose)
                hash = hash * 31 + c;
            return hash;
        }
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

        // Restore per-faction gold. A save from before Treasury storage has
        // only each faction's total, which becomes its reserve; one from
        // before per-faction wallets gives currentGold to the Player.
        var gs = save.gameState;
        if (gs.factionReserve != null)
        {
            foreach (var pair in gs.factionReserve)
                if (_wallets.TryGetValue(pair.Key, out var wallet))
                    wallet.SetReserve(pair.Value);
            TreasuryManager.Instance?.Restore(gs.treasuryGold, gs.goldPiles);
        }
        else if (gs.factionGold != null)
        {
            foreach (var pair in gs.factionGold)
                if (_wallets.TryGetValue(pair.Key, out var wallet))
                    wallet.SetReserve(pair.Value);
        }
        else
        {
            // Legacy single-wallet save — give gold to Player.
            GetWallet(FactionID.Player)?.SetReserve(gs.currentGold);
        }

        PaydaySystem.Instance?.Restore(gs.paydays);
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

        // Per-faction gold: the total (for older readers), the unspent
        // reserve, and where the rest physically is.
        data.factionGold    = new Dictionary<FactionID, int>();
        data.factionReserve = new Dictionary<FactionID, int>();
        foreach (var pair in _wallets)
        {
            data.factionGold[pair.Key]    = pair.Value.Gold;
            data.factionReserve[pair.Key] = pair.Value.Reserve;
        }
        data.treasuryGold = TreasuryManager.Instance?.CaptureTreasuryGold();
        data.goldPiles    = TreasuryManager.Instance?.CapturePiles();
        data.paydays      = PaydaySystem.Instance?.Capture();

        // Populate per-faction Dungeon Heart HP.
        data.factionHeartHP = DungeonHeart.Instance != null
            ? DungeonHeart.Instance.SnapshotHP()
            : new Dictionary<FactionID, int>();

        // Keep currentGold as the player's gold for backwards compatibility.
        data.currentGold = GetWallet(FactionID.Player)?.Gold ?? 0;

        data.masterSeed = MasterSeed;

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
