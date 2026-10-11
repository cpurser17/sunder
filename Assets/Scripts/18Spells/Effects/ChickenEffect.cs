/// <summary>
/// Chicken: turns an enemy minion into a chicken for Duration seconds. While
/// it's a chicken it wanders aimlessly and can't be hurt, attacked, picked
/// up or eaten; then it's itself again with the health it had.
/// </summary>
[SpellEffect("Chicken")]
public class ChickenEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target)
    {
        var m = target.Minion;
        return m != null && m.IsTargetable && CombatSystem.IsEnemy(target.Caster, m.Faction) &&
               target.Spell.DurationAt(target.Level) > 0f;
    }

    public override bool Cast(in SpellTarget target) =>
        target.Minion.Polymorph(target.Spell.DurationAt(target.Level));
}
