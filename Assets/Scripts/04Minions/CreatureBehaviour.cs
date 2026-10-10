using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What a non-worker minion does with its time, once nothing more pressing
/// (wages, food, sleep, a sulk — MinionController's errands) needs it.
///
///   ReportingForDuty  walk to the Dungeon Heart and report (once, on arrival)
///   Choosing          pick a job: the room it was dropped on by the Keeper's
///                     hand if it does that work ("assigned job"), otherwise
///                     a room job weighted by its preferences and the room's
///                     efficiency ("default job")
///   GoingToWork       walking to a free spot in that room
///   Working           working the spot (RoomWorkManager) for a session, then
///                     choosing again
///   Idle              nothing it will do: wander its own territory and look
///                     for work again now and then. Idling makes it
///                     grumpy; working calms that again.
///
/// On reporting, MinionController switches a minion whose definition sets
/// canDoWorkerJobs over to WorkerBehaviour, so it helps dig and haul instead.
/// </summary>
public class CreatureBehaviour : MinionBehaviour
{
    public enum CreatureState { ReportingForDuty, Choosing, GoingToWork, Working, Idle }

    [Tooltip("Seconds between retries if no route to the heart exists yet " +
             "(e.g. it hasn't been discovered, or territory isn't connected).")]
    [SerializeField] private float reportRetryInterval = 1f;

    [Header("Idle")]
    [Tooltip("Seconds between looks for work while idle.")]
    [SerializeField] private float idleRecheckSeconds = 10f;
    [Tooltip("Seconds it pauses between wanders (random between these).")]
    [SerializeField] private Vector2 wanderPause = new(2f, 5f);
    [Tooltip("How far, in cells, one wander goes at most.")]
    [SerializeField, Min(1)] private int wanderRange = 5;

    private CreatureState _state = CreatureState.ReportingForDuty;
    private bool  _reported;
    private bool  _startedReporting;
    private bool  _headingToHeart;
    private float _nextReportAttempt;

    private GridPathfinder _pathfinder;
    private RoomWork _work;
    private GridCell _spot;
    private float    _sessionEnds;
    private float    _nextIdleCheck;
    private float    _nextWander;
    private bool     _wandering;

    public CreatureState State       => _state;
    public bool          HasReported => _reported;
    public RoomWork      CurrentWork => _state is CreatureState.GoingToWork or CreatureState.Working ? _work : RoomWork.None;
    public bool          IsIdle      => enabled && _state == CreatureState.Idle;
    public bool          IsWorking   => enabled && _state == CreatureState.Working;

    /// <summary>Changed sides: it must report to its new masters' heart.</summary>
    public void ForgetReport() => _reported = false;

    /// <summary>Loading a save: it had already reported for duty — straight to choosing work.</summary>
    public void RestoreReported()
    {
        _reported         = true;
        _startedReporting = true;
        _headingToHeart   = false;
        _state            = CreatureState.Choosing;
    }

    // ── Behaviour lifecycle ────────────────────────────────────────────

    public override void Activate()
    {
        if (_pathfinder == null && Minion.Grid != null) _pathfinder = new GridPathfinder(Minion.Grid);
        _state             = _reported ? CreatureState.Choosing : CreatureState.ReportingForDuty;
        _startedReporting  = false;
        _headingToHeart    = false;
        _nextReportAttempt = 0f;
    }

    public override void Pause()
    {
        _headingToHeart = false;
        _wandering      = false;
        ReleaseSpot();
        Agent.Stop();
        if (_reported) _state = CreatureState.Choosing;
    }

    public override void Resume()
    {
        // Also covers a minion grabbed before its first Update.
        _startedReporting = true;
        if (_state == CreatureState.ReportingForDuty)
        {
            _headingToHeart    = false;
            _nextReportAttempt = 0f;
        }
        else _state = CreatureState.Choosing;
    }

    public override void Deactivate()
    {
        _headingToHeart = false;
        ReleaseSpot();
    }

    private void Update()
    {
        switch (_state)
        {
            case CreatureState.ReportingForDuty: UpdateReporting(); break;
            case CreatureState.Choosing:         Choose();          break;

            case CreatureState.GoingToWork:
                if (!HoldsSpot())          _state = CreatureState.Choosing;
                else if (Agent.HasArrived) BeginWorking();
                break;

            case CreatureState.Working:
                if (!HoldsSpot()) { _state = CreatureState.Choosing; break; }
                Minion.TickWorking(Time.deltaTime);
                bool carryOn = RoomWorkManager.Instance != null &&
                               RoomWorkManager.Instance.Work(Minion, _spot, _work, Time.deltaTime);
                if (!carryOn || Time.time >= _sessionEnds) { ReleaseSpot(); _state = CreatureState.Choosing; }
                break;

            case CreatureState.Idle:
                Minion.TickIdle(Time.deltaTime);
                if (Time.time >= _nextIdleCheck) { _state = CreatureState.Choosing; break; }
                Wander();
                break;
        }
    }

    // ── Reporting for duty ─────────────────────────────────────────────

