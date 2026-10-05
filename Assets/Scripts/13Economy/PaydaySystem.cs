using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Payday. Each faction has its own payday clock: every
/// interval ± variance seconds of gameplay, every minion of that faction is
/// owed its Salary (on top of anything still owed) and goes to collect it
/// (MinionController.OnPayday, WageBehaviour).
///
/// The first payday falls somewhere between firstPaydayMin and
/// firstPaydayMax, so factions start out of step with each other; the
/// variance keeps them from drifting back into line. Each faction's draws
/// come from its own random stream derived from the match seed
/// (GameManager2D.DeriveFactionSeed), so a given match always pays out at
/// the same moments.
///
/// Times are gameplay seconds — they stop while the game is paused.
/// Payday clocks aren't saved yet: loading a save starts them afresh.
///
/// Scene setup: none — GameManager2D adds one if the scene has none. Add it
/// yourself to tune the numbers.
/// </summary>
public class PaydaySystem : MonoBehaviour
{
    public static PaydaySystem Instance { get; private set; }

    /// <summary>Raised when a faction's payday comes round.</summary>
    public static event Action<FactionID> OnPayday;

    [Header("Interval (seconds of gameplay)")]
    [Tooltip("Average time between paydays. 360 = 6 minutes.")]
    [SerializeField, Min(1f)] private float interval = 360f;
    [Tooltip("Each interval is shifted by a random amount up to this, either way.")]
    [SerializeField, Min(0f)] private float variance = 30f;
    [Tooltip("The first payday falls at a random time between these two.")]
    [SerializeField, Min(0f)] private float firstPaydayMin = 240f;
    [SerializeField, Min(0f)] private float firstPaydayMax = 480f;

    [Header("Unpaid wages")]
    [Tooltip("Lasting anger (0-1 scale) for each payday in a row a minion isn't paid in full.")]
    [SerializeField, Range(0f, 1f)] private float angerPerMissedPayday = 0.25f;

    private class Schedule
    {
        public float         NextAt;
        public System.Random Random;
    }

    private readonly Dictionary<FactionID, Schedule> _schedules = new();
    private float _clock;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnEnable()  => GameManager2D.OnWalletsReady += StartClocks;
    private void OnDisable() => GameManager2D.OnWalletsReady -= StartClocks;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (_schedules.Count == 0) return;
        _clock += Time.deltaTime;

        foreach (var pair in _schedules)
        {
            var schedule = pair.Value;
            if (_clock < schedule.NextAt) continue;

            schedule.NextAt += Mathf.Max(1f, interval + Between(schedule.Random, -variance, variance));
            Pay(pair.Key);
        }
    }

    // ── Clocks ─────────────────────────────────────────────────────────

    /// <summary>Starts every active faction's clock afresh. Runs whenever a level or save is loaded.</summary>
    private void StartClocks()
    {
        _schedules.Clear();
        _clock = 0f;

        var gm = GameManager2D.Instance;
        if (gm == null) return;

        var factions = new List<FactionID>();
        foreach (var setup in gm.ActiveFactions) factions.Add(setup.factionId);
        if (factions.Count == 0) factions.Add(FactionID.Player);

        foreach (var faction in factions)
        {
            if (_schedules.ContainsKey(faction)) continue;
            var random = new System.Random(gm.DeriveFactionSeed(faction, "payday"));
            float min  = Mathf.Min(firstPaydayMin, firstPaydayMax);
            float max  = Mathf.Max(firstPaydayMin, firstPaydayMax);
            _schedules[faction] = new Schedule { Random = random, NextAt = Between(random, min, max) };
        }
    }

    /// <summary>Gameplay seconds until the faction's next payday, or -1 if it has none.</summary>
    public float SecondsUntilPayday(FactionID faction) =>
        _schedules.TryGetValue(faction, out var s) ? Mathf.Max(0f, s.NextAt - _clock) : -1f;

    // ── Paying ─────────────────────────────────────────────────────────

    private void Pay(FactionID faction)
    {
        int minions = 0, wages = 0;
        foreach (var minion in new List<MinionController>(MinionController.All))
        {
            if (minion == null || !minion.IsAlive || minion.Faction != faction || minion.Salary <= 0) continue;
            minions++;
            wages += minion.Salary;
            minion.OnPayday(angerPerMissedPayday);
        }

        Debug.Log($"[Payday] {faction}: {minions} minion(s) due {wages} gold in wages.");
        OnPayday?.Invoke(faction);
    }

    private static float Between(System.Random random, float min, float max) =>
        min + (float)random.NextDouble() * (max - min);
}
