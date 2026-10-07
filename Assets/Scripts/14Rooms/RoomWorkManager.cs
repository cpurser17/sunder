using System.Collections.Generic;
using UnityEngine;

/// <summary>The kinds of room work a creature can choose (its "default jobs").</summary>
public enum RoomWork { None, Train, Research, Manufacture, Pray }

/// <summary>
/// Room work: which room each kind of work happens in, who is working
/// where, and what a minute of it produces.
///
///   Train        Training Room   experience, at a gold cost per minute that
///                                rises with level; stops at max level or
///                                when the gold runs out
///   Research     Library         research points (FactionResearchState)
///   Manufacture  Workshop        manufacture points
///   Pray         Shrine          prayer points
///
/// Output scales with the room's efficiency and the minion's matching skill
/// stat (SkillTrain, SkillResearch, SkillBuild, SkillPray; 1 if not in the
/// data). Each tile is one work spot, held by one minion at a time.
///
/// Which work a minion picks — preferences from MinionData, the room it was
/// dropped on — is CreatureBehaviour's call; this only answers questions
/// and keeps the books.
///
/// Scene setup: none — GameManager2D adds one. Add it yourself to tune rates.
/// </summary>
public class RoomWorkManager : MonoBehaviour
{
    public static RoomWorkManager Instance { get; private set; }

    [Header("Training")]
    [Tooltip("Experience per minute of training, at efficiency 1 and SkillTrain 1.")]
    [SerializeField, Min(0f)] private float trainExperiencePerMinute = 60f;
    [Tooltip("Gold per minute of training, per level of the trainee.")]
    [SerializeField, Min(0f)] private float trainGoldPerMinutePerLevel = 10f;

    [Header("Other work (points per minute at efficiency 1, skill 1)")]
    [SerializeField, Min(0f)] private float researchPerMinute    = 10f;
    [SerializeField, Min(0f)] private float manufacturePerMinute = 10f;
    [SerializeField, Min(0f)] private float prayerPerMinute      = 10f;

    [Header("Sessions")]
    [Tooltip("Seconds a minion works before reconsidering what to do (random between these).")]
    [SerializeField] private Vector2 sessionSeconds = new(60f, 120f);

    private readonly Dictionary<GridCell, MinionController> _spots   = new();
    private readonly Dictionary<MinionController, GridCell> _byMinion = new();
    private readonly Dictionary<MinionController, float>    _goldDue  = new();

    public Vector2 SessionSeconds => sessionSeconds;
    public float   TrainExperiencePerMinute => trainExperiencePerMinute;

    private GridManager2D Grid => GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Mapping ────────────────────────────────────────────────────────

    public static readonly RoomWork[] AllWork = { RoomWork.Train, RoomWork.Research, RoomWork.Manufacture, RoomWork.Pray };

    public static TileType RoomFor(RoomWork work) => work switch
    {
        RoomWork.Train       => TileType.TrainingRoom,
        RoomWork.Research    => TileType.Library,
        RoomWork.Manufacture => TileType.Workshop,
        RoomWork.Pray        => TileType.Shrine,
        _                    => TileType.Stone,
    };

    public static RoomWork WorkFor(TileType room) => room switch
    {
        TileType.TrainingRoom => RoomWork.Train,
        TileType.Library      => RoomWork.Research,
        TileType.Workshop     => RoomWork.Manufacture,
        TileType.Shrine       => RoomWork.Pray,
        _                     => RoomWork.None,
    };

    /// <summary>How much this minion likes the work, from MinionData (0 = won't do it).</summary>
    public static float Preference(MinionController minion, RoomWork work)
    {
        var def = minion != null ? minion.Definition : null;
        if (def == null) return 0f;
        return work switch
        {
            RoomWork.Train       => def.trainPreference,
            RoomWork.Research    => def.researchPreference,
            RoomWork.Manufacture => def.buildPreference,
            RoomWork.Pray        => def.prayPreference,
            _                    => 0f,
        };
    }

    // ── Spots ──────────────────────────────────────────────────────────

    /// <summary>A tile of this faction's room for the work, with nobody working on it.</summary>
    public bool IsFreeSpot(GridCell cell, FactionID faction, RoomWork work)
    {
        if (cell == null || work == RoomWork.None) return false;
        if (cell.TileType != RoomFor(work) || cell.Owner != faction) return false;
        return !_spots.TryGetValue(cell, out var holder) || holder == null;
    }

