// Stand-ins for the uGUI package (UnityEngine.UI, UnityEngine.EventSystems) — compile check only.
using System;
using UnityEngine;
using UnityEngine.Events;

namespace UnityEngine.EventSystems
{
    public class UIBehaviour : MonoBehaviour { }
    public class BaseEventData { }
    public class PointerEventData : BaseEventData
    {
        public enum InputButton { Left, Right, Middle }
        public InputButton button;
        public Vector2 position;
        public Vector2 delta;
        public Vector2 scrollDelta;
        public int clickCount;
        public GameObject pointerEnter;
    }
    public interface IEventSystemHandler { }
    public interface IPointerClickHandler : IEventSystemHandler { void OnPointerClick(PointerEventData e); }
    public interface IPointerDownHandler : IEventSystemHandler { void OnPointerDown(PointerEventData e); }
    public interface IPointerUpHandler : IEventSystemHandler { void OnPointerUp(PointerEventData e); }
    public interface IPointerEnterHandler : IEventSystemHandler { void OnPointerEnter(PointerEventData e); }
    public interface IPointerExitHandler : IEventSystemHandler { void OnPointerExit(PointerEventData e); }
    public interface IScrollHandler : IEventSystemHandler { void OnScroll(PointerEventData e); }
    public interface IDragHandler : IEventSystemHandler { void OnDrag(PointerEventData e); }
    public interface IBeginDragHandler : IEventSystemHandler { void OnBeginDrag(PointerEventData e); }
    public interface IEndDragHandler : IEventSystemHandler { void OnEndDrag(PointerEventData e); }
    public class EventSystem : UIBehaviour
    {
        public static EventSystem current;
        public bool IsPointerOverGameObject() => false;
        public bool IsPointerOverGameObject(int pointerId) => false;
        public GameObject currentSelectedGameObject;
        public void SetSelectedGameObject(GameObject go) { }
    }
    public class BaseInputModule : UIBehaviour { }
    public class StandaloneInputModule : BaseInputModule { }
    public class BaseRaycaster : UIBehaviour { }
}

namespace UnityEngine.UI
{
    using UnityEngine.EventSystems;

    public class Graphic : UIBehaviour
    {
        public virtual Color color { get; set; }
        public bool raycastTarget { get; set; }
        public virtual Material material { get; set; }
        public RectTransform rectTransform => null;
        public Canvas canvas => null;
        public void SetAllDirty() { }
        public void SetVerticesDirty() { }
        public void CrossFadeAlpha(float alpha, float duration, bool ignoreTimeScale) { }
        protected virtual void OnPopulateMesh(VertexHelper vh) { }
    }
    public class VertexHelper
    {
        public int currentVertCount => 0;
        public void Clear() { }
        public void AddVert(Vector3 position, Color32 color, Vector4 uv0) { }
        public void AddTriangle(int idx0, int idx1, int idx2) { }
    }
    public class MaskableGraphic : Graphic { public bool maskable { get; set; } }
    public class Image : MaskableGraphic
    {
        public enum Type { Simple, Sliced, Tiled, Filled }
        public enum FillMethod { Horizontal, Vertical, Radial90, Radial180, Radial360 }
        public Sprite sprite { get; set; }
        public Type type { get; set; }
        public FillMethod fillMethod { get; set; }
        public float fillAmount { get; set; }
        public int fillOrigin { get; set; }
        public bool preserveAspect { get; set; }
    }
    public class RawImage : MaskableGraphic
    {
        public Texture texture { get; set; }
        public Rect uvRect { get; set; }
    }
    public class Text : MaskableGraphic
    {
        public string text { get; set; }
        public Font font { get; set; }
        public int fontSize { get; set; }
        public TextAnchor alignment { get; set; }
    }
    public class Selectable : UIBehaviour
    {
        public bool interactable { get; set; }
        public Graphic targetGraphic { get; set; }
        public ColorBlock colors { get; set; }
        public Image image { get; set; }
        public void Select() { }
    }
    public struct ColorBlock
    {
        public Color normalColor, highlightedColor, pressedColor, selectedColor, disabledColor;
        public float colorMultiplier, fadeDuration;
        public static ColorBlock defaultColorBlock => default;
    }
    public class Button : Selectable
    {
        public class ButtonClickedEvent : UnityEvent { }
        public ButtonClickedEvent onClick { get; set; } = new ButtonClickedEvent();
    }
    public class Toggle : Selectable
    {
        public class ToggleEvent : UnityEvent<bool> { }
        public bool isOn { get; set; }
        public ToggleEvent onValueChanged { get; set; } = new ToggleEvent();
    }
    public class Slider : Selectable
    {
        public class SliderEvent : UnityEvent<float> { }
        public float value { get; set; }
        public float minValue { get; set; }
        public float maxValue { get; set; }
        public bool wholeNumbers { get; set; }
        public RectTransform fillRect { get; set; }
        public RectTransform handleRect { get; set; }
        public enum Direction { LeftToRight, RightToLeft, BottomToTop, TopToBottom }
        public Direction direction { get; set; }
        public void SetValueWithoutNotify(float input) { }
        public SliderEvent onValueChanged { get; set; } = new SliderEvent();
    }
    public class ScrollRect : UIBehaviour
    {
        public RectTransform content { get; set; }
        public RectTransform viewport { get; set; }
        public bool horizontal { get; set; }
        public bool vertical { get; set; }
        public Vector2 normalizedPosition { get; set; }
        public float verticalNormalizedPosition { get; set; }
        public float horizontalNormalizedPosition { get; set; }
    }
    public class InputField : Selectable
    {
        public string text { get; set; }
    }
    public class Mask : UIBehaviour { public bool showMaskGraphic { get; set; } }
    public class RectMask2D : UIBehaviour { }
    public class Outline : UIBehaviour { public Color effectColor { get; set; } public Vector2 effectDistance { get; set; } }
    public class Shadow : UIBehaviour { public Color effectColor { get; set; } public Vector2 effectDistance { get; set; } }

