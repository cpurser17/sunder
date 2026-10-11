/// <summary>
/// Heal: restores health to one of the caster's minions — Power, plus
/// PowerPerLevel for each level charged. The minion stands still again for
/// the hand's hover pause, so it can be healed several times before it
/// walks off. A minion at full health can't be targeted (nothing is spent).
/// </summary>
[SpellEffect("Heal")]
public class HealEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target)
    {
        var m = target.Minion;
        return m != null && m.IsTargetable && m.Faction == target.Caster &&
               m.HealthFraction < 0.999f;
    }

    public override bool Cast(in SpellTarget target)
    {
        target.Minion.Heal(target.Spell.PowerAt(target.Level));
        KeeperHand.Instance?.HoldStill(target.Minion);
        return true;
    }
}
