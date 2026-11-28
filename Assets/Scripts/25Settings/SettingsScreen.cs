using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The Settings screen — General, Video, Audio and Controls tabs — opened
/// from the Start menu or the in-game menu with SettingsScreen.Open().
/// Changes apply at once and are saved (GameSettings); a change of display
/// mode or resolution asks to be kept, and reverts by itself after a few
/// seconds otherwise. Built in code with UiKit, like the other screens.
/// </summary>
public class SettingsScreen : MonoBehaviour
{
    private const float RevertSeconds = 12f;
    private static readonly string[] Tabs = { "General", "Video", "Audio", "Controls" };

    public static bool IsOpen => _instance != null;
    private static SettingsScreen _instance;

    private Action _onClosed;
    private string _tab = "General";
    private RectTransform _root, _list, _tabRow, _confirm;
    private TextMeshProUGUI _confirmText;
    private float _revertAt = -1f;
    private (DisplayMode mode, int w, int h) _revertTo;
    private InputActionRebindingExtensions.RebindingOperation _rebind;

    /// <summary>Opens Settings over whatever is showing; onClosed runs when it's closed.</summary>
    public static void Open(Action onClosed = null)
    {
        if (_instance != null) return;
        var go = new GameObject("SettingsScreen");
        _instance = go.AddComponent<SettingsScreen>();
        _instance._onClosed = onClosed;
        _instance.Build();
        GameControls.PushModal();
    }

    public void Close()
    {
        if (_revertAt >= 0f) Revert();
        _rebind?.Cancel();
        GameSettings.Save();
        GameControls.PopModal();
        _instance = null;
        Destroy(gameObject);
        _onClosed?.Invoke();
    }

    private void OnDestroy()
    {
        if (_instance == this) { _instance = null; GameControls.PopModal(); }
    }

    private void Update()
    {
        if (GameControls.Rebinding) return;
        if (_revertAt >= 0f)
        {
            float left = _revertAt - Time.unscaledTime;
            if (left <= 0f) { Revert(); return; }
            _confirmText.text = $"Keep these display settings?\n<size=80%><alpha=#AA>Going back to the old ones in {Mathf.CeilToInt(left)}…";
            if (GameControls.TakeBack()) Revert();
            return;
        }
        if (GameControls.TakeBack()) Close();
    }

    // ── Building ───────────────────────────────────────────────────────

    private void Build()
    {
        _root = UiKit.Canvas("SettingsCanvas", 300, transform);
        UiKit.Fill(UiKit.Box("Dim", _root, new Color(0f, 0f, 0f, 0.6f)).rectTransform);

        var panel = UiKit.Box("Panel", _root, UiKit.Panel).rectTransform;
        UiKit.Anchor(panel, 0.14f, 0.07f, 0.86f, 0.93f);

        var title = UiKit.Label("Title", panel, "Settings", 44f, TextAlignmentOptions.Left);
        var tr = title.rectTransform;
        tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f); tr.pivot = new Vector2(0.5f, 1f);
        tr.offsetMin = new Vector2(28f, -70f); tr.offsetMax = new Vector2(-28f, -12f);

        _tabRow = UiKit.Rect("Tabs", panel);
        _tabRow.anchorMin = new Vector2(0f, 1f); _tabRow.anchorMax = new Vector2(1f, 1f); _tabRow.pivot = new Vector2(0.5f, 1f);
        _tabRow.offsetMin = new Vector2(24f, -132f); _tabRow.offsetMax = new Vector2(-24f, -78f);
        UiKit.Row(_tabRow, 8f);

        var content = UiKit.Rect("Content", panel);
        UiKit.Fill(content, 24f, 84f, 24f, 142f);
        _list = UiKit.ScrollList("List", content);
        UiKit.Fill((RectTransform)_list.parent);

