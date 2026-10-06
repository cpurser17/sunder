using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The level editor's IMGUI: top bar (file, undo, teams, level settings),
/// left panel (team picker, tools, palettes), status bar, and the modal
/// windows (start prompt, teams, level settings, save warnings, discard
/// confirmation). Every panel's rect is recorded so the controller can tell
/// when the pointer is over UI rather than the map.
/// </summary>
public partial class LevelEditorController
{
    private enum Modal { None, Start, Teams, LevelSettings, Warnings, Confirm }

    private const float TopBarHeight = 34f;
    private const float PanelWidth   = 300f;
    private const float StatusHeight = 26f;
    private const float LabelZoomLimit = 14f; // orthographic size below which minion ids are drawn

    private Modal _modal = Modal.Start;

    private string       _widthText, _heightText;
    private List<string> _levelIds;
    private Vector2      _loadScroll, _paletteScroll, _warningScroll;
    private List<string> _warnings = new();
    private string       _confirmMessage;
    private System.Action _confirmAction;
    private string       _allowedText = "";
    private readonly Dictionary<string, string> _intBuffers = new();

    private readonly List<Rect> _guiRects = new();
    private float _uiScale = 1f;

    // Clicks that change which controls exist (tool, modal, a whole new
    // level) run after the GUI pass, so IMGUI's layout for the rest of the
    // pass still matches what it measured.
    private System.Action _deferred;
    private void Defer(System.Action action) => _deferred += action;

    private bool     _stylesReady;
    private GUIStyle _header, _small, _leftButton, _minionButton, _statusError, _statusOk, _mapLabel;

    private static readonly Color PanelColour = new(0.11f, 0.11f, 0.13f, 0.94f);

    // ── Pointer test (used by Update) ──────────────────────────────────

    private bool IsPointerOverGui()
    {
        if (_modal != Modal.None) return true;
        var p = new Vector2(Input.mousePosition.x / _uiScale,
                            (Screen.height - Input.mousePosition.y) / _uiScale);
        foreach (var r in _guiRects)
            if (r.Contains(p)) return true;
        return false;
    }

    // ── Root ───────────────────────────────────────────────────────────

    private void OnGUI()
    {
        EnsureStyles();
        _uiScale   = Mathf.Clamp(Screen.height / 1080f, 1f, 2.5f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(_uiScale, _uiScale, 1f));
        float w = Screen.width / _uiScale, h = Screen.height / _uiScale;

        _guiRects.Clear();
        if (!_editing) _modal = Modal.Start;

        if (_editing)
        {
            DrawMinionLabels();
            DrawTopBar(new Rect(0f, 0f, w, TopBarHeight));
            DrawToolPanel(new Rect(0f, TopBarHeight, PanelWidth, h - TopBarHeight - StatusHeight));
            DrawStatusBar(new Rect(0f, h - StatusHeight, w, StatusHeight));
        }

        if (_modal != Modal.None) DrawModal(w, h);

        // Clicking the map takes focus away from any text field, so the
        // keyboard goes back to shortcuts and camera panning.
        if (Event.current.type == EventType.MouseDown && !IsPointerOverGui())
            GUIUtility.keyboardControl = 0;

        if (_deferred != null)
        {
            var actions = _deferred;
            _deferred = null;
            actions();
        }
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        _header       = new GUIStyle(GUI.skin.label)  { fontStyle = FontStyle.Bold, fontSize = 13 };
        _small        = new GUIStyle(GUI.skin.label)  { fontSize = 11, wordWrap = true };
        _leftButton   = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        _minionButton = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, fontSize = 11 };
        _statusOk     = new GUIStyle(GUI.skin.label)  { alignment = TextAnchor.MiddleRight };
        _statusError  = new GUIStyle(_statusOk);
        _statusError.normal.textColor = new Color(1f, 0.45f, 0.4f);
        _mapLabel     = new GUIStyle(GUI.skin.label)  { alignment = TextAnchor.UpperCenter, fontSize = 10 };
        _mapLabel.normal.textColor = Color.white;
    }

    private void Panel(Rect rect)
    {
        _guiRects.Add(rect);
        DrawRect(rect, PanelColour);
    }

    private static void DrawRect(Rect rect, Color colour)
    {
        var old = GUI.color;
        GUI.color = colour;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private static void Swatch(Color colour, float size = 18f)
    {
        var r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
        r.y += 2f;
        DrawRect(r, Color.black);
        DrawRect(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), colour);
    }

    // ── Top bar ────────────────────────────────────────────────────────

    private void DrawTopBar(Rect rect)
    {
        Panel(rect);
        GUILayout.BeginArea(new Rect(rect.x + 6f, rect.y + 5f, rect.width - 12f, rect.height - 6f));
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("New / Load", GUILayout.Width(90))) Defer(() => RequestDiscard(OpenStart));
        if (GUILayout.Button("Save", GUILayout.Width(60)))        Defer(() => Save(toBuiltIn: false));
