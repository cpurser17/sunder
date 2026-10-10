using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// Handles worker summoning: cost calculation and click-to-place. Spawned
/// workers register themselves with their faction's WorkerTaskManager.
///
/// Cost formula: cost = b + k * c
///   b = baseCost     (default 25 — cost of the first worker)
///   c = costIncrement (default 25 — added per existing active worker)
///   k = current active worker count (0 when no workers exist)
///
/// Usage
/// -----
/// 1. Player clicks "Summon Worker" HUD button → sets IsSummonModeActive = true.
/// 2. Player left-clicks a valid world cell → worker spawns there, cost deducted.
/// 3. Right-click or pressing the button again cancels summon mode.
///
/// Scene setup
/// -----------
/// 1. Create child GO under GameManager. Name it "WorkerSpawner_Player".
/// 2. Attach WorkerSpawner. Set faction, gridManager, mainCamera. Leave workerPrefab
///    empty to use MinionSummoner's shared minion template.
/// 3. Wire the Summon Worker HUD button to ToggleSummonMode().
///
/// Data
/// ----
/// Workers are ordinary minions: the same template prefab as everything the
/// portal summons, configured from the faction's Worker MinionDefinition
/// (FactionDefinition.worker, filled in by Sunder > Import Minion Data from
/// the W row) — token, movement, stats — and running WorkerBehaviour. Only
/// how they arrive differs: placed by a click, for gold. A faction with no
/// Worker data spawns the template as authored, still as a worker.
/// </summary>
public class WorkerSpawner : MonoBehaviour
{
    // ── Per-faction registry ───────────────────────────────────────────
    // One spawner per faction, so a dying AI worker decrements the AI count
    // rather than the player's.
    private static readonly System.Collections.Generic.Dictionary<FactionID, WorkerSpawner>
        _registry = new();

    public static WorkerSpawner GetForFaction(FactionID faction) =>
        _registry.TryGetValue(faction, out var s) ? s : null;