    private void UpdateReporting()
    {
        // Deferred to the first Update rather than Activate: GridAgent.Start()
        // sets CurrentCell, and Start-order across components isn't
        // guaranteed, but every Start in the scene runs before any Update.
        if (!_startedReporting)
        {
            _startedReporting = true;
            TryHeadToHeart();
        }

        if (_headingToHeart)
        {
            if (Agent.HasArrived) Arrive();
            return;
        }

        if (Time.time >= _nextReportAttempt) TryHeadToHeart();
    }

    private void TryHeadToHeart()
    {
        var faction = Minion.Faction;
        var heart   = DungeonHeart.Instance;
        GridCell approach = heart != null && heart.IsReady(faction)
            ? heart.FindApproachCell(faction, Agent.CurrentCell, Agent.Capability, Agent.Radius)
            : null;

        if (approach == null || !Agent.SetDestination(approach))
        {
            _nextReportAttempt = Time.time + reportRetryInterval;
            return;
        }

        _headingToHeart = true;
    }

    private void Arrive()
    {
        _headingToHeart = false;
        _reported       = true;
        _state          = CreatureState.Choosing;
        Minion.OnReportedForDuty(); // may switch this minion to worker jobs
    }

    // ── Choosing work ──────────────────────────────────────────────────

    private void Choose()
    {
        var manager = RoomWorkManager.Instance;
        var from    = Agent.CurrentCell;
        if (from == null) return;   // not placed yet; try next frame

        if (manager != null && _pathfinder != null)
        {
            // Assigned job: dropped on a room it works in, by the Keeper's hand.
            if (Minion.TryGetDropAffinity(out var dropped))
            {
                var assigned = RoomWorkManager.WorkFor(dropped);
                if (assigned != RoomWork.None && RoomWorkManager.Preference(Minion, assigned) > 0f && TryTake(manager, assigned, from))
                    return;
            }

            // Default job: weighted by preference × the best room's efficiency.
            var options = new List<(RoomWork work, float weight)>();
            float total = 0f;
            foreach (var work in RoomWorkManager.AllWork)
            {
                float weight = RoomWorkManager.Preference(Minion, work) * manager.BestEfficiency(Minion.Faction, work);
                if (weight <= 0f) continue;
                options.Add((work, weight));
                total += weight;
            }

            while (options.Count > 0)
            {
                float roll = Random01() * total;
                int pick = options.Count - 1;
                for (int i = 0; i < options.Count; i++)
                {
                    roll -= options[i].weight;
                    if (roll <= 0f) { pick = i; break; }
                }
                if (TryTake(manager, options[pick].work, from)) return;
                total -= options[pick].weight;
                options.RemoveAt(pick);
            }
        }

        // Nothing it will do.
        _state         = CreatureState.Idle;
        _nextIdleCheck = Time.time + idleRecheckSeconds;
        _wandering     = false;
        _nextWander    = Time.time;
    }

    /// <summary>Claims the nearest free spot for this work it can reach and heads there.</summary>
    private bool TryTake(RoomWorkManager manager, RoomWork work, GridCell from)
    {
        var faction = Minion.Faction;
        var spot = _pathfinder.FindNearestMatching(from, c => manager.IsFreeSpot(c, faction, work),
                                                   Agent.Capability, faction, Agent.Radius);
        if (spot == null || !manager.Claim(Minion, spot, work)) return false;
        if (!Agent.SetDestination(spot)) { manager.Release(Minion); return false; }

        _work  = work;
        _spot  = spot;
        _state = CreatureState.GoingToWork;
        return true;
    }

    private void BeginWorking()
    {
        var session = RoomWorkManager.Instance != null ? RoomWorkManager.Instance.SessionSeconds : new Vector2(60f, 120f);
        _sessionEnds = Time.time + Mathf.Lerp(session.x, session.y, Random01());
        _state       = CreatureState.Working;
    }

    private bool HoldsSpot() =>
        RoomWorkManager.Instance != null && RoomWorkManager.Instance.StillHolds(Minion, _spot, _work);

    private void ReleaseSpot()
    {
        RoomWorkManager.Instance?.Release(Minion);
        _spot = null;
    }

    // ── Idle ───────────────────────────────────────────────────────────

    /// <summary>Strolls to a random nearby tile of its own territory, pausing in between.</summary>
    private void Wander()
    {
        if (_wandering)
        {
            if (!Agent.HasArrived) return;
            _wandering  = false;
            _nextWander = Time.time + Mathf.Lerp(wanderPause.x, wanderPause.y, Random01());
            return;
        }
        if (Time.time < _nextWander) return;

        var grid = Minion.Grid;
        var from = Agent.CurrentCell;
        if (grid == null || from == null) return;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            int dx = Mathf.RoundToInt((Random01() * 2f - 1f) * wanderRange);
            int dy = Mathf.RoundToInt((Random01() * 2f - 1f) * wanderRange);
            var to = grid.GetCell(from.X + dx, from.Y + dy);
            if (to == null || to == from || to.Owner != Minion.Faction) continue;
            if (!TraversalRules.CanPathOn(to.TileType, Agent.Capability, Minion.Faction)) continue;
            if (Agent.SetDestination(to)) { _wandering = true; return; }
        }
        _nextWander = Time.time + wanderPause.x;
    }

    private float Random01()
    {
        var tasks = WorkerTaskManager.GetForFaction(Minion.Faction);
        return tasks != null ? tasks.NextRandom01() : Random.value;
    }
}
