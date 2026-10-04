using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Sunder > Create HUD Footer: builds the bottom-of-screen HUD footer in the
/// open scene and wires it up, so nothing has to be assembled by hand:
///
///   HUDFooter (HudFooter)            anchored to the bottom, full width
///     TabBar                         Rooms | Spells | Traps &amp; Doors | Minions … [–]
///     Body                           hidden when minimised
///       RoomsTab   (FooterTab, Grid Layout Group, hotkey Tab)
///       SpellsTab / TrapsDoorsTab    (FooterTab, Grid Layout Group, no hotkey yet)
///       MinionsTab                   (FooterTab, placeholder text)
///     RoomButtonTemplate             inactive; cloned per room
///   HotkeyPopup (HotkeyPopup)        just above the footer, faded out
///
/// It goes under the selected Canvas (or one named HUDCanvas, or the first
/// Canvas), and points the scene's HUDController2D at the Rooms tab and the
/// template. Everything it makes is ordinary UI — restyle it freely; re-running
/// won't touch an existing footer.
/// </summary>
public static class HudFooterBuilder
{
    private const float FooterHeight = 170f;
    private const float TabBarHeight = 30f;
    private static readonly Vector2 CellSize = new(170f, 40f);

    [MenuItem("Sunder/Create HUD Footer", priority = 30)]
    public static void Create()
    {
        var existing = Object.FindAnyObjectByType<HudFooter>(FindObjectsInactive.Include);
        if (existing != null)
        {
            EditorGUIUtility.PingObject(existing);
            EditorUtility.DisplayDialog("HUD Footer", $"The scene already has a footer ({existing.name}) — left unchanged.", "OK");
            return;
        }

        var canvas = FindCanvas();
        if (canvas == null)
        {
            EditorUtility.DisplayDialog("HUD Footer", "No Canvas in the open scene to put the footer on.", "OK");
            return;
        }

        var resources = new TMP_DefaultControls.Resources
        {
            standard   = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
        };

        // ── Footer ──
        var footer = NewUI("HUDFooter", canvas.transform);
        var footerRect = (RectTransform)footer.transform;
        footerRect.anchorMin = new Vector2(0f, 0f);
        footerRect.anchorMax = new Vector2(1f, 0f);
        footerRect.pivot     = new Vector2(0.5f, 0f);
        footerRect.sizeDelta = new Vector2(0f, FooterHeight);
        footerRect.anchoredPosition = Vector2.zero;
        footer.AddComponent<Image>().color = new Color(0.08f, 0.07f, 0.09f, 0.85f);
        var hudFooter = footer.AddComponent<HudFooter>();

        // ── Tab bar ──
        var tabBar = NewUI("TabBar", footer.transform);
        var tabBarRect = (RectTransform)tabBar.transform;
        tabBarRect.anchorMin = new Vector2(0f, 1f);
        tabBarRect.anchorMax = new Vector2(1f, 1f);
        tabBarRect.pivot     = new Vector2(0.5f, 1f);
        tabBarRect.sizeDelta = new Vector2(0f, TabBarHeight);
        var bar = tabBar.AddComponent<HorizontalLayoutGroup>();
        bar.padding = new RectOffset(6, 6, 3, 3);
        bar.spacing = 4f;
        bar.childForceExpandWidth = false;
        bar.childControlWidth = bar.childControlHeight = true;

        // ── Body ──
        var body = NewUI("Body", footer.transform);
        var bodyRect = (RectTransform)body.transform;
        bodyRect.anchorMin = Vector2.zero;
        bodyRect.anchorMax = Vector2.one;
        bodyRect.offsetMin = Vector2.zero;
        bodyRect.offsetMax = new Vector2(0f, -TabBarHeight);

        var tabs = new List<FooterTab>
        {
            NewTab("Rooms",          "RoomsTab",      KeyCode.Tab,  true,  tabBar.transform, body.transform, resources),
            NewTab("Spells",         "SpellsTab",     KeyCode.None, true,  tabBar.transform, body.transform, resources),
            NewTab("Traps & Doors",  "TrapsDoorsTab", KeyCode.None, true,  tabBar.transform, body.transform, resources),
            NewTab("Minions",        "MinionsTab",    KeyCode.None, false, tabBar.transform, body.transform, resources),
        };

        var flex = NewUI("Spacer", tabBar.transform);
        flex.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var minimise = NewButton("MinimiseButton", "–", tabBar.transform, resources, 32f);

        // ── Room button template ──
        var template = NewButton("RoomButtonTemplate", "Room (0g)", footer.transform, resources, CellSize.x);
        ((RectTransform)template.transform).sizeDelta = CellSize;
        template.gameObject.SetActive(false);

        // ── Popup ──
        var popupGo = NewUI("HotkeyPopup", canvas.transform);
        var popupRect = (RectTransform)popupGo.transform;
        popupRect.anchorMin = popupRect.anchorMax = new Vector2(0.5f, 0f);
        popupRect.pivot = new Vector2(0.5f, 0f);
        popupRect.anchoredPosition = new Vector2(0f, FooterHeight + 12f);
        popupGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);
        popupGo.AddComponent<CanvasGroup>();
        var popupLayout = popupGo.AddComponent<VerticalLayoutGroup>();
        popupLayout.padding = new RectOffset(12, 12, 6, 6);
        popupLayout.childControlWidth = popupLayout.childControlHeight = true;
        var fitter = popupGo.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var popupText = NewText("Text", "", popupGo.transform, 18f, TextAlignmentOptions.Left);
        var popup = popupGo.AddComponent<HotkeyPopup>();

