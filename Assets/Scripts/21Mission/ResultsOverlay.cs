using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The end-of-mission screen, over the game: Victory or Defeat, the
/// objectives, and Age-of-Empires-style scores for every team — a table of
/// totals and a graph of each stat over the match. The game is paused while
/// it's open. Minimise to keep playing (a button brings it back); Finish
/// leaves for wherever the mission was started from.
///
/// Built in code as a placeholder until it has art.
/// </summary>
public class ResultsOverlay : MonoBehaviour
{
    private static ResultsOverlay _instance;

    private MissionDirector _director;
    private RectTransform   _canvas, _window, _body, _graphArea;
    private Button          _reopen, _scoresTab, _graphsTab;
    private readonly List<Button> _statButtons = new();
    private string _graphStat = MissionStats.Kills;
    private bool   _showingGraphs;

    /// <summary>Opens the results for the mission that just ended.</summary>
    public static void Show(MissionDirector director)
    {
        if (_instance == null) _instance = new GameObject("ResultsOverlay").AddComponent<ResultsOverlay>();
        _instance._director = director;
        _instance.Build();
        _instance.Open();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    /// <summary>Escape minimises the results (back to the dungeon); the button brings them back.</summary>
    private void Update()
    {
        if (_window != null && _window.gameObject.activeSelf && GameControls.General.Back.WasPressedThisFrame()) Minimise();
    }

    // ── Open / minimise ────────────────────────────────────────────────

    private void Open()
    {
        _window.gameObject.SetActive(true);
        _reopen.gameObject.SetActive(false);
        SetPaused(true);
    }

    private void Minimise()
    {
        _window.gameObject.SetActive(false);
        _reopen.gameObject.SetActive(true);
        SetPaused(false);
    }

    private static void SetPaused(bool paused)
    {
        var pause = FindAnyObjectByType<PauseController>();
        if (pause != null) pause.SetPaused(paused);
    }

    // ── Layout ─────────────────────────────────────────────────────────

    private void Build()
    {
        if (_canvas != null) Destroy(_canvas.gameObject);
        _canvas = UiKit.Canvas("ResultsCanvas", 300, transform);

        var reopenButton = UiKit.Button("ShowResults", _canvas, "Mission over — show results", Open, 22f);
        var reopenRect = (RectTransform)reopenButton.transform;
        reopenRect.anchorMin = reopenRect.anchorMax = new Vector2(0.5f, 1f);
        reopenRect.pivot = new Vector2(0.5f, 1f);
        reopenRect.sizeDelta = new Vector2(420f, 48f);
        reopenRect.anchoredPosition = new Vector2(0f, -12f);
        _reopen = reopenButton;

        _window = UiKit.Rect("Window", _canvas);
        UiKit.Fill(_window);
        var dim = _window.gameObject.AddComponent<Image>();
        dim.color = new Color(0f, 0f, 0f, 0.6f);

        var panel = UiKit.Box("Panel", _window, UiKit.Panel).rectTransform;
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(1560f, 900f);

        bool won = _director.State == MissionDirector.Outcome.Won;
        var title = UiKit.Label("Title", panel, won ? "Victory" : "Defeat", 64f, TextAlignmentOptions.Center,
                                won ? new Color(1f, 0.82f, 0.35f) : new Color(0.9f, 0.35f, 0.3f));
        Place(title.rectTransform, 0f, 820f, 1560f, 80f);
        var name = UiKit.Label("Mission", panel, $"{_director.MissionName}  ·  {Clock(MatchStats.Instance != null ? MatchStats.Instance.Seconds : 0f)}",
                               26f, TextAlignmentOptions.Center, UiKit.TextDim);
        Place(name.rectTransform, 0f, 785f, 1560f, 36f);

        // Objectives.
        var objectives = UiKit.Rect("Objectives", panel);
        Place(objectives, 40f, 650f, 1480f, 125f);
        UiKit.Column(objectives, 2f, 4);
        foreach (var c in _director.Conditions)
        {
            bool met = won || _director.IsMet(c);   // a win means every objective was met
            UiKit.Height(UiKit.Label(c.ToString(), objectives,
                $"{(met ? "<color=#7fd77f>Done</color>" : "<color=#e07060>Not done</color>")}   {WinConditions.Describe(c)}",
                24f), 30f);
        }

        // Tabs.
        _scoresTab = UiKit.Button("ScoresTab", panel, "Scores", () => ShowTab(false), 24f);
        Place((RectTransform)_scoresTab.transform, 40f, 595f, 200f, 44f);
        _graphsTab = UiKit.Button("GraphsTab", panel, "Graphs", () => ShowTab(true), 24f);
        Place((RectTransform)_graphsTab.transform, 250f, 595f, 200f, 44f);

        _body = UiKit.Box("Body", panel, UiKit.PanelLight).rectTransform;
        Place(_body, 40f, 100f, 1480f, 485f);

        // Buttons.
        var minimise = UiKit.Button("Minimise", panel, "Minimise", Minimise, 26f);
        Place((RectTransform)minimise.transform, 40f, 30f, 260f, 54f);
        var finish = UiKit.Button("Finish", panel, "Finish", () => _director.Finish(), 26f);
        Place((RectTransform)finish.transform, 1260f, 30f, 260f, 54f);

        ShowTab(_showingGraphs);
    }

    /// <summary>Positions a rect inside the 1560x900 panel by its bottom-left corner.</summary>
    private static void Place(RectTransform rect, float x, float y, float w, float h)
    {
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot     = Vector2.zero;
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(w, h);
    }

    private void ShowTab(bool graphs)
    {
        _showingGraphs = graphs;
        UiKit.Style(_scoresTab, !graphs);
        UiKit.Style(_graphsTab, graphs);
        UiKit.Clear(_body);
        _statButtons.Clear();
        if (graphs) BuildGraphs(); else BuildScores();
    }

    // ── Scores table ───────────────────────────────────────────────────

    private static readonly string[] TableStats =
    {
        MissionStats.Kills, MissionStats.MinionsLost, MissionStats.GoldMined, MissionStats.GoldSpent,
        MissionStats.TerritoryClaimed, MissionStats.CreaturesAttracted, MissionStats.CreaturesRecruited,
        MissionStats.SpellsCast, MissionStats.HeartHealthLeft,
    };

    private void BuildScores()
    {
        var stats = MatchStats.Instance;
        var table = UiKit.Rect("Table", _body);
        UiKit.Fill(table, 12f, 12f, 12f, 12f);
        UiKit.Column(table, 4f, 0);

        var header = UiKit.Height(UiKit.Rect("Header", table), 54f);
        UiKit.Row(header, 4f);
        UiKit.Width(UiKit.Label("Team", header, "Team", 20f, TextAlignmentOptions.BottomLeft, UiKit.TextDim), 230f);
        foreach (var stat in TableStats)
            UiKit.Label(stat, header, MissionStats.Label(stat), 18f, TextAlignmentOptions.Bottom, UiKit.TextDim);

        if (stats == null) return;
        foreach (var faction in stats.Factions())
        {
            var (teamName, colour) = MissionDirector.Describe(faction);
            var row = UiKit.Height(UiKit.Rect(faction.ToString(), table), 40f);
            UiKit.Row(row, 4f);
            UiKit.Width(UiKit.Label("Team", row, teamName, 22f, TextAlignmentOptions.Left, colour), 230f);
            foreach (var stat in TableStats)
                UiKit.Label(stat, row, Mathf.RoundToInt(stats.Get(faction, stat)).ToString("N0"), 22f, TextAlignmentOptions.Center);
        }
    }

    // ── Graphs ─────────────────────────────────────────────────────────

    private void BuildGraphs()
    {
        var bar = UiKit.Rect("Stats", _body);
        bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0.5f, 1f);
        bar.offsetMin = new Vector2(12f, -56f); bar.offsetMax = new Vector2(-12f, -10f);
        UiKit.Row(bar, 6f);
        foreach (var stat in MatchStats.Tracked)
        {
            string s = stat;
            var b = UiKit.Button(stat, bar, MissionStats.Label(stat), () => { _graphStat = s; ShowTab(true); }, 17f);
            UiKit.Style(b, stat == _graphStat);
            _statButtons.Add(b);
        }

        _graphArea = UiKit.Rect("Plot", _body);
        _graphArea.anchorMin = _graphArea.anchorMax = Vector2.zero;
        _graphArea.pivot = Vector2.zero;
        _graphArea.anchoredPosition = new Vector2(70f, 40f);
        _graphArea.sizeDelta = new Vector2(1360f, 370f);
        UiKit.Box("Back", _graphArea, new Color(0f, 0f, 0f, 0.25f)).rectTransform.Let(r => UiKit.Fill(r));

        var stats = MatchStats.Instance;
        if (stats == null || stats.Samples.Count == 0) return;

        float maxT = Mathf.Max(1f, stats.Samples[stats.Samples.Count - 1].Seconds);
        float maxV = 1f;
        foreach (var sample in stats.Samples)
            foreach (var byStat in sample.Values.Values)
                if (byStat.TryGetValue(_graphStat, out float v)) maxV = Mathf.Max(maxV, v);

        AxisLabel(Mathf.RoundToInt(maxV).ToString("N0"), new Vector2(-8f, 370f), TextAlignmentOptions.TopRight);
        AxisLabel("0", new Vector2(-8f, 0f), TextAlignmentOptions.BottomRight);
        AxisLabel(Clock(maxT), new Vector2(1360f, -6f), TextAlignmentOptions.TopRight);

        float w = _graphArea.sizeDelta.x, h = _graphArea.sizeDelta.y;
        foreach (var faction in stats.Factions())
        {
            var (_, colour) = MissionDirector.Describe(faction);
            Vector2? last = null;
            foreach (var sample in stats.Samples)
            {
                float v = sample.Values.TryGetValue(faction, out var byStat) && byStat.TryGetValue(_graphStat, out float x) ? x : 0f;
                var point = new Vector2(sample.Seconds / maxT * w, v / maxV * h);
                if (last.HasValue) Segment(last.Value, point, colour);
                last = point;
            }
        }
    }

