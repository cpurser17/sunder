using UnityEngine;

/// <summary>
/// One thing a minion can spend its time doing — working (WorkerBehaviour),
/// reporting for duty (CreatureBehaviour), and later researching, training,
/// fighting and so on. Lives on the same prefab as MinionController, which
/// decides which behaviour is active and switches the rest off.
///
/// The controller owns everything every minion shares (health, the Keeper's
/// hand, hazards, death); a behaviour only owns its own activity. It never
/// switches itself on or off — it's told, through these four calls:
///
///   Activate    became the active behaviour; start from scratch
///   Pause       interrupted for a while (picked up, fleeing a hazard) —
///               drop anything claimed, keep anything carried
///   Resume      the interruption is over; reassess from where it is now
///   Deactivate  no longer active (switched, died, converted) — release
///               everything, including registrations
///
/// Only the active, unpaused behaviour is enabled, so Update only runs then.
/// </summary>
[RequireComponent(typeof(MinionController))]
public abstract class MinionBehaviour : MonoBehaviour
{
    protected MinionController Minion { get; private set; }
    protected GridAgent        Agent  { get; private set; }

    protected virtual void Awake()
    {
        Minion = GetComponent<MinionController>();
        Agent  = GetComponent<GridAgent>();
    }

    public virtual void Activate()   { }
    public virtual void Pause()      { }
    public virtual void Resume()     { }
    public virtual void Deactivate() { }
}
