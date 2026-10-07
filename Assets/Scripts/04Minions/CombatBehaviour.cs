using UnityEngine;

/// <summary>
/// Fighting — the highest priority there is. MinionController starts it the
/// moment a minion sees an enemy, is hit, or an ally nearby calls it in,
/// pausing whatever it was doing (work, sleep, a meal, wages); when no enemy
/// is left in sight, it picks that up again where it left off.
///
///   Fight   close to within reach of the target (or, for a ranged attack,
///           just get it in sight and range — across lava if need be) and
///           attack on cooldown (CombatSystem rules the hits). Kill it, then
///           take the next enemy in sight. Creatures with no enemy minion in
///           sight attack an enemy Dungeon Heart they can see.
///   Flee    workers: run for their own heart, away from the enemy. Caught
///           (within CombatSystem.CorneredRadius) they turn and fight. Dropped
///           beside an enemy by the hand, they fight straight away.
///
/// A target that can neither be reached nor hit for CombatSystem.GiveUpSeconds
/// is ignored for a while. Minions don't retreat when hurt — the player can
/// pick them up.
/// </summary>
public class CombatBehaviour : MinionBehaviour
{
    private enum Mode { Fight, Flee }

    private Mode             _mode;
    private MinionController _target;
    private bool             _targetIsHeart;
    private FactionID        _heartFaction;
    private GridCell         _heartCell;
    private AttackProfile    _profile;
    private float            _nextAttack;
    private float            _nextRepath;
    private float            _stuckSince = -1f;
    private float            _lastSawEnemy;

    public bool IsFleeing => _mode == Mode.Flee;
    public MinionController Target => _target;

    // ── Starting and steering ──────────────────────────────────────────

    /// <summary>Takes on an enemy minion. Workers flee unless told to fight.</summary>
    public void Begin(MinionController enemy, bool fight)
    {
        _mode          = fight ? Mode.Fight : Mode.Flee;
        _target        = enemy;
        _targetIsHeart = false;
        Reset();
    }

    /// <summary>Goes for an enemy Dungeon Heart.</summary>
    public void BeginHeart(FactionID heartFaction, GridCell heartCell)
    {
        _mode          = Mode.Fight;
        _target        = null;
        _targetIsHeart = true;
        _heartFaction  = heartFaction;
        _heartCell     = heartCell;
        Reset();
    }

    /// <summary>
    /// Already fighting and another enemy turns up (or hits it): a minion
    /// beats a heart, and a worker told to fight stops fleeing.
    /// </summary>
    public void Engage(MinionController enemy, bool fight)
    {
        if (fight) _mode = Mode.Fight;
        if (enemy == null) return;
        if (_targetIsHeart || !Valid(_target)) { _target = enemy; _targetIsHeart = false; _stuckSince = -1f; }
    }

    private void Reset()
    {
        _profile      = CombatSystem.Instance != null ? CombatSystem.Instance.ProfileFor(Minion) : default;
        _nextAttack   = Time.time;
        _nextRepath   = 0f;
        _stuckSince   = -1f;
        _lastSawEnemy = Time.time;
    }

    public override void Pause()  => Agent.Stop();
    public override void Resume() { _nextRepath = 0f; _stuckSince = -1f; }

    public override void Deactivate()
    {
        Agent.Stop();
        _target = null;
        _targetIsHeart = false;
    }

    // ── Each frame ─────────────────────────────────────────────────────

    private void Update()
    {
        var combat = CombatSystem.Instance;
        if (combat == null) { Minion.EndCombat(); return; }

        if (!_targetIsHeart && !Valid(_target) && !Retarget(combat)) { Minion.EndCombat(); return; }
        if (_targetIsHeart && !HeartAlive() && !Retarget(combat))     { Minion.EndCombat(); return; }

        if (_mode == Mode.Flee) Flee(combat);
        else                    Fight(combat);
    }

    private bool Valid(MinionController m) =>
        m != null && m.IsAlive && !m.IsHeld && CombatSystem.IsEnemy(Minion.Faction, m.Faction) && WithinLeash(m.transform.position);

    private bool WithinLeash(Vector3 position)
    {
        var combat = CombatSystem.Instance;
        float leash = combat.SightRange * combat.LeashMultiplier * combat.CellSize;
        Vector3 d = position - transform.position;
        return d.x * d.x + d.z * d.z <= leash * leash;
    }

    private bool HeartAlive() =>
        DungeonHeart.Instance != null && DungeonHeart.Instance.CurrentHP(_heartFaction) > 0;

