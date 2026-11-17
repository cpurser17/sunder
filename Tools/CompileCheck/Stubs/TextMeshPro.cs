// Stand-ins for TextMesh Pro — compile check only.
using UnityEngine;

namespace TMPro
{
    public enum TextAlignmentOptions
    {
        TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight,
        MidlineLeft, Midline, MidlineRight, CaplineLeft, Capline, CaplineRight,
        BaselineLeft, Baseline, BaselineRight, Justified, Flush, TopJustified, BottomJustified,
    }
    public enum FontStyles { Normal = 0, Bold = 1, Italic = 2, Underline = 4, LowerCase = 8, UpperCase = 16, SmallCaps = 32, Strikethrough = 64 }
    public enum TextOverflowModes { Overflow, Ellipsis, Masking, Truncate, ScrollRect, Page, Linked }
    public enum TextWrappingModes { NoWrap, Normal, PreserveWhitespace, PreserveWhitespaceNoWrap }

    public class TMP_FontAsset : ScriptableObject { }

    public class TMP_Text : UnityEngine.UI.MaskableGraphic
    {
        public string text { get; set; }
        public float fontSize { get; set; }
        public float fontSizeMin { get; set; }
        public float fontSizeMax { get; set; }
        public bool enableAutoSizing { get; set; }
        public FontStyles fontStyle { get; set; }
        public TextAlignmentOptions alignment { get; set; }
        public TextOverflowModes overflowMode { get; set; }
        public TextWrappingModes textWrappingMode { get; set; }
        public bool enableWordWrapping { get; set; }
        public bool richText { get; set; }
        public TMP_FontAsset font { get; set; }
        public Vector4 margin { get; set; }
        public float lineSpacing { get; set; }
        public float characterSpacing { get; set; }
        public float preferredWidth => 0f;
        public float preferredHeight => 0f;
        public void SetText(string s) { }
        public Vector2 GetPreferredValues() => default;
        public Vector2 GetPreferredValues(string s) => default;
        public Vector2 GetPreferredValues(string s, float width, float height) => default;
        public void ForceMeshUpdate() { }
    }
    public class TextMeshProUGUI : TMP_Text { }
    public class TextMeshPro : TMP_Text { }

    public class TMP_InputField : UnityEngine.UI.Selectable
    {
        public class SubmitEvent : UnityEngine.Events.UnityEvent<string> { }
        public class OnChangeEvent : UnityEngine.Events.UnityEvent<string> { }
        public string text { get; set; }
        public TMP_Text textComponent { get; set; }
        public UnityEngine.UI.Graphic placeholder { get; set; }
        public SubmitEvent onSubmit { get; set; } = new SubmitEvent();
        public SubmitEvent onEndEdit { get; set; } = new SubmitEvent();
        public OnChangeEvent onValueChanged { get; set; } = new OnChangeEvent();
    }

    public static class TMP_DefaultControls
    {
        public struct Resources { public Sprite standard, background, inputField, knob, checkmark, dropdown, mask; }
        public static GameObject CreateButton(Resources r) => null;
        public static GameObject CreateText(Resources r) => null;
        public static GameObject CreateInputField(Resources r) => null;
    }
}
