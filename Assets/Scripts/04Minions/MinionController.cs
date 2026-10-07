using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The one controller every minion has — workers, summoned creatures,
/// commanders, all of them, on the one shared template prefab.
///
/// It owns what every minion has in common: which MinionDefinition it is,
/// level and experience, health and death, temper, the Keeper's hand,
/// hazards and faction. What the minion DOES with its time lives in
/// MinionBehaviour components on the same prefab; this picks the active one
/// and switches it off while the minion is held or fleeing (see
/// MinionBehaviour for the contract).
///
/// Which behaviour
/// ---------------
///   Worker stance (or no data, from WorkerSpawner)  → WorkerBehaviour
///   everything else                              → CreatureBehaviour, which
///     reports for duty at the heart; if the definition's canDoWorkerJobs is
///     set, it then helps with worker jobs (digging, claiming, hauling)
///
/// Where the data comes from
/// -------------------------
/// The prefab holds no MinionDefinition. Whatever spawns it — MinionSummoner
/// at the portal, WorkerSpawner on a click — calls Initialise with the right
/// one straight after Instantiate. A minion placed by hand in a scene (for
/// testing) uses placedDefinition instead.
///
/// Stats are never copied onto the minion: GetStat reads the definition at
/// the current level, so a re-import rebalances minions already on the map.
///
/// Errands
/// -------
/// Some things briefly take a minion away from its active behaviour and
/// then hand it back where it left off — collecting wages on payday
/// (WageBehaviour) is the first. An errand pauses the active behaviour
/// exactly as being picked up does, and resumes it when done. Being held or
/// fleeing pauses the errand in turn. (Combat will break into errands the
/// same way once it exists.)
///
/// Wages
/// -----
/// On payday (PaydaySystem) the minion is owed its Salary at its current
/// level, on top of anything still owed, and goes to collect it. Whatever it
/// can't collect it remembers, and is owed on top at the next payday. The
/// debt makes it angry in proportion (MinionTemper.Grievance): a whole
/// payday's wages owed adds the full angerPerUnpaidSalary, half adds half,
/// two paydays' worth adds double. Paid in full, the grievance clears.
///
/// Tiredness
/// ---------
/// Every minion except workers grows tired over time — TirednessRate per
/// minute on a 0-1 scale, faster while doing worker jobs. At sleepThreshold
/// it goes to its Lair bed to sleep (SleepBehaviour; see LairManager).
/// Fully tired (1) it is exhausted: anger grows the longer it lasts, its
/// performance stats drop (exhaustedStatMultiplier), and it can't recover
/// health from food (CanRecoverFromFood, for the Hatchery).
///
/// Hunger
/// -------
/// Every minion except workers grows hungry — HungerRate per minute on a
/// 0-1 scale. At hungerThreshold it goes to eat chickens (FoodBehaviour,
/// HatcheryManager); one passing close to a chicken while a little peckish
/// (snackThreshold) eats it there and then. Each chicken takes hunger off
/// and heals, unless the minion is exhausted. Fully hungry (1) it is
/// starving: anger grows, its performance stats drop, and it slowly loses
/// health (never the last point). Workers never get hungry: they eat only
/// when injured, to heal. Dropping a chicken on a minion force-feeds it.
///
/// Priorities (after Dungeon Keeper)
/// ---------------------------------
/// What a minion does is decided top-down; the first that applies wins.
///   Highest      fight (CombatBehaviour) — pauses anything, even sleep;
///                prisoner, torture                           (later)
///   Enchantment  spells cast on it: call to arms, etc.       (later)
///   High         collect wages, eat, recover (sleep when badly hurt), sleep
///   Anger        leave the dungeon (furious), sulk (annoyed)
///   Assigned     whatever the room it was dropped on by the hand is for:
///                Lair → sleep, Hatchery → eat, Treasury → wages (at low
///                thresholds), work rooms → that work (CreatureBehaviour)
///   Default      room work by preference: train, research, workshop, pray
///   Idle         nap if a little tired, else wander and grow grumpy
/// High and Anger jobs run as errands — one at a time, never interrupting
/// each other (payday doesn't wake a sleeper; it collects when it gets up).
/// Assigned, Default and Idle are the active behaviour's (CreatureBehaviour
/// for creatures, WorkerBehaviour for workers).
///
/// Mood
/// ----
/// Anger (MinionTemper) sums its causes: slaps, unpaid wages, exhaustion,
/// no bed, starving, idling. From annoyedAnger it sulks now and then; from
/// furiousAnger a minion that came through the portal leaves for good. All
/// placeholder values until the anger behaviours are designed properly.
/// </summary>
[RequireComponent(typeof(GridAgent))]
public class MinionController : MonoBehaviour, IHandTarget
{
    /// <summary>Every live minion, all factions.</summary>
    public static IReadOnlyList<MinionController> All => _all;
    private static readonly List<MinionController> _all = new();

