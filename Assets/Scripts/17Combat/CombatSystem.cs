using UnityEngine;

/// <summary>One minion's attack: from its ability data, or a default.</summary>
public struct AttackProfile
{
    /// <summary>Reach in tiles, beyond the two bodies' edges.</summary>
    public float Range;
    public float Cooldown;
    public float BasePower;
    public float ScalingFactor;
    public MinionStat ScalingStat;
    public DamageType DamageType;
    public AbilityDefinition.AbilityDelivery Delivery;

    public bool IsRanged => Delivery != AbilityDefinition.AbilityDelivery.Melee;
}

/// <summary>
/// The rules of a fight — who is an enemy, who can see whom, what an attack
/// does — shared by every minion of every faction, human or AI. The
/// fighting itself is CombatBehaviour's; MinionController decides when.
///
/// Enemies
/// -------
/// Any minion of another faction, and the Hostile team against everyone.
/// Unaligned minions are neutral. (No alliances yet — IsEnemy is the hook.)
///
/// Sight (Dungeon Keeper style)
/// ----------------------------
/// Within sightRange tiles, along a straight line that crosses only open
/// tiles: floor, rooms, and liquid — so a ranger can shoot across lava it
/// can't walk over. Rock, walls and the like block it.
///
/// Attacks
/// -------
/// A minion uses its lowest-level attack ability it has reached (Abilities
/// sheet). With none, it gets a default: Rangers a ranged Pierce shot,
/// Mages a ranged Fire bolt (scaled by Magic), everyone else a melee Blunt
/// blow — basic power, one a second.
///
///   hit chance = base + (attacker Accuracy − target Dexterity) × perPoint,
///                clamped to [minHit, maxHit]
///   damage     = (basePower + scaling stat × scalingFactor)
///                × target's damage-type multiplier (StrongTo/WeakTo)
///                ÷ (1 + defence / defenceScale), at least minDamage
///   defence    = Fortitude against melee and ranged, Resistance against magic
///
/// Exhaustion and starving already lower the stats involved.
///
/// Experience
/// ----------
/// Each attack a minion makes — hit or miss — earns its share of
/// combatXpMultiplier × the training rate per minute (the rate × cooldown),
/// so only minions actually using their attacks learn from a fight, and one
/// attacking without pause earns the full rate. The one landing the killing
/// blow gets killXpPerVictimLevel × the victim's level on top.
///
/// Scene setup: none — GameManager2D adds one. Add it yourself to tune.
/// </summary>
public class CombatSystem : MonoBehaviour
{
    public static CombatSystem Instance { get; private set; }

    [Header("Sight")]
    [Tooltip("How far, in tiles, minions see enemies.")]
    [SerializeField, Min(1f)] private float sightRange = 6f;
    [Tooltip("Seconds between each minion's looks around for enemies.")]
    [SerializeField, Min(0.05f)] private float senseInterval = 0.3f;
    [Tooltip("A target further than this × sight range is let go.")]
    [SerializeField, Min(1f)] private float leashMultiplier = 1.5f;

    [Header("Hit chance")]
    [SerializeField, Range(0f, 1f)] private float baseHitChance = 0.5f;
    [Tooltip("Hit chance added per point of attacker Accuracy over target Dexterity.")]
    [SerializeField, Min(0f)] private float hitPerPoint = 0.02f;
    [SerializeField, Range(0f, 1f)] private float minHitChance = 0.05f;
    [SerializeField, Range(0f, 1f)] private float maxHitChance = 0.95f;

    [Header("Damage")]
    [Tooltip("Defence (Fortitude/Resistance) equal to this halves damage.")]
    [SerializeField, Min(1f)] private float defenceScale = 100f;
    [SerializeField, Min(0f)] private float minDamage = 1f;

    [Header("Default attacks (minions with no attack ability)")]
    [SerializeField, Min(0f)] private float defaultPower = 10f;
    [SerializeField, Min(0f)] private float defaultScaling = 0.2f;
    [SerializeField, Min(0.1f)] private float defaultCooldown = 1f;
    [Tooltip("Reach in tiles of a default melee attack.")]
    [SerializeField, Min(0f)] private float defaultMeleeRange = 0.25f;
    [Tooltip("Reach in tiles of a default ranged attack.")]
    [SerializeField, Min(0f)] private float defaultRangedRange = 5f;

