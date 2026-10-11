/// <summary>
/// Call to Arms: puts the caster's rally point on the tile clicked (moving
/// it if there's one already). Their creatures within Radius tiles drop what
/// they're doing — short of fighting — to gather there; workers carry on.
/// UpkeepGold is taken every UpkeepInterval while it stands.
///
/// Called off by pressing the spell's button while it's on, or by
/// right-clicking the marker with the spell armed.
/// </summary>
[SpellEffect("CallToArms")]
public class CallToArmsEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target) =>
        RallyPoints.Instance != null && target.Cell != null &&
        TraversalRules.CanPathOn(target.Cell.TileType, TraversalCapability.LandOnly, target.Caster);

    public override bool Cast(in SpellTarget target)
    {
        var spell = target.Spell;
        RallyPoints.Instance.Set(target.Caster, target.Point, spell.RadiusAt(target.Level),
                                 spell.upkeepGold, spell.upkeepInterval);
        return true;
    }

    public override bool IsToggledOn(FactionID caster) =>
        RallyPoints.Instance != null && RallyPoints.Instance.IsActive(caster);

    public override void ToggleOff(FactionID caster) => RallyPoints.Instance?.Clear(caster);

    public override bool OnRightClick(FactionID caster, UnityEngine.Vector3 point)
    {
        var rally = RallyPoints.Instance;
        if (rally == null || !rally.IsOnMarker(caster, point)) return false;
        rally.Clear(caster);
        return true;
    }
}
