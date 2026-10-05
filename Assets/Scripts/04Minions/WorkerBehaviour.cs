using System.Collections;
using UnityEngine;

/// <summary>
/// Worker jobs: digging, claiming, reinforcing, hauling gold to the
/// treasury and collecting loose gold piles. The active behaviour for every Worker-stance minion, and
/// for any other minion whose definition sets canDoWorkerJobs, once it has
/// reported for duty.
///
/// Workers PULL work. When idle, the worker asks its faction's WorkerTaskManager
/// for a job and gets the nearest reachable one (with some weighted
/// randomness so a group does not all converge on the same tile). Nothing is
/// pushed onto a specific worker, which is what stops every worker servicing
/// the map in the same fixed order.
///
/// States
/// ------
/// Idle          asking for work each jobRequestInterval
/// MovingToJob   pathing to the job's work cell
/// Working       damaging a dig target, or progressing a claim / reinforce
/// MovingToVault hauling gold to the nearest Treasury tile with room, by
///               travel distance; the mining slot is released when this run begins
/// Depositing    brief pause at the treasury, then back to Idle for new work
/// MovingToPile  fetching a gold pile in its own territory (see below)
///
/// Gold
/// ----
/// Mined gold is banked on Treasury tiles, each holding a limited amount
/// (TreasuryManager). With no reachable tile that has room, the worker drops
/// its load as a gold pile where it stands and carries on working. Idle
/// workers fetch piles lying in their own territory whenever a Treasury tile
/// with room can be reached — before asking for other work.
///
/// Being held, fleeing a hazard and dying are MinionController's; it pauses
/// this behaviour (job dropped, gold kept) and resumes it afterwards, which
/// banks any carried gold first.
///
/// Data
/// ----
/// Read from the minion's definition at its level:
///   Strength   → dig damage per second
///   SkillBuild → claim/reinforce speed multiplier (1 = the durations below)
/// A slap from the Keeper's hand multiplies both for a while (MinionTemper).
/// Any stat without a row on the _Levels sheet yet uses the Inspector value.
/// </summary>
public class WorkerBehaviour : MinionBehaviour
{
    public enum WorkerState { Idle, MovingToJob, Working, MovingToVault, Depositing, MovingToPile }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Work")]
    [Tooltip("Used when the data has no Strength row.")]
    [SerializeField] private float damagePerSecond   = 20f;
    [Tooltip("Seconds to convert Cave to Tunnel, or to capture an enemy tile, " +
             "at SkillBuild 1.")]
    [SerializeField] private float claimDuration     = 2f;
    [Tooltip("Seconds to convert Stone into Wall, at SkillBuild 1.")]
    [SerializeField] private float reinforceDuration = 3f;
    [Tooltip("Seconds between work ticks. Damage and progress scale by this.")]
    [SerializeField] private float workTickInterval  = 0.25f;

    [Tooltip("Gap left between the token edge and the face being worked, in "
             + "world units. 0 puts the token flush against the wall.")]
    [SerializeField] private float wallGap           = 0.02f;

    [Header("Carrying")]
    [SerializeField] private int   maxCarryCapacity = 30;
    [SerializeField] private float depositDuration  = 1f;
    [SerializeField] private TileType treasuryRoomType = TileType.Treasury;

    [Tooltip("Chance per second of banking a part load while mining. Stops "
             + "workers carrying residual gold around after a vein runs out.")]
    [SerializeField] private float earlyDepositChancePerSecond = 0.08f;

    [Tooltip("Seconds between an idle worker's searches for gold piles to fetch.")]
    [SerializeField] private float pileSearchInterval = 1f;

    [Header("Job requests")]
    [Tooltip("Seconds between requests while idle. Stops idle workers hammering " +
             "the registry every frame when there is no work.")]
    [SerializeField] private float jobRequestInterval = 0.25f;

    // ── Runtime ────────────────────────────────────────────────────────
    private GridPathfinder _pathfinder;
    private WorkerState    _state = WorkerState.Idle;
    private DungeonJob     _job;
    private WorkerTaskManager _taskManager;

    private int       _carryingGold;
    private float     _damageAccumulator;   // fractional damage carry-over
    private float     _workProgress;        // seconds spent on claim / reinforce
    private float     _nextJobRequest;
    private float     _nextPileSearch;
    private GridCell  _depositCell;
    private GoldPile  _pile;
    private Coroutine _workCoroutine;
    private Coroutine _depositCoroutine;
    private bool      _completing;          // suppresses re-entrant abandon

    public bool        IsIdle       => enabled && _state == WorkerState.Idle;
    public FactionID   Faction      => Minion.Faction;
    public WorkerState State        => _state;
    public DungeonJob  Job          => _job;
    public GridCell    Cell         => Agent != null ? Agent.CurrentCell : null;
    public int         CarryingGold => _carryingGold;

