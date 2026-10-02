using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// One minion type: identity, art, combat data, behaviour preferences,
/// summoning prerequisites and level-by-level stats.
///
/// Normally generated from the MinionData workbook by Sunder > Import Minion
/// Data (one asset per faction per minion, under Data/Generated/Minions) —
/// edit the workbook and re-import rather than editing generated assets, as
/// the import overwrites them. Hand-made assets still work for tests.
///
/// There is no prefab per minion. MinionSummoner (portal) and WorkerSpawner
/// (workers) instantiate one shared template and MinionController.Initialise
/// applies this definition to it (token, movement, stats, behaviour).
/// <see cref="prefab"/> is only an optional override for a minion whose
/// structure genuinely differs from the template.
///
/// A faction's roster (see FactionDefinition) holds the summonable ones;
/// which it can actually summon at any moment is narrowed by
/// LevelData.allowedMinionIds (mission design), then by the prerequisites
/// below, then by population headroom.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/MinionDefinition", fileName = "NewMinionDefinition")]
public class MinionDefinition : ScriptableObject
{
    /// <summary>
    /// Broad role, from the _Data sheet's Stance column. For AI decision-making
    /// and UI grouping. Append only — assets store these as numbers.
    /// </summary>
    public enum MinionStance { Fighter, Mage, Ranger, Scout, Utility, Unique, Commander, Worker, General }

    [System.Serializable]
    public struct DamageMultiplier
    {
        public DamageType type;
        [Tooltip("Damage taken x this. 0 = immune, 0.5 = strong to, 2 = weak to.")]
        public float multiplier;
    }

    [System.Serializable]
    public struct AbilityUnlock
    {
        [Tooltip("Level at which the ability is gained.")]
        public int gainLevel;
        public AbilityDefinition ability;
    }

    [System.Serializable]
    public struct MinionRelation
    {
        public MinionDefinition other;
        [Tooltip("1 = likes, -1 = hates; fractions for degrees of feeling.")]
        public float feeling;
    }

    [System.Serializable]
    public struct RoomRequirement
    {
        public TileType roomType;
        [Tooltip("Smallest room of this type that counts, in tiles. 0 = any size.")]
        public int minTiles;
    }

    [Header("Identity")]
    [Tooltip("Faction content id this minion belongs to, e.g. 01U. Matches FactionDefinition.factionContentId.")]
    public string factionId;
    [Tooltip("Stable id referenced by LevelData.allowedMinionIds, e.g. T1F. Shared " +
             "across factions on purpose — allowing \"T1F\" allows every faction's " +
             "tier 1 fighter. Not the display name; renaming breaks level files.")]
    public string minionId;
    public string displayName;
    [Tooltip("2D token shown on the template prefab's SpriteRenderer.")]
    public Sprite token;
    [Tooltip("Optional. Leave empty to use MinionSummoner's shared template.")]
    public GameObject prefab;

    [Header("Classification")]
    [FormerlySerializedAs("role")]
    public MinionStance stance = MinionStance.Fighter;
    [Tooltip("Roughly how advanced this unit is. Parsed from the id (T2M → 2); 0 for non-tiered minions.")]
    public int tier = 1;

    [Header("Movement")]
    public TraversalCapability movement = TraversalCapability.LandOnly;

    [Header("Worker jobs")]
    [Tooltip("Digs, claims, reinforces and hauls gold once it has reported for duty. " +
             "Always true in effect for the Worker stance; set it for e.g. enemy " +
             "diggers or utility minions that help out.")]
    public bool canDoWorkerJobs;

    [Header("Defence")]
    [Tooltip("Damage types not listed take normal (x1) damage.")]
    public List<DamageMultiplier> damageTaken = new();

    [Header("Activity preferences (0-1)")]
    public float researchPreference;
    public float trainPreference;
    public float buildPreference;
    public float prayPreference;
    public float torturePreference;

    [Header("Abilities")]
    public List<AbilityUnlock> abilities = new();

    [Header("Relations (same faction)")]
    public List<MinionRelation> relations = new();

