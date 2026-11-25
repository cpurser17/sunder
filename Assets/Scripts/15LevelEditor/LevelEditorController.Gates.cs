using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The level editor's Gates tool: gates float over any tile that isn't solid
/// and belong to a team; each has deployments — a squad, the trigger that
/// sends it, and optional repeats (see GatePlacement). Click a gate to edit
/// it in the side panel; click empty floor to add one for the selected team.
/// </summary>
public partial class LevelEditorController
{
    private readonly List<GatePlacement>        _gates       = new();
    private readonly Dictionary<GatePlacement, SpriteRenderer> _gateMarkers = new();
    private GatePlacement _selectedGate;
    private Vector2       _gateScroll;

    // ── Rules ──────────────────────────────────────────────────────────

    /// <summary>Gates float over anything that isn't solid: floor, rooms, liquid. Not rock, walls, bedrock, gold or gems.</summary>
    private bool CanHoldGate(GridCell cell)
    {
        if (cell == null || IsBorder(cell) || cell.TileType == TileType.Wall) return false;
        return gridManager.Tiles.GetTraversal(cell.TileType) != TraversalType.Impassable;
    }

    private GatePlacement GateAt(int x, int y) => _gates.Find(g => g.x == x && g.y == y);

    // ── Editing ────────────────────────────────────────────────────────

    /// <summary>Click in place mode: select the gate here, or add one for the selected team.</summary>
    private bool ClickGate(int x, int y)
    {
        var existing = GateAt(x, y);
        if (existing != null)
        {
            _selectedGate = existing;
            SetStatus($"Selected {existing.id}.");
            return false;   // nothing changed
        }

        var cell = gridManager.GetCell(x, y);
        if (!CanHoldGate(cell))
        {
            SetStatus("A gate has to float over open floor, a room or liquid — not rock, walls, gold or gems.", error: true);
            return false;
        }

        var gate = new GatePlacement
        {
            id        = UniqueGateId(),
            factionId = _team == FactionID.Unaligned ? FactionID.AI1 : _team,
            x = x, y = y,
        };
        gate.deployments.Add(NewDeployment(gate));
        AddGate(gate);
        _selectedGate = gate;
        _dirty = true;
        SetStatus($"Added {gate.id} for {FactionTeams.DisplayName(gate.factionId)}.");
        return true;
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

    // ── Markers ────────────────────────────────────────────────────────

    private void AddGate(GatePlacement gate)
    {
        _gates.Add(gate);
        var marker = new GameObject($"Gate_{gate.id}").AddComponent<SpriteRenderer>();
        marker.transform.SetParent(_markerRoot, false);
        marker.transform.position      = gridManager.CellToWorld(gate.x, gate.y) + Vector3.up * 0.06f;
        marker.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        marker.transform.localScale    = Vector3.one * 0.95f * gridManager.CellSize;
        marker.sprite       = _squareSprite;
        marker.sortingOrder = 8;
        _gateMarkers[gate]  = marker;
        RefreshGateMarker(gate);
    }

    private void RemoveGate(GatePlacement gate)
    {
        if (_gateMarkers.TryGetValue(gate, out var marker) && marker != null) Destroy(marker.gameObject);
        _gateMarkers.Remove(gate);
        _gates.Remove(gate);
        if (_selectedGate == gate) _selectedGate = null;
    }

    private void ClearGates()
    {
        foreach (var m in _gateMarkers.Values) if (m != null) Destroy(m.gameObject);
        _gateMarkers.Clear();
        _gates.Clear();
        _selectedGate = null;
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

    private void LoadGates(List<GatePlacement> gates)
    {
        ClearGates();
        if (gates == null) return;
        foreach (var g in gates) if (g != null) AddGate(g.Clone());
    }

    // ── Side panel ─────────────────────────────────────────────────────

    private void DrawGatePanel()
    {
        GUILayout.Label(_removeMode
            ? "Click a gate to remove it."
            : "Click open floor (or a room, or liquid) to add a gate for the selected team; click a gate to edit it. " +
              "A gate disappears once its last deployment is done.", _small);
        foreach (var g in _gates) RefreshGateMarker(g);
        var gate = _selectedGate;
        if (gate == null || _removeMode) return;

        GUILayout.Space(6f);
        GUILayout.Label($"{gate.id}  ·  tile {gate.x},{gate.y}", _header);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Team", GUILayout.Width(50));
        if (GUILayout.Button(FactionTeams.DisplayName(gate.factionId))) { gate.factionId = NextTeam(gate.factionId); _dirty = true; }
        GUILayout.EndHorizontal();

        _gateScroll = GUILayout.BeginScrollView(_gateScroll, GUILayout.Height(380f));
        GateDeployment remove = null;
        foreach (var dep in gate.deployments)
        {
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            dep.id = GUILayout.TextField(dep.id ?? "", 40);
            if (GUILayout.Button("x", GUILayout.Width(24))) remove = dep;
            GUILayout.EndHorizontal();

            DrawTrigger(dep);
            DrawRepeats(dep);
            DrawSquad(dep);
        }
        GUILayout.EndScrollView();
        if (remove != null) { gate.deployments.Remove(remove); _dirty = true; }
        if (GUILayout.Button("Add deployment")) { gate.deployments.Add(NewDeployment(gate)); _dirty = true; }
    }

    private void DrawTrigger(GateDeployment dep)
    {
        var t = dep.trigger ??= new DeploymentTrigger();
        var kind = (DeploymentTriggerKind)GUILayout.Toolbar((int)t.kind, new[] { "Time", "Squad lost", "Tiles", "Heart" });
        if (kind != t.kind) { t.kind = kind; _dirty = true; }

        GUILayout.BeginHorizontal();
        switch (t.kind)
        {
            case DeploymentTriggerKind.AfterTime:
                GUILayout.Label("After minutes", GUILayout.Width(100));
                t.minutes = FloatField($"{dep.id}.min", t.minutes, GUILayout.Width(60));
                break;
            case DeploymentTriggerKind.SquadDefeated:
                GUILayout.Label("When defeated:", GUILayout.Width(100));
                if (GUILayout.Button(string.IsNullOrEmpty(t.deploymentId) ? "(choose)" : t.deploymentId))
                { t.deploymentId = NextDeploymentId(t.deploymentId, dep); _dirty = true; }
                break;
            case DeploymentTriggerKind.TilesClaimed:
                if (GUILayout.Button(FactionTeams.DisplayName(t.team), GUILayout.Width(100))) { t.team = NextTeam(t.team); _dirty = true; }
                GUILayout.Label("claims", GUILayout.Width(46));
                t.tiles = Mathf.Max(0, IntField($"{dep.id}.tiles", t.tiles, GUILayout.Width(60)));
                break;
            case DeploymentTriggerKind.HeartDestroyed:
                GUILayout.Label("Heart of", GUILayout.Width(60));
                if (GUILayout.Button(t.team == FactionID.Unaligned ? "any team" : FactionTeams.DisplayName(t.team)))
                { t.team = NextTeamOrAny(t.team); _dirty = true; }
                break;
        }
        GUILayout.EndHorizontal();
    }

    private void DrawRepeats(GateDeployment dep)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Repeat every (min, 0 = once)", GUILayout.Width(180));
        dep.repeatMinutes = Mathf.Max(0f, FloatField($"{dep.id}.rep", dep.repeatMinutes, GUILayout.Width(50)));
        GUILayout.EndHorizontal();
        if (dep.repeatMinutes <= 0f) return;

        GUILayout.BeginHorizontal();
        GUILayout.Label("Max repeats (blank = forever)", GUILayout.Width(180));
        string key = $"{dep.id}.max";
        string text = _intBuffers.TryGetValue(key, out var b) ? b : dep.maxRepeats?.ToString() ?? "";
        text = GUILayout.TextField(text, 6, GUILayout.Width(50));
        _intBuffers[key] = text;
        int? max = int.TryParse(text, out int m) ? Mathf.Max(0, m) : (int?)null;
        if (max != dep.maxRepeats) { dep.maxRepeats = max; _dirty = true; }
        GUILayout.EndHorizontal();
    }

    private void DrawSquad(GateDeployment dep)
    {
        SquadMember remove = null;
        foreach (var member in dep.squad)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{member.contentFactionId} {member.minionId} L{member.level}" + (member.isLord ? "  (lord)" : ""), _small);
            if (GUILayout.Button("-", GUILayout.Width(22))) { member.count = Mathf.Max(1, member.count - 1); _dirty = true; }
            GUILayout.Label($"x{member.count}", GUILayout.Width(30));
            if (GUILayout.Button("+", GUILayout.Width(22))) { member.count++; _dirty = true; }
            if (GUILayout.Button("x", GUILayout.Width(22))) remove = member;
            GUILayout.EndHorizontal();
        }
        if (remove != null) { dep.squad.Remove(remove); _dirty = true; }

        string what = _minionDef != null ? $"{MinionName(_minionDef)} L{_minionLevel}{(_placeAsLord ? " (lord)" : "")}" : "a minion";
        GUI.enabled = _minionDef != null;
        if (GUILayout.Button($"Add {what} to this squad"))
        {
            dep.squad.Add(new SquadMember
            {
                contentFactionId = _minionFaction.factionContentId,
                minionId         = _minionDef.minionId,
                level            = _minionLevel,
                count            = 1,
                isLord           = _placeAsLord,
            });
            _dirty = true;
        }
        GUI.enabled = true;
    }