        var bottom = UiKit.Rect("Bottom", panel);
        bottom.anchorMin = Vector2.zero; bottom.anchorMax = new Vector2(1f, 0f); bottom.pivot = new Vector2(0.5f, 0f);
        bottom.offsetMin = new Vector2(24f, 18f); bottom.offsetMax = new Vector2(-24f, 70f);
        var row = UiKit.Row(bottom, 12f);
        row.childForceExpandWidth = false;
        UiKit.Width(UiKit.Button("Reset", bottom, "Reset this tab", () => { GameSettings.ResetSection(_tab); ShowTab(_tab); }, 22f), 240f);
        var spacer = UiKit.Rect("Spacer", bottom);
        spacer.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        UiKit.Width(UiKit.Button("Back", bottom, "Back", Close, 24f), 200f);

        BuildConfirm();
        ShowTab(_tab);
    }

    private void BuildConfirm()
    {
        _confirm = UiKit.Fill(UiKit.Box("Confirm", _root, new Color(0f, 0f, 0f, 0.7f)).rectTransform);
        var box = UiKit.Box("Box", _confirm, UiKit.PanelLight).rectTransform;
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(640f, 260f);
        _confirmText = UiKit.Label("Text", box, "", 28f, TextAlignmentOptions.Center);
        UiKit.Fill(_confirmText.rectTransform, 24f, 90f, 24f, 24f);
        var buttons = UiKit.Rect("Buttons", box);
        buttons.anchorMin = Vector2.zero; buttons.anchorMax = new Vector2(1f, 0f); buttons.pivot = new Vector2(0.5f, 0f);
        buttons.offsetMin = new Vector2(60f, 20f); buttons.offsetMax = new Vector2(-60f, 76f);
        UiKit.Row(buttons, 20f);
        UiKit.Button("Keep", buttons, "Keep", Keep, 24f);
        UiKit.Button("Revert", buttons, "Go back", Revert, 24f);
        _confirm.gameObject.SetActive(false);
    }

    private void ShowTab(string tab)
    {
        _tab = tab;
        UiKit.Clear(_tabRow);
        foreach (var t in Tabs)
        {
            string name = t;
            UiKit.Style(UiKit.Button(t, _tabRow, t, () => ShowTab(name), 24f), t == _tab);
        }
        UiKit.Clear(_list);
        switch (tab)
        {
            case "General":  BuildGeneral();  break;
            case "Video":    BuildVideo();    break;
            case "Audio":    BuildAudio();    break;
            case "Controls": BuildControls(); break;
        }
    }

    // ── Rows ───────────────────────────────────────────────────────────

    /// <summary>A labelled row; returns the area on the right for its control.</summary>
    private RectTransform Row(string label, string hint = null)
    {
        var row = UiKit.Height(UiKit.Rect(label, _list), hint != null ? 64f : 52f);
        var layout = UiKit.Row(row, 16f);
        layout.childForceExpandWidth = false;
        var text = UiKit.Label("Label", row, hint != null ? $"{label}\n<size=70%><alpha=#99>{hint}" : label, 24f);
        text.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        var control = UiKit.Width(UiKit.Rect("Control", row), 460f);
        return control;
    }

    private void Heading(string text) =>
        UiKit.Height(UiKit.Label($"Heading_{text}", _list, $"<b>{text}</b>", 26f, TextAlignmentOptions.BottomLeft, UiKit.ButtonOn), 50f);

    private void Note(string text) =>
        UiKit.Height(UiKit.Label("Note", _list, $"<alpha=#99>{text}", 19f), 34f);

    private void Choice(string label, IReadOnlyList<string> options, int index, Action<int> set, string hint = null)
    {
        var area = Row(label, hint);
        UiKit.Fill(UiKit.Choice("Choice", area, options, index, set).transform.parent as RectTransform);
    }

    private void OnOff(string label, bool value, Action<bool> set, string hint = null)
    {
        var area = Row(label, hint);
        var b = UiKit.OnOff("Toggle", area, value, set);
        var r = (RectTransform)b.transform;
        r.anchorMin = new Vector2(0f, 0.1f); r.anchorMax = new Vector2(0.4f, 0.9f);
        r.offsetMin = r.offsetMax = Vector2.zero;
    }

    private void Percent(string label, float value, Action<float> set, float min = 0f, float max = 1f, bool multiplier = false)
    {
        var area = Row(label);
        var valueLabel = UiKit.Label("Value", area, "", 22f, TextAlignmentOptions.Right);
        string Show(float v) => multiplier ? $"×{v:0.00}" : $"{Mathf.RoundToInt(v * 100f)}%";
        valueLabel.text = Show(value);
        var vr = valueLabel.rectTransform;
        vr.anchorMin = new Vector2(0.8f, 0f); vr.anchorMax = Vector2.one; vr.offsetMin = vr.offsetMax = Vector2.zero;
        var slider = UiKit.Slider("Slider", area, min, max, value, v => { valueLabel.text = Show(v); set(v); });
        var sr = (RectTransform)slider.transform;
        sr.anchorMin = Vector2.zero; sr.anchorMax = new Vector2(0.77f, 1f); sr.offsetMin = sr.offsetMax = Vector2.zero;
    }

    /// <summary>Applies a change at once (saved when Settings closes).</summary>
    private static void Changed() => GameSettings.Apply(display: false);

    // ── General ────────────────────────────────────────────────────────

    private void BuildGeneral()
    {
        var c = GameSettings.Current;
        var text  = GameSettings.TextLanguages();
        var voice = GameSettings.VoiceLanguages();
        var all   = text.Union(voice).ToList();

        Heading("Language");
        Choice("Language", all.Select(GameSettings.LanguageName).ToList(),
               Mathf.Max(0, all.IndexOf(c.language ?? GameSettings.DefaultLanguage)),
               i => { c.language = all[i]; Changed(); });
        OnOff("Separate voice and text languages", c.separateLanguages, on =>
        {
            c.separateLanguages = on;
            if (on) { c.voiceLanguage ??= c.language; c.textLanguage ??= c.language; }
            Changed();
            ShowTab("General");
        }, "e.g. voices in one language, subtitles in another");
        if (c.separateLanguages)
        {
            Choice("Voice language", voice.Select(GameSettings.LanguageName).ToList(),
                   Mathf.Max(0, voice.IndexOf(GameSettings.VoiceLanguage)), i => { c.voiceLanguage = voice[i]; Changed(); });
            Choice("Text and subtitle language", text.Select(GameSettings.LanguageName).ToList(),
                   Mathf.Max(0, text.IndexOf(GameSettings.TextLanguage)), i => { c.textLanguage = text[i]; Changed(); });
        }
        Note("Languages are the ones the game's text and recordings come in. Anything missing in a language falls back to " +
             GameSettings.LanguageName(GameSettings.DefaultLanguage) + ".");

        Heading("Subtitles and interface");
        OnOff("Subtitles", c.subtitles, on => { c.subtitles = on; Changed(); }, "for narration");
        float[] sizes = { 0.8f, 1f, 1.3f, 1.6f };
        Choice("Subtitle size", new[] { "Small", "Medium", "Large", "Very large" }, Nearest(sizes, c.subtitleScale),
               i => { c.subtitleScale = sizes[i]; Changed(); });
        float[] scales = { 0.8f, 0.9f, 1f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f };
        Choice("Interface size", scales.Select(s => $"{Mathf.RoundToInt(s * 100f)}%").ToList(), Nearest(scales, c.uiScale),
               i => { c.uiScale = scales[i]; Changed(); });

        Heading("Game");
        OnOff("Ask before quitting a mission", c.confirmQuit, on => { c.confirmQuit = on; Changed(); });
    }

    private static int Nearest(float[] values, float v)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (Mathf.Abs(values[i] - v) < Mathf.Abs(values[best] - v)) best = i;
        return best;
    }

    // ── Video ──────────────────────────────────────────────────────────

    private void BuildVideo()
    {
        var c = GameSettings.Current;
        Heading("Display");
        var modes = new[] { DisplayMode.FullScreen, DisplayMode.Borderless, DisplayMode.Windowed };
        Choice("Display mode", new[] { "Full screen", "Borderless window", "Windowed" }, Array.IndexOf(modes, c.displayMode),
               i => ChangeDisplay(modes[i], c.width, c.height));

        var sizes = Resolutions();
        int cw = c.width > 0 ? c.width : Screen.width, ch = c.height > 0 ? c.height : Screen.height;
        int index = sizes.FindIndex(s => s.x == cw && s.y == ch);
        if (index < 0) { sizes.Insert(0, new Vector2Int(cw, ch)); index = 0; }
        Choice("Resolution", sizes.Select(s => $"{s.x} × {s.y}").ToList(), index,
               i => ChangeDisplay(c.displayMode, sizes[i].x, sizes[i].y));
        if (Application.isEditor) Note("In the Unity editor, display mode and resolution follow the Game view instead.");

        OnOff("Vertical sync", c.vSync, on => { c.vSync = on; Changed(); }, "no tearing; caps the frame rate to the screen's");
        int[] caps = { 0, 30, 60, 90, 120, 144, 165, 240 };
        Choice("Frame-rate limit", caps.Select(f => f == 0 ? "None" : $"{f} fps").ToList(),
               Mathf.Max(0, Array.IndexOf(caps, c.frameCap)), i => { c.frameCap = caps[i]; Changed(); });

        Heading("Graphics");
        var names = QualitySettings.names;
        if (names.Length > 0)
            Choice("Quality", names, c.quality >= 0 ? Mathf.Clamp(c.quality, 0, names.Length - 1) : QualitySettings.GetQualityLevel(),
                   i => { c.quality = i; Changed(); });
    }

    private static List<Vector2Int> Resolutions()
    {
        var list = new List<Vector2Int>();
        foreach (var r in Screen.resolutions)
        {
            var s = new Vector2Int(r.width, r.height);
            if (!list.Contains(s)) list.Add(s);
        }
        list.Sort((a, b) => b.x != a.x ? b.x.CompareTo(a.x) : b.y.CompareTo(a.y));
        return list;
    }

    /// <summary>Applies a new display mode or size, then asks to keep it (reverting by itself if not).</summary>
    private void ChangeDisplay(DisplayMode mode, int w, int h)
    {
        var c = GameSettings.Current;
        if (_revertAt < 0f) _revertTo = (c.displayMode, c.width, c.height);
        c.displayMode = mode; c.width = w; c.height = h;
        GameSettings.ApplyVideo();
        _revertAt = Time.unscaledTime + RevertSeconds;
        _confirm.gameObject.SetActive(true);
        _confirm.SetAsLastSibling();
    }

    private void Keep()
    {
        _revertAt = -1f;
        _confirm.gameObject.SetActive(false);
        GameSettings.Save();
    }

    private void Revert()
    {
        var c = GameSettings.Current;
        (c.displayMode, c.width, c.height) = _revertTo;
        GameSettings.ApplyVideo();
        _revertAt = -1f;
        _confirm.gameObject.SetActive(false);
        if (_tab == "Video") ShowTab("Video");
    }

    // ── Audio ──────────────────────────────────────────────────────────

    private void BuildAudio()
    {
        var c = GameSettings.Current;
        Heading("Volume");
        Percent("Master", c.master, v => { c.master = v; GameSettings.ApplyAudio(); Changed(); });
        Percent("Music", c.music, v => { c.music = v; Changed(); });
        Percent("Sound effects", c.effects, v => { c.effects = v; Changed(); });
        Percent("Narration", c.voice, v => { c.voice = v; Changed(); });
        Percent("Announcer", c.announcer, v => { c.announcer = v; Changed(); });
        OnOff("Mute when the game is in the background", c.muteInBackground, on => { c.muteInBackground = on; Changed(); });
    }

    // ── Controls ───────────────────────────────────────────────────────

    private void BuildControls()
    {
        var c = GameSettings.Current;
        Heading("Mouse and camera");
        OnOff("Swap left and right mouse buttons", c.swapMouseButtons, on => { c.swapMouseButtons = on; Changed(); });
        Percent("Camera turn speed", c.cameraTurnSpeed, v => { c.cameraTurnSpeed = v; Changed(); }, 0.25f, 3f, multiplier: true);
        Percent("Camera zoom speed", c.cameraZoomSpeed, v => { c.cameraZoomSpeed = v; Changed(); }, 0.25f, 3f, multiplier: true);
        Percent("Camera move speed", c.cameraMoveSpeed, v => { c.cameraMoveSpeed = v; Changed(); }, 0.25f, 3f, multiplier: true);

        var clashes = GameControls.Clashes();
        foreach (var (a, b, key) in clashes)
            Note($"<color=#E07A66>{GameControls.LabelOf(a)} and {GameControls.LabelOf(b)} share {Pretty(key)} — change one.</color>");

        foreach (var map in GameControls.Asset.actionMaps)
        {
            Heading(map.name == "LevelEditor" ? "Level editor" : map.name == "HUD" ? "HUD" : map.name);
            foreach (var action in map.actions) KeyRows(action);
        }
        Note("Click a key, then press the new one. Escape cancels.");
    }

    private static string Pretty(string path) => path.Replace("<Keyboard>/", "").Replace("<Mouse>/", "mouse ");

    /// <summary>Rows for an action: one per direction for movement, else one for its main key.</summary>
    private void KeyRows(InputAction action)
    {
        var bindings = action.bindings;
        string label = GameControls.LabelOf(action);
        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            if (b.isComposite && b.path == "2DVector")
            {
                for (int j = i + 1; j < bindings.Count && bindings[j].isPartOfComposite; j++)
                    KeyRow($"{label} — {bindings[j].name}", action, j, action.GetBindingDisplayString(j));
                return;
            }
            if (b.isComposite)
            {
                // Ctrl+Z style: the key part changes; the modifier stays.
                int key = i + 1;
                while (key + 1 < bindings.Count && bindings[key + 1].isPartOfComposite) key++;
                KeyRow(label, action, key, action.GetBindingDisplayString(i));
                return;
            }
            if (!b.isPartOfComposite)
            {
                KeyRow(label, action, i, action.GetBindingDisplayString(i));
                return;
            }
        }
        KeyRow(label, action, -1, "—");   // no key yet
    }

    private void KeyRow(string label, InputAction action, int bindingIndex, string shown)
    {
        var area = Row(label);
        bool canChange = GameControls.IsRebindable(action);
        Button button = null;
        button = UiKit.Button("Key", area, string.IsNullOrEmpty(shown) ? "—" : shown, () =>
        {
            UiKit.LabelOf(button).text = "Press a key…";
            StartRebind(action, bindingIndex);
        }, 22f);
        UiKit.Fill((RectTransform)button.transform, 0f, 4f, 0f, 4f);
        UiKit.Style(button, false, canChange);
    }

    private void StartRebind(InputAction action, int bindingIndex)
    {
        if (bindingIndex < 0)
        {
            action.AddBinding("");   // a key for an action that has none
            bindingIndex = action.bindings.Count - 1;
        }
        bool wasEnabled = action.enabled;
        action.Disable();
        GameControls.Rebinding = true;
        void Finish(InputActionRebindingExtensions.RebindingOperation op)
        {
            op.Dispose();
            _rebind = null;
            if (wasEnabled) action.Enable();
            GameControls.EndRebinding();
            GameControls.Save();
            if (this != null) ShowTab("Controls");
        }
        _rebind = action.PerformInteractiveRebinding(bindingIndex)
            .WithControlsExcluding("<Mouse>")
            .WithCancelingThrough("<Keyboard>/escape")
            .OnMatchWaitForAnother(0.1f)
            .OnComplete(Finish)
            .OnCancel(Finish)
            .Start();
    }
}
