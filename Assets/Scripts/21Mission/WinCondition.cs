using System.Collections.Generic;

/// <summary>
/// Something a level needs done to win. A level lists any number; all must
/// be met. Add a value here and a case in MissionDirector.IsMet for a new one.
/// </summary>
public enum WinCondition
{
    /// <summary>Every enemy team's Dungeon Heart destroyed (Neutral dungeons don't count).</summary>
    DestroyEnemyHearts,
    /// <summary>The lord of the land killed — or, once torture exists, converted to your side.</summary>
    DefeatLord,
}

public static class WinConditions
{
    /// <summary>What a level with none listed uses.</summary>
    public static readonly WinCondition[] Default = { WinCondition.DestroyEnemyHearts };

    /// <summary>The level's win conditions, or the default if it lists none.</summary>
    public static IReadOnlyList<WinCondition> Of(LevelData level) =>
        level?.winConditions != null && level.winConditions.Count > 0 ? level.winConditions : Default;

    /// <summary>How the objective reads on the briefing and results screens.</summary>
    public static string Describe(WinCondition c) => c switch
    {
        WinCondition.DestroyEnemyHearts => "Destroy every enemy Dungeon Heart",
        WinCondition.DefeatLord         => "Defeat the lord of the land",
        _                               => c.ToString(),
    };
}
