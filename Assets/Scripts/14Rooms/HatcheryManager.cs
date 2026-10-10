using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Hatcheries and their chickens — the dungeon's food.
///
/// Spawning
/// --------
/// Each Hatchery room hatches chickens on its own tiles until it holds its
/// capacity (the room's Capacity — capacityPerTile × tiles × efficiency from
/// RoomData — and never less than one). How often scales the same way:
/// chickensPerTilePerMinute × tiles × efficiency, so a bigger, better-built
/// Hatchery both holds more and restocks faster.
///
/// Chickens (Chicken) wander their Hatchery and stay in it. One dropped
/// elsewhere roams the dungeon freely until it wanders into a Hatchery,
/// where it's bound again. Chickens are decoration that happens to be
/// edible: hungry minions hunt them (FoodBehaviour), passing minions snack
/// on them, the Keeper's hand can carry them, drop one on a minion to
/// force-feed it, or slap one dead.
///
/// The eating numbers (hunger and health per chicken, eating time) live
/// here so every minion eats alike.
///
/// Scene setup: none — GameManager2D adds one. Add it yourself to set a
/// chicken prefab or tune the numbers.
/// </summary>
public class HatcheryManager : MonoBehaviour
{
    public static HatcheryManager Instance { get; private set; }

    [Header("Spawning")]
    [Tooltip("Chickens hatched per minute, per Hatchery tile, at efficiency 1 — while below capacity.")]
    [SerializeField, Min(0f)] private float chickensPerTilePerMinute = 0.1f;
    [Tooltip("Seconds between spawn checks.")]
    [SerializeField, Min(0.1f)] private float spawnCheckInterval = 1f;

    [Header("Chickens")]
    [Tooltip("Chicken prefab (e.g. a sprite). Empty = a small white placeholder.")]
    [SerializeField] private GameObject chickenPrefab;
    [Tooltip("Walking speed, in cells per second.")]
    [SerializeField, Min(0.05f)] private float moveSpeed = 0.5f;
    [Tooltip("Seconds a chicken idles between steps (random between these).")]
    [SerializeField] private Vector2 idleSeconds = new(1f, 4f);
    [Tooltip("Placeholder size, as a fraction of a cell.")]
    [SerializeField, Range(0.05f, 0.5f)] private float placeholderSize = 0.18f;
    [SerializeField] private Color placeholderColour = Color.white;

    [Header("Eating")]
    [Tooltip("Hunger (0-1) one chicken takes away.")]
    [SerializeField, Range(0f, 1f)] private float hungerPerChicken = 0.5f;
    [Tooltip("Health one chicken restores, as a fraction of max health (not while exhausted).")]
    [SerializeField, Range(0f, 1f)] private float healPerChicken = 0.15f;
    [Tooltip("Seconds a minion spends eating a chicken it caught.")]
    [SerializeField, Min(0f)] private float eatSeconds = 1.5f;
    [Tooltip("Seconds a minion stands still being force-fed by the Keeper's hand.")]
    [SerializeField, Min(0f)] private float forceFeedSeconds = 2.5f;

    [Header("Rooms")]
    [SerializeField] private TileType hatcheryType = TileType.Hatchery;

    private readonly List<Chicken> _chickens = new();
    private readonly Dictionary<GridCell, List<Chicken>> _byCell = new();
    private System.Random _rng;
    private float _nextCheck;

    public float HungerPerChicken => hungerPerChicken;
    public float HealPerChicken   => healPerChicken;
    public float EatSeconds       => eatSeconds;
    public float ForceFeedSeconds => forceFeedSeconds;
    public float MoveSpeed        => moveSpeed;
    public IReadOnlyList<Chicken> Chickens => _chickens;

