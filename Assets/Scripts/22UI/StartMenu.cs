using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// The Start scene's main menu: Continue Game, New Game, Load Game,
/// Skirmish, Level Editor, Settings, Credits and Exit.
///
/// It wires the buttons already in the scene by name — ContinueButton (or
/// ResumeButton), NewGameButton (or CampaignButton), LoadGameButton,
/// SkirmishButton, LevelEditorButton, SettingsButton, CreditsButton,
/// ExitButton — and sets their labels. With no LoadGameButton it adds one
/// (a copy of New Game) and moves the buttons below down a place.
///
/// New Game and Load Game open panels built in code (placeholders until
/// they have art). New Game: pick a faction, a campaign (the faction's own
/// by default) and a save slot. Strategic mode is shown but not built yet.
///
/// Scene setup: none — SceneScreens adds one to the Start scene.
/// </summary>
public class StartMenu : MonoBehaviour
{
    private enum Item { Continue, NewGame, LoadGame, Skirmish, LevelEditor, Settings, Credits, Exit }

    private static readonly (Item item, string label, string[] names)[] Items =
    {
        (Item.Continue,    "Continue Game", new[] { "ContinueButton", "ContinueGameButton", "ResumeButton" }),
        (Item.NewGame,     "New Game",      new[] { "NewGameButton", "CampaignButton" }),
        (Item.LoadGame,    "Load Game",     new[] { "LoadGameButton", "LoadButton" }),
        (Item.Skirmish,    "Skirmish",      new[] { "SkirmishButton" }),
        (Item.LevelEditor, "Level Editor",  new[] { "LevelEditorButton" }),
        (Item.Settings,    "Settings",      new[] { "SettingsButton" }),
        (Item.Credits,     "Credits",       new[] { "CreditsButton" }),
        (Item.Exit,        "Exit",          new[] { "ExitButton" }),
    };

    private readonly Dictionary<Item, Button> _buttons = new();
    private RectTransform   _overlay;
    private TextMeshProUGUI _status;
    private float           _statusUntil;

    private CampaignCatalogue _catalogue;

    // New Game choices.
    private string _faction, _campaign;
    private int    _slot = -1;
    private bool   _confirmOverwrite;

    private void Start()
    {
        _catalogue = CampaignCatalogue.Load();
        FindButtons();
        AddMissingLoadButton();
        foreach (var pair in _buttons)
        {
            var item = pair.Key;
            pair.Value.onClick.RemoveAllListeners();
            pair.Value.onClick.AddListener(() => Choose(item));
            SetLabel(pair.Value, LabelOf(item));
        }
        if (_buttons.TryGetValue(Item.Continue, out var cont)) cont.interactable = CampaignSession.LatestSlot() >= 0;

        _overlay = UiKit.Canvas("StartMenuPanels", 100, transform);
        _status  = UiKit.Label("Status", _overlay, "", 24f, TextAlignmentOptions.Bottom, UiKit.TextDim);
        UiKit.Anchor(_status.rectTransform, 0f, 0f, 1f, 0.08f);
    }

    private void Update()
    {
        // Escape closes the open panel (New Game, Load Game) — back to the menu.
        if (Input.GetKeyDown(KeyCode.Escape) && _overlay != null && _overlay.Find("Panel") != null) ClosePanel();

        if (_status != null && _status.text.Length > 0 && Time.unscaledTime > _statusUntil) _status.text = "";
    }

    private static string LabelOf(Item item)
    {
        foreach (var entry in Items) if (entry.item == item) return entry.label;
        return item.ToString();
    }

    // ── Wiring the scene's buttons ─────────────────────────────────────

