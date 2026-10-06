using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The level editor scene's one brain: start prompt (new level by size, or
/// load an existing one), painting tools, team setup, undo, and saving the
/// result as a LevelData file the Gameplay scene can load.
///
/// The grid is a real GridManager2D, so tiles look exactly as they will in
/// game and room/connectivity rebakes run as usual. Nothing else from the
/// Gameplay scene is needed — no GameManager2D, wallets or minion AI.
/// Placed minions are plain markers here (token on a team-coloured disc);
/// GameManager2D spawns the real thing from LevelData.minions on a new game.
///
/// Tools
///   Tiles      — paint a tile type. Owned types take the selected team;
///                terrain and liquid are always unowned. Bridge-like types
///                (placesOnLiquid) only go on water or lava.
///   Heart      — place a team's 3x3 Dungeon Heart (one per team: placing
///                again moves it), or remove one.
///   Minions    — place a minion from any content faction for the selected
///                team, or remove minions under the brush.
///   Traps      — place a trap or door on claimed floor, or remove them.
///                They take the owner of the tile they stand on, and keep
///                following it as the tile changes hands; a tile that stops
///                being claimed floor loses its trap or door.
///   Ownership  — paint the selected team onto owned tiles and/or minions.
///
/// The outer ring of the grid is always bedrock and can't be edited.
///
/// UI is IMGUI, drawn in LevelEditorController.GUI.cs.
/// </summary>
public partial class LevelEditorController : MonoBehaviour
{
    public enum Tool { Tiles, Heart, Minions, TrapsDoors, Ownership }

    private const int HeartSize = 3;

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [SerializeField] private GridManager2D     gridManager;
    [SerializeField] private FactionRegistry   factionRegistry;
    [SerializeField] private TrapDoorRegistry  trapDoorRegistry;
    [Tooltip("Empty = added to the main camera at startup.")]
    [SerializeField] private LevelEditorCamera editorCamera;

    [Header("New level")]
    [SerializeField] private int defaultWidth  = 40;
    [SerializeField] private int defaultHeight = 40;
    [Tooltip("Smallest side: a bedrock ring around a 3x3 heart.")]
    [SerializeField, Min(5)] private int minSize = 5;
    [SerializeField] private int maxSize = 256;
    [SerializeField] private int defaultStartingGold = 500;

    [Header("Editing")]
    [SerializeField] private int maxUndoSteps = 50;
    [SerializeField] private Color hoverColour   = new(1f, 1f, 1f, 0.35f);
    [SerializeField] private Color invalidColour = new(1f, 0.2f, 0.2f, 0.45f);
    [Tooltip("Scene the Play Test button opens with this level. Must be in Build Settings.")]
    [SerializeField] private string gameplaySceneName = "Gameplay";

    // ── Level state ────────────────────────────────────────────────────
    private bool      _editing;   // false while the start prompt is up
    private LevelData _level;     // metadata; grid and minions live in the scene until saved
    private bool      _dirty;

    private readonly Dictionary<FactionID, FactionSetup> _teams       = new();
    private readonly HashSet<FactionID>                  _activeTeams = new();

    private class Marker
    {
        public GameObject     Root;
        public SpriteRenderer Disc;
    }
    private readonly List<MinionPlacement> _minions = new();
    private readonly List<Marker>          _markers = new(); // parallel to _minions
    private Transform _markerRoot;
    private Sprite    _discSprite;
    private Sprite    _squareSprite;

    private class TrapDoorMarker
    {
        public TrapDoorPlacement Data;
        public GameObject        Root;
        public SpriteRenderer    Team;
    }
    // One trap or door per cell, keyed by cell.
    private readonly Dictionary<Vector2Int, TrapDoorMarker> _trapsDoors = new();
    private bool _rebuildingGrid; // ignore OnTileChanged while a whole grid is re-applied

    // ── Tool state ─────────────────────────────────────────────────────
    private Tool      _tool      = Tool.Tiles;
    private FactionID _team      = FactionID.Player;
    private TileType  _tileType  = TileType.Tunnel;
    private int       _brushSize = 1;      // odd: 1, 3, 5…
    private bool      _removeMode;         // Heart / Minions: remove instead of place
    private bool      _brushTiles   = true;
    private bool      _brushMinions = true;

    private FactionDefinition _minionFaction;
    private MinionDefinition  _minionDef;
    private int               _minionLevel = 1;

    private TrapDoorKind       _trapDoorKind = TrapDoorKind.Trap;
    private TrapDoorDefinition _trapDoorDef;

    // ── Stroke / hover ─────────────────────────────────────────────────
    private bool _stroking;
    private bool _strokeChanged;
    private Snapshot _strokeBefore;
    private Vector2Int _lastStrokeCell;
    private readonly HashSet<GridCell> _strokeCells = new();

    private bool    _hasHover;
    private int     _hoverX, _hoverY;
    private Vector3 _hoverWorld;
    private readonly List<GridCell> _highlighted = new();

    // ── Undo ───────────────────────────────────────────────────────────
    private class Snapshot
    {
        public GridSaveData          Grid;
        public List<MinionPlacement> Minions;
        public List<TrapDoorPlacement> TrapsDoors;
    }
    private readonly LinkedList<Snapshot> _undo = new();
    private readonly Stack<Snapshot>      _redo = new();

    // ── Messages ───────────────────────────────────────────────────────
    private string _status = "";
    private bool   _statusIsError;
    private float  _statusTime;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (gridManager == null) gridManager = FindAnyObjectByType<GridManager2D>();

        if (editorCamera == null)
        {
            var cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            editorCamera = cam.GetComponent<LevelEditorCamera>();
            if (editorCamera == null) editorCamera = cam.gameObject.AddComponent<LevelEditorCamera>();
        }

