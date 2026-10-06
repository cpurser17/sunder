/// <summary>
/// Identifies which faction owns a tile or asset.
/// Unaligned (0) means unclaimed — visible to all, capturable by the first
/// faction to reach it. It doubles as the Neutral team in the level editor.
///
/// Player and AI1–AI7 are the eight playable seats; any of them can be human
/// or AI (FactionSetup.isHuman). Hostile is the enemy-to-all team: it never
/// allies with anyone, for heroes and other level-placed threats.
///
/// Stored as numbers in Unity assets and as names in level/save JSON, so
/// append new values at the end and never reorder or rename existing ones.
/// </summary>
public enum FactionID
{
    Unaligned = 0,
    Player    = 1,
    AI1       = 2,
    AI2       = 3,
    AI3       = 4,
    AI4       = 5,
    AI5       = 6,
    AI6       = 7,
    AI7       = 8,
    Hostile   = 9,
}

/// <summary>
/// Team-level views of FactionID for tools and UI: which ids are playable
/// seats, and the names a level designer sees for them.
/// </summary>
public static class FactionTeams
{
    /// <summary>Most playable seats a level can have.</summary>
    public const int MaxPlayableTeams = 8;

    /// <summary>The eight playable seats, in team order (Team 1 = Player).</summary>
    public static readonly FactionID[] Playable =
    {
        FactionID.Player, FactionID.AI1, FactionID.AI2, FactionID.AI3,
        FactionID.AI4,    FactionID.AI5, FactionID.AI6, FactionID.AI7,
    };

    /// <summary>Every team a level can assign: Neutral, the eight seats, then Enemy to all.</summary>
    public static readonly FactionID[] All =
    {
        FactionID.Unaligned,
        FactionID.Player, FactionID.AI1, FactionID.AI2, FactionID.AI3,
        FactionID.AI4,    FactionID.AI5, FactionID.AI6, FactionID.AI7,
        FactionID.Hostile,
    };

    public static bool IsPlayable(FactionID id) => id >= FactionID.Player && id <= FactionID.AI7;

    /// <summary>"Neutral", "Team 1"…"Team 8" or "Enemy to all".</summary>
    public static string DisplayName(FactionID id) => id switch
    {
        FactionID.Unaligned => "Neutral",
        FactionID.Hostile   => "Enemy to all",
        _                   => $"Team {(int)id}",
    };
}
