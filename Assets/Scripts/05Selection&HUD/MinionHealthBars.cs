using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A small health bar floating above every minion, and a larger one above
/// every Dungeon Heart still standing.
///
/// Drawn on its own screen-space overlay canvas (beneath the HUD) and
/// placed over each minion every frame, so bars stay the same crisp size at
/// any zoom and never clip into the 3D scene. Bars are pooled — one per
/// live minion, reused as minions come and go.
///
/// The fill shades from green (healthy) through yellow to red (near death).
/// A thin strip under each bar shows its faction: the local player's in one
/// colour, everyone else's in another. Each minion's level is shown just
/// above its bar.
///
/// Scene setup: none — GameManager2D adds one. Add it yourself to tune the
/// look, or to show bars only on hurt minions.
/// </summary>
public class MinionHealthBars : MonoBehaviour
{
    [Header("Placement")]
    [Tooltip("World units above the minion's position the bar sits.")]
    [SerializeField] private float heightAboveMinion = 1.1f;
    [Tooltip("Bar size in pixels at the reference resolution.")]
    [SerializeField] private Vector2 size = new(36f, 5f);

    [Header("Dungeon Hearts")]
    [Tooltip("World units above the heart's centre its bar sits.")]
    [SerializeField] private float heartHeight = 2.5f;
    [Tooltip("Heart bar size in pixels at the reference resolution.")]
    [SerializeField] private Vector2 heartSize = new(110f, 10f);

    [Header("Showing")]
    [Tooltip("Hide bars on minions at full health.")]
    [SerializeField] private bool onlyWhenHurt = false;
    [Tooltip("Hide bars on minions held by the Keeper's hand.")]
    [SerializeField] private bool hideWhenHeld = true;
    [Tooltip("Show each minion's level just above its bar.")]
    [SerializeField] private bool showLevel = true;
    [Tooltip("Level text size in pixels at the reference resolution.")]
    [SerializeField] private float levelFontSize = 12f;

    [Header("Colours")]
    [SerializeField] private Color background   = new(0f, 0f, 0f, 0.65f);
    [SerializeField] private Color healthy      = new(0.30f, 0.85f, 0.30f, 1f);
    [SerializeField] private Color hurt         = new(0.95f, 0.85f, 0.20f, 1f);
    [SerializeField] private Color dying        = new(0.90f, 0.20f, 0.15f, 1f);
    [SerializeField] private Color ownFaction   = new(0.35f, 0.60f, 1.00f, 1f);
    [SerializeField] private Color otherFaction = new(0.85f, 0.30f, 0.85f, 1f);
    [SerializeField] private Color levelColour  = Color.white;
    [SerializeField] private FactionID localPlayer = FactionID.Player;

    private class Bar
    {
        public RectTransform Root, Fill;
        public Image FillImage, Strip;
        public TextMeshProUGUI Level;   // minion bars only
        public int ShownLevel = -1;
    }

    private Camera        _camera;
    private RectTransform _canvas;
    private readonly List<Bar> _bars      = new();
    private readonly List<Bar> _heartBars = new();

    private void Awake()
    {
        var go = new GameObject("HealthBarCanvas", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10;   // beneath the HUD
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        _canvas = (RectTransform)go.transform;
    }

    private void LateUpdate()
    {
        if (_camera == null) _camera = Camera.main;
        if (_camera == null) return;

        int used = 0;
        foreach (var minion in MinionController.All)
        {
            if (minion == null || !minion.IsAlive) continue;
            if (hideWhenHeld && minion.IsHeld) continue;

            float fraction = minion.HealthFraction;
            if (onlyWhenHurt && fraction >= 0.999f) continue;

            Vector3 screen = _camera.WorldToScreenPoint(minion.transform.position + Vector3.up * heightAboveMinion);
            if (screen.z <= 0f) continue;   // behind the camera

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, screen, null, out Vector2 local))
                continue;

            var bar = used < _bars.Count ? _bars[used] : NewBar(size, _bars, withLevel: true);
            used++;

            bar.Root.gameObject.SetActive(true);
            bar.Root.anchoredPosition = local;
            bar.Fill.localScale = new Vector3(Mathf.Clamp01(fraction), 1f, 1f);
            bar.FillImage.color = fraction > 0.5f
                ? Color.Lerp(hurt, healthy, (fraction - 0.5f) * 2f)
                : Color.Lerp(dying, hurt, fraction * 2f);
            bar.Strip.color = minion.Faction == localPlayer ? ownFaction : otherFaction;

            bar.Level.enabled = showLevel;
            if (showLevel && bar.ShownLevel != minion.Level)
            {
                bar.ShownLevel = minion.Level;
                bar.Level.text = minion.Level.ToString();
            }
        }

