using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// Handles all mouse interaction with the 2D logical grid.
///
/// Buy modes (set by HUDController2D via ActiveTileType):
///   Rooms  (Treasury, Lair, …) — source must be player-owned Tunnel.
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
    public TileType        ActiveTileType { get; set; } = TileType.Treasury;

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
        // Dropping or slapping a minion — the hand's click, not a deselect.
        if (KeeperHand.OwnsMouseButton(1)) return;
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
        var buildable = BuildableCells();
        int cost = buildable.Count * gridManager.GetBuyCost(ActiveTileType);
        Color col = Wallet.Gold >= cost ? ColCanAfford : ColCannotAfford;

        // Cells that will be built in the afford colour; the rest dimmed, so
        // the player sees the selection boundary but knows they won't be built.
        foreach (var c in _currentSelection)
            gridManager.SetHighlight(c, true, buildable.Contains(c) ? col : ColInvalid);

        return (cost, buildable.Count > 0);
    }

    /// <summary>
    /// The selected cells the active buy type will actually be built on.
    ///
    /// Each must be a valid source (liquid for a bridge, own Tunnel for a
    /// room). For types that require adjacency (bridges), a cell must also
    /// connect to the player's floor: it touches it directly, or touches —
    /// edge to edge, through the selection — another cell that does. That
    /// way a drag spans a whole lake from its shore in one go, while a
    /// second lake caught in the same drag that doesn't touch the player's
    /// floor gets nothing. Any number of separate starting points work.
    /// </summary>
    private HashSet<GridCell> BuildableCells()
    {
        bool placesOnLiquid = gridManager.PlacesOnLiquid(ActiveTileType);
        bool placesOnTunnel = gridManager.PlacesOnTunnel(ActiveTileType);

        var valid = new HashSet<GridCell>();
        foreach (var c in _currentSelection)
            if (IsValidSource(c, placesOnLiquid, placesOnTunnel)) valid.Add(c);

        if (!gridManager.RequiresAdjacency(ActiveTileType)) return valid;

        // Spread from every valid cell touching the player's floor, through
        // orthogonal neighbours that are themselves valid selected cells.
        var reached = new HashSet<GridCell>();
        var queue   = new Queue<GridCell>();
        foreach (var c in valid)
        {
            if (!gridManager.HasAdjacentMatch(c.X, c.Y,
                    n => n.Owner == localPlayer && IsOwnedDungeonTile(n.TileType))) continue;
            reached.Add(c);
            queue.Enqueue(c);
        }

        (int dx, int dy)[] dirs = { (0, 1), (0, -1), (1, 0), (-1, 0) };
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var (dx, dy) in dirs)
            {
                var n = gridManager.GetCell(c.X + dx, c.Y + dy);
                if (n == null || !valid.Contains(n) || !reached.Add(n)) continue;
                queue.Enqueue(n);
            }
        }
        return reached;
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
        // Only rooms (isRoom — which includes Bridge) can be sold via the UI.
        return gridManager.GetDefinition(c.TileType)?.isRoom ?? false;
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

        // Same rules as the preview, re-checked now (the grid may have changed).
        var targets = BuildableCells();
        if (targets.Count == 0) return;

        // All or nothing: if the whole selection can't be afforded, nothing is built.
        int total = targets.Count * gridManager.GetBuyCost(ActiveTileType);
        if (!Wallet.TrySpend(total))
        {
            Announcer.Announce(localPlayer, "NotEnoughGold");
            return;
        }

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
        GridCell dropAt = null;
        foreach (var c in _currentSelection)
        {
            if (c.TileType != ActiveTileType || c.Owner != localPlayer) continue;
            earned += gridManager.GetSellValue(c.TileType);
            ExecuteSell(c);
            if (c.TileType != TileType.Water && c.TileType != TileType.Lava) dropAt = c;
        }
        Wallet.Refund(earned, dropAt);
    }

    private void CommitSell()
    {
        int earned = 0;
        GridCell dropAt = null;
        foreach (var c in _currentSelection)
        {
            if (!IsSellable(c)) continue;
            earned += gridManager.GetSellValue(c.TileType);
            ExecuteSell(c);
            if (c.TileType != TileType.Water && c.TileType != TileType.Lava) dropAt = c;
        }
        Wallet.Refund(earned, dropAt);
    }

    // Sale proceeds are banked on Treasury tiles with room (FactionWallet.Refund);
    // what doesn't fit is dropped as a gold pile on the last floor tile sold.
    // A sold Treasury tile's own gold spills there too (TreasuryManager).

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
    /// Floor that counts as "ours" for adjacency rules (e.g. a bridge must
    /// touch one): Tunnel, any room (Bridge included), and the Portal and
    /// Dungeon Heart. Wall is excluded — it's a perimeter, not floor.
    /// </summary>
    private bool IsOwnedDungeonTile(TileType t) =>
        t == TileType.Tunnel || t == TileType.Portal || t == TileType.Heart ||
        (gridManager.GetDefinition(t)?.isRoom ?? false);
}
