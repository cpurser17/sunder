using System.Collections;
using UnityEngine;

/// <summary>
/// One imp: state machine, grid movement, and job execution.
///
/// Imps PULL work. When idle, the imp asks ImpTaskManager for a job and gets
/// the nearest reachable one (with some weighted randomness so a group does not
/// all converge on the same tile). Nothing is pushed onto a specific imp, which
/// is what stops every imp servicing the map in the same fixed order.
///
/// States
/// ------
/// Idle          asking for work each jobRequestInterval
/// MovingToJob   pathing to the job's work cell
/// Working       damaging a dig target, or progressing a claim / reinforce
/// MovingToVault hauling gold to the nearest treasury by travel distance;
///               the mining slot is released when this run begins
/// Depositing    brief pause at the treasury, then back to Idle for new work
/// Fleeing       pushed into a hazard — drops the job and escapes
/// Dead          releases its slot and unregisters
/// </summary>
[RequireComponent(typeof(GridAgent))]
public class ImpController : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("Work")]
    [SerializeField] private float damagePerSecond   = 20f;
    [Tooltip("Seconds to convert Cave to Tunnel, or to capture an enemy tile.")]
    [SerializeField] private float claimDuration     = 2f;
    [Tooltip("Seconds to convert Stone into Wall.")]
    [SerializeField] private float reinforceDuration = 3f;
    [Tooltip("Seconds between work ticks. Damage and progress scale by this.")]
    [SerializeField] private float workTickInterval  = 0.25f;

    [Tooltip("Gap left between the token edge and the face being worked, in "
             + "world units. 0 puts the token flush against the wall.")]
    [SerializeField] private float wallGap           = 0.02f;

    [Header("Carrying")]
    [SerializeField] private int   maxCarryCapacity = 30;
    [SerializeField] private float depositDuration  = 1f;
    [SerializeField] private TileType treasuryRoomType = TileType.RoomA;

    [Tooltip("Chance per second of banking a part load while mining. Stops "
             + "imps carrying residual gold around after a vein runs out.")]
    [SerializeField] private float earlyDepositChancePerSecond = 0.08f;

    [Tooltip("Seconds before retrying when no treasury can be reached.")]
    [SerializeField] private float depositRetryInterval = 3f;

    [Header("Survival")]
    [SerializeField] private float maxHealth = 100f;

    [Header("Job requests")]
    [Tooltip("Seconds between requests while idle. Stops idle imps hammering " +
             "the registry every frame when there is no work.")]
    [SerializeField] private float jobRequestInterval = 0.25f;

    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridAgent      _agent;
    private GridPathfinder _pathfinder;
    private ImpState       _state = ImpState.Idle;
    private DungeonJob     _job;
    private ImpTaskManager _taskManager;

    private int       _carryingGold;
    private float     _health;
    private float     _damageAccumulator;   // fractional damage carry-over
    private float     _workProgress;        // seconds spent on claim / reinforce
    private float     _nextJobRequest;
    private float     _nextDepositAttempt;
    private Coroutine _workCoroutine;
    private bool      _completing;          // suppresses re-entrant abandon

    public bool       IsIdle  => _state == ImpState.Idle;
    public FactionID  Faction => faction;
    public ImpState   State   => _state;
    public DungeonJob Job     => _job;
    public GridCell   Cell    => _agent != null ? _agent.CurrentCell : null;

    public enum ImpState
    {
        Idle, MovingToJob, Working, MovingToVault, Depositing, Fleeing, Dead
    }

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _agent  = GetComponent<GridAgent>();
        _health = maxHealth;

        // Prefab assets cannot reference scene objects, so a spawned imp starts
        // with gridManager null. Resolve it from the scene bootstrapper.
        if (gridManager == null)
        {
            gridManager = GameManager2D.Instance != null
                ? GameManager2D.Instance.Grid
                : FindAnyObjectByType<GridManager2D>();

            if (gridManager == null)
                Debug.LogError($"[ImpController] {name} could not resolve a " +
                               "GridManager2D. Is GameManager2D present in the scene?");
        }

        if (gridManager != null) _pathfinder = new GridPathfinder(gridManager);
    }

    private void OnEnable()  => _agent.OnStandingInHazard += HandleHazard;
    private void OnDisable() => _agent.OnStandingInHazard -= HandleHazard;

    /// <summary>Called by ImpSpawner immediately after instantiation.</summary>
    public void Initialise(FactionID owningFaction, ImpTaskManager taskManager)
    {
        faction      = owningFaction;
        _taskManager = taskManager;
    }

    private void Update()
    {
        switch (_state)
        {
            case ImpState.Idle:
                TryRequestJob();
                break;

            case ImpState.MovingToJob:
                if (_agent.HasArrived) BeginWorking();
                break;

            case ImpState.MovingToVault:
                if (_agent.HasArrived) StartCoroutine(DepositRoutine());
                break;

            case ImpState.Fleeing:
                if (!_agent.IsInHazard)      GoIdle();
                else if (_agent.HasArrived)  _agent.FleeToSafety();
                break;
        }
    }

    // ── Requesting work ────────────────────────────────────────────────

    private void TryRequestJob()
    {
        if (Time.time < _nextJobRequest) return;
        _nextJobRequest = Time.time + jobRequestInterval;

        _taskManager ??= ImpTaskManager.GetForFaction(faction);
        if (_taskManager == null) return;

        var job = _taskManager.RequestJob(this);
        if (job == null) return;

        StartJob(job);
    }

    private void StartJob(DungeonJob job)
    {
        _job          = job;
        _workProgress = 0f;

        // Dig targets track HP on the tile so several imps chip the same block.
        if (job.Type == JobType.Dig)
        {
            var def = gridManager.GetDefinition(job.Target.TileType);
            if (def != null)
            {
                if (job.Target.CurrentHP < 0) job.Target.InitialiseHP(def.maxHitPoints);

                bool wealthTile = job.Target.TileType == TileType.Gold ||
                                  job.Target.TileType == TileType.Gem;
                if (wealthTile && job.Target.WealthRemaining == 0)
                    job.Target.InitialiseWealth(def.wealthCapacity);
            }
        }

        if (!_agent.SetDestination(WorkPositionFor(job)))
        {
            // Unreachable after all — hand the slot back.
            _taskManager.ReleaseJob(job, this);
            _job = null;
            return;
        }

        SetState(ImpState.MovingToJob);
    }

    /// <summary>
    /// Called by ImpTaskManager when a job this imp holds is removed — the tile
    /// was mined by someone else, claimed, or otherwise stopped qualifying.
    /// </summary>
    public void OnJobCancelled(DungeonJob job)
    {
        if (_job != job) return;
        AbandonJob();
    }

    // ── Working ────────────────────────────────────────────────────────

    private void BeginWorking()
    {
        if (_job == null) { GoIdle(); return; }

        SetState(ImpState.Working);
        _workCoroutine = StartCoroutine(WorkRoutine());
    }

    private IEnumerator WorkRoutine()
    {
        while (_job != null && _state == ImpState.Working)
        {
            yield return new WaitForSeconds(workTickInterval);
            if (_job == null || _state != ImpState.Working) yield break;

            switch (_job.Type)
            {
                case JobType.Claim:     if (TickClaim())     yield break; break;
                case JobType.Reinforce: if (TickReinforce()) yield break; break;
                case JobType.Dig:       if (TickDig())       yield break; break;
            }
        }
    }

    /// <summary>Returns true when the job finished and the routine should stop.</summary>
    private bool TickClaim()
    {
        _workProgress += workTickInterval;
        if (_workProgress < claimDuration) return false;

        var cell = _job.Target;

        _completing = true;
        if (cell.TileType == TileType.Cave)
        {
            // Unclaimed cavern becomes our tunnel.
            gridManager.SetTileType(cell, TileType.Tunnel, faction);
        }
        else
        {
            // Captured from another faction: keep the room, change the flag.
            gridManager.SetOwner(cell, faction);
        }
        _completing = false;

        CompleteJob();
        return true;
    }

    private bool TickReinforce()
    {
        _workProgress += workTickInterval;
        if (_workProgress < reinforceDuration) return false;

        _completing = true;
        gridManager.SetTileType(_job.Target, TileType.Wall, faction);
        _completing = false;

        CompleteJob();
        return true;
    }

    private bool TickDig()
    {
        var cell = _job.Target;

        // Accumulate fractional damage so non-integer DPS stays accurate across
        // ticks instead of being rounded away every time.
        _damageAccumulator += damagePerSecond * workTickInterval;
        int damage = Mathf.FloorToInt(_damageAccumulator);
        if (damage <= 0) return false;
        _damageAccumulator -= damage;

        bool wealthTile = cell.TileType == TileType.Gold ||
                          cell.TileType == TileType.Gem;

        if (wealthTile)
        {
            int space = maxCarryCapacity - _carryingGold;
            _carryingGold += cell.ExtractWealth(damage, space);
        }

        var  def            = gridManager.GetDefinition(cell.TileType);
        bool indestructible = def != null && def.isIndestructible;

        // Gold is spent rather than smashed — it collapses once drained.
        bool depleted = cell.TileType == TileType.Gold && cell.WealthRemaining <= 0;

        if (!indestructible && (cell.ApplyDamage(damage) || depleted))
        {
            OnTileDestroyed();
            return true;
        }

        // Full load — bank it. If no treasury can be reached, keep mining
        // rather than freezing: extraction is already capped at capacity, so
        // nothing is lost and the deposit is retried on a later tick.
        if (_carryingGold >= maxCarryCapacity)
            return TryStartDeposit();

        // Occasionally bank a part load, so a vein running out does not leave
        // an imp carrying residual gold around indefinitely.
        if (_carryingGold > 0 && _taskManager != null &&
            _taskManager.NextRandom01() < earlyDepositChancePerSecond * workTickInterval)
            return TryStartDeposit();

        return false;
    }

    private void OnTileDestroyed()
    {
        var cell = _job.Target;

        _completing = true;
        gridManager.SetTileType(cell, TileType.Cave);
        DigSelectionManager.Instance?.GetController(faction)?.DequeueCell(cell);
        _completing = false;

        // Releasing the slot happens inside TryStartDeposit; if we cannot
        // reach a treasury just finish the job and carry the gold onward.
        if (_carryingGold > 0 && TryStartDeposit()) return;
        CompleteJob();
    }

    // ── Treasury ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts a deposit run if the imp is carrying gold and a treasury is
    /// reachable. Returns true if the run began.
    ///
    /// Starting a run RELEASES the mining slot, so another imp can take over
    /// the face while this one walks its load back. That is the whole point of
    /// separating hauling from digging.
    /// </summary>
    private bool TryStartDeposit()
    {
        if (_carryingGold <= 0) return false;
        if (Time.time < _nextDepositAttempt) return false;

        var treasury = FindNearestTreasuryByPath();
        if (treasury == null || !_agent.SetDestination(treasury))
        {
            _nextDepositAttempt = Time.time + depositRetryInterval;
            return false;
        }

        if (_job != null)
        {
            _taskManager?.ReleaseJob(_job, this);
            _job = null;
        }

        SetState(ImpState.MovingToVault);
        return true;
    }

    private IEnumerator DepositRoutine()
    {
        SetState(ImpState.Depositing);
        yield return new WaitForSeconds(depositDuration);

        GameManager2D.Instance?.GetWallet(faction)?.Earn(_carryingGold);
        _carryingGold = 0;

        // The slot was given up when the run started, so simply ask for work
        // again — often the same dig job, if a slot has come free.
        GoIdle();
    }

    /// <summary>
    /// Nearest treasury by TRAVEL distance, not straight-line distance.
    ///
    /// Straight-line selection sends imps toward whichever treasury tile is
    /// geometrically closest, which may sit behind a wall — so they walk over
    /// perfectly good deposit tiles to reach a back corner, and if the chosen
    /// tile cannot be pathed to at all the move simply fails.
    /// </summary>
    private GridCell FindNearestTreasuryByPath()
    {
        var from = _agent.CurrentCell;
        if (from == null || _pathfinder == null) return null;

        return _pathfinder.FindNearestMatching(
            from,
            c => c.TileType == treasuryRoomType && c.Owner == faction,
            _agent.Capability, faction, _agent.Radius);
    }

    // ── Hazard response ────────────────────────────────────────────────

    private void HandleHazard(GridCell cell)
    {
        var   def = gridManager.GetDefinition(cell.TileType);
        float dps = def != null ? def.hazardDamagePerSecond : 0f;

        _health -= dps * Time.deltaTime;
        if (_health <= 0f) { Die(); return; }

        if (_state == ImpState.Fleeing) return;

        // Forget the job, get to safety, look for other work once clear.
        AbandonJob();
        SetState(ImpState.Fleeing);

        if (!_agent.FleeToSafety())
            Debug.LogWarning($"[ImpController] {name} is trapped in a hazard " +
                             "with no reachable safe cell.");
    }

    // ── Job exits ──────────────────────────────────────────────────────

    private void CompleteJob()
    {
        if (_job == null && _state == ImpState.Idle) return;

        _taskManager?.ReleaseJob(_job, this);
        _job = null;
        GoIdle();
    }

    private void AbandonJob()
    {
        // GridManager2D.SetTileType raises OnTileChanged synchronously, so
        // finishing a job re-enters here via the task manager before completion
        // has run. Ignore those — CompleteJob is already handling it.
        if (_completing) return;

        if (_workCoroutine != null) { StopCoroutine(_workCoroutine); _workCoroutine = null; }

        if (_job != null)
        {
            // Only reset tile HP if we were the last one working it.
            if (_job.Type == JobType.Dig && _job.Workers.Count <= 1)
                _job.Target.ResetHP();

            _taskManager?.ReleaseJob(_job, this);
            _job = null;
        }

        _agent.Stop();

        // Fleeing sets its own state immediately after, so don't stomp it.
        if (_state != ImpState.Fleeing && _state != ImpState.Dead) GoIdle();
    }

    /// <summary>
    /// Returns the imp to Idle so the task manager will hand it new work.
    ///
    /// Every job exit funnels through here. An exit that leaves the state
    /// non-Idle, or leaves a dangling job reference, is what makes an imp stand
    /// still forever.
    /// </summary>
    private void GoIdle()
    {
        _job          = null;
        _workProgress = 0f;
        SetState(ImpState.Idle);
        _nextJobRequest = 0f;   // ask for new work on the next frame

        // Never pick up unrelated work while still holding gold — bank it
        // first. This is the backstop that guarantees no residual load is
        // carried around indefinitely.
        if (_carryingGold > 0) TryStartDeposit();
    }

    /// <summary>
    /// Where in the work cell this imp should stand.
    ///
    /// Dig and Reinforce push the imp up against the face of the tile it is
    /// working, rather than standing in the middle of its own cell swinging at
    /// nothing. Claim jobs have WorkCell == Target, so the direction below is
    /// zero and the imp stands centred on the tile it is claiming.
    ///
    /// Stand-off is derived from this imp's own radius, so a larger minion
    /// stops further out and its token still never touches the wall.
    /// </summary>
    private Vector3 WorkPositionFor(DungeonJob job)
    {
        Vector3 workCentre   = gridManager.CellToWorld(job.WorkCell.X, job.WorkCell.Y);
        Vector3 targetCentre = gridManager.CellToWorld(job.Target.X,   job.Target.Y);

        Vector3 toFace = targetCentre - workCentre;
        toFace.y = 0f;

        // Claim: same cell, so no direction and no offset.
        if (toFace.sqrMagnitude < 0.0001f) return workCentre;

        // Stop short of the shared edge by the token radius plus a small gap.
        float half     = gridManager.CellSize * 0.5f;
        float standOff = Mathf.Max(0f, half - _agent.Radius - wallGap);

        return workCentre + toFace.normalized * standOff;
    }

    private void SetState(ImpState state) => _state = state;

    // ── Damage and death ───────────────────────────────────────────────

    public void TakeDamage(float amount)
    {
        _health -= amount;
        if (_health <= 0f) Die();
    }

    public void Die()
    {
        if (_state == ImpState.Dead) return;

        if (_workCoroutine != null) StopCoroutine(_workCoroutine);

        if (_job != null)
        {
            if (_job.Type == JobType.Dig && _job.Workers.Count <= 1)
                _job.Target.ResetHP();
            _taskManager?.ReleaseJob(_job, this);
            _job = null;
        }

        _taskManager?.UnregisterImp(this);
        ImpSpawner.GetForFaction(faction)?.NotifyImpDied();

        SetState(ImpState.Dead);
        Destroy(gameObject, 0.1f);
    }
}