    /// <summary>
    /// True while any spawner is awaiting a placement click. Other click
    /// handlers (dig selection) check this so a summon click is not also
    /// consumed as a drag-select.
    /// </summary>
    public static bool AnySummonModeActive
    {
        get
        {
            foreach (var s in _registry.Values)
                if (s != null && s.IsSummonModeActive) return true;
            return false;
        }
    }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("References")]
    [Tooltip("Optional. Empty = MinionSummoner's shared minion template, which " +
             "is what you want. The Worker definition's own prefab overrides both.")]
    [FormerlySerializedAs("impPrefab")]
    [SerializeField] private GameObject    workerPrefab;
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private Camera        mainCamera;

    [Header("Placement")]
    [Tooltip("Gap kept between the spawned token edge and the tile boundary, "
             + "in world units.")]
    [SerializeField] private float spawnEdgeGap = 0.02f;

    [Header("Cost Formula: cost = baseCost + activeWorkers * costIncrement")]
    [SerializeField] private int baseCost      = 25;
    [SerializeField] private int costIncrement = 25;

    // ── Runtime ────────────────────────────────────────────────────────
    private bool _summonModeActive;
    private TraversalCapability? _prefabCapability;
    private bool _warnedNoWorkerData;
    private int  _activeWorkerCount;

    public bool IsSummonModeActive => _summonModeActive;
    public int  CurrentSummonCost  => baseCost + _activeWorkerCount * costIncrement;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _registry[faction] = this;
    }

    private void OnDestroy()
    {
        if (_registry.TryGetValue(faction, out var s) && s == this)
            _registry.Remove(faction);
    }

    private void Update()
    {
        if (!_summonModeActive) return;

        // Cancel on right-click or Escape.
        if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
        {
            SetSummonMode(false);
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            // Mode stays open — the player can place several workers in a row and
            // leaves deliberately via right-click, Escape, or the button.
            TrySpawnWorker();
        }
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Toggles summon mode on/off. Wire to the Summon Worker HUD button.</summary>
    public void ToggleSummonMode() => SetSummonMode(!_summonModeActive);

    /// <summary>Exits summon mode on every faction's spawner.</summary>
    public static void CancelAllSummonModes()
    {
        foreach (var s in _registry.Values)
            if (s != null && s.IsSummonModeActive) s.SetSummonMode(false);
    }

    /// <summary>
    /// Enters or exits summon mode.
    ///
    /// Button highlighting is owned by HUDController2D so the summon button
    /// behaves identically to the buy/sell buttons. When the mode is exited
    /// from here — right-click or Escape rather than the button — the HUD is
    /// told so it can clear the highlight.
    /// </summary>
    public void SetSummonMode(bool active)
    {
        if (_summonModeActive == active) return;
        _summonModeActive = active;

        if (!active)
            HUDController2D.Instance?.NotifySummonModeExited(faction);
    }

    // ── Spawning ───────────────────────────────────────────────────────

    private void TrySpawnWorker()
    {
        // Check funds before attempting placement; pay after.
        var wallet = GameManager2D.Instance?.GetWallet(faction);
        if (wallet == null) return;

        int cost = CurrentSummonCost;
        // TODO (spells): when more spells are added, give every spell cast one
        // shared "can't afford it" path that announces "NotEnoughGold" (as
        // room/bridge purchases do in SelectionController2D.CommitBuy) — and
        // route this summon through it, rather than announcing here alone.
        if (cost <= 0 || wallet.Gold < cost)
        {
            Debug.Log($"[WorkerSpawner] Not enough gold. Need {cost}, have {wallet.Gold}.");
            return;
        }

        // Raycast to find click position.
        if (!TryGetGridCell(out int x, out int y, out Vector3 clickPoint)) return;

        var cell = gridManager.GetCell(x, y);
        if (cell == null || !IsValidSpawnCell(cell))
        {
            Debug.Log("[WorkerSpawner] Invalid spawn location — must be an owned tile " +
                      "or unclaimed Cave that the worker can stand on.");
            return;
        }

        // Paid only once the worker exists, so a failed spawn costs nothing
        // (gold can't simply be handed back — it may have come from the reserve).
        if (SpawnWorker(cell, clickPoint)) wallet.TrySpend(cost);
    }

    private bool SpawnWorker(GridCell cell, Vector3 clickPoint)
    {
        Vector3 centre = gridManager.CellToWorld(cell.X, cell.Y);

        var workerData = WorkerDefinition;
        if (workerData == null && !_warnedNoWorkerData)
        {
            _warnedNoWorkerData = true;
            Debug.LogWarning($"[WorkerSpawner] {faction} has no Worker data (no FactionDefinition, " +
                             "or no Worker row imported) — workers use the template's own token and values.");
        }

        if (Template == null)
        {
            Debug.LogError("[WorkerSpawner] No minion template — assign MinionSummoner's Minion " +
                           "Template (Sunder > Create Minion Template Prefab does this).");
            return false;
        }

        var go  = Instantiate(Template, centre, Quaternion.identity);
        go.name = $"Worker_{faction}_{_activeWorkerCount}";

        var worker = go.GetComponent<MinionController>();
        if (worker == null)
        {
            Debug.LogError($"[WorkerSpawner] {Template.name} is missing MinionController — " +
                           "rebuild it with Sunder > Create Minion Template Prefab.");
            Destroy(go);
            return false;
        }

        // Initialise first: it swaps in the faction's token and re-measures the
        // GridAgent radius, so the clamp below uses the real token size. The
        // worker registers itself with the faction's WorkerTaskManager.
        worker.Initialise(faction, workerData, 1, MinionController.SpawnSource.WorkerSpawner);
        go.transform.position = ClampInsideCell(clickPoint, cell, centre,
                                                go.GetComponent<GridAgent>());

        _activeWorkerCount++;

        Debug.Log($"[WorkerSpawner] Spawned worker {_activeWorkerCount} for {faction}. " +
                  $"Next cost: {CurrentSummonCost}g.");
        return true;
    }

    /// <summary>
    /// The prefab workers spawn from: the Worker definition's own override, else
    /// this spawner's workerPrefab, else MinionSummoner's shared template.
    /// </summary>
    private GameObject Template
    {
        get
        {
            var workerData = WorkerDefinition;
            if (workerData != null && workerData.prefab != null) return workerData.prefab;
            if (workerPrefab != null) return workerPrefab;
            return MinionSummoner.Instance != null ? MinionSummoner.Instance.Template : null;
        }
    }

    /// <summary>
    /// Valid spawn tiles are either:
    ///   - a tile this faction owns, or
    ///   - unclaimed Cave, so workers can be seeded into open cavern before it
    ///     has been claimed.
    ///
    /// In both cases the worker must actually be able to stand there. Ownership
    /// alone is not enough — Wall is owned but impassable, and a worker placed
    /// inside it would be stuck in solid rock. Passability comes from the
    /// Worker data's (or prefab's) own capability rather than being assumed, so
    /// this stays correct for factions whose workers are amphibious or flying.
    /// </summary>
    private bool IsValidSpawnCell(GridCell cell)
    {
        bool owned = cell.Owner == faction &&
                     gridManager.GetCategory(cell.TileType) == TileCategory.Owned;

        // Cave is Unaligned by definition, so it cannot be expressed as an
        // ownership test and needs its own branch.
        bool unclaimedCave = cell.TileType == TileType.Cave &&
                             cell.Owner    == FactionID.Unaligned;

        if (!owned && !unclaimedCave) return false;

        var capability = WorkerDefinition != null ? WorkerDefinition.movement : PrefabCapability;
        return TraversalRules.CanPathOn(cell.TileType, capability, faction);
    }

    /// <summary>
    /// This faction's Worker data, from the FactionDefinition its seat plays.
    /// Looked up each time (a dictionary hit) rather than cached, since seats
    /// are assigned during level setup, after this component's Awake. Null
    /// when the faction has no Worker row.
    /// </summary>
    private MinionDefinition WorkerDefinition =>
        GameManager2D.Instance?.GetFactionDefinition(faction)?.worker;

    /// <summary>
    /// Traversal capability declared on the template's GridAgent, used when
    /// the faction has no Worker data.
    /// Cached after the first read; falls back to LandOnly if absent.
    /// </summary>
    private TraversalCapability PrefabCapability
    {
        get
        {
            if (_prefabCapability.HasValue) return _prefabCapability.Value;

            var template = Template;
            var agent    = template != null ? template.GetComponent<GridAgent>() : null;
            _prefabCapability = agent != null ? agent.Capability
                                              : TraversalCapability.LandOnly;
            return _prefabCapability.Value;
        }
    }

    /// <summary>
    /// Resolves the click to a grid cell AND keeps the exact world point that
    /// was hit, so the worker can be placed where the player actually clicked
    /// rather than snapped to the middle of the tile.
    /// </summary>
    private bool TryGetGridCell(out int x, out int y, out Vector3 hitPoint)
    {
        x = y = 0;
        hitPoint = Vector3.zero;

        Ray          ray  = mainCamera.ScreenPointToRay(Input.mousePosition);
        RaycastHit[] hits = Physics.RaycastAll(ray);

        foreach (var hit in hits)
        {
            if (hit.collider.gameObject != gridManager.gameObject) continue;
            hitPoint = hit.point;
            return gridManager.WorldToCell(hit.point, out x, out y);
        }
        return false;
    }

    /// <summary>
    /// Pulls a click point inward so the spawned token sits fully inside its
    /// cell and clear of any wall.
    ///
    /// Two limits apply. The token must stay within the cell, or WorldToCell
    /// would report it in a neighbouring tile. It must also respect the cell's
    /// clearance, since clicking hard against the edge of a tile that backs
    /// onto stone would otherwise drop the token partly inside the rock.
    /// </summary>
    private Vector3 ClampInsideCell(Vector3 point, GridCell cell,
                                    Vector3 centre, GridAgent agent)
    {
        float radius = agent != null ? agent.Radius : 0f;
        float half   = gridManager.CellSize * 0.5f;

        float maxOffset = half - radius - spawnEdgeGap;

        // Clearance is measured from the cell centre to the nearest wall, so
        // subtracting the radius gives how far the centre may travel before the
        // token edge would touch. Applied symmetrically — conservative, but it
        // only bites in cells that actually border solid rock.
        var clearance = gridManager.Clearance;
        if (clearance != null && agent != null)
        {
            float c = clearance.GetClearance(cell, agent.Capability);
            maxOffset = Mathf.Min(maxOffset, c - radius);
        }

        maxOffset = Mathf.Max(0f, maxOffset);

        Vector3 offset = point - centre;
        offset.x = Mathf.Clamp(offset.x, -maxOffset, maxOffset);
        offset.z = Mathf.Clamp(offset.z, -maxOffset, maxOffset);
        offset.y = 0f;

        return centre + offset;
    }

    // ── Worker death notification ─────────────────────────────────────────

    /// <summary>Called by MinionController when one of this spawner's workers dies or converts.</summary>
    public void NotifyWorkerDied() => _activeWorkerCount = Mathf.Max(0, _activeWorkerCount - 1);

    /// <summary>Loading a save: a worker it spawned earlier is back — counts towards the summon cost.</summary>
    public void NotifyWorkerRestored() => _activeWorkerCount++;
}
