using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// Handles all mouse interaction with the 2D logical grid.
///
/// Buy modes (set by HUDController2D via ActiveTileType):
///   Rooms  (RoomA/B/C) — source must be player-owned Tunnel.
///   Bridge             — source must be Liquid; selection must have at
///                        least one cell adjacent to a player-owned tile.
///
/// Sell mode:
///   Room tiles  → reverts to Tunnel (same owner).
///   Bridge      → reverts to underlying liquid tile (loses ownership).
///   Tunnel/Wall/Environmental/Liquid → not sellable.
///
/// Alt + LMB in Buy mode — sells only cells matching ActiveTileType
///                         that are owned by the local player.
///
/// Right-click (first)  — cancel active drag, keep button selected.
/// Right-click (second) — deselect HUD button.
/// </summary>
public class SelectionController2D : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private Camera        mainCamera;

    [Header("Local Player")]
    // localPlayer already declared below — Wallet resolved from GameManager2D.

    [Header("Local Player")]
    [SerializeField] private FactionID localPlayer = FactionID.Player;

    // Resolved at runtime — avoids a direct Inspector reference to a
    // dynamically-created FactionWallet.
    private FactionWallet Wallet =>
        GameManager2D.Instance?.GetWallet(localPlayer);

    [Header("Cost Label")]
    [SerializeField] private TextMeshPro costLabel;

    [Header("Selection Border")]
    [SerializeField] private float               borderLineWidth = 0.05f;
    [Tooltip("The 3D pillar box that rises above assets during selection. "
             + "Attach SelectionBoxVisuals to a child GO and drag it here.")]
    [SerializeField] private SelectionBoxVisuals selectionBox;

    // ── Colours ────────────────────────────────────────────────────────
    private static readonly Color ColCanAfford    = new(0f, 1f, 0f, 0.4f);
    private static readonly Color ColCannotAfford = new(1f, 1f, 0f, 0.4f);
    private static readonly Color ColSell         = new(1f, 0f, 0f, 0.4f);
    private static readonly Color ColInvalid      = new(0.4f, 0.4f, 0.4f, 0.25f);

    // ── Public state ───────────────────────────────────────────────────
    public enum InteractionMode { None, Buy, Sell }

    public InteractionMode ActiveMode     { get; set; } = InteractionMode.None;
    public TileType        ActiveTileType { get; set; } = TileType.RoomA;

    // ── Drag state ─────────────────────────────────────────────────────
    private bool _dragging;
    private int  _startX, _startY, _currentX, _currentY;

    private bool AltSellMode =>
        ActiveMode == InteractionMode.Buy &&
        (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

    private List<GridCell> _currentSelection = new();

    // ── Border LineRenderer ────────────────────────────────────────────
    private LineRenderer _border;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        BuildBorder();
        if (costLabel != null) costLabel.gameObject.SetActive(false);
    }

    private void Update()
    {
        HandleRightClick();
        if (ActiveMode == InteractionMode.None && !_dragging) return;
        HandleLeftMouseInput();
        if (_dragging) UpdateSelection();
    }

    // ── Border ─────────────────────────────────────────────────────────

    private void BuildBorder()
    {
        var go = new GameObject("SelectionBorder");
        go.transform.SetParent(transform, false);
        _border = go.AddComponent<LineRenderer>();
        _border.loop              = true;
        _border.positionCount     = 4;
        _border.useWorldSpace     = true;
        _border.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _border.receiveShadows    = false;
        _border.material          = new Material(Shader.Find("Unlit/Color"));
        _border.startWidth        = borderLineWidth;
        _border.endWidth          = borderLineWidth;
        _border.enabled           = false;
    }

    // ── Input handling ─────────────────────────────────────────────────

    private void HandleRightClick()
    {
        if (!Input.GetMouseButtonDown(1)) return;
        if (_dragging) CancelDrag();
        else           HUDController2D.Instance?.RequestDeselect();
    }

    private void HandleLeftMouseInput()
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;
            if (!TryGetCell(out int x, out int y)) return;
            _dragging = true;
            _startX = x; _startY = y;
            _currentX = x; _currentY = y;
        }

        if (Input.GetMouseButton(0) && _dragging)
            if (TryGetCell(out int x, out int y))
            { _currentX = x; _currentY = y; }

        if (Input.GetMouseButtonUp(0) && _dragging)
        {
            CommitTransaction();
            CancelDrag();
        }
    }

    // ── Selection update ───────────────────────────────────────────────

    private void UpdateSelection()
    {
        foreach (var c in _currentSelection) gridManager.SetHighlight(c, false);
        _currentSelection.Clear();

        _currentSelection = gridManager.GetCellsInRegion(
            _startX, _startY, _currentX, _currentY);

        int cost = 0;
        bool regionValid = false;

        if (ActiveMode == InteractionMode.Buy && !AltSellMode)
        {
            (cost, regionValid) = PreviewBuy();
        }
        else if (ActiveMode == InteractionMode.Buy && AltSellMode)
        {
            cost = PreviewAltSell();
            regionValid = true;
        }
        else if (ActiveMode == InteractionMode.Sell)
        {
            cost = PreviewSell();
            regionValid = true;
        }

        UpdateBorder(cost, regionValid);
        UpdateCostLabel(cost);
    }

    // ── Buy preview ────────────────────────────────────────────────────

    /// <summary>
    /// Highlights valid source cells for the active buy type and returns
    /// total cost and whether the region satisfies all placement rules.
    /// </summary>
    private (int cost, bool regionValid) PreviewBuy()
    {
        bool placesOnLiquid = gridManager.PlacesOnLiquid(ActiveTileType);
        bool placesOnTunnel = gridManager.PlacesOnTunnel(ActiveTileType);
        bool requiresAdj    = gridManager.RequiresAdjacency(ActiveTileType);

        // For adjacency-required types, check at the region level:
        // at least one valid source cell must neighbour a player-owned tile.
        bool adjacencySatisfied = true;
        if (requiresAdj)
        {
            adjacencySatisfied = false;
            foreach (var c in _currentSelection)
            {
                if (!IsValidSource(c, placesOnLiquid, placesOnTunnel)) continue;
                if (gridManager.HasAdjacentMatch(c.X, c.Y,
                        n => n.Owner == localPlayer && IsOwnedDungeonTile(n.TileType)))
                {
                    adjacencySatisfied = true;
                    break;
                }
            }
        }

        int cost = 0;
        foreach (var c in _currentSelection)
        {
            bool validSource = IsValidSource(c, placesOnLiquid, placesOnTunnel);

            if (validSource && adjacencySatisfied)
            {
                cost += gridManager.GetBuyCost(ActiveTileType);
                Color col = Wallet.Gold >= cost ? ColCanAfford : ColCannotAfford;
                gridManager.SetHighlight(c, true, col);
            }
            else
            {
                // Show dim highlight for invalid cells so the player can see
                // the selection boundary but knows those cells won't be built.
                gridManager.SetHighlight(c, true, ColInvalid);
            }
        }

        // Re-colour all valid cells uniformly once total cost is known.
        if (adjacencySatisfied)
        {
            Color finalCol = Wallet.Gold >= cost ? ColCanAfford : ColCannotAfford;
            foreach (var c in _currentSelection)
                if (IsValidSource(c, placesOnLiquid, placesOnTunnel))
                    gridManager.SetHighlight(c, true, finalCol);
        }

        bool regionValid = adjacencySatisfied && cost > 0;
        return (cost, regionValid);
    }

    private bool IsValidSource(GridCell c, bool placesOnLiquid, bool placesOnTunnel)
    {
        if (placesOnLiquid) return gridManager.GetCategory(c.TileType) == TileCategory.Liquid;
        if (placesOnTunnel) return c.TileType == TileType.Tunnel && c.Owner == localPlayer;
        return false;
    }

    // ── Sell preview ───────────────────────────────────────────────────

    private int PreviewSell()
    {
        int earned = 0;
        foreach (var c in _currentSelection)
        {
            bool sellable = IsSellable(c);
            gridManager.SetHighlight(c, sellable, sellable ? ColSell : ColInvalid);
            if (sellable) earned += gridManager.GetSellValue(c.TileType);
        }
        return -earned;
    }

    private bool IsSellable(GridCell c)
    {
        if (c.Owner != localPlayer) return false;
        // Only named rooms and bridges can be sold via the UI.
        return c.TileType == TileType.RoomA   ||
               c.TileType == TileType.RoomB   ||
               c.TileType == TileType.RoomC   ||
               c.TileType == TileType.Bridge;
    }

    // ── Alt-sell preview ───────────────────────────────────────────────

    private int PreviewAltSell()
    {
        int earned = 0;
        foreach (var c in _currentSelection)
        {
            bool valid = c.TileType == ActiveTileType && c.Owner == localPlayer;
            gridManager.SetHighlight(c, valid, valid ? ColSell : ColInvalid);
            if (valid) earned += gridManager.GetSellValue(c.TileType);
        }
        return -earned;
    }

    // ── Transaction commit ─────────────────────────────────────────────

    private void CommitTransaction()
    {
        if (_currentSelection.Count == 0) return;
        if      (ActiveMode == InteractionMode.Buy && !AltSellMode) CommitBuy();
        else if (ActiveMode == InteractionMode.Buy &&  AltSellMode) CommitAltSell();
        else if (ActiveMode == InteractionMode.Sell)                CommitSell();
    }

    private void CommitBuy()
    {
        bool placesOnLiquid = gridManager.PlacesOnLiquid(ActiveTileType);
        bool placesOnTunnel = gridManager.PlacesOnTunnel(ActiveTileType);
        bool requiresAdj    = gridManager.RequiresAdjacency(ActiveTileType);

        // Re-validate adjacency rule at commit time.
        if (requiresAdj)
        {
            bool ok = false;
            foreach (var c in _currentSelection)
            {
                if (!IsValidSource(c, placesOnLiquid, placesOnTunnel)) continue;
                if (gridManager.HasAdjacentMatch(c.X, c.Y,
                        n => n.Owner == localPlayer && IsOwnedDungeonTile(n.TileType)))
                { ok = true; break; }
            }
            if (!ok) return;
        }

        // Tally cost.
        var targets = new List<GridCell>();
        int total   = 0;
        foreach (var c in _currentSelection)
        {
            if (!IsValidSource(c, placesOnLiquid, placesOnTunnel)) continue;
            targets.Add(c);
            total += gridManager.GetBuyCost(ActiveTileType);
        }
        if (targets.Count == 0 || !Wallet.TrySpend(total)) return;

        // Apply changes.
        foreach (var c in targets)
        {
            if (placesOnLiquid)
                gridManager.PlaceBridge(c, localPlayer);
            else
                gridManager.SetTileType(c, ActiveTileType, localPlayer);
        }
    }

    private void CommitAltSell()
    {
        int earned = 0;
        foreach (var c in _currentSelection)
        {
            if (c.TileType != ActiveTileType || c.Owner != localPlayer) continue;
            earned += gridManager.GetSellValue(c.TileType);
            ExecuteSell(c);
        }
        Wallet.Earn(earned);
    }

    private void CommitSell()
    {
        int earned = 0;
        foreach (var c in _currentSelection)
        {
            if (!IsSellable(c)) continue;
            earned += gridManager.GetSellValue(c.TileType);
            ExecuteSell(c);
        }
        Wallet.Earn(earned);
    }

    /// <summary>Executes the correct sell revert for a single cell.</summary>
    private void ExecuteSell(GridCell c)
    {
        if (c.TileType == TileType.Bridge)
            gridManager.RemoveBridge(c);
        else
            gridManager.SellRoom(c); // Room → Tunnel, same owner
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void CancelDrag()
    {
        _dragging = false;
        foreach (var c in _currentSelection) gridManager.SetHighlight(c, false);
        _currentSelection.Clear();
        HideCostLabel();
        HideBorder();
    }

    private bool TryGetCell(out int x, out int y)
    {
        x = y = 0;
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        RaycastHit[] hits = Physics.RaycastAll(ray);
        foreach (var hit in hits)
            if (hit.collider.gameObject == gridManager.gameObject)
                return gridManager.WorldToCell(hit.point, out x, out y);
        return false;
    }

    // ── Border ─────────────────────────────────────────────────────────

    private void UpdateBorder(int cost, bool regionValid)
    {
        if (_border == null) return;

        Color col;
        if (ActiveMode == InteractionMode.Buy && !AltSellMode)
        {
            if (!regionValid)         col = new Color(0.4f, 0.4f, 0.4f, 1f);
            else if (Wallet.Gold >= cost) col = new Color(0f, 1f, 0f, 1f);
            else                          col = new Color(1f, 1f, 0f, 1f);
        }
        else
            col = new Color(1f, 0f, 0f, 1f);

        _border.material.color = col;

        float cs = gridManager.CellSize;
        const float yOff = 0.01f;

        int minX = Mathf.Min(_startX, _currentX), maxX = Mathf.Max(_startX, _currentX);
        int minY = Mathf.Min(_startY, _currentY), maxY = Mathf.Max(_startY, _currentY);

        Vector3 o = gridManager.transform.position;
        float wx0 = o.x +  minX      * cs, wx1 = o.x + (maxX+1) * cs;
        float wz0 = o.z +  minY      * cs, wz1 = o.z + (maxY+1) * cs;

        _border.SetPosition(0, new Vector3(wx0, yOff, wz0));
        _border.SetPosition(1, new Vector3(wx1, yOff, wz0));
        _border.SetPosition(2, new Vector3(wx1, yOff, wz1));
        _border.SetPosition(3, new Vector3(wx0, yOff, wz1));
        _border.enabled = true;

        // 3D pillar box — same world extents, same colour (alpha driven by shader).
        if (selectionBox != null)
            selectionBox.Show(wx0, wz0, wx1, wz1, col);
    }

    private void HideBorder()
    {
        if (_border    != null) _border.enabled = false;
        if (selectionBox != null) selectionBox.Hide();
    }

    // ── Cost label ─────────────────────────────────────────────────────

    private void UpdateCostLabel(int amount)
    {
        if (costLabel == null) return;
        if (_currentSelection.Count == 0) { costLabel.gameObject.SetActive(false); return; }

        Vector3 a = gridManager.CellToWorld(_startX,   _startY);
        Vector3 b = gridManager.CellToWorld(_currentX, _currentY);
        costLabel.transform.position = (a + b) * 0.5f + Vector3.up * 2f;
        costLabel.transform.rotation = Quaternion.LookRotation(
            costLabel.transform.position - mainCamera.transform.position);

        costLabel.gameObject.SetActive(true);

        if (amount > 0)      { costLabel.color = Color.red;   costLabel.text = $"Cost: {amount}g"; }
        else if (amount < 0) { costLabel.color = Color.green; costLabel.text = $"+{-amount}g"; }
        else                   costLabel.text = "";
    }

    private void HideCostLabel()
    {
        if (costLabel != null) costLabel.gameObject.SetActive(false);
    }

    /// <summary>
    /// Returns true for tile types that count as valid anchors for
    /// adjacency-required placements (Bridge, future Wall).
    /// Tunnel, all Room types, and existing Bridges all qualify.
    /// Wall is excluded — it is a perimeter tile, not a traversable floor.
    /// </summary>
    private static bool IsOwnedDungeonTile(TileType t) =>
        t == TileType.Tunnel  ||
        t == TileType.RoomA   ||
        t == TileType.RoomB   ||
        t == TileType.RoomC   ||
        t == TileType.Bridge;
}
