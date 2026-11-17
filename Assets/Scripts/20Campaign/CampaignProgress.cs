using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// How one attempt at a mission went, handed to the campaign when it ends.
/// Stats are free-form names (see MissionStats) so new ones need no change
/// here; an unlock rule can then ask about any of them.
/// </summary>
[Serializable]
public class MissionResult
{
    public string missionId;
    public bool   won;
    /// <summary>Stat name → value for this attempt, e.g. Kills = 42, MinionsLost = 3.</summary>
    public Dictionary<string, float> stats = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Items found during the attempt (kept only if it was won).</summary>
    public List<string> collected = new();
}

/// <summary>Stat names the game records in a MissionResult. The importer warns about any other name a rule uses.</summary>
public static class MissionStats
{
    public const string Kills       = "Kills";
    public const string MinionsLost = "MinionsLost";
    public const string GoldMined   = "GoldMined";
    public const string Seconds     = "Seconds";

    public static readonly string[] Known = { Kills, MinionsLost, GoldMined, Seconds };

    public static bool IsKnown(string stat) =>
        Array.Exists(Known, s => string.Equals(s, stat, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Where a mission stands for the player.</summary>
public enum MissionState { Locked, Available, Completed }

/// <summary>
/// One campaign playthrough's progress — one per save slot: which missions
/// are won, each mission's best results, items collected, and running
/// totals. Every mission can be replayed; each attempt updates its bests
/// and adds to the totals. Saved as campaign_progress.json in the slot's
/// folder.
///
/// Answers unlock rules (IUnlockState), asking CampaignProfile about
/// anything across playthroughs.
/// </summary>
[Serializable]
public class CampaignProgress : IUnlockState
{
    [Serializable]
    public class StatRange
    {
        public float lowest, highest;
    }

    public string campaignId;
    public List<string> completed = new();
    /// <summary>Attempts per mission, won or lost.</summary>
    public Dictionary<string, int> attempts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Mission → stat → the lowest and highest any attempt recorded.</summary>
    public Dictionary<string, Dictionary<string, StatRange>> best = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Mission → items collected there (in won attempts).</summary>
    public Dictionary<string, List<string>> collected = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Stat → summed over every attempt this campaign.</summary>
    public Dictionary<string, float> totals = new(StringComparer.OrdinalIgnoreCase);

    public CampaignProgress() { }
    public CampaignProgress(string campaignId) => this.campaignId = campaignId;

    // ── Recording ──────────────────────────────────────────────────────

    /// <summary>Adds an attempt's result: a win completes the mission; stats update its bests and the totals.</summary>
    public void Record(MissionResult result)
    {
        if (result == null || string.IsNullOrEmpty(result.missionId)) return;
        string id = result.missionId;

        attempts[id] = attempts.TryGetValue(id, out int n) ? n + 1 : 1;
        if (result.won && !IsComplete(id)) completed.Add(id);

        if (result.stats != null)
        {
            if (!best.TryGetValue(id, out var byStat))
                best[id] = byStat = new Dictionary<string, StatRange>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in result.stats)
            {
                if (byStat.TryGetValue(pair.Key, out var range))
                {
                    range.lowest  = Mathf.Min(range.lowest,  pair.Value);
                    range.highest = Mathf.Max(range.highest, pair.Value);
                }
                else byStat[pair.Key] = new StatRange { lowest = pair.Value, highest = pair.Value };

                totals[pair.Key] = (totals.TryGetValue(pair.Key, out float t) ? t : 0f) + pair.Value;
            }
        }

        if (result.won && result.collected != null && result.collected.Count > 0)
        {
            if (!collected.TryGetValue(id, out var items)) collected[id] = items = new List<string>();
            foreach (var item in result.collected)
                if (!items.Exists(i => string.Equals(i, item, StringComparison.OrdinalIgnoreCase))) items.Add(item);
        }
    }

    // ── Unlocking ──────────────────────────────────────────────────────

    /// <summary>Locked, Available or Completed, by the mission's unlock rule.</summary>
    public MissionState StateOf(CampaignCatalogue.Mission mission)
    {
        if (mission == null) return MissionState.Locked;
        if (IsComplete(mission.id)) return MissionState.Completed;
        return UnlockRule.Parse(mission.requires).IsMet(this) ? MissionState.Available : MissionState.Locked;
    }

    public bool IsComplete(string missionId) =>
        completed.Exists(m => string.Equals(m, missionId, StringComparison.OrdinalIgnoreCase));

    public bool HasCollected(string missionId, string itemId) =>
        collected.TryGetValue(missionId, out var items) &&
        items.Exists(i => string.Equals(i, itemId, StringComparison.OrdinalIgnoreCase));

    public bool TryBest(string missionId, string stat, bool lowest, out float value)
    {
        value = 0f;
        if (!best.TryGetValue(missionId, out var byStat) || !byStat.TryGetValue(stat, out var range)) return false;
        value = lowest ? range.lowest : range.highest;
        return true;
    }

    public float Total(string stat) => totals.TryGetValue(stat, out float t) ? t : 0f;

    public bool IsCampaignComplete(string id) => CampaignProfile.Load().IsCampaignComplete(id);

    // ── Save / load ────────────────────────────────────────────────────

    public static string PathFor(int slot) => Path.Combine(SaveLoadSystem.SlotPath(slot), "campaign_progress.json");

    /// <summary>The slot's progress, or a fresh one (for campaignId) if it has none yet.</summary>
    public static CampaignProgress Load(int slot, string campaignId)
    {
        string path = PathFor(slot);
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonConvert.DeserializeObject<CampaignProgress>(File.ReadAllText(path));
                if (loaded != null) return loaded;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CampaignProgress] Couldn't read {path}: {e.Message} — starting afresh.");
            }
        }
        return new CampaignProgress(campaignId);
    }

    public void Save(int slot)
    {
        string path = PathFor(slot);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}

/// <summary>
/// What's true across every playthrough: which campaigns have been finished
/// (for CompleteCampaign). Saved as Campaigns/profile.json.
/// </summary>
[Serializable]
public class CampaignProfile
{
    public List<string> completedCampaigns = new();

    public bool IsCampaignComplete(string id) =>
        completedCampaigns.Exists(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase));

    public void MarkCampaignComplete(string id)
    {
        if (!string.IsNullOrEmpty(id) && !IsCampaignComplete(id)) completedCampaigns.Add(id);
    }

    private static CampaignProfile _cached;

    public static string FilePath => Path.Combine(SaveLoadSystem.CampaignRoot, "profile.json");

    public static CampaignProfile Load()
    {
        if (_cached != null) return _cached;
        if (File.Exists(FilePath))
        {
            try { _cached = JsonConvert.DeserializeObject<CampaignProfile>(File.ReadAllText(FilePath)); }
            catch (Exception e) { Debug.LogWarning($"[CampaignProfile] Couldn't read {FilePath}: {e.Message}"); }
        }
        return _cached ??= new CampaignProfile();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        _cached = this;
    }
}
