using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The Campaign scene: the playthrough's missions, and the way into them.
/// A placeholder built in code until the overworld map exists — a list of
/// the campaign's story and challenge missions (Locked / Available /
/// Completed, by their unlock rules) and a briefing for the one selected:
/// its narration plays, then its briefing, objectives and best results show,
/// with Start Mission.
///
/// Back from a mission, the result is added to the playthrough (and saved),
/// and the mission's Outro (won) or Defeat (lost) narration plays.
///
/// Scene setup: none — SceneScreens adds one to the Campaign scene.
/// </summary>
public class CampaignScreen : MonoBehaviour
{
    private CampaignCatalogue          _catalogue;
    private CampaignCatalogue.Campaign _campaign;
    private CampaignCatalogue.Mission  _selected;

    private RectTransform   _canvas, _list, _detail;
    private TextMeshProUGUI _header;

    private string Lang     => CampaignSession.Language;
    private string Fallback => _catalogue != null ? _catalogue.defaultLanguage : "en";

    private void Start()
    {
        _catalogue = CampaignCatalogue.Load();
        _canvas    = UiKit.Canvas("CampaignCanvas", 10, transform);
        UiKit.Fill(UiKit.Box("Backdrop", _canvas, new Color(0.05f, 0.04f, 0.06f, 1f)).rectTransform);

        if (!CampaignSession.Active)
        {
            // Opened directly (e.g. in the editor): pick up the latest playthrough if there is one.
            int latest = CampaignSession.LatestSlot();
            if (latest >= 0) CampaignSession.Open(latest);
        }
        _campaign = CampaignSession.Active ? _catalogue?.Find(CampaignSession.Progress.campaignId) : null;
        if (_campaign == null)
        {
            Message(_catalogue == null
                ? "No campaigns yet — run Sunder > Import Campaigns in Unity."
                : "No campaign in progress — start one from New Game.");
            return;
        }

        BuildLayout();
        var back = TakeMissionResult();
        Refresh();
        Select(back ?? FirstAvailable(), playIntro: back == null);
    }

    /// <summary>Escape: skips the narration if a line is playing, else back to the main menu.</summary>
    private void Update()
    {
        if (!GameControls.General.Back.WasPressedThisFrame()) return;
        var narrator = CampaignNarrator.Instance;
        if (CampaignNarrator.SkippedFrame == Time.frameCount) return;        // that press skipped a line
        if (narrator != null && narrator.IsPlaying) { narrator.Stop(); return; }
        BackToMenu();
    }

    // ── Coming back from a mission ─────────────────────────────────────

    /// <summary>A mission just played: add its result, save, and play its Outro or Defeat line. Returns that mission.</summary>
    private CampaignCatalogue.Mission TakeMissionResult()
    {
        if (!MissionReturn.Pending || MissionReturn.Origin != MissionOrigin.Campaign) return null;
        var result = MissionReturn.Result;
        bool ours  = string.Equals(MissionReturn.CampaignId, _campaign.id, System.StringComparison.OrdinalIgnoreCase);
        MissionReturn.Clear();
        if (!ours || result == null) return null;

        var progress = CampaignSession.Progress;
        progress.Record(result);
        if (result.won && AllStoryComplete())
        {
            var profile = CampaignProfile.Load();
            profile.MarkCampaignComplete(_campaign.id);
            profile.Save();
        }
        CampaignSession.Save();

        var mission = _campaign.Find(result.missionId);
        var moment  = result.won ? MissionMoment.Outro : MissionMoment.Defeat;
        Debug.Log($"[CampaignScreen] Back from {_campaign.id} {result.missionId}: {(result.won ? "won" : "lost")} — playing its {moment} line.");
        if (mission != null) CampaignNarrator.Play(_campaign.id, mission.id, moment);
        return mission;
    }

    private bool AllStoryComplete()
    {
        foreach (var m in _campaign.missions)
            if (m.kind == MissionKind.Story && !CampaignSession.Progress.IsComplete(m.id)) return false;
        return true;
    }

    // ── Layout ─────────────────────────────────────────────────────────

    private void Message(string text)
    {
        var label = UiKit.Label("Message", _canvas, text, 32f, TextAlignmentOptions.Center);
        UiKit.Anchor(label.rectTransform, 0.1f, 0.4f, 0.9f, 0.6f);
        var back = UiKit.Button("Back", _canvas, "Back to menu", BackToMenu, 26f);
        UiKit.Anchor((RectTransform)back.transform, 0.4f, 0.25f, 0.6f, 0.32f);
    }