    private GridManager2D Grid => GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (Time.time < _nextCheck) return;
        _nextCheck = Time.time + spawnCheckInterval;
        SpawnTick();
    }

    // ── Rules ──────────────────────────────────────────────────────────

    public bool IsHatchery(GridCell cell) => cell != null && cell.TileType == hatcheryType;

    /// <summary>True if the faction owns any Hatchery tiles.</summary>
    public bool HasHatchery(FactionID faction)
    {
        var grid = Grid;
        if (grid == null) return false;
        foreach (var room in grid.Rooms)
            if (room.TileType == hatcheryType && room.Owner == faction) return true;
        return false;
    }

    /// <summary>
    /// Whether a chicken on 'from' may step to 'to': within a Hatchery it
    /// stays in that Hatchery; anywhere else it may walk any open floor.
    /// </summary>
    public bool CanWander(GridCell from, GridCell to)
    {
        if (to == null) return false;
        if (IsHatchery(from)) return IsHatchery(to) && to.Owner == from.Owner;
        if (to.TileType == TileType.Portal || to.TileType == TileType.Heart) return false;
        var def = Grid != null ? Grid.GetDefinition(to.TileType) : null;
        return def != null && def.traversalType == TraversalType.Normal;
    }

    /// <summary>Random number in [0, 1) from the match's seeded stream.</summary>
    public float Random01()
    {
        _rng ??= new System.Random(GameManager2D.Instance != null
            ? GameManager2D.Instance.DeriveFactionSeed(FactionID.Unaligned, "hatchery")
            : 0);
        return (float)_rng.NextDouble();
    }

    public float RandomIdle() => Mathf.Lerp(idleSeconds.x, idleSeconds.y, Random01());

    // ── Spawning ───────────────────────────────────────────────────────

    private void SpawnTick()
    {
        var grid = Grid;
        if (grid == null) return;

        foreach (var room in grid.Rooms)
        {
            if (room.TileType != hatcheryType || room.Cells.Count == 0) continue;

            int capacity = Mathf.Max(1, room.Capacity);
            int count = 0;
            foreach (var c in room.Cells) count += CountOn(c);
            if (count >= capacity) continue;

            float chance = chickensPerTilePerMinute * room.Cells.Count * room.Efficiency * spawnCheckInterval / 60f;
            if (Random01() >= chance) continue;

            var cell = room.Cells[Mathf.Min(room.Cells.Count - 1, (int)(Random01() * room.Cells.Count))];
            Spawn(cell);
        }
    }

    private void Spawn(GridCell cell) =>
        SpawnAt(cell, Grid.CellToWorld(cell.X, cell.Y) + Jitter(Grid.CellSize));

    private void SpawnAt(GridCell cell, Vector3 at)
    {
        var grid = Grid;
        var go   = new GameObject("Chicken");
        go.transform.SetParent(transform, false);

        float size = placeholderSize * grid.CellSize;
        if (chickenPrefab != null)
        {
            Instantiate(chickenPrefab, go.transform);
        }
        else
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localScale    = Vector3.one * size;
            body.transform.localPosition = new Vector3(0f, size * 0.5f, 0f);
            var r = body.GetComponent<Renderer>();
            if (r != null) r.material.color = placeholderColour;
        }

        var chicken = go.AddComponent<Chicken>();
        chicken.Initialise(this, grid, cell, at, size * 0.6f);
        _chickens.Add(chicken);
        AddToCell(chicken, cell);
    }

    /// <summary>A random point offset within a cell, kept off its edges.</summary>
    public Vector3 Jitter(float cellSize) =>
        new((Random01() - 0.5f) * 0.6f * cellSize, 0f, (Random01() - 0.5f) * 0.6f * cellSize);

    // ── Finding chickens ───────────────────────────────────────────────

    private int CountOn(GridCell cell) =>
        _byCell.TryGetValue(cell, out var list) ? list.Count : 0;

    /// <summary>A chicken on this cell that nobody's after and the hand isn't holding, or null.</summary>
    public Chicken FreeChickenOn(GridCell cell)
    {
        if (cell == null || !_byCell.TryGetValue(cell, out var list)) return null;
        foreach (var c in list) if (c != null && c.IsFree) return c;
        return null;
    }

    public bool HasFreeChicken(GridCell cell) => FreeChickenOn(cell) != null;

    /// <summary>The nearest free chicken within radius (world units) of a point, or null.</summary>
    public Chicken FindFreeChickenNear(Vector3 point, float radius)
    {
        Chicken best = null;
        float bestSq = radius * radius;
        foreach (var c in _chickens)
        {
            if (c == null || !c.IsFree) continue;
            Vector3 d = c.transform.position - point;
            float sq = d.x * d.x + d.z * d.z;
            if (sq > bestSq) continue;
            best = c; bestSq = sq;
        }
        return best;
    }

    // ── Bookkeeping (called by Chicken) ────────────────────────────────

    internal void Moved(Chicken chicken, GridCell from, GridCell to)
    {
        RemoveFromCell(chicken, from);
        AddToCell(chicken, to);
    }

    internal void Detach(Chicken chicken, GridCell from) => RemoveFromCell(chicken, from);

    internal void Attach(Chicken chicken, GridCell to) => AddToCell(chicken, to);

    /// <summary>Eaten, slapped or otherwise gone.</summary>
    internal void Remove(Chicken chicken)
    {
        RemoveFromCell(chicken, chicken.Cell);
        _chickens.Remove(chicken);
        if (chicken != null) Destroy(chicken.gameObject);
    }

    private void AddToCell(Chicken chicken, GridCell cell)
    {
        if (cell == null) return;
        if (!_byCell.TryGetValue(cell, out var list)) _byCell[cell] = list = new List<Chicken>();
        if (!list.Contains(chicken)) list.Add(chicken);
    }

    private void RemoveFromCell(Chicken chicken, GridCell cell)
    {
        if (cell == null || !_byCell.TryGetValue(cell, out var list)) return;
        list.Remove(chicken);
        if (list.Count == 0) _byCell.Remove(cell);
    }

    // ── Save / load ────────────────────────────────────────────────────

    /// <summary>Every chicken's position; one in the hand is saved where it was picked up.</summary>
    public List<ChickenSaveData> Capture()
    {
        var list = new List<ChickenSaveData>();
        var grid = Grid;
        foreach (var c in _chickens)
        {
            if (c == null || !c.IsAlive) continue;
            Vector3 at = c.transform.position;
            if (c.IsHeld && c.LastCell != null && grid != null) at = grid.CellToWorld(c.LastCell.X, c.LastCell.Y);
            list.Add(new ChickenSaveData { x = at.x, z = at.z });
        }
        return list;
    }

    public void Restore(List<ChickenSaveData> saved)
    {
        var grid = Grid;
        if (saved == null || grid == null) return;
        foreach (var c in saved)
        {
            var at = new Vector3(c.x, grid.transform.position.y, c.z);
            if (!grid.WorldToCell(at, out int x, out int y)) continue;
            var cell = grid.GetCell(x, y);
            if (cell != null) SpawnAt(cell, at);
        }
    }

    /// <summary>Removes every chicken — called when a new grid is loaded.</summary>
    public void ResetState()
    {
        foreach (var c in _chickens) if (c != null) Destroy(c.gameObject);
        _chickens.Clear();
        _byCell.Clear();
        _rng = null;
    }
}