    /// <summary>How the minion arrived — decides who is told when it leaves.</summary>
    public enum SpawnSource { Placed, Portal, WorkerSpawner }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("Placed in a scene by hand (testing only)")]
    [Tooltip("Used only if nothing calls Initialise — i.e. this minion was " +
             "placed in the scene rather than spawned. Leave empty on the template.")]
    [SerializeField] private MinionDefinition placedDefinition;
    [SerializeField, Min(1)] private int placedLevel = 1;

    [Header("Survival")]
    [Tooltip("Max health when the definition has no Health row yet.")]
    [SerializeField] private float fallbackMaxHealth = 100f;
    [Tooltip("Anger lost per second after a slap, on a 0-1 scale.")]
    [SerializeField] private float angerDecayPerSecond = 0.02f;

    [Header("Tiredness")]
    [Tooltip("Tiredness (0-1) at which it goes to bed.")]
    [SerializeField, Range(0f, 1f)] private float sleepThreshold = 0.7f;
    [Tooltip("Tiredness gained per minute when the data has no TirednessRate row.")]
    [SerializeField, Min(0f)] private float fallbackTirednessPerMinute = 0.1f;
    [Tooltip("Tiredness builds this many times faster while doing worker jobs.")]
    [SerializeField, Min(0f)] private float workingTirednessMultiplier = 1.5f;
    [Tooltip("Lasting anger (0-1) gained per minute while exhausted. Placeholder.")]
    [SerializeField, Range(0f, 1f)] private float exhaustedAngerPerMinute = 0.1f;
    [Tooltip("Performance stats (strength, skills, speed…) are multiplied by this while exhausted.")]
    [SerializeField, Range(0f, 1f)] private float exhaustedStatMultiplier = 0.75f;

    [Header("Hunger")]
    [Tooltip("Hunger (0-1) at which it goes to eat.")]
    [SerializeField, Range(0f, 1f)] private float hungerThreshold = 0.7f;
    [Tooltip("Hunger (0-1) above which it eats a chicken it happens to pass.")]
    [SerializeField, Range(0f, 1f)] private float snackThreshold = 0.3f;
    [Tooltip("How close, in cells, a passing chicken must be to be snacked on.")]
    [SerializeField, Min(0f)] private float snackReach = 1.5f;
    [Tooltip("Hunger gained per minute when the data has no HungerRate.")]
    [SerializeField, Min(0f)] private float fallbackHungerPerMinute = 0.08f;
    [Tooltip("Workers (who never get hungry) go to eat below this fraction of max health.")]
    [SerializeField, Range(0f, 1f)] private float workerEatBelowHealth = 0.5f;
    [Tooltip("Seconds before trying again after finding nothing to eat.")]
    [SerializeField, Min(1f)] private float foodRetrySeconds = 10f;
    [Tooltip("Lasting anger (0-1) gained per minute while starving. Placeholder.")]
    [SerializeField, Range(0f, 1f)] private float starvingAngerPerMinute = 0.1f;
    [Tooltip("Health lost per minute while starving, as a fraction of max health. Never kills.")]
    [SerializeField, Range(0f, 1f)] private float starvingDamagePerMinute = 0.05f;
    [Tooltip("Performance stats are multiplied by this while starving.")]
    [SerializeField, Range(0f, 1f)] private float starvingStatMultiplier = 0.75f;

    [Header("Recovering")]
    [Tooltip("Below this fraction of max health it goes to bed to recover (minions that sleep).")]
    [SerializeField, Range(0f, 1f)] private float recoverBelowHealth = 0.3f;
    [Tooltip("A recovering minion stays in bed until healed to this fraction.")]
    [SerializeField, Range(0f, 1f)] private float recoverUntilHealth = 0.9f;

    [Header("Assigned by the hand")]
    [Tooltip("Dropped on a Lair, it sleeps if at least this tired; on a Hatchery, eats if at least this hungry.")]
    [SerializeField, Range(0f, 1f)] private float assignedNeedThreshold = 0.2f;

    [Header("Idle")]
    [Tooltip("An idle minion naps if at least this tired.")]
    [SerializeField, Range(0f, 1f)] private float idleSleepThreshold = 0.3f;
    [Tooltip("Lasting anger (0-1) gained per minute idle. Placeholder.")]
    [SerializeField, Range(0f, 1f)] private float idleAngerPerMinute = 0.03f;
    [Tooltip("Idle anger worked off per minute of room work.")]
    [SerializeField, Range(0f, 1f)] private float workCalmPerMinute = 0.06f;

    [Header("Mood (placeholder)")]
    [Tooltip("Anger at which it starts to sulk now and then.")]
    [SerializeField, Range(0f, 1f)] private float annoyedAnger = 0.5f;
    [Tooltip("Chance per minute of a sulk while annoyed.")]
    [SerializeField, Range(0f, 1f)] private float sulkChancePerMinute = 0.5f;
    [Tooltip("Anger at which a minion that came through the portal leaves for good.")]
    [SerializeField, Range(0f, 1f)] private float furiousAnger = 0.9f;
    [Tooltip("Seconds before a minion that couldn't reach the portal tries to leave again.")]
    [SerializeField, Min(1f)] private float leaveRetrySeconds = 30f;

    [Header("Keeper's hand")]
    [Tooltip("Seconds a minion dropped on a room favours working in that room type.")]
    [SerializeField] private float dropAffinityDuration = 30f;

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridAgent        _agent;
    private MinionDefinition _definition;
    private SpawnSource      _source = SpawnSource.Placed;
    private bool             _initialised;
    private int              _level = 1;
    private float            _experience;
    private float            _health;
    private bool             _dead;
    private bool             _held;
    private bool             _fleeing;
    private MinionTemper     _temper;
    private TileType         _dropRoomType;
    private float            _dropAffinityUntil = float.NegativeInfinity;

    private WorkerBehaviour   _worker;
    private CreatureBehaviour _creature;
    private WageBehaviour     _wages;
    private SleepBehaviour    _sleep;
    private FoodBehaviour     _food;
    private SulkBehaviour     _sulk;
    private LeaveBehaviour    _leave;
    private CombatBehaviour   _combat;
    private bool              _inCombat;
    private float             _nextSense;
    private readonly Dictionary<MinionController, float> _ignoreUntil = new();
    private MinionBehaviour   _active;
    private MinionBehaviour   _errand;

    private int   _owedWages;
    private int   _missedPaydays;
    private float _angerPerUnpaidSalary;
    private bool  _wageTripPending;
    private float _tiredness;
    private float _hunger;
    private float _nextFoodAttempt;
    private float _nextSnackCheck;
    private bool  _recovering;
    private float _nextRecoverAttempt;
    private bool  _assignedSleep, _assignedFood;
    private float _nextMoodCheck;
    private float _nextLeaveAttempt;
    private readonly List<MinionBehaviour> _behaviours = new();

    public FactionID        Faction    => faction;
    public MinionDefinition Definition => _definition;
    public SpawnSource      Source     => _source;
    public int              Level      => _level;
    public float            Experience => _experience;
    public float            Health     => _health;
    public float            Anger      => _temper.Anger;
    public GridManager2D    Grid       => gridManager;
    public bool             IsFleeing  => _fleeing;
    public MinionBehaviour  ActiveBehaviour => _active;
    public WorkerBehaviour  Worker     => _worker;
    public CreatureBehaviour Creature  => _creature;
    /// <summary>The errand interrupting the active behaviour, if any (e.g. collecting wages).</summary>
    public MinionBehaviour  Errand     => _errand;

    /// <summary>Wages per payday at the current level (the Salary stat; 0 = not paid).</summary>
    public int Salary        => Mathf.Max(0, Mathf.RoundToInt(GetStatOr(MinionStat.Salary, 0f)));
    /// <summary>Wages owed and not yet collected.</summary>
    public int OwedWages     => _owedWages;
    /// <summary>Paydays in a row it wasn't paid in full.</summary>
    public int MissedPaydays => _missedPaydays;

    /// <summary>0 (rested) to 1 (exhausted).</summary>
    public float Tiredness   => _tiredness;
    /// <summary>Fully tired: angrier, weaker, and can't heal from food.</summary>
    public bool  IsExhausted => _tiredness >= 1f;
    /// <summary>False while exhausted — the Hatchery checks this before food heals.</summary>
    public bool  CanRecoverFromFood => !IsExhausted;
    /// <summary>Workers never sleep; nor does anything whose TirednessRate is 0.</summary>
    public bool  NeedsSleep  => !IsWorkerFirst && TirednessPerMinute > 0f;
    public float TirednessPerMinute => Mathf.Max(0f, GetStatOr(MinionStat.TirednessRate, fallbackTirednessPerMinute));
    public float SleepThreshold     => sleepThreshold;

    /// <summary>0 (full) to 1 (starving).</summary>
    public float Hunger     => _hunger;
    /// <summary>Fully hungry: angrier, weaker, slowly losing health.</summary>
    public bool  IsStarving => NeedsFood && _hunger >= 1f;
    /// <summary>Workers never get hungry; nor does anything whose HungerRate is 0.</summary>
    public bool  NeedsFood  => !IsWorkerFirst && HungerPerMinute > 0f;
    public float HungerPerMinute => Mathf.Max(0f, GetStatOr(MinionStat.HungerRate, fallbackHungerPerMinute));

    /// <summary>
    /// Hungry enough to go and eat — or, for a worker, hurt enough. False
    /// for a while after finding nothing to eat.
    /// </summary>
    public bool WantsFood => NeedsFood ? _hunger >= hungerThreshold : _health < MaxHealth * workerEatBelowHealth;

    /// <summary>Still worth eating another chicken: hungry at all, or (workers) not yet healed.</summary>
    public bool CouldEatMore => NeedsFood ? _hunger > 0.05f : _health < MaxHealth * 0.999f && CanRecoverFromFood;

    /// <summary>In bed to heal rather than (only) to rest.</summary>
    public bool  IsRecovering => _recovering;
    public float HealthFraction => MaxHealth > 0f ? _health / MaxHealth : 0f;
    public float RecoverUntilHealth => recoverUntilHealth;

    /// <summary>What it's doing right now, broadly — for the minion tracker and debugging.</summary>
    public MinionActivity Activity
    {
        get
        {
            if (_held) return MinionActivity.Held;
            if (_inCombat) return MinionActivity.Fighting;
            if (_errand == _wages) return MinionActivity.CollectingWages;
            if (_errand == _food)  return MinionActivity.Eating;
            if (_errand == _sleep) return MinionActivity.Sleeping;
            if (_errand == _sulk)  return MinionActivity.Sulking;
            if (_errand == _leave) return MinionActivity.Leaving;
            if (_active == _worker)
                return _worker.State == WorkerBehaviour.WorkerState.Idle ? MinionActivity.Idle : MinionActivity.Working;
            if (_active == _creature)
                return _creature.State switch
                {
                    CreatureBehaviour.CreatureState.ReportingForDuty => MinionActivity.Reporting,
                    CreatureBehaviour.CreatureState.GoingToWork      => MinionActivity.Working,
                    CreatureBehaviour.CreatureState.Working          => MinionActivity.Working,
                    _                                                => MinionActivity.Idle,
                };
            return MinionActivity.Idle;
        }
    }

    /// <summary>The behaviour actually running: the errand if there is one, else the active behaviour.</summary>
    private MinionBehaviour Running => _inCombat ? _combat : _errand != null ? _errand : _active;

    /// <summary>Fighting or fleeing an enemy.</summary>
    public bool InCombat => _inCombat;

    /// <summary>A worker (or WorkerSpawner minion without data): flees enemies rather than fighting.</summary>
    public bool IsWorkerKind => IsWorkerFirst;

    /// <summary>Multiplier for any work this minion does — above 1 while a slap's boost lasts.</summary>
    public float WorkSpeedMultiplier => _temper.WorkSpeedMultiplier;

    /// <summary>Health at the current level; the Inspector fallback until the data has a Health row.</summary>
    public float MaxHealth => GetStatOr(MinionStat.Health, fallbackMaxHealth);

    /// <summary>
    /// True if this minion does worker jobs: the Worker stance, a definition
    /// with canDoWorkerJobs set, or no data at all when WorkerSpawner made it.
    /// </summary>
    public bool CanDoWorkerJobs => _definition != null
        ? _definition.stance == MinionDefinition.MinionStance.Worker || _definition.canDoWorkerJobs
        : _source == SpawnSource.WorkerSpawner;

    private bool IsWorkerFirst => _definition != null
        ? _definition.stance == MinionDefinition.MinionStance.Worker
        : _source == SpawnSource.WorkerSpawner;

    // IHandTarget
    public GridAgent Agent   => _agent;
    public float     HandRadius => _agent.Radius;
    public bool      IsAlive => !_dead;
    public bool      IsHeld  => _held;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _agent  = GetComponent<GridAgent>();
        _temper = new MinionTemper(angerDecayPerSecond);

        // Prefab assets cannot reference scene objects, so a spawned minion
        // starts with gridManager null. Resolve it from the scene bootstrapper.
        if (gridManager == null)
        {
            gridManager = GameManager2D.Instance != null
                ? GameManager2D.Instance.Grid
                : FindAnyObjectByType<GridManager2D>();

            if (gridManager == null)
                Debug.LogError($"[MinionController] {name} could not resolve a " +
                               "GridManager2D. Is GameManager2D present in the scene?");
        }

        // Any prefab with this controller works: behaviours missing from it
        // are added with their default tuning.
        // (TryGetComponent, not GetComponent ?? — in the Editor a missing
        // component comes back as a fake null that ?? doesn't catch.)
        if (!TryGetComponent(out _worker))   _worker   = gameObject.AddComponent<WorkerBehaviour>();
        if (!TryGetComponent(out _creature)) _creature = gameObject.AddComponent<CreatureBehaviour>();
        if (!TryGetComponent(out _wages))    _wages    = gameObject.AddComponent<WageBehaviour>();
        if (!TryGetComponent(out _sleep))    _sleep    = gameObject.AddComponent<SleepBehaviour>();
        if (!TryGetComponent(out _food))     _food     = gameObject.AddComponent<FoodBehaviour>();
        if (!TryGetComponent(out _sulk))     _sulk     = gameObject.AddComponent<SulkBehaviour>();
        if (!TryGetComponent(out _leave))    _leave    = gameObject.AddComponent<LeaveBehaviour>();
        if (!TryGetComponent(out _combat))   _combat   = gameObject.AddComponent<CombatBehaviour>();
        GetComponents(_behaviours);
        foreach (var b in _behaviours) b.enabled = false;

        _health = MaxHealth;
    }

    private void Start()
    {
        // Spawners call Initialise straight after Instantiate, before Start.
        if (!_initialised) Initialise(faction, placedDefinition, placedLevel, SpawnSource.Placed);
    }

    private void OnEnable()
    {
        _agent.OnStandingInHazard += HandleHazard;
        KeeperHand.Register(this);
        if (!_all.Contains(this)) _all.Add(this);
    }

    private void OnDisable()
    {
        _agent.OnStandingInHazard -= HandleHazard;
        KeeperHand.Unregister(this);
        _all.Remove(this);
    }

    /// <summary>
    /// Makes this minion a particular MinionDefinition at a level. Called by
    /// whatever spawned it, immediately after Instantiate. A null definition
    /// keeps the prefab's own token and Inspector values.
    /// </summary>
    public void Initialise(FactionID owningFaction, MinionDefinition definition,
                           int level = 1, SpawnSource source = SpawnSource.Placed)
    {
        _initialised = true;
        faction      = owningFaction;
        _definition  = definition;
        _source      = source;
        _agent.SetFaction(owningFaction);

        definition?.ApplyTo(gameObject, _agent);

        _level      = definition != null ? Mathf.Clamp(level, 1, definition.maxLevel) : 1;
        _experience = definition != null ? definition.ExperienceForLevel(_level) : 0f;
        _health     = MaxHealth;

        SwitchTo(IsWorkerFirst ? _worker : _creature);
    }

    private void Update()
    {
        if (_dead) return;
        TickTiredness();
        TickHunger();
        SenseEnemies();

        if (_fleeing && !_held)
        {
            if (!_agent.IsInHazard)     EndFlee();
            else if (_agent.HasArrived) _agent.FleeToSafety();
        }

        StartDueErrand();
    }

    /// <summary>Starts the most pressing errand that's waiting, if the minion is free for one.</summary>
    private void StartDueErrand()
    {
        if (_errand != null || _held || _fleeing || _inCombat) return;

        if (_wageTripPending)
        {
            _wageTripPending = false;
            if (_owedWages > 0) StartErrand(_wages);
            return;
        }

        bool onDuty = _active != _creature || _creature.HasReported;
        if (!onDuty) return;

        // High: eat, recover, sleep. (Wages are above.)
        bool wantsFood = WantsFood || (_assignedFood && NeedsFood && _hunger >= assignedNeedThreshold);
        _assignedFood = false;
        if (Time.time >= _nextFoodAttempt && wantsFood) { StartErrand(_food); return; }

        _recovering = NeedsSleep && HealthFraction < recoverBelowHealth && Time.time >= _nextRecoverAttempt;
        bool wantsSleep = _recovering || (NeedsSleep && _tiredness >= sleepThreshold)
                       || (_assignedSleep && NeedsSleep && _tiredness >= assignedNeedThreshold);
        _assignedSleep = false;
        if (wantsSleep) { StartErrand(_sleep); return; }

        // Anger: leave, sulk.
        if (TryAngerJob()) return;

        // Idle: nap when a little tired.
        if (NeedsSleep && _tiredness >= idleSleepThreshold && Activity == MinionActivity.Idle)
        {
            StartErrand(_sleep);
            return;
        }

        TrySnack();
    }

    private bool TryAngerJob()
    {
        if (Time.time < _nextMoodCheck) return false;
        _nextMoodCheck = Time.time + 1f;

        float anger = Anger;
        if (anger >= furiousAnger && CanAbandon && Time.time >= _nextLeaveAttempt)
        {
            _nextLeaveAttempt = Time.time + leaveRetrySeconds;
            StartErrand(_leave);
            return true;
        }

        if (anger >= annoyedAnger && Random.value < sulkChancePerMinute / 60f)
        {
            StartErrand(_sulk);
            return true;
        }
        return false;
    }

    /// <summary>Called by CreatureBehaviour each frame it idles: grows grumpy.</summary>
    public void TickIdle(float deltaTime) =>
        _temper.AddGrievance(MinionTemper.Grievance.Idle, idleAngerPerMinute / 60f * deltaTime);

    /// <summary>Called by CreatureBehaviour each frame it works a room: idle grumpiness wears off.</summary>
    public void TickWorking(float deltaTime) =>
        _temper.AddGrievance(MinionTemper.Grievance.Idle, -workCalmPerMinute / 60f * deltaTime);

    /// <summary>Walked out through the portal for good (LeaveBehaviour).</summary>
    public void Desert()
    {
        if (_dead) return;
        Debug.Log($"[MinionController] {name} ({_definition?.displayName}) has left the {faction} dungeon.");
        Die();
    }

    /// <summary>A little peckish and a chicken right here: eat it on the spot.</summary>
    private void TrySnack()
    {
        if (!NeedsFood || _hunger < snackThreshold || Time.time < _nextSnackCheck) return;
        _nextSnackCheck = Time.time + 0.5f;

        var hatchery = HatcheryManager.Instance;
        if (hatchery == null || gridManager == null) return;

        var chicken = hatchery.FindFreeChickenNear(transform.position, snackReach * gridManager.CellSize);
        if (chicken != null) { _food.Target(chicken); StartErrand(_food); }
    }

    /// <summary>The eating trip is over.</summary>
    public void FinishFood() => EndErrand(_food);

    /// <summary>Called by FoodBehaviour when it finds nothing to eat: don't look again for a while.</summary>
    public void OnNoFood() => _nextFoodAttempt = Time.time + foodRetrySeconds;

    private void TickHunger()
    {
        if (!NeedsFood) return;

        _hunger = Mathf.Min(1f, _hunger + HungerPerMinute / 60f * Time.deltaTime);
        if (!IsStarving) return;

        float minutes = Time.deltaTime / 60f;
        _temper.AddGrievance(MinionTemper.Grievance.Starving, starvingAngerPerMinute * minutes);
        float damage = MaxHealth * starvingDamagePerMinute * minutes;
        _health = Mathf.Max(Mathf.Min(_health, 1f), _health - damage);
    }

    /// <summary>
    /// One chicken eaten: hunger down, and health up unless exhausted.
    /// Eaten below hungerThreshold, the anger of starving is forgotten.
    /// </summary>
    public void Eat(float hungerRelief, float healFraction)
    {
        if (_dead) return;
        if (NeedsFood) _hunger = Mathf.Max(0f, _hunger - hungerRelief);
        if (CanRecoverFromFood) Heal(MaxHealth * healFraction);
        if (_hunger < hungerThreshold) _temper.SetGrievance(MinionTemper.Grievance.Starving, 0f);
    }

    /// <summary>
    /// A chicken dropped on it by the Keeper's hand: eaten on the spot, full
    /// or not, and it stands still while it eats.
    /// </summary>
    public void ForceFeed(float hungerRelief, float healFraction, float eatSeconds)
    {
        if (_dead) return;
        Eat(hungerRelief, healFraction);
        _agent.Pause(eatSeconds);
    }

    private void TickTiredness()
    {
        if (!NeedsSleep || _errand == _sleep) return;

        bool working = _active == _worker && _worker.enabled && _worker.State == WorkerBehaviour.WorkerState.Working;
        float perSecond = TirednessPerMinute / 60f * (working ? workingTirednessMultiplier : 1f);
        _tiredness = Mathf.Min(1f, _tiredness + perSecond * Time.deltaTime);

        if (IsExhausted)
            _temper.AddGrievance(MinionTemper.Grievance.Exhaustion, exhaustedAngerPerMinute / 60f * Time.deltaTime);
    }

    // ── Behaviours ─────────────────────────────────────────────────────

    /// <summary>Makes another behaviour the active one, ending the current one.</summary>
    public void SwitchTo(MinionBehaviour next)
    {
        if (_active == next) return;

        if (_active != null)
        {
            _active.Deactivate();
            _active.enabled = false;
        }

        _active = next;
        if (_active == null || _dead) return;

        bool interrupted = _held || _fleeing || _errand != null || _inCombat;
        _active.enabled = !interrupted;
        _active.Activate();
        if (interrupted) _active.Pause();
    }

    /// <summary>
    /// Sends the minion on an errand: the active behaviour is paused (as if
    /// picked up) until the errand calls EndErrand. Ignored if already on one.
    /// </summary>
    public void StartErrand(MinionBehaviour errand)
    {
        if (_dead || errand == null || _errand != null) return;

        PauseActive();
        _errand = errand;

        bool interrupted = _held || _fleeing;
        _errand.enabled = !interrupted;
        _errand.Activate();
        if (interrupted && _errand == errand) _errand.Pause();
    }

    /// <summary>Ends the errand and hands the minion back to its active behaviour.</summary>
    public void EndErrand(MinionBehaviour errand)
    {
        if (errand == null || _errand != errand) return;

        _errand.Deactivate();
        _errand.enabled = false;
        _errand = null;

        if (!_held && !_fleeing) ResumeActive();
    }

    private void DropErrand()
    {
        if (_errand == null) return;
        _errand.Deactivate();
        _errand.enabled = false;
        _errand = null;
    }

    /// <summary>
    /// Called by CreatureBehaviour once the minion has reported to the heart.
    /// A creature allowed worker jobs starts helping with them from here.
    /// </summary>
    public void OnReportedForDuty()
    {
        DungeonHeart.Instance?.NotifyReported(faction, this);
        if (CanDoWorkerJobs) SwitchTo(_worker);
    }

    private void PauseActive()
    {
        var running = Running;
        if (running == null || !running.enabled) return;
        running.Pause();
        running.enabled = false;
    }

    private void ResumeActive()
    {
        var running = Running;
        if (running == null || running.enabled || _dead) return;
        running.enabled = true;
        running.Resume();
    }

    // ── Wages ──────────────────────────────────────────────────────────

    /// <summary>
    /// Payday for this minion's faction: adds its Salary to what it's owed
    /// and sends it to collect. A minion with no Salary is skipped.
    /// </summary>
    public void OnPayday(float angerPerUnpaidSalary)
    {
        if (_dead || Salary <= 0) return;

        _owedWages           += Salary;
        _angerPerUnpaidSalary = angerPerUnpaidSalary;
        _wageTripPending      = true;   // starts once it's free (e.g. after waking)
    }

    /// <summary>Gold handed over towards what it's owed.</summary>
    public void ReceiveWages(int amount)
    {
        if (amount > 0) _owedWages = Mathf.Max(0, _owedWages - amount);
    }

    /// <summary>
    /// The wage trip is over. Anything still owed is remembered for the next
    /// payday, and sets the grievance in proportion to the debt; paid in full
    /// clears it.
    /// </summary>
    public void FinishWageTrip()
    {
        if (_owedWages > 0)
        {
            _missedPaydays++;
            int salary = Mathf.Max(1, Salary);
            _temper.SetGrievance(MinionTemper.Grievance.UnpaidWages, _angerPerUnpaidSalary * _owedWages / salary);
        }
        else
        {
            _missedPaydays    = 0;
            _temper.SetGrievance(MinionTemper.Grievance.UnpaidWages, 0f);
        }
        EndErrand(_wages);
    }

    // ── Sleep ──────────────────────────────────────────────────────────

    /// <summary>Sleep takes tiredness off (never below 0).</summary>
    public void Rest(float amount)
    {
        if (amount > 0f) _tiredness = Mathf.Max(0f, _tiredness - amount);
    }

    /// <summary>
    /// Woken fully rested. In its own bed, the anger of exhaustion and of
    /// having had no bed is slept off.
    /// </summary>
    public void FinishSleep(bool sleptInBed)
    {
        // Came to recover but had no bed to heal in: don't try again straight away.
        if (_recovering && !sleptInBed) _nextRecoverAttempt = Time.time + 30f;
        _recovering = false;
        _temper.SetGrievance(MinionTemper.Grievance.Exhaustion, 0f);
        if (sleptInBed) _temper.SetGrievance(MinionTemper.Grievance.NoBed, 0f);
        EndErrand(_sleep);
    }

    /// <summary>Had to sleep on the floor: lasting anger of at least this much until it sleeps in a bed.</summary>
    public void OnNoBed(float anger)
    {
        var cause = MinionTemper.Grievance.NoBed;
        _temper.SetGrievance(cause, Mathf.Max(_temper.GetGrievance(cause), anger));
    }

    /// <summary>Its bed was sold or captured. It looks for a new one when it next sleeps.</summary>
    public void OnBedLost(float anger) => _temper.AddGrievance(MinionTemper.Grievance.NoBed, anger);

    /// <summary>Restores health, up to its maximum.</summary>
    public void Heal(float amount)
    {
        if (_dead || amount <= 0f) return;
        _health = Mathf.Min(MaxHealth, _health + amount);
    }

    // ── Stats & levelling ──────────────────────────────────────────────

    /// <summary>This minion's value for a stat at its current level (0 with no data).</summary>
    public float GetStat(MinionStat stat) =>
        (_definition != null ? _definition.GetStat(stat, _level) : 0f) * Exhaustion(stat);

    /// <summary>
    /// A stat at the current level if the data has a row for it, otherwise
    /// the caller's own fallback — so Inspector tuning stays in charge until
    /// the workbook is filled in.
    /// </summary>
    public float GetStatOr(MinionStat stat, float fallback) =>
        (_definition != null && _definition.TryGetAuthoredStat(stat, _level, out float v) ? v : fallback) * Exhaustion(stat);

    /// <summary>The penalty on a stat: exhausted and/or starving multiply performance stats down; others are untouched.</summary>
    private float Exhaustion(MinionStat stat)
    {
        if (!IsPerformanceStat(stat)) return 1f;
        float m = 1f;
        if (IsExhausted) m *= exhaustedStatMultiplier;
        if (IsStarving)  m *= starvingStatMultiplier;
        return m;
    }

    private static bool IsPerformanceStat(MinionStat stat) => stat switch
    {
        MinionStat.Strength or MinionStat.Accuracy or MinionStat.Dexterity or MinionStat.Speed or
        MinionStat.Magic or MinionStat.SkillResearch or MinionStat.SkillTrain or MinionStat.SkillBuild or
        MinionStat.SkillPray or MinionStat.SkillTorture => true,
        _ => false,
    };

    /// <summary>
    /// Adds experience (from fighting or training) and levels up as far as
    /// the definition's Experience curve allows. Returns true on level-up.
    /// </summary>
    public bool AddExperience(float amount)
    {
        if (_definition == null || amount <= 0f || _dead) return false;

        _experience += amount;
        int newLevel = _definition.LevelForExperience(_experience);
        if (newLevel <= _level) return false;

        // Keep the same fraction of health across the level-up.
        float fraction = MaxHealth > 0f ? _health / MaxHealth : 1f;
        _level  = newLevel;
        _health = fraction * MaxHealth;
        return true;
    }

    // ── Hazards ────────────────────────────────────────────────────────

    private void HandleHazard(GridCell cell)
    {
        if (_dead || _held) return;

        var   def = gridManager != null ? gridManager.GetDefinition(cell.TileType) : null;
        float dps = def != null ? def.hazardDamagePerSecond : 0f;

        TakeDamage(dps * Time.deltaTime);
        if (_dead || _fleeing) return;

        // Drop whatever it was doing, get to safety, reassess once clear.
        _fleeing = true;
        PauseActive();

        if (!_agent.FleeToSafety())
            Debug.LogWarning($"[MinionController] {name} is trapped in a hazard " +
                             "with no reachable safe cell.");
    }

    private void EndFlee()
    {
        _fleeing = false;
        ResumeActive();
    }

    // ── Keeper's hand ──────────────────────────────────────────────────

    /// <summary>
    /// Lifted by the hand: the active behaviour drops what it was doing (a
    /// worker keeps any gold it carries) and the agent is switched off so the
    /// hand can move the token freely.
    /// </summary>
    public void OnPickedUp()
    {
        if (_dead || _held) return;

        PauseActive();
        _held              = true;
        _fleeing           = false;
        _dropAffinityUntil = float.NegativeInfinity;

        _agent.Stop();
        _agent.enabled = false;
    }

    /// <summary>
    /// Set down: switch the agent back on where it landed and let the active
    /// behaviour reassess from here. Landing on a room is remembered for a while.
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

        AssignFromDrop(cell);
        ResumeActive();

        // Dropped right beside an enemy, even a worker stands and fights.
        var combat = CombatSystem.Instance;
        var enemy  = combat != null ? combat.FindVisibleEnemy(this) : null;
        if (enemy != null)
        {
            Vector3 d = enemy.transform.position - transform.position;
            float near = combat.DropFightRadius * combat.CellSize;
            if (d.x * d.x + d.z * d.z <= near * near) EnterCombat(enemy, fight: true);
        }
    }

    /// <summary>
    /// Assigned jobs: dropped on a Lair it sleeps, on a Hatchery it eats, on
    /// a Treasury it collects what it's owed — sooner than it would on its
    /// own (assignedNeedThreshold). A sulk is abandoned for it; any other
    /// errand carries on first.
    /// </summary>
    private void AssignFromDrop(GridCell cell)
    {
        bool assigned = false;
        if (cell.TileType == TileType.Lair)          assigned = _assignedSleep = NeedsSleep && _tiredness >= assignedNeedThreshold;
        else if (cell.TileType == TileType.Hatchery) assigned = _assignedFood  = NeedsFood && _hunger >= assignedNeedThreshold;
        else if (cell.TileType == TileType.Treasury && _owedWages > 0) assigned = _wageTripPending = true;

        if (assigned && _errand == _sulk) DropErrand();
    }

    /// <summary>
    /// The room type this minion was recently dropped on, if the drop is
    /// still fresh. For room-work behaviour to favour that room.
    /// </summary>
    public bool TryGetDropAffinity(out TileType roomType)
    {
        roomType = _dropRoomType;
        return Time.time < _dropAffinityUntil;
    }

    /// <summary>Only minions that came through the portal can leave by it.</summary>
    public bool CanAbandon => _source == SpawnSource.Portal;

    /// <summary>Dropped on the portal: leaves the dungeon, freeing its population slot.</summary>
    public void OnAbandon()
    {
        if (_dead || !CanAbandon) return;
        Die();
    }

    public void OnSlapped(in HandSlap slap)
    {
        if (_dead) return;

        _temper.ApplySlap(slap);
        TakeDamage(slap.DamageFor(_health, MaxHealth));
    }

    /// <summary>A room worth remembering a drop on — any room except Bridge.</summary>
    private bool IsRoom(TileType type)
    {
        var def = gridManager != null ? gridManager.GetDefinition(type) : null;
        return def != null && def.isRoom && !def.placesOnLiquid;
    }

    // ── Damage and death ───────────────────────────────────────────────

    public void TakeDamage(float amount)
    {
        if (_dead || amount <= 0f) return;

        _health -= amount;
        if (_health <= 0f) Die();
    }

    /// <summary>
    /// Hit by an enemy: it fights back (a worker runs, unless cornered), and
    /// the player hears their minions are under attack. A killing blow earns
    /// the attacker its bonus experience.
    /// </summary>
    public void TakeDamage(float amount, MinionController attacker)
    {
        if (_dead || amount <= 0f) return;

        Announcer.Announce(faction, "MinionsUnderAttack", transform.position);
        TakeDamage(amount);

        if (_dead) { CombatSystem.Instance?.AwardKill(attacker, this); return; }
        if (attacker != null && attacker.IsAlive) EnterCombat(attacker);
    }

    // ── Combat ─────────────────────────────────────────────────────────

    /// <summary>Looks around every so often; an enemy in sight starts a fight.</summary>
    private void SenseEnemies()
    {
        if (_inCombat || _held || Time.time < _nextSense) return;
        var combat = CombatSystem.Instance;
        if (combat == null) return;
        _nextSense = Time.time + combat.SenseInterval * (0.75f + 0.5f * Random.value);

        var enemy = combat.FindVisibleEnemy(this, IsIgnoring);
        if (enemy != null) { EnterCombat(enemy); return; }

        if (!IsWorkerFirst && combat.FindVisibleEnemyHeart(this, out var heartFaction, out var heartCell))
            EnterHeartAttack(heartFaction, heartCell);
    }

    /// <summary>
    /// Fights an enemy minion — above everything else. A worker flees
    /// instead unless 'fight' is set (cornered, or dropped beside it).
    /// Allies nearby are called in.
    /// </summary>
    public void EnterCombat(MinionController enemy, bool fight = false)
    {
        if (_dead || enemy == null || !CombatSystem.IsEnemy(faction, enemy.Faction)) return;
        bool fights = fight || !IsWorkerFirst;

        if (_inCombat) { _combat.Engage(enemy, fights); return; }

        BeginCombat();
        _combat.Begin(enemy, fights);
        FinishBeginCombat();
        CombatSystem.Instance?.Rally(this, enemy);
    }

    /// <summary>Attacks an enemy Dungeon Heart it has seen (creatures only).</summary>
    public void EnterHeartAttack(FactionID heartFaction, GridCell heartCell)
    {
        if (_dead || _inCombat || IsWorkerFirst) return;
        BeginCombat();
        _combat.BeginHeart(heartFaction, heartCell);
        FinishBeginCombat();
    }

    private void BeginCombat()
    {
        PauseActive();      // whatever was running — work, an errand — waits
        _inCombat = true;
    }

    private void FinishBeginCombat()
    {
        bool interrupted = _held || _fleeing;
        _combat.enabled = !interrupted;
        if (interrupted) _combat.Pause();
    }

    /// <summary>No enemy left in sight: back to whatever it was doing.</summary>
    public void EndCombat()
    {
        if (!_inCombat) return;
        _combat.Deactivate();
        _combat.enabled = false;
        _inCombat = false;
        if (!_held && !_fleeing) ResumeActive();
    }

    /// <summary>Leaves an enemy it couldn't reach or hit alone for a while.</summary>
    public void Ignore(MinionController enemy, float seconds)
    {
        if (enemy != null) _ignoreUntil[enemy] = Time.time + seconds;
    }

    public bool IsIgnoring(MinionController enemy) =>
        enemy != null && _ignoreUntil.TryGetValue(enemy, out float until) && Time.time < until;

    private void DropCombat()
    {
        if (!_inCombat) return;
        _combat.Deactivate();
        _combat.enabled = false;
        _inCombat = false;
    }

    public void Die()
    {
        if (_dead) return;

        DropCombat();
        DropErrand();
        if (_active != null) { _active.Deactivate(); _active.enabled = false; }
        _dead = true;
        LairManager.Instance?.Release(this);

        NotifyLeftFaction();
        Destroy(gameObject, 0.1f);
    }

    /// <summary>Tells whatever is counting this minion that it's gone.</summary>
    private void NotifyLeftFaction()
    {
        switch (_source)
        {
            case SpawnSource.Portal:
                MinionSummoner.Instance?.NotifyCreatureRemoved(faction, _definition);
                break;
            case SpawnSource.WorkerSpawner:
                WorkerSpawner.GetForFaction(faction)?.NotifyWorkerDied();
                break;
        }
    }

    // ── Conversion ─────────────────────────────────────────────────────

    /// <summary>
    /// Switches this minion to a new faction in place — same GameObject, same
    /// token, just a new owner. Groundwork for capture/torture-to-convert:
    /// nothing calls this yet, but when a future Prison room finishes
    /// converting a captured minion, this is the one call it needs to make.
    ///
    /// Starts its behaviour over, so a creature treks to its new masters'
    /// heart before counting as truly theirs, same as a freshly summoned one.
    /// </summary>
    public void ConvertTo(FactionID newFaction)
    {
        if (_dead || newFaction == faction) return;

        DropCombat();
        DropErrand();
        if (_active != null) { _active.Deactivate(); _active.enabled = false; _active = null; }
        _creature.ForgetReport();   // reports to its new masters' heart

        // Its new masters owe it nothing yet, it holds no grudge against them,
        // and its old bed isn't theirs.
        _owedWages       = 0;
        _missedPaydays   = 0;
        _wageTripPending = false;
        _temper.ClearGrievances();
        LairManager.Instance?.Release(this);

        NotifyLeftFaction();
        faction = newFaction;
        _agent.SetFaction(newFaction);
        if (_source == SpawnSource.Portal)
            MinionSummoner.Instance?.NotifyCreatureJoined(faction, _definition);
        else
            _source = SpawnSource.Placed; // the new side's worker count never included it

        SwitchTo(IsWorkerFirst ? _worker : _creature);
    }
}
