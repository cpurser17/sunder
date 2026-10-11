/// <summary>
/// Destroy Walls: turns one enemy Wall tile back into unowned Stone, which
/// anyone's workers can dig — a costly way into an enemy dungeon.
/// </summary>
[SpellEffect("DestroyWalls")]
public class DestroyWallsEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target) =>
        target.Cell != null && target.Cell.TileType == TileType.Wall &&
        CombatSystem.IsEnemy(target.Caster, target.Cell.Owner);

    public override bool Cast(in SpellTarget target)
    {
        var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (grid == null) return false;
        grid.SetTileType(target.Cell, TileType.Stone, FactionID.Unaligned);
        return true;
    }
}
