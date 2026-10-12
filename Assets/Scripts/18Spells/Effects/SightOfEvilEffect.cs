/// <summary>
/// Sight of Evil: everything within Radius tiles (+RadiusPerLevel) of the
/// point clicked is in sight for Duration seconds (+DurationPerLevel) —
/// walls don't block it. Once it ends the area stays remembered, as last
/// seen, like anywhere else explored.
/// </summary>
[SpellEffect("SightOfEvil")]
public class SightOfEvilEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target) =>
        FogOfWar.Instance != null && target.Cell != null &&
        target.Spell.RadiusAt(target.Level) > 0f && target.Spell.DurationAt(target.Level) > 0f;

    public override bool Cast(in SpellTarget target)
    {
        var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (grid == null) return false;
        FogOfWar.Instance.RevealCircle(target.Caster, target.Point,
                                       target.Spell.RadiusAt(target.Level) * grid.CellSize,
                                       target.Spell.DurationAt(target.Level));
        return true;
    }
}
