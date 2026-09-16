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

    public void Initialise(FactionID faction) => Faction = faction;

    public bool HasResearch(string researchId) =>
        !string.IsNullOrEmpty(researchId) && _completed.Contains(researchId);

    public void CompleteResearch(string researchId)
    {
        if (!string.IsNullOrEmpty(researchId)) _completed.Add(researchId);
    }

    public void SetSummonIntervalMultiplier(float multiplier) =>
        SummonIntervalMultiplier = Mathf.Max(0.05f, multiplier);
}
