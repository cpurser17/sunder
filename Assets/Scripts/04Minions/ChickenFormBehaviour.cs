using UnityEngine;

/// <summary>
/// A minion turned into a chicken (the Chicken spell): it just pecks about,
/// wandering to a random nearby tile now and then. Run by MinionController
/// in place of everything else until the spell wears off.
/// </summary>
public class ChickenFormBehaviour : MinionBehaviour
{
    [Tooltip("Seconds it stands between wanders (random between these).")]
    [SerializeField] private Vector2 wanderPause = new(0.5f, 1.5f);
    [Tooltip("How far, in cells, one wander goes at most.")]
    [SerializeField, Min(1)] private int wanderRange = 2;

    private float _nextWander;
    private bool  _wandering;

    public override void Activate()
    {
        Agent.Stop();
        _wandering  = false;
        _nextWander = Time.time + Random.Range(wanderPause.x, wanderPause.y);
    }

    public override void Pause()      => Agent.Stop();
    public override void Resume()     => _wandering = false;
    public override void Deactivate() => Agent.Stop();

    private void Update()
    {
        if (_wandering)
        {
            if (!Agent.HasArrived) return;
            _wandering  = false;
            _nextWander = Time.time + Random.Range(wanderPause.x, wanderPause.y);
            return;
        }
        if (Time.time < _nextWander) return;

        var grid = Minion.Grid;
        var from = Agent.CurrentCell;
        if (grid == null || from == null) return;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            var to = grid.GetCell(from.X + Random.Range(-wanderRange, wanderRange + 1),
                                  from.Y + Random.Range(-wanderRange, wanderRange + 1));
            if (to == null || to == from) continue;
            if (!TraversalRules.CanPathOn(to.TileType, Agent.Capability, Minion.Faction)) continue;
            if (Agent.SetDestination(to)) { _wandering = true; return; }
        }
        _nextWander = Time.time + wanderPause.x;
    }
}