    private void BuildLayout()
    {
        _header = UiKit.Label("Header", _canvas, "", 40f, TextAlignmentOptions.Left);
        UiKit.Anchor(_header.rectTransform, 0f, 0.9f, 0.75f, 1f, 24f);
        var back = UiKit.Button("Back", _canvas, "Back to menu", BackToMenu, 24f);
        UiKit.Anchor((RectTransform)back.transform, 0.8f, 0.92f, 0.98f, 0.98f);

        _list = UiKit.ScrollList("Missions", _canvas);
        UiKit.Anchor((RectTransform)_list.parent, 0f, 0f, 0.36f, 0.9f, 24f);

        _detail = UiKit.Box("Detail", _canvas, UiKit.Panel).rectTransform;
        UiKit.Anchor(_detail, 0.36f, 0f, 1f, 0.9f, 24f);
    }

    private void Refresh()
    {
        var progress = CampaignSession.Progress;
        string faction = string.IsNullOrEmpty(progress.playerFactionId) ? _campaign.id : progress.playerFactionId;
        _header.text = $"{_campaign.NameFor(Lang, Fallback)}  <size=60%><color=#aaa>playing as {faction} · slot {CampaignSession.Slot + 1}</color></size>";

        UiKit.Clear(_list);
        Section("Story", MissionKind.Story);
        Section("Challenges", MissionKind.Challenge);
    }

    private void Section(string title, MissionKind kind)
    {
        UiKit.Height(UiKit.Label(title, _list, title, 26f, TextAlignmentOptions.BottomLeft, UiKit.TextDim), 40f);
        foreach (var m in _campaign.missions)
        {
            if (m.kind != kind) continue;
            var mission = m;
            var state = CampaignSession.Progress.StateOf(m);
            string name = state == MissionState.Completed ? m.ConqueredNameFor(Lang, Fallback) : m.NameFor(Lang, Fallback);
            string tag = state switch
            {
                MissionState.Completed => "<color=#7fd77f>Completed</color>",
                MissionState.Available => "<color=#e8c070>Available</color>",
                _                      => "<color=#888>Locked</color>",
            };
            var b = UiKit.Height(UiKit.Button(m.id, _list, $"{m.id}   {name}   <size=75%>{tag}</size>",
                                              () => Select(mission, playIntro: true), 22f), 48f);
            UiKit.LabelOf(b).alignment = TextAlignmentOptions.Left;
            UiKit.Style(b, m == _selected, state != MissionState.Locked);
        }
    }

    private CampaignCatalogue.Mission FirstAvailable()
    {
        foreach (var m in _campaign.missions)
            if (m.kind == MissionKind.Story && CampaignSession.Progress.StateOf(m) == MissionState.Available) return m;
        return _campaign.missions.Count > 0 ? _campaign.missions[0] : null;
    }

    // ── Briefing ───────────────────────────────────────────────────────

