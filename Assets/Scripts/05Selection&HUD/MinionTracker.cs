using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The HUD footer's Minions tab: one entry per kind of minion the local
/// player has — its icon (MinionDefinition.token) and how many are idle,
/// working and fighting, kept up to date.
///
/// Click the icon to pick one of that kind up with the Keeper's hand (an
/// idle one first); click a number to pick up one doing that. Each click
/// takes the next, so clicking repeatedly gathers a handful.
///
///   working   worker jobs, or working (or heading to work) in a room
///   fighting  in combat (always 0 until combat exists)
///   idle      everything else — wandering, eating, sleeping, sulking…
///
/// Builds its own UI at runtime inside the tab, replacing the placeholder
/// text. Added to the footer's "Minions" tab by HUDController2D — no scene
/// setup. Restyle by editing the constants below, or replace it later with a
/// prefab-driven version.
/// </summary>
public class MinionTracker : MonoBehaviour
{
    private const float RefreshSeconds = 0.5f;
    private static readonly Vector2 CellSize = new(190f, 40f);
    private static readonly Color IdleColour     = new(0.80f, 0.80f, 0.80f, 1f);
    private static readonly Color WorkingColour  = new(0.45f, 0.90f, 0.45f, 1f);
    private static readonly Color FightingColour = new(0.95f, 0.40f, 0.35f, 1f);

    private enum Column { Idle, Working, Fighting }

    private class Row
    {
        public GameObject      Root;
        public Image           Icon;
        public TextMeshProUGUI Initials;
        public TextMeshProUGUI[] Counts = new TextMeshProUGUI[3];
    }

    private FactionID _player = FactionID.Player;
    private RectTransform _grid;
    private float _nextRefresh;
    private readonly Dictionary<string, Row> _rows = new();
    private readonly Dictionary<string, List<MinionController>> _groups = new();

    public void Initialise(FactionID localPlayer) => _player = localPlayer;

    private void OnEnable() => _nextRefresh = 0f;

