using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The in-game menu: Resume, Save, Load, Settings and Quit to the main menu.
/// Opened with Back (Escape) when nothing else wants it — putting a spell
/// away, closing the research screen and so on come first — or with the
/// Menu button on the HUD header (put opposite the gold display, on
/// HeaderPanel). The game is paused while it's open. Added by GameManager2D.
/// </summary>
public class GameMenu : MonoBehaviour
{
    [Tooltip("The HUD header the Menu button goes on, and the gold display it mirrors. Found by name if empty.")]
    [SerializeField] private RectTransform headerPanel;
    [SerializeField] private RectTransform goldDisplay;

    public static GameMenu Instance { get; private set; }
    /// <summary>The Menu button on the HUD header, once made (null with no header) — the research widget sits beside it.</summary>
    public RectTransform HeaderButton { get; private set; }
    public bool IsOpen => _window != null && _window.gameObject.activeSelf;

    private RectTransform _root, _window, _body, _confirm;
    private Canvas _canvas;
    private TextMeshProUGUI _status, _confirmText;
    private System.Action _confirmYes;
    private bool _wasPaused, _inLoadList;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (IsOpen) GameControls.PopModal();
    }

    private void Start()
    {
        Build();
        AddHeaderButton();
    }

    private void LateUpdate()
    {
        // Settings is on top and answers Back itself; everything else had its chance in Update.
        if (SettingsScreen.IsOpen || GameControls.Rebinding) return;
        if (IsOpen)
        {
            if (!GameControls.TakeBack()) return;
            if (_confirm.gameObject.activeSelf) HideConfirm();
            else if (_inLoadList) ShowMain();
            else Close();
            return;
        }
        if (!GameControls.ModalOpen && GameControls.TakeBack()) Open();
    }

    // ── Opening and closing ────────────────────────────────────────────

    public void Open()
    {
        if (IsOpen || _window == null) return;
        ResearchScreen.Instance?.Close();
        _wasPaused = PauseController.IsPaused;
        FindAnyObjectByType<PauseController>()?.SetPaused(true);
        _window.gameObject.SetActive(true);
        GameControls.PushModal();
        ShowMain();
    }

    public void Close()
    {
        if (!IsOpen) return;
        HideConfirm();
        _window.gameObject.SetActive(false);
        GameControls.PopModal();
        if (!_wasPaused) FindAnyObjectByType<PauseController>()?.SetPaused(false);
    }

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    // ── Building ───────────────────────────────────────────────────────

    private void Build()
    {
        _root   = UiKit.Canvas("GameMenuCanvas", 250, transform);
        _canvas = _root.GetComponent<Canvas>();
        _window = UiKit.Fill(UiKit.Box("Menu", _root, new Color(0f, 0f, 0f, 0.55f)).rectTransform);

        var panel = UiKit.Box("Panel", _window, UiKit.Panel).rectTransform;
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(560f, 720f);

        var title = UiKit.Label("Title", panel, "Menu", 44f, TextAlignmentOptions.Center);
        var tr = title.rectTransform;
        tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f); tr.pivot = new Vector2(0.5f, 1f);
        tr.offsetMin = new Vector2(20f, -80f); tr.offsetMax = new Vector2(-20f, -16f);

        _body = UiKit.Rect("Body", panel);
        UiKit.Fill(_body, 40f, 70f, 40f, 96f);
        UiKit.Column(_body, 14f, 0);

        _status = UiKit.Label("Status", panel, "", 22f, TextAlignmentOptions.Center, UiKit.TextDim);
        var sr = _status.rectTransform;
        sr.anchorMin = Vector2.zero; sr.anchorMax = new Vector2(1f, 0f); sr.pivot = new Vector2(0.5f, 0f);
        sr.offsetMin = new Vector2(20f, 18f); sr.offsetMax = new Vector2(-20f, 58f);

        // Confirmation, over the menu.
        _confirm = UiKit.Fill(UiKit.Box("Confirm", _window, new Color(0f, 0f, 0f, 0.6f)).rectTransform);
        var box = UiKit.Box("Box", _confirm, UiKit.PanelLight).rectTransform;
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(640f, 260f);
        _confirmText = UiKit.Label("Text", box, "", 26f, TextAlignmentOptions.Center);
        UiKit.Fill(_confirmText.rectTransform, 24f, 90f, 24f, 24f);
        var buttons = UiKit.Rect("Buttons", box);
        buttons.anchorMin = Vector2.zero; buttons.anchorMax = new Vector2(1f, 0f); buttons.pivot = new Vector2(0.5f, 0f);
        buttons.offsetMin = new Vector2(60f, 20f); buttons.offsetMax = new Vector2(-60f, 76f);
        UiKit.Row(buttons, 20f);
        UiKit.Button("Yes", buttons, "Yes", () => { var yes = _confirmYes; HideConfirm(); yes?.Invoke(); }, 24f);
        UiKit.Button("No", buttons, "No", HideConfirm, 24f);
        _confirm.gameObject.SetActive(false);

        _window.gameObject.SetActive(false);
    }

    private void ShowMain()
    {
        _inLoadList = false;
        _status.text = "";
        UiKit.Clear(_body);
        Item("Resume", Close);
        Item("Save", Save);
        Item("Load", ShowLoadList);
        Item("Settings", () => SettingsScreen.Open());
        Item("Quit to main menu", () => Ask("Quit to the main menu?\n<size=80%><alpha=#AA>Anything since your last save will be lost.", QuitToMenu));
    }

    private Button Item(string label, System.Action action) =>
        UiKit.Height(UiKit.Button(label.Replace(" ", ""), _body, label, action, 28f), 72f);

    // ── Header button ──────────────────────────────────────────────────

    /// <summary>A Menu button on the HUD header, opposite the gold display.</summary>
    private void AddHeaderButton()
    {
        if (headerPanel == null) headerPanel = FindRect("HeaderPanel");
        if (goldDisplay == null) goldDisplay = FindRect("GoldDisplay");

        if (headerPanel != null)
        {
            var button = UiKit.Button("MenuButton", headerPanel, "Menu", Toggle, 26f);
            var r = (RectTransform)button.transform;
            HeaderButton = r;
            if (goldDisplay != null && goldDisplay.parent == headerPanel)
            {
                // The gold display's mirror image: same size and height, the other side.
                r.anchorMin = new Vector2(1f - goldDisplay.anchorMax.x, goldDisplay.anchorMin.y);
                r.anchorMax = new Vector2(1f - goldDisplay.anchorMin.x, goldDisplay.anchorMax.y);
                r.pivot     = new Vector2(1f - goldDisplay.pivot.x, goldDisplay.pivot.y);
                r.sizeDelta = goldDisplay.sizeDelta;
                r.anchoredPosition = new Vector2(-goldDisplay.anchoredPosition.x, goldDisplay.anchoredPosition.y);
            }
            else
            {
                r.anchorMin = r.anchorMax = r.pivot = new Vector2(1f, 0.5f);
                r.sizeDelta = new Vector2(200f, 50f);
                r.anchoredPosition = new Vector2(-24f, 0f);
            }
            return;
        }

        // No header in this scene: a button of its own, top right.
        var own = UiKit.Button("MenuButton", _root, "Menu", Toggle, 24f);
        var o = (RectTransform)own.transform;
        o.anchorMin = o.anchorMax = o.pivot = new Vector2(1f, 1f);
        o.sizeDelta = new Vector2(160f, 50f);
        o.anchoredPosition = new Vector2(-16f, -80f);
        own.transform.SetAsFirstSibling();   // under the menu itself
    }

    private static RectTransform FindRect(string name)
    {
        foreach (var t in FindObjectsByType<RectTransform>(FindObjectsInactive.Include))
            if (t.name == name) return t;
        return null;
    }

    // ── Save ───────────────────────────────────────────────────────────

    private void Save()
    {
        if (GameManager2D.Instance == null) return;
        StartCoroutine(SaveWithMenuHidden());
    }

    /// <summary>Hides the menu for the frame the save's thumbnail is taken, so the picture is of the game.</summary>
    private IEnumerator SaveWithMenuHidden()
    {
        _canvas.enabled = false;
        var trigger = FindAnyObjectByType<SaveTrigger>();
        if (trigger != null) trigger.TriggerSave();
        else GameManager2D.Instance.Save();
        yield return new WaitForEndOfFrame();
        yield return null;
        yield return null;
        _canvas.enabled = true;
        _status.text = "Saved.";
    }

    // ── Load ───────────────────────────────────────────────────────────

    private void ShowLoadList()
    {
        _inLoadList = true;
        _status.text = "";
        UiKit.Clear(_body);
        var gm = GameManager2D.Instance;
        var manifest = gm != null ? SaveLoadSystem.LoadManifest(gm.ActiveSlot) : null;

        var saves = new List<SaveEntry>();
        if (manifest != null)
        {
            foreach (var e in manifest.saveEntries)
                if (e != null && !e.isMissing && e.levelId == gm.ActiveLevelId) saves.Add(e);
            if (manifest.autosaveEntry != null && !manifest.autosaveEntry.isMissing && manifest.autosaveEntry.levelId == gm.ActiveLevelId)
                saves.Add(manifest.autosaveEntry);
        }
        saves.Sort((a, b) => string.CompareOrdinal(b.dateTime, a.dateTime));   // ISO dates: newest first

        var list = UiKit.ScrollList("Saves", _body);
        list.parent.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
        if (saves.Count == 0)
            UiKit.Height(UiKit.Label("None", list, "<alpha=#99>No saves of this mission yet.", 22f, TextAlignmentOptions.Center), 60f);
        foreach (var entry in saves)
        {
            var e = entry;
            UiKit.Height(UiKit.Button("Save", list, Describe(e), () =>
                Ask("Load this save?\n<size=80%><alpha=#AA>Anything since your last save will be lost.", () => Load(e)), 20f), 64f);
        }
        Item("Back", ShowMain);
    }

    private static string Describe(SaveEntry e)
    {
        string name = e.isAutosave ? "Autosave" : !string.IsNullOrWhiteSpace(e.displayName) ? e.displayName : $"Save {e.saveIndex + 1}";
        string when = System.DateTime.TryParse(e.dateTime, out var t) ? t.ToLocalTime().ToString("d MMM  HH:mm") : e.dateTime;
        return $"{name}   <alpha=#99><size=85%>{when}   ·   {e.goldAtSave}g";
    }

    private void Load(SaveEntry e)
    {
        var gm = GameManager2D.Instance;
        if (gm == null) return;
        GameplaySceneArgs.SlotIndex       = gm.ActiveSlot;
        GameplaySceneArgs.BranchId        = e.branchId;
        GameplaySceneArgs.SaveIndex       = e.saveIndex;
        GameplaySceneArgs.LevelId         = e.levelId;
        GameplaySceneArgs.IsNewGame       = false;
        GameplaySceneArgs.LoadAutosave    = e.isAutosave;
        GameplaySceneArgs.LevelFromFile   = gm.LevelFromFile;
        GameplaySceneArgs.Origin          = gm.Origin;
        GameplaySceneArgs.CampaignId      = gm.CampaignId;
        GameplaySceneArgs.MissionId       = gm.MissionId;
        GameplaySceneArgs.PlayerFactionId = gm.PlayerFactionId;
        LeaveTo(SceneNames.Gameplay);
    }

    // ── Quit ───────────────────────────────────────────────────────────

    private void QuitToMenu()
    {
        MissionReturn.Clear();
        LeaveTo(SceneNames.Start);
    }

    private void LeaveTo(string scene)
    {
        if (!Application.CanStreamedLevelBeLoaded(scene))
        {
            _status.text = $"The {scene} scene isn't in Build Settings.";
            return;
        }
        FindAnyObjectByType<PauseController>()?.SetPaused(false);
        Time.timeScale = 1f;
        GameControls.PopModal();
        _window.gameObject.SetActive(false);
        SceneManager.LoadScene(scene);
    }

    // ── Confirmation ───────────────────────────────────────────────────

    /// <summary>Asks first (if the player wants to be asked), then does it.</summary>
    private void Ask(string question, System.Action yes)
    {
        if (!GameSettings.Current.confirmQuit) { yes(); return; }
        _confirmText.text = question;
        _confirmYes = yes;
        _confirm.gameObject.SetActive(true);
        _confirm.SetAsLastSibling();
    }

    private void HideConfirm()
    {
        _confirmYes = null;
        if (_confirm != null) _confirm.gameObject.SetActive(false);
    }
}
