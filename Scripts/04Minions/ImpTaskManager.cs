using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Job registry and dispatcher for one faction.
///
/// Pull, not push
/// --------------
/// Imps ask for work when they go idle; jobs are never pushed onto a specific
/// imp. That is what makes an imp take the job nearest to itself rather than
/// whichever job happened to be registered first — the previous push model
/// walked the job dictionary in insertion order, so every imp serviced the map
/// in the same fixed sequence regardless of where it stood.
///
/// Job sources
/// -----------
///   Dig       Stone / Wall / Gold / Gem marked by the player, one job per
///             accessible side, several imps per side
///   Claim     unclaimed Cave adjacent to owned territory, or an enemy-owned
///             tile adjacent to owned territory
///   Reinforce Stone adjacent to owned territory
///
/// Maintenance
/// -----------
/// The list is updated incrementally: any tile change re-evaluates that cell
/// and its orthogonal neighbours, since adjacency rules mean a change can make
/// a neighbour eligible or ineligible. Dig selection changes re-evaluate the
/// marked cell. A full sweep runs once at startup.
/// </summary>
public class ImpTaskManager : MonoBehaviour
{
    // ── Per-faction registry ───────────────────────────────────────────
    private static readonly Dictionary<FactionID, ImpTaskManager> _registry = new();

    public static ImpTaskManager GetForFaction(FactionID faction) =>
        _registry.TryGetValue(faction, out var mgr) ? mgr : null;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Capacity")]
    [Tooltip("Imps that can work one side of a dig target at the same time.")]
    [SerializeField] private int impsPerDigSide   = 3;
    [Tooltip("Imps on a single claim job. Claiming converts one tile, so 1 is normal.")]
    [SerializeField] private int impsPerClaim     = 1;
    [Tooltip("Imps on a single reinforce job.")]
    [SerializeField] private int impsPerReinforce = 1;

    [Header("Job selection")]
    [Tooltip("How many of the nearest jobs an imp chooses between. 1 = always " +
             "strictly nearest and fully predictable; higher spreads imps out.")]
    [SerializeField] private int considerNearest = 4;

    [Tooltip("How sharply distance dominates. Higher = stronger preference for " +
             "close work. 0 = distance ignored.")]
    [SerializeField] private float distanceBias = 2f;

    [Header("Type weighting")]
    [Tooltip("Relative pull of each job type. Applied on top of distance, so a " +
             "far high-priority job still loses to close work.")]
    [SerializeField] private float digWeight       = 3f;
    [SerializeField] private float claimWeight     = 2f;
    [SerializeField] private float reinforceWeight = 1f;

    [Header("Determinism")]
    [Tooltip("Seed for job-choice randomness. Fixed so runs are reproducible — " +
             "important for lockstep multiplayer and for debugging.")]
    [SerializeField] private int randomSeed = 12345;

    // ── Runtime ────────────────────────────────────────────────────────
    private readonly Dictionary<JobKey, DungeonJob> _jobs = new();
    private readonly List<ImpController>            _imps = new();
    private System.Random _rng;

    // Scratch buffers, reused to keep per-request allocation down.
    private readonly List<DungeonJob> _candidates = new();
    private readonly List<float>      _weights    = new();

    public FactionID                    Faction  => faction;
    public IReadOnlyList<ImpController> Imps     => _imps;
    public int                          JobCount => _jobs.Count;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => _rng = new System.Random(randomSeed);

    private void Start()
    {
        _registry[faction] = this;

        gridManager.OnTileChanged += OnTileChanged;

        // The grid may not exist yet at Start, so defer the first full sweep.
        GameManager2D.OnWalletsReady += RebuildAll;
        if (gridManager.Width > 0) RebuildAll();
    }

    private void OnDestroy()
    {
        _registry.Remove(faction);
        GameManager2D.OnWalletsReady -= RebuildAll;
        if (gridManager != null) gridManager.OnTileChanged -= OnTileChanged;
    }

    // ── Imp registration ───────────────────────────────────────────────

    public void RegisterImp(ImpController imp)
    {
        if (!_imps.Contains(imp)) _imps.Add(imp);
    }

    public void UnregisterImp(ImpController imp)
    {
        _imps.Remove(imp);
        foreach (var job in _jobs.Values) job.RemoveWorker(imp);
    }

    // ── Job requests (imps pull) ───────────────────────────────────────

