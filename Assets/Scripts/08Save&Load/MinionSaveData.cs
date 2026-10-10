using System.Collections.Generic;

/// <summary>
/// One minion in a save: who it is, where, and everything about its state
/// that should survive a reload. What it was doing at that moment (walking
/// to a job, mid-meal, fighting) isn't saved — on load it decides afresh
/// from its needs, exactly as it would after being set down by the hand.
/// </summary>
[System.Serializable]
public class MinionSaveData
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID faction;
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public MinionController.SpawnSource source;

    // Identity — MinionDefinition.factionId + minionId. Null for a worker
    // spawned with no data.
    public string contentFactionId;
    public string minionId;

    // World position (x, z).
    public float x, z;

    public int   level = 1;
    public float experience;
    public float health;

    // Needs
    public float tiredness;
    public float hunger;

    // Wages
    public int   owedWages;
    public int   missedPaydays;
    public float angerPerUnpaidSalary;

    /// <summary>Lasting anger by cause (MinionTemper.Grievance name → 0-1).</summary>
    public Dictionary<string, float> grievances;

    /// <summary>A creature that has already reported for duty at its heart.</summary>
    public bool reported;

    /// <summary>Its Lair bed, or -1 if none.</summary>
    public int bedX = -1, bedY = -1;

    /// <summary>Gold a worker was carrying.</summary>
    public int carryingGold;
}

/// <summary>A chicken in a save (world x, z).</summary>
[System.Serializable]
public class ChickenSaveData
{
    public float x, z;
}

/// <summary>A faction's research and room-work progress in a save.</summary>
[System.Serializable]
public class FactionProgressSaveData
{
    public List<string> completedResearch = new();
    public float researchPoints;
    public float manufacturePoints;
    public float prayerPoints;
}

/// <summary>One cell, by grid coordinates — e.g. a dig mark.</summary>
[System.Serializable]
public class CellRefSaveData
{
    public int x, y;
    public CellRefSaveData() { }
    public CellRefSaveData(int x, int y) { this.x = x; this.y = y; }
}