    private void Select(CampaignCatalogue.Mission mission, bool playIntro)
    {
        _selected = mission;
        Refresh();
        UiKit.Clear(_detail);
        if (mission == null) return;

        var state = CampaignSession.Progress.StateOf(mission);
        if (playIntro && state != MissionState.Locked) CampaignNarrator.Play(_campaign.id, mission.id, MissionMoment.Intro);

        var column = UiKit.Rect("Column", _detail);
        UiKit.Fill(column, 24f, 110f, 24f, 20f);
        UiKit.Column(column, 10f, 0);

        string name = state == MissionState.Completed
            ? $"{mission.ConqueredNameFor(Lang, Fallback)}  <size=60%><color=#aaa>(was {mission.NameFor(Lang, Fallback)})</color></size>"
            : mission.NameFor(Lang, Fallback);
        UiKit.Height(UiKit.Label("Name", column, name, 44f), 60f);
        UiKit.Height(UiKit.Label("Kind", column, $"{mission.id} · {mission.kind} · {state}", 22f, TextAlignmentOptions.Left, UiKit.TextDim), 30f);

        string briefing = mission.BriefingFor(Lang, Fallback);
        var brief = UiKit.Label("Briefing", column, string.IsNullOrWhiteSpace(briefing) ? "<i>No briefing written yet.</i>" : briefing, 24f);
        UiKit.Height(brief, 260f);

        UiKit.Height(UiKit.Label("ObjectivesTitle", column, "Objectives", 26f, TextAlignmentOptions.BottomLeft, UiKit.TextDim), 36f);
        var level = mission.level != null ? SaveLoadSystem.ParseLevel(mission.level.text, mission.levelId) : null;
        if (level == null)
            UiKit.Height(UiKit.Label("NoLevel", column, $"<color=#e07060>No level yet — put {mission.levelId}.json in Assets/Levels/Campaigns/{_campaign.id}/</color>", 22f), 32f);
        else
            foreach (var c in WinConditions.Of(level))
                UiKit.Height(UiKit.Label(c.ToString(), column, "•  " + WinConditions.Describe(c), 24f), 32f);

        var progress = CampaignSession.Progress;
        if (progress.attempts.TryGetValue(mission.id, out int tries) && tries > 0)
        {
            string best = BestLine(mission.id);
            UiKit.Height(UiKit.Label("Record", column, $"Played {tries} time(s). {best}", 20f, TextAlignmentOptions.Left, UiKit.TextDim), 30f);
        }

        var hear = UiKit.Button("Hear", _detail, "Hear again", () => CampaignNarrator.Play(_campaign.id, mission.id,
            state == MissionState.Completed ? MissionMoment.Outro : MissionMoment.Intro), 22f);
        UiKit.Anchor((RectTransform)hear.transform, 0.02f, 0.02f, 0.3f, 0.1f);
        UiKit.Style(hear, false, state != MissionState.Locked);

        var start = UiKit.Button("Start", _detail, state == MissionState.Completed ? "Replay Mission" : "Start Mission", () => StartMission(mission), 26f);
        UiKit.Anchor((RectTransform)start.transform, 0.66f, 0.02f, 0.98f, 0.1f);
        UiKit.Style(start, true, state != MissionState.Locked && mission.level != null);
    }

    private string BestLine(string missionId)
    {
        var parts = new List<string>();
        foreach (var stat in new[] { MissionStats.Kills, MissionStats.MinionsLost, MissionStats.Seconds })
        {
            bool lowest = stat != MissionStats.Kills;
            if (!CampaignSession.Progress.TryBest(missionId, stat, lowest, out float v)) continue;
            parts.Add(stat == MissionStats.Seconds ? $"Fastest {Mathf.FloorToInt(v / 60f)}:{Mathf.FloorToInt(v % 60f):00}"
                                                   : $"{(lowest ? "Fewest" : "Most")} {MissionStats.Label(stat).ToLowerInvariant()}: {Mathf.RoundToInt(v)}");
        }
        return string.Join(" · ", parts);
    }

    // ── Leaving ────────────────────────────────────────────────────────

    private void StartMission(CampaignCatalogue.Mission mission)
    {
        if (!Application.CanStreamedLevelBeLoaded(SceneNames.Gameplay))
        {
            Debug.LogError("[CampaignScreen] The Gameplay scene isn't in Build Settings.");
            return;
        }
        CampaignNarrator.Instance?.Stop();
        var progress = CampaignSession.Progress;
        progress.lastPlayed = System.DateTime.UtcNow.ToString("o");
        CampaignSession.Save();

        GameplaySceneArgs.Clear();
        GameplaySceneArgs.SlotIndex       = CampaignSession.Slot;
        GameplaySceneArgs.LevelId         = mission.levelId;
        GameplaySceneArgs.IsNewGame       = true;
        GameplaySceneArgs.Origin          = MissionOrigin.Campaign;
        GameplaySceneArgs.CampaignId      = _campaign.id;
        GameplaySceneArgs.MissionId       = mission.id;
        GameplaySceneArgs.PlayerFactionId = progress.playerFactionId;
        SceneManager.LoadScene(SceneNames.Gameplay);
    }

    private void BackToMenu()
    {
        CampaignNarrator.Instance?.Stop();
        if (Application.CanStreamedLevelBeLoaded(SceneNames.Start)) SceneManager.LoadScene(SceneNames.Start);
        else Debug.LogWarning("[CampaignScreen] The Start scene isn't in Build Settings.");
    }
}