    [Header("Summoning")]
    [Tooltip("False for minions that never come through the portal (commanders, workers, the general).")]
    public bool summonable = true;
    [Tooltip("Relative pick chance among minions that currently qualify to be summoned.")]
    public float summonWeight = 1f;
    [Tooltip("Population slots this minion consumes.")]
    public int populationCost = 1;

    [Header("Prerequisites")]
    [Tooltip("Faction must have a room of each listed type, at least minTiles in size.")]
    public List<RoomRequirement> requiredRooms = new();
    [Tooltip("Faction must have completed all listed research ids.")]
    public List<string> requiredResearchIds = new();

    [Header("Levels")]
    [Tooltip("Highest level. From the workbook's MaxLevel cell.")]
    public int maxLevel = 10;
    [Tooltip("One entry per stat. Stats with no entry read as 0.")]
    public List<StatCurve> stats = new();

    // ── Queries ────────────────────────────────────────────────────────

    public float GetStat(MinionStat stat, int level)
    {
        foreach (var curve in stats)
            if (curve.stat == stat) return curve.Evaluate(level, maxLevel);
        return 0f;
    }

    /// <summary>
    /// Like GetStat, but false when the stat has no row on the _Levels sheet
    /// yet — so callers with their own tuning (e.g. the worker prefab) can keep
    /// it until the data is filled in, instead of dropping to a 0 default.
    /// </summary>
    public bool TryGetAuthoredStat(MinionStat stat, int level, out float value)
    {
        foreach (var curve in stats)
            if (curve.stat == stat && curve.authored) { value = curve.Evaluate(level, maxLevel); return true; }
        value = 0f;
        return false;
    }

    /// <summary>Total experience needed to be at the given level.</summary>
    public float ExperienceForLevel(int level) => GetStat(MinionStat.Experience, level);

    /// <summary>
    /// Highest level whose experience threshold the given total meets. Stops
    /// where the threshold stops rising, so a minion whose Experience row
    /// isn't filled in yet (flat 0) stays at level 1 instead of jumping to max.
    /// </summary>
    public int LevelForExperience(float experience)
    {
        int level = 1;
        while (level < maxLevel)
        {
            float next = ExperienceForLevel(level + 1);
            if (next <= ExperienceForLevel(level) || experience < next) break;
            level++;
        }
        return level;
    }

    public float DamageTakenMultiplier(DamageType type)
    {
        foreach (var d in damageTaken)
            if (d.type == type) return d.multiplier;
        return 1f;
    }

    /// <summary>Abilities unlocked at or below the given level.</summary>
    public IEnumerable<AbilityDefinition> AbilitiesAt(int level)
    {
        foreach (var a in abilities)
            if (a.ability != null && a.gainLevel <= level) yield return a.ability;
    }

    /// <summary>
    /// Configures a spawned template (summoned creature or worker) as this
    /// minion: token on its SpriteRenderer, movement on its GridAgent, and the
    /// agent's radius re-measured for the new token. A 3D model and animator
    /// override will hang off here the same way.
    /// </summary>
    public void ApplyTo(GameObject target, GridAgent agent)
    {
        var sprite = target.GetComponentInChildren<SpriteRenderer>();
        if (token != null)
        {
            if (sprite != null) sprite.sprite = token;
            else Debug.LogWarning($"[MinionDefinition] {target.name} has no SpriteRenderer for {factionId} {minionId}'s token.");
        }
        else if (sprite != null && sprite.sprite == null)
        {
            Debug.LogWarning($"[MinionDefinition] {factionId} {minionId} has no token, so it spawns " +
                             $"invisible — add a Token_{factionId}_{minionId} image and re-import.");
        }

        if (agent != null)
        {
            agent.SetCapability(movement);
            agent.RemeasureRadius();
        }
    }

    /// <summary>1 = likes, -1 = hates, 0 = neutral or unrelated.</summary>
    public float FeelingTowards(MinionDefinition other)
    {
        foreach (var r in relations)
            if (r.other == other) return r.feeling;
        return 0f;
    }
}
