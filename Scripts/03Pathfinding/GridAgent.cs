using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Moves a minion along a smoothed, any-angle path. Replaces NavMeshAgent.
///
/// Position is CONTINUOUS. The agent is not snapped to tile centres and can
/// stand, stop or wander anywhere within a tile. CurrentCell is derived from
/// position each frame — cells are a spatial index for game logic, not a
/// constraint on movement.
///
/// Pipeline: GridPathfinder picks cells, PathSmoother collapses them into
/// straight any-angle segments with the token's radius held clear of walls,
/// and this component walks those segments.
///
/// Determinism is preserved throughout — routing, smoothing and steering are
/// all pure functions of grid state and agent positions.
/// </summary>
public class GridAgent : MonoBehaviour
{
    // ── Registry (used for separation) ─────────────────────────────────
    private static readonly List<GridAgent> _allAgents = new();
    private static int _nextAgentIndex;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Movement")]
    [Tooltip("Cells traversed per second.")]
    [SerializeField] private float moveSpeed = 3f;
    [Tooltip("Degrees per second the agent turns to face its heading. 0 = snap.")]
    [SerializeField] private float turnSpeed = 720f;
    [Tooltip("Height above the grid plane the token sits at.")]
    [SerializeField] private float agentHeight = 0.5f;
    [Tooltip("How close counts as reaching a waypoint, in world units.")]
    [SerializeField] private float arriveTolerance = 0.05f;

    [Header("Traversal")]
    [SerializeField] private TraversalCapability capability = TraversalCapability.LandOnly;
    [SerializeField] private FactionID           faction    = FactionID.Player;

    [Header("Token radius")]
    [Tooltip("Measure the token radius from its sprite/mesh bounds at startup. " +
             "Uncheck to use radiusOverride instead.")]
    [SerializeField] private bool  autoMeasureRadius = true;
    [Tooltip("Radius in world units, used when autoMeasureRadius is off or no " +
             "renderer is found.")]
    [SerializeField] private float radiusOverride    = 0.2f;
    [Tooltip("Extra breathing room so the token never sits flush against a wall.")]
    [SerializeField] private float radiusPadding     = 0.02f;

    [Header("Path refresh")]
    [Tooltip("Recompute the route when the grid changes. Mining a tile out can " +
             "reveal a much shorter path, and selling a bridge can sever one.")]
    [SerializeField] private bool  refreshPathOnGridChange = true;

    [Tooltip("Minimum seconds between recomputes for a single agent. Requests " +
             "are staggered per agent so a crowd never repaths on one frame.")]
    [SerializeField] private float pathRefreshInterval = 0.5f;

    [Header("Crowding")]
    [Tooltip("Agents pass through one another and drift apart afterwards rather " +
             "than blocking. Uncheck to disable crowd resolution entirely.")]
    [SerializeField] private bool  useSeparation = true;

    [Tooltip("Gap agents try to keep BEYOND their touching radii, in world units. " +
             "Small values let them pack tightly; large values spread them out.")]
    [SerializeField] private float personalSpace = 0.12f;

    [Tooltip("How hard crowded agents push apart. Higher settles faster but can " +
             "look springy.")]
    [SerializeField] private float separationStrength = 3f;

    [Tooltip("Ceiling on drift speed, as a fraction of moveSpeed. Guarantees net " +
             "forward progress of (1 - this) x moveSpeed however many neighbours " +
             "crowd an agent. Hard-limited below 1: at 1 the drift could exactly " +
             "cancel movement and the crowd would deadlock.")]
    [Range(0f, 0.9f)]
    [SerializeField] private float maxSeparationSpeed = 0.6f;

    [Tooltip("Multiplier for an agent standing still. Above 1 means a working or " +
             "depositing imp gives way more readily than one in transit.")]
    [SerializeField] private float stationaryYieldMultiplier = 1.5f;

