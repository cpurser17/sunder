using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The research screen: the local player's research web on a polar layout,
/// plus a small widget (top right) showing what's being researched — click
/// it, or press GameControls.General.Research (R), to open the screen. Built in code with
/// UiKit, so it works before it has art.
///
///   • Nodes sit at Angle/Radius (rings × ringSpacing). The web is zoomed to
///     fit the shorter side of the view when it opens, so it suits any
///     screen size or shape; the queue panel takes the right-hand side.
///   • Drag to pan, mouse wheel to zoom, Fit to reset.
///   • Click a node to queue it (and the route to it); Shift- or Ctrl-click
///     to research it next; right-click to take it off the queue.
///   • Hovering a node shows its info box: name, cost and progress, what it
///     does (written out from its effects), your description, and anything
///     it still needs.
/// The game keeps running while it's open.
/// </summary>
public class ResearchScreen : MonoBehaviour
{
    [SerializeField] private FactionID localPlayer = FactionID.Player;
    [Tooltip("Content units between rings — the web's scale before zooming.")]
    [SerializeField] private float ringSpacing = 150f;
    [SerializeField] private float nodeSize    = 84f;
    [Tooltip("Width of the queue panel on the right, in 1920x1080 units.")]
    [SerializeField] private float queueWidth  = 380f;

    public static ResearchScreen Instance { get; private set; }
    public bool IsOpen => _window != null && _window.gameObject.activeSelf;

    private RectTransform _root, _window, _viewport, _content, _queueList, _tooltip;
    private Canvas        _canvas;
    private ResearchLinksGraphic _links;
    private TextMeshProUGUI _widgetLabel, _currentLabel, _tooltipText, _emptyNote;
    private Image _widgetBar, _currentBar;
    private readonly List<ResearchNodeView> _views = new();
    private readonly List<(ResearchWeb.WebNode a, ResearchWeb.WebNode b)> _pairs = new();

    private FactionResearchState _research;
    private ResearchWeb _builtFor;
    private ResearchNodeView _hovered;
    private float _zoom = 1f, _fitZoom = 1f;
    private bool  _stateDirty = true, _queueDirty = true;

    // ── Positions ──────────────────────────────────────────────────────

    /// <summary>Polar (degrees from the top, clockwise; rings) to content units.</summary>
    public static Vector2 Place(float angleDeg, float radius, float spacing)
    {
        float a = angleDeg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * (radius * spacing);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Watch(null);
    }

    private void Start() => BuildShell();

    private void Update()
    {
        var research = GameManager2D.Instance != null ? GameManager2D.Instance.GetResearch(localPlayer) : null;
        if (research != _research) Watch(research);

        if (!IsTyping() && GameControls.General.Research.WasPressedThisFrame()) Toggle();
        if (IsOpen && !GameControls.ModalOpen && GameControls.TakeBack()) Close();

        UpdateWidget();
        if (!IsOpen) return;

        var web = _research?.Web;
        if (web != _builtFor) BuildWeb(web);
        RefreshNodes();
        if (_queueDirty) RebuildQueue();
        UpdateCurrent();
        UpdateTooltip();
    }

    private void Watch(FactionResearchState research)
    {
        if (_research != null) _research.OnChanged -= MarkDirty;
        _research = research;
        if (_research != null) _research.OnChanged += MarkDirty;
        MarkDirty();
    }

    private void MarkDirty() { _stateDirty = true; _queueDirty = true; }

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Open()
    {
        if (_window == null) return;
        _window.gameObject.SetActive(true);
        // The wheel zooms the web, not the camera (as for the full-screen minimap).
        ThirdPersonOrbitCamera.ZoomSuspended  = true;
        ThirdPersonOrbitCamera.OrbitSuspended = true;
        _builtFor = null;   // rebuild: the web may have been imported or the faction resolved since
        MarkDirty();
    }

    public void Close()
    {
        if (_window == null) return;
        _window.gameObject.SetActive(false);
        ThirdPersonOrbitCamera.ZoomSuspended  = MinimapFullscreen.IsOpen;
        ThirdPersonOrbitCamera.OrbitSuspended = MinimapFullscreen.IsOpen;
        _hovered = null;
        if (_tooltip != null) _tooltip.gameObject.SetActive(false);
    }

