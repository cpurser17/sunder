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
             "Empty = the template's own parent.")]
    [SerializeField] private Transform roomButtonContainer;

    [Header("Sell Button")]
    [SerializeField] private Button sellButton;

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

    private void Awake() => Instance = this;

    private void Start()
    {
        buyButtonEntries ??= new List<BuyButtonEntry2D>();
        AddGeneratedRoomButtons();

        foreach (var entry in buyButtonEntries)
        {
            var captured = entry;
            entry.button.onClick.AddListener(() => OnBuyClicked(captured));
        }
        sellButton.onClick.AddListener(OnSellClicked);

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

    /// <summary>Clones roomButtonTemplate for each buildable room that has no button yet.</summary>
    private void AddGeneratedRoomButtons()
    {
        if (roomButtonTemplate == null) return;

        var grid  = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        var tiles = grid != null ? grid.Tiles : null;
        if (tiles == null)
        {
            Debug.LogWarning("[HUDController2D] No TileRegistry to generate room buttons from.");
            return;
        }

        var parent = roomButtonContainer != null ? roomButtonContainer : roomButtonTemplate.transform.parent;
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
            if (label != null) label.text = def.buyCost > 0 ? $"{def.tileName} ({def.buyCost}g)" : def.tileName;

            buyButtonEntries.Add(new BuyButtonEntry2D { button = button, tileType = def.tileType });
        }
    }

    private bool HasEntryFor(TileType type)
    {
        foreach (var entry in buyButtonEntries)
            if (entry != null && entry.tileType == type) return true;
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
