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
/// </summary>
[RequireComponent(typeof(GridAgent))]
public class MinionController : MonoBehaviour, IHandTarget
{
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
    private MinionBehaviour   _active;
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
    }

    private void OnDisable()
    {
        _agent.OnStandingInHazard -= HandleHazard;
        KeeperHand.Unregister(this);
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
        if (!_fleeing || _held || _dead) return;

        if (!_agent.IsInHazard)     EndFlee();
        else if (_agent.HasArrived) _agent.FleeToSafety();
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

        bool interrupted = _held || _fleeing;
        _active.enabled = !interrupted;
        _active.Activate();
        if (interrupted) _active.Pause();
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
        if (_active == null || !_active.enabled) return;
        _active.Pause();
        _active.enabled = false;
    }

    private void ResumeActive()
    {
        if (_active == null || _active.enabled || _dead) return;
        _active.enabled = true;
        _active.Resume();
    }

    // ── Stats & levelling ──────────────────────────────────────────────

    /// <summary>This minion's value for a stat at its current level (0 with no data).</summary>
    public float GetStat(MinionStat stat) =>
        _definition != null ? _definition.GetStat(stat, _level) : 0f;

    /// <summary>
    /// A stat at the current level if the data has a row for it, otherwise
    /// the caller's own fallback — so Inspector tuning stays in charge until
    /// the workbook is filled in.
    /// </summary>
    public float GetStatOr(MinionStat stat, float fallback) =>
        _definition != null && _definition.TryGetAuthoredStat(stat, _level, out float v) ? v : fallback;

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

        ResumeActive();
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

    private static bool IsRoom(TileType type) =>
        type == TileType.RoomA || type == TileType.RoomB || type == TileType.RoomC;

    // ── Damage and death ───────────────────────────────────────────────

    public void TakeDamage(float amount)
    {
        if (_dead || amount <= 0f) return;

        _health -= amount;
        if (_health <= 0f) Die();
    }

    public void Die()
    {
        if (_dead) return;

        if (_active != null) { _active.Deactivate(); _active.enabled = false; }
        _dead = true;

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

        if (_active != null) { _active.Deactivate(); _active.enabled = false; _active = null; }

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
