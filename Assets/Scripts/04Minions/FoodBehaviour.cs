using UnityEngine;

/// <summary>
/// The eating errand: catch the nearest chicken it can reach, eat it, and
/// keep going until it's full (a worker, until it's healed). Started by
/// MinionController when hungry, or for a single chicken it happens to pass
/// (Target).
///
/// Any chicken will do — its own Hatchery's, a stray in a corridor, or one
/// in an enemy Hatchery it has got into. A chosen chicken stops wandering,
/// so it can be caught.
///
/// No chicken it can reach: it goes to its faction's nearest Hatchery and
/// waits for one to hatch. No Hatchery either (or still nothing after
/// waitSeconds): the player is told their hatchery is too small, and the
/// minion doesn't look again for a while (MinionController.OnNoFood).
/// </summary>
public class FoodBehaviour : MinionBehaviour
{
    private enum Step { Choosing, Chasing, Eating, Waiting }

    [Tooltip("Seconds to wait in a Hatchery for a chicken before giving up.")]
    [SerializeField, Min(0f)] private float waitSeconds = 30f;
    [Tooltip("How close, in cells beyond its own radius, it must get to a chicken to catch it.")]
    [SerializeField, Min(0f)] private float catchReach = 0.35f;

    private GridPathfinder _pathfinder;
    private Step    _step;
    private Chicken _chicken;
    private Chicken _preset;
    private float   _eatUntil;
    private float   _giveUpAt;
    private float   _nextLook;

    /// <summary>Makes the next trip about this chicken in particular (a snack it's passing).</summary>
    public void Target(Chicken chicken) => _preset = chicken;

    public override void Activate()
    {
        if (_pathfinder == null && Minion.Grid != null) _pathfinder = new GridPathfinder(Minion.Grid);
        _step     = Step.Choosing;
        _giveUpAt = Time.time + waitSeconds;

        var preset = _preset;
        _preset = null;
        if (preset != null && preset.TryClaim(this) && Agent.SetDestination(preset.transform.position))
        {
            _chicken = preset;
            _step    = Step.Chasing;
        }
        else if (preset != null) preset.Release(this);
    }

    public override void Pause()
    {
        ReleaseChicken();
        Agent.Stop();
        _step = Step.Choosing;
    }

    public override void Resume()
    {
        _step     = Step.Choosing;
        _giveUpAt = Time.time + waitSeconds;
    }

    public override void Deactivate()
    {
        ReleaseChicken();
        _preset = null;
        Agent.Stop();
    }

    private void Update()
    {
        if (_step != Step.Eating && !Minion.CouldEatMore) { Finish(false); return; }

        var hatchery = HatcheryManager.Instance;
        if (hatchery == null) { Finish(true); return; }

        switch (_step)
        {
            case Step.Choosing:
                Choose(hatchery, allowWait: true);
                break;

            case Step.Chasing:
                if (!StillMine()) { _step = Step.Choosing; break; }
                // Arrived counts as caught: the chicken stands still once chosen,
                // so the path ends beside it even if crowding keeps it a little off.
                if (CloseEnough() || Agent.HasArrived)
                {
                    Agent.Stop();
                    _eatUntil = Time.time + hatchery.EatSeconds;
                    _step     = Step.Eating;
                }
                break;

            case Step.Eating:
                if (!StillMine()) { _step = Step.Choosing; break; }
                if (Time.time < _eatUntil) break;
                var eaten = _chicken;
                _chicken = null;
                eaten.Consume();
                Minion.Eat(hatchery.HungerPerChicken, hatchery.HealPerChicken);
                _step = Step.Choosing;
                break;

            case Step.Waiting:
                if (Time.time > _giveUpAt) { GiveUp(); break; }
                if (Time.time >= _nextLook) { _nextLook = Time.time + 1f; Choose(hatchery, allowWait: false); }
                break;
        }
    }

    private void Choose(HatcheryManager hatchery, bool allowWait)
    {
        var from = Agent.CurrentCell;
        if (from == null || _pathfinder == null) return;   // not placed yet; try next frame
        var faction = Minion.Faction;

        var cell    = _pathfinder.FindNearestMatching(from, hatchery.HasFreeChicken, Agent.Capability, faction, Agent.Radius);
        var chicken = hatchery.FreeChickenOn(cell);
        if (chicken != null && chicken.TryClaim(this))
        {
            if (Agent.SetDestination(chicken.transform.position))
            {
                _chicken = chicken;
                _step    = Step.Chasing;
                return;
            }
            chicken.Release(this);
        }

        if (!allowWait) return;   // already waiting in a Hatchery

        var hatch = _pathfinder.FindNearestMatching(from, c => hatchery.IsHatchery(c) && c.Owner == faction,
                                                    Agent.Capability, faction, Agent.Radius);
        if (hatch != null && Agent.SetDestination(hatch))
        {
            _step     = Step.Waiting;
            _nextLook = Time.time + 1f;
            return;
        }

        GiveUp();
    }

    private void GiveUp()
    {
        Announcer.Announce(Minion.Faction, "HatcheryTooSmall");
        Finish(true);
    }

    private void Finish(bool foundNothing)
    {
        ReleaseChicken();
        if (foundNothing) Minion.OnNoFood();
        Minion.FinishFood();
    }

    private bool StillMine() =>
        _chicken != null && _chicken.IsAlive && !_chicken.IsHeld && _chicken.ClaimedBy == (Object)this;

    private bool CloseEnough()
    {
        Vector3 d = _chicken.transform.position - transform.position;
        float reach = Agent.Radius + catchReach * Minion.Grid.CellSize;
        return d.x * d.x + d.z * d.z <= reach * reach;
    }

    private void ReleaseChicken()
    {
        if (_chicken != null) _chicken.Release(this);
        _chicken = null;
    }
}
