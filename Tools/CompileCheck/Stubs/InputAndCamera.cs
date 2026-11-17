// Stand-ins for the Input System and Cinemachine packages — compile check only.
using UnityEngine;

namespace UnityEngine.InputSystem
{
    namespace Controls
    {
        public class ButtonControl
        {
            public bool isPressed => false;
            public bool wasPressedThisFrame => false;
            public bool wasReleasedThisFrame => false;
        }
        public class KeyControl : ButtonControl { }
        public class Vector2Control
        {
            public Vector2 ReadValue() => default;
        }
    }

    public class Keyboard
    {
        public static Keyboard current;
        public Controls.KeyControl this[Key key] => null;
        public Controls.KeyControl aKey, bKey, cKey, dKey, eKey, fKey, qKey, rKey, sKey, wKey, xKey, zKey;
        public Controls.KeyControl ctrlKey, shiftKey, altKey, leftShiftKey, rightShiftKey, leftCtrlKey, spaceKey, escapeKey, tabKey, enterKey;
    }
    public enum Key { None, Space, Enter, Tab, Escape, A, B, C, D, E, F, Q, R, S, W, X, Z }

    public class Mouse
    {
        public static Mouse current;
        public Controls.ButtonControl leftButton, rightButton, middleButton;
        public Controls.Vector2Control delta, scroll, position;
    }
}

namespace Unity.Cinemachine
{
    public struct InputAxis
    {
        public float Value;
        public float Center;
        public Vector2 Range;
        public bool Wrap;
        public float ClampValue(float v) => v;
    }
    public class CinemachineCamera : MonoBehaviour
    {
        public bool PreviousStateIsValid { get; set; }
        public Transform Follow { get; set; }
        public Transform LookAt { get; set; }
        public int Priority { get; set; }
    }
    public class CinemachineOrbitalFollow : MonoBehaviour
    {
        public InputAxis HorizontalAxis;
        public InputAxis VerticalAxis;
        public InputAxis RadialAxis;
        public float Radius;
    }
    public class CinemachineBrain : MonoBehaviour
    {
        public bool IgnoreTimeScale;
    }
}