        // ── Wiring ──
        var so = new SerializedObject(hudFooter);
        var tabList = so.FindProperty("tabs");
        tabList.arraySize = tabs.Count;
        for (int i = 0; i < tabs.Count; i++) tabList.GetArrayElementAtIndex(i).objectReferenceValue = tabs[i];
        so.FindProperty("body").objectReferenceValue           = bodyRect;
        so.FindProperty("tabBar").objectReferenceValue         = tabBarRect;
        so.FindProperty("minimiseButton").objectReferenceValue = minimise;
        so.FindProperty("popup").objectReferenceValue          = popup;
        so.ApplyModifiedPropertiesWithoutUndo();

        var pso = new SerializedObject(popup);
        pso.FindProperty("text").objectReferenceValue = popupText;
        pso.ApplyModifiedPropertiesWithoutUndo();

        string hudNote = HookUpHud(tabs[0], template);

        Undo.RegisterCreatedObjectUndo(footer,  "Create HUD Footer");
        Undo.RegisterCreatedObjectUndo(popupGo, "Create HUD Footer");
        EditorSceneManager.MarkSceneDirty(canvas.gameObject.scene);
        Selection.activeGameObject = footer;

        EditorUtility.DisplayDialog("HUD Footer",
            $"Created the HUD footer on {canvas.name}.\n\n{hudNote}\n\nSave the scene to keep it.", "OK");
    }

    // ── Pieces ─────────────────────────────────────────────────────────

    private static FooterTab NewTab(string title, string objectName, KeyCode hotkey, bool hasGrid,
                                    Transform tabBar, Transform body, TMP_DefaultControls.Resources resources)
    {
        var tabButton = NewButton($"{objectName}Button", title, tabBar, resources, 130f);

        var content = NewUI(objectName, body);
        var rect = (RectTransform)content.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        GridLayoutGroup grid = null;
        if (hasGrid)
        {
            grid = content.AddComponent<GridLayoutGroup>();
            grid.padding         = new RectOffset(8, 8, 8, 8);
            grid.cellSize        = CellSize;
            grid.spacing         = new Vector2(6f, 6f);
            grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 6;
        }
        else
        {
            var placeholder = NewText("Placeholder", $"{title} — coming soon", content.transform, 18f, TextAlignmentOptions.Center);
            var pr = (RectTransform)placeholder.transform;
            pr.anchorMin = Vector2.zero; pr.anchorMax = Vector2.one;
            pr.offsetMin = pr.offsetMax = Vector2.zero;
        }

        var tab = content.AddComponent<FooterTab>();
        var so = new SerializedObject(tab);
        so.FindProperty("tabName").stringValue           = title;
        so.FindProperty("hotkey").intValue               = (int)hotkey;
        so.FindProperty("tabButton").objectReferenceValue = tabButton;
        so.FindProperty("grid").objectReferenceValue      = grid;
        so.ApplyModifiedPropertiesWithoutUndo();
        return tab;
    }

    private static Button NewButton(string name, string label, Transform parent,
                                    TMP_DefaultControls.Resources resources, float preferredWidth)
    {
        var go = TMP_DefaultControls.CreateButton(resources);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.AddComponent<LayoutElement>().preferredWidth = preferredWidth;

        var text = go.GetComponentInChildren<TextMeshProUGUI>(true);
        if (text != null)
        {
            text.text      = label;
            text.fontSize  = 16f;
            text.richText  = true;
            text.textWrappingMode = TextWrappingModes.NoWrap;
        }
        return go.GetComponent<Button>();
    }

    private static TextMeshProUGUI NewText(string name, string value, Transform parent, float size, TextAlignmentOptions align)
    {
        var go = NewUI(name, parent);
        var text = go.AddComponent<TextMeshProUGUI>();
        text.text      = value;
        text.fontSize  = size;
        text.alignment = align;
        text.richText  = true;
        text.color     = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private static GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        go.transform.SetParent(parent, false);
        return go;
    }

    private static Canvas FindCanvas()
    {
        if (Selection.activeGameObject != null)
        {
            var selected = Selection.activeGameObject.GetComponentInParent<Canvas>(true);
            if (selected != null) return selected.rootCanvas;
        }

        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include);
        foreach (var c in canvases) if (c.name == "HUDCanvas") return c;
        return canvases.Length > 0 ? canvases[0].rootCanvas : null;
    }

    /// <summary>Points the scene's HUDController2D at the Rooms tab and template, if it has none yet.</summary>
    private static string HookUpHud(FooterTab roomsTab, Button template)
    {
        var hud = Object.FindAnyObjectByType<HUDController2D>(FindObjectsInactive.Include);
        if (hud == null) return "No HUDController2D in the scene — set its Rooms Tab and Room Button Template by hand.";

        var so = new SerializedObject(hud);
        so.FindProperty("roomsTab").objectReferenceValue = roomsTab;

        var templateProp = so.FindProperty("roomButtonTemplate");
        string note = "HUDController2D now fills the Rooms tab.";
        if (templateProp.objectReferenceValue == null)
            templateProp.objectReferenceValue = template;
        else
            note += $" It keeps its existing Room Button Template ({templateProp.objectReferenceValue.name}); " +
                    "point it at the footer's RoomButtonTemplate if you'd rather use that one.";

        so.ApplyModifiedPropertiesWithoutUndo();
        return note;
    }
}
