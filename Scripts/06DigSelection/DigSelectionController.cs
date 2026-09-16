using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Manages the persistent dig order queue for one faction.
/// Spawned and configured by DigSelectionManager at runtime.
///
/// Valid dig targets
/// -----------------
///   Stone — no owner; imp digs → Cave (marker removed on tile change)
///   Gold  — no owner; imp digs → Cave + currency (marker removed on tile change)
///   Wall  — must be owned by THIS faction (marker removed on tile change)
///   Gem   — no owner; imp harvests for slow currency, tile never changes;
///           marker persists until manually deselected.
///
/// Input
/// -----
///   LMB press/drag — rectangle-select valid tiles; pillar box shown in addBoxColour.
///                    Drag only begins if the first cell is a valid dig target.
///   RMB press/drag — rectangle-deselect; pillar box shown in removeBoxColour.
///                    Drag only begins if the first cell already has a marker.
///   On mouse-up    — commits the rectangle, hides the box, places/removes markers.
///
/// Suspension
/// ----------
///   Input suspended while a buy/sell button is active or a summon
///   placement is pending, so those clicks are not also read as drags.
///   Existing markers and any in-progress drag box remain visible during suspension.
/// </summary>
public class DigSelectionController : MonoBehaviour
{
    // ── Runtime configuration (set by DigSelectionManager.Initialise) ─
    private FactionID          _faction;
    private GridManager2D      _gridManager;
    private Camera             _mainCamera;
    private float              _markerHeight;
    private Color              _markerColour;
    private Transform          _markerRoot;
    private SelectionBoxVisuals _selectionBox;
    private Color              _addBoxColour;
    private Color              _removeBoxColour;

    private bool _initialised;

    // ── Drag state ─────────────────────────────────────────────────────
    private bool _lmbDragging;
    private bool _rmbDragging;
    private int  _startX,   _startY;
    private int  _currentX, _currentY;

    // ── Queue ──────────────────────────────────────────────────────────
    private readonly Dictionary<GridCell, DigMarker> _queue = new();

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void OnDestroy()
    {
        if (_gridManager != null)
            _gridManager.OnTileChanged -= OnTileChanged;
    }

    private void Update()
    {
        if (!_initialised) return;

        bool hudActive = HUDController2D.Instance != null &&
                         HUDController2D.Instance.AnyButtonActive;

        if (hudActive || ImpSpawner.AnySummonModeActive)
        {
            // Cancel any in-progress drag and hide the box if HUD becomes active.
            if (_lmbDragging || _rmbDragging)
            {
                _lmbDragging = false;
                _rmbDragging = false;
                _selectionBox?.Hide();
            }
            return;
        }

        HandleLMB();
        HandleRMB();

        // Update the selection box live during any active drag.
        if (_lmbDragging || _rmbDragging)
            UpdateSelectionBox();
    }

    // ── Initialisation ─────────────────────────────────────────────────

    /// <summary>
    /// Configures this controller. Called immediately after AddComponent
    /// by DigSelectionManager.
    /// </summary>
    public void Initialise(FactionID faction, GridManager2D gridManager,
                           Camera mainCamera, float markerHeight, Color markerColour,
                           SelectionBoxVisuals selectionBox,
                           Color addBoxColour, Color removeBoxColour)
    {
        _faction         = faction;
        _gridManager     = gridManager;
        _mainCamera      = mainCamera;
        _markerHeight    = markerHeight;
        _markerColour    = markerColour;
        _selectionBox    = selectionBox;
        _addBoxColour    = addBoxColour;
        _removeBoxColour = removeBoxColour;

        var rootGo  = new GameObject("MarkerRoot");
        rootGo.transform.SetParent(transform, false);
        _markerRoot = rootGo.transform;

        _gridManager.OnTileChanged += OnTileChanged;
        _initialised = true;
    }

    // ── Input: add (LMB) ───────────────────────────────────────────────

