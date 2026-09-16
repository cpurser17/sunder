using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds and owns the 2D logical grid as two combined meshes on the XZ plane.
///
///   Base mesh    — vertex colour = tile colour tinted by faction ownership.
///   Overlay mesh — vertex colour = selection highlight (alpha 0 when inactive).
///
/// Colour is always derived from TileRegistry.GetColour(tileType, owner) so
/// faction tinting is automatic on every tile write.
///
/// Total GameObjects: 3. Total draw calls: 2. Both constant regardless of map size.
/// </summary>
public class GridManager2D : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("References")]
    [SerializeField] private TileRegistry tileRegistry;

    [Header("Grid")]
    [Tooltip("Cell size in world units. All cells are square. "
             + "Must match the footprint of your 3D tile assets.")]
    [SerializeField] private float cellSize = 1f;

    [Header("Agent sizes")]
    [Tooltip("Token radii (world units) to bake connectivity for. Leave as one "
             + "entry unless you add a minion too large for a one-tile corridor; "
             + "each extra tier costs another set of flood-fills.")]
    [SerializeField] private float[] agentRadiusTiers = { 0.45f };

    [Header("Rebake")]
    [SerializeField] private float rebakeDelay = 0.15f;

    // ── Runtime (set by Initialise, not the Inspector) ─────────────────
    private int          width;
    private int          height;
    private GridCell[,]  _grid;
    private RoomRegistry _roomRegistry;
    private Coroutine    _rebakeCoroutine;

    private Mesh      _baseMesh;
    private Mesh      _overlayMesh;
    private Color32[] _baseColours;
    private Color32[] _overlayColours;
    private bool      _baseDirty;
    private bool      _overlayDirty;

    public int   Width    => width;
    public int   Height   => height;
    public float CellSize => cellSize;

    public event Action<IReadOnlyList<DungeonRoom>> OnRebakeComplete;
    /// <summary>Fired immediately on every tile write, before the debounced rebake.</summary>
    public event Action<GridCell> OnTileChanged;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        tileRegistry.Initialise();
        // Grid is NOT built here. GameManager2D.Start() calls Initialise()
        // after loading the level or save file, so dimensions come from the
        // file rather than the Inspector. In-editor Play mode without a full
        // scene flow falls back to Inspector defaults via Initialise().
        _roomRegistry = new RoomRegistry(new GridCell[0,0], 0, 0,
                                        tileRegistry, cellSize); // placeholder
        _roomRegistry.OnRebakeComplete += rooms => OnRebakeComplete?.Invoke(rooms);
    }

    /// <summary>
    /// Builds (or rebuilds) the grid at the specified dimensions and applies
    /// saved cell data. Called by GameManager2D after loading a level or save.
    /// If cellData is null, all cells initialise to Stone (new level default).
    /// Safe to call multiple times — tears down existing meshes first.
    /// </summary>
    public void Initialise(int newWidth, int newHeight, CellSaveData[,] cellData = null)
    {
        // Tear down existing mesh GOs and collider if rebuilding.
        var existingBox = GetComponent<BoxCollider>();
        if (existingBox != null) Destroy(existingBox);

        // Destroy child mesh GOs (GridBase, GridOverlay).
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            if (child.name == "GridBase" || child.name == "GridOverlay")
                Destroy(child.gameObject);
        }

        width  = newWidth;
        height = newHeight;

        BuildGrid(cellData);

        // Replace the room registry with one sized to the new grid.
        _roomRegistry = new RoomRegistry(_grid, width, height, tileRegistry, cellSize);
        _roomRegistry.Connectivity.SetRadiusTiers(agentRadiusTiers);
        _roomRegistry.OnRebakeComplete += rooms => OnRebakeComplete?.Invoke(rooms);

        ScheduleRebake();
    }

    private void LateUpdate()
    {
        if (_baseDirty)
        {
            _baseMesh.colors32 = _baseColours;
            _baseDirty = false;
        }
        if (_overlayDirty)
        {
            _overlayMesh.colors32 = _overlayColours;
            _overlayDirty = false;
        }
    }

    // ── Grid construction ──────────────────────────────────────────────

    private void BuildGrid(CellSaveData[,] cellData = null)
    {
        int cellCount = width * height;
        _grid           = new GridCell[width, height];
        _baseColours    = new Color32[cellCount * 4];
        _overlayColours = new Color32[cellCount * 4];

        var vertices      = new Vector3[cellCount * 4];
        var uvs           = new Vector2[cellCount * 4];
        var triangles     = new int[cellCount * 6];
        var overlayVerts  = new Vector3[cellCount * 4];

        Color32 stoneCol = tileRegistry.GetColour(TileType.Stone);
        Color32 clearCol = new Color32(0, 0, 0, 0);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width;  x++)
        {
            _grid[x, y] = new GridCell(x, y);
            // Apply saved data if provided; otherwise default to Stone.
            if (cellData != null)
            {
                var d = cellData[x, y];
                if (d != null)
                {
                    if (d.tileType == TileType.Bridge)
                        _grid[x, y].PlaceBridgeInternal(d.owner);
                    else
                        _grid[x, y].SetTileTypeInternal(d.tileType, d.owner);
                }
            }

            int cell = y * width + x;
            int vi   = cell * 4;
            int ti   = cell * 6;

            float wx = transform.position.x + x * cellSize;
            float wz = transform.position.z + y * cellSize;

            vertices[vi+0] = new Vector3(wx,            0f,      wz);
            vertices[vi+1] = new Vector3(wx + cellSize, 0f,      wz);
            vertices[vi+2] = new Vector3(wx + cellSize, 0f,      wz + cellSize);
            vertices[vi+3] = new Vector3(wx,            0f,      wz + cellSize);

            overlayVerts[vi+0] = vertices[vi+0] + new Vector3(0f, 0.002f, 0f);
            overlayVerts[vi+1] = vertices[vi+1] + new Vector3(0f, 0.002f, 0f);
            overlayVerts[vi+2] = vertices[vi+2] + new Vector3(0f, 0.002f, 0f);
            overlayVerts[vi+3] = vertices[vi+3] + new Vector3(0f, 0.002f, 0f);

            uvs[vi+0] = new Vector2(0f, 0f);
            uvs[vi+1] = new Vector2(1f, 0f);
            uvs[vi+2] = new Vector2(1f, 1f);
            uvs[vi+3] = new Vector2(0f, 1f);

            triangles[ti+0] = vi; triangles[ti+1] = vi+2; triangles[ti+2] = vi+1;
            triangles[ti+3] = vi; triangles[ti+4] = vi+3; triangles[ti+5] = vi+2;

            Color32 cellCol = tileRegistry.GetColour(_grid[x, y].TileType, _grid[x, y].Owner);
            _baseColours[vi] = _baseColours[vi+1] =
            _baseColours[vi+2] = _baseColours[vi+3] = cellCol;

            _overlayColours[vi] = _overlayColours[vi+1] =
            _overlayColours[vi+2] = _overlayColours[vi+3] = clearCol;
        }

        _baseMesh = BuildMesh("GridBase", vertices, uvs, triangles, _baseColours);
        _overlayMesh = BuildMesh("GridOverlay", overlayVerts, uvs, triangles, _overlayColours);

        SpawnMeshObject("GridBase",    _baseMesh,    false);
        SpawnMeshObject("GridOverlay", _overlayMesh, true);

        // Notify subscribers (TileVisualizer3D) of every cell's initial state.
        for (int x = 0; x < width;  x++)
        for (int y = 0; y < height; y++)
            OnTileChanged?.Invoke(_grid[x, y]);

        // Single flat BoxCollider for raycasting — works from any camera angle.
        var box    = gameObject.AddComponent<BoxCollider>();
        float totX = width  * cellSize;
        float totZ = height * cellSize;
        box.center = new Vector3(totX * 0.5f, 0f, totZ * 0.5f);
        box.size   = new Vector3(totX, 0.01f, totZ);
    }

    private static Mesh BuildMesh(string meshName,
                                  Vector3[] verts, Vector2[] uvs,
                                  int[] tris, Color32[] colours)
    {
        var mesh = new Mesh
        {
            name        = meshName,
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };
        mesh.vertices  = verts;
        mesh.uv        = uvs;
        mesh.triangles = tris;
        mesh.colors32  = colours;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void SpawnMeshObject(string goName, Mesh mesh, bool transparent)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().mesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;

        string shaderName = transparent ? "Dungeon2D/GridOverlay" : "Dungeon2D/GridBase";
        var shader = Shader.Find(shaderName);
        if (shader == null)
        {
            Debug.LogError($"[GridManager2D] Shader '{shaderName}' not found. " +
                           "Ensure GridBase.shader and GridOverlay.shader are in your Assets folder.");
            return;
        }
        mr.material = new Material(shader);
    }

    // ── Colour helpers ─────────────────────────────────────────────────

    private void SetBaseColour(int x, int y, Color32 col)
    {
        int vi = (y * width + x) * 4;
        _baseColours[vi] = _baseColours[vi+1] =
        _baseColours[vi+2] = _baseColours[vi+3] = col;
        _baseDirty = true;
    }

    private void SetOverlayColour(int x, int y, Color32 col)
    {
        int vi = (y * width + x) * 4;
        _overlayColours[vi] = _overlayColours[vi+1] =
        _overlayColours[vi+2] = _overlayColours[vi+3] = col;
        _overlayDirty = true;
    }

    // ── Public API ─────────────────────────────────────────────────────

    public GridCell GetCell(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return null;
        return _grid[x, y];
    }

    public List<GridCell> GetCellsInRegion(int x0, int y0, int x1, int y1)
    {
        int minX = Mathf.Min(x0,x1), maxX = Mathf.Max(x0,x1);
        int minY = Mathf.Min(y0,y1), maxY = Mathf.Max(y0,y1);
        var result = new List<GridCell>((maxX-minX+1)*(maxY-minY+1));
        for (int x = minX; x <= maxX; x++)
        for (int y = minY; y <= maxY; y++)
        {
            var c = GetCell(x, y);
            if (c != null) result.Add(c);
        }
        return result;
    }

    public Vector3 CellToWorld(int x, int y) =>
        transform.position + new Vector3((x + 0.5f) * cellSize, 0f, (y + 0.5f) * cellSize);

    public bool WorldToCell(Vector3 worldPos, out int x, out int y)
    {
        Vector3 local = worldPos - transform.position;
        x = Mathf.FloorToInt(local.x / cellSize);
        y = Mathf.FloorToInt(local.z / cellSize);
        return x >= 0 && x < width && y >= 0 && y < height;
    }

    /// <summary>
    /// Returns all orthogonal neighbours of (x, y) that satisfy a predicate.
    /// Used by SelectionController2D for adjacency validation.
    /// </summary>
    public bool HasAdjacentMatch(int x, int y, System.Func<GridCell, bool> predicate)
    {
        (int dx, int dy)[] dirs = { (0,1),(0,-1),(1,0),(-1,0) };
        foreach (var (dx, dy) in dirs)
        {
            var n = GetCell(x + dx, y + dy);
            if (n != null && predicate(n)) return true;
        }
        return false;
    }

    // ── Tile write methods ─────────────────────────────────────────────

    /// <summary>Standard tile replacement (rooms, cave, etc.).</summary>
    public void SetTileType(GridCell cell, TileType newType, FactionID owner = FactionID.Unaligned)
    {
        cell.SetTileTypeInternal(newType, owner);
        SetBaseColour(cell.X, cell.Y, tileRegistry.GetColour(newType, owner));
        OnTileChanged?.Invoke(cell);
        ScheduleRebake();
    }

    /// <summary>Place a bridge over a liquid tile.</summary>
    public void PlaceBridge(GridCell cell, FactionID owner)
    {
        cell.PlaceBridgeInternal(owner);
        SetBaseColour(cell.X, cell.Y, tileRegistry.GetColour(TileType.Bridge, owner));
        OnTileChanged?.Invoke(cell);
        ScheduleRebake();
    }

    /// <summary>Sell a bridge, restoring the underlying liquid tile.</summary>
    public void RemoveBridge(GridCell cell)
    {
        cell.RemoveBridgeInternal();
        SetBaseColour(cell.X, cell.Y,
            tileRegistry.GetColour(cell.TileType, FactionID.Unaligned));
        OnTileChanged?.Invoke(cell);
        ScheduleRebake();
    }

    /// <summary>
    /// Sell a room tile back to Tunnel, retaining the current owner.
    /// </summary>
    public void SellRoom(GridCell cell)
    {
        FactionID owner = cell.Owner;
        cell.SetTileTypeInternal(TileType.Tunnel, owner);
        SetBaseColour(cell.X, cell.Y, tileRegistry.GetColour(TileType.Tunnel, owner));
        OnTileChanged?.Invoke(cell);
        ScheduleRebake();
    }

    /// <summary>Transfer ownership of a cell without changing its tile type.</summary>
    public void SetOwner(GridCell cell, FactionID newOwner)
    {
        cell.SetOwnerInternal(newOwner);
        SetBaseColour(cell.X, cell.Y, tileRegistry.GetColour(cell.TileType, newOwner));
        OnTileChanged?.Invoke(cell);
        ScheduleRebake();
    }

    public void SetHighlight(GridCell cell, bool visible, UnityEngine.Color colour = default)
    {
        Color32 col = visible ? (Color32)colour : new Color32(0, 0, 0, 0);
        SetOverlayColour(cell.X, cell.Y, col);
    }

    public int        GetBuyCost(TileType  type) => tileRegistry.GetBuyCost(type);
    public int        GetSellValue(TileType type) => tileRegistry.GetSellValue(type);
    public TileCategory GetCategory(TileType type) => tileRegistry.GetCategory(type);
    public bool       RequiresAdjacency(TileType type) => tileRegistry.RequiresAdjacency(type);
    public bool       PlacesOnLiquid(TileType  type) => tileRegistry.PlacesOnLiquid(type);
    public bool       PlacesOnTunnel(TileType  type) => tileRegistry.PlacesOnTunnel(type);
    public DungeonRoom GetRoomForCell(GridCell cell) => _roomRegistry.GetRoomForCell(cell);
    public List<DungeonRoom> GetRoomsForFaction(FactionID f) => _roomRegistry.GetRoomsForFaction(f);
    public ConnectivityRegistry Connectivity => _roomRegistry.Connectivity;
    public TerritoryRegistry    Territory    => _roomRegistry.Territory;
    public ClearanceMap         Clearance    => _roomRegistry.Clearance;
    public TileDefinition GetDefinition(TileType type) => tileRegistry.GetDefinition(type);

    // ── Debounced rebake ───────────────────────────────────────────────

    public void ScheduleRebake()
    {
        if (_rebakeCoroutine != null) StopCoroutine(_rebakeCoroutine);
        _rebakeCoroutine = StartCoroutine(RebakeAfterDelay());
    }

    private IEnumerator RebakeAfterDelay()
    {
        yield return new WaitForSeconds(rebakeDelay);
        _roomRegistry.Rebake();
        _rebakeCoroutine = null;
    }

}

