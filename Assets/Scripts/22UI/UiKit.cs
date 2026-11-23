using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Small helpers for building placeholder UI in code — the Start menu's
/// panels, the Campaign screen and the results overlay — so screens work
/// before they have art. Everything is a plain uGUI object you can restyle
/// or replace later.
/// </summary>
public static class UiKit
{
    public static readonly Color Panel      = new(0.08f, 0.07f, 0.10f, 0.94f);
    public static readonly Color PanelLight = new(0.16f, 0.14f, 0.20f, 1f);
    public static readonly Color ButtonColour = new(0.26f, 0.21f, 0.32f, 1f);
    public static readonly Color ButtonOn   = new(0.62f, 0.42f, 0.18f, 1f);
    public static readonly Color Disabled   = new(0.18f, 0.18f, 0.18f, 1f);
    public static readonly Color Text       = new(0.93f, 0.90f, 0.85f, 1f);
    public static readonly Color TextDim    = new(0.65f, 0.62f, 0.58f, 1f);

    /// <summary>A screen-space overlay canvas scaled to 1920x1080, with an EventSystem if the scene has none.</summary>
    public static RectTransform Canvas(string name, int sortingOrder, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        if (parent != null) go.transform.SetParent(parent, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        EnsureEventSystem();
        return (RectTransform)go.transform;
    }

    public static void EnsureEventSystem()
    {
        if (EventSystem.current != null || UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<StandaloneInputModule>();
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    /// <summary>Stretches a rect over its parent, inset by the given margins (left, bottom, right, top).</summary>
    public static RectTransform Fill(RectTransform rect, float left = 0f, float bottom = 0f, float right = 0f, float top = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
        return rect;
    }

    /// <summary>A rect anchored by fractions of its parent (0-1), e.g. the left third.</summary>
    public static RectTransform Anchor(RectTransform rect, float xMin, float yMin, float xMax, float yMax, float margin = 0f)
    {
        rect.anchorMin = new Vector2(xMin, yMin);
        rect.anchorMax = new Vector2(xMax, yMax);
        rect.offsetMin = new Vector2(margin, margin);
        rect.offsetMax = new Vector2(-margin, -margin);
        return rect;
    }

    public static Image Box(string name, Transform parent, Color colour)
    {
        var rect  = Rect(name, parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.color = colour;
        return image;
    }

    public static TextMeshProUGUI Label(string name, Transform parent, string text, float size,
                                        TextAlignmentOptions align = TextAlignmentOptions.Left, Color? colour = null)
    {
        var rect = Rect(name, parent);
        var t = rect.gameObject.AddComponent<TextMeshProUGUI>();
        t.text          = text;
        t.fontSize      = size;
        t.alignment     = align;
        t.color         = colour ?? Text;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>A button with a label. Returns the button; its label is the child TextMeshProUGUI.</summary>
    public static Button Button(string name, Transform parent, string label, Action onClick, float fontSize = 24f)
    {
        var image = Box(name, parent, ButtonColour);
        var button = image.gameObject.AddComponent<UnityEngine.UI.Button>();
        button.targetGraphic = image;
        var text = Label("Label", image.transform, label, fontSize, TextAlignmentOptions.Center);
        Fill(text.rectTransform, 8f, 2f, 8f, 2f);
        if (onClick != null) button.onClick.AddListener(() => onClick());
        return button;
    }

    public static TextMeshProUGUI LabelOf(Button button) => button.GetComponentInChildren<TextMeshProUGUI>(true);

    /// <summary>Shows a button as selected, normal or unavailable.</summary>
    public static void Style(Button button, bool selected, bool interactable = true)
    {
        button.interactable = interactable;
        if (button.targetGraphic is Image img) img.color = !interactable ? Disabled : selected ? ButtonOn : ButtonColour;
        var label = LabelOf(button);
        if (label != null) label.color = interactable ? Text : TextDim;
    }

    /// <summary>A vertical list that sizes its children: give each a LayoutElement height (see Height).</summary>
    public static VerticalLayoutGroup Column(RectTransform rect, float spacing = 6f, int padding = 8)
    {
        var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = spacing;
        layout.padding = new RectOffset(padding, padding, padding, padding);
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    public static HorizontalLayoutGroup Row(RectTransform rect, float spacing = 6f, int padding = 0)
    {
        var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = spacing;
        layout.padding = new RectOffset(padding, padding, padding, padding);
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = true;
        return layout;
    }

    public static T Height<T>(T component, float height) where T : Component
    {
        var element = component.GetComponent<LayoutElement>() ?? component.gameObject.AddComponent<LayoutElement>();
        element.preferredHeight = height;
        element.minHeight       = height;
        return component;
    }

    public static T Width<T>(T component, float width) where T : Component
    {
        var element = component.GetComponent<LayoutElement>() ?? component.gameObject.AddComponent<LayoutElement>();
        element.preferredWidth = width;
        element.flexibleWidth  = 0f;
        return component;
    }

    /// <summary>A scrolling list: returns the content rect to add rows to (laid out top-down).</summary>
    public static RectTransform ScrollList(string name, RectTransform parent)
    {
        var view = Box(name, parent, PanelLight).rectTransform;
        view.gameObject.AddComponent<RectMask2D>();
        var scroll = view.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;

        var content = Rect("Content", view);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot     = new Vector2(0.5f, 1f);
        content.offsetMin = content.offsetMax = Vector2.zero;
        Column(content, 4f, 6);
        var fit = content.gameObject.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = view;
        scroll.content  = content;
        return content;
    }

    public static void Clear(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
    }

    public static Color FromHex(string hex, Color fallback) =>
        !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
}
