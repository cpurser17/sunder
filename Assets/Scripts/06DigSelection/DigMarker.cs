using UnityEngine;

/// <summary>
/// Visual marker placed above a grid cell when it has been added to a
/// faction's dig selection queue.
///
/// Owns a single flat quad rendered above the 3D assets. The quad colour
/// is set at creation time by DigSelectionController.
///
/// The marker holds a reference to its cell so DigSelectionController can
/// find and remove it when the tile changes or the player deselects it.
///
/// Intended to be replaced with a more elaborate visual later (icon, animated
/// sprite, etc.) without changing DigSelectionController — just swap the
/// visuals inside this class.
/// </summary>
public class DigMarker : MonoBehaviour
{
    // ── Public state ───────────────────────────────────────────────────
    public GridCell Cell { get; private set; }

    // ── Private ────────────────────────────────────────────────────────
    private MeshRenderer _renderer;

    // ── Initialisation ─────────────────────────────────────────────────

    /// <summary>
    /// Called immediately after Instantiate by DigSelectionController.
    /// Builds the flat quad mesh and applies the marker colour.
    /// </summary>
    public void Initialise(GridCell cell, float cellSize, Color colour)
    {
        Cell = cell;

        // ── Mesh ───────────────────────────────────────────────────────
        var mf   = gameObject.AddComponent<MeshFilter>();
        _renderer = gameObject.AddComponent<MeshRenderer>();

        mf.mesh  = BuildQuad(cellSize * 0.85f); // slight inset so border is visible

        // ── Material ───────────────────────────────────────────────────
        // Reuse the GridOverlay vertex-colour shader — it's alpha-blended
        // and always available.  The quad sits above assets so z-fighting
        // is not a concern.
        var shader = Shader.Find("Dungeon2D/GridOverlay");
        if (shader == null)
        {
            Debug.LogWarning("[DigMarker] GridOverlay shader not found. " +
                             "Falling back to Unlit/Transparent.");
            shader = Shader.Find("Unlit/Transparent");
        }

        var mat          = new Material(shader);
        mat.mainTexture  = Texture2D.whiteTexture;
        _renderer.material           = mat;
        _renderer.shadowCastingMode  =
            UnityEngine.Rendering.ShadowCastingMode.Off;
        _renderer.receiveShadows     = false;

        SetColour(colour);
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Updates the marker tint at runtime (e.g. to show dig priority).</summary>
    public void SetColour(Color colour)
    {
        if (_renderer == null) return;
        // Write colour into vertex colour array so the overlay shader picks it up.
        var mesh    = GetComponent<MeshFilter>().mesh;
        var colours = new Color[mesh.vertexCount];
        for (int i = 0; i < colours.Length; i++) colours[i] = colour;
        mesh.colors = colours;
    }

    // ── Mesh builder ───────────────────────────────────────────────────

    private static Mesh BuildQuad(float size)
    {
        float h    = size * 0.5f;
        var mesh   = new Mesh { name = "DigMarkerQuad" };
        mesh.vertices  = new Vector3[]
        {
            new(-h, 0f, -h), new( h, 0f, -h),
            new( h, 0f,  h), new(-h, 0f,  h),
        };
        mesh.uv        = new Vector2[]
        {
            new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f),
        };
        mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateNormals();
        return mesh;
    }
}
