using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Reads the Campaigns workbook into campaign records, validating as it
/// goes. Knows nothing about assets — CampaignImporter builds the catalogue
/// and matches recordings. Problems become warnings naming sheet and row.
///
///   Campaigns        one row per campaign: CampaignID, CampaignName_&lt;lang&gt;, Order, Requires
///   &lt;ID&gt;_Missions    one row per mission: MissionID, Kind, LevelId, Requires, then a
///                    PreName_&lt;lang&gt; / PostName_&lt;lang&gt; / Briefing_&lt;lang&gt; per language
///   &lt;ID&gt;_Endonyms    one row per narrated line: CampaignID, MissionID, Moment,
///                    Dialogue_&lt;lang&gt;, Cues_&lt;lang&gt;
///
/// Notes and any other column are yours and ignored. Endonym rows with no
/// dialogue yet are skipped. Older layouts still read: Name_&lt;lang&gt; on the
/// Campaigns sheet, and Name_&lt;lang&gt; on Endonyms rows (Intro → PreName,
/// Outro → PostName) for a mission whose Missions row has none.
/// </summary>
public class CampaignWorkbookParser : WorkbookParser
{
    public readonly List<CampaignCatalogue.Campaign> Campaigns = new();
    /// <summary>Every language with a Name_/Dialogue_/Cues_ column, in the order first seen.</summary>
    public readonly List<string> Languages = new();

    private static readonly string[] TextPrefixes = { "CampaignName_", "PreName_", "PostName_", "Briefing_", "Dialogue_", "Cues_", "Name_" };

    public static CampaignWorkbookParser Parse(XlsxReader book)
    {
        var p = new CampaignWorkbookParser();
        p.ReadCampaigns(book);
        foreach (var campaign in p.Campaigns)
        {
            p.ReadMissions(book, campaign);
            p.ReadEndonyms(book, campaign);
        }
        p.CheckRules();
        return p;
    }

    // ── Campaigns ──────────────────────────────────────────────────────

    private void ReadCampaigns(XlsxReader book)
    {
        var sheet = book.GetSheet("Campaigns");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "CampaignID") : null;

        if (table != null)
        {
            NoteLanguages(table.Headers);
            foreach (var row in table.Rows)
            {
                string id = row["CampaignID"];
                if (id == null) continue;
                if (Campaigns.Any(c => Same(c.id, id))) { Warn(row, $"Duplicate CampaignID {id} — skipped (first kept)."); continue; }

                var campaign = new CampaignCatalogue.Campaign
                {
                    id       = id,
                    order    = Int(row, "Order", Campaigns.Count + 1),
                    requires = row["Requires"],
                };
                var names = Texts(row, "CampaignName_");
                campaign.names.AddRange(names.Count > 0 ? names : Texts(row, "Name_"));   // older layout
                Campaigns.Add(campaign);
            }
        }
        else Warnings.Add("No Campaigns sheet (with a CampaignID header) — campaigns are taken from the <ID>_Missions sheet names.");