    /// <summary>
    /// Returns the job this imp should take, or null if none is available or
    /// reachable. The imp is registered as a worker before returning, so two
    /// imps cannot over-fill a slot.
    ///
    /// Selection is weighted-random among the nearest few reachable jobs rather
    /// than strictly nearest, so a cluster of imps does not all converge on the
    /// same tile.
    /// </summary>
    public DungeonJob RequestJob(ImpController imp)
    {
        var agent = imp.GetComponent<GridAgent>();
        if (agent == null) return null;

        GridCell impCell = agent.CurrentCell;
        if (impCell == null) return null;

        var connectivity = gridManager.Connectivity;
        if (connectivity == null) return null;

        _candidates.Clear();

        foreach (var job in _jobs.Values)
        {
            if (!job.HasRoom) continue;
            if (!connectivity.AreConnected(impCell, job.WorkCell,
                                           agent.Capability, agent.Radius))
                continue;

            _candidates.Add(job);
        }

        if (_candidates.Count == 0) return null;

        Vector3 impPos = imp.transform.position;

        // Nearest-first, then weighted choice among the leading few.
        _candidates.Sort((a, b) =>
            SqrDistTo(impPos, a.WorkCell).CompareTo(SqrDistTo(impPos, b.WorkCell)));

        int pool = Mathf.Min(_candidates.Count, Mathf.Max(1, considerNearest));

        _weights.Clear();
        float total = 0f;
        for (int i = 0; i < pool; i++)
        {
            float dist = Mathf.Sqrt(SqrDistTo(impPos, _candidates[i].WorkCell));
            float w    = TypeWeight(_candidates[i].Type) /
                         Mathf.Pow(dist + 1f, distanceBias);
            _weights.Add(w);
            total += w;
        }

        if (total <= 0f) return null;

        float roll = (float)_rng.NextDouble() * total;
        int   pick = pool - 1;
        for (int i = 0; i < pool; i++)
        {
            roll -= _weights[i];
            if (roll <= 0f) { pick = i; break; }
        }

        var chosen = _candidates[pick];
        return chosen.AddWorker(imp) ? chosen : null;
    }

    /// <summary>
    /// Shared deterministic random source. Imps use this rather than
    /// UnityEngine.Random so behaviour stays reproducible for a given seed —
    /// the same reason job selection uses it.
    /// </summary>
    public float NextRandom01() => (float)_rng.NextDouble();

    /// <summary>Releases an imp from a job it is no longer working.</summary>
    public void ReleaseJob(DungeonJob job, ImpController imp)
    {
        job?.RemoveWorker(imp);
    }

    private float TypeWeight(JobType type) => type switch
    {
        JobType.Dig       => digWeight,
        JobType.Claim     => claimWeight,
        JobType.Reinforce => reinforceWeight,
        _                 => 1f,
    };

    private float SqrDistTo(Vector3 from, GridCell cell) =>
        (from - gridManager.CellToWorld(cell.X, cell.Y)).sqrMagnitude;

    // ── Job list maintenance ───────────────────────────────────────────

    private void OnTileChanged(GridCell cell)
    {
        // A tile change alters its own eligibility and that of its neighbours:
        // claiming a tile exposes adjacent Cave and Stone, mining one opens a
        // new working side on whatever lay behind it.
        RefreshCell(cell);
        foreach (var (dx, dy) in TraversalRules.Orthogonal)
        {
            var n = gridManager.GetCell(cell.X + dx, cell.Y + dy);
            if (n != null) RefreshCell(n);
        }
    }

    /// <summary>Called by DigSelectionController when a dig marker is added or removed.</summary>
    public void RefreshDigTarget(GridCell cell) => RefreshCell(cell);