    [Header("Static obstacle")]
    [Tooltip("A static agent never paths, moves, or takes hazard damage — " +
             "SetDestination/FleeToSafety are no-ops — but is still sized and " +
             "registered exactly like a moving token, so other agents' own " +
             "separation gently pushes them clear of it. Used for physical " +
             "obstacles with no logic of their own, such as the Dungeon " +
             "Heart's crystal.")]
    [SerializeField] private bool isStatic = false;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridPathfinder _pathfinder;
    private PathSmoother   _smoother;
    private List<Vector3>  _waypoints = new();
    private int            _waypointIndex;
    private float          _radius;
    private int            _agentIndex;
    private GridCell       _destinationCell;
    private Vector3        _destinationPoint;
    private bool           _destinationAllowUnsafe;
    private bool           _hasDestination;
    private bool           _pathDirty;
    private float          _nextPathRefresh;

    /// <summary>
    /// The cell the agent currently stands in, derived from its continuous
    /// position. Authoritative for game logic (which room am I in, am I in
    /// lava) but it does not constrain movement.
    /// </summary>
    public GridCell CurrentCell { get; private set; }

    public bool                HasPath    => _waypointIndex < _waypoints.Count;
    public bool                HasArrived => !HasPath;
    public TraversalCapability Capability => capability;
    public FactionID           Faction    => faction;
    public bool                IsStatic   => isStatic;

    /// <summary>Token radius in world units, including padding.</summary>
    public float Radius => _radius;

    /// <summary>Raised each frame the agent stands on a tile that damages it.</summary>
    public event System.Action<GridCell> OnStandingInHazard;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        ResolveGridManager();

