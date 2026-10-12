using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maintains one 3D asset GameObject per grid cell, positioned just above
/// the 2D flat grid on the XZ plane so both layers are simultaneously visible.
///
/// Assets are sourced from TileDefinition.prefab3D. If a tile type has no
/// prefab assigned, no asset is spawned for that cell (leaving it as the
/// flat 2D colour). This means you can roll out 3D assets tile-type by
/// tile-type without every type needing a prefab upfront.
///
/// Write path
/// ----------
/// GridManager2D.OnTileChanged fires immediately on every tile write.
/// TileVisualizer3D responds by destroying the old asset (if any) and
/// instantiating the new one. This is direct instantiate/destroy for now;
/// a pool-per-type can be swapped in later with minimal changes.
///
/// Scene setup
/// -----------
/// 1. Add TileVisualizer3D to any persistent GameObject (e.g. GameManager).
/// 2. Assign gridManager and tileRegistry in the Inspector.
/// 3. Optionally adjust assetYOffset (default 0.05) so assets clear the
///    flat quads without floating visibly above them.
/// </summary>
public class TileVisualizer3D : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private TileRegistry  tileRegistry;

    [Header("Placement")]
    [Tooltip("Height above the XZ plane at which assets are spawned. " +
             "Enough to clear the 2D quad (0.002) without visible float.")]
    [SerializeField] private float assetYOffset = 0.05f;

    // Live asset map: one entry per cell that has an active 3D GO.
    // Cells whose TileDefinition has no prefab3D are absent from this dict.
    private readonly Dictionary<GridCell, GameObject> _assets = new();
    // The type each asset was spawned for, and the brightness it's tinted to.
    private readonly Dictionary<GridCell, (TileType type, float brightness)> _assetLooks = new();
    private readonly List<Renderer> _renderers = new();
    private MaterialPropertyBlock _block;

    // Parent transform that keeps the hierarchy clean.
    private Transform _assetRoot;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _assetRoot = new GameObject("3D Assets").transform;
        _assetRoot.SetParent(transform, false);
    }

    private void Start()
    {
        // Subscribe to future tile changes.
        gridManager.OnTileChanged += OnTileChanged;
        gridManager.OnLookChanged += OnTileChanged;

        // Initialise assets for the current grid state.
        // This runs in Start (not Awake) so GridManager2D.Awake has already
        // built the grid before we iterate it.
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell != null) RefreshCell(cell);
        }
    }

    private void OnDestroy()
    {
        if (gridManager != null)
        {
            gridManager.OnTileChanged -= OnTileChanged;
            gridManager.OnLookChanged -= OnTileChanged;
        }
    }

    // ── Event handler ──────────────────────────────────────────────────

    private void OnTileChanged(GridCell cell) => RefreshCell(cell);

    // ── Core logic ─────────────────────────────────────────────────────

    /// <summary>
    /// Destroys any existing asset for the cell and spawns a new one if the
    /// tile — as the local player sees it (GridManager2D.LookOf: fog of war
    /// may show a remembered or unknown tile) — has a prefab3D assigned.
    /// Out-of-sight tiles are darkened.
    /// </summary>
    private void RefreshCell(GridCell cell)
    {
        var look = gridManager.LookOf(cell);

        // Same tile as before, only lit differently: just re-tint.
        if (_assets.TryGetValue(cell, out GameObject current) && current != null &&
            _assetLooks.TryGetValue(cell, out var shown) && shown.type == look.Type)
        {
            if (!Mathf.Approximately(shown.brightness, look.Brightness)) Tint(cell, current, look);
            return;
        }

        // Remove old asset if present.
        if (_assets.TryGetValue(cell, out GameObject existing))
        {
            Destroy(existing);
            _assets.Remove(cell);
            _assetLooks.Remove(cell);
        }

        // Look up the prefab for the new tile type.
        var def = tileRegistry.GetDefinition(look.Type);
        if (def == null || def.prefab3D == null) return;

        // Spawn the new asset, applying per-definition offsets.
        Vector3 basePos = gridManager.CellToWorld(cell.X, cell.Y)
                          + new Vector3(0f, assetYOffset, 0f);
        Vector3    pos  = basePos + def.positionOffset;
        Quaternion rot  = Quaternion.Euler(def.rotationOffset);

        GameObject asset = Instantiate(def.prefab3D, pos, rot, _assetRoot);
        asset.name = $"Asset_{cell.X}_{cell.Y}_{look.Type}";

        if (!Mathf.Approximately(def.scaleMultiplier, 1f))
            asset.transform.localScale = Vector3.one * def.scaleMultiplier;

        _assets[cell] = asset;
        _assetLooks[cell] = (look.Type, 1f);
        if (look.Brightness < 0.999f) Tint(cell, asset, look);
    }

    /// <summary>Darkens (or restores) an asset by its look's brightness, leaving its materials untouched.</summary>
    private void Tint(GridCell cell, GameObject asset, CellLook look)
    {
        _block ??= new MaterialPropertyBlock();
        asset.GetComponentsInChildren(true, _renderers);
        foreach (var r in _renderers)
        {
            var mat = r.sharedMaterial;
            if (mat == null) continue;
            if (look.Brightness >= 0.999f) { r.SetPropertyBlock(null); continue; }

            _block.Clear();
            if (mat.HasProperty("_BaseColor")) _block.SetColor("_BaseColor", Darken(mat.GetColor("_BaseColor"), look.Brightness));
            if (mat.HasProperty("_Color"))     _block.SetColor("_Color",     Darken(mat.GetColor("_Color"),     look.Brightness));
            r.SetPropertyBlock(_block);
        }
        _assetLooks[cell] = (look.Type, look.Brightness);
    }

    private static Color Darken(Color c, float b) => new(c.r * b, c.g * b, c.b * b, c.a);

    // ── Public API (future use) ────────────────────────────────────────

    /// <summary>Returns the live 3D asset for a cell, or null if none.</summary>
    public GameObject GetAsset(GridCell cell) =>
        _assets.TryGetValue(cell, out var go) ? go : null;

    /// <summary>
    /// Forces a full refresh of all cells. Useful after bulk changes
    /// such as loading a saved map (which fires OnTileChanged per cell,
    /// so this is rarely needed directly).
    /// </summary>
    public void RefreshAll()
    {
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell != null) RefreshCell(cell);
        }
    }
}
