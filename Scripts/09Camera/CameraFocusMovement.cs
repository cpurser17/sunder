using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

public class CameraFocusMovement : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed        = 5f;
    [SerializeField] private float sprintMultiplier = 2f;
    [SerializeField] private float smoothTime       = 0.1f;

    [Header("Height Lock")]
    [SerializeField] private float fixedHeight = 0.8f;

    [Header("Reference")]
    [Tooltip("Assign your ThirdPersonOrbitCamera here")]
    [SerializeField] private ThirdPersonOrbitCamera orbitCamera;

    private Vector3 _currentVelocity;

    // ── MoveTo support ─────────────────────────────────────────────────
    private bool    _movingToTarget;
    private Vector3 _moveTarget;
    [Tooltip("Speed used when jumping to a minimap location (world units/s).")]
    [SerializeField] private float jumpSpeed = 12f;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Start()
    {
        Vector3 pos = transform.position;
        pos.y = fixedHeight;
        transform.position = pos;
    }

    private void Update()
    {
        if (Keyboard.current == null || orbitCamera == null) return;

        // If we're jumping to a target, interpolate toward it and suppress WASD.
        if (_movingToTarget)
        {
            transform.position = Vector3.MoveTowards(
                transform.position, _moveTarget, jumpSpeed * Time.deltaTime);

            if (Vector3.Distance(transform.position, _moveTarget) < 0.05f)
            {
                transform.position = _moveTarget;
                _movingToTarget    = false;
            }
            return;
        }

        // Normal WASD movement.
        Vector2 input = Vector2.zero;
        if (Keyboard.current.wKey.isPressed) input.y += 1f;
        if (Keyboard.current.sKey.isPressed) input.y -= 1f;
        if (Keyboard.current.aKey.isPressed) input.x -= 1f;
        if (Keyboard.current.dKey.isPressed) input.x += 1f;
        input = Vector2.ClampMagnitude(input, 1f);

        float speed = moveSpeed;
        if (Keyboard.current.leftShiftKey.isPressed) speed *= sprintMultiplier;

        float     yaw         = orbitCamera.CurrentYaw;
        Quaternion yawRotation = Quaternion.Euler(0f, yaw, 0f);
        Vector3   forward     = yawRotation * Vector3.forward;
        Vector3   right       = yawRotation * Vector3.right;

        Vector3 targetVelocity = (forward * input.y + right * input.x) * speed;

        float lerpFactor  = smoothTime > 0.0001f ? Time.deltaTime / smoothTime : 1f;
        _currentVelocity  = Vector3.Lerp(_currentVelocity, targetVelocity, lerpFactor);
        transform.position += _currentVelocity * Time.deltaTime;

        Vector3 pos = transform.position;
        pos.y = fixedHeight;
        transform.position = pos;
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>
    /// Smoothly moves the camera focus point to the given world position.
    /// Called by MinimapFullscreen when the player left-clicks the map.
    /// Y is locked to fixedHeight regardless of the input position.
    /// </summary>
    /// <summary>Smoothly moves the camera focus to the given world position.</summary>
    public void MoveTo(Vector3 worldPosition)
    {
        _moveTarget      = new Vector3(worldPosition.x, fixedHeight, worldPosition.z);
        _movingToTarget  = true;
        _currentVelocity = Vector3.zero;
    }

    /// <summary>Instantly teleports the camera focus to the given world position.</summary>
    public void TeleportTo(Vector3 worldPosition)
    {
        transform.position = new Vector3(worldPosition.x, fixedHeight, worldPosition.z);
        _movingToTarget    = false;
        _currentVelocity   = Vector3.zero;

        // Tell Cinemachine to snap immediately rather than smoothly
        // following the focus point to its new position.
        if (orbitCamera != null)
        {
            var vcam = orbitCamera.GetComponent<CinemachineCamera>();
            if (vcam != null) vcam.PreviousStateIsValid = false;
        }
    }
}
