using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One summonable minion type. A faction's MinionSummoner holds a roster of
/// these; which ones it can actually summon at any moment is narrowed by
/// LevelData.allowedMinionIds (mission design), then by the prerequisites
/// below, then by population headroom.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/MinionDefinition", fileName = "NewMinionDefinition")]
public class MinionDefinition : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Stable id referenced by LevelData.allowedMinionIds. Not the " +
             "display name — renaming this breaks existing level files.")]
    public string minionId;
    public string displayName;
    public GameObject prefab;

    [Header("Summon weighting")]
    [Tooltip("Relative pick chance among minions that currently qualify to be summoned.")]
    public float summonWeight = 1f;
    [Tooltip("Population slots this minion consumes.")]
    public int populationCost = 1;

    [Header("Prerequisites")]
    [Tooltip("Faction must have at least one room of each listed type built.")]
    public List<TileType> requiredRoomTypes = new();
    [Tooltip("Faction must have completed all listed research ids.")]
    public List<string> requiredResearchIds = new();
}
