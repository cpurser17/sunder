using UnityEngine;

/// <summary>
/// Answering Call to Arms: the creature heads to its faction's rally point
/// and lingers close by, shuffling about, until the rally is called off.
/// Started by MinionController for creatures within the rally's radius —
/// above every other errand, below only fighting. Workers never answer.
/// </summary>
public class RallyBehaviour : MinionBehaviour
{
    [Tooltip("How far from the rally point, in cells, it lingers.")]
    [SerializeField, Min(0f)] private float lingerRadius = 1f;
    [Tooltip("Seconds it stands between shuffles (random between these).")]
    [SerializeField] private Vector2 shufflePause = new(2f, 4f);
    [Tooltip("Seconds before trying again, if it can't find a way there.")]
    [SerializeField] private float retrySeconds = 10f;

    private bool    _moving;
    private float   _nextMove;
    private Vector3 _rallyAt;

    public override void Activate()   { _moving = false; _nextMove = 0f; }
    public override void Pause()      => Agent.Stop();
    public override void Resume()     { _moving = false; _nextMove = 0f; }
    public override void Deactivate() => Agent.Stop();

    private void Update()
    {
        var rally = RallyPoints.Instance;
        if (rally == null || !rally.TryGet(Minion.Faction, out Vector3 point, out _))
        {
            Minion.EndErrand(this);
            return;
        }

        // Moved: head for the new spot at once.
        if ((point - _rallyAt).sqrMagnitude > 0.01f) { _rallyAt = point; _moving = false; _nextMove = 0f; }

        if (_moving)
        {
            if (!Agent.HasArrived) return;
            _moving   = false;
            _nextMove = Time.time + Random.Range(shufflePause.x, shufflePause.y);
            return;
        }
        if (Time.time < _nextMove) return;

        float   cell   = Minion.Grid != null ? Minion.Grid.CellSize : 1f;
        Vector2 offset = Random.insideUnitCircle * lingerRadius * cell;
        Vector3 spot   = point + new Vector3(offset.x, 0f, offset.y);

        if (Agent.SetDestination(spot) || Agent.SetDestination(point)) { _moving = true; return; }
        Minion.GiveUpRally(retrySeconds);
    }
}
