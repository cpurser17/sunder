using System;
using System.Collections.Generic;

/// <summary>
/// What a research node's Requires rule can ask, on top of the campaign
/// conditions (which never hold during a mission). FactionResearchState
/// answers it for one team.
/// </summary>
public interface IResearchUnlockState
{
    bool HasResearch(string researchId);
    /// <summary>Tiles in this team's biggest room of a type (Room(Library, 25) needs one room of 25+).</summary>
    int  BiggestRoom(TileType type);
}

/// <summary>Research(Alchemy): that node has been researched this mission (or the node it was replaced by).</summary>
[UnlockCondition("Research")]
public class ResearchDoneCondition : UnlockCondition
{
    public override string Validate(IReadOnlyList<string> args, string op) =>
        args.Count != 1 ? "needs one research id, e.g. Research(Alchemy)" :
        op != null      ? "takes no comparison" : null;

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state) =>
        state is IResearchUnlockState r && r.HasResearch(args[0]);

    public override IEnumerable<string> ResearchReferenced(IReadOnlyList<string> args) { yield return args[0]; }
    public override bool ForResearch => true;
}

/// <summary>Room(Library, 25): the team has a Library of at least 25 tiles. Room(Library) = any Library.</summary>
[UnlockCondition("Room")]
public class RoomSizeCondition : UnlockCondition
{
    public override string Validate(IReadOnlyList<string> args, string op)
    {
        if (op != null) return "takes no comparison — put the size inside, e.g. Room(Library, 25)";
        if (args.Count < 1 || args.Count > 2) return "needs a room and optionally a size, e.g. Room(Library, 25)";
        if (!Enum.TryParse(args[0], true, out TileType _)) return $"'{args[0]}' isn't a room type";
        if (args.Count == 2 && !int.TryParse(args[1], out _)) return $"'{args[1]}' isn't a whole number of tiles";
        return null;
    }

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state)
    {
        if (state is not IResearchUnlockState r || !Enum.TryParse(args[0], true, out TileType type)) return false;
        int size = args.Count == 2 && int.TryParse(args[1], out int n) ? n : 1;
        return r.BiggestRoom(type) >= Math.Max(1, size);
    }

    public override bool ForResearch => true;
}
