using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Which volume slider a sound follows.</summary>
public enum AudioCategory { Music, Effects, Voice, Announcer }

/// <summary>Full screen, borderless or windowed.</summary>
public enum DisplayMode { FullScreen, Borderless, Windowed }

/// <summary>
/// The player's settings — General, Video, Audio and Controls — kept in
/// settings.json in the player's data folder, applied when the game starts
/// (in any scene) and whenever they change. SettingsScreen edits them.
///
/// Changes apply at once: change a field on Current, then call Apply() (or
/// a narrower Apply…) and Save(). Code that depends on a setting reads it
/// from here, e.g. GameSettings.Volume(AudioCategory.Voice), TextLanguage.
/// </summary>
public static class GameSettings
{
    [Serializable]
    public class Data
    {
        public int version = 1;

        // General
        public string language;                 // null = the workbooks' default language
        public bool   separateLanguages;        // voice and text chosen separately
        public string voiceLanguage;
        public string textLanguage;
        public bool   subtitles     = true;
        public float  subtitleScale = 1f;       // 0.8 small, 1 medium, 1.3 large
        public float  uiScale       = 1f;
        public bool   confirmQuit   = true;

        // Video
        public DisplayMode displayMode = DisplayMode.Borderless;   // Unity's own default
        public int    width, height;            // 0 = the screen's own
        public bool   vSync = true;
        public int    frameCap;                 // 0 = no cap
        public int    quality = -1;             // -1 = the project's default

        // Audio (0–1)
        public float master = 1f, music = 0.8f, effects = 1f, voice = 1f, announcer = 1f;
        public bool  muteInBackground = true;

        // Controls
        public string bindings;                 // the Input System's rebinds, as JSON
        public bool   swapMouseButtons;
        public float  cameraTurnSpeed = 1f, cameraZoomSpeed = 1f, cameraMoveSpeed = 1f;

        public Data Clone() => (Data)MemberwiseClone();
    }

    public static Data Current { get; private set; } = new();

    /// <summary>Raised after any Apply — for things that read settings as they go (volumes, subtitles).</summary>
    public static event Action OnChanged;

    private static string FilePath => Path.Combine(Application.persistentDataPath, "settings.json");

