using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where every faction's gold physically is.
///
/// Gold lives in three places:
///   reserve      a faction's starting gold, held by its FactionWallet. Spent
///                before anything else and never refilled.
///   Treasury     banked on individual Treasury tiles (GridCell.StoredGold).
///                Each tile holds up to capacityPerTile, × its room's
///                efficiency when the Treasury's capacity scales with it.
///   gold piles   loose gold on the floor (GoldPile): mined with nowhere to
///                bank it, or spilled from a sold Treasury tile. Workers haul
///                piles in their own territory back to a Treasury, and the
///                Keeper's hand can pick them up and drop them on one.
///
/// A faction's gold (FactionWallet.Gold) is its reserve plus everything on
/// the Treasury tiles it owns — piles don't count until they're banked.
/// Because the gold is stored on the tile, a captured Treasury tile's gold
/// changes hands with it.
///
/// A Treasury tile that stops being one (sold, destroyed) spills its gold as
/// a pile on the spot; a Treasury built under a pile banks it automatically.
///
/// Scene setup: none — GameManager2D adds one if the scene has none. Add it
/// yourself to set a gold-pile prefab or tune the placeholder.
/// </summary>
public class TreasuryManager : MonoBehaviour
{
    public static TreasuryManager Instance { get; private set; }

    /// <summary>Raised whenever banked gold moves, so wallets can refresh.</summary>
    public static event Action OnGoldMoved;

    [Header("Gold piles")]
    [Tooltip("Prefab for a pile of gold on the floor (e.g. a pixel-art sprite), " +
             "authored at full size. Empty = a placeholder gold disc.")]
    [SerializeField] private GameObject pilePrefab;
    [Tooltip("Gold at which a pile is drawn at full size.")]
    [SerializeField, Min(1)] private int fullPileAmount = 300;
    [Tooltip("Pile radius, as a fraction of a cell, at 1 gold and at full size.")]
    [SerializeField, Range(0.05f, 0.5f)] private float minPileRadius = 0.12f;
    [SerializeField, Range(0.05f, 0.5f)] private float maxPileRadius = 0.32f;
    [SerializeField] private Color placeholderColour = new(1f, 0.8f, 0.15f, 1f);

    [Header("Rooms")]
    [SerializeField] private TileType treasuryType = TileType.Treasury;

    [Header("Dependencies")]
    [Tooltip("Leave empty to use GameManager2D's grid.")]
    [SerializeField] private GridManager2D gridManager;

    private readonly Dictionary<GridCell, GoldPile> _piles  = new();
    private readonly Dictionary<FactionID, int>     _stored = new();
    private readonly Dictionary<FactionID, int>     _free   = new();
    private bool _countsDirty = true;
    private bool _subscribed;

    public TileType TreasuryType => treasuryType;
    public IReadOnlyCollection<GoldPile> Piles => _piles.Values;

