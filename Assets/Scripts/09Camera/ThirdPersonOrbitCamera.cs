using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

[RequireComponent(typeof(CinemachineCamera))]
public class ThirdPersonOrbitCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private CinemachineCamera cinemachineCamera;

    [Header("Orbit Speeds")]
    [SerializeField] private float yawSpeed = 180f;
    [SerializeField] private float pitchSpeed = 120f;

    [Header("Pitch Limits")]
    [SerializeField] private float minPitch = -30f;
    [SerializeField] private float maxPitch = 70f;

    [Header("Snap Rotation")]
    [SerializeField] private float snapAngle = 45f;
    [SerializeField] private float snapSpeed = 20f;

    [Header("Zoom")]
    [Tooltip("Distance change per physical wheel notch (see scrollUnitsPerNotch below), not per raw scroll unit.")]
    [SerializeField] private float zoomSpeed = 5f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 15f;
    [SerializeField] private float zoomSmoothTime = 0.1f;
    [Tooltip("Raw units Mouse.current.scroll reports per physical notch. The " +
             "new Input System commonly reports the old Windows WHEEL_DELTA " +
             "value (120) here rather than a normalised 1, which made a single " +
             "notch jump the entire zoom range.")]
    [SerializeField] private float scrollUnitsPerNotch = 120f;

    [Header("Cursor")]
    [SerializeField] private bool lockCursorWhileRotating = false;
    [SerializeField] private bool hideCursorWhileRotating = false;

    private CinemachineOrbitalFollow orbitalFollow;
    private float targetYaw;
    private bool isSnapping = false;
    private float targetDistance;
    private float zoomVelocity;

    public float CurrentYaw => orbitalFollow != null ? orbitalFollow.HorizontalAxis.Value : 0f;

    private void Reset()
    {
        cinemachineCamera = GetComponent<CinemachineCamera>();
    }

    private void Awake()
    {
        if (cinemachineCamera == null)
            cinemachineCamera = GetComponent<CinemachineCamera>();

        orbitalFollow = cinemachineCamera.GetComponent<CinemachineOrbitalFollow>();

        if (orbitalFollow == null)
        {
            Debug.LogError("Missing CinemachineOrbitalFollow.");
            enabled = false;
            return;
        }

        targetYaw = orbitalFollow.HorizontalAxis.Value;
        targetDistance = orbitalFollow.Radius;
    }

    private void Update()
    {
        if (orbitalFollow == null || Mouse.current == null || Keyboard.current == null)
            return;

        bool middleMouse = Mouse.current.middleButton.isPressed;
        bool rightMouse = Mouse.current.rightButton.isPressed;
        bool ctrlHeld = Keyboard.current.ctrlKey.isPressed;

        // Suspend orbit when the fullscreen map is open — middle mouse
        // is used for map panning instead.
        bool rotating = !OrbitSuspended &&
                        (middleMouse || (rightMouse && ctrlHeld));

        HandleMouseOrbit(rotating);
        HandleSnapInput(rotating);
        HandleSnapRotation();
        HandleZoom();
        HandleCursor(rotating);
    }

    private void HandleMouseOrbit(bool rotating)
    {
        if (!rotating)
            return;

        Vector2 mouseDelta = Mouse.current.delta.ReadValue();

        if (mouseDelta.sqrMagnitude > 0.0001f)
            isSnapping = false;

        InputAxis horizontal = orbitalFollow.HorizontalAxis;
        horizontal.Value += mouseDelta.x * yawSpeed * Time.deltaTime;
        orbitalFollow.HorizontalAxis = horizontal;

        InputAxis vertical = orbitalFollow.VerticalAxis;
        vertical.Value -= mouseDelta.y * pitchSpeed * Time.deltaTime;
        vertical.Value = Mathf.Clamp(vertical.Value, minPitch, maxPitch);
        orbitalFollow.VerticalAxis = vertical;

        targetYaw = horizontal.Value;
    }

    private void HandleSnapInput(bool rotating)
    {
        if (rotating)
            return;

        if (Keyboard.current.qKey.wasPressedThisFrame)
        {
            float currentYaw = orbitalFollow.HorizontalAxis.Value;
            targetYaw = GetNextSnapAngle(currentYaw, 1);
            isSnapping = true;
        }

        if (Keyboard.current.eKey.wasPressedThisFrame)
        {
            float currentYaw = orbitalFollow.HorizontalAxis.Value;
            targetYaw = GetNextSnapAngle(currentYaw, -1);
            isSnapping = true;
        }
    }

    private float GetNextSnapAngle(float currentYaw, int direction)
    {
        float step = snapAngle;
        float remainder = Mathf.Repeat(currentYaw, step);

        const float epsilon = 0.01f;
        bool isOnSnapPoint = remainder < epsilon || remainder > step - epsilon;

        if (direction > 0)
            return isOnSnapPoint ? Mathf.Round(currentYaw / step) * step + step : Mathf.Ceil(currentYaw / step) * step;

        return isOnSnapPoint ? Mathf.Round(currentYaw / step) * step - step : Mathf.Floor(currentYaw / step) * step;
    }

    private void HandleSnapRotation()
    {
        if (!isSnapping)
            return;

        InputAxis horizontal = orbitalFollow.HorizontalAxis;

        float newYaw = Mathf.LerpAngle(horizontal.Value, targetYaw, snapSpeed * Time.deltaTime);

        horizontal.Value = newYaw;
        orbitalFollow.HorizontalAxis = horizontal;

        if (Mathf.Abs(Mathf.DeltaAngle(newYaw, targetYaw)) < 0.5f)
        {
            horizontal.Value = targetYaw;
            orbitalFollow.HorizontalAxis = horizontal;
            isSnapping = false;
        }
    }

    // ── Suspension flags (set by MinimapFullscreen while map is open) ──
    public static bool ZoomSuspended    = false;
    public static bool OrbitSuspended   = false;

    private void HandleZoom()
    {
        if (ZoomSuspended) return;

        float scroll = Mouse.current.scroll.ReadValue().y;

        if (Mathf.Abs(scroll) > 0.01f)
        {
            float notches = scroll / scrollUnitsPerNotch;
            targetDistance -= notches * zoomSpeed;
            targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
        }

        float newDistance = Mathf.SmoothDamp(
            orbitalFollow.Radius,
            targetDistance,
            ref zoomVelocity,
            zoomSmoothTime
        );

        orbitalFollow.Radius = newDistance;
    }

    private void HandleCursor(bool rotating)
    {
        if (lockCursorWhileRotating)
            Cursor.lockState = rotating ? CursorLockMode.Locked : CursorLockMode.None;

        if (hideCursorWhileRotating)
            Cursor.visible = !rotating;
    }
}