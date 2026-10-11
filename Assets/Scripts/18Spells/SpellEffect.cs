using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Everything about one cast: who, which spell, how charged, and at what.</summary>
public struct SpellTarget
{
    public FactionID        Caster;
    public SpellDefinition  Spell;
    public int              Level;
    /// <summary>The tile aimed at — for a minion target, the tile it stands on.</summary>
    public GridCell         Cell;
    /// <summary>The point on the map aimed at.</summary>
    public Vector3          Point;
    /// <summary>The minion aimed at, for minion-targeted spells.</summary>
    public MinionController Minion;
}

/// <summary>Names the spell effect a class implements — the Effect column in the SpellData workbook.</summary>
[AttributeUsage(AttributeTargets.Class)]
public class SpellEffectAttribute : Attribute
{
    public readonly string Id;
    public SpellEffectAttribute(string id) => Id = id;
}

/// <summary>
/// What a spell actually does. One small class per effect, tagged with
/// [SpellEffect("Id")] — found automatically, nothing to register. The
/// shared work (aiming, charging, paying, cooldown, warnings) is done by
/// SpellSystem and SpellCaster before an effect is asked to act.
///
/// Effects hold no per-cast state; anything that lasts (a rally point, a
/// polymorph timer) belongs to the system the effect drives.
/// </summary>
public abstract class SpellEffect
{
    /// <summary>Gold to cast at this level. Most spells use the workbook's cost; some (Summon Worker) vary.</summary>
    public virtual int Cost(FactionID caster, SpellDefinition spell, int level) => spell.CostAt(level);

    /// <summary>Whether this target can take the spell. Checked before paying — an invalid cast costs nothing.</summary>
    public abstract bool CanTarget(in SpellTarget target);

    /// <summary>Does the spell. Returns false if it couldn't after all (then nothing is paid).</summary>
    public abstract bool Cast(in SpellTarget target);

    // ── Registry ───────────────────────────────────────────────────────

    private static Dictionary<string, SpellEffect> _effects;

    /// <summary>The effect with this id, or null if no script implements it yet.</summary>
    public static SpellEffect Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (_effects == null) Discover();
        return _effects.TryGetValue(id, out var effect) ? effect : null;
    }

    private static void Discover()
    {
        _effects = new Dictionary<string, SpellEffect>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in typeof(SpellEffect).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(SpellEffect).IsAssignableFrom(type)) continue;
            var tag = (SpellEffectAttribute)Attribute.GetCustomAttribute(type, typeof(SpellEffectAttribute));
            if (tag == null) continue;
            if (_effects.ContainsKey(tag.Id))
            {
                Debug.LogError($"[SpellEffect] Two effects claim the id {tag.Id} — {type.Name} ignored.");
                continue;
            }
            _effects[tag.Id] = (SpellEffect)Activator.CreateInstance(type);
        }
    }
}
