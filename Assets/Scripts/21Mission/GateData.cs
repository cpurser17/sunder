using System.Collections.Generic;

/// <summary>
/// A gate in a level: a floating point where a team's minions arrive during
/// the mission — heroes storming in, or reinforcements. Owned by a team;
/// everything it spawns joins that team. It can't be claimed, and the tiles
/// under it can be claimed (or built on) without changing its owner. Placed
/// over any tile that isn't solid. Once its last deployment is done, it's gone.
/// </summary>
[System.Serializable]
public class GatePlacement
{
    public string id;   // unique in the level, e.g. "Gate 1"
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID factionId = FactionID.AI1;
    public int x, y;    // the tile it floats over
    public List<GateDeployment> deployments = new();

    public GatePlacement Clone()
    {
        var copy = (GatePlacement)MemberwiseClone();
        copy.deployments = new List<GateDeployment>();
        foreach (var d in deployments) copy.deployments.Add(d.Clone());
        return copy;
    }
}

/// <summary>One squad a gate sends out, when its trigger fires — optionally again and again.</summary>
[System.Serializable]
public class GateDeployment
{
    public string id;   // unique in the level (other triggers refer to it), e.g. "Gate 1 · Squad 1"
    public List<SquadMember> squad = new();
    public DeploymentTrigger trigger = new();

    /// <summary>Minutes between repeats once it has first fired. 0 = it fires once.</summary>
    public float repeatMinutes;
    /// <summary>Most repeats after the first deployment. Null = repeat forever (when repeatMinutes > 0).</summary>
    public int? maxRepeats;

    public GateDeployment Clone()
    {
        var copy = (GateDeployment)MemberwiseClone();
        copy.squad = new List<SquadMember>();
        foreach (var m in squad) copy.squad.Add(m.Clone());
        copy.trigger = trigger.Clone();
        return copy;
    }
}

/// <summary>Some minions of one kind in a squad.</summary>
[System.Serializable]
public class SquadMember
{
    public string contentFactionId;   // e.g. "01H"
    public string minionId;           // e.g. "T1F"
    public int    level = 1;
    public int    count = 1;
    public bool   isLord;             // the lord of the land (count should then be 1)

    public SquadMember Clone() => (SquadMember)MemberwiseClone();
}

/// <summary>What sets a deployment off. More kinds to come (a door broken, an area reached…).</summary>
public enum DeploymentTriggerKind
{
    /// <summary>Minutes into the mission.</summary>
    AfterTime,
    /// <summary>Another deployment's squad has arrived and every member is dead (or has changed sides).</summary>
    SquadDefeated,
    /// <summary>A team has claimed at least this many tiles this mission.</summary>
    TilesClaimed,
    /// <summary>A Dungeon Heart falls — a particular team's, or any.</summary>
    HeartDestroyed,
}

[System.Serializable]
public class DeploymentTrigger
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public DeploymentTriggerKind kind = DeploymentTriggerKind.AfterTime;
    public float  minutes;          // AfterTime
    public string deploymentId;     // SquadDefeated
    public int    tiles;            // TilesClaimed
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID team = FactionID.Player;   // TilesClaimed: whose; HeartDestroyed: whose (Unaligned = any)

    public DeploymentTrigger Clone() => (DeploymentTrigger)MemberwiseClone();

    public string Describe() => kind switch
    {
        DeploymentTriggerKind.AfterTime      => $"after {minutes:0.#} min",
        DeploymentTriggerKind.SquadDefeated  => $"when {(string.IsNullOrEmpty(deploymentId) ? "(choose a squad)" : deploymentId)} is defeated",
        DeploymentTriggerKind.TilesClaimed   => $"when {FactionTeams.DisplayName(team)} has claimed {tiles} tiles",
        DeploymentTriggerKind.HeartDestroyed => team == FactionID.Unaligned ? "when any Dungeon Heart falls"
                                                                            : $"when {FactionTeams.DisplayName(team)}'s heart falls",
        _ => kind.ToString(),
    };
}

/// <summary>A gate's progress, in a save.</summary>
[System.Serializable]
public class GateSaveData
{
    public string gateId;
    public bool   gone;
    public List<DeploymentSaveData> deployments = new();
}

[System.Serializable]
public class DeploymentSaveData
{
    public string deploymentId;
    public int    timesFired;
    public float  nextAt = -1f;   // mission seconds of the next repeat, -1 = none due
}
