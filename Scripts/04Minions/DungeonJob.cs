using System.Collections.Generic;

/// <summary>
/// What kind of work a job represents.
/// Priority between types is a weighting in ImpTaskManager, not a hard order —
/// a distant high-priority job should not beat a job at the imp's feet.
/// </summary>
public enum JobType
{
    /// <summary>Mine Stone, Wall, Gold or Gem that the player marked for digging.</summary>
    Dig,
    /// <summary>Convert unclaimed Cave to Tunnel, or capture an enemy-owned tile.</summary>
    Claim,
    /// <summary>Convert Stone adjacent to owned territory into Wall.</summary>
    Reinforce,
}

/// <summary>
/// One unit of assignable work.
///
/// A job is identified by (Type, Target, WorkCell) rather than by target cell
/// alone, because a dig target is worked from its sides: a Stone tile with three
/// exposed faces produces three jobs, each able to hold several imps. Keying by
/// target alone would cap the whole tile at one worker.
///
/// WorkCell is where the imp physically stands:
///   Dig / Reinforce — an adjacent walkable cell
///   Claim           — the target itself, since the imp stands on what it claims
/// </summary>
public class DungeonJob
{
    public JobType   Type     { get; }
    public GridCell  Target   { get; }
    public GridCell  WorkCell { get; }
    public int       Capacity { get; }

    public List<ImpController> Workers { get; } = new();

    public bool HasRoom => Workers.Count < Capacity;
    public bool IsEmpty => Workers.Count == 0;

    public DungeonJob(JobType type, GridCell target, GridCell workCell, int capacity)
    {
        Type     = type;
        Target   = target;
        WorkCell = workCell;
        Capacity = capacity;
    }

    public bool AddWorker(ImpController imp)
    {
        if (!HasRoom || Workers.Contains(imp)) return false;
        Workers.Add(imp);
        return true;
    }

    public void RemoveWorker(ImpController imp) => Workers.Remove(imp);

    /// <summary>Stable dictionary key. Two jobs match only if all three parts match.</summary>
    public JobKey Key => new(Type, Target, WorkCell);

    public override string ToString() =>
        $"{Type} @ ({Target.X},{Target.Y}) from ({WorkCell.X},{WorkCell.Y}) " +
        $"[{Workers.Count}/{Capacity}]";
}

/// <summary>Composite identity for a job slot.</summary>
public readonly struct JobKey : System.IEquatable<JobKey>
{
    public readonly JobType  Type;
    public readonly GridCell Target;
    public readonly GridCell WorkCell;

    public JobKey(JobType type, GridCell target, GridCell workCell)
    {
        Type     = type;
        Target   = target;
        WorkCell = workCell;
    }

    public bool Equals(JobKey other) =>
        Type == other.Type && Target == other.Target && WorkCell == other.WorkCell;

    public override bool Equals(object obj) => obj is JobKey k && Equals(k);

    public override int GetHashCode()
    {
        int h = (int)Type;
        h = h * 397 ^ (Target?.GetHashCode()   ?? 0);
        h = h * 397 ^ (WorkCell?.GetHashCode() ?? 0);
        return h;
    }
}