    [Header("Experience")]
    [Tooltip("Combat experience per minute of attacking without pause, as a multiple of the training rate. " +
             "Earned per attack made, not per second in a fight.")]
    [SerializeField, Min(0f)] private float combatXpMultiplier = 2f;
    [Tooltip("Bonus experience for the killing blow, per level of the victim.")]
    [SerializeField, Min(0f)] private float killXpPerVictimLevel = 100f;

    [Header("Rallying and fleeing")]
    [Tooltip("Allies within this many tiles of a fight join in.")]
    [SerializeField, Min(0f)] private float alertRadius = 6f;
    [Tooltip("A worker dropped by the hand within this many tiles of an enemy fights instead of fleeing.")]
    [SerializeField, Min(0f)] private float dropFightRadius = 1.5f;
    [Tooltip("A fleeing worker caught within this many tiles of its enemy turns to fight.")]
    [SerializeField, Min(0f)] private float corneredRadius = 1f;
    [Tooltip("Seconds a minion keeps trying to reach a target it can neither reach nor hit before ignoring it for a while.")]
    [SerializeField, Min(1f)] private float giveUpSeconds = 5f;

    public float SightRange      => sightRange;
    public float SenseInterval   => senseInterval;
    public float LeashMultiplier => leashMultiplier;
    public float DropFightRadius => dropFightRadius;
    public float CorneredRadius  => corneredRadius;
    public float GiveUpSeconds   => giveUpSeconds;

    private GridManager2D Grid => GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
    public float CellSize => Grid != null ? Grid.CellSize : 1f;

    /// <summary>
    /// Goes up by one on every tile change. A minion that gave up on an
    /// unreachable enemy remembers the version it gave up at; once the map
    /// has changed (a bridge built, a wall dug out) it checks again whether
    /// it can now reach or hit that enemy (CanReachOrHit), and only then
    /// stops ignoring it.
    /// </summary>
    public int MapVersion { get; private set; }

    private GridManager2D  _subscribedGrid;
    private GridPathfinder _pathfinder;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Update()
    {
        // The grid is created by the scene bootstrapper; hook it once it exists.
        var grid = Grid;
        if (grid == null || grid == _subscribedGrid) return;
        if (_subscribedGrid != null) _subscribedGrid.OnTileChanged -= OnTileChanged;
        _subscribedGrid = grid;
        _subscribedGrid.OnTileChanged += OnTileChanged;
    }

    private void OnTileChanged(GridCell cell) => MapVersion++;

