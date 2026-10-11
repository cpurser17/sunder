using UnityEngine;

/// <summary>
/// Cave-In: rocks fall on an area — Power damage (+PowerPerLevel) of the
/// workbook's DamageType to every enemy minion within Radius tiles
/// (+RadiusPerLevel). The caster's own minions, and anyone else not at war
/// with them, are untouched. It must land on open ground, not solid rock.
/// </summary>
[SpellEffect("CaveIn")]
public class CaveInEffect : SpellEffect
{
    public override bool CanTarget(in SpellTarget target)
    {
        var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (grid == null || target.Cell == null) return false;
        var def = grid.GetDefinition(target.Cell.TileType);
        return def != null && def.traversalType != TraversalType.Impassable;
    }

    public override bool Cast(in SpellTarget target)
    {
        var   grid   = GameManager2D.Instance.Grid;
        float radius = target.Spell.RadiusAt(target.Level) * grid.CellSize;
        float power  = target.Spell.PowerAt(target.Level);

        // A copy: a kill removes the minion from All.
        foreach (var m in new System.Collections.Generic.List<MinionController>(MinionController.All))
        {
            if (m == null || !m.IsTargetable || !CombatSystem.IsEnemy(target.Caster, m.Faction)) continue;
            float dx = m.transform.position.x - target.Point.x, dz = m.transform.position.z - target.Point.z;
            if (dx * dx + dz * dz > radius * radius) continue;

            float multiplier = m.Definition != null ? m.Definition.DamageTakenMultiplier(target.Spell.damageType) : 1f;
            m.TakeDamage(power * multiplier, null);
        }

        RockFall.Spawn(target.Point, radius, grid.CellSize);
        return true;
    }
}
