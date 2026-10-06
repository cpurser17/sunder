using UnityEngine;

/// <summary>
/// Top-down orthographic camera for the level editor. Driven by
/// LevelEditorController (Tick) rather than its own Update, so the
/// controller decides when the pointer or keyboard belongs to the UI.
///
///   Pan  — WASD / arrow keys, or drag with the right or middle mouse button
///   Zoom — scroll wheel, towards the cursor
/// </summary>
[RequireComponent(typeof(Camera))]
public class LevelEditorCamera : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Keyboard pan speed, in screen-heights per second.")]
    [SerializeField] private float keyPanSpeed = 1.2f;
    [Tooltip("Fraction of the view size each scroll notch zooms by.")]
    [SerializeField] private float zoomStep = 0.15f;
    [SerializeField] private float minViewSize = 3f;
    [SerializeField] private float maxViewSize = 200f;
    [Tooltip("Height above the grid the camera sits at. Any value works for an " +
             "orthographic camera; it only needs to clear the markers.")]
    [SerializeField] private float height = 50f;

    private Camera  _cam;
    private float   _maxSize;
    private bool    _dragging;
    private Vector3 _dragGroundPoint;

    public Camera Camera => _cam;

    private void Awake()
    {
        _cam = GetComponent<Camera>();
        _maxSize = maxViewSize;
        _cam.orthographic  = true;
        _cam.nearClipPlane = 0.1f;
        _cam.farClipPlane  = height * 4f;
        _cam.clearFlags    = CameraClearFlags.SolidColor;
        _cam.backgroundColor = new Color(0.08f, 0.08f, 0.1f);
        transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    /// <summary>Centres the view on the grid and zooms to fit all of it.</summary>
    public void Frame(GridManager2D grid)
    {
        float w = grid.Width  * grid.CellSize;
        float h = grid.Height * grid.CellSize;
        Vector3 centre = grid.transform.position + new Vector3(w * 0.5f, 0f, h * 0.5f);

        transform.position = centre + Vector3.up * height;
        float fit = Mathf.Max(h * 0.5f, w * 0.5f / Mathf.Max(0.1f, _cam.aspect)) * 1.1f;
        _maxSize = Mathf.Max(maxViewSize, fit * 1.5f); // a huge level can still be seen whole
        _cam.orthographicSize = Mathf.Clamp(fit, minViewSize, _maxSize);
    }

    /// <summary>Where a screen point lands on the grid plane (y = 0).</summary>
    public bool TryGetGroundPoint(Vector3 screenPoint, out Vector3 world)
    {
        var ray = _cam.ScreenPointToRay(screenPoint);
        if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float d))
        {
            world = ray.GetPoint(d);
            return true;
        }
        world = default;
        return false;
    }

    /// <param name="pointerFree">False while the pointer is over the editor UI.</param>
    /// <param name="keyboardFree">False while a text field has focus.</param>
    public void Tick(bool pointerFree, bool keyboardFree)
    {
        if (keyboardFree) KeyboardPan();

        // Drags continue over the UI once started, so a pan isn't cut short.
        bool dragButton = Input.GetMouseButton(1) || Input.GetMouseButton(2);
        if (!dragButton) _dragging = false;
        else if (!_dragging && pointerFree &&
                 (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)) &&
                 TryGetGroundPoint(Input.mousePosition, out _dragGroundPoint))
            _dragging = true;

        if (_dragging && TryGetGroundPoint(Input.mousePosition, out var now))
            transform.position += _dragGroundPoint - now;

        float scroll = Input.mouseScrollDelta.y;
        if (pointerFree && Mathf.Abs(scroll) > 0.01f) Zoom(scroll);
    }

    private void KeyboardPan()
    {
        float x = 0f, z = 0f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  x -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  z -= 1f;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    z += 1f;
        if (x == 0f && z == 0f) return;

        // Ctrl+S saves; don't also drift the camera down.
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return;

        float speed = _cam.orthographicSize * 2f * keyPanSpeed * Time.unscaledDeltaTime;
        transform.position += new Vector3(x, 0f, z).normalized * speed;
    }

    private void Zoom(float scroll)
    {
        bool anchored = TryGetGroundPoint(Input.mousePosition, out var before);

        float size = _cam.orthographicSize * Mathf.Pow(1f - zoomStep, scroll);
        _cam.orthographicSize = Mathf.Clamp(size, minViewSize, _maxSize);

        // Keep the point under the cursor where it was.
        if (anchored && TryGetGroundPoint(Input.mousePosition, out var after))
            transform.position += before - after;
    }
}