    /// <summary>The best room efficiency of this faction's rooms for the work, or 0 if it has none.</summary>
    public float BestEfficiency(FactionID faction, RoomWork work)
    {
        var grid = Grid;
        if (grid == null) return 0f;
        float best = 0f;
        var type = RoomFor(work);
        foreach (var room in grid.Rooms)
            if (room.TileType == type && room.Owner == faction) best = Mathf.Max(best, Mathf.Max(0.01f, room.Efficiency));
        return best;
    }

    public bool Claim(MinionController minion, GridCell cell, RoomWork work)
    {
        if (minion == null || !IsFreeSpot(cell, minion.Faction, work)) return false;
        Release(minion);
        _spots[cell]      = minion;
        _byMinion[minion] = cell;
        return true;
    }

    public void Release(MinionController minion)
    {
        if (minion == null || !_byMinion.TryGetValue(minion, out var cell)) return;
        _byMinion.Remove(minion);
        if (_spots.TryGetValue(cell, out var holder) && holder == minion) _spots.Remove(cell);
    }

    /// <summary>True while the minion still holds this spot and it's still the right room of its faction.</summary>
    public bool StillHolds(MinionController minion, GridCell cell, RoomWork work) =>
        cell != null && _spots.TryGetValue(cell, out var holder) && holder == minion &&
        cell.TileType == RoomFor(work) && cell.Owner == minion.Faction;

    // ── Working ────────────────────────────────────────────────────────

    /// <summary>
    /// One frame of work at a spot. Returns false when the minion can't
    /// carry on with this work (fully trained, or the gold has run out).
    /// </summary>
    public bool Work(MinionController minion, GridCell cell, RoomWork work, float deltaTime)
    {
        float minutes = deltaTime / 60f;
        float rate    = Efficiency(cell) * minion.WorkSpeedMultiplier;
        var   gm      = GameManager2D.Instance;
        var   state   = gm != null ? gm.GetResearch(minion.Faction) : null;

        switch (work)
        {
            case RoomWork.Train:
                if (minion.Definition == null || minion.Level >= minion.Definition.maxLevel) return false;
                if (!PayForTraining(minion, minutes)) return false;
                minion.AddExperience(trainExperiencePerMinute * minutes * rate * Skill(minion, MinionStat.SkillTrain));
                return true;

            case RoomWork.Research:
                state?.AddResearchPoints(researchPerMinute * minutes * rate * Skill(minion, MinionStat.SkillResearch));
                return true;

            case RoomWork.Manufacture:
                state?.AddManufacturePoints(manufacturePerMinute * minutes * rate * Skill(minion, MinionStat.SkillBuild));
                return true;

            case RoomWork.Pray:
                state?.AddPrayerPoints(prayerPerMinute * minutes * rate * Skill(minion, MinionStat.SkillPray));
                return true;
        }
        return false;
    }

    /// <summary>Training is paid as it goes, in whole gold pieces. False once the faction can't pay.</summary>
    private bool PayForTraining(MinionController minion, float minutes)
    {
        float due = (_goldDue.TryGetValue(minion, out float d) ? d : 0f)
                  + trainGoldPerMinutePerLevel * minion.Level * minutes;
        int whole = Mathf.FloorToInt(due);
        if (whole > 0)
        {
            var wallet = GameManager2D.Instance != null ? GameManager2D.Instance.GetWallet(minion.Faction) : null;
            if (wallet == null || !wallet.TrySpend(whole))
            {
                Announcer.Announce(minion.Faction, "NoGoldForTraining");
                return false;
            }
            due -= whole;
        }
        _goldDue[minion] = due;
        return true;
    }

    private static float Skill(MinionController minion, MinionStat stat) =>
        Mathf.Max(0f, minion.GetStatOr(stat, 1f));

    private float Efficiency(GridCell cell)
    {
        var grid = Grid;
        var room = cell != null && cell.RoomId >= 0 && grid != null ? grid.GetRoomForCell(cell) : null;
        return room != null && room.TileType == cell.TileType ? Mathf.Max(0f, room.Efficiency) : 1f;
    }
}
