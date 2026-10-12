using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Fog of war. For each vision group (a faction and, later, its allies) it
/// knows which tiles are in sight right now, and remembers every other tile
/// as it was when last seen.
///
/// What a group sees
/// -----------------
///   • Every tile it owns. Its owned floor (anything but walls) also shows
///     the 8 tiles around it — so it sees its own walls, but not past them
///     until it digs there. Walls reveal nothing beyond themselves.
///   • What its minions see: within combat's sight range, along lines that
///     rock and walls block (CombatSystem.CanSee). A minion in the hand sees
///     nothing; one turned into a chicken sees as normal.
///   • Sight of Evil circles (RevealCircle), walls and all, while they last.
///
/// What it remembers
/// -----------------
/// The first snapshot holds what everyone knows from the start — bedrock,
/// portals (in their owner's colour), gold and gems — plus the group's own
/// territory. Everything else looks like plain stone until seen. Once seen,
/// a tile is remembered as it was when it went out of sight.
///
/// Drawing
/// -------
/// It's the grid's Appearance (GridManager2D): the local player's tiles are
/// drawn as remembered, darkened (exploredBrightness) when not in sight.
/// Bedrock is never fogged. Enemy minions, loose chickens and gold piles out
/// of sight are hidden (Renderer.forceRenderingOff).
///
/// Who's tracked
/// -------------
/// Only human players for now (SetTrackedFactions) — anyone else sees
/// everything: IsVisible is true and KnownLook is the truth. Track a faction
/// and the same checks give an AI its fair view.
///
/// Debug: revealKey (F9, editor and development builds) shows the whole map.
///
/// Scene setup: none — GameManager2D adds one. The level editor has none.
/// </summary>
public class FogOfWar : MonoBehaviour, ICellAppearance
{
    public static FogOfWar Instance { get; private set; }

    [SerializeField] private FactionID localPlayer = FactionID.Player;
    [Tooltip("Seconds between sight updates.")]
    [SerializeField, Min(0.05f)] private float updateInterval = 0.2f;
    [Tooltip("Brightness of tiles out of sight (1 = no darkening).")]
    [SerializeField, Range(0f, 1f)] private float exploredBrightness = 0.5f;
    [Tooltip("Shows the whole map while on. Editor and development builds only.")]
    [SerializeField] private KeyCode revealKey = KeyCode.F9;

    /// <summary>Fired when a cell's remembered look changes, or it comes into or goes out of sight, for any group.</summary>
    public event Action<FactionID, GridCell> OnKnowledgeChanged;

    private class Knowledge
    {
        public FactionID Group;
        public bool[]     Visible;
        public bool[]     Next;
        public TileType[] SeenType;
        public FactionID[] SeenOwner;
    }

    private struct Reveal
    {
        public FactionID Group;
        public Vector3   Centre;
        public float     Radius;
        public float     Until;
    }

    private readonly Dictionary<FactionID, Knowledge> _groups = new();
    private readonly List<FactionID> _tracked = new();
    private readonly List<Reveal> _reveals = new();
    private GridManager2D _grid;
    private float _nextUpdate;
    private bool  _revealAll;