        for (int i = used; i < _bars.Count; i++)
            if (_bars[i].Root.gameObject.activeSelf) _bars[i].Root.gameObject.SetActive(false);

        DrawHearts();
    }

    /// <summary>One bar per standing heart — always shown, hurt or not.</summary>
    private void DrawHearts()
    {
        int used = 0;
        var hearts = DungeonHeart.Instance;
        if (hearts != null)
            foreach (var heart in hearts.StandingHearts())
            {
                float fraction = heart.MaxHP > 0 ? (float)heart.CurrentHP / heart.MaxHP : 0f;
                var bar = used < _heartBars.Count ? _heartBars[used] : NewBar(heartSize, _heartBars);
                if (!Place(bar, heart.Centre + Vector3.up * heartHeight, fraction, heart.Faction)) continue;
                used++;
            }

        for (int i = used; i < _heartBars.Count; i++)
            if (_heartBars[i].Root.gameObject.activeSelf) _heartBars[i].Root.gameObject.SetActive(false);
    }

    /// <summary>Positions and fills a bar over a world point. False if the point is behind the camera.</summary>
    private bool Place(Bar bar, Vector3 world, float fraction, FactionID owner)
    {
        Vector3 screen = _camera.WorldToScreenPoint(world);
        if (screen.z <= 0f) { bar.Root.gameObject.SetActive(false); return false; }
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, screen, null, out Vector2 local))
        { bar.Root.gameObject.SetActive(false); return false; }

        bar.Root.gameObject.SetActive(true);
        bar.Root.anchoredPosition = local;
        bar.Fill.localScale = new Vector3(Mathf.Clamp01(fraction), 1f, 1f);
        bar.FillImage.color = fraction > 0.5f
            ? Color.Lerp(hurt, healthy, (fraction - 0.5f) * 2f)
            : Color.Lerp(dying, hurt, fraction * 2f);
        bar.Strip.color = owner == localPlayer ? ownFaction : otherFaction;
        return true;
    }

    private Bar NewBar(Vector2 barSize, List<Bar> pool, bool withLevel = false)
    {
        var root = NewRect("HealthBar", _canvas);
        root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot     = new Vector2(0.5f, 0f);
        root.sizeDelta = barSize;
        var bg = root.gameObject.AddComponent<Image>();
        bg.color = background;
        bg.raycastTarget = false;

        // Fill: anchored to the left edge, scaled horizontally by health.
        var fill = NewRect("Fill", root);
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = Vector2.one;
        fill.offsetMin = new Vector2(1f, 1f);
        fill.offsetMax = new Vector2(-1f, -1f);
        fill.pivot     = new Vector2(0f, 0.5f);
        var fillImage = fill.gameObject.AddComponent<Image>();
        fillImage.raycastTarget = false;

        // Faction strip: a thin line along the bottom.
        var strip = NewRect("Faction", root);
        strip.anchorMin = new Vector2(0f, 0f);
        strip.anchorMax = new Vector2(1f, 0f);
        strip.pivot     = new Vector2(0.5f, 1f);
        strip.sizeDelta = new Vector2(0f, 2f);
        var stripImage = strip.gameObject.AddComponent<Image>();
        stripImage.raycastTarget = false;

        var bar = new Bar { Root = root, Fill = fill, FillImage = fillImage, Strip = stripImage };

        // Level: centred just above the bar.
        if (withLevel)
        {
            var level = NewRect("Level", root);
            level.anchorMin = level.anchorMax = new Vector2(0.5f, 1f);
            level.pivot     = new Vector2(0.5f, 0f);
            level.sizeDelta = new Vector2(barSize.x, levelFontSize + 2f);
            level.anchoredPosition = new Vector2(0f, 1f);
            var text = level.gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize      = levelFontSize;
            text.fontStyle     = FontStyles.Bold;
            text.color         = levelColour;
            text.alignment     = TextAlignmentOptions.Bottom;
            text.raycastTarget = false;
            bar.Level = text;
        }
        pool.Add(bar);
        return bar;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }
}
