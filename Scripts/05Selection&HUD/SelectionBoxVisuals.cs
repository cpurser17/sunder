using UnityEngine;

/// <summary>
/// Owns a single resizable box mesh that visualises the current tile selection
/// in 3D space. The box spans the full selection rectangle on XZ and rises
/// above the tallest 3D assets. Bottom vertices are opaque, top vertices are
/// fully transparent, producing a glow-pillar gradient via vertex colour
/// interpolation in SelectionPillar.shader.
///
/// Colours match the 2D flat overlay:
///   Green  — buying, affordable
///   Yellow — buying, unaffordable
///   Red    — selling
///
/// The mesh is rebuilt (8 vertices, 6 faces) only when the selection region
/// or colour changes — one GameObject, one draw call, no per-cell overhead.
///
/// Scene setup
/// -----------
/// 1. Attach SelectionBoxVisuals to any persistent GO in the Gameplay Scene
///    (e.g. the GameManager GO).
/// 2. Ensure SelectionPillar.shader is anywhere inside your Assets folder.
/// 3. Drag the SelectionBoxVisuals reference into SelectionController2D.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SelectionBoxVisuals : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dimensions")]
    [Tooltip("How tall the pillar rises above the grid surface (world units).")]
    [SerializeField] private float pillarHeight = 3f;

    [Tooltip("Y position of the pillar base. Should sit just above the 2D grid quads.")]
    [SerializeField] private float baseY = 0.05f;

    [Header("Alpha")]
    [Tooltip("Alpha of the pillar at its base (fully visible).")]
    [SerializeField] [Range(0f, 1f)] private float bottomAlpha = 0.55f;
    [Tooltip("Alpha at the pillar top (fade to transparent).")]
    [SerializeField] [Range(0f, 1f)] private float topAlpha    = 0f;

    // ── Runtime ────────────────────────────────────────────────────────
    private Mesh         _mesh;
    private MeshRenderer _renderer;

    // Track last-set values so we only rebuild when something changes.
    private float  _lastMinX, _lastMinZ, _lastMaxX, _lastMaxZ;
    private Color  _lastColour;
    private bool   _initialised;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _mesh = new Mesh { name = "SelectionPillar" };
        GetComponent<MeshFilter>().mesh = _mesh;

        _renderer = GetComponent<MeshRenderer>();
        _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _renderer.receiveShadows    = false;

        var shader = Shader.Find("Dungeon2D/SelectionPillar");
        if (shader == null)
            Debug.LogError("[SelectionBoxVisuals] Shader 'Dungeon2D/SelectionPillar' " +
                           "not found. Ensure SelectionPillar.shader is in your Assets.");
        else
            _renderer.material = new Material(shader);

        gameObject.SetActive(false);
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Sets the pillar height at runtime (used when spawned programmatically).</summary>
    public void SetPillarHeight(float height) => pillarHeight = height;

    /// <summary>
    /// Shows and sizes the pillar to cover the rectangular region defined
    /// by world-space XZ extents. Rebuilds the mesh only if the region or
    /// colour has changed since the last call.
    /// </summary>
    /// <param name="worldMinX">Left edge of the selection in world X.</param>
    /// <param name="worldMinZ">Near edge of the selection in world Z.</param>
    /// <param name="worldMaxX">Right edge of the selection in world X.</param>
    /// <param name="worldMaxZ">Far edge of the selection in world Z.</param>
    /// <param name="colour">Tint colour (alpha channel is overridden by
    ///                      bottomAlpha/topAlpha per vertex).</param>
    public void Show(float worldMinX, float worldMinZ,
                     float worldMaxX, float worldMaxZ,
                     Color colour)
    {
        gameObject.SetActive(true);

        // Only rebuild the mesh when something actually changed.
        if (_initialised              &&
            Mathf.Approximately(worldMinX, _lastMinX) &&
            Mathf.Approximately(worldMinZ, _lastMinZ) &&
            Mathf.Approximately(worldMaxX, _lastMaxX) &&
            Mathf.Approximately(worldMaxZ, _lastMaxZ) &&
            colour == _lastColour)
            return;

        _lastMinX   = worldMinX;
        _lastMinZ   = worldMinZ;
        _lastMaxX   = worldMaxX;
        _lastMaxZ   = worldMaxZ;
        _lastColour = colour;
        _initialised = true;

        RebuildMesh(worldMinX, worldMinZ, worldMaxX, worldMaxZ, colour);
    }

    /// <summary>Hides the pillar without destroying the mesh.</summary>
    public void Hide() => gameObject.SetActive(false);

    // ── Mesh construction ──────────────────────────────────────────────

    /// <summary>
    /// Builds a box mesh with:
    ///   Bottom face at y = baseY
    ///   Top face    at y = baseY + pillarHeight
    ///   Bottom vertices: colour at bottomAlpha
    ///   Top vertices:    colour at topAlpha (fade to transparent)
    ///
    /// Vertex layout (viewed from above):
    ///   Bottom: 0=NW  1=NE  2=SE  3=SW
    ///   Top:    4=NW  5=NE  6=SE  7=SW
    ///
    ///   N = +Z (far),  S = -Z (near)
    ///   W = -X (left), E = +X (right)
    /// </summary>
    private void RebuildMesh(float x0, float z0, float x1, float z1, Color col)
    {
        float yBot = baseY;
        float yTop = baseY + pillarHeight;

        // ── Vertices ───────────────────────────────────────────────────
        var verts = new Vector3[8];

        // Bottom ring
        verts[0] = new Vector3(x0, yBot, z1); // NW
        verts[1] = new Vector3(x1, yBot, z1); // NE
        verts[2] = new Vector3(x1, yBot, z0); // SE
        verts[3] = new Vector3(x0, yBot, z0); // SW

        // Top ring
        verts[4] = new Vector3(x0, yTop, z1); // NW
        verts[5] = new Vector3(x1, yTop, z1); // NE
        verts[6] = new Vector3(x1, yTop, z0); // SE
        verts[7] = new Vector3(x0, yTop, z0); // SW

        // ── Vertex colours ─────────────────────────────────────────────
        Color colBot = new Color(col.r, col.g, col.b, bottomAlpha);
        Color colTop = new Color(col.r, col.g, col.b, topAlpha);

        var colours = new Color[8];
        colours[0] = colours[1] = colours[2] = colours[3] = colBot; // bottom
        colours[4] = colours[5] = colours[6] = colours[7] = colTop; // top

        // ── Triangles (two per face, six faces) ────────────────────────
        // Wound so faces are visible from outside (Cull Off in shader
        // means inside faces also render, but winding still matters for
        // correct normal direction if lighting is ever added).
        var tris = new int[]
        {
            // North face  (z1)
            0, 5, 1,   0, 4, 5,
            // East face   (x1)
            1, 6, 2,   1, 5, 6,
            // South face  (z0)
            2, 7, 3,   2, 6, 7,
            // West face   (x0)
            3, 4, 0,   3, 7, 4,
            // Bottom face (yBot) — visible looking down from editor
            0, 1, 2,   0, 2, 3,
            // Top face    (yTop) — alpha 0, invisible but correct
            4, 6, 5,   4, 7, 6,
        };

        // ── Upload to mesh ─────────────────────────────────────────────
        _mesh.Clear();
        _mesh.vertices  = verts;
        _mesh.colors    = colours;
        _mesh.triangles = tris;
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();
    }
}
