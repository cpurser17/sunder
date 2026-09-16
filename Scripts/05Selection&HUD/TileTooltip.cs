using UnityEngine;
using TMPro;

/// <summary>
/// Follows the mouse over the grid and displays the hovered cell's tile name
/// and owner in a world-space TextMeshPro label.
///
/// This is a temporary debug aid — disable or destroy this component once
/// 3D tile assets make cell identification obvious.
///
/// Scene setup
/// -----------
/// 1. Hierarchy > 3D Object > Text - TextMeshPro. Name it "TileTooltip".
///    Font size ~2.5, centre-aligned, disabled by default.
/// 2. Attach TileTooltip.cs to any persistent GO (e.g. GameManager).
/// 3. Assign gridManager, mainCamera, and the tooltipLabel in the Inspector.
/// </summary>
public class TileTooltip : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private Camera        mainCamera;
    [SerializeField] private TileRegistry  tileRegistry;

    [Header("Label")]
    [SerializeField] private TextMeshPro tooltipLabel;
    [Tooltip("Height above the grid surface the label hovers.")]
    [SerializeField] private float hoverHeight = 1.5f;

    private GridCell _lastCell; // tracks previous cell to avoid redundant updates

    private void Awake()
    {
        if (tooltipLabel != null)
            tooltipLabel.gameObject.SetActive(false);
    }

    private void Update()
    {
        if (tooltipLabel == null) return;

        GridCell hovered = GetHoveredCell();

        if (hovered == null)
        {
            tooltipLabel.gameObject.SetActive(false);
            _lastCell = null;
            return;
        }

        // Only rebuild the string when the cell changes.
        if (hovered != _lastCell)
        {
            _lastCell = hovered;
            tooltipLabel.text = BuildLabel(hovered);
        }

        // Position above the cell centre, billboarded to face the camera.
        Vector3 worldPos = gridManager.CellToWorld(hovered.X, hovered.Y)
                           + Vector3.up * hoverHeight;
        tooltipLabel.transform.position = worldPos;
        tooltipLabel.transform.rotation = Quaternion.LookRotation(
            worldPos - mainCamera.transform.position);

        tooltipLabel.gameObject.SetActive(true);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private GridCell GetHoveredCell()
    {
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        RaycastHit[] hits = Physics.RaycastAll(ray);
        foreach (var hit in hits)
        {
            if (hit.collider.gameObject != gridManager.gameObject) continue;
            if (gridManager.WorldToCell(hit.point, out int x, out int y))
                return gridManager.GetCell(x, y);
        }
        return null;
    }

    private string BuildLabel(GridCell cell)
    {
        string name = tileRegistry.GetTileName(cell.TileType);

        // Append owner for owned tiles only.
        if (cell.Owner != FactionID.Unaligned)
            return $"{name}\n<size=70%>{cell.Owner}</size>";

        return name;
    }
}