        if (factionRegistry != null) factionRegistry.Initialise();
        else Debug.LogWarning("[LevelEditor] No FactionRegistry assigned — minions can't be placed.");

        _markerRoot   = new GameObject("PlacedMinions").transform;
        _discSprite   = BuildDiscSprite();
        _squareSprite = BuildSquareSprite();
        gridManager.OnTileChanged += OnTileChanged;

        _widthText  = defaultWidth.ToString();
        _heightText = defaultHeight.ToString();

        ResetTeams();
        SelectMinionFaction(FirstFactionDefinition());
        SelectTrapDoorKind(TrapDoorKind.Trap);
    }

    private void OnDestroy()
    {
        if (gridManager != null) gridManager.OnTileChanged -= OnTileChanged;
    }

    private void Update()
    {
        if (!_editing || _modal != Modal.None)
        {
            ClearHighlights();
            return;
        }

        bool pointerFree  = !IsPointerOverGui();
        bool keyboardFree = GUIUtility.keyboardControl == 0;

        editorCamera.Tick(pointerFree, keyboardFree);
        if (keyboardFree) HandleShortcuts();

        UpdateHover(pointerFree);
        HandlePointer(pointerFree);
    }

    // ── Start / load ───────────────────────────────────────────────────

    /// <summary>A rectangle of stone inside a one-tile bedrock ring.</summary>
    private void CreateNewLevel(int width, int height)
    {
        var cells = new CellSaveData[width, height];
        for (int x = 0; x < width;  x++)
        for (int y = 0; y < height; y++)
        {
            bool edge  = x == 0 || y == 0 || x == width - 1 || y == height - 1;
            var  type  = edge ? TileType.Bedrock : TileType.Stone;
            cells[x, y] = new CellSaveData(type, type, FactionID.Unaligned);
        }

        ClearHighlights();
        ApplyWholeGrid(() => gridManager.Initialise(width, height, cells));

        _level = new LevelData
        {
            levelId      = UniqueLevelId("NewLevel"),
            displayName  = "New Level",
            description  = "",
            startingGold = defaultStartingGold,
        };
        ResetTeams();
        ClearMinions();
        ClearTrapsDoors();
        BeginEditing();
        SetStatus($"Created a {width}x{height} level.");
    }

    private void LoadLevel(string levelId)
    {
        var data = SaveLoadSystem.LoadLevel(levelId);
        if (data == null || data.grid == null || data.grid.cells == null)
        {
            SetStatus($"Couldn't load '{levelId}' — see the console.", error: true);
            return;
        }

        ClearHighlights();
        ApplyWholeGrid(() => SaveLoadSystem.ApplyGrid(gridManager, data.grid));

        _level = data;
        _level.allowedMinionIds ??= new List<string>();

        ResetTeams();
        foreach (var setup in data.factions ?? new List<FactionSetup>())
        {
            if (setup == null || setup.factionId == FactionID.Unaligned) continue;
            _teams[setup.factionId] = setup;
            _activeTeams.Add(setup.factionId);
        }

        ClearMinions();
        foreach (var p in data.minions ?? new List<MinionPlacement>())
            if (p != null) AddMinion(p.Clone());

        ClearTrapsDoors();
        foreach (var t in data.trapsAndDoors ?? new List<TrapDoorPlacement>())
            if (t != null) AddTrapDoor(t.Clone());
        SyncAllTrapDoors(); // an older or hand-edited file may disagree with its tiles

        BeginEditing();
        SetStatus($"Loaded '{levelId}' ({data.grid.width}x{data.grid.height}, " +
                  $"{_minions.Count} minion(s)).");
    }

    private void BeginEditing()
    {
        _editing = true;
        _dirty   = false;
        _modal   = Modal.None;
        _undo.Clear();
        _redo.Clear();
        editorCamera.Frame(gridManager);
    }

    private static string UniqueLevelId(string stem)
    {
        var existing = new HashSet<string>(SaveLoadSystem.GetAvailableLevelIds(),
                                           System.StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(stem)) return stem;
        for (int i = 2; ; i++)
            if (!existing.Contains($"{stem}{i}")) return $"{stem}{i}";
    }

    // ── Teams ──────────────────────────────────────────────────────────

    /// <summary>Default setup for every assignable team; none active.</summary>
    private void ResetTeams()
    {
        _teams.Clear();
        _activeTeams.Clear();

        string content = FirstFactionDefinition()?.factionContentId ?? "";
        foreach (var id in FactionTeams.All)
        {
            if (id == FactionID.Unaligned) continue;
            _teams[id] = new FactionSetup
            {
                factionId        = id,
                displayName      = FactionTeams.DisplayName(id),
                isHuman          = id == FactionID.Player,
                contentFactionId = content,
                startingGold     = 0, // 0 = the level's default
            };
        }
    }

    /// <summary>Anything placed for a team makes it part of the level.</summary>
    private void ActivateTeam(FactionID team)
    {
        if (team != FactionID.Unaligned) _activeTeams.Add(team);
    }

    private Color TeamColour(FactionID team) => gridManager.Tiles.GetFactionColour(team);

    private string TeamLabel(FactionID team)
    {
        string label = FactionTeams.DisplayName(team);
        if (_teams.TryGetValue(team, out var setup) &&
            !string.IsNullOrWhiteSpace(setup.displayName) && setup.displayName != label)
            label += $" — {setup.displayName}";
        return label;
    }

    // ── Pointer ────────────────────────────────────────────────────────

    private void UpdateHover(bool pointerFree)
    {
        _hasHover = pointerFree &&
                    editorCamera.TryGetGroundPoint(Input.mousePosition, out _hoverWorld) &&
                    gridManager.WorldToCell(_hoverWorld, out _hoverX, out _hoverY);

        ClearHighlights();
        if (!_hasHover) return;

        bool valid = true;
        IEnumerable<GridCell> cells;
        switch (_tool)
        {
            case Tool.Heart when !_removeMode:
                cells = HeartFootprint(_hoverX, _hoverY);
                valid = CanPlaceHeart(_hoverX, _hoverY, out _);
                break;
            case Tool.Minions when !_removeMode:
                cells = new[] { gridManager.GetCell(_hoverX, _hoverY) };
                valid = CanStandOn(gridManager.GetCell(_hoverX, _hoverY));
                break;
            case Tool.TrapsDoors when !_removeMode:
                cells = new[] { gridManager.GetCell(_hoverX, _hoverY) };
                valid = CanHoldTrapDoor(gridManager.GetCell(_hoverX, _hoverY), out _);
                break;
            case Tool.Heart:
                cells = new[] { gridManager.GetCell(_hoverX, _hoverY) };
                break;
            default:
                cells = BrushCells(_hoverX, _hoverY);
                break;
        }

        Color colour = valid ? hoverColour : invalidColour;
        foreach (var cell in cells)
        {
            if (cell == null) continue;
            gridManager.SetHighlight(cell, true, colour);
            _highlighted.Add(cell);
        }
    }

    private void ClearHighlights()
    {
        // Cells can be stale after a grid rebuild (undo, load) — only touch
        // ones that are still the grid's own.
        foreach (var cell in _highlighted)
            if (gridManager.GetCell(cell.X, cell.Y) == cell)
                gridManager.SetHighlight(cell, false);
        _highlighted.Clear();
    }

    private void HandlePointer(bool pointerFree)
    {
        if (_stroking)
        {
            if (Input.GetMouseButton(0)) ContinueStroke();
            else EndStroke();
            return;
        }

        if (!pointerFree || !_hasHover || !Input.GetMouseButtonDown(0)) return;

        switch (_tool)
        {
            case Tool.Tiles:
            case Tool.Ownership:
                BeginStroke();
                break;

            case Tool.Minions when _removeMode:
            case Tool.TrapsDoors when _removeMode:
                BeginStroke();
                break;

            case Tool.TrapsDoors:
                RunSingleEdit(() => PlaceTrapDoor(gridManager.GetCell(_hoverX, _hoverY)));
                break;

            case Tool.Minions:
                RunSingleEdit(() => PlaceMinion(_hoverWorld));
                break;

            case Tool.Heart:
                RunSingleEdit(() => _removeMode ? RemoveHeartAt(_hoverX, _hoverY)
                                                : PlaceHeart(_hoverX, _hoverY));
                break;
        }
    }

    /// <summary>One click = one undo step, recorded only if it changed something.</summary>
    private void RunSingleEdit(System.Func<bool> edit)
    {
        var before = CaptureSnapshot();
        if (edit()) PushUndo(before);
    }

    // ── Strokes (drag painting) ────────────────────────────────────────

    private void BeginStroke()
    {
        _stroking      = true;
        _strokeChanged = false;
        _strokeBefore  = CaptureSnapshot();
        _strokeCells.Clear();
        _lastStrokeCell = new Vector2Int(_hoverX, _hoverY);
        PaintBrushAt(_hoverX, _hoverY);
    }

    private void ContinueStroke()
    {
        if (!editorCamera.TryGetGroundPoint(Input.mousePosition, out var world)) return;
        gridManager.WorldToCell(world, out int x, out int y);
        x = Mathf.Clamp(x, 0, gridManager.Width  - 1);
        y = Mathf.Clamp(y, 0, gridManager.Height - 1);

        var target = new Vector2Int(x, y);
        if (target == _lastStrokeCell) return;

        // Walk the line from the last cell so a fast drag leaves no gaps.
        foreach (var step in CellLine(_lastStrokeCell, target))
            PaintBrushAt(step.x, step.y);
        _lastStrokeCell = target;
    }

    private void EndStroke()
    {
        _stroking = false;
        if (_strokeChanged) PushUndo(_strokeBefore);
        _strokeBefore = null;
        _strokeCells.Clear();
    }

    private void PaintBrushAt(int cx, int cy)
    {
        foreach (var cell in BrushCells(cx, cy))
        {
            if (!_strokeCells.Add(cell)) continue;

            bool changed = _tool switch
            {
                Tool.Tiles     => PaintTile(cell),
                Tool.Ownership => PaintOwnership(cell),
                Tool.Minions   => RemoveMinionsIn(cell),
                Tool.TrapsDoors => RemoveTrapDoorAt(cell),
                _              => false,
            };
            _strokeChanged |= changed;
        }
    }

    private List<GridCell> BrushCells(int cx, int cy)
    {
        int r = _brushSize / 2;
        var cells = new List<GridCell>(_brushSize * _brushSize);
        for (int x = cx - r; x <= cx + r; x++)
        for (int y = cy - r; y <= cy + r; y++)
        {
            var c = gridManager.GetCell(x, y);
            if (c != null) cells.Add(c);
        }
        return cells;
    }

    private static IEnumerable<Vector2Int> CellLine(Vector2Int from, Vector2Int to)
    {
        int dx = Mathf.Abs(to.x - from.x), sx = from.x < to.x ? 1 : -1;
        int dy = -Mathf.Abs(to.y - from.y), sy = from.y < to.y ? 1 : -1;
        int err = dx + dy, x = from.x, y = from.y;
        while (true)
        {
            yield return new Vector2Int(x, y);
            if (x == to.x && y == to.y) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    // ── Tile painting ──────────────────────────────────────────────────

    private bool IsBorder(GridCell cell) =>
        cell.X == 0 || cell.Y == 0 || cell.X == gridManager.Width - 1 || cell.Y == gridManager.Height - 1;

    private bool IsOwnable(TileType type) => gridManager.GetCategory(type) == TileCategory.Owned;

    private bool PaintTile(GridCell cell)
    {
        if (IsBorder(cell)) return false;
        if (cell.TileType == TileType.Heart)
        {
            SetStatus("Hearts are edited with the Heart tool.", error: true);
            return false;
        }

        FactionID owner = IsOwnable(_tileType) ? _team : FactionID.Unaligned;

        if (gridManager.PlacesOnLiquid(_tileType))
        {
            if (cell.TileType == _tileType)
            {
                if (cell.Owner == owner) return false;
                gridManager.SetOwner(cell, owner);
            }
            else if (gridManager.GetCategory(cell.TileType) == TileCategory.Liquid)
                gridManager.PlaceBridge(cell, owner);
            else
            {
                SetStatus($"{gridManager.Tiles.GetTileName(_tileType)} can only go on water or lava.", error: true);
                return false;
            }
        }
        else
        {
            if (cell.TileType == _tileType && cell.Owner == owner) return false;
            gridManager.SetTileType(cell, _tileType, owner);
        }

        ActivateTeam(owner);
        _dirty = true;
        return true;
    }

    private bool PaintOwnership(GridCell cell)
    {
        bool changed = false;

        if (_brushTiles && IsOwnable(cell.TileType) && cell.Owner != _team)
        {
            if (cell.TileType == TileType.Heart) changed |= TransferHeart(cell.Owner);
            else
            {
                gridManager.SetOwner(cell, _team);
                changed = true;
            }
        }

        if (_brushMinions)
        {
            for (int i = 0; i < _minions.Count; i++)
            {
                var p = _minions[i];
                if (p.factionId == _team || !IsInCell(p, cell)) continue;
                p.factionId = _team;
                RefreshMarker(i);
                changed = true;
            }
        }

        if (changed)
        {
            ActivateTeam(_team);
            _dirty = true;
        }
        return changed;
    }

    // ── Dungeon Hearts ─────────────────────────────────────────────────

    private List<GridCell> HeartFootprint(int cx, int cy)
    {
        int r = HeartSize / 2;
        var cells = new List<GridCell>(HeartSize * HeartSize);
        for (int x = cx - r; x <= cx + r; x++)
        for (int y = cy - r; y <= cy + r; y++)
            cells.Add(gridManager.GetCell(x, y)); // null if off-grid
        return cells;
    }

    private List<GridCell> HeartCellsOf(FactionID team)
    {
        var cells = new List<GridCell>();
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var c = gridManager.GetCell(x, y);
            if (c.TileType == TileType.Heart && c.Owner == team) cells.Add(c);
        }
        return cells;
    }

    private bool CanPlaceHeart(int cx, int cy, out string reason)
    {
        if (_team == FactionID.Unaligned)
        {
            reason = "A Dungeon Heart needs a team — pick one other than Neutral.";
            return false;
        }
        foreach (var cell in HeartFootprint(cx, cy))
        {
            if (cell == null || IsBorder(cell))
            {
                reason = "The heart doesn't fit there — keep it inside the bedrock border.";
                return false;
            }
            if (cell.TileType == TileType.Heart && cell.Owner != _team)
            {
                reason = $"That overlaps {FactionTeams.DisplayName(cell.Owner)}'s heart.";
                return false;
            }
        }
        reason = null;
        return true;
    }

    /// <summary>Places the selected team's heart centred here, moving its old one if it had one.</summary>
    private bool PlaceHeart(int cx, int cy)
    {
        if (!CanPlaceHeart(cx, cy, out string reason))
        {
            SetStatus(reason, error: true);
            return false;
        }

        var footprint = HeartFootprint(cx, cy);
        bool moved = false;
        foreach (var old in HeartCellsOf(_team))
        {
            if (footprint.Contains(old)) continue;
            gridManager.SetTileType(old, TileType.Tunnel, _team);
            moved = true;
        }

        foreach (var cell in footprint)
            if (cell.TileType != TileType.Heart || cell.Owner != _team)
                gridManager.SetTileType(cell, TileType.Heart, _team);

        ActivateTeam(_team);
        _dirty = true;
        SetStatus(moved ? $"Moved {FactionTeams.DisplayName(_team)}'s Dungeon Heart."
                        : $"Placed {FactionTeams.DisplayName(_team)}'s Dungeon Heart.");
        return true;
    }

    /// <summary>Turns the clicked heart back into its team's tunnel.</summary>
    private bool RemoveHeartAt(int x, int y)
    {
        var cell = gridManager.GetCell(x, y);
        if (cell == null || cell.TileType != TileType.Heart)
        {
            SetStatus("Click a Dungeon Heart to remove it.", error: true);
            return false;
        }

        FactionID team = cell.Owner;
        foreach (var c in HeartCellsOf(team))
            gridManager.SetTileType(c, TileType.Tunnel, team);

        _dirty = true;
        SetStatus($"Removed {FactionTeams.DisplayName(team)}'s Dungeon Heart.");
        return true;
    }

    /// <summary>Ownership brush on a heart: the whole heart changes team, if that team has none.</summary>
    private bool TransferHeart(FactionID from)
    {
        var cells = HeartCellsOf(from);
        foreach (var c in cells) _strokeCells.Add(c);

        if (_team == FactionID.Unaligned)
        {
            SetStatus("A Dungeon Heart can't be Neutral.", error: true);
            return false;
        }
        if (HeartCellsOf(_team).Count > 0)
        {
            SetStatus($"{FactionTeams.DisplayName(_team)} already has a Dungeon Heart.", error: true);
            return false;
        }

        foreach (var c in cells) gridManager.SetOwner(c, _team);
        return true;
    }

    // ── Minions ────────────────────────────────────────────────────────

    private void SelectMinionFaction(FactionDefinition faction)
    {
        _minionFaction = faction;
        _minionDef     = null;
        if (faction == null) return;
        foreach (var def in faction.EveryMinion()) { _minionDef = def; break; }
        _minionLevel = 1;
    }

    private FactionDefinition FirstFactionDefinition()
    {
        if (factionRegistry == null) return null;
        foreach (var def in factionRegistry.Definitions)
            if (def != null) return def;
        return null;
    }

    private bool CanStandOn(GridCell cell)
    {
        if (cell == null || IsBorder(cell)) return false;
        var traversal = gridManager.Tiles.GetTraversal(cell.TileType);
        return traversal == TraversalType.Normal || traversal == TraversalType.Hazard;
    }

    private bool PlaceMinion(Vector3 world)
    {
        if (_minionFaction == null || _minionDef == null)
        {
            SetStatus("Pick a minion to place first.", error: true);
            return false;
        }
        if (!gridManager.WorldToCell(world, out int x, out int y) || !CanStandOn(gridManager.GetCell(x, y)))
        {
            SetStatus("Minions need floor to stand on — dig it out first.", error: true);
            return false;
        }

        Vector3 local = world - gridManager.transform.position;
        AddMinion(new MinionPlacement
        {
            factionId        = _team,
            contentFactionId = _minionFaction.factionContentId,
            minionId         = _minionDef.minionId,
            level            = Mathf.Clamp(_minionLevel, 1, Mathf.Max(1, _minionDef.maxLevel)),
            x                = local.x / gridManager.CellSize,
            y                = local.z / gridManager.CellSize,
        });

        ActivateTeam(_team);
        _dirty = true;
        SetStatus($"Placed {MinionName(_minionDef)} for {FactionTeams.DisplayName(_team)}.");
        return true;
    }

    private bool RemoveMinionsIn(GridCell cell)
    {
        bool changed = false;
        for (int i = _minions.Count - 1; i >= 0; i--)
        {
            if (!IsInCell(_minions[i], cell)) continue;
            RemoveMinionAt(i);
            changed = true;
        }
        _dirty |= changed;
        return changed;
    }

    private static bool IsInCell(MinionPlacement p, GridCell cell) =>
        Mathf.FloorToInt(p.x) == cell.X && Mathf.FloorToInt(p.y) == cell.Y;

    private MinionDefinition ResolveMinion(MinionPlacement p) =>
        factionRegistry != null ? factionRegistry.FindMinion(p.contentFactionId, p.minionId) : null;

    private static string MinionName(MinionDefinition def) =>
        string.IsNullOrWhiteSpace(def.displayName) ? def.minionId : def.displayName;

    private Vector3 PlacementWorld(MinionPlacement p) =>
        gridManager.transform.position +
        new Vector3(p.x * gridManager.CellSize, 0f, p.y * gridManager.CellSize);

    private void AddMinion(MinionPlacement p)
    {
        _minions.Add(p);
        _markers.Add(CreateMarker(p));
    }

    private void RemoveMinionAt(int index)
    {
        if (_markers[index].Root != null) Destroy(_markers[index].Root);
        _minions.RemoveAt(index);
        _markers.RemoveAt(index);
    }

    private void ClearMinions()
    {
        foreach (var m in _markers)
            if (m.Root != null) Destroy(m.Root);
        _minions.Clear();
        _markers.Clear();
    }

    private Marker CreateMarker(MinionPlacement p)
    {
        float cell = gridManager.CellSize;
        var root = new GameObject($"Minion_{p.contentFactionId}_{p.minionId}");
        root.transform.SetParent(_markerRoot, false);
        root.transform.position = PlacementWorld(p) + Vector3.up * 0.05f;

        var disc = new GameObject("Team").AddComponent<SpriteRenderer>();
        disc.transform.SetParent(root.transform, false);
        disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        disc.transform.localScale    = Vector3.one * 0.8f * cell;
        disc.sprite       = _discSprite;
        disc.sortingOrder = 10;

        var def = ResolveMinion(p);
        if (def != null && def.token != null)
        {
            var token = new GameObject("Token").AddComponent<SpriteRenderer>();
            token.transform.SetParent(root.transform, false);
            token.transform.localPosition = Vector3.up * 0.01f;
            token.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Vector3 size = def.token.bounds.size;
            float largest = Mathf.Max(size.x, size.y, 0.0001f);
            token.transform.localScale = Vector3.one * (0.62f * cell / largest);
            token.sprite       = def.token;
            token.sortingOrder = 11;
        }

        var marker = new Marker { Root = root, Disc = disc };
        disc.color = TeamColour(p.factionId);
        return marker;
    }

    private void RefreshMarker(int index) =>
        _markers[index].Disc.color = TeamColour(_minions[index].factionId);

    /// <summary>A soft-edged white circle, one world unit across, tinted per team.</summary>
    private static Sprite BuildDiscSprite()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode   = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name       = "LevelEditorDisc",
        };
        var pixels = new Color32[size * size];
        float c = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c; // 0 centre, 1 edge
            float alpha = Mathf.Clamp01((1f - d) * size * 0.5f);              // anti-aliased rim
            byte  shade = (byte)(d > 0.8f ? 255 : 200);                       // lighter ring
            pixels[y * size + x] = new Color32(shade, shade, shade, (byte)(alpha * 255));
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    // ── Traps and doors ────────────────────────────────────────────────

    private void SelectTrapDoorKind(TrapDoorKind kind)
    {
        _trapDoorKind = kind;
        _trapDoorDef  = null;
        if (trapDoorRegistry == null) return;
        foreach (var def in trapDoorRegistry.Definitions)
            if (def != null && def.kind == kind) { _trapDoorDef = def; break; }
    }

    private TrapDoorDefinition ResolveTrapDoor(TrapDoorPlacement p) =>
        trapDoorRegistry != null ? trapDoorRegistry.GetDefinition(p.kind, p.typeId) : null;

    /// <summary>Claimed floor: tunnel or a room, not the border, a heart or a portal.</summary>
    private bool CanHoldTrapDoor(GridCell cell, out string reason)
    {
        reason = null;
        if (cell == null || IsBorder(cell)) { reason = "That's the bedrock border."; return false; }
        if (cell.TileType == TileType.Tunnel || gridManager.Tiles.IsRoom(cell.TileType)) return true;
        reason = "Traps and doors go on claimed floor — tunnel or a room.";
        return false;
    }

    private bool PlaceTrapDoor(GridCell cell)
    {
        if (_trapDoorDef == null)
        {
            SetStatus($"No {_trapDoorKind.ToString().ToLower()}s in the Trap/Door Registry yet.", error: true);
            return false;
        }
        if (!CanHoldTrapDoor(cell, out string reason))
        {
            SetStatus(reason, error: true);
            return false;
        }

        var key = new Vector2Int(cell.X, cell.Y);
        if (_trapsDoors.TryGetValue(key, out var existing))
        {
            if (existing.Data.kind == _trapDoorDef.kind && existing.Data.typeId == _trapDoorDef.typeId)
                return false;
            RemoveTrapDoorMarker(key);
        }

        AddTrapDoor(new TrapDoorPlacement
        {
            kind      = _trapDoorDef.kind,
            typeId    = _trapDoorDef.typeId,
            factionId = cell.Owner, // inherited from the tile
            x         = cell.X,
            y         = cell.Y,
        });
        _dirty = true;
        SetStatus($"Placed {TrapDoorName(_trapDoorDef)} for {FactionTeams.DisplayName(cell.Owner)} (the tile's owner).");
        return true;
    }

    private bool RemoveTrapDoorAt(GridCell cell)
    {
        var key = new Vector2Int(cell.X, cell.Y);
        if (!_trapsDoors.ContainsKey(key)) return false;
        RemoveTrapDoorMarker(key);
        _dirty = true;
        return true;
    }

    private static string TrapDoorName(TrapDoorDefinition def) =>
        string.IsNullOrWhiteSpace(def.displayName) ? def.typeId : def.displayName;

    /// <summary>
    /// Every tile write: the trap or door on it follows the tile's new owner,
    /// or is removed if the tile is no longer claimed floor.
    /// </summary>
    private void OnTileChanged(GridCell cell)
    {
        if (_rebuildingGrid) return;
        var key = new Vector2Int(cell.X, cell.Y);
        if (!_trapsDoors.TryGetValue(key, out var marker)) return;

        if (!CanHoldTrapDoor(cell, out _))
        {
            RemoveTrapDoorMarker(key);
            SetStatus($"Removed the {marker.Data.kind.ToString().ToLower()} at ({cell.X}, {cell.Y}) — " +
                      "its tile is no longer claimed floor.");
            return;
        }
        if (marker.Data.factionId != cell.Owner)
        {
            marker.Data.factionId = cell.Owner;
            marker.Team.color     = TeamColour(cell.Owner);
        }
    }

    /// <summary>Re-applies the tile rule to every trap and door (after a load, before a save).</summary>
    private void SyncAllTrapDoors()
    {
        var keys = new List<Vector2Int>(_trapsDoors.Keys);
        foreach (var key in keys)
        {
            var cell = gridManager.GetCell(key.x, key.y);
            if (cell == null) RemoveTrapDoorMarker(key);
            else OnTileChanged(cell);
        }
    }

    /// <summary>Rebuilds the whole grid without the per-tile trap/door rule firing for every cell.</summary>
    private void ApplyWholeGrid(System.Action apply)
    {
        _rebuildingGrid = true;
        try { apply(); }
        finally { _rebuildingGrid = false; }
    }

    private void AddTrapDoor(TrapDoorPlacement p)
    {
        var key = new Vector2Int(p.x, p.y);
        if (_trapsDoors.ContainsKey(key)) RemoveTrapDoorMarker(key); // one per cell
        _trapsDoors[key] = CreateTrapDoorMarker(p);
    }

    private void RemoveTrapDoorMarker(Vector2Int key)
    {
        if (!_trapsDoors.TryGetValue(key, out var marker)) return;
        if (marker.Root != null) Destroy(marker.Root);
        _trapsDoors.Remove(key);
    }

    private void ClearTrapsDoors()
    {
        foreach (var marker in _trapsDoors.Values)
            if (marker.Root != null) Destroy(marker.Root);
        _trapsDoors.Clear();
    }

    private List<TrapDoorPlacement> CloneTrapsDoors()
    {
        var list = new List<TrapDoorPlacement>(_trapsDoors.Count);
        foreach (var marker in _trapsDoors.Values) list.Add(marker.Data.Clone());
        list.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x)); // stable file order
        return list;
    }

    /// <summary>
    /// A team-coloured plate — a diamond for a trap, a square for a door —
    /// with the definition's icon (or a block of its editor colour) on top.
    /// Drawn under minion markers.
    /// </summary>
    private TrapDoorMarker CreateTrapDoorMarker(TrapDoorPlacement p)
    {
        float cell = gridManager.CellSize;
        bool  trap = p.kind == TrapDoorKind.Trap;

        var root = new GameObject($"{p.kind}_{p.typeId}_{p.x}_{p.y}");
        root.transform.SetParent(_markerRoot, false);
        root.transform.position = gridManager.CellToWorld(p.x, p.y) + Vector3.up * 0.03f;

        var team = new GameObject("Team").AddComponent<SpriteRenderer>();
        team.transform.SetParent(root.transform, false);
        team.transform.localRotation = Quaternion.Euler(90f, trap ? 45f : 0f, 0f);
        team.transform.localScale    = Vector3.one * (trap ? 0.62f : 0.86f) * cell;
        team.sprite       = _squareSprite;
        team.sortingOrder = 5;
        team.color        = TeamColour(p.factionId);

        var def  = ResolveTrapDoor(p);
        var icon = new GameObject("Icon").AddComponent<SpriteRenderer>();
        icon.transform.SetParent(root.transform, false);
        icon.transform.localPosition = Vector3.up * 0.01f;
        icon.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        icon.sortingOrder = 6;
        if (def != null && def.icon != null)
        {
            Vector3 size = def.icon.bounds.size;
            icon.sprite = def.icon;
            icon.transform.localScale = Vector3.one * (0.5f * cell / Mathf.Max(size.x, size.y, 0.0001f));
        }
        else
        {
            icon.sprite = _squareSprite;
            icon.color  = def != null ? def.editorColour : Color.magenta; // magenta = unknown type
            icon.transform.localScale = Vector3.one * 0.38f * cell;
        }

        return new TrapDoorMarker { Data = p, Root = root, Team = team };
    }

    /// <summary>A white square with a darker rim, one world unit across.</summary>
    private static Sprite BuildSquareSprite()
    {
        const int size = 32;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode   = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name       = "LevelEditorSquare",
        };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool rim = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
            byte shade = (byte)(rim ? 120 : 235);
            pixels[y * size + x] = new Color32(shade, shade, shade, 255);
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    // ── Undo ───────────────────────────────────────────────────────────

    private Snapshot CaptureSnapshot()
    {
        var minions = new List<MinionPlacement>(_minions.Count);
        foreach (var p in _minions) minions.Add(p.Clone());
        return new Snapshot
        {
            Grid       = SaveLoadSystem.CaptureGrid(gridManager),
            Minions    = minions,
            TrapsDoors = CloneTrapsDoors(),
        };
    }

    private void RestoreSnapshot(Snapshot snapshot)
    {
        ClearHighlights();
        ApplyWholeGrid(() => SaveLoadSystem.ApplyGrid(gridManager, snapshot.Grid));
        ClearMinions();
        foreach (var p in snapshot.Minions) AddMinion(p.Clone());
        ClearTrapsDoors();
        foreach (var t in snapshot.TrapsDoors) AddTrapDoor(t.Clone());
        _dirty = true;
    }

    private void PushUndo(Snapshot before)
    {
        _undo.AddLast(before);
        while (_undo.Count > maxUndoSteps) _undo.RemoveFirst();
        _redo.Clear();
    }

    private void Undo()
    {
        if (_stroking || _undo.Count == 0) return;
        _redo.Push(CaptureSnapshot());
        var snapshot = _undo.Last.Value;
        _undo.RemoveLast();
        RestoreSnapshot(snapshot);
        SetStatus("Undone.");
    }

    private void Redo()
    {
        if (_stroking || _redo.Count == 0) return;
        _undo.AddLast(CaptureSnapshot());
        RestoreSnapshot(_redo.Pop());
        SetStatus("Redone.");
    }

    // ── Keyboard ───────────────────────────────────────────────────────

    private void HandleShortcuts()
    {
        bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) ||
                     Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        if (ctrl)
        {
            if (Input.GetKeyDown(KeyCode.Z)) { if (shift) Redo(); else Undo(); }
            if (Input.GetKeyDown(KeyCode.Y)) Redo();
            if (Input.GetKeyDown(KeyCode.S)) Save(toBuiltIn: false);
            return;
        }

        if (Input.GetKeyDown(KeyCode.Alpha1)) _tool = Tool.Tiles;
        if (Input.GetKeyDown(KeyCode.Alpha2)) _tool = Tool.Heart;
        if (Input.GetKeyDown(KeyCode.Alpha3)) _tool = Tool.Minions;
        if (Input.GetKeyDown(KeyCode.Alpha4)) _tool = Tool.TrapsDoors;
        if (Input.GetKeyDown(KeyCode.Alpha5)) _tool = Tool.Ownership;
        if (Input.GetKeyDown(KeyCode.R))      _removeMode = !_removeMode;
        if (Input.GetKeyDown(KeyCode.LeftBracket))  _brushSize = Mathf.Max(1, _brushSize - 2);
        if (Input.GetKeyDown(KeyCode.RightBracket)) _brushSize = Mathf.Min(MaxBrush, _brushSize + 2);
        if (Input.GetKeyDown(KeyCode.F) && _editing) editorCamera.Frame(gridManager);
    }

    private const int MaxBrush = 15;

    // ── Save ───────────────────────────────────────────────────────────

    private LevelData BuildLevelData()
    {
        _level.grid    = SaveLoadSystem.CaptureGrid(gridManager);
        _level.factions = new List<FactionSetup>();
        foreach (var id in FactionTeams.All)
        {
            if (id == FactionID.Unaligned || !_activeTeams.Contains(id)) continue;
            var setup = _teams[id];
            setup.factionId = id;
            setup.colourHex = "#" + ColorUtility.ToHtmlStringRGB(TeamColour(id));
            _level.factions.Add(setup);
        }

        _level.minions = new List<MinionPlacement>(_minions.Count);
        foreach (var p in _minions) _level.minions.Add(p.Clone());
        SyncAllTrapDoors();
        _level.trapsAndDoors = CloneTrapsDoors();
        _level.allowedMinionIds ??= new List<string>();
        return _level;
    }

    /// <summary>Things that will load but probably aren't what the designer meant.</summary>
    private List<string> Validate()
    {
        var warnings = new List<string>();

        var owned       = new HashSet<FactionID>();
        var heartTiles  = new Dictionary<FactionID, int>();
        var portalTiles = new Dictionary<FactionID, int>();
        for (int x = 0; x < gridManager.Width;  x++)
        for (int y = 0; y < gridManager.Height; y++)
        {
            var c = gridManager.GetCell(x, y);
            if (c.Owner != FactionID.Unaligned) owned.Add(c.Owner);
            if (c.TileType == TileType.Heart)  heartTiles[c.Owner]  = heartTiles.GetValueOrDefault(c.Owner) + 1;
            if (c.TileType == TileType.Portal) portalTiles[c.Owner] = portalTiles.GetValueOrDefault(c.Owner) + 1;
        }
        foreach (var p in _minions)
            if (p.factionId != FactionID.Unaligned) owned.Add(p.factionId);

        if (_activeTeams.Count == 0)
            warnings.Add("No team is active — the level has no players.");

        bool anyHuman = false;
        foreach (var id in FactionTeams.Playable)
        {
            if (!_activeTeams.Contains(id)) continue;
            anyHuman |= _teams[id].isHuman;
            string name = FactionTeams.DisplayName(id);

            if (!heartTiles.ContainsKey(id))
                warnings.Add($"{name} has no Dungeon Heart.");
            int portals = portalTiles.GetValueOrDefault(id);
            if (portals == 0)
                warnings.Add($"{name} has no Portal, so it can't summon minions.");
            else if (portals > 1)
                warnings.Add($"{name} has {portals} Portals — only one is used to summon.");
            if (string.IsNullOrEmpty(_teams[id].contentFactionId))
                warnings.Add($"{name} has no content faction (roster) assigned.");
        }
        if (_activeTeams.Count > 0 && !anyHuman)
            warnings.Add("No active team is human-controlled.");

        foreach (var id in owned)
            if (!_activeTeams.Contains(id))
                warnings.Add($"{FactionTeams.DisplayName(id)} owns tiles or minions but isn't active " +
                             "(turn it on in Teams), so it won't play.");

        int stranded = 0, unknown = 0;
        foreach (var p in _minions)
        {
            if (ResolveMinion(p) == null) unknown++;
            if (!gridManager.WorldToCell(PlacementWorld(p), out int mx, out int my) ||
                !CanStandOn(gridManager.GetCell(mx, my))) stranded++;
        }
        int unknownTraps = 0;
        foreach (var t in _trapsDoors.Values)
            if (ResolveTrapDoor(t.Data) == null) unknownTraps++;
        if (unknownTraps > 0)
            warnings.Add($"{unknownTraps} trap(s)/door(s) aren't in the Trap/Door Registry.");

        if (stranded > 0) warnings.Add($"{stranded} minion(s) are standing inside solid rock or liquid.");
        if (unknown  > 0) warnings.Add($"{unknown} minion(s) aren't in the Faction Registry and won't spawn.");

        return warnings;
    }

    private static bool IsValidLevelId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Writes the level to CustomLevels, or (Editor only) to
    /// StreamingAssets/Levels to ship it with the game. True on success.
    /// </summary>
    private bool Save(bool toBuiltIn)
    {
        if (!_editing) return false;
        if (!IsValidLevelId(_level.levelId))
        {
            SetStatus("The level id can't be empty or contain characters a file name can't.", error: true);
            _modal = Modal.LevelSettings;
            return false;
        }

        var data   = BuildLevelData();
        string dir = toBuiltIn ? SaveLoadSystem.BuiltInLevelsRoot : SaveLoadSystem.CustomLevelsRoot;
        if (!SaveLoadSystem.SaveLevelTo(data, dir))
        {
            SetStatus("Save failed — see the console.", error: true);
            return false;
        }

        _dirty = false;
        _warnings = Validate();
        SetStatus($"Saved '{data.levelId}' to {Path.Combine(dir, data.levelId + ".json")}" +
                  (_warnings.Count > 0 ? $" — {_warnings.Count} warning(s)." : "."));
        if (_warnings.Count > 0) _modal = Modal.Warnings;
        return true;
    }

    /// <summary>Saves, then opens the Gameplay scene on this level as a new game.</summary>
    private void PlayTest()
    {
        if (!Application.CanStreamedLevelBeLoaded(gameplaySceneName))
        {
            SetStatus($"Scene '{gameplaySceneName}' isn't in Build Settings.", error: true);
            return;
        }
        if (!Save(toBuiltIn: false)) return;

        GameplaySceneArgs.Clear();
        GameplaySceneArgs.LevelId   = _level.levelId;
        GameplaySceneArgs.IsNewGame = true;
        SceneManager.LoadScene(gameplaySceneName);
    }

    // ── Status line ────────────────────────────────────────────────────

    private void SetStatus(string message, bool error = false)
    {
        _status        = message;
        _statusIsError = error;
        _statusTime    = Time.unscaledTime;
        if (error) Debug.Log($"[LevelEditor] {message}");
    }
}
