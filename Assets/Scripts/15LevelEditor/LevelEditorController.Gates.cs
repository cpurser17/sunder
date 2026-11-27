using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The level editor's Gates tool: gates float over any tile that isn't solid
/// and belong to a team (or are Neutral — their squads can be recruited by
/// whoever finds them first); each has deployments — a squad, the trigger
/// that sends it, and optional repeats (see GatePlacement). Click a gate to
/// edit it in the right-hand panel, drag it to move it; click empty floor to
/// add one for the selected team.
/// </summary>
public partial class LevelEditorController
{
    private const float GatePanelWidth = 470f;

    private readonly List<GatePlacement>        _gates       = new();
    private readonly Dictionary<GatePlacement, SpriteRenderer> _gateMarkers = new();
    private GatePlacement _selectedGate;
    private Vector2       _gateScroll;

    // Dragging a gate to another tile.
    private GatePlacement _dragGate;
    private Snapshot      _dragBefore;

    // ── Rules ──────────────────────────────────────────────────────────

    /// <summary>Gates float over anything that isn't solid: floor, rooms, liquid. Not rock, walls, bedrock, gold or gems.</summary>
    private bool CanHoldGate(GridCell cell)
    {
        if (cell == null || IsBorder(cell) || cell.TileType == TileType.Wall) return false;
        return gridManager.Tiles.GetTraversal(cell.TileType) != TraversalType.Impassable;
    }

    private GatePlacement GateAt(int x, int y) => _gates.Find(g => g.x == x && g.y == y);

    /// <summary>Can the gate being dragged be dropped at x,y? (Its own tile counts — that's just a click.)</summary>
    private bool CanDropGate(int x, int y) =>
        _dragGate != null && ((x == _dragGate.x && y == _dragGate.y) ||
                              (GateAt(x, y) == null && CanHoldGate(gridManager.GetCell(x, y))));

    // ── Editing ────────────────────────────────────────────────────────

    /// <summary>Click in place mode: select the gate here, or add one for the selected team.</summary>
    private bool ClickGate(int x, int y)
    {
        var existing = GateAt(x, y);
        if (existing != null)
        {
            SelectGate(existing);
            return false;   // nothing changed
        }

        var cell = gridManager.GetCell(x, y);
        if (!CanHoldGate(cell))
        {
            SetStatus("A gate has to float over open floor, a room or liquid — not rock, walls, gold or gems.", error: true);
            return false;
        }

        var gate = new GatePlacement { id = UniqueGateId(), factionId = _team, x = x, y = y };
        gate.deployments.Add(NewDeployment(gate));
        AddGate(gate);
        SelectGate(gate);
        _dirty = true;
        SetStatus($"Added {gate.id} for {FactionTeams.DisplayName(gate.factionId)}.");
        return true;
    }

    private void SelectGate(GatePlacement gate)
    {
        if (_selectedGate != gate) { ClearGateBuffers(); _gateScroll = Vector2.zero; _popup = null; _renaming = null; }
        _selectedGate = gate;
        if (gate != null) SetStatus($"Selected {gate.id} — edit it on the right, or drag it to move it.");
    }

    private bool RemoveGateAt(int x, int y)
    {
        var gate = GateAt(x, y);
        if (gate == null) return false;
        RemoveGate(gate);
        _dirty = true;
        SetStatus($"Removed {gate.id}.");
        return true;
    }

    private string UniqueGateId()
    {
        for (int i = 1; ; i++)
        {
            string id = $"Gate {i}";
            if (!_gates.Exists(g => g.id == id)) return id;
        }
    }

    private GateDeployment NewDeployment(GatePlacement gate)
    {
        for (int i = 1; ; i++)
        {
            string id = $"{gate.id} · Squad {i}";
            if (!AllDeployments().Exists(d => d.id == id))
                return new GateDeployment { id = id, trigger = new DeploymentTrigger { kind = DeploymentTriggerKind.AfterTime, minutes = 5f } };
        }
    }

    private List<GateDeployment> AllDeployments()
    {
        var all = new List<GateDeployment>();
        foreach (var g in _gates) all.AddRange(g.deployments);
        return all;
    }

