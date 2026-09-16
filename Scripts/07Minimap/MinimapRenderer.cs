using UnityEngine;

/// <summary>
/// Owns the base minimap Texture2D and keeps it in sync with the logical grid.
///
/// One pixel = one grid cell. Pixel colour = TileRegistry.GetColour(type, owner).
///
/// Update strategy
/// ---------------
/// Individual tile changes are batched: OnTileChanged marks a dirty flag and
/// records the cell. LateUpdate uploads all dirty pixels in one Apply() call
/// per frame, matching the pattern used by the grid mesh colour arrays.
///
/// The texture is also fully redrawn on the first frame (InitialiseTexture)
/// and whenever LoadMapData triggers a full grid reload.
///
/// Scene setup
/// -----------
/// 1. Create an empty child GO under GameManager. Name it "MinimapSystem".
/// 2. Attach MinimapRenderer.
/// 3. Assign gridManager and tileRegistry.
/// 4. Drag MinimapTexture into the MinimapCorner and MinimapFullscreen
///    RawImage components — they share the same texture reference.
/// </summary>
public class MinimapRenderer : MonoBehaviour
{
    public static MinimapRenderer Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private TileRegistry  tileRegistry;

    // ── Runtime ────────────────────────────────────────────────────────
    private Texture2D _texture;
    private bool      _dirty;

    /// <summary>The base minimap texture. Assign to RawImage.texture in UI.</summary>
    public Texture2D Texture => _texture;

    /// <summary>
    /// Fired once after the texture is created and fully drawn.
    /// MinimapCorner and MinimapFullscreen subscribe to this to safely
    /// assign the texture regardless of Start() execution order.
    /// </summary>
    public static event System.Action OnTextureReady;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        if (Instance == null) Instance = this;
    }

    private void Start()
    {
        // Subscribe to OnRebakeComplete for full redraws (room changes etc).
        // We do NOT subscribe to OnTileChanged here — that is done inside
        // InitialiseTexture once the texture exists, via OnWalletsReady which
        // fires after GridManager2D.Initialise() has finished building the grid.
        gridManager.OnRebakeComplete += _ => FullRedraw();

        // If the grid is already initialised (e.g. Start order puts us after
        // GameManager2D), initialise the texture now.
        // Otherwise wait for GameManager2D.OnWalletsReady which fires after
        // Initialise() completes and the grid has valid dimensions.
        if (gridManager.Width > 0 && gridManager.Height > 0)
            InitialiseTexture();
        else
            GameManager2D.OnWalletsReady += InitialiseTexture;
    }

    private void OnDestroy()
    {
        GameManager2D.OnWalletsReady         -= InitialiseTexture;
        if (gridManager != null)
        {
            gridManager.OnTileChanged    -= OnTileChanged;
            gridManager.OnRebakeComplete -= _ => FullRedraw();
        }
    }

    private void LateUpdate()
    {
        if (!_dirty) return;
        _texture.Apply();
        _dirty = false;
    }

    // ── Texture management ─────────────────────────────────────────────

    private void InitialiseTexture()
    {
        // Unsubscribe from the deferred init event if we were waiting on it.
        GameManager2D.OnWalletsReady -= InitialiseTexture;

        if (gridManager.Width <= 0 || gridManager.Height <= 0)
        {
            Debug.LogError("[MinimapRenderer] Grid has invalid dimensions. " +
                           "Ensure GridManager2D.Initialise() has been called first.");
            return;
        }

        _texture = new Texture2D(gridManager.Width, gridManager.Height,
                                 TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point, // pixel-perfect, no blurring
            wrapMode   = TextureWrapMode.Clamp,
            name       = "MinimapBase",
        };

        // Subscribe to OnTileChanged only once the texture exists.
        gridManager.OnTileChanged -= OnTileChanged; // prevent double-subscribe
        gridManager.OnTileChanged += OnTileChanged;

        FullRedraw();
        OnTextureReady?.Invoke();
    }

    private void FullRedraw()
    {
        if (_texture == null) return;

        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var cell = gridManager.GetCell(x, y);
            if (cell != null)
                _texture.SetPixel(x, y, tileRegistry.GetColour(cell.TileType, cell.Owner));
        }

        _texture.Apply();
        _dirty = false;
    }

    // ── Per-cell update ────────────────────────────────────────────────

    private void OnTileChanged(GridCell cell)
    {
        if (_texture == null) return;
        _texture.SetPixel(cell.X, cell.Y,
            tileRegistry.GetColour(cell.TileType, cell.Owner));
        _dirty = true;
    }

    // ── Coordinate utilities (shared by corner and fullscreen) ─────────

    /// <summary>
    /// Converts a world XZ position to a UV coordinate on the minimap texture.
    /// Returns values in [0,1] clamped to the grid bounds.
    /// </summary>
    public Vector2 WorldToUV(Vector3 worldPos)
    {
        if (!gridManager.WorldToCell(worldPos, out int x, out int y))
        {
            // Clamp to grid bounds for positions outside the grid.
            Vector3 origin = gridManager.transform.position;
            float u = Mathf.Clamp01((worldPos.x - origin.x) /
                                    (gridManager.Width  * gridManager.CellSize));
            float v = Mathf.Clamp01((worldPos.z - origin.z) /
                                    (gridManager.Height * gridManager.CellSize));
            return new Vector2(u, v);
        }
        return new Vector2(
            (x + 0.5f) / gridManager.Width,
            (y + 0.5f) / gridManager.Height);
    }

    /// <summary>
    /// Converts a UV coordinate on the minimap texture to a world XZ position
    /// (Y is always 0 — callers apply fixedHeight as needed).
    /// </summary>
    public Vector3 UVToWorld(Vector2 uv)
    {
        Vector3 origin = gridManager.transform.position;
        return new Vector3(
            origin.x + uv.x * gridManager.Width  * gridManager.CellSize,
            0f,
            origin.z + uv.y * gridManager.Height * gridManager.CellSize);
    }
}