    private void HandleLMB()
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (IsPointerOverUI()) return;
            if (!TryGetCellCoords(out int x, out int y)) return;
            var startCell = _gridManager.GetCell(x, y);
            if (startCell == null || !IsValidDigTarget(startCell)) return;
            _lmbDragging = true;
            _startX = _currentX = x;
            _startY = _currentY = y;
        }

        if (_lmbDragging && Input.GetMouseButton(0))
        {
            if (TryGetCellCoords(out int x, out int y))
            { _currentX = x; _currentY = y; }
        }

        if (Input.GetMouseButtonUp(0) && _lmbDragging)
        {
            var region = _gridManager.GetCellsInRegion(
                _startX, _startY, _currentX, _currentY);
            foreach (var cell in region)
                if (IsValidDigTarget(cell)) AddCell(cell);

            _lmbDragging = false;
            _selectionBox?.Hide();
        }
    }

    // ── Input: remove (RMB) ────────────────────────────────────────────

    private void HandleRMB()
    {
        if (Input.GetMouseButtonDown(1))
        {
            if (IsPointerOverUI()) return;
            if (!TryGetCellCoords(out int x, out int y)) return;
            var startCell = _gridManager.GetCell(x, y);
            if (startCell == null || !_queue.ContainsKey(startCell)) return;
            _rmbDragging = true;
            _startX = _currentX = x;
            _startY = _currentY = y;
        }

        if (_rmbDragging && Input.GetMouseButton(1))
        {
            if (TryGetCellCoords(out int x, out int y))
            { _currentX = x; _currentY = y; }
        }

        if (Input.GetMouseButtonUp(1) && _rmbDragging)
        {
            var region = _gridManager.GetCellsInRegion(
                _startX, _startY, _currentX, _currentY);
            foreach (var cell in region)
                RemoveCell(cell);

            _rmbDragging = false;
            _selectionBox?.Hide();
        }
    }

    // ── Selection box ──────────────────────────────────────────────────

    private void UpdateSelectionBox()
    {
        if (_selectionBox == null) return;

        // Determine colour — add (orange) or remove (grey).
        Color col = _lmbDragging ? _addBoxColour : _removeBoxColour;

        // Compute world-space extents from grid coordinates,
        // same formula as SelectionController2D.UpdateBorder().
        float cs     = _gridManager.CellSize;
        Vector3 origin = _gridManager.transform.position;

        int minX = Mathf.Min(_startX, _currentX);
        int maxX = Mathf.Max(_startX, _currentX);
        int minY = Mathf.Min(_startY, _currentY);
        int maxY = Mathf.Max(_startY, _currentY);

        float wx0 = origin.x +  minX      * cs;
        float wx1 = origin.x + (maxX + 1) * cs;
        float wz0 = origin.z +  minY      * cs;
        float wz1 = origin.z + (maxY + 1) * cs;

        _selectionBox.Show(wx0, wz0, wx1, wz1, col);
    }

    // ── Queue management ───────────────────────────────────────────────

    private void AddCell(GridCell cell)
    {
        if (_queue.ContainsKey(cell)) return;

        Vector3 pos = _gridManager.CellToWorld(cell.X, cell.Y)
                      + Vector3.up * _markerHeight;

        var go = new GameObject($"DigMarker_{cell.X}_{cell.Y}");
        go.transform.SetParent(_markerRoot, false);
        go.transform.position = pos;

        var marker = go.AddComponent<DigMarker>();
        marker.Initialise(cell, _gridManager.CellSize, _markerColour);

        _queue[cell] = marker;

        // Job list is derived from tile state + dig markers, so just ask
        // the registry to re-evaluate this cell.
        ImpTaskManager.GetForFaction(_faction)?.RefreshDigTarget(cell);
    }

    private void RemoveCell(GridCell cell)
    {
        if (!_queue.TryGetValue(cell, out var marker)) return;
        if (marker != null) Destroy(marker.gameObject);
        _queue.Remove(cell);

        // Marker gone — the cell no longer produces a dig job.
        ImpTaskManager.GetForFaction(_faction)?.RefreshDigTarget(cell);
    }

    // ── Tile change listener ───────────────────────────────────────────

    private void OnTileChanged(GridCell cell)
    {
        if (!_queue.ContainsKey(cell)) return;
        if (!IsValidDigTarget(cell))
            RemoveCell(cell);
    }

    // ── Validation ─────────────────────────────────────────────────────

    private bool IsValidDigTarget(GridCell cell) =>
        cell.TileType switch
        {
            TileType.Stone => true,
            TileType.Gold  => true,
            TileType.Gem   => true,
            TileType.Wall  => cell.Owner == _faction,
            _              => false,
        };

    // ── Helpers ────────────────────────────────────────────────────────

    private bool TryGetCellCoords(out int x, out int y)
    {
        x = y = 0;
        Ray          ray  = _mainCamera.ScreenPointToRay(Input.mousePosition);
        RaycastHit[] hits = Physics.RaycastAll(ray);
        foreach (var hit in hits)
            if (hit.collider.gameObject == _gridManager.gameObject)
                return _gridManager.WorldToCell(hit.point, out x, out y);
        return false;
    }

    private static bool IsPointerOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    // ── Public API (imp system) ────────────────────────────────────────

    public FactionID                     Faction       => _faction;
    public IReadOnlyCollection<GridCell> GetDigQueue() => _queue.Keys;
    public bool                          IsQueued(GridCell cell) => _queue.ContainsKey(cell);
    public void                          DequeueCell(GridCell cell) => RemoveCell(cell);

    public void ClearQueue()
    {
        foreach (var marker in _queue.Values)
            if (marker != null) Destroy(marker.gameObject);
        _queue.Clear();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (_gridManager == null) return;
        Gizmos.color = _markerColour;
        foreach (var cell in _queue.Keys)
            Gizmos.DrawWireCube(
                _gridManager.CellToWorld(cell.X, cell.Y) + Vector3.up * _markerHeight,
                new Vector3(_gridManager.CellSize * 0.85f, 0.05f,
                            _gridManager.CellSize * 0.85f));
    }
#endif
}