    private static bool IsTyping()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && (selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null);
    }

    // ── Building ───────────────────────────────────────────────────────

    private void BuildShell()
    {
        _root   = UiKit.Canvas("ResearchCanvas", 40, transform);
        _canvas = _root.GetComponent<Canvas>();

        // Widget: what's being researched, top right. Click to open.
        var widget = UiKit.Button("ResearchWidget", _root, "", Toggle, 18f);
        var wr = (RectTransform)widget.transform;
        wr.anchorMin = wr.anchorMax = wr.pivot = new Vector2(1f, 1f);
        wr.sizeDelta = new Vector2(340f, 54f);
        wr.anchoredPosition = new Vector2(-16f, -16f);
        _widgetLabel = UiKit.LabelOf(widget);
        _widgetLabel.alignment = TextAlignmentOptions.Left;
        _widgetLabel.rectTransform.offsetMin = new Vector2(12f, 10f);
        _widgetBar = Bar("Progress", wr, 6f);

        // The window.
        _window = UiKit.Fill(UiKit.Box("ResearchWindow", _root, new Color(0.04f, 0.035f, 0.05f, 0.96f)).rectTransform);

        var title = UiKit.Label("Title", _window, "Research", 34f, TextAlignmentOptions.Left);
        var tr = title.rectTransform;
        tr.anchorMin = new Vector2(0f, 1f); tr.anchorMax = new Vector2(1f, 1f); tr.pivot = new Vector2(0.5f, 1f);
        tr.offsetMin = new Vector2(24f, -60f); tr.offsetMax = new Vector2(-24f, -10f);

        var close = UiKit.Button("Close", _window, $"Close  ({GameControls.KeysOf(GameControls.General.Research)})", Close, 20f);
        var cr = (RectTransform)close.transform;
        cr.anchorMin = cr.anchorMax = cr.pivot = new Vector2(1f, 1f);
        cr.sizeDelta = new Vector2(150f, 44f);
        cr.anchoredPosition = new Vector2(-16f, -12f);

        var fit = UiKit.Button("Fit", _window, "Fit", () => FitView(), 20f);
        var fr = (RectTransform)fit.transform;
        fr.anchorMin = fr.anchorMax = fr.pivot = new Vector2(1f, 1f);
        fr.sizeDelta = new Vector2(90f, 44f);
        fr.anchoredPosition = new Vector2(-176f, -12f);

        // Web viewport: everything left of the queue panel, under the title.
        var vp = UiKit.Box("Viewport", _window, new Color(0f, 0f, 0f, 0.01f));   // invisible, but catches drags
        _viewport = vp.rectTransform;
        _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
        _viewport.offsetMin = new Vector2(0f, 40f);
        _viewport.offsetMax = new Vector2(-queueWidth, -70f);
        vp.gameObject.AddComponent<RectMask2D>();
        vp.gameObject.AddComponent<ResearchViewport>().Screen = this;

        _content = UiKit.Rect("Content", _viewport);
        _content.anchorMin = _content.anchorMax = _content.pivot = new Vector2(0.5f, 0.5f);
        _content.sizeDelta = Vector2.zero;

        _emptyNote = UiKit.Label("Empty", _viewport, "", 24f, TextAlignmentOptions.Center, UiKit.TextDim);
        UiKit.Fill(_emptyNote.rectTransform, 40f, 40f, 40f, 40f);

        var hint = UiKit.Label("Hint", _window,
            "Click: add to queue   ·   Shift-click: research next   ·   Right-click: take off the queue   ·   Drag: pan   ·   Wheel: zoom",
            17f, TextAlignmentOptions.Center, UiKit.TextDim);
        var hr = hint.rectTransform;
        hr.anchorMin = Vector2.zero; hr.anchorMax = new Vector2(1f, 0f); hr.pivot = new Vector2(0.5f, 0f);
        hr.offsetMin = new Vector2(0f, 8f); hr.offsetMax = new Vector2(-queueWidth, 36f);

        BuildQueuePanel();

        // Info box, over everything; never catches the pointer.
        var tip = UiKit.Box("InfoBox", _window, new Color(0.10f, 0.09f, 0.13f, 0.98f));
        tip.raycastTarget = false;
        _tooltip = tip.rectTransform;
        _tooltip.anchorMin = _tooltip.anchorMax = new Vector2(0.5f, 0.5f);
        _tooltipText = UiKit.Label("Text", _tooltip, "", 18f, TextAlignmentOptions.TopLeft);
        UiKit.Fill(_tooltipText.rectTransform, 14f, 12f, 14f, 12f);
        _tooltip.gameObject.SetActive(false);

        _window.gameObject.SetActive(false);
    }

    private void BuildQueuePanel()
    {
        var panel = UiKit.Box("QueuePanel", _window, UiKit.Panel).rectTransform;
        panel.anchorMin = new Vector2(1f, 0f); panel.anchorMax = new Vector2(1f, 1f); panel.pivot = new Vector2(1f, 0.5f);
        panel.offsetMin = new Vector2(-queueWidth, 0f); panel.offsetMax = new Vector2(0f, -70f);
        UiKit.Column(panel, 8f, 14);

        UiKit.Height(UiKit.Label("Heading", panel, "Researching", 24f), 32f);
        _currentLabel = UiKit.Height(UiKit.Label("Current", panel, "", 19f), 52f);
        var barBack = UiKit.Height(UiKit.Box("CurrentBar", panel, UiKit.PanelLight), 10f);
        _currentBar = Bar("Fill", barBack.rectTransform, 10f);

        UiKit.Height(UiKit.Label("QueueHeading", panel, "Queue", 24f), 40f);
        var list = UiKit.ScrollList("Queue", panel);
        var le = list.parent.gameObject.AddComponent<LayoutElement>();
        le.flexibleHeight = 1f;
        _queueList = list;

        UiKit.Height(UiKit.Button("ClearQueue", panel, "Clear queue", () => _research?.ClearQueue(), 20f), 44f);
    }

    /// <summary>A thin fill bar along the bottom of a rect.</summary>
    private static Image Bar(string name, RectTransform parent, float height)
    {
        var bar = UiKit.Box(name, parent, ResearchNodeView.RimResearching);
        bar.sprite = ResearchSprites.Square;   // a filled Image needs a sprite to fill
        bar.type = Image.Type.Filled;
        bar.fillMethod = Image.FillMethod.Horizontal;
        bar.fillAmount = 0f;
        bar.raycastTarget = false;
        var r = bar.rectTransform;
        r.anchorMin = Vector2.zero; r.anchorMax = new Vector2(1f, 0f); r.pivot = new Vector2(0.5f, 0f);
        r.offsetMin = Vector2.zero; r.offsetMax = new Vector2(0f, height);
        return bar;
    }

    private void BuildWeb(ResearchWeb web)
    {
        _builtFor = web;
        UiKit.Clear(_content);
        _views.Clear();
        _pairs.Clear();
        _hovered = null;

        if (web == null || web.Nodes.Count == 0)
        {
            _emptyNote.text = ResearchCatalogue.Load() == null
                ? "No research yet — run Sunder > Import Research in Unity."
                : "This faction has no research.";
            _links = null;
            return;
        }
        _emptyNote.text = "";

        var linksRect = UiKit.Rect("Links", _content);
        linksRect.sizeDelta = Vector2.zero;
        _links = linksRect.gameObject.AddComponent<ResearchLinksGraphic>();
        _links.raycastTarget = false;
        _links.RingSpacing = ringSpacing;

        float maxRadius = 0f;
        foreach (var n in web.Nodes) maxRadius = Mathf.Max(maxRadius, n.Radius);
        _links.Rings = Mathf.CeilToInt(maxRadius - 0.01f);
        // As big as the web, so the mask doesn't cull it when the centre is panned out of view.
        float extent = (maxRadius + 1f) * ringSpacing * 2f;
        linksRect.sizeDelta = new Vector2(extent, extent);

        foreach (var n in web.Nodes)
            foreach (var l in n.Links)
                if (web.Nodes.IndexOf(l) > web.Nodes.IndexOf(n)) _pairs.Add((n, l));

        foreach (var n in web.Nodes)
        {
            var view = ResearchNodeView.Create(this, n, _content, nodeSize, NameOf(n));
            ((RectTransform)view.transform).anchoredPosition = Place(n.Angle, n.Radius, ringSpacing);
            _views.Add(view);
        }

        FitView(maxRadius);
        _stateDirty = true;
    }

    // ── View ───────────────────────────────────────────────────────────

    private void FitView() => FitView(-1f);

    private void FitView(float maxRadius)
    {
        if (maxRadius < 0f)
        {
            maxRadius = 0f;
            if (_builtFor != null) foreach (var n in _builtFor.Nodes) maxRadius = Mathf.Max(maxRadius, n.Radius);
        }
        var size = _viewport.rect.size;
        float half = Mathf.Max(100f, Mathf.Min(size.x, size.y) * 0.5f);
        float extent = maxRadius * ringSpacing + nodeSize * 0.9f;   // room for the outer nodes and their names
        _fitZoom = half / Mathf.Max(1f, extent);
        _zoom = _fitZoom;
        _content.localScale = Vector3.one * _zoom;
        _content.anchoredPosition = Vector2.zero;
    }

    public void Pan(Vector2 screenDelta)
    {
        float scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
        _content.anchoredPosition += screenDelta / scale;
    }

    public void Zoom(float wheel, Vector2 screenPoint)
    {
        if (Mathf.Approximately(wheel, 0f)) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_viewport, screenPoint, null, out Vector2 local)) return;
        // Keep the point under the cursor where it is. The content sits at the viewport's centre.
        Vector2 pivot = local - _viewport.rect.center;
        Vector2 inContent = (pivot - _content.anchoredPosition) / _zoom;
        _zoom = Mathf.Clamp(_zoom * Mathf.Pow(1.12f, wheel), _fitZoom * 0.5f, Mathf.Max(_fitZoom * 4f, 2.5f));
        _content.localScale = Vector3.one * _zoom;
        _content.anchoredPosition = pivot - inContent * _zoom;
    }

    // ── Refreshing ─────────────────────────────────────────────────────

    private void RefreshNodes()
    {
        var r = _research;
        if (r == null || _builtFor == null) return;
        var queue = r.Queue;
        foreach (var view in _views)
        {
            var n = view.Node;
            int place = IndexIn(queue, n.Id);
            view.Show(StateOf(n, place), n.Cost > 0f ? r.ProgressOf(n) / n.Cost : 0f, place);
        }
        if (!_stateDirty || _links == null) return;
        _stateDirty = false;

        _links.Links.Clear();
        foreach (var (a, b) in _pairs)
        {
            bool da = r.IsDone(a), db = r.IsDone(b);
            var colour = da && db ? new Color(1f, 0.82f, 0.3f, 0.75f)
                       : da || db ? new Color(0.85f, 0.82f, 0.95f, 0.45f)
                                  : new Color(0.5f, 0.48f, 0.55f, 0.22f);
            _links.Links.Add(new ResearchLinksGraphic.Link
            {
                AngleA = a.Angle, RadiusA = a.Radius, AngleB = b.Angle, RadiusB = b.Radius,
                Colour = colour, Width = da && db ? 5f : 3f,
            });
        }
        _links.Redraw();
    }

    private ResearchNodeView.State StateOf(ResearchWeb.WebNode n, int queuePlace)
    {
        var r = _research;
        if (r.IsDone(n))       return ResearchNodeView.State.Done;
        if (r.Current == n)    return ResearchNodeView.State.Researching;
        if (queuePlace >= 0)   return ResearchNodeView.State.Queued;
        if (r.CanResearch(n))  return ResearchNodeView.State.Open;
        if (r.IsOpen(n))       return ResearchNodeView.State.Blocked;
        return ResearchNodeView.State.Locked;
    }

    private static int IndexIn(IReadOnlyList<string> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, System.StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private bool _docked;

    /// <summary>With a Menu button on the HUD header, the widget moves onto the header beside it.</summary>
    private void DockWidget()
    {
        if (_docked) return;
        var menu = GameMenu.Instance != null ? GameMenu.Instance.HeaderButton : null;
        if (menu == null) return;
        _docked = true;
        var widget = (RectTransform)_widgetLabel.transform.parent;
        widget.SetParent(menu.parent, false);
        widget.anchorMin = menu.anchorMin;
        widget.anchorMax = menu.anchorMax;
        widget.pivot     = new Vector2(0.5f, 0.5f);
        widget.sizeDelta = new Vector2(340f, menu.sizeDelta.y > 0f ? menu.sizeDelta.y : 54f);
        // Just inside the Menu button, towards the middle of the header.
        float menuCentre = menu.anchoredPosition.x + (0.5f - menu.pivot.x) * menu.sizeDelta.x;
        float side = menuCentre >= 0f ? -1f : 1f;
        widget.anchoredPosition = new Vector2(menuCentre + side * (menu.sizeDelta.x * 0.5f + 16f + widget.sizeDelta.x * 0.5f),
                                              menu.anchoredPosition.y + (0.5f - menu.pivot.y) * menu.sizeDelta.y);
    }

    private void UpdateWidget()
    {
        if (_widgetLabel == null) return;
        DockWidget();
        var r = _research;
        var web = r?.Web;
        bool any = web != null && web.Nodes.Count > 0;
        _widgetLabel.transform.parent.gameObject.SetActive(any || IsOpen);
        if (!any) return;

        var current = r.Current;
        if (current == null)
        {
            _widgetLabel.text = web.Nodes.TrueForAll(r.IsDone) ? "Research: all done" : "Research: nothing to research";
            _widgetBar.fillAmount = 0f;
            return;
        }
        float p = current.Cost > 0f ? r.ProgressOf(current) / current.Cost : 1f;
        _widgetLabel.text = $"Research: {NameOf(current)}  <alpha=#99>{Mathf.FloorToInt(p * 100f)}%";
        _widgetBar.fillAmount = p;
    }

    private void UpdateCurrent()
    {
        var r = _research;
        var current = r?.Current;
        if (current == null)
        {
            _currentLabel.text = "<alpha=#99>Nothing — queue a node, or build a Library and put minions to work.";
            _currentBar.fillAmount = 0f;
            return;
        }
        float p = current.Cost > 0f ? r.ProgressOf(current) / current.Cost : 1f;
        _currentLabel.text = $"{NameOf(current)}   {Mathf.FloorToInt(r.ProgressOf(current))} / {Mathf.FloorToInt(current.Cost)}" +
                             (r.CurrentIsAuto ? "\n<size=80%><alpha=#99>Chosen for you — queue a node to choose yourself." : "");
        _currentBar.fillAmount = p;
    }

    private void RebuildQueue()
    {
        _queueDirty = false;
        if (_queueList == null) return;
        UiKit.Clear(_queueList);
        var r = _research;
        if (r == null || r.Web == null) return;

        var queue = r.Queue;
        if (queue.Count == 0)
        {
            UiKit.Height(UiKit.Label("None", _queueList, "<alpha=#99>Empty — click nodes on the web to queue them.", 18f), 60f);
            return;
        }
        for (int i = 0; i < queue.Count; i++)
        {
            var node = r.Web.Find(queue[i]);
            if (node == null) continue;
            string id = node.Id;
            int index = i;

            var row = UiKit.Height(UiKit.Rect($"Row_{id}", _queueList), 40f);
            var layout = UiKit.Row(row, 4f);
            layout.childForceExpandWidth = false;

            bool ready = r.CanResearch(node);
            var label = UiKit.Label("Name", row, $"{i + 1}. {NameOf(node)}" + (ready ? "" : "  <alpha=#88><size=80%>(waiting)"), 18f);
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            UiKit.Width(UiKit.Button("Up", row, "Up", () => r.MoveInQueue(id, index - 1), 15f), 44f).interactable = i > 0;
            UiKit.Width(UiKit.Button("Down", row, "Dn", () => r.MoveInQueue(id, index + 1), 15f), 44f).interactable = i < queue.Count - 1;
            UiKit.Width(UiKit.Button("Remove", row, "X", () => r.Dequeue(id), 15f), 36f);
        }
    }

    // ── Pointer ────────────────────────────────────────────────────────

    public void Hover(ResearchNodeView view)   { _hovered = view; }
    public void Unhover(ResearchNodeView view) { if (_hovered == view) _hovered = null; }

    public void Click(ResearchNodeView view, PointerEventData.InputButton button)
    {
        var r = _research;
        if (r == null || view == null || r.IsDone(view.Node)) return;
        if (button == PointerEventData.InputButton.Right) { r.Dequeue(view.Node.Id); return; }
        if (button != PointerEventData.InputButton.Left) return;

        bool next = GameControls.Gameplay.ResearchNextModifier.IsPressed();
        if (next) r.EnqueueFirst(view.Node.Id);
        else      r.Enqueue(view.Node.Id);
    }

    private void UpdateTooltip()
    {
        if (_hovered == null || _research == null) { _tooltip.gameObject.SetActive(false); return; }

        _tooltipText.text = Describe(_hovered.Node);
        const float width = 380f;
        float height = _tooltipText.GetPreferredValues(_tooltipText.text, width - 28f, 10000f).y + 26f;
        _tooltip.sizeDelta = new Vector2(width, height);
        _tooltip.gameObject.SetActive(true);
        _tooltip.SetAsLastSibling();

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_window, Input.mousePosition, null, out Vector2 p)) return;
        var area = _window.rect;
        // Beside the pointer, flipping to the other side near an edge.
        float x = p.x + 24f, y = p.y - 16f;
        if (x + width > area.xMax - 8f) x = p.x - 24f - width;
        if (y - height < area.yMin + 8f) y = area.yMin + 8f + height;
        _tooltip.pivot = new Vector2(0f, 1f);
        _tooltip.anchoredPosition = new Vector2(Mathf.Max(area.xMin + 8f, x), Mathf.Min(area.yMax - 8f, y));
    }

    private string Describe(ResearchWeb.WebNode n)
    {
        var r  = _research;
        var sb = new StringBuilder();
        sb.Append("<b><size=120%>").Append(NameOf(n)).Append("</size></b>\n");

        if (r.IsDone(n)) sb.Append("<color=#FFD24D>Researched</color>\n");
        else
        {
            float have = r.ProgressOf(n);
            sb.Append($"Cost {Mathf.CeilToInt(n.Cost)}");
            if (have > 0f) sb.Append($"   <alpha=#AA>({Mathf.FloorToInt(have)} done)<alpha=#FF>");
            sb.Append('\n');
            int place = IndexIn(r.Queue, n.Id);
            if (r.Current == n) sb.Append("<color=#FF9E33>Researching now</color>\n");
            else if (place >= 0) sb.Append($"<color=#8CBFFF>Queued — #{place + 1}</color>\n");
        }

        if (n.Effects.Count > 0)
        {
            sb.Append('\n');
            foreach (var e in n.Effects) sb.Append("• ").Append(e.Describe(NameOf)).Append('\n');
        }

        string description = n.Data.DescriptionFor(CampaignSession.Language, Catalogue?.defaultLanguage ?? "en");
        if (!string.IsNullOrWhiteSpace(description)) sb.Append("\n<i><alpha=#CC>").Append(description).Append("</i><alpha=#FF>\n");

        if (!r.IsDone(n))
        {
            if (!r.IsOpen(n))
                sb.Append("\n<color=#E07A66>Not reachable yet — research a linked node first.</color>\n");
            else if (!r.CanResearch(n))
                sb.Append($"\n<color=#E07A66>Also needs: {n.Data.requires}</color>\n");
            sb.Append("\n<size=85%><alpha=#99>Click: queue   ·   Shift-click: next   ·   Right-click: unqueue");
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ── Names ──────────────────────────────────────────────────────────

    private static ResearchCatalogue Catalogue => ResearchCatalogue.Load();

    private static string NameOf(ResearchWeb.WebNode n) =>
        n.Data.NameFor(CampaignSession.Language, Catalogue?.defaultLanguage ?? "en");

    private string NameOf(UnlockKind kind, string id)
    {
        var gm = GameManager2D.Instance;
        switch (kind)
        {
            case UnlockKind.Room:
                if (System.Enum.TryParse(id, true, out TileType type) && gm != null && gm.Grid != null)
                    return gm.Grid.Tiles?.GetDefinition(type)?.tileName ?? id;
                return id;
            case UnlockKind.Spell:
                return SpellSystem.Instance?.Find(id)?.displayName ?? id;
            case UnlockKind.Minion:
                var def = gm != null ? gm.GetFactionDefinition(localPlayer)?.FindMinion(id) : null;
                return def != null && !string.IsNullOrEmpty(def.displayName) ? def.displayName : id;
            default:
                return id;
        }
    }
}

/// <summary>The research web's background: drag to pan, wheel to zoom.</summary>
public class ResearchViewport : MonoBehaviour, IDragHandler, IScrollHandler
{
    public ResearchScreen Screen;

    public void OnDrag(PointerEventData e)   => Screen?.Pan(e.delta);
    public void OnScroll(PointerEventData e) => Screen?.Zoom(e.scrollDelta.y, e.position);
}
