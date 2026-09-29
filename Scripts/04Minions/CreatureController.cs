using UnityEngine;

/// <summary>
/// One summoned minion (anything MinionSummoner spawns — never imps, which
/// stay on ImpController). Every creature's first and only job right now is
/// to walk to its faction's Dungeon Heart and report for duty; once it
/// arrives it is Active and simply idles, ready for future combat/room-job
/// behaviour to hang off that state.
///
/// Every summoned creature is the same template prefab; Initialise applies
/// its MinionDefinition (token, movement, radius) and sets its level. Stats
/// are never stored per creature — GetStat reads them from the definition
/// at the current level, so a re-import rebalances existing creatures too.
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
    private int              _level = 1;
    private float            _experience;
    private bool             _startedReporting;
    private bool             _headingToHeart;
    private float            _nextReportAttempt;

    public FactionID        Faction    => faction;
    public MinionDefinition Definition => _definition;
    public CreatureState    State      => _state;
    public int              Level      => _level;
    public float            Experience => _experience;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake() => _agent = GetComponent<GridAgent>();

    /// <summary>Called by MinionSummoner immediately after instantiation.</summary>
    public void Initialise(FactionID owningFaction, MinionDefinition definition, int level = 1)
    {
        faction     = owningFaction;
        _definition = definition;
        _agent.SetFaction(owningFaction);

        ApplyDefinition();

        _level      = definition != null ? Mathf.Clamp(level, 1, definition.maxLevel) : 1;
        _experience = definition != null ? definition.ExperienceForLevel(_level) : 0f;
    }

    /// <summary>
    /// Configures the shared template for this minion type. Only the token is
    /// swapped for now; a 3D model and animator override will hang off the
    /// definition the same way.
    /// </summary>
    private void ApplyDefinition()
    {
        if (_definition == null) return;

        if (_definition.token != null)
        {
            var sprite = GetComponentInChildren<SpriteRenderer>();
            if (sprite != null) sprite.sprite = _definition.token;
            else Debug.LogWarning($"[CreatureController] {name} has no SpriteRenderer for {_definition.minionId}'s token.");
        }

        _agent.SetCapability(_definition.movement);
        _agent.RemeasureRadius();
    }

    // ── Stats & levelling ──────────────────────────────────────────────

    /// <summary>This creature's value for a stat at its current level.</summary>
    public float GetStat(MinionStat stat) =>
        _definition != null ? _definition.GetStat(stat, _level) : 0f;

    /// <summary>
    /// Adds experience (from fighting or training) and levels up as far as
    /// the definition's Experience curve allows. Returns true on level-up.
    /// </summary>
    public bool AddExperience(float amount)
    {
        if (_definition == null || amount <= 0f) return false;

        _experience += amount;
        int newLevel = _definition.LevelForExperience(_experience);
        if (newLevel <= _level) return false;

        _level = newLevel;
        return true;
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
        MinionSummoner.Instance?.NotifyCreatureRemoved(faction, _definition);
        Destroy(gameObject, 0.1f);
    }

    // ── Conversion ─────────────────────────────────────────────────────

    /// <summary>
    /// Switches this creature to a new faction in place — same GameObject,
    /// same prefab, same model, just a new owner. Groundwork for capture/
    /// torture-to-convert: nothing calls this yet, but when a future Prison
    /// room finishes converting a captured creature, this is the one call it
    /// needs to make.
    ///
    /// Re-enters ReportingForDuty so the creature treks to its new masters'
    /// heart before counting as truly theirs, same as a freshly summoned one.
    /// </summary>
    public void ConvertTo(FactionID newFaction)
    {
        if (_state == CreatureState.Dead || newFaction == faction) return;

        MinionSummoner.Instance?.NotifyCreatureRemoved(faction, _definition);

        faction = newFaction;
        _agent.SetFaction(newFaction);

        MinionSummoner.Instance?.NotifyCreatureJoined(faction, _definition);

        _headingToHeart    = false;
        _nextReportAttempt = 0f;
        _state             = CreatureState.ReportingForDuty;
    }
}
