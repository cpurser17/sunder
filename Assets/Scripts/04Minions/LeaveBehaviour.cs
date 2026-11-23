using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Walking out of the dungeon for good, through a portal.
///
///   An anger job: a furious minion heads for its own faction's portal. Only
///   minions that came through the portal leave this way. If it can't reach
///   it, it gives up for now (and sulks later, angrier still). The player is
///   told it's leaving.
///
///   Deserting (its team's heart has fallen — MinionController.IsDeserting):
///   any minion heads for the nearest portal it can reach, whoever owns it,
///   and never gives up. Nobody is told.
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

        bool deserting = Minion.IsDeserting;
        if (!deserting && Time.time > _giveUpAt) { Minion.EndErrand(this); return; }
        if (Time.time < _nextTry) return;
        _nextTry = Time.time + retrySeconds;

        if (deserting)
        {
            _heading = HeadForNearestPortal();
            return;
        }

        var portal = Portal.Instance != null ? Portal.Instance.CellOf(Minion.Faction) : null;
        if (portal == null || !Agent.SetDestination(portal)) return;

        _heading = true;
        if (!_announced)
        {
            _announced = true;
            Announcer.Announce(Minion.Faction, "MinionLeaving");
        }
    }

    /// <summary>Sets off for the nearest portal (of any owner) it can find a way to. False if none.</summary>
    private bool HeadForNearestPortal()
    {
        if (Portal.Instance == null) return false;
        var candidates = new List<GridCell>(Portal.Instance.AllCells());
        Vector3 here = transform.position;
        var grid = Minion.Grid;
        candidates.Sort((a, b) =>
            (grid.CellToWorld(a.X, a.Y) - here).sqrMagnitude.CompareTo((grid.CellToWorld(b.X, b.Y) - here).sqrMagnitude));
        foreach (var cell in candidates)
            if (Agent.SetDestination(cell)) return true;
        return false;
    }
}
