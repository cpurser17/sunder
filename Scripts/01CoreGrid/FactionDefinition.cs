using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One playable faction's content — its roster, prerequisites, summon
/// pacing, and Dungeon Heart appearance. Seat-agnostic: nothing here says
/// which of Player/AI1/AI2/AI3 is playing it. A level's FactionSetup assigns
/// a FactionDefinition (by factionContentId, via FactionRegistry) to a seat,
/// so two seats can play the same faction independently, or different ones,
/// entirely as level/mission configuration.
///
/// There is deliberately no shared "default" any of this falls back to —
/// every faction's roster and numbers are bespoke, authored on its own
/// asset. DungeonHeart/MinionSummoner's own defaultX fields are only a
/// missing-data safety net for a seat with no FactionDefinition assigned,
/// not a baseline most factions are expected to share.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/FactionDefinition", fileName = "NewFactionDefinition")]
public class FactionDefinition : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Stable id referenced by FactionSetup.contentFactionId in level files. " +
             "Not the display name — renaming this breaks existing level files.")]
    public string factionContentId;
    public string displayName;
    [TextArea] public string description;

    [Tooltip("Optional. Used for future AI relationship logic (e.g. a faction's " +
             "default opinion of its own pantheon's counterpart vs. an unrelated faction).")]
    public Pantheon pantheon;

    [Header("Roster")]
    [Tooltip("Every minion type this faction can summon, subject to each " +
             "MinionDefinition's own prerequisites and the mission's allowedMinionIds.")]
    public List<MinionDefinition> roster = new();

    [Header("Population")]
    public int populationLimit = 10;

    [Header("Timing")]
    [Tooltip("Average seconds between summons.")]
    public float baseSummonInterval = 30f;
    [Tooltip("Random offset applied to each interval, in seconds. X = min, Y = max.")]
    public Vector2 summonIntervalJitter = new(-5f, 5f);

    [Header("Dungeon Heart")]
    [Tooltip("0 = use DungeonHeart's own fallback.")]
    public int maxHeartHitPoints = 0;
    [Tooltip("Empty = use DungeonHeart's own fallback crystal.")]
    public GameObject crystalPrefab;
}
