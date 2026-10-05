using UnityEngine;

/// <summary>
/// Loose gold lying on one cell. Made and tracked by TreasuryManager —
/// never placed by hand.
///
/// Workers haul piles in their own territory to a Treasury tile (one worker
/// per pile at a time, via TryClaim). The Keeper's hand picks up piles in
/// its own territory like a minion; dropped on a Treasury tile it's banked,
/// anywhere else it lies there as a pile again.
/// </summary>
public class GoldPile : MonoBehaviour, IHandTarget
{
    private TreasuryManager _manager;
    private Transform       _visual;
    private bool            _placeholder;
    private bool            _held;

    public int      Amount    { get; private set; }
    public GridCell Cell      { get; private set; }
    /// <summary>The worker heading for this pile, if any. Cleared automatically if it's destroyed.</summary>
    public Object   ClaimedBy { get; private set; }

    internal void Initialise(TreasuryManager manager, Transform visual, bool placeholder)
    {
        _manager     = manager;
        _visual      = visual;
        _placeholder = placeholder;
    }

    internal void SetAmount(int amount)
    {
        Amount = Mathf.Max(0, amount);
        if (_visual == null || _manager == null) return;

        float r = _manager.PileRadius(Amount);
        _visual.localScale = _placeholder
            ? new Vector3(r * 2f, r * 0.25f, r * 2f)        // flat disc, half a unit tall per scale unit
            : Vector3.one * (r / _manager.MaxPileRadius);   // prefab authored at full size
        _visual.localPosition = _placeholder ? new Vector3(0f, r * 0.25f, 0f) : Vector3.zero;
    }

    internal void AttachTo(GridCell cell, Vector3 position)
    {
        Cell = cell;
        transform.position = position;
        name = $"GoldPile_{cell.X}_{cell.Y}";
    }

    /// <summary>Reserves this pile for one worker. False if held, or another worker has it.</summary>
    public bool TryClaim(Object by)
    {
        if (_held || Amount <= 0) return false;
        if (ClaimedBy != null && ClaimedBy != by) return false;
        ClaimedBy = by;
        return true;
    }

    public void Release(Object by)
    {
        if (ClaimedBy == by) ClaimedBy = null;
    }

    public bool IsClaimed => ClaimedBy != null;

    // ── IHandTarget ────────────────────────────────────────────────────

    private void OnEnable()  => KeeperHand.Register(this);
    private void OnDisable() => KeeperHand.Unregister(this);

    /// <summary>The owner of the floor it lies on — the hand only grabs piles in its own territory.</summary>
    public FactionID Faction    => Cell != null ? Cell.Owner : FactionID.Unaligned;
    public GridAgent Agent      => null;
    public float     HandRadius => _manager != null ? _manager.PileRadius(Amount) : 0.2f;
    public bool      IsAlive    => this != null && Amount > 0;
    public bool      IsHeld     => _held;
    public bool      CanAbandon => false;

    public void OnPickedUp()
    {
        _held     = true;
        ClaimedBy = null;
        _manager.Detach(this);
        Cell = null;
    }

    public void OnDropped(Vector3 worldPosition, GridCell cell)
    {
        _held = false;
        _manager.Place(this, cell, worldPosition);
    }

    public void OnAbandon() { }
    public void OnSlapped(in HandSlap slap) { }
}
