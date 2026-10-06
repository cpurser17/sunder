using UnityEngine;

/// <summary>
/// An anger job: a furious minion walks out — to its faction's portal, and
/// gone for good. Only minions that came through the portal can leave this
/// way. If it can't reach the portal it gives up for now (and sulks later,
/// angrier still). The player is told it's leaving.
/// </summary>
public class LeaveBehaviour : MinionBehaviour
{
    [Tooltip("Seconds between route attempts while the portal can't be reached.")]
    [SerializeField, Min(0.5f)] private float retrySeconds = 2f;
    [Tooltip("Seconds of failing to find a route before giving up for now.")]
    [SerializeField, Min(1f)] private float giveUpSeconds = 20f;

    private bool  _heading;
    private float _nextTry;
    private float _giveUpAt;
    private bool  _announced;

    public override void Activate()
    {
        _heading   = false;
        _nextTry   = 0f;
        _giveUpAt  = Time.time + giveUpSeconds;
        _announced = false;
    }

    public override void Pause()
    {
        _heading = false;
        Agent.Stop();
    }

    public override void Resume()
    {
        _heading  = false;
        _giveUpAt = Time.time + giveUpSeconds;
    }

    public override void Deactivate() => Agent.Stop();

    private void Update()
    {
        if (_heading)
        {
            if (Agent.HasArrived) Minion.Desert();
            return;
        }

        if (Time.time > _giveUpAt) { Minion.EndErrand(this); return; }
        if (Time.time < _nextTry) return;
        _nextTry = Time.time + retrySeconds;

        var portal = Portal.Instance != null ? Portal.Instance.CellOf(Minion.Faction) : null;
        if (portal == null || !Agent.SetDestination(portal)) return;

        _heading = true;
        if (!_announced)
        {
            _announced = true;
            Announcer.Announce(Minion.Faction, "MinionLeaving");
        }
    }
}