    // The squad-lost triggers that follow the deployment being renamed,
    // worked out when its renaming starts, so typing a name that briefly
    // matches another squad's doesn't steal that squad's triggers.
    private GateDeployment          _renaming;
    private readonly List<DeploymentTrigger> _renamingTriggers = new();

    /// <summary>Squad-lost triggers find their squad by name, so renaming one carries its triggers along.</summary>
    private void RenameDeployment(GateDeployment dep, string newId)
    {
        if (_renaming != dep)
        {
            _renaming = dep;
            _renamingTriggers.Clear();
            string old = dep.id;
            bool unique = !string.IsNullOrEmpty(old) && !AllDeployments().Exists(d => d != dep && d.id == old);
            if (unique)
                foreach (var d in AllDeployments())
                    if (d.trigger != null && d.trigger.deploymentId == old) _renamingTriggers.Add(d.trigger);
        }
        dep.id = newId;
        foreach (var t in _renamingTriggers) t.deploymentId = newId;
        _dirty = true;
    }

    // ── Dragging ───────────────────────────────────────────────────────

    private void BeginGateDrag(GatePlacement gate)
    {
        SelectGate(gate);
        _dragGate   = gate;
        _dragBefore = CaptureSnapshot();
    }

    /// <summary>Called every frame while dragging; the marker follows the pointer.</summary>
    private void ContinueGateDrag()
    {
        if (_hasHover) PositionGateMarker(_dragGate, _hoverX, _hoverY);
    }

    private void EndGateDrag()
    {
        var gate = _dragGate;
        var before = _dragBefore;
        _dragGate = null;
        _dragBefore = null;
        if (gate == null) return;

        bool moved = _hasHover && (_hoverX != gate.x || _hoverY != gate.y);
        if (moved)
        {
            if (GateAt(_hoverX, _hoverY) != null)
                SetStatus("There's a gate there already — move cancelled.", error: true);
            else if (!CanHoldGate(gridManager.GetCell(_hoverX, _hoverY)))
                SetStatus("A gate can't go there — move cancelled.", error: true);
            else
            {
                gate.x = _hoverX;
                gate.y = _hoverY;
                PushUndo(before);
                _dirty = true;
                SetStatus($"Moved {gate.id} to {gate.x},{gate.y}.");
            }
        }
        PositionGateMarker(gate, gate.x, gate.y);
    }

    private void CancelGateDrag()
    {
        if (_dragGate == null) return;
        PositionGateMarker(_dragGate, _dragGate.x, _dragGate.y);
        _dragGate = null;
        _dragBefore = null;
        SetStatus("Move cancelled.");
    }

    // ── Markers ────────────────────────────────────────────────────────

    private void AddGate(GatePlacement gate)
    {
        _gates.Add(gate);
        var marker = new GameObject($"Gate_{gate.id}").AddComponent<SpriteRenderer>();
        marker.transform.SetParent(_markerRoot, false);
        marker.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        marker.transform.localScale    = Vector3.one * 0.95f * gridManager.CellSize;
        marker.sprite       = _squareSprite;
        marker.sortingOrder = 8;
        _gateMarkers[gate]  = marker;
        PositionGateMarker(gate, gate.x, gate.y);
        RefreshGateMarker(gate);
    }

    private void PositionGateMarker(GatePlacement gate, int x, int y)
    {
        if (gate != null && _gateMarkers.TryGetValue(gate, out var marker) && marker != null)
            marker.transform.position = gridManager.CellToWorld(x, y) + Vector3.up * 0.06f;
    }

    private void RemoveGate(GatePlacement gate)
    {
        if (_gateMarkers.TryGetValue(gate, out var marker) && marker != null) Destroy(marker.gameObject);
        _gateMarkers.Remove(gate);
        _gates.Remove(gate);
        if (_selectedGate == gate) SelectGate(null);
    }

    private void ClearGates()
    {
        foreach (var m in _gateMarkers.Values) if (m != null) Destroy(m.gameObject);
        _gateMarkers.Clear();
        _gates.Clear();
        SelectGate(null);
        ResyncGateBaseline();
    }

