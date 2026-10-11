using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Manages HUD buy/sell buttons and gold display.
///
/// Each BuyButtonEntry2D maps a UI Button to a TileType.
/// The TileDefinition for that type drives all placement rules —
/// HUDController2D just sets ActiveTileType on SelectionController2D.
///
/// Room buttons can also be generated: assign roomButtonTemplate and every
/// player-buildable room (TileDefinition.playerBuildable, from the RoomData
/// workbook) without a hand-made entry gets a clone of it. New rooms then
/// appear in the HUD after an import, with no scene wiring.
///
/// With roomsTab set (the HUD footer's Rooms tab), the buttons go into that
/// tab's grid at each room's ButtonRow/ButtonColumn slot, which is also its
/// hotkey (Tab, row, column — see HudFooter). The footer then owns every
/// player-buildable room button: hand-made Buy Button entries for those
/// rooms are hidden and replaced by generated ones, and Sell is generated
/// too, at sellRow/sellColumn. Entries for non-room tiles (e.g. Tunnel) are
/// left as they are.
///
/// Likewise, with a Spells tab (spellsTab, or the footer's tab named
/// "Spells"), every spell with an effect script and a slot in the SpellData
/// workbook gets a button there, Summon Worker included; any hand-made summon
/// button is hidden. Selecting a spell button arms it (SpellCaster).
/// </summary>
public class HUDController2D : MonoBehaviour
{
    public static HUDController2D Instance { get; private set; }

    [Header("Dependencies")]
    [SerializeField] private SelectionController2D selectionController;
    [SerializeField] private FactionID             localPlayer = FactionID.Player;

    private FactionWallet Wallet =>
        GameManager2D.Instance?.GetWallet(localPlayer);

    [Header("Gold Display")]
    [SerializeField] private TextMeshProUGUI goldLabel;

    [Header("Buy Buttons")]
    [SerializeField] private List<BuyButtonEntry2D> buyButtonEntries;

    [Header("Room Buttons (generated)")]
    [Tooltip("Optional. Cloned once per player-buildable room that has no Buy Button " +
             "entry above; its TextMeshProUGUI child shows the room's name and cost. " +
             "Usually an inactive button inside the panel the clones should go in.")]
    [SerializeField] private Button roomButtonTemplate;
    [Tooltip("Parent for generated buttons, e.g. a panel with a Layout Group. " +
             "Empty = the template's own parent. Ignored when Rooms Tab is set.")]
    [SerializeField] private Transform roomButtonContainer;
    [Tooltip("The HUD footer's Rooms tab. Buttons go into its grid at each room's " +
             "row/column slot, which doubles as the hotkey.")]
    [SerializeField] private FooterTab roomsTab;

    [Header("Sell Button")]
    [Tooltip("Hand-made Sell button. Ignored (and hidden) when Rooms Tab is set — " +
             "the footer gets a generated Sell button instead.")]
    [SerializeField] private Button sellButton;
    [Tooltip("Slot of the generated Sell button in the Rooms tab (also its hotkey).")]
    [SerializeField, Range(0, 9)] private int sellRow    = 4;
    [SerializeField, Range(0, 9)] private int sellColumn = 1;

    [Header("Spell Buttons")]
    [Tooltip("Hand-made Summon Worker button, for a HUD without a Spells tab — it arms the " +
             "Summon Worker spell. Ignored (and hidden) when there's a Spells tab.")]
    [SerializeField] private Button summonButton;
    [Tooltip("The HUD footer's Spells tab. Empty = the footer's tab named \"Spells\", if any. " +
             "Spells go at their SpellData ButtonRow/ButtonColumn slot (also the hotkey).")]
    [SerializeField] private FooterTab spellsTab;

    [Header("Highlight Colours")]
    [SerializeField] private Color activeColour = new(1f, 0.85f, 0.1f, 1f);
    [SerializeField] private Color normalColour = Color.white;
    [Tooltip("A lasting spell that's on (e.g. Call to Arms with its rally up), when its button isn't selected.")]
    [SerializeField] private Color toggledColour = new(1f, 0.55f, 0.3f, 1f);

    private Button _activeButton;

    private class SpellButton
    {
        public SpellDefinition Spell;
        public TextMeshProUGUI Label;   // kept showing the current cost
        public int             ShownCost = -1;
    }
    private readonly Dictionary<Button, SpellButton> _spellButtons = new();

