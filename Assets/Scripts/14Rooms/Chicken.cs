using UnityEngine;

/// <summary>
/// A chicken: wanders, gets eaten. Made by HatcheryManager — never placed
/// by hand.
///
/// It steps from cell to cell with pauses in between. On a Hatchery tile it
/// only steps onto tiles of the same Hatchery, so it stays put; anywhere
/// else it roams any open floor, and is bound again once it wanders into a
/// Hatchery. Once a minion has picked it to eat, it stands still.
///
/// The Keeper's hand can pick up chickens on its own floor, drop them on
/// open floor, or drop one on one of its minions to force-feed it. A slap
/// kills it.
/// </summary>
public class Chicken : MonoBehaviour, IHandTarget, IHandFeed
{
    private HatcheryManager _manager;
    private GridManager2D   _grid;
    private float           _radius;
    private bool            _held;
    private bool            _gone;
    private bool            _moving;
    private Vector3         _target;
    private float           _idleUntil;

    public GridCell Cell      { get; private set; }
    /// <summary>The minion that's chosen to eat it, if any. Cleared automatically if that minion is destroyed.</summary>
    public Object   ClaimedBy { get; private set; }

    /// <summary>Not held, not spoken for, not eaten — fair game.</summary>
    public bool IsFree => !_gone && !_held && ClaimedBy == null;

    internal void Initialise(HatcheryManager manager, GridManager2D grid, GridCell cell, Vector3 position, float radius)
    {
        _manager = manager;
        _grid    = grid;
        _radius  = radius;
        Cell     = cell;
        transform.position = position;
        _idleUntil = Time.time + manager.RandomIdle();
    }

    // ── Wandering ──────────────────────────────────────────────────────

    private void Update()
    {
        if (_gone || _held || ClaimedBy != null || _manager == null) return;

        if (!_moving)
        {
            if (Time.time >= _idleUntil) PickStep();
            return;
        }

        float step = _manager.MoveSpeed * _grid.CellSize * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, _target, step);

        if (_grid.WorldToCell(transform.position, out int x, out int y))
        {
            var now = _grid.GetCell(x, y);
            if (now != null && now != Cell) { _manager.Moved(this, Cell, now); Cell = now; }
        }

        if ((transform.position - _target).sqrMagnitude < 0.0001f)
        {
            _moving    = false;
            _idleUntil = Time.time + _manager.RandomIdle();
        }
    }

    private void PickStep()
    {
        _idleUntil = Time.time + _manager.RandomIdle();
        if (Cell == null) return;

        var options = new System.Collections.Generic.List<GridCell>(4);
        foreach (var (dx, dy) in TraversalRules.Orthogonal)
        {
            var n = _grid.GetCell(Cell.X + dx, Cell.Y + dy);
            if (_manager.CanWander(Cell, n)) options.Add(n);
        }
        // Sometimes just shuffle about within its own cell.
        options.Add(Cell);

        var to = options[Mathf.Min(options.Count - 1, (int)(_manager.Random01() * options.Count))];
        _target = _grid.CellToWorld(to.X, to.Y) + _manager.Jitter(_grid.CellSize);
        _moving = true;
    }

    // ── Being eaten ────────────────────────────────────────────────────

    /// <summary>Reserves it for one minion; it stops wandering. False if it isn't free.</summary>
    public bool TryClaim(Object by)
    {
        if (_gone || _held) return false;
        if (ClaimedBy != null && ClaimedBy != by) return false;
        ClaimedBy = by;
        _moving   = false;
        return true;
    }

    public void Release(Object by)
    {
        if (ClaimedBy == by) ClaimedBy = null;
    }

    /// <summary>Eaten (or killed): gone for good.</summary>
    public void Consume()
    {
        if (_gone) return;
        _gone = true;
        _manager.Remove(this);
    }

    // ── IHandTarget ────────────────────────────────────────────────────

    private void OnEnable()  => KeeperHand.Register(this);
    private void OnDisable() => KeeperHand.Unregister(this);

    /// <summary>The owner of the floor it's on — the hand only grabs chickens on its own floor.</summary>
    public FactionID Faction    => Cell != null ? Cell.Owner : FactionID.Unaligned;
    public GridAgent Agent      => null;
    public float     HandRadius => _radius;
    public bool      IsAlive    => !_gone && this != null;
    public bool      IsHeld     => _held;
    public bool      CanAbandon => false;

    /// <summary>Where it was before the hand picked it up — a save puts a held chicken back there.</summary>
    public GridCell LastCell { get; private set; }

    public void OnPickedUp()
    {
        _held     = true;
        _moving   = false;
        ClaimedBy = null;
        _manager.Detach(this, Cell);
        LastCell = Cell;
        Cell = null;
    }

    public void OnDropped(Vector3 worldPosition, GridCell cell)
    {
        _held = false;
        Cell  = cell;
        transform.position = new Vector3(worldPosition.x, _grid.transform.position.y, worldPosition.z);
        _manager.Attach(this, cell);
        _idleUntil = Time.time + _manager.RandomIdle();
    }

    /// <summary>Dropped on a minion: force-fed to it.</summary>
    public bool TryFeed(MinionController minion)
    {
        if (_gone || minion == null || !minion.IsAlive) return false;
        _held = false;
        minion.ForceFeed(_manager.HungerPerChicken, _manager.HealPerChicken, _manager.ForceFeedSeconds);
        Consume();
        return true;
    }

    public void OnAbandon() { }

    /// <summary>A slap kills a chicken.</summary>
    public void OnSlapped(in HandSlap slap) => Consume();
}
