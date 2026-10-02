using UnityEngine;

/// <summary>
/// One attack or defensive move. Generated from the MinionData workbook's
/// Abilities sheet by Sunder > Import Minion Data — edit the sheet and
/// re-import rather than editing these assets, as the import overwrites them.
///
/// Nothing executes abilities yet; this is the data combat will read.
/// </summary>
public class AbilityDefinition : ScriptableObject
{
    public enum AbilityKind      { Attack, Defence, Heal, Buff, Debuff, Summon }
    public enum AbilityTargeting { Self, SingleEnemy, SingleAlly, AreaEnemy, AreaAlly, Cone, Line }
    public enum AbilityDelivery  { Melee, Ranged, Magic }

    [Header("Identity")]
    [Tooltip("Stable id referenced by Ability1-5 on the _Data sheets.")]
    public string abilityId;
    public string displayName;
    [TextArea] public string description;

    [Header("Behaviour")]
    public AbilityKind      kind      = AbilityKind.Attack;
    public AbilityTargeting targeting = AbilityTargeting.SingleEnemy;
    [Tooltip("Melee/Ranged roll Accuracy against Dexterity.")]
    public AbilityDelivery  delivery  = AbilityDelivery.Melee;
    [Tooltip("Ignored when dealsDamage is false.")]
    public DamageType damageType;
    [Tooltip("False for abilities with no DamageType (shields, buffs, heals).")]
    public bool dealsDamage = true;

    [Header("Power")]
    [Tooltip("Flat damage/heal/shield amount before scaling.")]
    public float basePower;
    public MinionStat scalingStat = MinionStat.Strength;
    [Tooltip("Power = basePower + scalingStat x scalingFactor.")]
    public float scalingFactor;

    [Header("Reach & timing")]
    [Tooltip("Tiles. 1 = adjacent.")]
    public float range = 1f;
    [Tooltip("Tiles. 0 = single target.")]
    public float areaRadius;
    [Tooltip("Seconds between uses.")]
    public float cooldown = 1f;
    [Tooltip("Seconds of wind-up before the effect lands.")]
    public float castTime;

    [Header("Status effect (optional)")]
    [Tooltip("Free text until status effects have their own sheet, e.g. Burn, Stun.")]
    public string statusEffect;
    [Range(0f, 1f)] public float statusChance;
    public float statusDuration;
    public float statusMagnitude;

    /// <summary>Power before the target's defences and damage multipliers.</summary>
    public float PowerFor(MinionDefinition user, int level) =>
        basePower + (user != null ? user.GetStat(scalingStat, level) : 0f) * scalingFactor;
}