        _pathfinder = new GridPathfinder(gridManager);
        _smoother   = new PathSmoother(gridManager);
        _radius     = MeasureRadius() + radiusPadding;
    }

    /// <summary>
    /// Prefab assets cannot hold references to scene objects, so a spawned
    /// minion always starts with gridManager null. Resolve it from the scene
    /// bootstrapper instead of relying on Inspector wiring.
    ///
    /// Safe to call from Awake: minions are only ever instantiated at runtime,
    /// long after GameManager2D.Awake has set its Instance.
    /// </summary>
    private void ResolveGridManager()
    {
        if (gridManager != null) return;

        gridManager = GameManager2D.Instance != null
            ? GameManager2D.Instance.Grid
            : FindFirstObjectByType<GridManager2D>();

        if (gridManager == null)
            Debug.LogError($"[GridAgent] {name} could not resolve a GridManager2D. " +
                           "Is GameManager2D present in the scene?");
    }

    private void OnEnable()
    {
        _agentIndex = _nextAgentIndex++;
        _allAgents.Add(this);

        if (gridManager != null) gridManager.OnTileChanged += OnTileChanged;
    }

    private void OnDisable()
    {
        _allAgents.Remove(this);
        if (gridManager != null) gridManager.OnTileChanged -= OnTileChanged;
    }

    /// <summary>
    /// Any grid change can open a shortcut or sever the current route, so the
    /// path is flagged for recomputation. Flagging rather than recomputing
    /// immediately matters: a single dig fires this for every agent at once,
    /// and A* for the whole crowd on one frame would spike badly.
    /// </summary>
    private void OnTileChanged(GridCell cell)
    {
        if (refreshPathOnGridChange) _pathDirty = true;
    }

    private void Start()
    {
        RefreshCurrentCell();

        // Sit at the correct height without snapping to the tile centre.
        Vector3 p = transform.position;
        transform.position = new Vector3(p.x, agentHeight, p.z);
    }

    private void Update()
    {
        // A static obstacle never moves and has no path/hazard state of its
        // own to update — it only needs to sit in _allAgents so OTHER agents'
        // ApplySeparation sees it and pushes clear. Nothing below this point
        // applies to it.
        if (isStatic) return;

        RefreshCurrentCell();

        if (CurrentCell != null &&
            TraversalRules.IsHazardousFor(CurrentCell.TileType, capability))
        {
            OnStandingInHazard?.Invoke(CurrentCell);
        }

        // Staggered by agent index so a dig that dirties fifty imps spreads
        // their recomputes across frames instead of spiking one.
        if (HasPath && _pathDirty && Time.time >= _nextPathRefresh)
        {
            _pathDirty       = false;
            _nextPathRefresh = Time.time + pathRefreshInterval
                             + (_agentIndex % 8) * 0.02f;
            RecomputePath();
        }

        if (HasPath)         FollowPath();
        if (useSeparation)   ApplySeparation();
    }

    // ── Destinations ───────────────────────────────────────────────────

    /// <summary>
    /// Paths to the centre of a cell. Returns false if no route exists,
    /// leaving the agent where it is.
    /// </summary>
    public bool SetDestination(GridCell destination, bool allowUnsafe = false)
    {
        if (isStatic || destination == null) return false;
        return SetDestination(
            gridManager.CellToWorld(destination.X, destination.Y), allowUnsafe);
    }

    /// <summary>
    /// Paths to an exact world position, which may be anywhere inside a tile.
    /// This is what lets minions move around within a tile rather than only
    /// between tile centres.
    /// </summary>
    public bool SetDestination(Vector3 worldTarget, bool allowUnsafe = false)
    {
        if (isStatic) return false;

        RefreshCurrentCell();
        if (CurrentCell == null) return false;

        if (!gridManager.WorldToCell(worldTarget, out int tx, out int ty)) return false;
        var targetCell = gridManager.GetCell(tx, ty);
        if (targetCell == null) return false;

        Vector3 target = new(worldTarget.x, agentHeight, worldTarget.z);

        // Straight shot? Skip routing entirely — common inside a room.
        if (_smoother.HasLineOfSight(transform.position, target,
                                     capability, _radius, faction))
        {
            _waypoints.Clear();
            _waypoints.Add(target);
            _waypointIndex   = 0;
            RecordDestination(targetCell, target, allowUnsafe);
            return true;
        }

        var cellPath = _pathfinder.FindPath(
            CurrentCell, targetCell, capability, faction, allowUnsafe, _radius);

        if (cellPath == null) return false;

        _waypoints = _smoother.Smooth(
            transform.position, cellPath, capability, _radius, faction, target);

        _waypointIndex = 0;
        RecordDestination(targetCell, target, allowUnsafe);
        return _waypoints.Count > 0;
    }

    /// <summary>
    /// Routes to the nearest cell that is safe for this agent, ignoring hazards
    /// en route. Returns false if nowhere safe is reachable.
    /// </summary>
    public bool FleeToSafety()
    {
        if (isStatic) return false;

        RefreshCurrentCell();
        var safe = _pathfinder.FindNearestSafeCell(
            CurrentCell, capability, faction, _radius);

        if (safe == null) return false;

        var cellPath = _pathfinder.FindPath(
            CurrentCell, safe, capability, faction, allowUnsafe: true, _radius);

        if (cellPath == null) return false;

        // Smoothing assumes a clear corridor, which does not hold while wading
        // out of a hazard — follow the raw cell path instead.
        _waypoints.Clear();
        foreach (var c in cellPath)
            _waypoints.Add(gridManager.CellToWorld(c.X, c.Y));

        _waypointIndex = 0;
        RecordDestination(safe, gridManager.CellToWorld(safe.X, safe.Y), true);
        return _waypoints.Count > 0;
    }

    /// <summary>Abandons the current path. The agent stops where it stands.</summary>
    public void Stop()
    {
        _waypoints.Clear();
        _waypointIndex   = 0;
        _destinationCell = null;
        _hasDestination  = false;
        _pathDirty       = false;
    }

    /// <summary>True if the agent's current tile damages it.</summary>
    public bool IsInHazard =>
        CurrentCell != null &&
        TraversalRules.IsHazardousFor(CurrentCell.TileType, capability);

    /// <summary>Teleports to a cell centre, clearing any path. Used on spawn.</summary>
    public void SnapTo(GridCell cell)
    {
        if (cell == null) return;
        Stop();
        Vector3 c = gridManager.CellToWorld(cell.X, cell.Y);
        transform.position = new Vector3(c.x, agentHeight, c.z);
        CurrentCell = cell;
    }

    // ── Path following ─────────────────────────────────────────────────

    private void RecordDestination(GridCell cell, Vector3 point, bool allowUnsafe)
    {
        _destinationCell        = cell;
        _destinationPoint       = point;
        _destinationAllowUnsafe = allowUnsafe;
        _hasDestination         = true;
        _pathDirty              = false;
    }

    /// <summary>
    /// Rebuilds the route to the same destination. Keeps the existing path if a
    /// new one cannot be found, rather than stopping — an agent that halts is
    /// reported as arrived, and its controller would start working from the
    /// wrong place.
    /// </summary>
    private bool RecomputePath()
    {
        if (!_hasDestination) return false;

        var saved      = _waypoints;
        var savedIndex = _waypointIndex;

        if (SetDestination(_destinationPoint, _destinationAllowUnsafe)) return true;

        _waypoints     = saved;
        _waypointIndex = savedIndex;
        return false;
    }

    private void FollowPath()
    {
        Vector3 target = _waypoints[_waypointIndex];
        target.y = transform.position.y;

        // Openings are common and can wait for the scheduled refresh, but a
        // closure — a bridge sold, a wall reinforced across the route — must be
        // caught now or the agent walks into solid rock.
        if (gridManager.WorldToCell(target, out int tx, out int ty))
        {
            var targetCell = gridManager.GetCell(tx, ty);
            if (targetCell != null &&
                !TraversalRules.CanOccupy(targetCell.TileType, capability, faction))
            {
                RecomputePath();
                return;
            }
        }

        float step = moveSpeed * gridManager.CellSize * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, target, step);

        FaceTowards(target);

        if (Vector3.SqrMagnitude(transform.position - target) <=
            arriveTolerance * arriveTolerance)
        {
            _waypointIndex++;
            if (!HasPath) Stop();
        }
    }

    private void FaceTowards(Vector3 target)
    {
        Vector3 dir = target - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return;

        Quaternion want = Quaternion.LookRotation(dir);
        transform.rotation = turnSpeed <= 0f
            ? want
            : Quaternion.RotateTowards(transform.rotation, want,
                                       turnSpeed * Time.deltaTime);
    }

    // ── Crowding ───────────────────────────────────────────────────────

    /// <summary>
    /// Soft crowd resolution. Agents may overlap freely and drift apart over
    /// time, the way particles in a fluid settle into even spacing.
    ///
    /// Why not solid bodies: any model where minions physically block one
    /// another deadlocks once traffic is dense enough. Two imps meeting in a
    /// one-wide corridor have no legal way past, and no local rule reliably
    /// resolves it — they stall nose to nose. Letting them interpenetrate and
    /// settle afterwards removes the failure mode instead of managing it.
    ///
    /// The drift is capped at a fraction of movement speed, so crowding can
    /// nudge an agent but can never overpower where it is trying to go. That
    /// cap is what stops a dense crowd freezing its own members in place.
    /// </summary>
    private void ApplySeparation()
    {
        Vector3 push = Vector3.zero;

        foreach (var other in _allAgents)
        {
            if (other == this) continue;

            Vector3 delta = transform.position - other.transform.position;
            delta.y = 0f;

            // Agents only interact once actually overlapping their personal
            // space — outside that they ignore each other completely.
            float desired = _radius + other._radius + personalSpace;
            float distSq  = delta.sqrMagnitude;
            if (distSq >= desired * desired) continue;

            float   dist = Mathf.Sqrt(distSq);
            Vector3 dir;

            if (dist < 1e-4f)
            {
                // Exactly coincident: no direction to push along. Use a stable
                // per-agent bearing so they still separate, without introducing
                // frame-to-frame randomness that would break determinism.
                dir  = StableBearing();
                dist = 0f;
            }
            else
            {
                dir = delta / dist;
            }

            // Proportional to overlap depth, so deeply buried agents push out
            // harder than ones merely brushing. Summed rather than averaged:
            // an agent in a dense pocket should feel the whole crowd.
            push += dir * ((desired - dist) / desired);
        }

        if (push.sqrMagnitude < 1e-8f) return;

        float   yield = HasPath ? 1f : stationaryYieldMultiplier;
        Vector3 vel   = push * separationStrength * yield;

        float maxSpeed = maxSeparationSpeed * moveSpeed * gridManager.CellSize;
        if (vel.magnitude > maxSpeed) vel = vel.normalized * maxSpeed;

        Vector3 proposed = transform.position + vel * Time.deltaTime;

        // Agents pass through each other, but never through walls.
        if (!gridManager.WorldToCell(proposed, out int px, out int py)) return;

        var cell = gridManager.GetCell(px, py);
        if (cell == null) return;
        if (!TraversalRules.CanOccupy(cell.TileType, capability, faction)) return;

        transform.position = ClampToClearance(proposed, transform.position, cell);
    }

    /// <summary>
    /// A fixed direction unique to this agent, used only to break the tie when
    /// two agents occupy the exact same point. Derived from spawn order so it
    /// stays reproducible across runs.
    /// </summary>
    private Vector3 StableBearing()
    {
        float angle = _agentIndex * 2.399963f;   // golden angle, spreads evenly
        return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
    }

    /// <summary>
    /// Pulls a position back toward its cell centre until the token clears the
    /// nearest wall.
    ///
    /// A cell-level fit test is not enough here. Clearance is measured from the
    /// cell CENTRE, so an imp already standing hard against a face — which is
    /// exactly where mining imps stand — passes that test while its token is
    /// only a hair from the rock. Nudging it further then pushes it in.
    /// </summary>
    private Vector3 ClampToClearance(Vector3 proposed, Vector3 current, GridCell cell)
    {
        var clearance = gridManager.Clearance;
        if (clearance == null) return proposed;

        Vector3 centre = gridManager.CellToWorld(cell.X, cell.Y);

        Vector3 proposedOffset = proposed - centre;
        Vector3 currentOffset  = current  - centre;
        proposedOffset.y = 0f;
        currentOffset.y  = 0f;

        float room = clearance.GetClearance(cell, capability) - _radius;

        // CRITICAL: never pull the agent closer to the cell centre than it
        // already is.
        //
        // Clearance is measured from the cell centre, so 'room' is often well
        // under half a cell — but crossing into the next cell necessarily takes
        // an agent 0.5 from its origin centre. Clamping the absolute position
        // to 'room' therefore drags a travelling agent backwards every frame
        // separation fires, pinning it at its own cell centre and making it
        // look as though other minions are blocking it.
        //
        // This clamp exists to stop crowd drift pushing a token into a wall,
        // not to constrain path following.
        float allowed = Mathf.Max(room, currentOffset.magnitude);
        if (allowed <= 0f) return proposed;

        float dist = proposedOffset.magnitude;
        if (dist <= allowed || dist < 1e-6f) return proposed;

        Vector3 result = centre + proposedOffset / dist * allowed;
        result.y = proposed.y;
        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void RefreshCurrentCell()
    {
        if (gridManager.WorldToCell(transform.position, out int x, out int y))
            CurrentCell = gridManager.GetCell(x, y);
    }

    /// <summary>
    /// Reads the token radius from the prefab's renderer bounds — the sprite or
    /// quad used for the ground token. Uses the larger of the X and Z extents,
    /// since the token lies flat on the XZ plane and is treated as a circle
    /// enclosing its sprite.
    /// </summary>
    private float MeasureRadius()
    {
        if (!autoMeasureRadius) return radiusOverride;

        var renderers = GetComponentsInChildren<Renderer>();
        if (renderers == null || renderers.Length == 0)
        {
            Debug.LogWarning($"[GridAgent] {name} has no Renderer to measure. " +
                             $"Using radiusOverride ({radiusOverride}).");
            return radiusOverride;
        }

        Bounds combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        return Mathf.Max(combined.extents.x, combined.extents.z);
    }
}
