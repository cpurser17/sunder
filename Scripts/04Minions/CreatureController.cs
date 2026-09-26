using UnityEngine;

/// <summary>
/// One summoned minion (anything MinionSummoner spawns — never imps, which
/// stay on ImpController). Every creature's first and only job right now is
/// to walk to its faction's Dungeon Heart and report for duty; once it
/// arrives it is Active and simply idles, ready for future combat/room-job
/// behaviour to hang off that state.
/// </summary>
[RequireComponent(typeof(GridAgent))]
public class CreatureController : MonoBehaviour
{
    public enum CreatureState { ReportingForDuty, Active, Dead }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Tooltip("Seconds between retries if no route to the heart exists yet " +
             "(e.g. it hasn't been discovered, or territory isn't connected).")]
    [SerializeField] private float reportRetryInterval = 1f;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridAgent        _agent;
    private MinionDefinition _definition;
    private CreatureState    _state = CreatureState.ReportingForDuty;
    private bool             _startedReporting;
    private bool             _headingToHeart;
    private float            _nextReportAttempt;

    public FactionID        Faction    => faction;
    public MinionDefinition Definition => _definition;
    public CreatureState    State      => _state;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => _agent = GetComponent<GridAgent>();

    /// <summary>Called by MinionSummoner immediately after instantiation.</summary>
    public void Initialise(FactionID owningFaction, MinionDefinition definition)
    {
        faction     = owningFaction;
        _definition = definition;
    }

    private void Update()
    {
        // Deferred to the first Update rather than Start: GridAgent.Start()
        // sets CurrentCell, and Start-order across components isn't
        // guaranteed, but every Start in the scene runs before any Update —
        // the same reason ImpController waits for its first Update tick.
        if (!_startedReporting)
        {
            _startedReporting = true;
            TryHeadToHeart();
        }

        if (_state != CreatureState.ReportingForDuty) return;

        if (_headingToHeart)
        {
            if (_agent.HasArrived) Arrive();
            return;
        }

        if (Time.time >= _nextReportAttempt) TryHeadToHeart();
    }

    // ── Reporting for duty ───────────────────────────────────────────────

    private void TryHeadToHeart()
    {
        var heart = DungeonHeart.Instance;
        GridCell approach = heart != null && heart.IsReady(faction)
            ? heart.FindApproachCell(faction, _agent.CurrentCell, _agent.Capability, _agent.Radius)
            : null;

        if (approach == null || !_agent.SetDestination(approach))
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
        DungeonHeart.Instance?.NotifyReported(faction, this);
    }

    // ── Death ──────────────────────────────────────────────────────────

    public void Die()
    {
        if (_state == CreatureState.Dead) return;

        _state = CreatureState.Dead;
        MinionSummoner.Instance?.NotifyCreatureDied(faction, _definition);
        Destroy(gameObject, 0.1f);
    }
}
