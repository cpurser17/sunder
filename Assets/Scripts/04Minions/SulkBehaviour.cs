using UnityEngine;

/// <summary>
/// An anger job: an annoyed minion downs tools and sulks — off to its bed
/// if it has one (else it stays put) and does nothing for a while. Started
/// by MinionController now and then while the minion is annoyed.
/// Placeholder for the fuller anger behaviours to come (job stress,
/// stealing gold, fighting other minions, joining the enemy…).
/// </summary>
public class SulkBehaviour : MinionBehaviour
{
    [Tooltip("Seconds a sulk lasts.")]
    [SerializeField, Min(1f)] private float sulkSeconds = 30f;

    private float _endsAt;
    private bool  _started;

    public override void Activate()
    {
        _endsAt  = Time.time + sulkSeconds;
        _started = false;
    }

    public override void Pause()  => Agent.Stop();
    public override void Resume() => _started = false;
    public override void Deactivate() => Agent.Stop();

    private void Update()
    {
        if (Time.time >= _endsAt) { Minion.EndErrand(this); return; }
        if (_started) return;
        _started = true;

        var bed = LairManager.Instance != null ? LairManager.Instance.BedOf(Minion) : null;
        if (bed != null) Agent.SetDestination(bed);
        else Agent.Stop();
    }
}
