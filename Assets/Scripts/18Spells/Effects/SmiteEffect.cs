/// <summary>
/// Smite: a lightning bolt on an enemy minion — Power damage, plus
/// PowerPerLevel for each level charged, of the workbook's DamageType (so a
/// minion's weaknesses and resistances to it apply). It can't miss.
/// </summary>
[SpellEffect("Smite")]
public class SmiteEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target)
    {
        var m = target.Minion;
        return m != null && m.IsAlive && !m.IsHeld && CombatSystem.IsEnemy(target.Caster, m.Faction);
    }

    public override bool Cast(in SpellTarget target)
    {
        var m = target.Minion;
        float multiplier = m.Definition != null ? m.Definition.DamageTakenMultiplier(target.Spell.damageType) : 1f;
        m.TakeDamage(target.Spell.PowerAt(target.Level) * multiplier, null);
        return true;
    }
}
