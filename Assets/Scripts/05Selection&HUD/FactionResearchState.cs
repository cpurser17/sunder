using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks completed research and the resulting summon-interval multiplier for
/// one faction. One instance per active faction, created by GameManager2D
/// alongside FactionWallet — same lifecycle, same reason (per-faction runtime
/// state with no scene presence of its own).
///
/// This deliberately does not know what any research id "means" — completing
/// one and deciding what it unlocks (a MinionDefinition prerequisite, a
/// summon-speed bonus, anything else) is content, wired in from wherever
/// research actually gets completed.
/// </summary>
public class FactionResearchState : MonoBehaviour
{
    public FactionID Faction { get; private set; }

    private readonly HashSet<string> _completed = new();

    /// <summary>Multiplies MinionSummoner's summon interval. 1 = no change, below 1 = faster.</summary>
    public float SummonIntervalMultiplier { get; private set; } = 1f;

    // ── Progress from room work ────────────────────────────────────────
    // Points minions produce working in rooms. Nothing spends them yet —
    // the research tree (Library), traps & doors (Workshop) and prayer
    // effects (Shrine) will.

    /// <summary>Research points from minions working in the Library.</summary>
    public float ResearchPoints    { get; private set; }
    /// <summary>Manufacture points from minions working in the Workshop.</summary>
    public float ManufacturePoints { get; private set; }
    /// <summary>Prayer points from minions praying at the Shrine.</summary>
    public float PrayerPoints      { get; private set; }

    public void AddResearchPoints(float amount)    { if (amount > 0f) ResearchPoints    += amount; }
    public void AddManufacturePoints(float amount) { if (amount > 0f) ManufacturePoints += amount; }
    public void AddPrayerPoints(float amount)      { if (amount > 0f) PrayerPoints      += amount; }

    public void Initialise(FactionID faction) => Faction = faction;

    public bool HasResearch(string researchId) =>
        !string.IsNullOrEmpty(researchId) && _completed.Contains(researchId);

    public void CompleteResearch(string researchId)
    {
        if (!string.IsNullOrEmpty(researchId)) _completed.Add(researchId);
    }

    // ── Save / load ────────────────────────────────────────────────────

    public FactionProgressSaveData Capture() => new()
    {
        completedResearch = new List<string>(_completed),
        researchPoints    = ResearchPoints,
        manufacturePoints = ManufacturePoints,
        prayerPoints      = PrayerPoints,
    };

    public void Restore(FactionProgressSaveData data)
    {
        if (data == null) return;
        _completed.Clear();
        if (data.completedResearch != null)
            foreach (var id in data.completedResearch) CompleteResearch(id);
        ResearchPoints    = data.researchPoints;
        ManufacturePoints = data.manufacturePoints;
        PrayerPoints      = data.prayerPoints;
    }

    public void SetSummonIntervalMultiplier(float multiplier) =>
        SummonIntervalMultiplier = Mathf.Max(0.05f, multiplier);
}