#if UNITY_EDITOR
        if (GUILayout.Button(new GUIContent("Save to StreamingAssets",
                "Ship this level with the game (Editor only). A same-named level in " +
                "CustomLevels still takes priority when loading."), GUILayout.Width(170)))
            Defer(() => Save(toBuiltIn: true));
#endif
        if (GUILayout.Button("Play Test", GUILayout.Width(80))) Defer(PlayTest);

        GUILayout.Space(14f);
        GUI.enabled = _undo.Count > 0;
        if (GUILayout.Button("Undo", GUILayout.Width(55))) Defer(Undo);
        GUI.enabled = _redo.Count > 0;
        if (GUILayout.Button("Redo", GUILayout.Width(55))) Defer(Redo);
        GUI.enabled = true;

        GUILayout.Space(14f);
        if (GUILayout.Button("Teams", GUILayout.Width(70))) Defer(() => _modal = Modal.Teams);
        if (GUILayout.Button("Level Settings", GUILayout.Width(110))) Defer(OpenLevelSettings);
        if (GUILayout.Button("Frame   (F)", GUILayout.Width(80))) editorCamera.Frame(gridManager);

        GUILayout.FlexibleSpace();
        GUILayout.Label($"{_level.displayName}  [{_level.levelId}]  {gridManager.Width}x{gridManager.Height}" +
                        (_dirty ? "  •  unsaved" : ""), _header);

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    // ── Left panel ─────────────────────────────────────────────────────

    private void DrawToolPanel(Rect rect)
    {
        Panel(rect);
        GUILayout.BeginArea(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 10f));

        GUILayout.Label("Team", _header);
        foreach (var id in FactionTeams.All)
        {
            GUILayout.BeginHorizontal();
            Swatch(TeamColour(id));
            string label = TeamLabel(id);
            if (id != FactionID.Unaligned && !_activeTeams.Contains(id)) label += "  (off)";
            if (GUILayout.Toggle(_team == id, label, _leftButton) && _team != id) _team = id;
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8f);
        GUILayout.Label("Tool   (1-4)", _header);
        var tool = (Tool)GUILayout.Toolbar((int)_tool, new[] { "Tiles", "Heart", "Minions", "Owner" });
        if (tool != _tool) Defer(() => _tool = tool);
        GUILayout.Space(4f);

        switch (_tool)
        {
            case Tool.Tiles:
                BrushSizeRow();
                break;
            case Tool.Heart:
                PlaceRemoveRow();
                GUILayout.Label(_removeMode
                    ? "Click a heart to turn it back into its team's tunnel."
                    : "Click to place the selected team's 3x3 Dungeon Heart. Each team " +
                      "has one — placing it again moves it.", _small);
                break;
            case Tool.Minions:
                PlaceRemoveRow();
                if (_removeMode) BrushSizeRow();
                break;
            case Tool.Ownership:
                BrushSizeRow();
                GUILayout.BeginHorizontal();
                _brushTiles   = GUILayout.Toggle(_brushTiles,   " Tiles");
                _brushMinions = GUILayout.Toggle(_brushMinions, " Minions");
                GUILayout.EndHorizontal();
                GUILayout.Label("Paints the selected team onto owned tiles (tunnel, walls, rooms, " +
                                "hearts…) and minions. Terrain and liquid are never owned.", _small);
                break;
        }

        GUILayout.Space(6f);
        _paletteScroll = GUILayout.BeginScrollView(_paletteScroll);
        if (_tool == Tool.Tiles)                         DrawTilePalette();
        else if (_tool == Tool.Minions && !_removeMode) DrawMinionPalette();
        else if (_tool == Tool.Heart)                   DrawHeartSummary();
        GUILayout.EndScrollView();

        GUILayout.EndArea();
    }

    private void BrushSizeRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Brush   ([ ])", GUILayout.Width(90));
        if (GUILayout.Button("-", GUILayout.Width(26))) _brushSize = Mathf.Max(1, _brushSize - 2);
        GUILayout.Label($"{_brushSize} x {_brushSize}", GUILayout.Width(50));
        if (GUILayout.Button("+", GUILayout.Width(26))) _brushSize = Mathf.Min(MaxBrush, _brushSize + 2);
        GUILayout.EndHorizontal();
    }

    private void PlaceRemoveRow()
    {
        bool remove = GUILayout.Toolbar(_removeMode ? 1 : 0, new[] { "Place", "Remove   (R)" }) == 1;
        if (remove != _removeMode) Defer(() => _removeMode = remove);
    }

    private void DrawTilePalette()
    {
        var groups = new (string title, List<TileDefinition> defs)[]
        {
            ("Terrain",    new List<TileDefinition>()),
            ("Liquid",     new List<TileDefinition>()),
            ("Structures", new List<TileDefinition>()),
            ("Rooms",      new List<TileDefinition>()),
        };

        foreach (var def in gridManager.Tiles.Definitions)
        {
            if (def == null || def.tileType == TileType.Heart) continue; // the Heart tool owns hearts
            int group = def.category switch
            {
                TileCategory.Environmental => 0,
                TileCategory.Liquid        => 1,
                _ => def.isRoom && !def.placesOnLiquid ? 3 : 2,
            };
            groups[group].defs.Add(def);
        }

        foreach (var (title, defs) in groups)
        {
            if (defs.Count == 0) continue;
            GUILayout.Label(title, _header);
            foreach (var def in defs)
            {
                FactionID owner = def.category == TileCategory.Owned ? _team : FactionID.Unaligned;
                GUILayout.BeginHorizontal();
                Swatch(gridManager.Tiles.GetColour(def.tileType, owner));
                string name = string.IsNullOrEmpty(def.tileName) ? def.tileType.ToString() : def.tileName;
                if (GUILayout.Toggle(_tileType == def.tileType, name, _leftButton))
                    _tileType = def.tileType;
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4f);
        }
    }

    private void DrawMinionPalette()
    {
        if (factionRegistry == null || factionRegistry.Definitions.Count == 0)
        {
            GUILayout.Label("No Faction Registry (or it's empty) — assign one on the LevelEditor object.", _small);
            return;
        }

        GUILayout.Label("Faction", _header);
        var factions = new List<FactionDefinition>();
        var names    = new List<string>();
        foreach (var def in factionRegistry.Definitions)
        {
            if (def == null) continue;
            factions.Add(def);
            names.Add(string.IsNullOrWhiteSpace(def.displayName) || def.displayName == def.factionContentId
                ? def.factionContentId
                : $"{def.displayName} ({def.factionContentId})");
        }
        int current = Mathf.Max(0, factions.IndexOf(_minionFaction));
        int picked  = GUILayout.SelectionGrid(current, names.ToArray(), 2);
        if (picked != current || _minionFaction == null)
        {
            var faction = factions[picked];
            Defer(() => SelectMinionFaction(faction));
        }
        if (_minionFaction == null) return;

        if (_minionDef != null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Level", GUILayout.Width(90));
            if (GUILayout.Button("-", GUILayout.Width(26))) _minionLevel--;
            GUILayout.Label(_minionLevel.ToString(), GUILayout.Width(50));
            if (GUILayout.Button("+", GUILayout.Width(26))) _minionLevel++;
            _minionLevel = Mathf.Clamp(_minionLevel, 1, Mathf.Max(1, _minionDef.maxLevel));
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(4f);
        foreach (var def in _minionFaction.EveryMinion())
        {
            GUILayout.BeginHorizontal();
            TokenIcon(def.token, 34f);
            string label = $"{MinionName(def)}\n{def.minionId} · {def.stance}" +
                           (def.summonable ? "" : " · not summonable");
            if (GUILayout.Toggle(_minionDef == def, label, _minionButton, GUILayout.Height(36)) && _minionDef != def)
            {
                _minionDef   = def;
                _minionLevel = Mathf.Clamp(_minionLevel, 1, Mathf.Max(1, def.maxLevel));
            }
            GUILayout.EndHorizontal();
        }
    }

    private static void TokenIcon(Sprite token, float size)
    {
        var r = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
        if (token == null || token.texture == null)
        {
            DrawRect(r, new Color(0.25f, 0.25f, 0.28f));
            return;
        }
        var tex = token.texture;
        var tr  = token.textureRect;
        GUI.DrawTextureWithTexCoords(r, tex,
            new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
    }

    private void DrawHeartSummary()
    {
        GUILayout.Label("Hearts", _header);
        var counts = new Dictionary<FactionID, int>();
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var c = gridManager.GetCell(x, y);
            if (c.TileType == TileType.Heart) counts[c.Owner] = counts.GetValueOrDefault(c.Owner) + 1;
        }

        foreach (var id in FactionTeams.All)
        {
            if (id == FactionID.Unaligned || (!_activeTeams.Contains(id) && !counts.ContainsKey(id))) continue;
            GUILayout.BeginHorizontal();
            Swatch(TeamColour(id), 14f);
            GUILayout.Label($"{FactionTeams.DisplayName(id)}: {(counts.ContainsKey(id) ? "placed" : "none yet")}", _small);
            GUILayout.EndHorizontal();
        }
    }

    // ── Status bar and map labels ──────────────────────────────────────

    private void DrawStatusBar(Rect rect)
    {
        Panel(rect);
        GUILayout.BeginArea(new Rect(rect.x + 8f, rect.y + 3f, rect.width - 16f, rect.height - 4f));
        GUILayout.BeginHorizontal();

        if (_hasHover)
        {
            var cell = gridManager.GetCell(_hoverX, _hoverY);
            int here = 0;
            foreach (var p in _minions) if (IsInCell(p, cell)) here++;
            string owner = IsOwnable(cell.TileType) ? $" · {FactionTeams.DisplayName(cell.Owner)}" : "";
            GUILayout.Label($"({_hoverX}, {_hoverY})  {gridManager.Tiles.GetTileName(cell.TileType)}{owner}" +
                            (here > 0 ? $" · {here} minion(s)" : "") +
                            (IsBorder(cell) ? " · border (locked)" : ""), GUILayout.Width(420));
        }
        else GUILayout.Label("Paint: left mouse · Pan: right/middle drag or WASD · Zoom: wheel · Ctrl+Z / Ctrl+Y",
                             GUILayout.Width(560));

        GUILayout.FlexibleSpace();
        GUILayout.Label(_status, _statusIsError ? _statusError : _statusOk);

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void DrawMinionLabels()
    {
        if (Event.current.type != EventType.Repaint) return;
        var cam = editorCamera.Camera;
        if (cam.orthographicSize > LabelZoomLimit || _minions.Count > 500) return;

        float cell = gridManager.CellSize;
        foreach (var p in _minions)
        {
            Vector3 screen = cam.WorldToScreenPoint(PlacementWorld(p) - new Vector3(0f, 0f, 0.4f * cell));
            var pos = new Vector2(screen.x / _uiScale, (Screen.height - screen.y) / _uiScale);
            GUI.Label(new Rect(pos.x - 40f, pos.y, 80f, 16f), $"{p.minionId} L{p.level}", _mapLabel);
        }
    }

    // ── Modals ─────────────────────────────────────────────────────────

    private void DrawModal(float w, float h)
    {
        DrawRect(new Rect(0f, 0f, w, h), new Color(0f, 0f, 0f, 0.55f));

        float width = _modal switch
        {
            Modal.Teams         => 800f,
            Modal.LevelSettings => 520f,
            Modal.Start         => 440f,
            _                   => 460f,
        };
        var rect = new Rect((w - width) * 0.5f, Mathf.Max(20f, h * 0.12f), width, 0f);

        switch (_modal)
        {
            case Modal.Start:
                GUILayout.Window(1, rect, StartWindow, "Sunder — Level Editor");
                break;
            case Modal.Teams:
                GUILayout.Window(2, rect, TeamsWindow, "Teams");
                break;
            case Modal.LevelSettings:
                GUILayout.Window(3, rect, LevelSettingsWindow, "Level Settings");
                break;
            case Modal.Warnings:
                GUILayout.Window(4, rect, WarningsWindow, "Saved — with warnings");
                break;
            case Modal.Confirm:
                GUILayout.Window(5, rect, ConfirmWindow, "Unsaved changes");
                break;
        }
    }

    private void OpenStart()
    {
        _levelIds = SaveLoadSystem.GetAvailableLevelIds();
        _levelIds.Sort(System.StringComparer.OrdinalIgnoreCase);
        _modal = Modal.Start;
    }

    private void OpenLevelSettings()
    {
        _allowedText = string.Join(", ", _level.allowedMinionIds ?? new List<string>());
        _modal = Modal.LevelSettings;
    }

    /// <summary>Runs the action now, or after the designer confirms losing unsaved work.</summary>
    private void RequestDiscard(System.Action action)
    {
        if (!_editing || !_dirty) { action(); return; }
        _confirmMessage = "This level has unsaved changes. Discard them?";
        _confirmAction  = action;
        _modal          = Modal.Confirm;
    }

    private void StartWindow(int id)
    {
        if (_levelIds == null) OpenStart();

        GUILayout.Label("New level", _header);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Width", GUILayout.Width(45));
        _widthText = GUILayout.TextField(_widthText, 4, GUILayout.Width(60));
        GUILayout.Space(16f);
        GUILayout.Label("Height", GUILayout.Width(48));
        _heightText = GUILayout.TextField(_heightText, 4, GUILayout.Width(60));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Create", GUILayout.Width(90))) Defer(TryCreateFromPrompt);
        GUILayout.EndHorizontal();
        GUILayout.Label($"{minSize}–{maxSize} tiles a side. The outer edge is bedrock; " +
                        "every tile inside starts as stone.", _small);

        GUILayout.Space(12f);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Load a level", _header);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Refresh", GUILayout.Width(70))) Defer(OpenStart);
        GUILayout.EndHorizontal();

        _loadScroll = GUILayout.BeginScrollView(_loadScroll, GUILayout.Height(220));
        if (_levelIds.Count == 0) GUILayout.Label("No levels found.", _small);
        foreach (var levelId in _levelIds)
            if (GUILayout.Button(levelId, _leftButton)) Defer(() => LoadLevel(levelId));
        GUILayout.EndScrollView();
        GUILayout.Label("Built-in levels come from StreamingAssets/Levels, your own from " +
                        "CustomLevels in the persistent data folder.", _small);

        if (_editing)
        {
            GUILayout.Space(8f);
            if (GUILayout.Button("Cancel")) Defer(() => _modal = Modal.None);
        }

        GUILayout.Space(4f);
        GUILayout.Label(_status, _statusIsError ? _statusError : _small);
    }

    private void TryCreateFromPrompt()
    {
        if (!int.TryParse(_widthText, out int width) || !int.TryParse(_heightText, out int height) ||
            width < minSize || height < minSize || width > maxSize || height > maxSize)
        {
            SetStatus($"Width and height must be whole numbers from {minSize} to {maxSize}.", error: true);
            return;
        }
        CreateNewLevel(width, height);
    }

    private void TeamsWindow(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("On",           _header, GUILayout.Width(28));
        GUILayout.Label("Team",         _header, GUILayout.Width(132));
        GUILayout.Label("Name",         _header, GUILayout.Width(190));
        GUILayout.Label("Human",        _header, GUILayout.Width(60));
        GUILayout.Label("Faction",      _header, GUILayout.Width(150));
        GUILayout.Label("Starting gold", _header);
        GUILayout.EndHorizontal();

        foreach (var team in FactionTeams.All)
        {
            if (team == FactionID.Unaligned) continue;
            var setup = _teams[team];

            GUILayout.BeginHorizontal();
            bool on = GUILayout.Toggle(_activeTeams.Contains(team), "", GUILayout.Width(28));
            if (on) _activeTeams.Add(team); else _activeTeams.Remove(team);

            Swatch(TeamColour(team));
            GUILayout.Label(FactionTeams.DisplayName(team), GUILayout.Width(110));
            setup.displayName = GUILayout.TextField(setup.displayName ?? "", 40, GUILayout.Width(190));

            GUI.enabled = FactionTeams.IsPlayable(team);
            setup.isHuman = GUILayout.Toggle(setup.isHuman && GUI.enabled, "", GUILayout.Width(60));
            GUI.enabled = true;

            if (GUILayout.Button(string.IsNullOrEmpty(setup.contentFactionId) ? "(none)" : setup.contentFactionId,
                                 GUILayout.Width(150)))
                setup.contentFactionId = NextContentFaction(setup.contentFactionId);

            setup.startingGold = Mathf.Max(0, IntField($"gold{team}", setup.startingGold, GUILayout.Width(80)));
            GUILayout.Label(setup.startingGold == 0 ? $"(level default {_level.startingGold})" : "", _small);
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8f);
        GUILayout.Label("Teams switch on by themselves when you paint, place a heart or a minion for them. " +
                        "Only active teams play. Neutral is unowned land and minions — it never plays. " +
                        "Enemy to all is hostile to every team. Faction picks the roster a team summons " +
                        "from (click to cycle). Tile colours come from the Tile Registry.", _small);
        if (GUILayout.Button("Close")) Defer(() => { _modal = Modal.None; _dirty = true; });
    }

    private string NextContentFaction(string current)
    {
        var ids = new List<string> { "" };
        if (factionRegistry != null)
            foreach (var def in factionRegistry.Definitions)
                if (def != null && !string.IsNullOrEmpty(def.factionContentId)) ids.Add(def.factionContentId);
        int i = ids.IndexOf(current ?? "");
        return ids[(i + 1) % ids.Count];
    }

    private void LevelSettingsWindow(int id)
    {
        GUILayout.Label("Level id (file name)", _header);
        _level.levelId = GUILayout.TextField(_level.levelId ?? "", 64);
        GUILayout.Label("Display name", _header);
        _level.displayName = GUILayout.TextField(_level.displayName ?? "", 80);
        GUILayout.Label("Description / briefing", _header);
        _level.description = GUILayout.TextArea(_level.description ?? "", GUILayout.Height(70));

        GUILayout.BeginHorizontal();
        GUILayout.Label("Default starting gold", GUILayout.Width(150));
        _level.startingGold = Mathf.Max(0, IntField("levelGold", _level.startingGold, GUILayout.Width(90)));
        GUILayout.EndHorizontal();

        GUILayout.Label("Allowed minion ids (comma-separated, empty = all)", _header);
        string allowed = GUILayout.TextField(_allowedText);
        if (allowed != _allowedText)
        {
            _allowedText = allowed;
            _level.allowedMinionIds = new List<string>();
            foreach (var part in allowed.Split(','))
                if (!string.IsNullOrWhiteSpace(part)) _level.allowedMinionIds.Add(part.Trim());
        }

        GUILayout.Space(6f);
        GUILayout.Label($"Grid {gridManager.Width} x {gridManager.Height} · {_minions.Count} placed minion(s). " +
                        $"Saves to CustomLevels/{_level.levelId}.json.", _small);
        if (GUILayout.Button("Close")) Defer(() => { _modal = Modal.None; _dirty = true; });
    }

    private void WarningsWindow(int id)
    {
        GUILayout.Label("The level saved and will load, but check these:", _small);
        _warningScroll = GUILayout.BeginScrollView(_warningScroll, GUILayout.Height(Mathf.Min(260f, 22f * _warnings.Count + 10f)));
        foreach (var w in _warnings) GUILayout.Label("• " + w, _small);
        GUILayout.EndScrollView();
        if (GUILayout.Button("OK")) Defer(() => _modal = Modal.None);
    }

    private void ConfirmWindow(int id)
    {
        GUILayout.Label(_confirmMessage, _small);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Discard"))
        {
            var action = _confirmAction;
            Defer(() =>
            {
                _modal = Modal.None;
                _confirmAction = null;
                action?.Invoke();
            });
        }
        if (GUILayout.Button("Cancel")) Defer(() => { _modal = Modal.None; _confirmAction = null; });
        GUILayout.EndHorizontal();
    }

    /// <summary>A text field for an int that tolerates half-typed input.</summary>
    private int IntField(string key, int value, params GUILayoutOption[] options)
    {
        string text = _intBuffers.TryGetValue(key, out var buffered) ? buffered : value.ToString();
        if (int.TryParse(text, out int shown) && shown != value) text = value.ToString(); // changed elsewhere
        string edited = GUILayout.TextField(text, 9, options);
        _intBuffers[key] = edited;
        return int.TryParse(edited, out int parsed) ? parsed : value;
    }
}