    // ── Lifecycle ──────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (_grid != null)
        {
            _grid.OnGridBuilt   += OnGridBuilt;
            _grid.OnTileChanged += OnTileChanged;
            _grid.Appearance     = this;
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_grid != null)
        {
            _grid.OnGridBuilt   -= OnGridBuilt;
            _grid.OnTileChanged -= OnTileChanged;
            if (_grid.Appearance == (ICellAppearance)this) _grid.Appearance = null;
        }
    }

    private int Width  => _grid.Width;
    private int Height => _grid.Height;
    private int Index(GridCell c) => c.Y * _grid.Width + c.X;

    // ── Who's tracked ──────────────────────────────────────────────────

    /// <summary>The group whose sight a faction shares. Just itself until there are alliances.</summary>
    public static FactionID VisionGroupOf(FactionID faction) => faction;

    private static bool InGroup(FactionID faction, FactionID group) =>
        faction != FactionID.Unaligned && VisionGroupOf(faction) == group;

    /// <summary>The factions with fog — human players for now. Everyone else sees everything.</summary>
    public void SetTrackedFactions(IEnumerable<FactionID> factions)
    {
        _tracked.Clear();
        foreach (var f in factions) if (!_tracked.Contains(f)) _tracked.Add(f);
        _reveals.Clear();
        BuildKnowledge();
    }

    private void OnGridBuilt() => BuildKnowledge();

    /// <summary>First snapshots for every tracked group, then a sight update.</summary>
    private void BuildKnowledge()
    {
        _groups.Clear();
        if (_grid == null || Width <= 0 || Height <= 0) return;

        foreach (var faction in _tracked)
        {
            var group = VisionGroupOf(faction);
            if (_groups.ContainsKey(group)) continue;

            int n = Width * Height;
            var k = new Knowledge
            {
                Group = group, Visible = new bool[n], Next = new bool[n],
                SeenType = new TileType[n], SeenOwner = new FactionID[n],
            };
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                var cell = _grid.GetCell(x, y);
                int i = y * Width + x;
                if (KnownFromStart(cell, group)) { k.SeenType[i] = cell.TileType; k.SeenOwner[i] = cell.Owner; }
                else                              { k.SeenType[i] = TileType.Stone; k.SeenOwner[i] = FactionID.Unaligned; }
            }
            _groups[group] = k;
        }

        UpdateSight();
        _grid.RefreshAllLooks();
    }

    /// <summary>Known to everyone from the start: bedrock, portals, gold and gems — and your own territory.</summary>
    private static bool KnownFromStart(GridCell cell, FactionID group) =>
        cell.TileType == TileType.Bedrock || cell.TileType == TileType.Portal ||
        cell.TileType == TileType.Gold    || cell.TileType == TileType.Gem    ||
        InGroup(cell.Owner, group);

    // ── Queries (for gameplay and, later, AI) ──────────────────────────

    /// <summary>Whether this faction can see the tile right now. Always true for factions without fog.</summary>
    public bool IsVisible(FactionID faction, GridCell cell)
    {
        if (cell == null) return false;
        var group = VisionGroupOf(faction);
        if (!_groups.TryGetValue(group, out var k)) return true;
        if (_revealAll && InGroup(localPlayer, group)) return true;
        return InGroup(cell.Owner, group) || k.Visible[Index(cell)];
    }

    /// <summary>Whether this faction can see the world point right now.</summary>
    public bool IsVisible(FactionID faction, Vector3 world) =>
        _grid != null && _grid.WorldToCell(world, out int x, out int y) && IsVisible(faction, _grid.GetCell(x, y));

    /// <summary>What this faction knows of the tile: the truth if in sight, else what it last saw.</summary>
    public CellLook KnownLook(FactionID faction, GridCell cell)
    {
        var group = VisionGroupOf(faction);
        if (!_groups.TryGetValue(group, out var k) || IsVisible(faction, cell))
            return new CellLook(cell.TileType, cell.Owner, 1f);

        int i = Index(cell);
        float brightness = cell.TileType == TileType.Bedrock ? 1f : exploredBrightness;
        return new CellLook(k.SeenType[i], k.SeenOwner[i], brightness);
    }

    /// <summary>How the local player sees the tile (the grid's Appearance).</summary>
    public CellLook LookOf(GridCell cell) => KnownLook(localPlayer, cell);

    /// <summary>Sight of Evil: everything within radius (world units) of the centre is in sight for a while, walls and all.</summary>
    public void RevealCircle(FactionID faction, Vector3 centre, float radius, float seconds)
    {
        if (seconds <= 0f || radius <= 0f) return;
        _reveals.Add(new Reveal { Group = VisionGroupOf(faction), Centre = centre, Radius = radius, Until = Time.time + seconds });
        _nextUpdate = 0f;
    }

    // ── Updating sight ─────────────────────────────────────────────────

    private void Update()
    {
        if ((Application.isEditor || Debug.isDebugBuild) && Input.GetKeyDown(revealKey))
        {
            _revealAll = !_revealAll;
            Debug.Log($"[FogOfWar] Reveal all {(_revealAll ? "on" : "off")}.");
            _grid?.RefreshAllLooks();
        }

        if (Time.time < _nextUpdate) return;
        _nextUpdate = Time.time + updateInterval;
        UpdateSight();
    }

    private void UpdateSight()
    {
        if (_grid == null || _groups.Count == 0) return;
        _reveals.RemoveAll(r => Time.time >= r.Until);

        foreach (var k in _groups.Values)
        {
            Array.Clear(k.Next, 0, k.Next.Length);
            SeeTerritory(k);
            SeeWithMinions(k);
            SeeReveals(k);
            Commit(k);
        }
        HideUnseenThings();
    }

    /// <summary>Owned tiles, and the ring of tiles round each owned floor tile (not walls).</summary>
    private void SeeTerritory(Knowledge k)
    {
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            var cell = _grid.GetCell(x, y);
            if (!InGroup(cell.Owner, k.Group)) continue;
            // A wall is seen (it's owned) but shows nothing past itself.
            if (cell.TileType == TileType.Wall) { k.Next[y * Width + x] = true; continue; }
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < Width && ny < Height) k.Next[ny * Width + nx] = true;
            }
        }
    }

    /// <summary>Everything each of the group's minions can see, as combat sees it.</summary>
    private void SeeWithMinions(Knowledge k)
    {
        var combat = CombatSystem.Instance;
        if (combat == null) return;
        int range = Mathf.CeilToInt(combat.SightRange);
        float rangeSq = combat.SightRange * combat.SightRange;

        foreach (var m in MinionController.All)
        {
            if (m == null || !m.IsAlive || m.IsHeld || !InGroup(m.Faction, k.Group)) continue;
            var from = m.Agent != null ? m.Agent.CurrentCell : null;
            if (from == null) continue;

            for (int dy = -range; dy <= range; dy++)
            for (int dx = -range; dx <= range; dx++)
            {
                if (dx * dx + dy * dy > rangeSq) continue;
                var to = _grid.GetCell(from.X + dx, from.Y + dy);
                if (to == null) continue;
                int i = Index(to);
                if (k.Next[i]) continue;
                if (combat.CanSee(from, to)) k.Next[i] = true;
            }
        }
    }

    private void SeeReveals(Knowledge k)
    {
        foreach (var r in _reveals)
        {
            if (r.Group != k.Group) continue;
            if (!_grid.WorldToCell(r.Centre, out int cx, out int cy)) continue;
            int reach = Mathf.CeilToInt(r.Radius / _grid.CellSize);
            for (int dy = -reach; dy <= reach; dy++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                var cell = _grid.GetCell(cx + dx, cy + dy);
                if (cell == null) continue;
                Vector3 d = _grid.CellToWorld(cell.X, cell.Y) - r.Centre;
                if (d.x * d.x + d.z * d.z <= r.Radius * r.Radius) k.Next[Index(cell)] = true;
            }
        }
    }

    /// <summary>Swaps in the new sight, updating memory of what's seen and redrawing what changed.</summary>
    private void Commit(Knowledge k)
    {
        bool local = InGroup(localPlayer, k.Group);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int  i    = y * Width + x;
            bool now  = k.Next[i];
            bool was  = k.Visible[i];
            bool seen = false;

            if (now)
            {
                var cell = _grid.GetCell(x, y);
                if (k.SeenType[i] != cell.TileType || k.SeenOwner[i] != cell.Owner)
                {
                    k.SeenType[i]  = cell.TileType;
                    k.SeenOwner[i] = cell.Owner;
                    seen = true;
                }
            }

            k.Visible[i] = now;
            if (now == was && !seen) continue;

            var changed = _grid.GetCell(x, y);
            OnKnowledgeChanged?.Invoke(k.Group, changed);
            if (local) _grid.RefreshLook(changed);
        }
    }

    /// <summary>A tile changed while in sight: remember it as it is now.</summary>
    private void OnTileChanged(GridCell cell)
    {
        foreach (var k in _groups.Values)
        {
            int i = Index(cell);
            if (!k.Visible[i] && !InGroup(cell.Owner, k.Group)) continue;
            if (k.SeenType[i] == cell.TileType && k.SeenOwner[i] == cell.Owner) continue;
            k.SeenType[i]  = cell.TileType;
            k.SeenOwner[i] = cell.Owner;
            OnKnowledgeChanged?.Invoke(k.Group, cell);
        }
    }

    // ── Hiding things out of sight ─────────────────────────────────────

    /// <summary>The local player's view: enemy minions, loose chickens and gold piles only show when in sight.</summary>
    private void HideUnseenThings()
    {
        foreach (var m in MinionController.All)
            if (m != null) SetShown(m, InGroup(m.Faction, VisionGroupOf(localPlayer)) || IsVisible(localPlayer, m.transform.position));

        var hatchery = HatcheryManager.Instance;
        if (hatchery != null)
            foreach (var c in hatchery.Chickens)
                if (c != null) SetShown(c, c.IsHeld || IsVisible(localPlayer, c.transform.position));

        var treasury = TreasuryManager.Instance;
        if (treasury != null)
            foreach (var p in treasury.Piles)
                if (p != null) SetShown(p, p.IsHeld || IsVisible(localPlayer, p.transform.position));
    }

    private readonly List<Renderer> _renderers = new();

    private void SetShown(Component thing, bool shown)
    {
        thing.GetComponentsInChildren(true, _renderers);
        foreach (var r in _renderers)
            if (r.forceRenderingOff == shown) r.forceRenderingOff = !shown;
    }

    /// <summary>True if the local player can see this minion right now (own and allied minions always).</summary>
    public bool LocalCanSee(MinionController minion) =>
        minion != null && (InGroup(minion.Faction, VisionGroupOf(localPlayer)) || IsVisible(localPlayer, minion.transform.position));

    // ── Save / load ────────────────────────────────────────────────────

    public List<FogSaveData> Capture()
    {
        var list = new List<FogSaveData>();
        foreach (var k in _groups.Values)
        {
            int n = k.SeenType.Length;
            var types  = new byte[n];
            var owners = new byte[n];
            for (int i = 0; i < n; i++) { types[i] = (byte)k.SeenType[i]; owners[i] = (byte)k.SeenOwner[i]; }

            var data = new FogSaveData
            {
                group = k.Group, width = Width, height = Height,
                types = Convert.ToBase64String(types), owners = Convert.ToBase64String(owners),
                reveals = new List<FogRevealSaveData>(),
            };
            foreach (var r in _reveals)
                if (r.Group == k.Group)
                    data.reveals.Add(new FogRevealSaveData
                    {
                        x = r.Centre.x, y = r.Centre.y, z = r.Centre.z, radius = r.Radius,
                        secondsLeft = Mathf.Max(0f, r.Until - Time.time),
                    });
            list.Add(data);
        }
        return list;
    }

    /// <summary>Puts back each group's memory. Groups missing from the save keep their first snapshot.</summary>
    public void Restore(List<FogSaveData> saved)
    {
        if (saved == null || _grid == null) return;
        foreach (var s in saved)
        {
            if (!_groups.TryGetValue(s.group, out var k)) continue;
            if (s.width != Width || s.height != Height) continue;
            byte[] types, owners;
            try { types = Convert.FromBase64String(s.types); owners = Convert.FromBase64String(s.owners); }
            catch (FormatException) { continue; }
            if (types.Length != k.SeenType.Length || owners.Length != k.SeenOwner.Length) continue;

            for (int i = 0; i < types.Length; i++) { k.SeenType[i] = (TileType)types[i]; k.SeenOwner[i] = (FactionID)owners[i]; }
            if (s.reveals != null)
                foreach (var r in s.reveals)
                    _reveals.Add(new Reveal { Group = s.group, Centre = new Vector3(r.x, r.y, r.z), Radius = r.radius, Until = Time.time + r.secondsLeft });
        }
        UpdateSight();
        _grid.RefreshAllLooks();
    }
}

[Serializable]
public class FogSaveData
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID group;
    public int width, height;
    /// <summary>Remembered tile types, one byte per cell (row by row), base64.</summary>
    public string types;
    /// <summary>Remembered owners, one byte per cell, base64.</summary>
    public string owners;
    public List<FogRevealSaveData> reveals;
}

[Serializable]
public class FogRevealSaveData
{
    public float x, y, z, radius, secondsLeft;
}
