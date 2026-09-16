using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// Fullscreen map overlay toggled by Caps Lock.
///
/// Features
/// --------
/// • Scroll wheel zooms (shrinks/grows the UV rect).
/// • Middle mouse button pans (drags the UV rect).
/// • Left click  → camera jumps to clicked world position.
/// • Right click → places a faction ping at the clicked position.
/// • Camera scroll zoom is suspended while the map is open.
///
/// The map is NOT stretched to fill the screen — it shows a zoomable,
/// pannable view of the minimap texture at pixel-perfect scale (FilterMode.Point).
///
/// Scene setup
/// -----------
/// 1. Create a second Canvas with a higher Sort Order than your HUD (e.g. 10).
///    Name it "FullscreenMapCanvas". Set it to disabled by default.
/// 2. Inside it add a Panel that fills the screen. Name it "FullscreenMapPanel".
/// 3. Add a child RawImage named "MapImage". Stretch to fill the panel.
/// 4. Add a child RawImage named "PingImage" (same rect). Alpha = 1.
/// 5. Optionally add a small RawImage named "CameraIndicator" (a dot/arrow sprite).
/// 6. Attach MinimapFullscreen to the FullscreenMapCanvas GO.
/// 7. Assign all references in the Inspector.
/// </summary>
public class MinimapFullscreen : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("UI References")]
    [SerializeField] private GameObject fullscreenPanel;
    [SerializeField] private RawImage   mapImage;
    [SerializeField] private RawImage   pingImage;
    [SerializeField] private RawImage   cameraIndicator; // optional dot showing camera pos

    [Header("Dependencies")]
    [SerializeField] private CameraFocusMovement cameraFocus;

    // Resolved via singleton Instance at runtime — no Inspector reference needed.
    private MinimapRenderer MinimapRenderer => MinimapRenderer.Instance;
    private PingManager     PingManager     => PingManager.Instance;

    [Header("Faction")]
    [SerializeField] private FactionID localPlayerFaction = FactionID.Player;

    [Header("Zoom")]
    [Tooltip("UV size of the initial view (1 = whole map visible).")]
    [SerializeField] private float initialUVSize  = 1f;
    [Tooltip("How fast scroll zooms the map.")]
    [SerializeField] private float zoomSpeed      = 0.1f;
    [Tooltip("Minimum UV window size (maximum zoom-in level).")]
    [SerializeField] private float minUVSize      = 0.1f;
    [Tooltip("Maximum UV window size (minimum zoom / full map).")]
    [SerializeField] private float maxUVSize      = 1f;

    // ── Runtime ────────────────────────────────────────────────────────
    private bool    _open;
    private Vector2 _uvOffset;   // bottom-left corner of the current view in UV space
    private float   _uvSize;     // width and height of the view in UV space

    // Middle mouse pan state
    private bool    _panning;
    private Vector2 _panStartMousePos;
    private Vector2 _panStartOffset;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Start()
    {
        _uvSize   = Mathf.Clamp(initialUVSize, minUVSize, maxUVSize);
        _uvOffset = Vector2.zero;

        SetOpen(false);

        // Texture is created after grid load — defer assignment.
        MinimapRenderer.OnTextureReady += ConnectTextures;
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

        if (MinimapRenderer.Texture == null)
        {
            MinimapRenderer.OnTextureReady += ConnectTextures;
            return;
        }

        mapImage.texture = MinimapRenderer.Texture;
        if (pingImage != null)
            pingImage.texture = PingManager.Instance?.OverlayTexture;
    }

    private void Update()
    {
        HandleToggle();
        // Assign textures if they weren't ready at Start.
        if (mapImage.texture == null && MinimapRenderer?.Texture != null)
            ConnectTextures();
        if (!_open) return;

        HandleZoom();
        HandlePan();
        HandleClick();
        UpdateCameraIndicator();
        ApplyUVRect();
    }

    // ── Toggle ─────────────────────────────────────────────────────────

    private void HandleToggle()
    {
        if (Input.GetKeyDown(KeyCode.CapsLock))
            SetOpen(!_open);
    }

    private void SetOpen(bool open)
    {
        _open = open;
        fullscreenPanel.SetActive(open);
        ThirdPersonOrbitCamera.ZoomSuspended  = open;
        ThirdPersonOrbitCamera.OrbitSuspended = open;

        if (open)
        {
            // Centre the view on the camera focus when opening.
            Vector2 camUV = MinimapRenderer.WorldToUV(cameraFocus.transform.position);
            _uvOffset = ClampOffset(camUV - Vector2.one * (_uvSize * 0.5f));
        }
    }

    // ── Zoom (scroll wheel) ────────────────────────────────────────────

    private void HandleZoom()
    {
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) < 0.001f) return;

        // Zoom toward the mouse cursor position on the map.
        Vector2 mouseUV  = MouseToUV();
        float   prevSize = _uvSize;
        _uvSize          = Mathf.Clamp(_uvSize - scroll * zoomSpeed, minUVSize, maxUVSize);

        // Shift offset so the point under the cursor stays fixed.
        float scale  = _uvSize / prevSize;
        _uvOffset    = mouseUV - (mouseUV - _uvOffset) * scale;
        _uvOffset    = ClampOffset(_uvOffset);
    }

    // ── Pan (middle mouse) ─────────────────────────────────────────────

    private void HandlePan()
    {
        if (Input.GetMouseButtonDown(2))
        {
            _panning          = true;
            _panStartMousePos = Input.mousePosition;
            _panStartOffset   = _uvOffset;
        }

        if (Input.GetMouseButtonUp(2))
            _panning = false;

        if (!_panning) return;

        Vector2 delta      = (Vector2)Input.mousePosition - _panStartMousePos;
        RectTransform rt   = mapImage.rectTransform;

        // Convert pixel delta to UV delta.
        Vector2 panUV  = new Vector2(
            -delta.x / rt.rect.width,
            -delta.y / rt.rect.height) * _uvSize;

        _uvOffset = ClampOffset(_panStartOffset + panUV);
    }

    // ── Click handling ─────────────────────────────────────────────────

    private void HandleClick()
    {
        // Only act on clicks that land directly on the map image.
        // We cannot use IsPointerOverGameObject() here because the fullscreen
        // map IS a UI element — that check would always block.
        if (!IsPointerOverMapImage()) return;

        if (Input.GetMouseButtonDown(0))
        {
            Vector2 uv       = MouseToUV();
            Vector3 worldPos = MinimapRenderer.UVToWorld(uv);
            cameraFocus.TeleportTo(worldPos);
            SetOpen(false);
        }

        if (Input.GetMouseButtonDown(1))
        {
            Vector2 uv       = MouseToUV();
            Vector3 worldPos = MinimapRenderer.UVToWorld(uv);
            PingManager.PlacePing(worldPos, localPlayerFaction);
        }
    }

    /// <summary>
    /// Returns true if the mouse is currently within the bounds of the map RawImage.
    /// Used instead of IsPointerOverGameObject() which blocks all UI clicks.
    /// </summary>
    private bool IsPointerOverMapImage()
    {
        if (mapImage == null) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(
            mapImage.rectTransform, Input.mousePosition, null);
    }

    // ── Camera indicator ───────────────────────────────────────────────

    private void UpdateCameraIndicator()
    {
        if (cameraIndicator == null) return;

        Vector2 camUV    = MinimapRenderer.WorldToUV(cameraFocus.transform.position);
        // Convert UV to panel-local position.
        RectTransform rt = mapImage.rectTransform;
        Vector2 local    = UVToLocalPoint(camUV, rt);

        cameraIndicator.rectTransform.anchoredPosition = local;
        cameraIndicator.gameObject.SetActive(
            camUV.x >= _uvOffset.x && camUV.x <= _uvOffset.x + _uvSize &&
            camUV.y >= _uvOffset.y && camUV.y <= _uvOffset.y + _uvSize);
    }

    // ── UV rect application ────────────────────────────────────────────

    private void ApplyUVRect()
    {
        var rect            = new Rect(_uvOffset.x, _uvOffset.y, _uvSize, _uvSize);
        mapImage.uvRect     = rect;
        if (pingImage != null) pingImage.uvRect = rect;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>Converts current mouse screen position to a UV on the map texture.</summary>
    private Vector2 MouseToUV()
    {
        RectTransform rt = mapImage.rectTransform;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rt, Input.mousePosition, null, out Vector2 local);

        // local is in the RawImage's local space; normalise to [0,1].
        float u = (local.x - rt.rect.xMin) / rt.rect.width;
        float v = (local.y - rt.rect.yMin) / rt.rect.height;

        // Remap through the current UV rect to get the actual texture UV.
        return new Vector2(
            _uvOffset.x + u * _uvSize,
            _uvOffset.y + v * _uvSize);
    }

    /// <summary>Converts a texture UV to a local point within a RectTransform.</summary>
    private Vector2 UVToLocalPoint(Vector2 uv, RectTransform rt)
    {
        float u = (uv.x - _uvOffset.x) / _uvSize;
        float v = (uv.y - _uvOffset.y) / _uvSize;
        return new Vector2(
            rt.rect.xMin + u * rt.rect.width,
            rt.rect.yMin + v * rt.rect.height);
    }

    /// <summary>Clamps a UV offset so the view rect stays within [0,1].</summary>
    private Vector2 ClampOffset(Vector2 offset) =>
        new Vector2(
            Mathf.Clamp(offset.x, 0f, 1f - _uvSize),
            Mathf.Clamp(offset.y, 0f, 1f - _uvSize));
}