    private void Segment(Vector2 a, Vector2 b, Color colour)
    {
        var line = UiKit.Box("Line", _graphArea, colour).rectTransform;
        line.anchorMin = line.anchorMax = Vector2.zero;
        line.pivot = new Vector2(0.5f, 0.5f);
        Vector2 d = b - a;
        line.sizeDelta = new Vector2(d.magnitude + 2f, 3f);
        line.anchoredPosition = (a + b) * 0.5f;
        line.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    private void AxisLabel(string text, Vector2 at, TextAlignmentOptions align)
    {
        var label = UiKit.Label("Axis", _graphArea, text, 16f, align, UiKit.TextDim);
        var r = label.rectTransform;
        r.anchorMin = r.anchorMax = Vector2.zero;
        r.pivot = align == TextAlignmentOptions.TopRight || align == TextAlignmentOptions.BottomRight ? new Vector2(1f, 0.5f) : new Vector2(0f, 0.5f);
        r.sizeDelta = new Vector2(160f, 24f);
        r.anchoredPosition = at;
    }

    private static string Clock(float seconds)
    {
        int s = Mathf.Max(0, Mathf.RoundToInt(seconds));
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}

internal static class UiKitExtensions
{
    /// <summary>Runs an action on a value and returns it — for one-line UI setup.</summary>
    public static T Let<T>(this T value, System.Action<T> action) { action(value); return value; }
}