    /// <summary>The minion picker for squads (the same one the Minions tool uses).</summary>
    private void DrawGateMinionPicker()
    {
        if (_selectedGate == null || _removeMode) return;
        GUILayout.Label("Squad minion (choose, then 'Add … to this squad')", _header);
        DrawMinionPalette();
    }

    private FactionID NextTeam(FactionID current)
    {
        var teams = new List<FactionID>();
        foreach (var id in FactionTeams.All) if (id != FactionID.Unaligned) teams.Add(id);
        int i = teams.IndexOf(current);
        return teams[(i + 1) % teams.Count];
    }

    private FactionID NextTeamOrAny(FactionID current) =>
        current == FactionID.Hostile ? FactionID.Unaligned : current == FactionID.Unaligned ? FactionID.Player : NextTeam(current);

    private string NextDeploymentId(string current, GateDeployment self)
    {
        var ids = new List<string>();
        foreach (var d in AllDeployments()) if (d != self && !string.IsNullOrEmpty(d.id)) ids.Add(d.id);
        if (ids.Count == 0) return null;
        int i = ids.IndexOf(current);
        return ids[(i + 1) % ids.Count];
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
                    warnings.Add($"{d.id} is set to go when another squad is defeated, but none is chosen (or it's been renamed).");
            }
        }
    }

    /// <summary>Is any lord of the land in the level — placed, or in a gate's squad?</summary>
    private bool AnyLord() =>
        _minions.Exists(p => p.isLord) || _gates.Exists(g => g.deployments.Exists(d => d.squad.Exists(m => m.isLord)));
}
