using UnityEngine;

/// <summary>What a spell is aimed at.</summary>
public enum SpellTargeting
{
    None,         // casts on the spot — no target
    Tile,         // a tile (the effect says which kinds)
    Area,         // a point on the map, hitting everything within the radius
    OwnMinion,    // one of the caster's minions
    EnemyMinion,  // an enemy minion
    EnemyWall,    // an enemy-owned Wall tile
}

/// <summary>Where a spell may be cast.</summary>
public enum SpellCastOn
{
    OwnTerritory,  // the target tile must be the caster's
    Visible,       // anywhere the caster can see (anywhere, until fog of war exists)
    Anywhere,
}

/// <summary>
/// One spell's numbers, from a row of the SpellData workbook (Sunder >
/// Import Spell Data). Everything shared by every spell lives here: cost,
/// charging, cooldown, where it may be cast and its HUD slot. What the spell
/// actually does is its effect script (see SpellEffect), named by Effect.
///
/// Level-based values are base + (level − 1) × per-level, so a spell with
/// MaxLevel 1 just uses the base. Each effect reads only the columns it
/// needs; the workbook's ReadMe says which.
/// </summary>
[System.Serializable]
public class SpellDefinition
{
    public string id;
    public string displayName;
    [Tooltip("The effect script that runs (SpellEffect id). Blank in the workbook = the spell's Id.")]
    public string effect;
    [TextArea] public string description;

    [Header("Aiming")]
    public SpellTargeting targeting  = SpellTargeting.Tile;
    public SpellCastOn    castOn     = SpellCastOn.Visible;

    [Header("Cost and charging")]
    public int   cost          = 0;
    public int   costPerLevel  = 0;
    [Min(1)] public int maxLevel = 1;
    [Tooltip("Seconds of holding the button per extra level.")]
    public float chargeSeconds = 0.5f;
    [Tooltip("Seconds before the same player can cast it again. 0 = none.")]
    public float cooldown      = 0f;

    [Header("Effect values")]
    public float power;
    public float powerPerLevel;
    public float radius;
    public float radiusPerLevel;
    public float duration;
    public float durationPerLevel;
    public DamageType damageType = DamageType.Lightning;

    [Header("Upkeep (lasting spells)")]
    public int   upkeepGold;
    public float upkeepInterval = 10f;

    [Header("HUD")]
    [Tooltip("Slot in the Spells tab (also its hotkey). 0 = no button.")]
    public int buttonRow;
    public int buttonColumn;

    public string EffectId => string.IsNullOrWhiteSpace(effect) ? id : effect;

    public int   CostAt(int level)     => Mathf.Max(0, cost + (Clamp(level) - 1) * costPerLevel);
    public float PowerAt(int level)    => power    + (Clamp(level) - 1) * powerPerLevel;
    public float RadiusAt(int level)   => radius   + (Clamp(level) - 1) * radiusPerLevel;
    public float DurationAt(int level) => duration + (Clamp(level) - 1) * durationPerLevel;

    public int Clamp(int level) => Mathf.Clamp(level, 1, Mathf.Max(1, maxLevel));
}