        // Any campaign with sheets but no row on the Campaigns sheet.
        foreach (var s in book.Sheets)
        {
            string id = s.Name.EndsWith("_Missions", StringComparison.OrdinalIgnoreCase) ? s.Name.Substring(0, s.Name.Length - 9) : null;
            if (id == null || Campaigns.Any(c => Same(c.id, id))) continue;
            if (table != null) Warnings.Add($"{s.Name}: campaign {id} isn't on the Campaigns sheet — added at the end.");
            Campaigns.Add(new CampaignCatalogue.Campaign { id = id, order = Campaigns.Count + 1 });
        }
        Campaigns.Sort((a, b) => a.order.CompareTo(b.order));
    }

    // ── Missions ───────────────────────────────────────────────────────

    private void ReadMissions(XlsxReader book, CampaignCatalogue.Campaign campaign)
    {
        string name  = $"{campaign.id}_Missions";
        var    sheet = book.GetSheet(name);
        var    table = sheet != null ? XlsxReader.ReadTable(sheet, "MissionID") : null;
        if (table == null) { Warnings.Add($"Campaign {campaign.id} has no {name} sheet (with a MissionID header) — no missions."); return; }

        NoteLanguages(table.Headers);
        foreach (var row in table.Rows)
        {
            string id = row["MissionID"];
            if (id == null) continue;
            if (campaign.Find(id) != null) { Warn(row, $"Duplicate MissionID {id} — skipped (first kept)."); continue; }

            var fallbackKind = id.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? MissionKind.Challenge : MissionKind.Story;
            var mission = new CampaignCatalogue.Mission
            {
                id       = id,
                kind     = ParseEnum(row, "Kind", fallbackKind),
                levelId  = row["LevelId"] ?? $"{campaign.id}_{id}",
                requires = row["Requires"],
            };
            mission.preNames.AddRange(Texts(row, "PreName_"));
            mission.postNames.AddRange(Texts(row, "PostName_"));
            mission.briefings.AddRange(Texts(row, "Briefing_"));
            campaign.missions.Add(mission);
        }
    }

    // ── Endonyms ───────────────────────────────────────────────────────

    private void ReadEndonyms(XlsxReader book, CampaignCatalogue.Campaign campaign)
    {
        string name  = $"{campaign.id}_Endonyms";
        var    sheet = book.GetSheet(name);
        var    table = sheet != null ? XlsxReader.ReadTable(sheet, "CampaignID") : null;
        if (table == null) { Warnings.Add($"Campaign {campaign.id} has no {name} sheet (with a CampaignID header) — no narration."); return; }
        NoteLanguages(table.Headers);

        foreach (var row in table.Rows)
        {
            var oldNames = Texts(row, "Name_");   // older layout: names on the Intro/Outro rows
            var dialogue = Texts(row, "Dialogue_");
            var cues     = Texts(row, "Cues_");
            if (oldNames.Count == 0 && dialogue.Count == 0) continue;   // not written yet

            string missionId = row["MissionID"];
            string rowCampaign = row["CampaignID"];
            if (rowCampaign != null && !Same(rowCampaign, campaign.id))
                Warn(row, $"CampaignID {rowCampaign} on the {campaign.id} sheet — treated as {campaign.id}.");

            var mission = missionId != null ? campaign.Find(missionId) : null;
            if (mission == null) { Warn(row, $"Mission {missionId ?? "(blank)"} isn't on {campaign.id}_Missions — line skipped."); continue; }

            string momentText = row["Moment"] ?? row["Intro/Outro"];
            if (!TryEnum(momentText, out MissionMoment moment))
            { Warn(row, $"Moment '{momentText}' isn't Intro, Outro or Defeat — line skipped."); continue; }
            if (mission.Line(moment) != null) { Warn(row, $"{mission.id} already has a {moment} line — skipped."); continue; }

            if (oldNames.Count > 0)
            {
                var into = moment == MissionMoment.Intro ? mission.preNames : moment == MissionMoment.Outro ? mission.postNames : null;
                if (into == null || into.Count > 0)
                    Warn(row, "Name_ columns belong on the Missions sheet now (PreName_/PostName_) — this one is ignored.");
                else into.AddRange(oldNames);
            }
            if (dialogue.Count == 0) continue;

            foreach (var cue in cues)
            {
                var said   = dialogue.FirstOrDefault(d => d.language == cue.language)?.text;
                int needed = Subtitles.Split(said).Count;
                var times  = Subtitles.ParseCues(cue.text);
                if (times == null)
                    Warn(row, $"Cues_{cue.language} '{cue.text}' should be seconds in order, e.g. 0, 6.5, 12.2 — timed automatically instead.");
                else if (times.Count != needed)
                    Warn(row, $"Cues_{cue.language} has {times.Count} time(s) but the dialogue has {needed} subtitle(s) — timed automatically instead.");
            }

            var line = new CampaignCatalogue.NarrationLine { moment = moment };
            line.dialogue.AddRange(dialogue);
            line.cues.AddRange(cues);
            mission.lines.Add(line);
        }
    }

    // ── Unlock rules ───────────────────────────────────────────────────

    private void CheckRules()
    {
        foreach (var campaign in Campaigns)
        {
            CheckRule($"Campaign {campaign.id}", campaign.requires, null);
            foreach (var mission in campaign.missions)
                CheckRule($"{campaign.id} {mission.id}", mission.requires, campaign);
        }
    }

    private void CheckRule(string where, string text, CampaignCatalogue.Campaign campaign)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var rule = UnlockRule.Parse(text);
        if (rule.Error != null) { Warnings.Add($"{where}: Requires '{text}' — {rule.Error}. It will stay locked."); return; }

        foreach (var id in rule.MissionsReferenced())
        {
            if (campaign == null)
                Warnings.Add($"{where}: Requires '{text}' names mission {id}, but a campaign's rule can't ask about missions — use CompleteCampaign(…).");
            else if (campaign.Find(id) == null)
                Warnings.Add($"{where}: Requires '{text}' names mission {id}, which isn't in {campaign.id}.");
        }
        foreach (var stat in rule.StatsReferenced())
            if (!MissionStats.IsKnown(stat))
                Warnings.Add($"{where}: Requires '{text}' uses the stat '{stat}', which the game doesn't record yet " +
                             $"(it records: {string.Join(", ", MissionStats.Known)}). It will hold only once that stat exists.");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void NoteLanguages(IEnumerable<string> headers)
    {
        foreach (var h in headers)
            foreach (var prefix in TextPrefixes)
                if (h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && h.Length > prefix.Length)
                {
                    string lang = h.Substring(prefix.Length).ToLowerInvariant();
                    if (!Languages.Contains(lang)) Languages.Add(lang);
                }
    }

    /// <summary>The row's text in every language for a column prefix (Name_, Dialogue_, Cues_).</summary>
    private List<CampaignCatalogue.LocalText> Texts(XlsxReader.Row row, string prefix)
    {
        var list = new List<CampaignCatalogue.LocalText>();
        foreach (var pair in row.Values)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || pair.Key.Length <= prefix.Length) continue;
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            list.Add(new CampaignCatalogue.LocalText { language = pair.Key.Substring(prefix.Length).ToLowerInvariant(), text = pair.Value.Trim() });
        }
        return list;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
