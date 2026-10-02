using UnityEngine;

/// <summary>
/// What a non-worker minion does with its time. Right now that's one thing:
/// walk to its faction's Dungeon Heart and report for duty. Once it arrives
/// it is Active and idles — the hook for room work (research, training,
/// praying, torture) and combat to hang off later.
///
/// On reporting, MinionController switches a minion whose definition sets
/// canDoWorkerJobs over to WorkerBehaviour, so it helps dig and haul instead
/// of idling.
///
/// Picked up before reporting, it sets off for the heart again from wherever
/// it lands.
/// </summary>
public class CreatureBehaviour : MinionBehaviour
{
    public enum CreatureState { ReportingForDuty, Active }

    [Tooltip("Seconds between retries if no route to the heart exists yet " +
             "(e.g. it hasn't been discovered, or territory isn't connected).")]
    [SerializeField] private float reportRetryInterval = 1f;

    private CreatureState _state = CreatureState.ReportingForDuty;
    private bool  _startedReporting;
    private bool  _headingToHeart;
    private float _nextReportAttempt;

    public CreatureState State       => _state;
    public bool          HasReported => _state == CreatureState.Active;

    // ── Behaviour lifecycle ────────────────────────────────────────────

    public override void Activate()
    {
        _state             = CreatureState.ReportingForDuty;
        _startedReporting  = false;
        _headingToHeart    = false;
        _nextReportAttempt = 0f;
    }

    public override void Pause()
    {
        _headingToHeart = false;
        Agent.Stop();
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
    }

    public override void Deactivate() => _headingToHeart = false;

    private void Update()
    {
        // Deferred to the first Update rather than Activate: GridAgent.Start()
        // sets CurrentCell, and Start-order across components isn't
        // guaranteed, but every Start in the scene runs before any Update.
        if (!_startedReporting)
        {
            _startedReporting = true;
            TryHeadToHeart();
        }

        if (_state != CreatureState.ReportingForDuty) return;

        if (_headingToHeart)
        {
            if (Agent.HasArrived) Arrive();
            return;
        }

        if (Time.time >= _nextReportAttempt) TryHeadToHeart();
    }

    // ── Reporting for duty ───────────────────────────────────────────────

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
        _state          = CreatureState.Active;
        Minion.OnReportedForDuty(); // may switch this minion to worker jobs
    }
}
