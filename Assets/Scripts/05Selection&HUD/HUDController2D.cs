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

    [Header("Summon Button")]
    [Tooltip("Summon Worker button. Managed here so it highlights with the same "
             + "system as the buy/sell buttons and is mutually exclusive with them.")]
    [SerializeField] private Button summonButton;

    [Header("Highlight Colours")]
    [SerializeField] private Color activeColour = new(1f, 0.85f, 0.1f, 1f);
    [SerializeField] private Color normalColour = Color.white;

    private Button _activeButton;
    private bool   _syncingSummon;   // re-entrancy guard

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
        if (roomsTab != null)
        {
            RetireHandMadeRoomButtons();
            AddGeneratedSellButton();
        }
        AddGeneratedRoomButtons();
        RegisterRoomsWithFooter();

        foreach (var entry in buyButtonEntries)
        {
            if (entry?.button == null) continue;
            var captured = entry;
            entry.button.onClick.AddListener(() => OnBuyClicked(captured));
        }
        if (sellButton != null) sellButton.onClick.AddListener(OnSellClicked);

        if (summonButton != null)
            summonButton.onClick.AddListener(OnSummonClicked);

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

    private void OnSummonClicked()
    {
        SelectButton(summonButton);

        // Summon is not a buy/sell interaction, so the tile selection
        // controller must stand down while it is held open.
        selectionController.ActiveMode = SelectionController2D.InteractionMode.None;
    }

    /// <summary>
    /// Called by WorkerSpawner when summon mode is exited from its own input
    /// (right-click or Escape) rather than from the button, so the highlight
    /// does not get left on.
    /// </summary>
    public void NotifySummonModeExited(FactionID exitedFaction)
    {
        if (_syncingSummon) return;
        if (exitedFaction != localPlayer) return;
        if (_activeButton != summonButton) return;

        SetHighlight(_activeButton, false);
        _activeButton = null;
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
            SyncSummonMode();
            return;
        }

        _activeButton = btn;
        SetHighlight(_activeButton, true);
        SyncSummonMode();
    }

    /// <summary>
    /// Summon mode is on exactly when the summon button is the active one.
    /// Selecting any other button therefore cancels it, which is what makes all
    /// the mode buttons mutually exclusive without special-casing summon.
    /// </summary>
    private void SyncSummonMode()
    {
        if (_syncingSummon) return;

        var spawner = WorkerSpawner.GetForFaction(localPlayer);
        if (spawner == null) return;

        _syncingSummon = true;
        spawner.SetSummonMode(summonButton != null && _activeButton == summonButton);
        _syncingSummon = false;
    }

    private void Deselect()
    {
        SetHighlight(_activeButton, false);
        _activeButton = null;
        selectionController.ActiveMode = SelectionController2D.InteractionMode.None;
        SyncSummonMode();
    }

    private void SetHighlight(Button btn, bool on)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img) img.color = on ? activeColour : normalColour;
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
