using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages active pings: world-space 3D markers and 2D dots on the ping
/// overlay texture.
///
/// Scene setup
/// -----------
/// 1. Attach to the MinimapSystem GO alongside MinimapRenderer.
/// 2. Assign tileRegistry.
/// 3. Optionally assign pingPrefab — if null a programmatic cylinder is used.
/// MinimapRenderer is resolved via singleton — no Inspector reference needed.
/// </summary>
public class PingManager : MonoBehaviour
{
    public static PingManager Instance { get; private set; }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private TileRegistry tileRegistry;

    [Header("Ping Settings")]
    [SerializeField] private float       pingLifetime  = 30f;
    [SerializeField] private int         pingDotRadius = 1;
    [Tooltip("Optional prefab for the 3D world marker. If null a cylinder is used.")]
    [SerializeField] private GameObject  pingPrefab;
    [SerializeField] private float       markerHeight  = 1f;
    [SerializeField] private float       markerScale   = 0.1f;

    // ── Resolved via singleton ─────────────────────────────────────────
    private MinimapRenderer MinimapRenderer => MinimapRenderer.Instance;

    // ── Runtime ────────────────────────────────────────────────────────
    private Texture2D          _overlayTexture;
    private bool               _overlayDirty;
    private readonly List<ActivePing> _activePings = new();

    public Texture2D OverlayTexture => _overlayTexture;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        // Only assign if not already set — prevents a destroyed duplicate
        // from overwriting the valid instance.
        if (Instance == null) Instance = this;
    }

    private void Start()
    {
        MinimapRenderer.OnTextureReady += InitialiseOverlay;

        // Immediate call in case texture already exists.
        if (MinimapRenderer != null && MinimapRenderer.Texture != null)
        {
            MinimapRenderer.OnTextureReady -= InitialiseOverlay;
            InitialiseOverlay();
        }
    }

    private void OnDestroy()
    {
        MinimapRenderer.OnTextureReady -= InitialiseOverlay;
    }

    private void LateUpdate()
    {
        // Lazy fallback — catches any remaining timing edge cases.
        if (_overlayTexture == null)
        {
            if (MinimapRenderer?.Texture != null)
                InitialiseOverlay();

            return;
        }

        if (!_overlayDirty) return;
        _overlayTexture.Apply();
        _overlayDirty = false;
    }

    // ── Initialisation ─────────────────────────────────────────────────

    private void InitialiseOverlay()
    {
        MinimapRenderer.OnTextureReady -= InitialiseOverlay;

        if (MinimapRenderer?.Texture == null)
        {
            Debug.LogWarning("[PingManager] InitialiseOverlay called but texture still null.");
            return;
        }

        int w = MinimapRenderer.Texture.width;
        int h = MinimapRenderer.Texture.height;

        _overlayTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode   = TextureWrapMode.Clamp,
            name       = "MinimapPingOverlay",
        };

        ClearOverlay();
        Debug.Log($"[PingManager] Overlay created: {w}x{h}");
    }

    // ── Public API ─────────────────────────────────────────────────────

    public void PlacePing(Vector3 worldPos, FactionID faction)
    {
        if (_overlayTexture == null)
        {
            // Try to initialise now as a last resort.
            if (MinimapRenderer?.Texture != null)
            {
                InitialiseOverlay();
            }
            else
            {
                Debug.LogWarning("[PingManager] Overlay not ready yet.");
                return;
            }
        }

        Color colour = GetFactionColour(faction);
        Spawn3DMarker(worldPos, colour);

        Vector2 uv = MinimapRenderer.WorldToUV(worldPos);
        int px     = Mathf.RoundToInt(uv.x * _overlayTexture.width);
        int py     = Mathf.RoundToInt(uv.y * _overlayTexture.height);

        DrawDot(px, py, colour);

        var ping = new ActivePing { pixelX = px, pixelY = py };
        _activePings.Add(ping);
        ping.clearCoroutine = StartCoroutine(ClearPingAfterDelay(ping, pingLifetime));
    }

    // ── 3D marker ──────────────────────────────────────────────────────

    private void Spawn3DMarker(Vector3 worldPos, Color colour)
    {
        GameObject marker;
        Vector3    pos = new Vector3(worldPos.x, markerHeight, worldPos.z);

        if (pingPrefab != null)
        {
            marker = Instantiate(pingPrefab, pos, Quaternion.identity);
        }
        else
        {
            marker                      = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name                 = "PingMarker3D";
            marker.transform.position   = pos;
            marker.transform.localScale = new Vector3(markerScale, markerHeight * 0.5f, markerScale);
            Destroy(marker.GetComponent<Collider>());
            marker.GetComponent<Renderer>().material =
                new Material(Shader.Find("Unlit/Color")) { color = colour };
        }

        marker.AddComponent<PingMarker>().Initialise(colour, pingLifetime);
    }

    // ── Overlay drawing ────────────────────────────────────────────────

    private void DrawDot(int cx, int cy, Color colour)
    {
        int w = _overlayTexture.width;
        int h = _overlayTexture.height;

        for (int dx = -pingDotRadius; dx <= pingDotRadius; dx++)
        for (int dy = -pingDotRadius; dy <= pingDotRadius; dy++)
        {
            if (dx * dx + dy * dy > pingDotRadius * pingDotRadius) continue;
            _overlayTexture.SetPixel(
                Mathf.Clamp(cx + dx, 0, w - 1),
                Mathf.Clamp(cy + dy, 0, h - 1),
                colour);
        }
        _overlayDirty = true;
    }

    private void ClearDot(int cx, int cy)
    {
        int w = _overlayTexture.width;
        int h = _overlayTexture.height;

        for (int dx = -pingDotRadius; dx <= pingDotRadius; dx++)
        for (int dy = -pingDotRadius; dy <= pingDotRadius; dy++)
        {
            if (dx * dx + dy * dy > pingDotRadius * pingDotRadius) continue;
            _overlayTexture.SetPixel(
                Mathf.Clamp(cx + dx, 0, w - 1),
                Mathf.Clamp(cy + dy, 0, h - 1),
                Color.clear);
        }
        _overlayDirty = true;
    }

    private void ClearOverlay()
    {
        var pixels = new Color[_overlayTexture.width * _overlayTexture.height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.clear;
        _overlayTexture.SetPixels(pixels);
        _overlayTexture.Apply();
    }

    private IEnumerator ClearPingAfterDelay(ActivePing ping, float delay)
    {
        yield return new WaitForSeconds(delay);
        ClearDot(ping.pixelX, ping.pixelY);
        _activePings.Remove(ping);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private Color GetFactionColour(FactionID faction)
    {
        Color col = tileRegistry.GetColour(TileType.Tunnel, faction);
        col.a     = 1f;
        return col;
    }

    private class ActivePing
    {
        public int       pixelX;
        public int       pixelY;
        public Coroutine clearCoroutine;
    }
}