    /// <summary>The next enemy in sight (or, for a creature, an enemy heart). False if there's none.</summary>
    private bool Retarget(CombatSystem combat)
    {
        var next = combat.FindVisibleEnemy(Minion, Minion.IsIgnoring);
        if (next != null)
        {
            _target = next; _targetIsHeart = false; _stuckSince = -1f;
            return true;
        }
        if (_mode == Mode.Fight && !Minion.IsWorkerKind &&
            combat.FindVisibleEnemyHeart(Minion, out var f, out var cell))
        {
            _targetIsHeart = true; _heartFaction = f; _heartCell = cell; _target = null; _stuckSince = -1f;
            return true;
        }
        return false;
    }

    // ── Fighting ───────────────────────────────────────────────────────

    private void Fight(CombatSystem combat)
    {
        Minion.AddExperience(combat.CombatXpPerMinute * Time.deltaTime / 60f);

        Vector3 targetPos = _targetIsHeart
            ? Minion.Grid.CellToWorld(_heartCell.X, _heartCell.Y)
            : _target.transform.position;
        GridCell targetCell = _targetIsHeart ? _heartCell : _target.Agent != null ? _target.Agent.CurrentCell : null;

        float targetRadius = _targetIsHeart ? 1.5f * combat.CellSize : _target.HandRadius;
        float reach = _profile.Range * combat.CellSize + Agent.Radius + targetRadius;
        Vector3 d = targetPos - transform.position;
        bool inRange = d.x * d.x + d.z * d.z <= reach * reach;
        bool inSight = combat.CanSee(Agent.CurrentCell, targetCell);

        if (inRange && inSight)
        {
            Agent.Stop();
            _stuckSince = -1f;
            if (Time.time < _nextAttack) return;
            _nextAttack = Time.time + _profile.Cooldown;

            if (_targetIsHeart) combat.AttackHeart(Minion, _heartFaction, _profile, targetPos);
            else                combat.Attack(Minion, _target, _profile);
            return;
        }

        if (Time.time < _nextRepath) { CheckStuck(combat); return; }
        _nextRepath = Time.time + 0.5f;

        bool moving = _targetIsHeart
            ? MoveToHeart()
            : Agent.SetDestination(targetPos);
        if (moving) _stuckSince = -1f;
        CheckStuck(combat);
    }

    private bool MoveToHeart()
    {
        var heart = DungeonHeart.Instance;
        var approach = heart != null
            ? heart.FindApproachCell(_heartFaction, Agent.CurrentCell, Agent.Capability, Agent.Radius)
            : null;
        return approach != null && Agent.SetDestination(approach);
    }

    /// <summary>Can't reach it and can't hit it: after a while, ignore it and look elsewhere.</summary>
    private void CheckStuck(CombatSystem combat)
    {
        if (!Agent.HasArrived) { _stuckSince = -1f; return; }
        if (_stuckSince < 0f) { _stuckSince = Time.time; return; }
        if (Time.time - _stuckSince < combat.GiveUpSeconds) return;

        if (!_targetIsHeart) Minion.Ignore(_target, 10f);
        _target = null;
        _targetIsHeart = false;
        _stuckSince = -1f;
    }

    // ── Fleeing (workers) ──────────────────────────────────────────────

    private void Flee(CombatSystem combat)
    {
        Vector3 away = transform.position - _target.transform.position;
        away.y = 0f;
        float caught = combat.CorneredRadius * combat.CellSize + Agent.Radius + _target.HandRadius;
        if (away.sqrMagnitude <= caught * caught) { _mode = Mode.Fight; return; }   // cornered: fight

        // Lost sight of it for a few seconds: safe enough.
        if (combat.CanSee(Agent.CurrentCell, _target.Agent != null ? _target.Agent.CurrentCell : null))
            _lastSawEnemy = Time.time;
        else if (Time.time - _lastSawEnemy > 3f) { Minion.EndCombat(); return; }

        if (Time.time < _nextRepath) return;
        _nextRepath = Time.time + 0.5f;

        // Home to the heart if that's away from the enemy; otherwise just away.
        var heart = DungeonHeart.Instance;
        var home  = heart != null && heart.IsReady(Minion.Faction)
            ? heart.FindApproachCell(Minion.Faction, Agent.CurrentCell, Agent.Capability, Agent.Radius)
            : null;
        if (home != null)
        {
            Vector3 toHome = Minion.Grid.CellToWorld(home.X, home.Y) - transform.position;
            if (Vector3.Dot(toHome, away) > 0f && Agent.SetDestination(home)) return;
        }

        Vector3 spot = transform.position + away.normalized * 4f * combat.CellSize;
        if (!Agent.SetDestination(spot)) _mode = Mode.Fight;   // nowhere to run: cornered
    }
}