    private void Update()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + RefreshSeconds;
        Refresh();
    }

    // ── Counting ───────────────────────────────────────────────────────

    private void Refresh()
    {
        EnsureGrid();

        foreach (var list in _groups.Values) list.Clear();
        foreach (var m in MinionController.All)
        {
            if (m == null || !m.IsAlive || m.Faction != _player) continue;
            string key = KeyOf(m);
            if (!_groups.TryGetValue(key, out var list)) _groups[key] = list = new List<MinionController>();
            list.Add(m);
        }

        foreach (var pair in _groups)
        {
            if (!_rows.TryGetValue(pair.Key, out var row))
            {
                if (pair.Value.Count == 0) continue;
                row = _rows[pair.Key] = BuildRow(pair.Key, pair.Value[0]);
            }

            int idle = 0, working = 0, fighting = 0;
            foreach (var m in pair.Value)
                switch (ColumnOf(m))
                {
                    case Column.Working:  working++;  break;
                    case Column.Fighting: fighting++; break;
                    default:              idle++;     break;
                }

            row.Root.SetActive(pair.Value.Count > 0);
            row.Counts[(int)Column.Idle].text     = idle.ToString();
            row.Counts[(int)Column.Working].text  = working.ToString();
            row.Counts[(int)Column.Fighting].text = fighting.ToString();
        }
    }

    private static string KeyOf(MinionController m) =>
        m.Definition != null ? MinionWorkbookKey(m.Definition) : "Worker";

    private static string MinionWorkbookKey(MinionDefinition d) => $"{d.factionId}_{d.minionId}";

    private static Column ColumnOf(MinionController m) => m.Activity switch
    {
        MinionActivity.Working  => Column.Working,
        MinionActivity.Fighting => Column.Fighting,
        _                       => Column.Idle,
    };

    // ── Picking up ─────────────────────────────────────────────────────

    /// <summary>Picks up one minion of this kind — from the given column, or (null) idle first.</summary>
    private void PickUp(string key, Column? column)
    {
        var hand = KeeperHand.Instance;
        if (hand == null || !_groups.TryGetValue(key, out var list)) return;

        Column[] order = column.HasValue
            ? new[] { column.Value }
            : new[] { Column.Idle, Column.Working, Column.Fighting };

        foreach (var wanted in order)
            foreach (var m in list)
                if (m != null && m.IsAlive && !m.IsHeld && ColumnOf(m) == wanted && hand.TryPickUp(m))
                    return;
    }

    // ── UI ─────────────────────────────────────────────────────────────

    private void EnsureGrid()
    {
        if (_grid != null) return;

        var placeholder = transform.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        var go = new GameObject("TrackerGrid", typeof(RectTransform));
        go.layer = gameObject.layer;
        _grid = (RectTransform)go.transform;
        _grid.SetParent(transform, false);
        _grid.anchorMin = Vector2.zero;
        _grid.anchorMax = Vector2.one;
        _grid.offsetMin = _grid.offsetMax = Vector2.zero;

        var layout = go.AddComponent<GridLayoutGroup>();
        layout.padding    = new RectOffset(8, 8, 8, 8);
        layout.cellSize   = CellSize;
        layout.spacing    = new Vector2(6f, 6f);
        layout.constraint = GridLayoutGroup.Constraint.Flexible;
    }

    private Row BuildRow(string key, MinionController sample)
    {
        var row = new Row { Root = NewUI($"Minion_{key}", _grid) };
        row.Root.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);
        var h = row.Root.AddComponent<HorizontalLayoutGroup>();
        h.padding = new RectOffset(4, 4, 2, 2);
        h.spacing = 6f;
        h.childAlignment = TextAnchor.MiddleLeft;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = h.childForceExpandHeight = false;

        // Icon — picks one up, idle first.
        var icon = NewUI("Icon", row.Root.transform);
        icon.AddComponent<LayoutElement>().preferredWidth = 36f;
        icon.GetComponent<LayoutElement>().preferredHeight = 36f;
        row.Icon = icon.AddComponent<Image>();
        var token = sample.Definition != null ? sample.Definition.token : null;
        row.Icon.sprite = token;
        row.Icon.preserveAspect = true;
        if (token == null)
        {
            row.Icon.color = new Color(0.3f, 0.3f, 0.35f, 1f);
            row.Initials = NewText("Initials", Initials(sample), icon.transform, 14f, Color.white);
            Stretch(row.Initials.rectTransform);
        }
        icon.AddComponent<Button>().onClick.AddListener(() => PickUp(key, null));

        row.Counts[(int)Column.Idle]     = CountButton(row.Root.transform, key, Column.Idle,     IdleColour);
        row.Counts[(int)Column.Working]  = CountButton(row.Root.transform, key, Column.Working,  WorkingColour);
        row.Counts[(int)Column.Fighting] = CountButton(row.Root.transform, key, Column.Fighting, FightingColour);
        return row;
    }

    private TextMeshProUGUI CountButton(Transform parent, string key, Column column, Color colour)
    {
        var go = NewUI(column.ToString(), parent);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = 40f; le.preferredHeight = 36f;
        go.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.05f);
        go.AddComponent<Button>().onClick.AddListener(() => PickUp(key, column));

        var text = NewText("Count", "0", go.transform, 18f, colour);
        Stretch(text.rectTransform);
        return text;
    }

    private static string Initials(MinionController m)
    {
        string name = m.Definition != null ? m.Definition.displayName : "Worker";
        return string.IsNullOrEmpty(name) ? "?" : name.Substring(0, Mathf.Min(2, name.Length));
    }

    private GameObject NewUI(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = gameObject.layer;
        go.transform.SetParent(parent, false);
        return go;
    }

    private TextMeshProUGUI NewText(string name, string value, Transform parent, float size, Color colour)
    {
        var text = NewUI(name, parent).AddComponent<TextMeshProUGUI>();
        text.text          = value;
        text.fontSize      = size;
        text.color         = colour;
        text.alignment     = TextAlignmentOptions.Center;
        text.raycastTarget = false;
        return text;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
    }
}
