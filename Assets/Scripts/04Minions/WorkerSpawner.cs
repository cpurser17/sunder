using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Spawns a faction's workers. Summoning is the Summon Worker spell (see
/// SummonWorkerEffect): the spell system handles the button, the click and
/// the gold, then asks this spawner whether a tile will do and to spawn
/// there. Its price — the SummonWorker row of the SpellData workbook — rises
/// with ActiveWorkerCount.
///
/// Scene setup
/// -----------
/// 1. Create child GO under GameManager. Name it "WorkerSpawner_Player".
/// 2. Attach WorkerSpawner. Set faction and gridManager. Leave workerPrefab
///    empty to use MinionSummoner's shared minion template.
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

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("References")]
    [Tooltip("Optional. Empty = MinionSummoner's shared minion template, which " +
             "is what you want. The Worker definition's own prefab overrides both.")]
    [FormerlySerializedAs("impPrefab")]
    [SerializeField] private GameObject    workerPrefab;
    [SerializeField] private GridManager2D gridManager;

    [Header("Placement")]
    [Tooltip("Gap kept between the spawned token edge and the tile boundary, "
             + "in world units.")]
    [SerializeField] private float spawnEdgeGap = 0.02f;

    // ── Runtime ────────────────────────────────────────────────────────
    private TraversalCapability? _prefabCapability;
    private bool _warnedNoWorkerData;
    private int  _activeWorkerCount;

    /// <summary>Workers this spawner has out — the Summon Worker price rises with it.</summary>
    public int ActiveWorkerCount => _activeWorkerCount;

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

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Whether a worker could be summoned onto this tile.</summary>
    public bool CanSpawnOn(GridCell cell)
    {
        if (gridManager == null && GameManager2D.Instance != null) gridManager = GameManager2D.Instance.Grid;
        return cell != null && gridManager != null && IsValidSpawnCell(cell);
    }

    /// <summary>
    /// Spawns a worker on the tile, as close to point (where the player
    /// clicked) as its token fits. False if it couldn't. Gold is the
    /// caller's business — see SummonWorkerEffect.
    /// </summary>
    public bool SpawnAt(GridCell cell, Vector3 point) =>
        CanSpawnOn(cell) && SpawnWorker(cell, point);

    // ── Spawning ───────────────────────────────────────────────────────

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

        Debug.Log($"[WorkerSpawner] Spawned worker {_activeWorkerCount} for {faction}.");
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