    // ── Start-up ───────────────────────────────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Startup()
    {
        // Play mode doesn't reload scripts in this project, so anything kept
        // from the last session is reset here.
        Load();
        _focused = true;
        GameControls.Rebuild();
        ThirdPersonOrbitCamera.ZoomSuspended  = false;
        ThirdPersonOrbitCamera.OrbitSuspended = false;
        var runtime = new GameObject("GameSettings").AddComponent<SettingsRuntime>();
        UnityEngine.Object.DontDestroyOnLoad(runtime.gameObject);
        SceneManager.sceneLoaded -= OnSceneLoaded;   // once, however many sessions
        SceneManager.sceneLoaded += OnSceneLoaded;
        Apply();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ApplyUiScale();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GameSettings] Couldn't read {FilePath} — using defaults. {e.Message}");
            Current = new Data();
        }
        // Key bindings used to be kept in PlayerPrefs.
        if (string.IsNullOrEmpty(Current.bindings) && PlayerPrefs.HasKey("Sunder.ControlBindings"))
            Current.bindings = PlayerPrefs.GetString("Sunder.ControlBindings");
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(Current, true)); }
        catch (Exception e) { Debug.LogWarning($"[GameSettings] Couldn't save {FilePath}. {e.Message}"); }
    }

    public static void ResetSection(string section)
    {
        var d = new Data();
        var c = Current;
        switch (section)
        {
            case "General":
                c.language = d.language; c.separateLanguages = d.separateLanguages; c.voiceLanguage = d.voiceLanguage;
                c.textLanguage = d.textLanguage; c.subtitles = d.subtitles; c.subtitleScale = d.subtitleScale;
                c.uiScale = d.uiScale; c.confirmQuit = d.confirmQuit;
                break;
            case "Video":
                c.displayMode = d.displayMode; c.width = d.width; c.height = d.height;
                c.vSync = d.vSync; c.frameCap = d.frameCap; c.quality = d.quality;
                break;
            case "Audio":
                c.master = d.master; c.music = d.music; c.effects = d.effects; c.voice = d.voice;
                c.announcer = d.announcer; c.muteInBackground = d.muteInBackground;
                break;
            case "Controls":
                c.swapMouseButtons = d.swapMouseButtons; c.cameraTurnSpeed = d.cameraTurnSpeed;
                c.cameraZoomSpeed = d.cameraZoomSpeed; c.cameraMoveSpeed = d.cameraMoveSpeed;
                GameControls.ResetToDefaults();
                break;
        }
        Apply();
        Save();
    }

    // ── Applying ───────────────────────────────────────────────────────

    /// <summary>Applies everything. display: false leaves the screen mode and size alone (they flicker).</summary>
    public static void Apply(bool display = true)
    {
        if (display) ApplyVideo(); else ApplyGraphics();
        ApplyAudio();
        ApplyUiScale();
        MouseButtons.Swap = Current.swapMouseButtons;
        OnChanged?.Invoke();
    }

    /// <summary>Quality, vsync and frame cap.</summary>
    public static void ApplyGraphics()
    {
        var c = Current;
        if (c.quality >= 0 && c.quality < QualitySettings.names.Length && QualitySettings.GetQualityLevel() != c.quality)
            QualitySettings.SetQualityLevel(c.quality, true);
        QualitySettings.vSyncCount  = c.vSync ? 1 : 0;
        Application.targetFrameRate = c.frameCap > 0 ? c.frameCap : -1;
    }

    /// <summary>Graphics, plus the display mode and resolution.</summary>
    public static void ApplyVideo()
    {
        var c = Current;
        ApplyGraphics();
        if (Application.isEditor) return;   // the editor's Game view sets its own size

        var desktop = Screen.currentResolution;
        // No size chosen: the screen's own — or, windowed, a window a little smaller than it.
        float share = c.displayMode == DisplayMode.Windowed ? 0.8f : 1f;
        int w = c.width  > 0 ? c.width  : Mathf.RoundToInt(desktop.width  * share);
        int h = c.height > 0 ? c.height : Mathf.RoundToInt(desktop.height * share);
        Screen.SetResolution(w, h, ToUnity(c.displayMode));
    }

    public static FullScreenMode ToUnity(DisplayMode mode) => mode switch
    {
        // Exclusive full screen is Windows-only; elsewhere the maximised window is the nearest.
        DisplayMode.FullScreen => Application.platform == RuntimePlatform.WindowsPlayer
                                  ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.MaximizedWindow,
        DisplayMode.Borderless => FullScreenMode.FullScreenWindow,
        _                      => FullScreenMode.Windowed,
    };

    private static bool _focused = true;

    public static void ApplyAudio()
    {
        bool muted = Current.muteInBackground && !_focused;
        AudioListener.volume = muted ? 0f : Mathf.Clamp01(Current.master);
    }

    internal static void SetFocused(bool focused)
    {
        _focused = focused;
        ApplyAudio();
    }

    /// <summary>A category's volume (0–1), before the master volume (which the listener applies).</summary>
    public static float Volume(AudioCategory category) => Mathf.Clamp01(category switch
    {
        AudioCategory.Music     => Current.music,
        AudioCategory.Effects   => Current.effects,
        AudioCategory.Voice     => Current.voice,
        AudioCategory.Announcer => Current.announcer,
        _                       => 1f,
    });

    // ── UI scale ───────────────────────────────────────────────────────

    private static readonly Dictionary<int, Vector2> _baseResolution = new();

    /// <summary>Scales every canvas that scales with the screen (the HUD, menus, research…).</summary>
    public static void ApplyUiScale()
    {
        foreach (var scaler in UnityEngine.Object.FindObjectsByType<CanvasScaler>(FindObjectsInactive.Include))
            ApplyUiScale(scaler);
    }

    public static void ApplyUiScale(CanvasScaler scaler)
    {
        if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return;
        int id = scaler.GetInstanceID();
        if (!_baseResolution.TryGetValue(id, out var original)) _baseResolution[id] = original = scaler.referenceResolution;
        float s = Mathf.Clamp(Current.uiScale, 0.5f, 2f);
        scaler.referenceResolution = original / s;
    }

    // ── Languages ──────────────────────────────────────────────────────

    /// <summary>The language text is shown in (subtitles, names, the research web…).</summary>
    public static string TextLanguage =>
        (Current.separateLanguages ? Current.textLanguage : null) ?? Current.language ?? DefaultLanguage;

    /// <summary>The language narration and announcements are spoken in.</summary>
    public static string VoiceLanguage =>
        (Current.separateLanguages ? Current.voiceLanguage : null) ?? Current.language ?? DefaultLanguage;

    /// <summary>The workbooks' first language — the fallback for anything missing.</summary>
    public static string DefaultLanguage
    {
        get
        {
            var campaigns = CampaignCatalogue.Load();
            if (campaigns != null && !string.IsNullOrEmpty(campaigns.defaultLanguage)) return campaigns.defaultLanguage;
            var announcements = Resources.Load<AnnouncementCatalogue>(AnnouncementCatalogue.ResourcePath);
            if (announcements != null && !string.IsNullOrEmpty(announcements.defaultLanguage)) return announcements.defaultLanguage;
            return "en";
        }
    }

    /// <summary>Every language the imported workbooks have text in (the default first).</summary>
    public static List<string> TextLanguages()
    {
        var found = new List<string> { DefaultLanguage };
        void Add(string lang) { if (!string.IsNullOrEmpty(lang) && !found.Contains(lang)) found.Add(lang); }

        var campaigns = CampaignCatalogue.Load();
        if (campaigns != null)
            foreach (var c in campaigns.campaigns)
            {
                if (c == null) continue;
                foreach (var t in c.names) Add(t?.language);
                foreach (var m in c.missions)
                {
                    if (m == null) continue;
                    foreach (var t in m.preNames)  Add(t?.language);
                    foreach (var t in m.briefings) Add(t?.language);
                    foreach (var l in m.lines) if (l != null) foreach (var t in l.dialogue) Add(t?.language);
                }
            }
        var announcements = Resources.Load<AnnouncementCatalogue>(AnnouncementCatalogue.ResourcePath);
        if (announcements != null)
            foreach (var a in announcements.announcements)
                if (a != null) foreach (var l in a.lines) if (l != null) foreach (var t in l.texts) Add(t?.language);
        var research = ResearchCatalogue.Load();
        if (research != null)
            foreach (var n in research.nodes)
                if (n != null) foreach (var t in n.names) Add(t?.language);
        return found;
    }

    /// <summary>Every language there are recordings in (the default first, even with none).</summary>
    public static List<string> VoiceLanguages()
    {
        var found = new List<string> { DefaultLanguage };
        void Add(string lang) { if (!string.IsNullOrEmpty(lang) && !found.Contains(lang)) found.Add(lang); }

        var campaigns = CampaignCatalogue.Load();
        if (campaigns != null)
            foreach (var c in campaigns.campaigns)
                if (c != null) foreach (var m in c.missions)
                    if (m != null) foreach (var l in m.lines)
                        if (l != null) foreach (var clip in l.clips) if (clip?.clip != null) Add(clip.language);
        var announcements = Resources.Load<AnnouncementCatalogue>(AnnouncementCatalogue.ResourcePath);
        if (announcements != null)
            foreach (var a in announcements.announcements)
                if (a != null) foreach (var l in a.lines)
                    if (l != null) foreach (var clip in l.clips) if (clip?.clip != null) Add(clip.language);
        return found;
    }

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["fr"] = "Français", ["de"] = "Deutsch", ["es"] = "Español", ["it"] = "Italiano",
        ["pt"] = "Português", ["nl"] = "Nederlands", ["pl"] = "Polski", ["sv"] = "Svenska", ["da"] = "Dansk",
        ["no"] = "Norsk", ["fi"] = "Suomi", ["cs"] = "Čeština", ["hu"] = "Magyar", ["tr"] = "Türkçe",
        ["ru"] = "Russian", ["uk"] = "Ukrainian", ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese",
        ["ar"] = "Arabic",
    };

    /// <summary>"Français (fr)", or the code alone for one not in the list.</summary>
    public static string LanguageName(string code) =>
        string.IsNullOrEmpty(code) ? "" : LanguageNames.TryGetValue(code, out string name) ? $"{name} ({code})" : code;
}

/// <summary>Watches window focus for "mute in the background", and survives scene changes.</summary>
public class SettingsRuntime : MonoBehaviour
{
    private void OnApplicationFocus(bool focus) => GameSettings.SetFocused(focus);
}