    private void FindButtons()
    {
        var all = FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var (item, _, names) in Items)
            foreach (var name in names)
            {
                var found = System.Array.Find(all, b => b.name == name);
                if (found != null) { _buttons[item] = found; break; }
            }
    }

    /// <summary>No Load Game button in the scene: copy New Game's, put it next, and move the ones below down a place.</summary>
    private void AddMissingLoadButton()
    {
        if (_buttons.ContainsKey(Item.LoadGame) || !_buttons.TryGetValue(Item.NewGame, out var newGame)) return;

        var copy = Instantiate(newGame, newGame.transform.parent);
        copy.name = "LoadGameButton";
        copy.transform.SetSiblingIndex(newGame.transform.GetSiblingIndex() + 1);
        _buttons[Item.LoadGame] = copy;

        // Buttons placed by hand: shift Load and everything after it down one step.
        if (newGame.transform.parent.GetComponent<LayoutGroup>() != null) return;
        float step = 0f;
        if (_buttons.TryGetValue(Item.Continue, out var first))
            step = ((RectTransform)newGame.transform).anchoredPosition.y - ((RectTransform)first.transform).anchoredPosition.y;
        if (Mathf.Approximately(step, 0f)) step = -((RectTransform)newGame.transform).rect.height * 1.1f;

        bool after = false;
        foreach (var (item, _, _) in Items)
        {
            if (item == Item.LoadGame) after = true;
            if (!after || item == Item.Exit || !_buttons.TryGetValue(item, out var b)) continue;
            var rect = (RectTransform)b.transform;
            var prev = item == Item.LoadGame ? (RectTransform)newGame.transform : null;
            rect.anchoredPosition = prev != null ? prev.anchoredPosition + new Vector2(0f, step) : rect.anchoredPosition + new Vector2(0f, step);
        }
    }

    private static void SetLabel(Button button, string text)
    {
        var tmp = button.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null) { tmp.text = text; return; }
        var legacy = button.GetComponentInChildren<Text>(true);
        if (legacy != null) legacy.text = text;
    }

    private void Say(string message)
    {
        if (_status == null) return;
        _status.text = message;
        _statusUntil = Time.unscaledTime + 4f;
    }

    // ── Menu actions ───────────────────────────────────────────────────

    private void Choose(Item item)
    {
        switch (item)
        {
            case Item.Continue:
                int latest = CampaignSession.LatestSlot();
                if (latest >= 0 && CampaignSession.Open(latest)) GoToCampaign();
                else Say("There's no game to continue yet.");
                break;
            case Item.NewGame:     OpenNewGame(); break;
            case Item.LoadGame:    OpenLoadGame(); break;
            case Item.Skirmish:    Load(SceneNames.Skirmish, "Skirmish isn't built yet."); break;
            case Item.LevelEditor: Load(SceneNames.LevelEditor, "The LevelEditor scene isn't in Build Settings."); break;
            case Item.Settings:    Say("Settings aren't built yet."); break;
            case Item.Credits:     Say("Credits aren't built yet."); break;
            case Item.Exit:
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#endif
                Application.Quit();
                break;
        }
    }

    private void Load(string scene, string missing)
    {
        if (Application.CanStreamedLevelBeLoaded(scene)) SceneManager.LoadScene(scene);
        else Say(missing);
    }

    private void GoToCampaign() =>
        Load(SceneNames.Campaign, "The Campaign scene isn't in Build Settings.");

    private void ClosePanel()
    {
        var panel = _overlay.Find("Panel");
        if (panel != null) Destroy(panel.gameObject);
    }

    private RectTransform NewPanel(string title)
    {
        ClosePanel();
        var panel = UiKit.Box("Panel", _overlay, UiKit.Panel).rectTransform;
        panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(1500f, 860f);
        panel.SetSiblingIndex(0);   // under the status line
        var heading = UiKit.Label("Title", panel, title, 48f, TextAlignmentOptions.Center);
        heading.rectTransform.anchorMin = new Vector2(0f, 1f);
        heading.rectTransform.anchorMax = new Vector2(1f, 1f);
        heading.rectTransform.pivot     = new Vector2(0.5f, 1f);
        heading.rectTransform.sizeDelta = new Vector2(0f, 80f);
        heading.rectTransform.anchoredPosition = new Vector2(0f, -10f);
        return panel;
    }

    private static RectTransform Column(RectTransform panel, string heading, float xMin, float xMax)
    {
        var label = UiKit.Label(heading, panel, heading, 26f, TextAlignmentOptions.BottomLeft, UiKit.TextDim);
        UiKit.Anchor(label.rectTransform, xMin, 0.82f, xMax, 0.88f, 14f);
        var list = UiKit.ScrollList(heading + "List", panel);
        UiKit.Anchor((RectTransform)list.parent, xMin, 0.14f, xMax, 0.82f, 14f);
        return list;
    }

    private static Button Footer(RectTransform panel, string label, System.Action action, float xMin, float xMax)
    {
        var b = UiKit.Button(label, panel, label, action, 28f);
        UiKit.Anchor((RectTransform)b.transform, xMin, 0.03f, xMax, 0.11f, 10f);
        return b;
    }

    // ── New Game ───────────────────────────────────────────────────────

    private void OpenNewGame()
    {
        if (_catalogue == null) { Say("No campaigns yet — run Sunder > Import Campaigns in Unity."); return; }
        _faction ??= FirstFaction();
        _campaign ??= _catalogue.Find(_faction) != null ? _faction : _catalogue.campaigns.Count > 0 ? _catalogue.campaigns[0].id : null;
        if (_slot < 0) _slot = FirstEmptySlot();
        _confirmOverwrite = false;
        DrawNewGame();
    }

    private List<(string id, string name)> Factions()
    {
        var list = new List<(string, string)>();
        var registry = _catalogue.factionRegistry;
        if (registry != null)
            foreach (var def in registry.Definitions)
            {
                if (def == null || string.IsNullOrEmpty(def.factionContentId)) continue;
                if (!def.factionContentId.EndsWith("U", System.StringComparison.OrdinalIgnoreCase)) continue;   // Underworld factions
                string name = string.IsNullOrWhiteSpace(def.displayName) || def.displayName == def.factionContentId
                    ? def.factionContentId : $"{def.displayName} ({def.factionContentId})";
                list.Add((def.factionContentId, name));
            }
        if (list.Count == 0)
            foreach (var c in _catalogue.campaigns) list.Add((c.id, c.id));
        return list;
    }

    private string FirstFaction()
    {
        var f = Factions();
        return f.Count > 0 ? f[0].id : null;
    }

    private static int FirstEmptySlot()
    {
        var slots = CampaignSession.AllSlots();
        for (int i = 0; i < slots.Length; i++) if (slots[i] == null) return i;
        return 0;
    }

    private void DrawNewGame()
    {
        var panel = NewPanel("New Game");
        string lang = CampaignSession.Language, fallback = _catalogue.defaultLanguage;

        var factions = Column(panel, "Play as", 0f, 0.3f);
        foreach (var (id, name) in Factions())
        {
            string fid = id;
            var b = UiKit.Height(UiKit.Button(id, factions, name, () =>
            {
                _faction = fid;
                if (_catalogue.Find(fid) != null) _campaign = fid;   // the faction's own campaign by default
                DrawNewGame();
            }, 22f), 44f);
            UiKit.Style(b, id == _faction);
        }

        var campaigns = Column(panel, "Campaign", 0.3f, 0.62f);
        var mode = UiKit.Rect("Mode", campaigns);
        UiKit.Height(mode, 44f);
        UiKit.Row(mode, 6f);
        UiKit.Style(UiKit.Button("Campaign", mode, "Campaign", null, 20f), true);
        UiKit.Style(UiKit.Button("Strategic", mode, "Strategic (coming later)", null, 18f), false, interactable: false);
        var blank = new CampaignProgress();   // campaign rules only ask about the profile (CompleteCampaign)
        foreach (var c in _catalogue.campaigns)
        {
            var cc = c;
            bool open = UnlockRule.Parse(c.requires).IsMet(blank);
            string name = c.NameFor(lang, fallback);
            var b = UiKit.Height(UiKit.Button(c.id, campaigns, name == c.id ? c.id : $"{name} ({c.id})", () =>
            {
                _campaign = cc.id;
                DrawNewGame();
            }, 22f), 44f);
            UiKit.Style(b, c.id == _campaign, open);
        }

        var slots = Column(panel, "Save slot", 0.62f, 1f);
        var all = CampaignSession.AllSlots();
        for (int i = 0; i < all.Length; i++)
        {
            int slot = i;
            var b = UiKit.Height(UiKit.Button($"Slot{i}", slots, CampaignSession.Describe(i, all[i], _catalogue), () =>
            {
                _slot = slot;
                _confirmOverwrite = false;
                DrawNewGame();
            }, 18f), 44f);
            UiKit.Style(b, i == _slot);
        }

        Footer(panel, "Back", ClosePanel, 0f, 0.2f);
        bool occupied = _slot >= 0 && all[_slot] != null;
        string start = _confirmOverwrite ? $"Overwrite slot {_slot + 1}?" : "Start";
        var go = Footer(panel, start, StartNewGame, 0.75f, 1f);
        UiKit.Style(go, _confirmOverwrite, _faction != null && _campaign != null && _slot >= 0);
        if (occupied && !_confirmOverwrite) Say($"Slot {_slot + 1} has a game in it — starting will replace it.");
    }

    private void StartNewGame()
    {
        if (_faction == null || _campaign == null || _slot < 0) return;
        bool occupied = CampaignProgress.TryLoad(_slot) != null;
        if (occupied && !_confirmOverwrite)
        {
            _confirmOverwrite = true;
            DrawNewGame();
            return;
        }
        CampaignSession.StartNew(_slot, _campaign, _faction);
        GoToCampaign();
    }

    // ── Load Game ──────────────────────────────────────────────────────

    private void OpenLoadGame()
    {
        var panel = NewPanel("Load Game");
        var list = Column(panel, "Save slots", 0.2f, 0.8f);
        var all = CampaignSession.AllSlots();
        bool any = false;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            any = true;
            int slot = i;
            UiKit.Height(UiKit.Button($"Slot{i}", list, CampaignSession.Describe(i, all[i], _catalogue), () =>
            {
                if (CampaignSession.Open(slot)) GoToCampaign();
            }, 22f), 52f);
        }
        if (!any) UiKit.Height(UiKit.Label("None", list, "No saved games yet.", 24f, TextAlignmentOptions.Center, UiKit.TextDim), 52f);
        Footer(panel, "Back", ClosePanel, 0f, 0.2f);
    }
}