    /// <summary>
    /// Whether the minion could now get at the enemy: a walkable path to it,
    /// or (for a ranged attack) already in range with a clear line of sight.
    /// </summary>
    public bool CanReachOrHit(MinionController self, MinionController enemy)
    {
        var grid = Grid;
        if (grid == null || self == null || enemy == null || self.Agent == null || enemy.Agent == null) return false;
        var from = self.Agent.CurrentCell;
        var to   = enemy.Agent.CurrentCell;
        if (from == null || to == null) return false;

        var profile = ProfileFor(self);
        float reach = profile.Range * CellSize + self.Agent.Radius + enemy.HandRadius;
        Vector3 d = enemy.transform.position - self.transform.position;
        if (d.x * d.x + d.z * d.z <= reach * reach && CanSee(from, to)) return true;

        if (_pathfinder == null || _subscribedGrid != grid) _pathfinder = new GridPathfinder(grid);
        return _pathfinder.FindPath(from, to, self.Agent.Capability, self.Faction, false, self.Agent.Radius) != null;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_subscribedGrid != null) _subscribedGrid.OnTileChanged -= OnTileChanged;
    }

    // ── Sides ──────────────────────────────────────────────────────────

    public static bool IsEnemy(FactionID a, FactionID b) =>
        a != b && a != FactionID.Unaligned && b != FactionID.Unaligned;

    // ── Sight ──────────────────────────────────────────────────────────

    /// <summary>True if nothing solid lies on the straight line between two cells.</summary>
    public bool CanSee(GridCell from, GridCell to)
    {
        var grid = Grid;
        if (grid == null || from == null || to == null) return false;

        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        // Walk the line; every cell strictly between the ends must be open.
        while (x0 != x1 || y0 != y1)
        {
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 <  dx) { err += dx; y0 += sy; }
            if (x0 == x1 && y0 == y1) break;
            if (!SeeThrough(grid.GetCell(x0, y0))) return false;
        }
        return true;
    }

    private bool SeeThrough(GridCell cell)
    {
        if (cell == null) return false;
        if (cell.TileType == TileType.Wall) return false;
        var def = Grid.GetDefinition(cell.TileType);
        return def != null && def.traversalType != TraversalType.Impassable;
    }

    /// <summary>The nearest enemy minion the given minion can see, skipping any it's ignoring.</summary>
    public MinionController FindVisibleEnemy(MinionController self, System.Func<MinionController, bool> ignore = null)
    {
        var from = self.Agent != null ? self.Agent.CurrentCell : null;
        if (from == null) return null;

        float range = sightRange * CellSize;
        MinionController best = null;
        float bestSq = range * range;
        foreach (var other in MinionController.All)
        {
            if (other == null || other == self || !other.IsAlive || other.IsHeld) continue;
            if (!IsEnemy(self.Faction, other.Faction)) continue;

            Vector3 d = other.transform.position - self.transform.position;
            float sq = d.x * d.x + d.z * d.z;
            if (sq > bestSq) continue;
            if (!CanSee(from, other.Agent != null ? other.Agent.CurrentCell : null)) continue;
            // Last, as it may search for a path (see MinionController.IsIgnoring).
            if (ignore != null && ignore(other)) continue;
            best = other; bestSq = sq;
        }
        return best;
    }

    /// <summary>The nearest enemy Dungeon Heart the minion can see, if any.</summary>
    public bool FindVisibleEnemyHeart(MinionController self, out FactionID heartFaction, out GridCell heartCell)
    {
        heartFaction = FactionID.Unaligned;
        heartCell    = null;
        var heart = DungeonHeart.Instance;
        var gm    = GameManager2D.Instance;
        var from  = self.Agent != null ? self.Agent.CurrentCell : null;
        if (heart == null || gm == null || from == null) return false;

        float range  = sightRange;
        float bestSq = range * range;
        foreach (var setup in gm.ActiveFactions)
        {
            var f = setup.factionId;
            if (!IsEnemy(self.Faction, f) || heart.CurrentHP(f) <= 0) continue;
            var centre = heart.CentreCell(f);
            if (centre == null) continue;

            float dx = centre.X - from.X, dy = centre.Y - from.Y;
            float sq = dx * dx + dy * dy;
            if (sq > bestSq || !CanSee(from, centre)) continue;
            heartFaction = f; heartCell = centre; bestSq = sq;
        }
        return heartCell != null;
    }

    // ── Attacks ────────────────────────────────────────────────────────

    /// <summary>The minion's attack: its lowest-level attack ability reached so far, else a default for its stance.</summary>
    public AttackProfile ProfileFor(MinionController minion)
    {
        var def = minion.Definition;
        if (def != null)
        {
            AbilityDefinition chosen = null;
            int chosenLevel = int.MaxValue;
            foreach (var unlock in def.abilities)
            {
                var a = unlock.ability;
                if (a == null || a.kind != AbilityDefinition.AbilityKind.Attack || !a.dealsDamage) continue;
                if (unlock.gainLevel > minion.Level || unlock.gainLevel >= chosenLevel) continue;
                chosen = a; chosenLevel = unlock.gainLevel;
            }
            if (chosen != null)
                return new AttackProfile
                {
                    Range         = Mathf.Max(0f, chosen.range),
                    Cooldown      = Mathf.Max(0.1f, chosen.cooldown),
                    BasePower     = chosen.basePower,
                    ScalingFactor = chosen.scalingFactor,
                    ScalingStat   = chosen.scalingStat,
                    DamageType    = chosen.damageType,
                    Delivery      = chosen.delivery,
                };
        }

        var stance = def != null ? def.stance : MinionDefinition.MinionStance.Worker;
        var profile = new AttackProfile
        {
            Range         = defaultMeleeRange,
            Cooldown      = defaultCooldown,
            BasePower     = defaultPower,
            ScalingFactor = defaultScaling,
            ScalingStat   = MinionStat.Strength,
            DamageType    = DamageType.Blunt,
            Delivery      = AbilityDefinition.AbilityDelivery.Melee,
        };
        if (stance == MinionDefinition.MinionStance.Ranger)
        {
            profile.Range      = defaultRangedRange;
            profile.DamageType = DamageType.Pierce;
            profile.Delivery   = AbilityDefinition.AbilityDelivery.Ranged;
        }
        else if (stance == MinionDefinition.MinionStance.Mage)
        {
            profile.Range       = defaultRangedRange;
            profile.DamageType  = DamageType.Fire;
            profile.ScalingStat = MinionStat.Magic;
            profile.Delivery    = AbilityDefinition.AbilityDelivery.Magic;
        }
        return profile;
    }

    /// <summary>One attack: rolls to hit, then deals damage. Returns true if it hit.</summary>
    public bool Attack(MinionController attacker, MinionController target, in AttackProfile profile)
    {
        if (attacker == null || target == null || !target.IsAlive) return false;

        float accuracy = attacker.GetStatOr(MinionStat.Accuracy, 0f);
        float dodge    = target.GetStatOr(MinionStat.Dexterity, 0f);
        float hit      = Mathf.Clamp(baseHitChance + (accuracy - dodge) * hitPerPoint, minHitChance, maxHitChance);
        if (Random01(attacker.Faction) >= hit) return false;

        var   defenceStat = profile.Delivery == AbilityDefinition.AbilityDelivery.Magic ? MinionStat.Resistance : MinionStat.Fortitude;
        float defence     = Mathf.Max(0f, target.GetStatOr(defenceStat, 0f));
        float multiplier  = target.Definition != null ? target.Definition.DamageTakenMultiplier(profile.DamageType) : 1f;

        float damage = (profile.BasePower + attacker.GetStatOr(profile.ScalingStat, 0f) * profile.ScalingFactor)
                     * multiplier / (1f + defence / defenceScale);
        target.TakeDamage(Mathf.Max(minDamage, damage), attacker);
        return true;
    }

    /// <summary>One attack on a Dungeon Heart — it can't dodge or resist.</summary>
    public void AttackHeart(MinionController attacker, FactionID heartFaction, in AttackProfile profile, Vector3 heartPosition)
    {
        var heart = DungeonHeart.Instance;
        if (heart == null) return;
        float damage = profile.BasePower + attacker.GetStatOr(profile.ScalingStat, 0f) * profile.ScalingFactor;
        heart.TakeDamage(heartFaction, Mathf.Max(1, Mathf.RoundToInt(Mathf.Max(minDamage, damage))));
        Announcer.Announce(heartFaction, "HeartUnderAttack", heartPosition);
    }

    // ── Experience & rallying ──────────────────────────────────────────

    /// <summary>Experience per minute for taking part in a fight.</summary>
    public float CombatXpPerMinute =>
        combatXpMultiplier * (RoomWorkManager.Instance != null ? RoomWorkManager.Instance.TrainExperiencePerMinute : 60f);

    /// <summary>The killing blow's bonus.</summary>
    public void AwardKill(MinionController killer, MinionController victim)
    {
        if (killer == null || victim == null || !killer.IsAlive) return;
        killer.AddExperience(killXpPerVictimLevel * Mathf.Max(1, victim.Level));
    }

    /// <summary>Calls the fighter's allies nearby into the fight — waking sleepers. Workers don't answer.</summary>
    public void Rally(MinionController fighter, MinionController enemy)
    {
        if (fighter == null || enemy == null) return;
        float radius = alertRadius * CellSize;
        foreach (var ally in MinionController.All)
        {
            if (ally == null || ally == fighter || !ally.IsAlive || ally.IsHeld || ally.InCombat) continue;
            if (ally.Faction != fighter.Faction || ally.IsWorkerKind) continue;
            Vector3 d = ally.transform.position - fighter.transform.position;
            if (d.x * d.x + d.z * d.z > radius * radius) continue;
            ally.EnterCombat(enemy);
        }
    }

    public float Random01(FactionID faction)
    {
        var tasks = WorkerTaskManager.GetForFaction(faction);
        return tasks != null ? tasks.NextRandom01() : Random.value;
    }
}
