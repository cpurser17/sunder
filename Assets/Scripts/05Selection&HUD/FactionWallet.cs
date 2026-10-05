using System;
using UnityEngine;

/// <summary>
/// One faction's gold. One instance per active faction, created at runtime
/// by GameManager2D — no scene setup needed.
///
/// Gold = Reserve + the gold banked on the faction's Treasury tiles (see
/// TreasuryManager). Reserve is the level's starting gold: spent before any
/// Treasury gold and never refilled — new gold only arrives by mining and
/// banking it. Gold piles on the floor don't count until they're banked.
/// </summary>
public class FactionWallet : MonoBehaviour
{
    public FactionID Faction { get; private set; }

    /// <summary>Starting gold not yet spent. Spent first on building and summoning; never refilled, never used for wages.</summary>
    public int Reserve { get; private set; }

    /// <summary>Gold banked on this faction's Treasury tiles.</summary>
    public int Stored => TreasuryManager.Instance != null ? TreasuryManager.Instance.StoredGold(Faction) : 0;

    /// <summary>Everything this faction can spend: Reserve + Stored.</summary>
    public int Gold => Reserve + Stored;

    /// <summary>Fired whenever the balance changes. Arg = new balance.</summary>
    public event Action<FactionID, int> OnGoldChanged;

    private int _lastReported = int.MinValue;

    // ── Initialisation ─────────────────────────────────────────────────

    public void Initialise(FactionID faction, int startingGold)
    {
        Faction = faction;
        Reserve = Mathf.Max(0, startingGold);
        Report();
    }

    private void OnEnable()  => TreasuryManager.OnGoldMoved += Report;
    private void OnDisable() => TreasuryManager.OnGoldMoved -= Report;

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Sets the unspent starting gold directly (save/load).</summary>
    public void SetReserve(int amount)
    {
        Reserve = Mathf.Max(0, amount);
        Report();
    }

    /// <summary>
    /// Returns true and deducts if sufficient funds exist: from the reserve
    /// first, then from the Treasury tiles, emptiest first.
    /// </summary>
    public bool TrySpend(int amount)
    {
        if (amount <= 0) return false;
        if (Gold   <  amount) return false;

        int fromReserve = Mathf.Min(Reserve, amount);
        Reserve -= fromReserve;
        if (amount > fromReserve)
            TreasuryManager.Instance?.WithdrawAnywhere(Faction, amount - fromReserve);

        Report();
        return true;
    }

    /// <summary>
    /// Gold coming back to the faction, e.g. from selling a tile: banked
    /// straight onto Treasury tiles with room. What doesn't fit is dropped as
    /// a gold pile at dropAt (lost if dropAt is null). Never goes to Reserve.
    /// </summary>
    public void Refund(int amount, GridCell dropAt)
    {
        if (amount <= 0) return;

        var treasury = TreasuryManager.Instance;
        int banked   = treasury != null ? treasury.DepositAnywhere(Faction, amount) : 0;
        if (amount > banked && treasury != null) treasury.DropGold(dropAt, amount - banked);
        Report();
    }

    private void Report()
    {
        int gold = Gold;
        if (gold == _lastReported) return;
        _lastReported = gold;
        OnGoldChanged?.Invoke(Faction, gold);
    }
}