    private GridManager2D Grid => Minion.Grid;

    private float DigDamagePerSecond => Minion.GetStatOr(MinionStat.Strength, damagePerSecond);
    private float BuildSpeed         => Mathf.Max(0.01f, Minion.GetStatOr(MinionStat.SkillBuild, 1f));

    // ── Behaviour lifecycle ────────────────────────────────────────────

    public override void Activate()
    {
        if (_pathfinder == null && Grid != null) _pathfinder = new GridPathfinder(Grid);

        _taskManager = WorkerTaskManager.GetForFaction(Minion.Faction);
        _taskManager?.RegisterWorker(this);
        GoIdle();
    }

    /// <summary>Picked up or fleeing: drop the job (slot freed), keep any gold.</summary>
    public override void Pause()
    {
        StopRoutines();
        ReleaseJob();
        ReleasePile();
        _workProgress = 0f;
        Agent.Stop();
        _state = WorkerState.Idle;
    }

    /// <summary>Set down or safe again: bank any gold first, then look for work from here.</summary>
    public override void Resume()
    {
        GoIdle();
    }

    public override void Deactivate()
    {
        StopRoutines();
        ReleaseJob();
        ReleasePile();
        _taskManager?.UnregisterWorker(this);
        _taskManager = null;
        _state = WorkerState.Idle;
    }

    private void StopRoutines()
    {
        if (_workCoroutine    != null) { StopCoroutine(_workCoroutine);    _workCoroutine    = null; }
        if (_depositCoroutine != null) { StopCoroutine(_depositCoroutine); _depositCoroutine = null; }
    }

    private void Update()
    {
        switch (_state)
        {
            case WorkerState.Idle:
                TryRequestJob();
                break;

            case WorkerState.MovingToJob:
                if (Agent.HasArrived) BeginWorking();
                break;

            case WorkerState.MovingToVault:
                if (Agent.HasArrived) _depositCoroutine = StartCoroutine(DepositRoutine());
                break;

            case WorkerState.MovingToPile:
                if (_pile == null || _pile.IsHeld || _pile.ClaimedBy != (Object)this) GoIdle();
                else if (Agent.HasArrived) CollectPile();
                break;
        }
    }

    // ── Requesting work ────────────────────────────────────────────────

    private void TryRequestJob()
    {
        if (Time.time < _nextJobRequest) return;
        _nextJobRequest = Time.time + jobRequestInterval;

        if (TryFetchPile()) return;

        if (_taskManager == null)
        {
            // The faction's task manager may not have existed on Activate.
            _taskManager = WorkerTaskManager.GetForFaction(Minion.Faction);
            if (_taskManager == null) return;
            _taskManager.RegisterWorker(this);
        }

        var job = _taskManager.RequestJob(this);
        if (job == null) return;

        StartJob(job);
    }

    private void StartJob(DungeonJob job)
    {
        _job          = job;
        _workProgress = 0f;

        // Dig targets track HP on the tile so several workers chip the same block.
        if (job.Type == JobType.Dig)
        {
            var def = Grid.GetDefinition(job.Target.TileType);
            if (def != null)
            {
                if (job.Target.CurrentHP < 0) job.Target.InitialiseHP(def.maxHitPoints);

                bool wealthTile = job.Target.TileType == TileType.Gold ||
                                  job.Target.TileType == TileType.Gem;
                if (wealthTile && job.Target.WealthRemaining == 0)
                    job.Target.InitialiseWealth(def.wealthCapacity);
            }
        }

        if (!Agent.SetDestination(WorkPositionFor(job)))
        {
            // Unreachable after all — hand the slot back.
            _taskManager.ReleaseJob(job, this);
            _job = null;
            return;
        }

        _state = WorkerState.MovingToJob;
    }

    /// <summary>
    /// Called by WorkerTaskManager when a job this worker holds is removed — the
    /// tile was mined by someone else, claimed, or otherwise stopped qualifying.
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

