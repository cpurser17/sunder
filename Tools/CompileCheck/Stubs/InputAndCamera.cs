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

    public enum InputActionType { Value, Button, PassThrough }

    public struct InputBinding
    {
        public string name;
        public string path;
        public string overridePath;
        public string effectivePath => overridePath ?? path;
        public bool isComposite;
        public bool isPartOfComposite;
        [System.Flags] public enum DisplayStringOptions { None = 0, DontUseShortDisplayNames = 1, DontOmitDevice = 2, DontIncludeInteractions = 4, IgnoreBindingOverrides = 8 }
    }

    public class InputAction
    {
        public InputAction(string name = null, InputActionType type = default, string binding = null) { }
        public string name => null;
        public InputActionType type => default;
        public InputActionMap actionMap => null;
        public System.Collections.Generic.IReadOnlyList<InputBinding> bindings => null;
        public bool enabled => false;
        public void Enable() { }
        public void Disable() { }
        public bool IsPressed() => false;
        public bool WasPressedThisFrame() => false;
        public bool WasReleasedThisFrame() => false;
        public TValue ReadValue<TValue>() where TValue : struct => default;
    }

    public class InputActionMap
    {
        public InputActionMap(string name = null) { }
        public string name => null;
        public System.Collections.Generic.IReadOnlyList<InputAction> actions => null;
        public InputAction FindAction(string nameOrId, bool throwIfNotFound = false) => null;
        public void Enable() { }
        public void Disable() { }
    }

    public class InputActionAsset : ScriptableObject
    {
        public System.Collections.Generic.IReadOnlyList<InputActionMap> actionMaps => null;
        public InputActionMap FindActionMap(string nameOrId, bool throwIfNotFound = false) => null;
        public InputAction FindAction(string actionNameOrId, bool throwIfNotFound = false) => null;
        public void Enable() { }
        public void Disable() { }
    }

    public static class InputActionSetupExtensions
    {
        public struct BindingSyntax
        {
            public BindingSyntax WithName(string name) => this;
        }
        public struct CompositeSyntax
        {
            public CompositeSyntax With(string name, string binding, string groups = null, string processors = null) => this;
        }
        public static InputActionMap AddActionMap(this InputActionAsset asset, string name) => null;
        public static InputAction AddAction(this InputActionMap map, string name, InputActionType type = default,
                                            string binding = null, string interactions = null, string processors = null,
                                            string groups = null, string expectedControlLayout = null) => null;
        public static BindingSyntax AddBinding(this InputAction action, string path, string interactions = null,
                                               string processors = null, string groups = null) => default;
        public static CompositeSyntax AddCompositeBinding(this InputAction action, string composite,
                                                          string interactions = null, string processors = null) => default;
    }

    public static class InputActionRebindingExtensions
    {
        public static string SaveBindingOverridesAsJson(this InputActionAsset actions) => null;
        public static void LoadBindingOverridesFromJson(this InputActionAsset actions, string json, bool removeExisting = true) { }
        public static void RemoveAllBindingOverrides(this InputActionAsset actions) { }
        public static string GetBindingDisplayString(this InputAction action, int bindingIndex, InputBinding.DisplayStringOptions options = default) => null;
        public static string GetBindingDisplayString(this InputAction action, InputBinding.DisplayStringOptions options = default, string group = null) => null;
        public static RebindingOperation PerformInteractiveRebinding(this InputAction action, int bindingIndex = -1) => null;
        public sealed class RebindingOperation : System.IDisposable
        {
            public RebindingOperation WithControlsExcluding(string path) => this;
            public RebindingOperation WithCancelingThrough(string binding) => this;
            public RebindingOperation OnMatchWaitForAnother(float seconds) => this;
            public RebindingOperation OnComplete(System.Action<RebindingOperation> callback) => this;
            public RebindingOperation OnCancel(System.Action<RebindingOperation> callback) => this;
            public RebindingOperation Start() => this;
            public void Cancel() { }
            public void Dispose() { }
        }
    }

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
