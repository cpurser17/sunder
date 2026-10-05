using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One tab of the HUD footer (Rooms, Spells, Traps &amp; Doors, …): a grid of
/// buttons, each with a fixed row/column slot. The slot is both where the
/// button sits and its hotkey — HudFooter turns "tab key, row digit, column
/// digit" into clicking the button in that slot.
///
/// Whatever fills the tab (HUDController2D for rooms, later spells/traps)
/// calls Add for each button, then RebuildLayout. Slots come from data (the
/// RoomData workbook's ButtonRow/ButtonColumn), never from button order, so a
/// newly added entry never shifts anyone else's hotkey.
///
/// The Grid Layout Group on the content still does the pixel layout, so it
/// scales with screen size; RebuildLayout only orders the buttons row by row
/// and drops invisible spacers into empty slots so the drawn grid matches.
/// </summary>
public class FooterTab : MonoBehaviour
{
    public class Entry
    {
        /// <summary>1-9, or 0 for "no slot" (shown after the others, no hotkey).</summary>
        public int    Row, Column;
        public string Label;
        public Button Button;
    }

    [SerializeField] private string  tabName = "Rooms";
    [Tooltip("Starts the hotkey sequence for this tab. None = click only.")]
    [SerializeField] private KeyCode hotkey  = KeyCode.None;
    [Tooltip("The button in the footer's tab bar that shows this tab.")]
    [SerializeField] private Button  tabButton;
    [Tooltip("Grid the entries are laid out in. Empty for tabs without buttons (e.g. a tracker).")]
    [SerializeField] private GridLayoutGroup grid;

    private readonly List<Entry>      _entries = new();
    private readonly List<GameObject> _spacers = new();

    public string  TabName   => tabName;
    public KeyCode Hotkey    => hotkey;
    public Button  TabButton => tabButton;
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Where buttons for this tab should be parented.</summary>
    public RectTransform Content => grid != null ? (RectTransform)grid.transform : (RectTransform)transform;

    public void Add(Entry entry)
    {
        if (entry?.Button == null) return;
        var clash = Get(entry.Row, entry.Column);
        if (clash != null)
        {
            Debug.LogWarning($"[FooterTab] {tabName} {entry.Row}·{entry.Column}: {entry.Label} shares its slot with " +
                             $"{clash.Label} — it gets no hotkey or grid slot until one is moved.", this);
            entry.Row = entry.Column = 0;
        }
        _entries.Add(entry);
    }

    public Entry Get(int row, int column)
    {
        if (row <= 0 || column <= 0) return null;
        foreach (var e in _entries)
            if (e.Row == row && e.Column == column) return e;
        return null;
    }

    /// <summary>Entries in a row, by column.</summary>
    public List<Entry> RowEntries(int row)
    {
        var list = new List<Entry>();
        foreach (var e in _entries)
            if (row > 0 && e.Row == row) list.Add(e);
        list.Sort((a, b) => a.Column.CompareTo(b.Column));
        return list;
    }

    /// <summary>Rows that have at least one entry, ascending.</summary>
    public List<int> Rows()
    {
        var rows = new SortedSet<int>();
        foreach (var e in _entries)
            if (e.Row > 0) rows.Add(e.Row);
        return new List<int>(rows);
    }

    /// <summary>
    /// Orders this tab's buttons row by row, column by column, with spacers in
    /// empty slots, and sizes the grid to the widest row. Entries without a
    /// slot follow. Buttons parented elsewhere keep their hotkey but aren't moved.
    /// </summary>
    public void RebuildLayout()
    {
        foreach (var s in _spacers) if (s != null) Destroy(s);
        _spacers.Clear();
        if (grid == null) return;

        int rows = 0, columns = 1;
        foreach (var e in _entries)
        {
            if (e.Row    > rows)    rows    = e.Row;
            if (e.Column > columns) columns = e.Column;
        }
        grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = columns;

        var content = Content;
        int index = 0;
        for (int r = 1; r <= rows; r++)
        for (int c = 1; c <= columns; c++)
        {
            var entry = Get(r, c);
            if (entry != null && entry.Button.transform.parent == content)
                entry.Button.transform.SetSiblingIndex(index++);
            else
            {
                var spacer = new GameObject($"Empty {r}·{c}", typeof(RectTransform));
                spacer.transform.SetParent(content, false);
                spacer.transform.SetSiblingIndex(index++);
                _spacers.Add(spacer);
            }
        }

        foreach (var e in _entries)
            if (e.Row <= 0 && e.Button.transform.parent == content)
                e.Button.transform.SetSiblingIndex(index++);
    }
}