        _state = WorkerState.Working;
        _workCoroutine = StartCoroutine(WorkRoutine());
    }

    private IEnumerator WorkRoutine()
    {
        while (_job != null && _state == WorkerState.Working)
        {
            yield return new WaitForSeconds(workTickInterval);
            if (_job == null || _state != WorkerState.Working) yield break;

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
        _workProgress += workTickInterval * Minion.WorkSpeedMultiplier;
        if (_workProgress < claimDuration / BuildSpeed) return false;

        var cell = _job.Target;

        _completing = true;
        if (cell.TileType == TileType.Cave)
        {
            // Unclaimed cavern becomes our tunnel.
            Grid.SetTileType(cell, TileType.Tunnel, Faction);
        }
        else
        {
            // Captured from another faction: keep the room, change the flag.
            Grid.SetOwner(cell, Faction);
        }
        _completing = false;

        CompleteJob();
        return true;
    }

    private bool TickReinforce()
    {
        _workProgress += workTickInterval * Minion.WorkSpeedMultiplier;
        if (_workProgress < reinforceDuration / BuildSpeed) return false;

        _completing = true;
        Grid.SetTileType(_job.Target, TileType.Wall, Faction);
        _completing = false;

        CompleteJob();
        return true;
    }

    private bool TickDig()
    {
        var cell = _job.Target;

        // Accumulate fractional damage so non-integer DPS stays accurate across
        // ticks instead of being rounded away every time.
        _damageAccumulator += DigDamagePerSecond * Minion.WorkSpeedMultiplier * workTickInterval;
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

        var  def            = Grid.GetDefinition(cell.TileType);
        bool indestructible = def != null && def.isIndestructible;

        // Gold is spent rather than smashed — it collapses once drained.
        bool depleted = cell.TileType == TileType.Gold && cell.WealthRemaining <= 0;

        if (!indestructible && (cell.ApplyDamage(damage) || depleted))
        {
            OnTileDestroyed();
            return true;
        }

        // Full load — bank it. If no Treasury tile with room can be reached,
        // the load is dropped here as a pile and mining carries on.
        if (_carryingGold >= maxCarryCapacity)
            return TryStartDeposit();

        // Occasionally bank a part load, so a vein running out does not leave
        // a worker carrying residual gold around indefinitely.
        if (_carryingGold > 0 && _taskManager != null &&
            _taskManager.NextRandom01() < earlyDepositChancePerSecond * workTickInterval)
            return TryStartDeposit();

        return false;
    }

    private void OnTileDestroyed()
    {
        var cell = _job.Target;

        _completing = true;
        Grid.SetTileType(cell, TileType.Cave);
        DigSelectionManager.Instance?.GetController(Faction)?.DequeueCell(cell);
        _completing = false;

        // Releasing the slot happens inside TryStartDeposit; if no treasury
        // can take the gold it's dropped here and the job simply finishes.
        if (_carryingGold > 0 && TryStartDeposit()) return;
        CompleteJob();
    }

    // ── Treasury ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts a deposit run if the worker is carrying gold and a Treasury
    /// tile with room is reachable. Returns true if the run began. If none
    /// is, the load is dropped here as a gold pile and false is returned, so
    /// the worker carries straight on.
    ///
    /// Starting a run RELEASES the mining slot, so another worker can take
    /// over the face while this one walks its load back. That is the whole
    /// point of separating hauling from digging.
    /// </summary>
    private bool TryStartDeposit()
    {
        if (_carryingGold <= 0) return false;

        var treasury = FindNearestTreasuryByPath();
        if (treasury == null || !Agent.SetDestination(treasury))
        {
            DropCarriedGold();
            return false;
        }
        _depositCell = treasury;

        if (_job != null)
        {
            _taskManager?.ReleaseJob(_job, this);
            _job = null;
        }

        _state = WorkerState.MovingToVault;
        return true;
    }

    private IEnumerator DepositRoutine()
    {
        _state = WorkerState.Depositing;
        yield return new WaitForSeconds(depositDuration);
        _depositCoroutine = null;

        // The tile may have filled up on the way; GoIdle takes any remainder
        // on to the next tile with room, or drops it.
        var treasury = TreasuryManager.Instance;
        if (treasury != null) _carryingGold -= treasury.Deposit(_depositCell, Faction, _carryingGold);
        _depositCell = null;

        // The slot was given up when the run started, so simply ask for work
        // again — often the same dig job, if a slot has come free.
        GoIdle();
    }

    /// <summary>Drops whatever gold is carried as a pile on the current cell.</summary>
    private void DropCarriedGold()
    {
        var treasury = TreasuryManager.Instance;
        var cell     = Agent.CurrentCell;
        if (treasury == null || cell == null || _carryingGold <= 0) return;

        treasury.DropGold(cell, _carryingGold);
        _carryingGold = 0;
    }

    // ── Gold piles ─────────────────────────────────────────────────────

    /// <summary>
    /// Heads for the nearest unclaimed gold pile in this faction's territory,
    /// if there's a reachable Treasury tile with room to take it to.
    /// Returns true if it set off.
    /// </summary>
    private bool TryFetchPile()
    {
        if (Time.time < _nextPileSearch) return false;
        _nextPileSearch = Time.time + pileSearchInterval;

        var treasury = TreasuryManager.Instance;
        if (treasury == null || _pathfinder == null || _carryingGold >= maxCarryCapacity) return false;
        if (!AnyPileToFetch(treasury) || treasury.FreeCapacity(Faction) <= 0) return false;
        if (FindNearestTreasuryByPath() == null) return false;

        var cell = _pathfinder.FindNearestMatching(
            Agent.CurrentCell,
            c => c.Owner == Faction && CanFetch(treasury.PileAt(c)),
            Agent.Capability, Faction, Agent.Radius);
        var pile = treasury.PileAt(cell);
        if (pile == null || !pile.TryClaim(this)) return false;

        if (!Agent.SetDestination(cell)) { pile.Release(this); return false; }

        _pile  = pile;
        _state = WorkerState.MovingToPile;
        return true;
    }

    private bool AnyPileToFetch(TreasuryManager treasury)
    {
        foreach (var pile in treasury.Piles)
            if (pile != null && pile.Cell != null && pile.Cell.Owner == Faction && CanFetch(pile)) return true;
        return false;
    }

    private bool CanFetch(GoldPile pile) =>
        pile != null && !pile.IsHeld && (!pile.IsClaimed || pile.ClaimedBy == (Object)this);

    private void CollectPile()
    {
        var treasury = TreasuryManager.Instance;
        if (treasury != null)
            _carryingGold += treasury.TakeFromPile(_pile, maxCarryCapacity - _carryingGold);
        GoIdle();   // releases the claim, then banks the load
    }

    private void ReleasePile()
    {
        if (_pile != null) _pile.Release(this);
        _pile = null;
    }

    /// <summary>
    /// Nearest treasury by TRAVEL distance, not straight-line distance.
    ///
    /// Straight-line selection sends workers toward whichever treasury tile is
    /// geometrically closest, which may sit behind a wall — so they walk over
    /// perfectly good deposit tiles to reach a back corner, and if the chosen
    /// tile cannot be pathed to at all the move simply fails.
    /// </summary>
    private GridCell FindNearestTreasuryByPath()
    {
        var from = Agent.CurrentCell;
        if (from == null || _pathfinder == null) return null;

        var treasury = TreasuryManager.Instance;
        return _pathfinder.FindNearestMatching(
            from,
            treasury != null
                ? c => c.TileType == treasuryRoomType && treasury.HasSpace(c, Faction)
                : c => c.TileType == treasuryRoomType && c.Owner == Faction,
            Agent.Capability, Faction, Agent.Radius);
    }

    // ── Job exits ──────────────────────────────────────────────────────

    private void CompleteJob()
    {
        if (_job == null && _state == WorkerState.Idle) return;

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

        ReleaseJob();
        Agent.Stop();

        // Paused (held/fleeing) or inactive: the controller resumes it later.
        if (enabled) GoIdle();
    }

    /// <summary>
    /// Hands the current job's slot back. A dig tile's HP is only reset if
    /// this worker was the last one on it.
    /// </summary>
    private void ReleaseJob()
    {
        if (_job == null) return;

        if (_job.Type == JobType.Dig && _job.Workers.Count <= 1)
            _job.Target.ResetHP();

        _taskManager?.ReleaseJob(_job, this);
        _job = null;
    }

    /// <summary>
    /// Returns the worker to Idle so the task manager will hand it new work.
    ///
    /// Every job exit funnels through here. An exit that leaves the state
    /// non-Idle, or leaves a dangling job reference, is what makes a worker
    /// stand still forever.
    /// </summary>
    private void GoIdle()
    {
        ReleasePile();
        _job          = null;
        _workProgress = 0f;
        _state        = WorkerState.Idle;
        _nextJobRequest = 0f;   // ask for new work on the next frame

        // Never pick up unrelated work while still holding gold — bank it
        // first, or drop it here if there's nowhere to bank it. This is the
        // backstop that guarantees no residual load is carried around.
        if (_carryingGold > 0) TryStartDeposit();
    }

    /// <summary>
    /// Where in the work cell this worker should stand.
    ///
    /// Dig and Reinforce push the worker up against the face of the tile it
    /// is working, rather than standing in the middle of its own cell swinging
    /// at nothing. Claim jobs have WorkCell == Target, so the direction below
    /// is zero and the worker stands centred on the tile it is claiming.
    ///
    /// Stand-off is derived from this worker's own radius, so a larger minion
    /// stops further out and its token still never touches the wall.
    /// </summary>
    private Vector3 WorkPositionFor(DungeonJob job)
    {
        Vector3 workCentre   = Grid.CellToWorld(job.WorkCell.X, job.WorkCell.Y);
        Vector3 targetCentre = Grid.CellToWorld(job.Target.X,   job.Target.Y);

        Vector3 toFace = targetCentre - workCentre;
        toFace.y = 0f;

        // Claim: same cell, so no direction and no offset.
        if (toFace.sqrMagnitude < 0.0001f) return workCentre;

        // Stop short of the shared edge by the token radius plus a small gap.
        float half     = Grid.CellSize * 0.5f;
        float standOff = Mathf.Max(0f, half - Agent.Radius - wallGap);

        return workCentre + toFace.normalized * standOff;
    }
}