    private GridManager2D Grid
    {
        get
        {
            if (gridManager == null && GameManager2D.Instance != null) gridManager = GameManager2D.Instance.Grid;
            if (gridManager != null && !_subscribed)
            {
                gridManager.OnTileChanged    += HandleTileChanged;
                gridManager.OnRebakeComplete += HandleRebake;
                _subscribed = true;
            }
            return gridManager;
        }
    }

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Start() => _ = Grid;

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_subscribed && gridManager != null)
        {
            gridManager.OnTileChanged    -= HandleTileChanged;
            gridManager.OnRebakeComplete -= HandleRebake;
        }
    }

    // ── Treasury tiles ─────────────────────────────────────────────────

    public bool IsTreasury(GridCell cell) => cell != null && cell.TileType == treasuryType;

    /// <summary>
    /// Gold one Treasury tile can hold: capacityPerTile, × its room's
    /// efficiency if the Treasury scales with it. Just after the tile is
    /// built, before the room is measured, the room type's base efficiency
    /// stands in.
    /// </summary>
    public int CapacityOf(GridCell cell)
    {
        if (!IsTreasury(cell) || Grid == null) return 0;
        var def = Grid.GetDefinition(treasuryType);
        if (def == null) return 0;

        float capacity = def.capacityPerTile;
        if (def.capacityScalesWithEfficiency)
        {
            var room = cell.RoomId >= 0 ? Grid.GetRoomForCell(cell) : null;
            bool current = room != null && room.TileType == cell.TileType && room.Owner == cell.Owner;
            capacity *= current ? room.Efficiency : def.baseEfficiency;
        }
        return (int)(capacity + 0.0001f);
    }

    public int FreeSpace(GridCell cell) => Mathf.Max(0, CapacityOf(cell) - (cell?.StoredGold ?? 0));

    public bool HasSpace(GridCell cell, FactionID faction) =>
        IsTreasury(cell) && cell.Owner == faction && FreeSpace(cell) > 0;

    public bool HasGold(GridCell cell, FactionID faction) =>
        IsTreasury(cell) && cell.Owner == faction && cell.StoredGold > 0;

    /// <summary>Gold banked on all of a faction's Treasury tiles.</summary>
    public int StoredGold(FactionID faction)
    {
        Recount();
        return _stored.TryGetValue(faction, out int v) ? v : 0;
    }

    /// <summary>Room left across all of a faction's Treasury tiles.</summary>
    public int FreeCapacity(FactionID faction)
    {
        Recount();
        return _free.TryGetValue(faction, out int v) ? v : 0;
    }

    /// <summary>Banks up to amount on one tile of the faction's. Returns how much went in.</summary>
    public int Deposit(GridCell cell, FactionID faction, int amount)
    {
        if (amount <= 0 || !HasSpace(cell, faction)) return 0;
        int taken = Mathf.Min(amount, FreeSpace(cell));
        cell.SetStoredGoldInternal(cell.StoredGold + taken);
        Changed();
        return taken;
    }

    /// <summary>Takes up to amount from one tile of the faction's. Returns how much came out.</summary>
    public int Withdraw(GridCell cell, FactionID faction, int amount)
    {
        if (amount <= 0 || !HasGold(cell, faction)) return 0;
        int taken = Mathf.Min(amount, cell.StoredGold);
        cell.SetStoredGoldInternal(cell.StoredGold - taken);
        Changed();
        return taken;
    }

    /// <summary>
    /// Banks gold on whichever of the faction's tiles have room, fullest
    /// first so gold gathers into full tiles. Returns how much went in.
    /// </summary>
    public int DepositAnywhere(FactionID faction, int amount)
    {
        if (amount <= 0) return 0;
        var cells = TreasuryCells(faction, c => FreeSpace(c) > 0);
        cells.Sort((a, b) => b.StoredGold.CompareTo(a.StoredGold));

        int banked = 0;
        foreach (var c in cells)
        {
            if (banked >= amount) break;
            int take = Mathf.Min(amount - banked, FreeSpace(c));
            c.SetStoredGoldInternal(c.StoredGold + take);
            banked += take;
        }
        if (banked > 0) Changed();
        return banked;
    }

    /// <summary>
    /// Takes gold from the faction's tiles, emptiest first so part-filled
    /// tiles clear before full ones are touched. Returns how much came out.
    /// </summary>
    public int WithdrawAnywhere(FactionID faction, int amount)
    {
        if (amount <= 0) return 0;
        var cells = TreasuryCells(faction, c => c.StoredGold > 0);
        cells.Sort((a, b) => a.StoredGold.CompareTo(b.StoredGold));

        int taken = 0;
        foreach (var c in cells)
        {
            if (taken >= amount) break;
            int take = Mathf.Min(amount - taken, c.StoredGold);
            c.SetStoredGoldInternal(c.StoredGold - take);
            taken += take;
        }
        if (taken > 0) Changed();
        return taken;
    }

    private List<GridCell> TreasuryCells(FactionID faction, Func<GridCell, bool> filter)
    {
        var list = new List<GridCell>();
        var grid = Grid;
        if (grid == null) return list;
        for (int x = 0; x < grid.Width;  x++)
        for (int y = 0; y < grid.Height; y++)
        {
            var c = grid.GetCell(x, y);
            if (IsTreasury(c) && c.Owner == faction && filter(c)) list.Add(c);
        }
        return list;
    }

    private void Recount()
    {
        if (!_countsDirty) return;
        _countsDirty = false;
        _stored.Clear();
        _free.Clear();

        var grid = Grid;
        if (grid == null) return;
        for (int x = 0; x < grid.Width;  x++)
        for (int y = 0; y < grid.Height; y++)
        {
            var c = grid.GetCell(x, y);
            if (c == null || c.StoredGold <= 0 && !IsTreasury(c)) continue;
            _stored[c.Owner] = (_stored.TryGetValue(c.Owner, out int s) ? s : 0) + c.StoredGold;
            _free[c.Owner]   = (_free.TryGetValue(c.Owner, out int f) ? f : 0) + FreeSpace(c);
        }
    }

    private void Changed()
    {
        _countsDirty = true;
        OnGoldMoved?.Invoke();
    }

    // ── Gold piles ─────────────────────────────────────────────────────

    public GoldPile PileAt(GridCell cell) =>
        cell != null && _piles.TryGetValue(cell, out var pile) ? pile : null;

    /// <summary>
    /// Puts gold on the floor at a cell. On a Treasury tile it is banked
    /// there first, as far as the tile has room; the rest joins the cell's
    /// pile, or starts one. Returns the pile, or null if it was all banked.
    /// </summary>
    public GoldPile DropGold(GridCell cell, int amount)
    {
        if (cell == null || amount <= 0) return null;

        amount -= Deposit(cell, cell.Owner, amount);
        if (amount <= 0) return PileAt(cell);

        var pile = PileAt(cell);
        if (pile != null) { pile.SetAmount(pile.Amount + amount); return pile; }
        return CreatePile(cell, amount);
    }

    /// <summary>Takes up to amount from a pile, removing it once empty. Returns how much was taken.</summary>
    public int TakeFromPile(GoldPile pile, int amount)
    {
        if (pile == null || pile.IsHeld || amount <= 0) return 0;
        int taken = Mathf.Min(amount, pile.Amount);
        pile.SetAmount(pile.Amount - taken);
        if (pile.Amount <= 0) RemovePile(pile);
        return taken;
    }

    /// <summary>Lifted by the Keeper's hand: no longer on any cell.</summary>
    internal void Detach(GoldPile pile)
    {
        if (pile.Cell != null && PileAt(pile.Cell) == pile) _piles.Remove(pile.Cell);
    }

    /// <summary>Set down by the Keeper's hand: banked if dropped on a Treasury, else merged into the cell's pile.</summary>
    internal void Place(GoldPile pile, GridCell cell, Vector3 point)
    {
        int amount = pile.Amount - Deposit(cell, cell.Owner, pile.Amount);
        var existing = PileAt(cell);
        if (amount <= 0 || existing != null)
        {
            if (amount > 0) existing.SetAmount(existing.Amount + amount);
            pile.SetAmount(0);
            Destroy(pile.gameObject);
            return;
        }

        pile.SetAmount(amount);
        pile.AttachTo(cell, PilePosition(cell));
        _piles[cell] = pile;
    }

    private GoldPile CreatePile(GridCell cell, int amount)
    {
        var go = new GameObject($"GoldPile_{cell.X}_{cell.Y}");
        go.transform.SetParent(transform, false);

        Transform visual;
        if (pilePrefab != null)
        {
            visual = Instantiate(pilePrefab, go.transform).transform;
        }
        else
        {
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(disc.GetComponent<Collider>());
            disc.name = "Placeholder";
            disc.transform.SetParent(go.transform, false);
            var r = disc.GetComponent<Renderer>();
            if (r != null) r.material.color = placeholderColour;
            visual = disc.transform;
        }

        var pile = go.AddComponent<GoldPile>();
        pile.Initialise(this, visual, pilePrefab == null);
        pile.SetAmount(amount);
        pile.AttachTo(cell, PilePosition(cell));
        _piles[cell] = pile;
        return pile;
    }

    private void RemovePile(GoldPile pile)
    {
        Detach(pile);
        if (pile != null) Destroy(pile.gameObject);
    }

    private Vector3 PilePosition(GridCell cell) => Grid.CellToWorld(cell.X, cell.Y);

    /// <summary>World radius a pile of this size is drawn at.</summary>
    internal float PileRadius(int amount)
    {
        float t = Mathf.Sqrt(Mathf.Clamp01(amount / (float)fullPileAmount));
        float cell = Grid != null ? Grid.CellSize : 1f;
        return Mathf.Lerp(minPileRadius, maxPileRadius, t) * cell;
    }

    internal float MaxPileRadius => maxPileRadius * (Grid != null ? Grid.CellSize : 1f);

    // ── Grid events ────────────────────────────────────────────────────

    /// <summary>A Treasury tile that stops being one spills its gold where it stood.</summary>
    private void HandleTileChanged(GridCell cell)
    {
        if (cell.StoredGold > 0 && !IsTreasury(cell))
        {
            int gold = cell.StoredGold;
            cell.SetStoredGoldInternal(0);
            DropGold(cell, gold);
        }
        Changed();
    }

    /// <summary>Room shapes (so capacities) are settled: bank any pile sitting on a Treasury tile.</summary>
    private void HandleRebake(IReadOnlyList<DungeonRoom> rooms)
    {
        foreach (var pile in new List<GoldPile>(_piles.Values))
        {
            if (pile == null || !IsTreasury(pile.Cell)) continue;
            int banked = Deposit(pile.Cell, pile.Cell.Owner, pile.Amount);
            if (banked > 0) TakeFromPile(pile, banked);
        }
        Changed();
    }

    // ── Save / load ────────────────────────────────────────────────────

    /// <summary>Forgets every pile — called when a new grid is loaded.</summary>
    public void ResetState()
    {
        foreach (var pile in _piles.Values) if (pile != null) Destroy(pile.gameObject);
        _piles.Clear();
        Changed();
    }

    public List<CellGoldSaveData> CaptureTreasuryGold()
    {
        var list = new List<CellGoldSaveData>();
        var grid = Grid;
        if (grid == null) return list;
        for (int x = 0; x < grid.Width;  x++)
        for (int y = 0; y < grid.Height; y++)
        {
            var c = grid.GetCell(x, y);
            if (c != null && c.StoredGold > 0) list.Add(new CellGoldSaveData(x, y, c.StoredGold));
        }
        return list;
    }

    public List<CellGoldSaveData> CapturePiles()
    {
        var list = new List<CellGoldSaveData>();
        foreach (var pile in _piles.Values)
            if (pile != null && pile.Cell != null)
                list.Add(new CellGoldSaveData(pile.Cell.X, pile.Cell.Y, pile.Amount));
        return list;
    }

    /// <summary>Restores banked gold exactly as saved (capacity isn't rechecked) and the piles.</summary>
    public void Restore(List<CellGoldSaveData> treasuryGold, List<CellGoldSaveData> piles)
    {
        var grid = Grid;
        if (grid == null) return;

        if (treasuryGold != null)
            foreach (var e in treasuryGold)
            {
                var c = grid.GetCell(e.x, e.y);
                if (c != null) c.SetStoredGoldInternal(e.gold);
            }

        if (piles != null)
            foreach (var e in piles)
            {
                var c = grid.GetCell(e.x, e.y);
                if (c == null || e.gold <= 0) continue;
                var pile = PileAt(c);
                if (pile != null) pile.SetAmount(pile.Amount + e.gold);
                else CreatePile(c, e.gold);
            }

        Changed();
    }
}

/// <summary>Gold on one cell — a Treasury tile's banked gold, or a pile — in a save.</summary>
[Serializable]
public class CellGoldSaveData
{
    public int x, y, gold;

    public CellGoldSaveData() { }
    public CellGoldSaveData(int x, int y, int gold) { this.x = x; this.y = y; this.gold = gold; }
}
