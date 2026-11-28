using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One node on the research screen: its icon (or a placeholder disc with
/// its initials), a rim showing its state, a ring that fills as it's
/// researched, its place in the queue, and its name. Hover shows the info
/// box; click queues it (ResearchScreen handles both).
/// </summary>
public class ResearchNodeView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    public enum State { Locked, Open, Blocked, Queued, Researching, Done }

    public static readonly Color RimLocked      = new(0.25f, 0.23f, 0.28f, 1f);
    public static readonly Color RimOpen        = new(0.85f, 0.82f, 0.95f, 1f);
    public static readonly Color RimBlocked     = new(0.75f, 0.35f, 0.30f, 1f);
    public static readonly Color RimQueued      = new(0.55f, 0.75f, 1.00f, 1f);
    public static readonly Color RimResearching = new(1.00f, 0.62f, 0.20f, 1f);
    public static readonly Color RimDone        = new(1.00f, 0.82f, 0.30f, 1f);

    public ResearchWeb.WebNode Node { get; private set; }

    private ResearchScreen  _screen;
    private Image           _rim, _back, _icon, _progress, _badge;
    private TextMeshProUGUI _initials, _badgeText, _name;
    private State           _shownState = (State)(-1);
    private int             _shownQueue = -2;

    public static ResearchNodeView Create(ResearchScreen screen, ResearchWeb.WebNode node, Transform parent,
                                          float size, string displayName)
    {
        var root = UiKit.Rect($"Node_{node.Id}", parent);
        root.sizeDelta = new Vector2(size, size);
        var view = root.gameObject.AddComponent<ResearchNodeView>();
        view._screen = screen;
        view.Node    = node;

        view._rim = UiKit.Box("Rim", root, RimLocked);
        view._rim.sprite = ResearchSprites.Disc;
        UiKit.Fill(view._rim.rectTransform);

        view._back = UiKit.Box("Back", root, new Color(0.10f, 0.09f, 0.12f, 1f));
        view._back.sprite = ResearchSprites.Disc;
        view._back.raycastTarget = false;
        UiKit.Fill(view._back.rectTransform, 5f, 5f, 5f, 5f);

        view._progress = UiKit.Box("Progress", root, RimResearching);
        view._progress.sprite        = ResearchSprites.Ring;
        view._progress.type          = Image.Type.Filled;
        view._progress.fillMethod    = Image.FillMethod.Radial360;
        view._progress.fillOrigin    = 2;   // top
        view._progress.fillAmount    = 0f;
        view._progress.raycastTarget = false;
        UiKit.Fill(view._progress.rectTransform);

        view._icon = UiKit.Box("Icon", root, Color.white);
        view._icon.raycastTarget  = false;
        view._icon.preserveAspect = true;
        UiKit.Fill(view._icon.rectTransform, 10f, 10f, 10f, 10f);
        if (node.Data.icon != null) view._icon.sprite = node.Data.icon;
        else
        {
            // Placeholder: a coloured disc with the node's initials.
            view._icon.sprite = ResearchSprites.Disc;
            view._icon.color  = PlaceholderColour(node.Id);
            view._initials = UiKit.Label("Initials", view._icon.transform, Initials(displayName), size * 0.3f,
                                         TextAlignmentOptions.Center, Color.white);
            UiKit.Fill(view._initials.rectTransform);
        }

        view._badge = UiKit.Box("Badge", root, RimQueued);
        view._badge.sprite = ResearchSprites.Disc;
        view._badge.raycastTarget = false;
        var br = view._badge.rectTransform;
        br.anchorMin = br.anchorMax = new Vector2(1f, 1f);
        br.sizeDelta = new Vector2(size * 0.36f, size * 0.36f);
        br.anchoredPosition = new Vector2(-size * 0.08f, -size * 0.08f);
        view._badgeText = UiKit.Label("Number", br, "", size * 0.22f, TextAlignmentOptions.Center, Color.black);
        UiKit.Fill(view._badgeText.rectTransform);

        view._name = UiKit.Label("Name", root, displayName, 17f, TextAlignmentOptions.Top, UiKit.Text);
        var nr = view._name.rectTransform;
        nr.anchorMin = new Vector2(0.5f, 0f);
        nr.anchorMax = new Vector2(0.5f, 0f);
        nr.pivot     = new Vector2(0.5f, 1f);
        nr.sizeDelta = new Vector2(size * 2.2f, 44f);
        nr.anchoredPosition = new Vector2(0f, -4f);
        return view;
    }

    /// <summary>Shows the node's state, progress (0–1) and place in the queue (-1 = not queued).</summary>
    public void Show(State state, float progress, int queuePlace)
    {
        _progress.fillAmount = state == State.Done ? 0f : Mathf.Clamp01(progress);
        if (state == _shownState && queuePlace == _shownQueue) return;
        _shownState = state;
        _shownQueue = queuePlace;

        _rim.color = state switch
        {
            State.Done        => RimDone,
            State.Researching => RimResearching,
            State.Queued      => RimQueued,
            State.Blocked     => RimBlocked,
            State.Open        => RimOpen,
            _                 => RimLocked,
        };
        _progress.color = state == State.Researching ? RimResearching : new Color(RimResearching.r, RimResearching.g, RimResearching.b, 0.5f);

        float dim = state == State.Locked ? 0.35f : state == State.Blocked ? 0.6f : 1f;
        var baseColour = Node.Data.icon != null ? Color.white : PlaceholderColour(Node.Id);
        _icon.color = new Color(baseColour.r * dim, baseColour.g * dim, baseColour.b * dim, 1f);
        _name.color = state == State.Locked ? UiKit.TextDim : UiKit.Text;

        bool queued = queuePlace >= 0;
        _badge.gameObject.SetActive(queued);
        if (queued) _badgeText.text = (queuePlace + 1).ToString();
    }

    public void OnPointerEnter(PointerEventData e) => _screen.Hover(this);
    public void OnPointerExit(PointerEventData e)  => _screen.Unhover(this);
    public void OnPointerClick(PointerEventData e) => _screen.Click(this, e.button);

    private static string Initials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split(new[] { ' ', '-', '_' }, System.StringSplitOptions.RemoveEmptyEntries);
        string s = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : parts[0].Substring(0, Mathf.Min(2, parts[0].Length));
        return s.ToUpperInvariant();
    }

    /// <summary>A steady colour per node id, so placeholders are easy to tell apart.</summary>
    private static Color PlaceholderColour(string id)
    {
        int h = 17;
        foreach (char c in id ?? "") h = h * 31 + c;
        return Color.HSVToRGB(Mathf.Abs(h % 360) / 360f, 0.45f, 0.75f);
    }
}

/// <summary>Disc and ring sprites made in code, so the research screen works before it has art.</summary>
public static class ResearchSprites
{
    private static Sprite _disc, _ring, _square;

    public static Sprite Square
    {
        get
        {
            if (_square != null) return _square;
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var white = new Color32[16];
            for (int i = 0; i < white.Length; i++) white[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(white);
            tex.Apply();
            return _square = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        }
    }

    public static Sprite Disc => _disc != null ? _disc : _disc = Make(0f);
    public static Sprite Ring => _ring != null ? _ring : _ring = Make(0.80f);

    /// <summary>A white circle (with a hole of the given fraction of its radius), softened at the edges.</summary>
    private static Sprite Make(float hole)
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float c = (size - 1) * 0.5f, outer = c, inner = c * hole;
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                float a = Mathf.Clamp01(outer - d) * (hole > 0f ? Mathf.Clamp01(d - inner) : 1f);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }
}
