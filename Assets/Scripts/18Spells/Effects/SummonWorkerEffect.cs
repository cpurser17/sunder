/// <summary>
/// Summon Worker: a new worker on the tile clicked — one of the caster's own
/// tiles, or unclaimed cave, that a worker can stand on.
///
/// Workbook: Cost is the first worker's price; Power is added for every
/// worker the caster already has (DK-style rising cost).
/// </summary>
[SpellEffect("SummonWorker")]
public class SummonWorkerEffect : SpellEffect
{
    public override int Cost(FactionID caster, SpellDefinition spell, int level)
    {
        var spawner = WorkerSpawner.GetForFaction(caster);
        int workers = spawner != null ? spawner.ActiveWorkerCount : 0;
        return spell.CostAt(level) + workers * UnityEngine.Mathf.RoundToInt(spell.PowerAt(level));
    }

    public override bool CanTarget(in SpellTarget target)
    {
        var spawner = WorkerSpawner.GetForFaction(target.Caster);
        return spawner != null && target.Cell != null && spawner.CanSpawnOn(target.Cell);
    }

    public override bool Cast(in SpellTarget target)
    {
        var spawner = WorkerSpawner.GetForFaction(target.Caster);
        return spawner != null && spawner.SpawnAt(target.Cell, target.Point);
    }
}
