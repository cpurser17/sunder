using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Plays a mission's narration in the Campaign scene: its recording, with
/// subtitles a sentence at a time (see Subtitles). Like the Announcer, but
/// for the mission-selection map, and one line at a time — starting another
/// stops the first.
///
///   CampaignNarrator.Play("04U", "M20", MissionMoment.Intro);
///
/// Escape, or a click off the UI, skips. A line with no recording shows its subtitles for
/// as long as they take to read. Runs on unscaled time.
///
/// Scene setup: none — Play() creates one if the scene has none. Add it
/// yourself to choose the language or restyle the subtitles.
/// </summary>
public class CampaignNarrator : MonoBehaviour
{
    public static CampaignNarrator Instance { get; private set; }

    [Tooltip("Language code for text and audio, matching the workbook's _<code> columns (e.g. en).")]
    [SerializeField] private string language = "en";
    [Tooltip("Empty = the catalogue built by Sunder > Import Campaigns.")]
    [SerializeField] private CampaignCatalogue catalogue;
    [SerializeField, Range(0f, 1f)] private float volume = 1f;
    [SerializeField] private bool skipOnClick = true;

    [Header("Subtitles")]
    [SerializeField] private float   fontSize      = 30f;
    [SerializeField] private Color   textColour    = Color.white;
    [SerializeField] private Color   backColour    = new(0f, 0f, 0f, 0.6f);
    [Tooltip("Distance of the subtitle box above the bottom of the screen, in reference pixels.")]
    [SerializeField] private float   bottomMargin  = 80f;
    [SerializeField] private float   maxWidth      = 1400f;

    /// <summary>Fired when a line finishes or is skipped or stopped: campaign, mission, moment.</summary>
    public event Action<string, string, MissionMoment> OnLineFinished;

    public bool IsPlaying => _playing;

    /// <summary>The frame Escape last skipped a line — so the same press doesn't also leave the screen.</summary>
    public static int SkippedFrame { get; private set; } = -1;
    public string Language { get => language; set => language = value; }

    private AudioSource     _voice;
    private RectTransform   _box;
    private TextMeshProUGUI _text;
    private LayoutElement   _textSize;
    private List<Subtitles.Cue> _cues;
    private float  _startedAt, _endsAt;
    private int    _shown = -1;
    private bool   _playing;
    private string _campaign, _mission;
    private MissionMoment _moment;

    // ── Playing ────────────────────────────────────────────────────────

    /// <summary>Plays a mission's line, creating a narrator if the scene has none. False if there's no such line.</summary>
    public static bool Play(string campaignId, string missionId, MissionMoment moment)
    {
        if (Instance == null) new GameObject("CampaignNarrator").AddComponent<CampaignNarrator>();
        return Instance.PlayLine(campaignId, missionId, moment);
    }

    public bool PlayLine(string campaignId, string missionId, MissionMoment moment)
    {
        Stop();
        var cat = catalogue != null ? catalogue : CampaignCatalogue.Load();
        var line = cat?.Find(campaignId)?.Find(missionId)?.Line(moment);
        if (line == null)
        {
            Debug.Log($"[CampaignNarrator] {campaignId} {missionId} has no {moment} line yet (no dialogue in the Campaigns workbook).");
            return false;
        }

        string fallback = cat.defaultLanguage;
        string dialogue = line.DialogueFor(language, fallback);
        var    clip     = line.ClipFor(language, fallback);
        if (string.IsNullOrWhiteSpace(dialogue) && clip == null) return false;
        Debug.Log($"[CampaignNarrator] Playing {campaignId} {missionId} {moment}" +
                  (clip != null ? $" ({clip.name})." : " (no recording — subtitles only)."));

        float clipSeconds = clip != null ? clip.length : 0f;
        _cues = Subtitles.Time(dialogue, clipSeconds, line.CuesFor(language, fallback));

        _campaign = campaignId; _mission = missionId; _moment = moment;
        _startedAt = Time.unscaledTime;
        float last = _cues.Count > 0 ? _cues[_cues.Count - 1].End : 0f;
        _endsAt   = _startedAt + Mathf.Max(clipSeconds, last);
        _shown    = -1;
        _playing  = true;

        if (clip != null) { _voice.clip = clip; _voice.volume = volume; _voice.Play(); }
        return true;
    }

    /// <summary>Stops the line now (as skipping does).</summary>
    public void Stop()
    {
        if (!_playing) return;
        _playing = false;
        _voice.Stop();
        Show(null);
        OnLineFinished?.Invoke(_campaign, _mission, _moment);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _voice = gameObject.AddComponent<AudioSource>();
        _voice.playOnAwake  = false;
        _voice.spatialBlend = 0f;
        _voice.ignoreListenerPause = true;
        BuildSubtitles();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (!_playing) return;

        // Escape, or a click that isn't on the UI (browsing the mission list doesn't cut a line off).
        bool clickOffUi = Input.GetMouseButtonDown(0) &&
                          (UnityEngine.EventSystems.EventSystem.current == null ||
                           !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject());
        if (skipOnClick && (clickOffUi || Input.GetKeyDown(KeyCode.Escape)))
        {
            if (Input.GetKeyDown(KeyCode.Escape)) SkippedFrame = Time.frameCount;
            Stop();
            return;
        }

        float t = Time.unscaledTime - _startedAt;
        if (Time.unscaledTime >= _endsAt) { Stop(); return; }

        int current = -1;
        for (int i = 0; i < _cues.Count; i++)
            if (t >= _cues[i].Start && t < _cues[i].End) { current = i; break; }
        if (current == _shown) return;
        _shown = current;
        Show(current >= 0 ? _cues[current].Text : null);
    }

    // ── Subtitles ──────────────────────────────────────────────────────

    private void BuildSubtitles()
    {
        var canvasGo = new GameObject("NarrationCanvas", typeof(RectTransform));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;

        var boxGo = new GameObject("Subtitle", typeof(RectTransform));
        boxGo.transform.SetParent(canvasGo.transform, false);
        _box = (RectTransform)boxGo.transform;
        _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0f);
        _box.pivot     = new Vector2(0.5f, 0f);
        _box.anchoredPosition = new Vector2(0f, bottomMargin);
        var back = boxGo.AddComponent<Image>();
        back.color = backColour;
        back.raycastTarget = false;
        var fit = boxGo.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
        var layout = boxGo.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(24, 24, 12, 12);
        layout.childControlWidth = layout.childControlHeight = true;

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(boxGo.transform, false);
        _text = textGo.AddComponent<TextMeshProUGUI>();
        _text.fontSize      = fontSize;
        _text.color         = textColour;
        _text.alignment     = TextAlignmentOptions.Center;
        _text.raycastTarget = false;
        _textSize = textGo.AddComponent<LayoutElement>();

        boxGo.SetActive(false);
    }

    private void Show(string subtitle)
    {
        if (_box == null) return;
        bool on = !string.IsNullOrEmpty(subtitle);
        if (on)
        {
            _text.text = subtitle;
            // As wide as the sentence needs, wrapping at maxWidth.
            _textSize.preferredWidth = Mathf.Min(maxWidth, _text.GetPreferredValues(subtitle).x + 1f);
        }
        _box.gameObject.SetActive(on);
    }
}
