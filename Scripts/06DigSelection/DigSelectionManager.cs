using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns and owns one DigSelectionController per active faction.
/// Reads the faction list from LevelData via GameManager2D at Start().
///
/// Only localPlayer's controller reads mouse input on this client — every
/// other faction's (AI today; another human's own client, eventually) sits
/// idle, since driving a shared mouse from every controller at once queued
/// every faction's dig orders from the same drag.
///
/// Selection box GOs must exist in the scene BEFORE Play mode so their
/// SelectionBoxVisuals Inspector values (Pillar Height, Base Y, Alpha) are
/// serialised and persist. Create one child GO per faction under this GO,
/// attach SelectionBoxVisuals to each, and wire them into factionBoxEntries.
///
/// Scene setup
/// -----------
/// 1. Create an empty child GO under GameManager. Name it "DigSelectionManager".
/// 2. Attach this component.
/// 3. Assign gridManager and mainCamera.
/// 4. For each faction (Player, AI1, AI2 etc.):
///      a. Create a child GO under DigSelectionManager. Name it e.g. "DigBox_Player".
///      b. Attach SelectionBoxVisuals to it. Tune Pillar Height / Base Y / Alpha.
///      c. Add an entry to Faction Box Entries: set Faction and drag the GO's
///         SelectionBoxVisuals into the Box field.
/// 5. Tune Marker Height, Default Marker Colour, Add Box Colour, Remove Box Colour.
/// </summary>
public class DigSelectionManager : MonoBehaviour
{
    public static DigSelectionManager Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private Camera        mainCamera;

    [Header("Local Player")]
    [Tooltip("Only this faction's controller reads mouse input on this client. " +
             "Every other faction's controller — AI or, eventually, another " +
             "human player's own client — sits idle until something calls " +
             "QueueCell/DequeueCell on it directly.")]
    [SerializeField] private FactionID localPlayer = FactionID.Player;

    [Header("Marker Defaults")]
    [Tooltip("Height above grid at which dig markers are placed.")]
    [SerializeField] private float markerHeight        = 3.5f;
    [Tooltip("Fallback marker colour when a faction has no colourHex set.")]
    [SerializeField] private Color defaultMarkerColour = new Color(1f, 0.5f, 0f, 0.6f);

    [Header("Selection Box Colours")]
    [Tooltip("Pillar colour while dragging to ADD tiles (LMB).")]
    [SerializeField] private Color addBoxColour    = new Color(1f, 0.5f, 0f, 0.5f);
    [Tooltip("Pillar colour while dragging to REMOVE tiles (RMB).")]
    [SerializeField] private Color removeBoxColour = new Color(0.5f, 0.5f, 0.5f, 0.5f);

    [Header("Faction Selection Boxes")]
    [Tooltip("Pre-existing scene GOs with SelectionBoxVisuals, one per faction. " +
             "These must exist before Play mode so their Inspector values persist.")]
    [SerializeField] private List<FactionBoxEntry> factionBoxEntries = new();

    // ── Runtime ────────────────────────────────────────────────────────
    private readonly Dictionary<FactionID, DigSelectionController> _controllers = new();

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        if (Instance == null) Instance = this;
    }

    private void Start()
    {
        // Faction data is populated by GameManager2D after level load, which
        // may happen after this Start(). Subscribe to OnWalletsReady which
        // fires once the level is fully loaded and factions are set.
        GameManager2D.OnWalletsReady += SpawnControllers;

        // If GameManager2D already finished loading before we subscribed,
        // call immediately.
        if (GameManager2D.Instance?.ActiveFactions?.Count > 0)
        {
            GameManager2D.OnWalletsReady -= SpawnControllers;
            SpawnControllers();
        }
    }

    private void OnDestroy()
    {
        GameManager2D.OnWalletsReady -= SpawnControllers;
    }

    private void SpawnControllers()
    {
        GameManager2D.OnWalletsReady -= SpawnControllers;

        var factions = GameManager2D.Instance?.ActiveFactions;

        if (factions == null || factions.Count == 0)
        {
            Debug.LogWarning("[DigSelectionManager] No faction data found. " +
                             "Spawning Player controller as fallback.");
            SpawnController(FactionID.Player, defaultMarkerColour);
            return;
        }

        foreach (var setup in factions)
        {
            Color colour    = ParseColour(setup.colourHex, defaultMarkerColour);
            Color markerCol = Color.Lerp(colour, defaultMarkerColour, 0.5f);
            markerCol.a     = 0.65f;
            SpawnController(setup.factionId, markerCol);
        }

        Debug.Log($"[DigSelectionManager] Spawned {_controllers.Count} controller(s).");
    }

    // ── Spawning ───────────────────────────────────────────────────────

    private void SpawnController(FactionID faction, Color markerColour)
    {
        if (_controllers.ContainsKey(faction))
        {
            Debug.LogWarning($"[DigSelectionManager] Controller for {faction} " +
                             "already exists. Skipping.");
            return;
        }

        // Look up the pre-existing scene box for this faction.
        SelectionBoxVisuals box = FindBoxForFaction(faction);
        if (box == null)
        {
            Debug.LogWarning($"[DigSelectionManager] No SelectionBoxVisuals entry " +
                             $"found for faction {faction}. Selection box will be absent " +
                             "for this faction. Add an entry to Faction Box Entries.");
        }

        // Controller GO is still created at runtime — only the box GO needs
        // to be pre-existing so its Inspector values are serialised.
        var go   = new GameObject($"DigSelection_{faction}");
        go.transform.SetParent(transform, false);

        var ctrl = go.AddComponent<DigSelectionController>();
        ctrl.Initialise(faction, faction == localPlayer, gridManager, mainCamera,
                        markerHeight, markerColour, box, addBoxColour, removeBoxColour);

        _controllers[faction] = ctrl;
    }

    private SelectionBoxVisuals FindBoxForFaction(FactionID faction)
    {
        foreach (var entry in factionBoxEntries)
            if (entry.faction == faction) return entry.box;
        return null;
    }

    // ── Public API ─────────────────────────────────────────────────────

    public DigSelectionController GetController(FactionID faction) =>
        _controllers.TryGetValue(faction, out var ctrl) ? ctrl : null;

    public bool IsQueuedByAnyFaction(GridCell cell)
    {
        foreach (var ctrl in _controllers.Values)
            if (ctrl.IsQueued(cell)) return true;
        return false;
    }

    public IReadOnlyDictionary<FactionID, DigSelectionController> Controllers =>
        _controllers;

    // ── Helpers ────────────────────────────────────────────────────────

    private static Color ParseColour(string hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        if (ColorUtility.TryParseHtmlString(hex, out Color col)) return col;
        Debug.LogWarning($"[DigSelectionManager] Could not parse '{hex}'. Using fallback.");
        return fallback;
    }
}

/// <summary>Maps a FactionID to its pre-existing scene SelectionBoxVisuals.</summary>
[System.Serializable]
public class FactionBoxEntry
{
    public FactionID          faction;
    public SelectionBoxVisuals box;
}
