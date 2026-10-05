using UnityEngine;

/// <summary>
/// The sleep errand: go to its Lair bed and sleep until fully rested, then
/// go back to whatever it was doing. Started by MinionController once
/// tiredness reaches its sleep threshold.
///
/// Finding a bed (LairManager)
/// ---------------------------
///   1. Its own bed, if it still has one and can walk there.
///   2. Otherwise the nearest free Lair tile it can walk to becomes its new
///      bed; the old one, if any, disappears.
///   3. Otherwise it sleeps on the floor where it stands — far less
///      restful, and it's angry about it until it next sleeps in a bed —
///      and the player hears that the lair is too small.
///
/// Rest comes faster in a well-built Lair (the room's efficiency) and heals
/// a little too. Picked up, it wakes; set down, it heads back to bed. If the
/// bed is lost while it's asleep or on the way, it looks for another.
/// </summary>
public class SleepBehaviour : MinionBehaviour
{
    private enum Step { Choosing, Walking, Sleeping }

    [Tooltip("Tiredness removed per minute asleep in a bed of an ordinary (efficiency 1) Lair.")]
    [SerializeField, Min(0.01f)] private float restPerMinute = 0.5f;
    [Tooltip("Rest on the floor, as a fraction of resting in a bed.")]
    [SerializeField, Range(0f, 1f)] private float floorRestFactor = 0.25f;
    [Tooltip("Health restored per minute asleep in a bed, as a fraction of max health. 0 = none.")]
    [SerializeField, Range(0f, 1f)] private float healPerMinute = 0.1f;
    [Tooltip("Lasting anger (0-1) from having to sleep on the floor. Placeholder.")]
    [SerializeField, Range(0f, 1f)] private float noBedAnger = 0.15f;

    private GridPathfinder _pathfinder;
    private Step     _step;
    private GridCell _bed;
    private bool     _onFloor;

    public bool IsAsleep => enabled && _step == Step.Sleeping;

    public override void Activate()
    {
        if (_pathfinder == null && Minion.Grid != null) _pathfinder = new GridPathfinder(Minion.Grid);
        _step = Step.Choosing;
    }

    /// <summary>Picked up or fleeing: wakes, still tired.</summary>
    public override void Pause()
    {
        Agent.Stop();
        _step = Step.Choosing;
    }

    public override void Resume() => _step = Step.Choosing;

    public override void Deactivate()
    {
        Agent.Stop();
        _step = Step.Choosing;
    }

    private void Update()
    {
        if (Minion.Tiredness <= 0f) { Minion.FinishSleep(!_onFloor); return; }

        var lair = LairManager.Instance;
        switch (_step)
        {
            case Step.Choosing:
                ChooseBed(lair);
                break;

            case Step.Walking:
                if (!StillMyBed(lair))     _step = Step.Choosing;
                else if (Agent.HasArrived) _step = Step.Sleeping;
                break;

            case Step.Sleeping:
                if (!_onFloor && !StillMyBed(lair)) { _step = Step.Choosing; break; }
                Sleep(lair);
                break;
        }
    }

    private void ChooseBed(LairManager lair)
    {
        var from = Agent.CurrentCell;
        if (from == null) return;   // not placed yet; try next frame

        _onFloor = false;
        _bed     = lair != null ? lair.BedOf(Minion) : null;

        // 1. Its own bed, if it can get there.
        if (StillMyBed(lair) && Agent.SetDestination(_bed)) { _step = Step.Walking; return; }

        // 2. The nearest free bed space it can get to.
        if (lair != null && _pathfinder != null)
        {
            var faction = Minion.Faction;
            var space   = _pathfinder.FindNearestMatching(from, c => lair.IsFreeBedSpace(c, faction),
                                                          Agent.Capability, faction, Agent.Radius);
            if (space != null && lair.Claim(Minion, space))
            {
                _bed = space;
                if (Agent.SetDestination(_bed)) { _step = Step.Walking; return; }
            }
        }

        // 3. The floor.
        _bed     = null;
        _onFloor = true;
        Agent.Stop();
        Minion.OnNoBed(noBedAnger);
        lair?.NotifyNoBed(Minion.Faction);
        _step = Step.Sleeping;
    }

    private void Sleep(LairManager lair)
    {
        float minutes = Time.deltaTime / 60f;
        if (_onFloor)
        {
            Minion.Rest(restPerMinute * floorRestFactor * minutes);
            return;
        }

        float efficiency = lair != null ? lair.RestEfficiency(_bed) : 1f;
        Minion.Rest(restPerMinute * efficiency * minutes);
        Minion.Heal(Minion.MaxHealth * healPerMinute * minutes);
    }

    private bool StillMyBed(LairManager lair) =>
        _bed != null && lair != null && lair.BedOf(Minion) == _bed;
}
