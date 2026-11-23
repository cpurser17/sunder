using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Age-of-Empires-style match statistics: running totals per faction (kills,
/// minions lost, gold mined and spent, territory, creatures, spells — see
/// MissionStats), sampled every so often for the results screen's graphs.
/// Freezes when the mission ends; play can carry on, but nothing counts.
///
/// Anything can report with MatchStats.Add(faction, stat, amount).
///
/// Scene setup: none — GameManager2D adds one.
/// </summary>
public class MatchStats : MonoBehaviour
{
    public static MatchStats Instance { get; private set; }

    [Tooltip("Seconds of game time between graph samples.")]
    [SerializeField, Min(1f)] private float sampleInterval = 15f;

    /// <summary>The stats tracked over time, in the order the results screen shows them.</summary>
    public static readonly string[] Tracked =
    {
        MissionStats.Kills, MissionStats.MinionsLost, MissionStats.GoldMined, MissionStats.GoldSpent,
        MissionStats.TerritoryClaimed, MissionStats.CreaturesAttracted, MissionStats.CreaturesRecruited,
        MissionStats.SpellsCast,
    };

    public class Sample
    {
        public float Seconds;
        public readonly Dictionary<FactionID, Dictionary<string, float>> Values = new();
    }

    private readonly Dictionary<FactionID, Dictionary<string, float>> _totals = new();
    private readonly List<Sample> _samples = new();
    private float _startedAt;
    private float _endedAt = -1f;
    private float _nextSample;

    private GridManager2D _grid;
    private FactionID[]   _owners;   // last seen owner per cell, for territory claimed

    public bool Frozen => _endedAt >= 0f;
    public float Seconds => (Frozen ? _endedAt : Time.time) - _startedAt;
    public IReadOnlyList<Sample> Samples => _samples;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _startedAt = Time.time;

        _grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (_grid != null)
        {
            _grid.OnGridBuilt   += SnapshotOwners;
            _grid.OnTileChanged += OnTileChanged;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_grid != null)
        {
            _grid.OnGridBuilt   -= SnapshotOwners;
            _grid.OnTileChanged -= OnTileChanged;
        }
    }

    /// <summary>A new mission (or a loaded one): counting starts afresh from here.</summary>
    public void Begin(float secondsAlreadyPlayed = 0f)
    {
        _totals.Clear();
        _samples.Clear();
        _startedAt  = Time.time - Mathf.Max(0f, secondsAlreadyPlayed);
        _endedAt    = -1f;
        _nextSample = Time.time;
        SnapshotOwners();
    }

    // ── Reporting ──────────────────────────────────────────────────────

    public static void Add(FactionID faction, string stat, float amount = 1f)
    {
        var s = Instance;
        if (s == null || s.Frozen || amount == 0f || !FactionTeams.IsPlayable(faction)) return;
        var byStat = s.TotalsOf(faction);
        byStat[stat] = (byStat.TryGetValue(stat, out float v) ? v : 0f) + amount;
    }

    public float Get(FactionID faction, string stat) =>
        _totals.TryGetValue(faction, out var byStat) && byStat.TryGetValue(stat, out float v) ? v : 0f;

    private Dictionary<string, float> TotalsOf(FactionID faction)
    {
        if (!_totals.TryGetValue(faction, out var byStat)) _totals[faction] = byStat = new Dictionary<string, float>();
        return byStat;
    }

    /// <summary>The playable factions in this mission, in seat order.</summary>
    public List<FactionID> Factions()
    {
        var list = new List<FactionID>();
        var gm = GameManager2D.Instance;
        if (gm != null && gm.ActiveFactions != null)
            foreach (var setup in gm.ActiveFactions)
                if (setup != null && FactionTeams.IsPlayable(setup.factionId) && !list.Contains(setup.factionId))
                    list.Add(setup.factionId);
        foreach (var f in _totals.Keys) if (!list.Contains(f)) list.Add(f);
        return list;
    }

    // ── Sampling and freezing ──────────────────────────────────────────

    private void Update()
    {
        if (Frozen || Time.time < _nextSample) return;
        _nextSample = Time.time + sampleInterval;
        TakeSample();
    }

    private void TakeSample()
    {
        var sample = new Sample { Seconds = Seconds };
        foreach (var f in Factions())
        {
            var values = new Dictionary<string, float>();
            foreach (var stat in Tracked) values[stat] = Get(f, stat);
            sample.Values[f] = values;
        }
        _samples.Add(sample);
    }

    /// <summary>The mission's over: a last sample, the hearts' health, then nothing more counts.</summary>
    public void Freeze()
    {
        if (Frozen) return;
        TakeSample();
        var hearts = DungeonHeart.Instance != null ? DungeonHeart.Instance.SnapshotHP() : null;
        if (hearts != null)
            foreach (var pair in hearts)
                if (FactionTeams.IsPlayable(pair.Key)) TotalsOf(pair.Key)[MissionStats.HeartHealthLeft] = pair.Value;
        _endedAt = Time.time;
    }

    // ── Territory ──────────────────────────────────────────────────────

    private void SnapshotOwners()
    {
        if (_grid == null || _grid.Width <= 0) { _owners = null; return; }
        _owners = new FactionID[_grid.Width * _grid.Height];
        for (int y = 0; y < _grid.Height; y++)
        for (int x = 0; x < _grid.Width; x++)
            _owners[y * _grid.Width + x] = _grid.GetCell(x, y).Owner;
    }

    /// <summary>A tile changing hands to a team counts as territory claimed by that team.</summary>
    private void OnTileChanged(GridCell cell)
    {
        if (_owners == null || _grid.IsBuilding) return;   // a level or save loading isn't anyone claiming
        int i = cell.Y * _grid.Width + cell.X;
        if (i < 0 || i >= _owners.Length) return;
        var before = _owners[i];
        _owners[i] = cell.Owner;
        if (cell.Owner != before && cell.Owner != FactionID.Unaligned) Add(cell.Owner, MissionStats.TerritoryClaimed);
    }

    // ── Result ─────────────────────────────────────────────────────────

    /// <summary>A faction's totals as a mission result's stats, with the time played.</summary>
    public Dictionary<string, float> StatsFor(FactionID faction)
    {
        var stats = new Dictionary<string, float>(System.StringComparer.OrdinalIgnoreCase);
        if (_totals.TryGetValue(faction, out var byStat))
            foreach (var pair in byStat) stats[pair.Key] = pair.Value;
        stats[MissionStats.Seconds] = Seconds;
        return stats;
    }
}
