using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small always-visible minimap in the corner of the HUD.
/// Shows a fixed-size UV window of the minimap texture centred on the
/// camera focus point. As the camera moves the window slides over the texture.
///
/// Scene setup
/// -----------
/// 1. In your HUD Canvas create a Panel. Name it "MinimapCorner".
///    Size it to your desired corner map size (e.g. 200×200).
/// 2. Add a child RawImage named "MapImage". Stretch to fill the panel.
///    Assign MinimapRenderer.Texture to its Texture field.
/// 3. Add a second child RawImage named "PingImage" (same rect as MapImage).
///    Assign PingManager.OverlayTexture to its Texture field.
///    Set its Color alpha to 1 so pings are fully visible.
/// 4. Attach MinimapCorner to the Panel GO.
/// 5. Assign mapImage, pingImage, cameraFocus.
///
/// The uvWindowSize controls how much of the map is visible at once.
/// 0.3 = shows 30% of the map width/height centred on the camera.
/// Reduce for a more zoomed-in corner view.
/// </summary>
public class MinimapCorner : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("UI References")]
    [SerializeField] private RawImage mapImage;
    [SerializeField] private RawImage pingImage;

    [Header("Dependencies")]
    [SerializeField] private Transform cameraFocus; // CameraFocusMovement GO

    // Resolved via singleton Instance at runtime — no Inspector reference needed.
    private MinimapRenderer MinimapRenderer => MinimapRenderer.Instance;

    [Header("View")]
    [Tooltip("Fraction of the full map shown in the corner window (0–1). " +
             "Lower = more zoomed in.")]
    [SerializeField] [Range(0.1f, 1f)] private float uvWindowSize = 0.25f;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Start()
    {
        // Texture is created after grid load — subscribe to know when it's ready.
        MinimapRenderer.OnTextureReady += ConnectTextures;

        // If texture already exists (script order ran us after MinimapRenderer)
        // connect immediately.
        if (MinimapRenderer != null && MinimapRenderer.Texture != null)
        {
            MinimapRenderer.OnTextureReady -= ConnectTextures;
            ConnectTextures();
        }
    }

    private void OnDestroy()
    {
        MinimapRenderer.OnTextureReady -= ConnectTextures;
    }

    private void ConnectTextures()
    {
        MinimapRenderer.OnTextureReady -= ConnectTextures;

        if (MinimapRenderer == null) return;

        // Poll until the texture exists — it's created in MinimapRenderer's
        // own OnWalletsReady handler which may run in the same frame.
        if (MinimapRenderer.Texture == null)
        {
            // Re-subscribe and try again next event cycle.
            MinimapRenderer.OnTextureReady += ConnectTextures;
            return;
        }

        mapImage.texture = MinimapRenderer.Texture;
        if (pingImage != null)
            pingImage.texture = PingManager.Instance?.OverlayTexture;
    }

    private void Update()
    {
        if (MinimapRenderer == null || cameraFocus == null) return;
        // Assign textures if they weren't ready at Start.
        if (mapImage.texture == null && MinimapRenderer.Texture != null)
            ConnectTextures();
        UpdateUVRect();
    }

    // ── UV rect ────────────────────────────────────────────────────────

    private void UpdateUVRect()
    {
        // Convert camera focus world position to UV centre.
        Vector2 centre = MinimapRenderer.WorldToUV(cameraFocus.position);

        float half = uvWindowSize * 0.5f;
        float x    = Mathf.Clamp(centre.x - half, 0f, 1f - uvWindowSize);
        float y    = Mathf.Clamp(centre.y - half, 0f, 1f - uvWindowSize);

        var rect = new Rect(x, y, uvWindowSize, uvWindowSize);
        mapImage.uvRect  = rect;
        if (pingImage != null) pingImage.uvRect = rect;
    }
}
