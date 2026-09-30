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
///
/// Keeper's hand
/// -------------
/// Picking a creature up wipes whatever it was doing; it reassesses when set
/// down. A creature that hadn't yet reported sets off for the heart again
/// from wherever it lands. Dropping a summoned creature on its portal makes
/// it abandon the dungeon — it despawns and frees its population slot.
/// Non-summonable ones (commanders, the general) are just set down there. Dropping one on a room remembers that room's type
/// for dropAffinityDuration seconds (see TryGetDropAffinity) — the hook for
/// room-work behaviour to weight that room more heavily. A slap costs a
/// little health, adds anger and speeds up work for a while (see
/// MinionTemper and WorkSpeedMultiplier).
/// </summary>
[RequireComponent(typeof(GridAgent))]
public class CreatureController : MonoBehaviour, IHandTarget
{
    public enum CreatureState { ReportingForDuty, Active, Dead }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Tooltip("Seconds between retries if no route to the heart exists yet " +
             "(e.g. it hasn't been discovered, or territory isn't connected).")]
    [SerializeField] private float reportRetryInterval = 1f;

    [Header("Survival")]
    [Tooltip("Used when the definition has no Health row.")]
    [SerializeField] private float fallbackMaxHealth = 100f;

    [Header("Keeper's hand")]
    [Tooltip("Seconds a creature dropped on a room favours working in that room type.")]
    [SerializeField] private float dropAffinityDuration = 30f;
    [Tooltip("Anger lost per second after a slap, on a 0-1 scale.")]
    [SerializeField] private float angerDecayPerSecond  = 0.02f;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridAgent        _agent;
    private MinionDefinition _definition;
    private CreatureState    _state = CreatureState.ReportingForDuty;
    private int              _level = 1;
    private float            _experience;
    private bool             _startedReporting;
    private bool             _headingToHeart;
    private float            _nextReportAttempt;
    private float            _health;
    private bool             _held;
    private MinionTemper     _temper;
    private TileType         _dropRoomType;
    private float            _dropAffinityUntil = float.NegativeInfinity;

    public FactionID        Faction    => faction;
    public MinionDefinition Definition => _definition;
    public CreatureState    State      => _state;
    public int              Level      => _level;
    public float            Experience => _experience;
    public float            Health     => _health;
    public float            Anger      => _temper.Anger;

    /// <summary>Multiplier for any work this creature does — above 1 while a slap's boost lasts.</summary>
    public float            WorkSpeedMultiplier => _temper.WorkSpeedMultiplier;

    /// <summary>Health at the current level; the Inspector fallback until the data has a Health row.</summary>
    public float MaxHealth =>
        _definition != null && _definition.TryGetAuthoredStat(MinionStat.Health, _level, out float v)
            ? v : fallbackMaxHealth;

    // IHandTarget
    public GridAgent        Agent   => _agent;
    public bool             IsAlive => _state != CreatureState.Dead;
    public bool             IsHeld  => _held;

    /// <summary>
    /// The room type this creature was recently dropped on, if the drop is
    /// still fresh. For room-work behaviour to favour that room.
    /// </summary>
    public bool TryGetDropAffinity(out TileType roomType)
    {
        roomType = _dropRoomType;
        return Time.time < _dropAffinityUntil;
    }

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _agent  = GetComponent<GridAgent>();
        _temper = new MinionTemper(angerDecayPerSecond);
        _health = MaxHealth;
    }

    private void OnEnable()  => KeeperHand.Register(this);
    private void OnDisable() => KeeperHand.Unregister(this);

    /// <summary>Called by MinionSummoner immediately after instantiation.</summary>
    public void Initialise(FactionID owningFaction, MinionDefinition definition, int level = 1)
    {
        faction     = owningFaction;
        _definition = definition;
        _agent.SetFaction(owningFaction);

        definition?.ApplyTo(gameObject, _agent);

        _level      = definition != null ? Mathf.Clamp(level, 1, definition.maxLevel) : 1;
        _experience = definition != null ? definition.ExperienceForLevel(_level) : 0f;
        _health     = MaxHealth;
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

        // Keep the same fraction of health across the level-up.
        float fraction = MaxHealth > 0f ? _health / MaxHealth : 1f;
        _level  = newLevel;
        _health = fraction * MaxHealth;
        return true;
    }

    private void Update()
    {
        // In the hand: the hand moves the token and nothing else happens.
        if (_held) return;

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

    // ── Keeper's hand ──────────────────────────────────────────────────

    /// <summary>
    /// Lifted by the hand: forgets where it was going and switches the agent
    /// off so the hand can move the token freely.
    /// </summary>
    public void OnPickedUp()
    {
        if (!IsAlive || _held) return;

        _held              = true;
        _headingToHeart    = false;
        _dropAffinityUntil = float.NegativeInfinity;

        _agent.Stop();
        _agent.enabled = false;
    }

    /// <summary>
    /// Set down: switch the agent back on where it landed and reassess. Not
    /// yet reported → head for the heart again, from here. Landed on a room
    /// → remember it for a while.
    /// </summary>
    public void OnDropped(Vector3 worldPosition, GridCell cell)
    {
        if (!_held) return;
        _held = false;

        _agent.enabled = true;
        if (!_agent.WarpTo(worldPosition)) _agent.SnapTo(cell);

        if (IsRoom(cell.TileType))
        {
            _dropRoomType      = cell.TileType;
            _dropAffinityUntil = Time.time + dropAffinityDuration;
        }

        // Also covers a creature grabbed before its first Update.
        _startedReporting = true;
        if (_state == CreatureState.ReportingForDuty)
        {
            _headingToHeart    = false;
            _nextReportAttempt = 0f;
        }
    }

    /// <summary>
    /// Only creatures that come through the portal can leave by it — the
    /// definition's summonable flag, which is false for commanders, workers
    /// and the general.
    /// </summary>
    public bool CanAbandon => _definition != null && _definition.summonable;

    /// <summary>Dropped on the portal: leaves the dungeon, freeing its population slot.</summary>
    public void OnAbandon()
    {
        if (!IsAlive || !CanAbandon) return;
        Die();
    }

    public void OnSlapped(in HandSlap slap)
    {
        if (!IsAlive) return;

        _temper.ApplySlap(slap);
        TakeDamage(slap.DamageFor(_health, MaxHealth));
    }

    private static bool IsRoom(TileType type) =>
        type == TileType.RoomA || type == TileType.RoomB || type == TileType.RoomC;

    // ── Damage and death ───────────────────────────────────────────────

    public void TakeDamage(float amount)
    {
        if (!IsAlive || amount <= 0f) return;

        _health -= amount;
        if (_health <= 0f) Die();
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
