using UnityEngine;

/// <summary>
/// The payday errand: walk to the nearest Treasury tile of its faction that
/// holds gold, take what it's owed (or as much as the tile has), and move on
/// to the next tile until paid. Wages only ever come out of Treasury tiles
/// — never the faction's starting gold (FactionWallet.Reserve).
///
/// Started by MinionController.OnPayday as an errand — the minion's usual
/// behaviour is paused, not ended, and picks up again afterwards. If there's
/// nothing left to collect, or none can be reached within giveUpAfter
/// seconds, it stops; what's still owed is carried to the next payday.
/// </summary>
public class WageBehaviour : MinionBehaviour
{
    private enum Step { Choosing, Walking, Collecting }

    [Tooltip("Seconds spent taking gold at each stop.")]
    [SerializeField] private float collectDuration = 1f;
    [Tooltip("Seconds after setting off (or being set down) before giving up on " +
             "collecting the rest — it stays owed until next payday.")]
    [SerializeField] private float giveUpAfter = 60f;

    private GridPathfinder _pathfinder;
    private Step     _step;
    private GridCell _target;
    private float    _collectUntil;
    private float    _deadline;

    public override void Activate()
    {
        if (_pathfinder == null && Minion.Grid != null) _pathfinder = new GridPathfinder(Minion.Grid);
        BeginLeg();
    }

    public override void Pause()
    {
        Agent.Stop();
        _step = Step.Choosing;
    }

    public override void Resume() => BeginLeg();

    public override void Deactivate()
    {
        Agent.Stop();
        _target = null;
    }

    /// <summary>Chooses where to go on the next Update — after GridAgent has its current cell.</summary>
    private void BeginLeg()
    {
        _step     = Step.Choosing;
        _target   = null;
        _deadline = Time.time + giveUpAfter;
    }

    private void Update()
    {
        if (Minion.OwedWages <= 0 || Time.time > _deadline) { Minion.FinishWageTrip(); return; }

        switch (_step)
        {
            case Step.Choosing:
                ChooseSource();
                break;

            case Step.Walking:
                if (!StillHasGold())        _step = Step.Choosing;
                else if (Agent.HasArrived) { _step = Step.Collecting; _collectUntil = Time.time + collectDuration; }
                break;

            case Step.Collecting:
                if (Time.time >= _collectUntil) Collect();
                break;
        }
    }

    private void ChooseSource()
    {
        var faction  = Minion.Faction;
        var treasury = TreasuryManager.Instance;
        var from     = Agent.CurrentCell;
        if (from == null) return;   // not placed yet; try next frame

        _target = treasury != null && _pathfinder != null
            ? _pathfinder.FindNearestMatching(from, c => treasury.HasGold(c, faction),
                                              Agent.Capability, faction, Agent.Radius)
            : null;

        // Nothing to collect, or no way there: stays owed.
        if (_target == null || !Agent.SetDestination(_target)) { Minion.FinishWageTrip(); return; }
        _step = Step.Walking;
    }

    private void Collect()
    {
        var faction = Minion.Faction;
        int owed    = Minion.OwedWages;
        int got     = TreasuryManager.Instance?.Withdraw(_target, faction, owed) ?? 0;

        Minion.ReceiveWages(got);
        if (Minion.OwedWages > 0) _step = Step.Choosing;
        else Minion.FinishWageTrip();
    }

    private bool StillHasGold() =>
        TreasuryManager.Instance != null && TreasuryManager.Instance.HasGold(_target, Minion.Faction);
}