    public class LayoutGroup : UIBehaviour
    {
        public RectOffset padding { get; set; } = new RectOffset();
        public TextAnchor childAlignment { get; set; }
    }
    public class HorizontalOrVerticalLayoutGroup : LayoutGroup
    {
        public float spacing { get; set; }
        public bool childControlWidth { get; set; }
        public bool childControlHeight { get; set; }
        public bool childForceExpandWidth { get; set; }
        public bool childForceExpandHeight { get; set; }
        public bool childScaleWidth { get; set; }
        public bool childScaleHeight { get; set; }
        public bool reverseArrangement { get; set; }
    }
    public class HorizontalLayoutGroup : HorizontalOrVerticalLayoutGroup { }
    public class VerticalLayoutGroup : HorizontalOrVerticalLayoutGroup { }
    public class GridLayoutGroup : LayoutGroup
    {
        public enum Corner { UpperLeft, UpperRight, LowerLeft, LowerRight }
        public enum Axis { Horizontal, Vertical }
        public enum Constraint { Flexible, FixedColumnCount, FixedRowCount }
        public Vector2 cellSize { get; set; }
        public Vector2 spacing { get; set; }
        public Corner startCorner { get; set; }
        public Axis startAxis { get; set; }
        public Constraint constraint { get; set; }
        public int constraintCount { get; set; }
    }
    public class LayoutElement : UIBehaviour
    {
        public bool ignoreLayout { get; set; }
        public float minWidth { get; set; }
        public float minHeight { get; set; }
        public float preferredWidth { get; set; }
        public float preferredHeight { get; set; }
        public float flexibleWidth { get; set; }
        public float flexibleHeight { get; set; }
    }
    public class ContentSizeFitter : UIBehaviour
    {
        public enum FitMode { Unconstrained, MinSize, PreferredSize }
        public FitMode horizontalFit { get; set; }
        public FitMode verticalFit { get; set; }
    }
    public class AspectRatioFitter : UIBehaviour { }
    public class CanvasScaler : UIBehaviour
    {
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public enum ScreenMatchMode { MatchWidthOrHeight, Expand, Shrink }
        public ScaleMode uiScaleMode { get; set; }
        public ScreenMatchMode screenMatchMode { get; set; }
        public Vector2 referenceResolution { get; set; }
        public float matchWidthOrHeight { get; set; }
        public float scaleFactor { get; set; }
    }
    public class GraphicRaycaster : BaseRaycaster { }
    public static class LayoutRebuilder
    {
        public static void ForceRebuildLayoutImmediate(RectTransform rect) { }
        public static void MarkLayoutForRebuild(RectTransform rect) { }
    }
    public static class DefaultControls
    {
        public struct Resources { }
        public static GameObject CreateButton(Resources r) => null;
        public static GameObject CreatePanel(Resources r) => null;
    }
}