    private void RefreshGateMarker(GatePlacement gate)
    {
        if (!_gateMarkers.TryGetValue(gate, out var marker) || marker == null) return;
        var c = TeamColour(gate.factionId);
        marker.color = new Color(c.r, c.g, c.b, gate == _selectedGate ? 0.85f : 0.5f);
    }

    private List<GatePlacement> CloneGates()
    {
        var list = new List<GatePlacement>(_gates.Count);
        foreach (var g in _gates) list.Add(g.Clone());
        return list;
    }

    /// <summary>Replaces every gate (load, undo); the selected gate stays selected if it's still there.</summary>
    private void LoadGates(List<GatePlacement> gates)
    {
        string selectedId = _selectedGate?.id;
        _renaming = null;
        _dragGate = null;
        _dragBefore = null;
        ClearGates();
        if (gates == null) return;
        foreach (var g in gates) if (g != null) AddGate(g.Clone());
        if (selectedId != null) _selectedGate = _gates.Find(g => g.id == selectedId);
        ResyncGateBaseline();
    }

    // ── Undo for panel edits ───────────────────────────────────────────
    // Panel edits change the gates directly, so after each GUI event the
    // gates are compared with how they were; a difference becomes an undo
    // step. Typing in one text field is one step until something else is done.

    private string              _gatesBaseline;
    private List<GatePlacement> _gatesBaselineCopy;
    private int                 _gateTypingControl;

    private string GatesJson() => Newtonsoft.Json.JsonConvert.SerializeObject(_gates);

    /// <summary>The gates as they are now are the baseline (after any undo step, load or undo).</summary>
    private void ResyncGateBaseline()
    {
        _gatesBaseline     = GatesJson();
        _gatesBaselineCopy = CloneGates();
        _gateTypingControl = 0;
    }

    private void RecordGatePanelEdit()
    {
        var type = Event.current.type;
        if (type == EventType.Layout || type == EventType.Repaint || _gatesBaseline == null) return;
        string now = GatesJson();
        if (now == _gatesBaseline) return;

        // Only keystrokes merge: a button click while a field has focus is its own step.
        int typing = type == EventType.KeyDown ? GUIUtility.keyboardControl : 0;
        bool sameTyping = typing != 0 && typing == _gateTypingControl;
        if (!sameTyping)
        {
            var before = CaptureSnapshot();
            before.Gates = _gatesBaselineCopy;
            PushUndo(before);
        }
        _gatesBaseline     = now;
        _gatesBaselineCopy = CloneGates();
        _gateTypingControl = typing;
    }

    private void ClearGateBuffers()
    {
        var keys = new List<string>();
        foreach (var k in _intBuffers.Keys) if (k.StartsWith("gate:")) keys.Add(k);
        foreach (var k in keys) _intBuffers.Remove(k);
    }

    // ── Left panel (help text and the minion picker) ───────────────────

    private void DrawGatePanel()
    {
        GUILayout.Label(_removeMode
            ? "Click a gate to remove it."
            : "Click open floor (or a room, or liquid) to add a gate for the selected team. Click a gate to " +
              "edit it on the right; drag it to move it. A gate disappears once its last deployment is done.", _small);
        foreach (var g in _gates) RefreshGateMarker(g);
    }

    /// <summary>The minion picker for squads (the same one the Minions tool uses).</summary>
    private void DrawGateMinionPicker()
    {
        if (_selectedGate == null || _removeMode) return;
        GUILayout.Label("Squad minion (choose, then 'Add … to this squad' on the right)", _header);
        DrawMinionPalette();
    }

    // ── Right panel (the selected gate) ────────────────────────────────

    private bool GateEditorOpen => _tool == Tool.Gates && _selectedGate != null && !_removeMode;

