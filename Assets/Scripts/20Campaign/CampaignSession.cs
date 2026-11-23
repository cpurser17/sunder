using System;
using System.Collections.Generic;

/// <summary>
/// The campaign playthrough being played this session: which save slot, and
/// its progress. Set by the Start menu (New Game, Continue, Load Game), used
/// by the Campaign scene. One save slot is one campaign playthrough; there
/// are SlotCount of them.
/// </summary>
public static class CampaignSession
{
    public const int SlotCount = 10;

    /// <summary>Language for campaign text and narration (until there's a settings screen).</summary>
    public static string Language = "en";

    public static int              Slot     { get; private set; } = -1;
    public static CampaignProgress Progress { get; private set; }
    public static bool             Active   => Slot >= 0 && Progress != null;

    /// <summary>A new playthrough of a campaign in a slot (replacing whatever was there), as a faction.</summary>
    public static void StartNew(int slot, string campaignId, string playerFactionId)
    {
        Slot     = slot;
        Progress = new CampaignProgress(campaignId) { playerFactionId = playerFactionId };
        Progress.Save(slot);
    }

    /// <summary>Opens a slot's playthrough. False if the slot is empty.</summary>
    public static bool Open(int slot)
    {
        var progress = CampaignProgress.TryLoad(slot);
        if (progress == null || string.IsNullOrEmpty(progress.campaignId)) return false;
        Slot     = slot;
        Progress = progress;
        return true;
    }

    public static void Save()
    {
        if (Active) Progress.Save(Slot);
    }

    /// <summary>Every slot's playthrough (null for an empty slot), by slot number.</summary>
    public static CampaignProgress[] AllSlots()
    {
        var slots = new CampaignProgress[SlotCount];
        for (int i = 0; i < SlotCount; i++) slots[i] = CampaignProgress.TryLoad(i);
        return slots;
    }

    /// <summary>The slot played most recently, or -1 if every slot is empty.</summary>
    public static int LatestSlot()
    {
        int best = -1;
        DateTime bestTime = DateTime.MinValue;
        var slots = AllSlots();
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null || string.IsNullOrEmpty(slots[i].campaignId)) continue;
            DateTime.TryParse(slots[i].lastPlayed, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t);
            if (best < 0 || t > bestTime) { best = i; bestTime = t; }
        }
        return best;
    }

    /// <summary>"Slot 3 — 04U, last played 12 Nov" style text for a slot.</summary>
    public static string Describe(int slot, CampaignProgress progress, CampaignCatalogue catalogue)
    {
        if (progress == null || string.IsNullOrEmpty(progress.campaignId)) return $"Slot {slot + 1} — empty";
        string name = catalogue?.Find(progress.campaignId)?.NameFor(Language, catalogue.defaultLanguage) ?? progress.campaignId;
        int story = 0, done = 0;
        var campaign = catalogue?.Find(progress.campaignId);
        if (campaign != null)
            foreach (var m in campaign.missions)
                if (m.kind == MissionKind.Story) { story++; if (progress.IsComplete(m.id)) done++; }
        string when = DateTime.TryParse(progress.lastPlayed, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
            ? t.ToLocalTime().ToString("d MMM yyyy HH:mm") : "";
        return $"Slot {slot + 1} — {name}  ({done}/{story})  {when}";
    }
}
