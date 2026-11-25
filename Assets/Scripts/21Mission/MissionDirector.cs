using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Runs a mission's objectives for the local player. Every win condition the
/// level lists (WinConditions.Of) must be met to win; losing your own
/// Dungeon Heart is a loss. Either way the mission ends once: the stats
/// freeze, the result is kept, and the results overlay opens (paused). The
/// player can minimise it and carry on playing — the dungeon stays
/// playable, but the mission is over and its result fixed. Finish leaves for
/// wherever the mission was started from (MissionOrigin), handing the result
/// back through MissionReturn.
///
/// Scene setup: none — GameManager2D adds one and calls Begin.
/// </summary>
public class MissionDirector : MonoBehaviour
{
    public static MissionDirector Instance { get; private set; }

    [SerializeField] private FactionID localPlayer = FactionID.Player;
    [Tooltip("Seconds between objective checks.")]
    [SerializeField, Min(0.1f)] private float checkInterval = 0.5f;

    public enum Outcome { Playing, Won, Lost }

    public Outcome State { get; private set; } = Outcome.Playing;
    public IReadOnlyList<WinCondition> Conditions => _conditions;
    public MissionResult Result { get; private set; }
    public MissionOrigin Origin => _origin;
    public string MissionName { get; private set; }

    private readonly List<WinCondition> _conditions = new();
    private readonly List<MinionController> _lordsSeen = new();
    private bool   _enemyHeartSeen;
    private bool   _begun;
    private float  _nextCheck;
    private MissionOrigin _origin;
    private string _campaignId, _missionId, _levelId;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        DungeonHeart.OnFactionEliminated += OnFactionEliminated;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        DungeonHeart.OnFactionEliminated -= OnFactionEliminated;
    }

    /// <summary>The level is loaded: these are its objectives, and this is where it came from.</summary>
    public void Begin(LevelData level, MissionOrigin origin, string campaignId, string missionId)
    {
        _conditions.Clear();
        _conditions.AddRange(WinConditions.Of(level));
        _lordsSeen.Clear();
        _enemyHeartSeen = false;
        _origin     = origin;
        _campaignId = campaignId;
        _missionId  = missionId;
        _levelId    = level != null ? level.levelId : null;
        MissionName = NameOf(level);
        State       = Outcome.Playing;
        Result      = null;
        _begun      = true;
        _nextCheck  = Time.time + 1f;   // let the level settle (hearts registered, minions spawned)
    }

    private string NameOf(LevelData level)
    {
        var cat = CampaignCatalogue.Load();
        var mission = cat?.Find(_campaignId)?.Find(_missionId);
        if (mission != null) return mission.NameFor("en", cat.defaultLanguage);
        return level != null && !string.IsNullOrWhiteSpace(level.displayName) ? level.displayName : _levelId ?? "Mission";
    }

    // ── Objectives ─────────────────────────────────────────────────────

    private void Update()
    {
        if (!_begun || State != Outcome.Playing || Time.time < _nextCheck) return;
        _nextCheck = Time.time + checkInterval;

        TrackLords();
        if (_conditions.Count > 0 && _conditions.TrueForAll(IsMet)) End(Outcome.Won);
    }

    /// <summary>Whether a win condition is met right now.</summary>
    public bool IsMet(WinCondition condition) => condition switch
    {
        WinCondition.DestroyEnemyHearts => NoEnemyHeartStands(),
        WinCondition.DefeatLord         => LordDefeated(),
        _                               => false,
    };

    /// <summary>
    /// No enemy heart stands — once there has been one. A level with no enemy
    /// hearts can't be won this way (the level editor warns about that).
    /// </summary>
    private bool NoEnemyHeartStands()
    {
        var hearts = DungeonHeart.Instance;
        if (hearts == null) return false;
        foreach (var heart in hearts.StandingHearts())
            if (CombatSystem.IsEnemy(localPlayer, heart.Faction)) { _enemyHeartSeen = true; return false; }
        if (!_enemyHeartSeen && !_warnedNoEnemyHeart)
        {
            _warnedNoEnemyHeart = true;
            Debug.LogWarning("[MissionDirector] This level has no enemy Dungeon Heart, so 'destroy every enemy heart' can't be met.");
        }
        return _enemyHeartSeen;
    }

    private bool _warnedNoEnemyHeart;

    /// <summary>Remembers every lord that has appeared — he may arrive part-way through.</summary>
    private void TrackLords()
    {
        foreach (var m in MinionController.All)
            if (m != null && m.IsLord && !_lordsSeen.Contains(m)) _lordsSeen.Add(m);
    }

    /// <summary>Every lord that appeared is dead or has joined you. Not met before any has appeared.</summary>
    private bool LordDefeated()
    {
        TrackLords();
        if (_lordsSeen.Count == 0) return false;
        foreach (var lord in _lordsSeen)
            if (lord != null && lord.IsAlive && lord.Faction != localPlayer) return false;
        // TODO (torture): a lord converted to the player's team counts — ConvertTo already makes Faction == localPlayer.
        return true;
    }

    private void OnFactionEliminated(FactionID faction)
    {
        if (_begun && State == Outcome.Playing && faction == localPlayer) End(Outcome.Lost);
    }

    // ── Ending ─────────────────────────────────────────────────────────

    private void End(Outcome outcome)
    {
        if (State != Outcome.Playing) return;
        State = outcome;

        var stats = MatchStats.Instance;
        stats?.Freeze();
        Result = new MissionResult
        {
            missionId = _missionId,
            levelId   = _levelId,
            won       = outcome == Outcome.Won,
        };
        if (stats != null) Result.stats = stats.StatsFor(localPlayer);

        Debug.Log($"[MissionDirector] Mission {(Result.won ? "won" : "lost")}: {MissionName}.");
        ResultsOverlay.Show(this);
    }

    /// <summary>Leaves the Gameplay scene for wherever the mission was started from, with its result.</summary>
    public void Finish()
    {
        MissionReturn.Origin     = _origin;
        MissionReturn.CampaignId = _campaignId;
        MissionReturn.Result     = Result;
        MissionReturn.LevelId    = _levelId;

        var pause = FindAnyObjectByType<PauseController>();
        if (pause != null) pause.SetPaused(false);
        Time.timeScale = 1f;

        string scene = _origin switch
        {
            MissionOrigin.Campaign    => SceneNames.Campaign,
            MissionOrigin.LevelEditor => SceneNames.LevelEditor,
            MissionOrigin.Skirmish    => Application.CanStreamedLevelBeLoaded(SceneNames.Skirmish) ? SceneNames.Skirmish : SceneNames.Start,
            _                         => SceneNames.Start,
        };
        Debug.Log($"[MissionDirector] Finish: back to {scene} (started from {_origin}" +
                  (_origin == MissionOrigin.Campaign ? $", {_campaignId} {_missionId}" : "") +
                  $", {(Result != null && Result.won ? "won" : "lost")}).");
        if (!Application.CanStreamedLevelBeLoaded(scene))
        {
            Debug.LogWarning($"[MissionDirector] Scene '{scene}' isn't in Build Settings — staying here.");
            return;
        }
        SceneManager.LoadScene(scene);
    }

    /// <summary>The local player's team colour and name, and everyone else's, for the results screen.</summary>
    public static (string name, Color colour) Describe(FactionID faction)
    {
        var gm = GameManager2D.Instance;
        if (gm != null && gm.ActiveFactions != null)
            foreach (var setup in gm.ActiveFactions)
                if (setup != null && setup.factionId == faction)
                    return (string.IsNullOrWhiteSpace(setup.displayName) ? FactionTeams.DisplayName(faction) : setup.displayName,
                            UiKit.FromHex(setup.colourHex, Color.white));
        return (FactionTeams.DisplayName(faction), Color.white);
    }
}