    /// <summary>
    /// True while any buy or sell button is active.
    /// DigSelectionController reads this to suspend dig input.
    /// </summary>
    public bool AnyButtonActive => _activeButton != null;

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogError($"[HUDController2D] Second HUDController2D on {name} (the first is on " +
                           $"{Instance.name}). Each would add its own buttons and click handlers — " +
                           "remove one.", this);
        Instance = this;
    }

    private void Start()
    {
        buyButtonEntries ??= new List<BuyButtonEntry2D>();
        UseFooter();
        if (roomsTab != null)
        {
            RetireHandMadeRoomButtons();
            AddGeneratedSellButton();
        }
        AddGeneratedRoomButtons();
        RegisterRoomsWithFooter();

        if (spellsTab != null)
        {
            AddGeneratedSpellButtons();
            spellsTab.RebuildLayout();
        }
        else if (summonButton != null)
        {
            var summon = SpellSystem.Instance != null ? SpellSystem.Instance.Find("SummonWorker") : null;
            if (summon != null) AddSpellButton(summonButton, summon);
        }

        // The Minions tab tracks this player's minions (see MinionTracker).
        var minionsTab = HudFooter.Instance != null ? HudFooter.Instance.FindTab("Minions") : null;
        if (minionsTab != null && !minionsTab.TryGetComponent(out MinionTracker tracker))
            minionsTab.gameObject.AddComponent<MinionTracker>().Initialise(localPlayer);

        foreach (var entry in buyButtonEntries)
        {
            if (entry?.button == null) continue;
            var captured = entry;
            entry.button.onClick.AddListener(() => OnBuyClicked(captured));
        }
        if (sellButton != null) sellButton.onClick.AddListener(OnSellClicked);

        foreach (var pair in _spellButtons)
        {
            var button = pair.Key;
            button.onClick.AddListener(() => OnSpellClicked(button));
        }

        // Wallets are created by GameManager2D after the scene loads.
        // Subscribe to OnWalletsReady so the gold display connects at the
        // right time regardless of Start() execution order.
        GameManager2D.OnWalletsReady += ConnectWallet;

        // In case wallets were already ready before we subscribed
        // (e.g. script execution order puts HUD after GameManager2D).
        ConnectWallet();
    }

    private void OnDestroy()
    {
        GameManager2D.OnWalletsReady -= ConnectWallet;
    }

    private void Update() => RefreshSpellLabels();

    private void ConnectWallet()
    {
        var w = Wallet;
        if (w == null) return;
        // Unsubscribe first to avoid double-subscription if called twice.
        w.OnGoldChanged -= OnWalletGoldChanged;
        w.OnGoldChanged += OnWalletGoldChanged;
        UpdateGoldLabel(w.Gold);
    }

    private void OnWalletGoldChanged(FactionID faction, int gold)
    {
        // Only update the display for the local player's faction.
        if (faction == localPlayer) UpdateGoldLabel(gold);
    }

    private void OnBuyClicked(BuyButtonEntry2D entry)
    {
        SelectButton(entry.button);
        selectionController.ActiveTileType = entry.tileType;
        selectionController.ActiveMode     = SelectionController2D.InteractionMode.Buy;
    }

    private void OnSellClicked()
    {
        SelectButton(sellButton);
        selectionController.ActiveMode = SelectionController2D.InteractionMode.Sell;
    }

    private void OnSpellClicked(Button button)
    {
        // A lasting spell that's on (Call to Arms): its button calls it off.
        var effect = SpellSystem.EffectFor(_spellButtons[button].Spell);
        if (effect != null && effect.IsToggledOn(localPlayer))
        {
            effect.ToggleOff(localPlayer);
            if (_activeButton == button) Deselect();
            return;
        }

        SelectButton(button);

        // A spell is not a buy/sell interaction, so the tile selection
        // controller stands down while one is armed.
        selectionController.ActiveMode = SelectionController2D.InteractionMode.None;
    }

    public void RequestDeselect() => Deselect();

    private void SelectButton(Button btn)
    {
        SetHighlight(_activeButton, false);

        if (_activeButton == btn)
        {
            // Clicking the active button toggles it off.
            _activeButton = null;
            selectionController.ActiveMode = SelectionController2D.InteractionMode.None;
            SyncSpell();
            return;
        }

        _activeButton = btn;
        SetHighlight(_activeButton, true);
        SyncSpell();
    }

    /// <summary>
    /// A spell is armed exactly when its button is the active one, so
    /// selecting any other button puts it away — every mode button stays
    /// mutually exclusive without special cases.
    /// </summary>
    private void SyncSpell()
    {
        var caster = SpellCaster.Instance;
        if (caster == null) return;

        if (_activeButton != null && _spellButtons.TryGetValue(_activeButton, out var spell)) caster.Arm(spell.Spell);
        else caster.Disarm();
    }

    private void Deselect()
    {
        SetHighlight(_activeButton, false);
        _activeButton = null;
        selectionController.ActiveMode = SelectionController2D.InteractionMode.None;
        SyncSpell();
    }

    private void SetHighlight(Button btn, bool on)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img) img.color = on ? activeColour : normalColour;
    }

    /// <summary>
    /// With a HUD footer in the scene, fills in anything left unwired: the
    /// Rooms and Spells tabs (found by name), and the footer's own
    /// RoomButtonTemplate — sized for its grids — in place of an empty
    /// template or one from the old HUD.
    /// </summary>
    private void UseFooter()
    {
        var footer = HudFooter.Instance;
        if (footer == null) return;

        if (roomsTab  == null) roomsTab  = footer.FindTab("Rooms");
        if (spellsTab == null) spellsTab = footer.FindTab("Spells");

        var footerTemplate = footer.transform.Find("RoomButtonTemplate");
        var template = footerTemplate != null ? footerTemplate.GetComponent<Button>() : null;
        if (template != null && (roomButtonTemplate == null || !roomButtonTemplate.transform.IsChildOf(footer.transform)))
        {
            if (roomButtonTemplate != null)
                Debug.Log($"[HUDController2D] Using the footer's RoomButtonTemplate instead of {roomButtonTemplate.name} " +
                          "(not part of the footer). Point Room Button Template at a footer button to choose another.", this);
            roomButtonTemplate = template;
        }

        if (roomsTab == null)
            Debug.LogWarning("[HUDController2D] The HUD footer has no tab named \"Rooms\" — room buttons stay in the old HUD.", this);
    }

    private TileRegistry Tiles
    {
        get
        {
            var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
            return grid != null ? grid.Tiles : null;
        }
    }

    /// <summary>
    /// With the footer in charge, hand-made Buy Button entries for
    /// player-buildable rooms are dropped and their buttons hidden, so those
    /// rooms are generated like the rest. Only needs a template to replace them.
    /// </summary>
    private void RetireHandMadeRoomButtons()
    {
        var tiles = Tiles;
        if (roomButtonTemplate == null || tiles == null) return;

        var retired = new List<string>();
        for (int i = buyButtonEntries.Count - 1; i >= 0; i--)
        {
            var entry = buyButtonEntries[i];
            if (entry == null) { buyButtonEntries.RemoveAt(i); continue; }

            var def = tiles.GetDefinition(entry.tileType);
            if (def == null || !def.playerBuildable) continue;

            if (entry.button != null && entry.button != roomButtonTemplate)
                entry.button.gameObject.SetActive(false);
            buyButtonEntries.RemoveAt(i);
            retired.Add(entry.tileType.ToString());
        }

        if (retired.Count > 0)
            Debug.Log($"[HUDController2D] The footer generates {string.Join(", ", retired)} — their hand-made " +
                      "Buy Button entries were hidden. Remove them from Buy Buttons (and delete the old " +
                      "buttons) to tidy the scene.", this);
    }

    /// <summary>Puts a generated Sell button in the Rooms tab, hiding any hand-made one.</summary>
    private void AddGeneratedSellButton()
    {
        if (roomButtonTemplate == null) return;

        if (sellButton != null && sellButton != roomButtonTemplate)
            sellButton.gameObject.SetActive(false);

        var button = Instantiate(roomButtonTemplate, roomsTab.Content);
        button.name = "Sell";
        button.gameObject.SetActive(true);

        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null) label.text = "Sell" + SlotSuffix(sellRow, sellColumn);

        sellButton = button;
        roomsTab.Add(new FooterTab.Entry { Row = sellRow, Column = sellColumn, Label = "Sell", Button = button });
    }

    /// <summary>
    /// A button in the Spells tab for every spell that has an effect script
    /// and a slot, hiding any hand-made summon button. Spells still waiting
    /// on their script get no button yet.
    /// </summary>
    private void AddGeneratedSpellButtons()
    {
        var spells = SpellSystem.Instance;
        if (roomButtonTemplate == null || spells == null) return;

        if (summonButton != null && summonButton != roomButtonTemplate)
            summonButton.gameObject.SetActive(false);

        foreach (var spell in spells.Spells)
        {
            if (spell.buttonRow <= 0 || spell.buttonColumn <= 0) continue;
            if (SpellSystem.EffectFor(spell) == null) continue;

            var button = Instantiate(roomButtonTemplate, spellsTab.Content);
            button.name = $"Spell_{spell.id}";
            button.gameObject.SetActive(true);
            AddSpellButton(button, spell);

            spellsTab.Add(new FooterTab.Entry
            {
                Row = spell.buttonRow, Column = spell.buttonColumn, Label = spell.displayName, Button = button,
            });
        }
    }

    private void AddSpellButton(Button button, SpellDefinition spell)
    {
        _spellButtons[button] = new SpellButton
        {
            Spell = spell,
            Label = button.GetComponentInChildren<TextMeshProUGUI>(true),
        };
    }

    /// <summary>
    /// Keeps each spell button's cost current — Summon Worker's rises with
    /// each worker — and shows lasting spells that are on.
    /// </summary>
    private void RefreshSpellLabels()
    {
        var spells = SpellSystem.Instance;
        if (spells == null) return;

        foreach (var pair in _spellButtons)
        {
            var entry = pair.Value;
            if (pair.Key != _activeButton)
            {
                var effect = SpellSystem.EffectFor(entry.Spell);
                var img    = pair.Key.GetComponent<Image>();
                var colour = effect != null && effect.IsToggledOn(localPlayer) ? toggledColour : normalColour;
                if (img != null && img.color != colour) img.color = colour;
            }

            if (entry.Label == null) continue;
            int cost = spells.CostOf(localPlayer, entry.Spell, 1);
            if (cost == entry.ShownCost) continue;

            entry.ShownCost  = cost;
            entry.Label.text = (cost > 0 ? $"{entry.Spell.displayName} ({cost}g)" : entry.Spell.displayName)
                             + SlotSuffix(entry.Spell.buttonRow, entry.Spell.buttonColumn);
        }
    }

    /// <summary>Clones roomButtonTemplate for each buildable room that has no button yet.</summary>
    private void AddGeneratedRoomButtons()
    {
        if (roomButtonTemplate == null) return;

        var tiles = Tiles;
        if (tiles == null)
        {
            Debug.LogWarning("[HUDController2D] No TileRegistry to generate room buttons from.");
            return;
        }

        var parent = roomsTab != null ? roomsTab.Content
                   : roomButtonContainer != null ? roomButtonContainer : roomButtonTemplate.transform.parent;
        var rooms  = new List<TileDefinition>();
        foreach (var def in tiles.Definitions)
            if (def != null && def.playerBuildable && !HasEntryFor(def.tileType)) rooms.Add(def);
        rooms.Sort((a, b) => a.tileType.CompareTo(b.tileType));

        foreach (var def in rooms)
        {
            var button = Instantiate(roomButtonTemplate, parent);
            button.name = $"Build_{def.tileType}";
            button.gameObject.SetActive(true);

            var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = ButtonLabel(def);

            buyButtonEntries.Add(new BuyButtonEntry2D { button = button, tileType = def.tileType });
        }
    }

    /// <summary>"Library (60g)", plus the hotkey slot in small print when it has one.</summary>
    private static string ButtonLabel(TileDefinition def)
    {
        string text = def.buyCost > 0 ? $"{def.tileName} ({def.buyCost}g)" : def.tileName;
        return text + SlotSuffix(def.buttonRow, def.buttonColumn);
    }

    private static string SlotSuffix(int row, int column) =>
        row > 0 && column > 0 ? $"  <size=70%><alpha=#99>{row}·{column}</size>" : "";

    /// <summary>
    /// Gives the footer's Rooms tab every room button — generated or hand-made —
    /// at its slot, so hotkeys reach all of them, then lays the grid out.
    /// </summary>
    private void RegisterRoomsWithFooter()
    {
        if (roomsTab == null) return;

        var tiles = Tiles;
        if (tiles == null) { roomsTab.RebuildLayout(); return; }

        foreach (var entry in buyButtonEntries)
        {
            if (entry?.button == null) continue;
            var def = tiles.GetDefinition(entry.tileType);
            if (def == null || !def.isRoom) continue;

            roomsTab.Add(new FooterTab.Entry
            {
                Row    = def.buttonRow,
                Column = def.buttonColumn,
                Label  = def.tileName,
                Button = entry.button,
            });
        }
        roomsTab.RebuildLayout();
        Debug.Log($"[HUDController2D] Rooms tab: {roomsTab.Entries.Count} button(s).", this);
    }

    private bool HasEntryFor(TileType type)
    {
        foreach (var entry in buyButtonEntries)
            if (entry?.button != null && entry.tileType == type) return true;
        return false;
    }

    private void UpdateGoldLabel(int gold)
    {
        if (goldLabel) goldLabel.text = $"Gold: {gold}";
    }
}

[System.Serializable]
public class BuyButtonEntry2D
{
    public Button   button;
    public TileType tileType;
}