    /// <summary>Full sweep. Startup only — incremental refresh handles the rest.</summary>
    public void RebuildAll()
    {
        GameManager2D.OnWalletsReady -= RebuildAll;

        // Drop everything first rather than reconciling. Loading a save calls
        // GridManager2D.Initialise, which replaces every GridCell object, so
        // existing jobs point at a grid that no longer exists. Reconciling
        // misses Claim jobs in particular: their WorkCell IS their Target, so
        // the key still self-matches and they would survive as orphans.
        CancelAllJobs();

        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell != null) RefreshCell(cell);
        }
    }

    /// <summary>
    /// Recomputes every job this cell should generate and reconciles it against
    /// what is registered, so a cell that stops qualifying has its jobs dropped.
    /// Imps working a removed job are told to stand down.
    /// </summary>
    private void RefreshCell(GridCell cell)
    {
        var desired = new List<DungeonJob>();
        BuildJobsFor(cell, desired);

        // Remove registered jobs for this cell that are no longer wanted.
        var stale = new List<JobKey>();
        foreach (var pair in _jobs)
        {
            if (pair.Value.Target != cell) continue;

            bool stillWanted = false;
            foreach (var d in desired)
                if (d.Key.Equals(pair.Key)) { stillWanted = true; break; }

            if (!stillWanted) stale.Add(pair.Key);
        }

        foreach (var key in stale)
        {
            var job = _jobs[key];
            foreach (var imp in new List<ImpController>(job.Workers))
                imp.OnJobCancelled(job);
            _jobs.Remove(key);
        }

        // Add newly eligible jobs, preserving any that already exist so their
        // current workers are not disturbed.
        foreach (var d in desired)
            if (!_jobs.ContainsKey(d.Key)) _jobs[d.Key] = d;
    }

    /// <summary>Every job the given cell currently qualifies to produce.</summary>
    private void BuildJobsFor(GridCell cell, List<DungeonJob> into)
    {
        // ── Dig: player-marked, one job per accessible side ────────────
        var digCtrl = DigSelectionManager.Instance?.GetController(faction);
        if (digCtrl != null && digCtrl.IsQueued(cell) && IsDiggable(cell))
        {
            foreach (var (dx, dy) in TraversalRules.Orthogonal)
            {
                var side = gridManager.GetCell(cell.X + dx, cell.Y + dy);
                if (side == null) continue;
                if (!TraversalRules.CanPathOn(side.TileType,
                                              TraversalCapability.LandOnly, faction))
                    continue;

                into.Add(new DungeonJob(JobType.Dig, cell, side, impsPerDigSide));
            }
            return; // a dig-marked tile is not also a claim or reinforce target
        }

        // ── Claim: unclaimed Cave, or an enemy tile, touching our territory ──
        if (IsClaimable(cell))
        {
            into.Add(new DungeonJob(JobType.Claim, cell, cell, impsPerClaim));
            return;
        }

        // ── Reinforce: Stone touching our territory ────────────────────
        if (IsReinforceable(cell))
        {
            foreach (var (dx, dy) in TraversalRules.Orthogonal)
            {
                var side = gridManager.GetCell(cell.X + dx, cell.Y + dy);
                if (side == null) continue;
                if (side.Owner != faction) continue;
                if (!TraversalRules.CanPathOn(side.TileType,
                                              TraversalCapability.LandOnly, faction))
                    continue;

                // One job per valid side, same as digging, so an imp works
                // from whichever face it happens to approach rather than
                // walking around to a single designated side.
                into.Add(new DungeonJob(JobType.Reinforce, cell, side, impsPerReinforce));
            }
        }
    }

    // ── Eligibility rules ──────────────────────────────────────────────

    private bool IsDiggable(GridCell cell) => cell.TileType switch
    {
        TileType.Stone => true,
        TileType.Gold  => true,
        TileType.Gem   => true,
        TileType.Wall  => cell.Owner == faction,
        _              => false,
    };

    /// <summary>
    /// Unclaimed Cave, or a tile held by another faction — in both cases only
    /// when it touches territory we already hold.
    /// </summary>
    private bool IsClaimable(GridCell cell)
    {
        bool unclaimedCave = cell.TileType == TileType.Cave &&
                             cell.Owner    == FactionID.Unaligned;

        // Portal and Heart are both Owned and walkable like a normal room
        // tile, but neither is capturable through ordinary territory-claim
        // overflow — they only change hands through dedicated mechanics
        // (Heart specifically only falls when its HP reaches 0).
        bool enemyHeld = cell.Owner != faction &&
                         cell.Owner != FactionID.Unaligned &&
                         cell.TileType != TileType.Portal &&
                         cell.TileType != TileType.Heart &&
                         gridManager.GetCategory(cell.TileType) == TileCategory.Owned &&
                         TraversalRules.CanPathOn(cell.TileType,
                                                  TraversalCapability.LandOnly, faction);

        if (!unclaimedCave && !enemyHeld) return false;
        return TouchesOwnTerritory(cell);
    }

    private bool IsReinforceable(GridCell cell) =>
        cell.TileType == TileType.Stone && TouchesOwnTerritory(cell);

    /// <summary>
    /// True if the cell orthogonally touches territory this faction holds AND
    /// can actually operate from.
    ///
    /// Ownership alone is not enough: Wall is owned but impassable, so a tile
    /// backing onto nothing but our own walls is not reachable frontier — no
    /// imp can stand there to claim or reinforce from. Testing passability
    /// rather than excluding Wall by name keeps this correct if further
    /// impassable owned tile types are added later.
    /// </summary>
    private bool TouchesOwnTerritory(GridCell cell) =>
        gridManager.HasAdjacentMatch(cell.X, cell.Y,
            n => n.Owner == faction &&
                 gridManager.GetCategory(n.TileType) == TileCategory.Owned &&
                 TraversalRules.CanPathOn(n.TileType,
                                          TraversalCapability.LandOnly, faction));

    // ── Housekeeping ───────────────────────────────────────────────────

    /// <summary>Cancels and clears every job, standing down any imps working them.</summary>
    private void CancelAllJobs()
    {
        foreach (var job in _jobs.Values)
            foreach (var imp in new List<ImpController>(job.Workers))
                imp.OnJobCancelled(job);

        _jobs.Clear();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || gridManager == null) return;

        foreach (var job in _jobs.Values)
        {
            Gizmos.color = job.Type switch
            {
                JobType.Dig       => Color.yellow,
                JobType.Claim     => Color.cyan,
                JobType.Reinforce => Color.grey,
                _                 => Color.white,
            };
            Gizmos.DrawLine(gridManager.CellToWorld(job.Target.X,   job.Target.Y),
                            gridManager.CellToWorld(job.WorkCell.X, job.WorkCell.Y));
        }
    }
#endif
}
