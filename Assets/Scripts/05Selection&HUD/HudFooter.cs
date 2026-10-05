using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The HUD footer along the bottom of the screen: tabs (Rooms, Spells,
/// Traps &amp; Doors, Minions…), each a FooterTab, plus minimise and hotkeys.
///
/// Clicking
/// --------
/// Tab-bar buttons switch tabs; the minimise button (or minimiseKey) folds
/// the footer down to just its tab bar.
///
/// Hotkeys — work whether the footer is open or minimised
/// ------------------------------------------------------
///   tab key (e.g. Tab for Rooms) → row digit → column digit
/// selects the button in that slot, exactly as if clicked. Each key must
/// follow the last within sequenceTimeout. At each step a HotkeyPopup shows
/// what the next digit will choose, then fades. Escape, or the same tab key
/// again, cancels; another tab's key restarts on that tab. A digit with
/// nothing behind it is ignored (the popup says so) and the sequence waits.
/// </summary>
public class HudFooter : MonoBehaviour
{
    public static HudFooter Instance { get; private set; }

    [Header("Layout")]
    [SerializeField] private List<FooterTab> tabs = new();
    [Tooltip("The part hidden when minimised — everything except the tab bar.")]
    [SerializeField] private RectTransform body;
    [Tooltip("Stays visible when minimised. Its height becomes the footer's height.")]
    [SerializeField] private RectTransform tabBar;

    [Header("Minimise")]
    [SerializeField] private Button  minimiseButton;
    [Tooltip("Optional key that toggles minimised. None = button only.")]
    [SerializeField] private KeyCode minimiseKey = KeyCode.None;
    [SerializeField] private bool    startMinimised;

    [Header("Tab highlight")]
    [SerializeField] private Color activeTabColour   = new(1f, 0.85f, 0.1f, 1f);
    [SerializeField] private Color inactiveTabColour = Color.white;

    [Header("Hotkeys")]
    [SerializeField] private HotkeyPopup popup;
    [Tooltip("Seconds allowed between each key of a sequence.")]
    [SerializeField] private float sequenceTimeout = 1.5f;
    [Tooltip("Seconds the popup shows what was picked.")]
    [SerializeField] private float confirmHold = 0.6f;

    private RectTransform _rect;
    private float         _expandedHeight;
    private FooterTab     _activeTab;
    private bool          _minimised;

    // Hotkey sequence: _seqTab set while one is in progress; _seqRow 0 = waiting for a row.
    private FooterTab _seqTab;
    private int       _seqRow;
    private float     _deadline;

    public FooterTab ActiveTab   => _activeTab;
    public HotkeyPopup Popup     => popup;

    /// <summary>The tab with this name (e.g. "Spells"), ignoring case, or null.</summary>
    public FooterTab FindTab(string tabName)
    {
        foreach (var t in tabs)
            if (t != null && string.Equals(t.TabName, tabName, System.StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }
    public bool      IsMinimised => _minimised;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        Instance        = this;
        _rect           = (RectTransform)transform;
        _expandedHeight = _rect.sizeDelta.y;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Start()
    {
        foreach (var tab in tabs)
        {
            if (tab == null || tab.TabButton == null) continue;
            var captured = tab;
            tab.TabButton.onClick.AddListener(() => ShowTab(captured));
        }
        if (minimiseButton != null) minimiseButton.onClick.AddListener(ToggleMinimised);

        ShowTab(tabs.Count > 0 ? tabs[0] : null);
        SetMinimised(startMinimised);
    }

    private void Update()
    {
        if (IsTyping()) return;

        if (minimiseKey != KeyCode.None && Input.GetKeyDown(minimiseKey)) ToggleMinimised();

        foreach (var tab in tabs)
        {
            if (tab == null || tab.Hotkey == KeyCode.None || !Input.GetKeyDown(tab.Hotkey)) continue;
            if (_seqTab == tab) CancelSequence();
            else BeginSequence(tab);
            return;
        }

        if (_seqTab == null) return;

        if (Input.GetKeyDown(KeyCode.Escape)) { CancelSequence(); return; }
        if (Time.unscaledTime > _deadline)    { CancelSequence(); return; }

        int digit = DigitDown();
        if (digit > 0) OnDigit(digit);
    }

    // ── Tabs & minimise ────────────────────────────────────────────────

    public void ShowTab(FooterTab tab)
    {
        _activeTab = tab;
        foreach (var t in tabs)
        {
            if (t == null) continue;
            t.gameObject.SetActive(t == tab);
            if (t.TabButton != null && t.TabButton.targetGraphic != null)
                t.TabButton.targetGraphic.color = t == tab ? activeTabColour : inactiveTabColour;
        }
    }

    public void ToggleMinimised() => SetMinimised(!_minimised);

    public void SetMinimised(bool minimised)
    {
        _minimised = minimised;
        if (body != null) body.gameObject.SetActive(!minimised);

        float height = minimised && tabBar != null ? tabBar.rect.height : _expandedHeight;
        _rect.sizeDelta = new Vector2(_rect.sizeDelta.x, height);
    }

    // ── Hotkey sequence ────────────────────────────────────────────────

    private void BeginSequence(FooterTab tab)
    {
        _seqTab = tab;
        _seqRow = 0;
        _deadline = Time.unscaledTime + sequenceTimeout;
        if (!_minimised) ShowTab(tab);

        var rows = tab.Rows();
        if (rows.Count == 0) { Prompt($"<b>{tab.TabName}</b>  nothing to choose yet"); CancelSequence(keepPopup: true); return; }

        var sb = new StringBuilder($"<b>{tab.TabName}</b>  <alpha=#99>row?</alpha>");
        foreach (int r in rows)
        {
            sb.Append($"\n<b>{r}</b>  ");
            var names = new List<string>();
            foreach (var e in tab.RowEntries(r)) names.Add(e.Label);
            sb.Append(string.Join("  ·  ", names));
        }
        Prompt(sb.ToString());
    }

    private void OnDigit(int digit)
    {
        _deadline = Time.unscaledTime + sequenceTimeout;

        if (_seqRow == 0)
        {
            var entries = _seqTab.RowEntries(digit);
            if (entries.Count == 0) { Prompt($"<b>{_seqTab.TabName}</b>  no row {digit}  <alpha=#99>row?</alpha>"); return; }

            _seqRow = digit;
            var sb = new StringBuilder($"<b>{_seqTab.TabName} › {digit}</b>  ");
            foreach (var e in entries) sb.Append($"<b>{e.Column}</b> {e.Label}   ");
            Prompt(sb.ToString().TrimEnd());
            return;
        }

        var entry = _seqTab.Get(_seqRow, digit);
        if (entry == null)
        {
            Prompt($"<b>{_seqTab.TabName} › {_seqRow}</b>  nothing in column {digit}  <alpha=#99>column?</alpha>");
            return;
        }

        string tabName = _seqTab.TabName;
        CancelSequence(keepPopup: true);
        if (popup != null) popup.Show($"<b>{entry.Label}</b>", confirmHold);

        if (entry.Button.interactable) entry.Button.onClick.Invoke();
        else if (popup != null) popup.Show($"<b>{entry.Label}</b>  <alpha=#99>unavailable</alpha>", confirmHold);
        Debug.Log($"[HudFooter] Hotkey selected {tabName} › {entry.Row}·{entry.Column} {entry.Label}");
    }

    private void CancelSequence(bool keepPopup = false)
    {
        _seqTab = null;
        _seqRow = 0;
        if (!keepPopup && popup != null) popup.Hide();
    }

    private void Prompt(string message)
    {
        if (popup != null) popup.Show(message, sequenceTimeout);
    }

    /// <summary>1-9 from the number row or keypad this frame, else 0.</summary>
    private static int DigitDown()
    {
        for (int d = 1; d <= 9; d++)
            if (Input.GetKeyDown(KeyCode.Alpha0 + d) || Input.GetKeyDown(KeyCode.Keypad0 + d))
                return d;
        return 0;
    }

    /// <summary>True while a text field has focus, so typing isn't read as hotkeys.</summary>
    private static bool IsTyping()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && (selected.GetComponent<TMP_InputField>() != null ||
                                    selected.GetComponent<InputField>() != null);
    }
}