    private void DrawGateEditor(Rect rect)
    {
        var gate = _selectedGate;
        int gi = _gates.IndexOf(gate);
        Panel(rect);
        GUILayout.BeginArea(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 10f));

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{gate.id}  ·  tile {gate.x},{gate.y}", _header);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", GUILayout.Width(60))) Defer(() => SelectGate(null));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Team", GUILayout.Width(50));
        TeamDropdown(gate.factionId, neutral: true, any: false, f => { gate.factionId = f; _dirty = true; RefreshGateMarker(gate); });
        GUILayout.EndHorizontal();
        if (gate.factionId == FactionID.Unaligned)
            GUILayout.Label("Neutral: its squads can be recruited by the first team that finds them.", _small);

        GUILayout.Space(4f);
        _gateScroll = GUILayout.BeginScrollView(_gateScroll, false, true);
        GateDeployment remove = null;
        for (int di = 0; di < gate.deployments.Count; di++)
        {
            var dep = gate.deployments[di];
            string key = $"gate:{gi}.{di}";

            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(44));
            string id = GUILayout.TextField(dep.id ?? "", 40);
            if (id != (dep.id ?? "")) RenameDeployment(dep, id);
            if (GUILayout.Button("Delete", GUILayout.Width(60))) remove = dep;
            GUILayout.EndHorizontal();

            DrawTrigger(dep, key);
            DrawRepeats(dep, key);
            DrawSquad(dep);
            GUILayout.EndVertical();
            GUILayout.Space(4f);
        }
        if (GUILayout.Button("Add deployment")) { gate.deployments.Add(NewDeployment(gate)); _renaming = null; _dirty = true; }
        GUILayout.EndScrollView();

        if (remove != null)
        {
            gate.deployments.Remove(remove);
            ClearGateBuffers();
            _dirty = true;
        }
        GUILayout.EndArea();
    }

    private void DrawTrigger(GateDeployment dep, string key)
    {
        var t = dep.trigger ??= new DeploymentTrigger();
        var kind = (DeploymentTriggerKind)GUILayout.Toolbar((int)t.kind, new[] { "Time", "Squad lost", "Tiles", "Heart" });
        if (kind != t.kind)
        {
            t.kind = kind;
            if (kind == DeploymentTriggerKind.TilesClaimed && !FactionTeams.IsPlayable(t.team) && t.team != FactionID.Hostile)
                t.team = FactionID.Player;
            _dirty = true;
        }

        GUILayout.BeginHorizontal();
        switch (t.kind)
        {
            case DeploymentTriggerKind.AfterTime:
                GUILayout.Label("After minutes", GUILayout.Width(100));
                t.minutes = Mathf.Max(0f, FloatField($"{key}.min", t.minutes, GUILayout.Width(60)));
                GUILayout.FlexibleSpace();
                break;
            case DeploymentTriggerKind.SquadDefeated:
                GUILayout.Label("When lost:", GUILayout.Width(70));
                SquadDropdown(dep, t);
                break;
            case DeploymentTriggerKind.TilesClaimed:
                TeamDropdown(t.team, neutral: false, any: false, f => { t.team = f; _dirty = true; }, GUILayout.Width(150));
                GUILayout.Label("claims", GUILayout.Width(46));
                t.tiles = Mathf.Max(0, IntField($"{key}.tiles", t.tiles, GUILayout.Width(60)));
                GUILayout.Label("tiles");
                break;
            case DeploymentTriggerKind.HeartDestroyed:
                GUILayout.Label("Heart of", GUILayout.Width(60));
                TeamDropdown(t.team, neutral: false, any: true, f => { t.team = f; _dirty = true; });
                GUILayout.Label("is destroyed", GUILayout.Width(80));
                break;
        }
        GUILayout.EndHorizontal();
        if (t.kind == DeploymentTriggerKind.SquadDefeated)
            GUILayout.Label("Lost = every member dead (or, for a Neutral squad, dead or recruited).", _small);
    }

    private void DrawRepeats(GateDeployment dep, string key)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Repeat every (min, 0 = once)", GUILayout.Width(190));
        dep.repeatMinutes = Mathf.Max(0f, FloatField($"{key}.rep", dep.repeatMinutes, GUILayout.Width(60)));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        if (dep.repeatMinutes <= 0f) return;

        GUILayout.BeginHorizontal();
        GUILayout.Label("Max repeats (blank = forever)", GUILayout.Width(190));
        string k = $"{key}.max";
        string text = _intBuffers.TryGetValue(k, out var b) ? b : dep.maxRepeats?.ToString() ?? "";
        int? shown = int.TryParse(text, out int s) ? Mathf.Max(0, s) : (int?)null;
        if (shown != dep.maxRepeats) text = dep.maxRepeats?.ToString() ?? "";   // changed elsewhere (undo)
        text = GUILayout.TextField(text, 6, GUILayout.Width(60));
        _intBuffers[k] = text;
        int? max = int.TryParse(text, out int m) ? Mathf.Max(0, m) : (int?)null;
        if (max != dep.maxRepeats) { dep.maxRepeats = max; _dirty = true; }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    private void DrawSquad(GateDeployment dep)
    {
        GUILayout.Label(dep.squad.Count == 0 ? "Squad: empty" : "Squad", _small);
        SquadMember remove = null;
        foreach (var member in dep.squad)
        {
            var def = factionRegistry != null ? factionRegistry.FindMinion(member.contentFactionId, member.minionId) : null;
            int maxLevel = def != null ? Mathf.Max(1, def.maxLevel) : 99;
            string name = def != null ? MinionName(def) : $"{member.contentFactionId} {member.minionId} (missing)";

            GUILayout.BeginHorizontal();
            GUILayout.Label(name + (member.isLord ? "  (lord)" : ""), _small, GUILayout.MinWidth(80));
            GUILayout.FlexibleSpace();

            GUILayout.Label("Lv", GUILayout.Width(18));
            GUI.enabled = member.level > 1;
            if (GUILayout.Button("-", GUILayout.Width(22))) { member.level--; _dirty = true; }
            GUI.enabled = true;
            GUILayout.Label(member.level.ToString(), GUILayout.Width(24));
            GUI.enabled = member.level < maxLevel;
            if (GUILayout.Button("+", GUILayout.Width(22))) { member.level++; _dirty = true; }
            GUI.enabled = true;

            GUILayout.Space(8f);
            GUI.enabled = member.count > 1;
            if (GUILayout.Button("-", GUILayout.Width(22))) { member.count--; _dirty = true; }
            GUI.enabled = true;
            GUILayout.Label($"x{member.count}", GUILayout.Width(30));
            if (GUILayout.Button("+", GUILayout.Width(22))) { member.count++; _dirty = true; }

            GUILayout.Space(8f);
            if (GUILayout.Button("Remove", GUILayout.Width(62))) remove = member;
            GUILayout.EndHorizontal();
        }
        if (remove != null) { dep.squad.Remove(remove); _dirty = true; }

        string what = _minionDef != null ? $"{MinionName(_minionDef)} L{_minionLevel}{(_placeAsLord ? " (lord)" : "")}" : "a minion (choose one on the left)";
        GUI.enabled = _minionDef != null;
        if (GUILayout.Button($"Add {what} to this squad"))
        {
            dep.squad.Add(new SquadMember
            {
                contentFactionId = _minionFaction.factionContentId,
                minionId         = _minionDef.minionId,
                level            = Mathf.Clamp(_minionLevel, 1, Mathf.Max(1, _minionDef.maxLevel)),
                count            = 1,
                isLord           = _placeAsLord,
            });
            _dirty = true;
        }
        GUI.enabled = true;
    }

    // ── Dropdowns ──────────────────────────────────────────────────────

    private static string TeamOrAny(FactionID id, bool any) =>
        any && id == FactionID.Unaligned ? "any team" : FactionTeams.DisplayName(id);

    /// <summary>A team list: optionally Neutral, optionally "any team" (stored as Unaligned).</summary>
    private void TeamDropdown(FactionID current, bool neutral, bool any, System.Action<FactionID> set,
                              params GUILayoutOption[] options)
    {
        var items = new List<PopupItem>();
        if (any) items.Add(new PopupItem("any team", () => set(FactionID.Unaligned)));
        foreach (var id in FactionTeams.All)
        {
            if (id == FactionID.Unaligned && !neutral) continue;
            var team = id;
            items.Add(new PopupItem(TeamLabel(team), () => set(team), TeamColour(team)));
        }
        string label = any || neutral || current != FactionID.Unaligned ? TeamOrAny(current, any) : "(choose)";
        Dropdown(label, items, searchable: false, options);
    }

    /// <summary>Every other deployment, from any gate and any team, with a filter box.</summary>
    private void SquadDropdown(GateDeployment self, DeploymentTrigger t)
    {
        var items = new List<PopupItem>();
        string current = null;
        foreach (var g in _gates)
            foreach (var d in g.deployments)
            {
                if (string.IsNullOrEmpty(d.id)) continue;
                string label = SquadLabel(g, d);
                if (d.id == t.deploymentId) current = label;
                if (d == self) continue;
                string id = d.id;
                items.Add(new PopupItem(label, () => { t.deploymentId = id; _renaming = null; _dirty = true; }, TeamColour(g.factionId)));
            }
        string shown = current ?? (string.IsNullOrEmpty(t.deploymentId) ? "(choose a squad)" : $"{t.deploymentId} (missing)");
        Dropdown(shown, items, searchable: true);
    }

    private static string SquadLabel(GatePlacement g, GateDeployment d)
    {
        string team = FactionTeams.DisplayName(g.factionId);
        return d.id.StartsWith(g.id) ? $"{d.id} ({team})" : $"{d.id} ({g.id}, {team})";
    }

    // ── Popup list ─────────────────────────────────────────────────────
    // An IMGUI dropdown, drawn last so it sits over the panels. While it's
    // open, mouse events over it are hidden from the panels underneath, and
    // a click anywhere else closes it.

    private readonly struct PopupItem
    {
        public readonly string Label;
        public readonly System.Action Pick;
        public readonly Color? Colour;
        public PopupItem(string label, System.Action pick, Color? colour = null) { Label = label; Pick = pick; Colour = colour; }
    }

    private class PopupState
    {
        public Vector2 Position;
        public float   Width;
        public List<PopupItem> Items;
        public bool    Searchable;
        public string  Filter = "";
        public bool    Focus  = true;
        public Vector2 Scroll;
        public Rect    Rect;
    }
    private PopupState _popup;
    private EventType  _popupHiddenEvent = EventType.Ignore;

    private void Dropdown(string label, List<PopupItem> items, bool searchable, params GUILayoutOption[] options)
    {
        if (!GUILayout.Button(label + "  ▼", _leftButton, options)) return;
        var last = GUILayoutUtility.GetLastRect();
        Vector2 pointer = GuiPointer();
        _popup = new PopupState
        {
            Position   = new Vector2(pointer.x - 20f, pointer.y + 12f),
            Width      = Mathf.Max(searchable ? 320f : 200f, last.width),
            Items      = items,
            Searchable = searchable,
        };
    }

    private Vector2 GuiPointer() =>
        new(Input.mousePosition.x / _uiScale, (Screen.height - Input.mousePosition.y) / _uiScale);

    /// <summary>Before the panels draw: keep the popup's clicks to itself, and close it on a click elsewhere.</summary>
    private void BeginPopupInput()
    {
        _popupHiddenEvent = EventType.Ignore;
        var e = Event.current;
        if (_popup == null || !(e.isMouse || e.type == EventType.ScrollWheel)) return;
        if (_popup.Rect.Contains(GuiPointer()))
        {
            _popupHiddenEvent = e.type;
            e.type = EventType.Used;
        }
        else if (e.type == EventType.MouseDown)
        {
            _popup = null;
            e.Use();
        }
    }

    private void DrawPopup(float w, float h)
    {
        if (_popupHiddenEvent != EventType.Ignore) { Event.current.type = _popupHiddenEvent; _popupHiddenEvent = EventType.Ignore; }
        var p = _popup;
        if (p == null) return;

        var shown = new List<PopupItem>();
        foreach (var item in p.Items)
            if (string.IsNullOrEmpty(p.Filter) || item.Label.IndexOf(p.Filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                shown.Add(item);

        const float row = 24f;
        float height = Mathf.Min(360f, (p.Searchable ? 30f : 0f) + Mathf.Max(1, shown.Count) * row + 14f);
        var rect = new Rect(Mathf.Clamp(p.Position.x, 0f, Mathf.Max(0f, w - p.Width)),
                            Mathf.Clamp(p.Position.y, 0f, Mathf.Max(0f, h - height)), p.Width, height);
        p.Rect = rect;
        _guiRects.Add(rect);
        DrawRect(rect, Color.black);
        DrawRect(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f), new Color(0.16f, 0.16f, 0.19f, 1f));

        GUILayout.BeginArea(new Rect(rect.x + 6f, rect.y + 6f, rect.width - 12f, rect.height - 12f));
        if (p.Searchable)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", GUILayout.Width(40));
            GUI.SetNextControlName("GatePopupFilter");
            p.Filter = GUILayout.TextField(p.Filter, 40);
            GUILayout.EndHorizontal();
            if (p.Focus && Event.current.type == EventType.Repaint) { GUI.FocusControl("GatePopupFilter"); p.Focus = false; }
        }
        p.Scroll = GUILayout.BeginScrollView(p.Scroll);
        if (shown.Count == 0) GUILayout.Label(p.Items.Count == 0 ? "Nothing to choose yet." : "No matches.", _small);
        foreach (var item in shown)
        {
            GUILayout.BeginHorizontal();
            if (item.Colour.HasValue) Swatch(item.Colour.Value, 14f);
            if (GUILayout.Button(item.Label, _leftButton))
            {
                var pick = item.Pick;
                Defer(() => { _popup = null; GUIUtility.keyboardControl = 0; pick(); });
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void ClosePopup()
    {
        _popup = null;
        GUIUtility.keyboardControl = 0;
    }

    private float FloatField(string key, float value, params GUILayoutOption[] options)
    {
        string text = _intBuffers.TryGetValue(key, out var buffered) ? buffered : value.ToString("0.##");
        if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float shown) &&
            !Mathf.Approximately(shown, value)) text = value.ToString("0.##");
        string edited = GUILayout.TextField(text, 8, options);
        _intBuffers[key] = edited;
        if (!float.TryParse(edited, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)) return value;
        if (!Mathf.Approximately(parsed, value)) _dirty = true;
        return parsed;
    }

    // ── Labels and checks ──────────────────────────────────────────────

    private void DrawGateLabels(Camera cam)
    {
        foreach (var g in _gates)
        {
            Vector3 screen = cam.WorldToScreenPoint(gridManager.CellToWorld(g.x, g.y));
            var pos = new Vector2(screen.x / _uiScale, (Screen.height - screen.y) / _uiScale - 8f);
            GUI.Label(new Rect(pos.x - 50f, pos.y, 100f, 16f), $"{g.id} ({g.deployments.Count})", _mapLabel);
        }
    }

    private void ValidateGates(List<string> warnings)
    {
        var ids = new HashSet<string>();
        foreach (var g in _gates)
        {
            if (!CanHoldGate(gridManager.GetCell(g.x, g.y)))
                warnings.Add($"{g.id} is over a solid tile now — move it onto open floor, a room or liquid.");
            if (g.deployments.Count == 0)
                warnings.Add($"{g.id} has no deployments, so it disappears as soon as the mission starts.");
            foreach (var d in g.deployments)
            {
                if (string.IsNullOrWhiteSpace(d.id)) warnings.Add($"{g.id} has a deployment with no name.");
                else if (!ids.Add(d.id)) warnings.Add($"Two deployments are both called '{d.id}' — rename one (squad-lost triggers find them by name).");
                if (d.squad.Count == 0) warnings.Add($"{d.id} has an empty squad.");
                foreach (var m in d.squad)
                    if (factionRegistry != null && factionRegistry.FindMinion(m.contentFactionId, m.minionId) == null)
                        warnings.Add($"{d.id}: no minion {m.contentFactionId} {m.minionId} in the Faction Registry.");
                if (d.trigger?.kind == DeploymentTriggerKind.SquadDefeated &&
                    !AllDeployments().Exists(x => x != d && x.id == d.trigger.deploymentId))
                    warnings.Add($"{d.id} is set to go when another squad is lost, but none is chosen (or it's been deleted).");
            }
        }
    }

    /// <summary>Is any lord of the land in the level — placed, or in a gate's squad?</summary>
    private bool AnyLord() =>
        _minions.Exists(p => p.isLord) || _gates.Exists(g => g.deployments.Exists(d => d.squad.Exists(m => m.isLord)));
}
