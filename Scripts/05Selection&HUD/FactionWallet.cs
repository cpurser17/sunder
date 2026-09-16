using System;
using UnityEngine;

/// <summary>
/// Tracks gold for one faction. Replaces PlayerWallet.
/// One instance per active faction, managed by GameManager2D.
///
/// Attach to any persistent GO — in practice GameManager2D creates
/// these at runtime via AddComponent, so no scene setup is needed.
/// </summary>
public class FactionWallet : MonoBehaviour
{
    public FactionID Faction { get; private set; }
    public int       Gold    { get; private set; }

    /// <summary>Fired whenever the balance changes. Arg = new balance.</summary>
    public event Action<FactionID, int> OnGoldChanged;

    // ── Initialisation ─────────────────────────────────────────────────

    public void Initialise(FactionID faction, int startingGold)
    {
        Faction = faction;
        Gold    = Mathf.Max(0, startingGold);
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Sets gold directly (used by save/load). Fires OnGoldChanged.</summary>
    public void SetGold(int amount)
    {
        Gold = Mathf.Max(0, amount);
        OnGoldChanged?.Invoke(Faction, Gold);
    }

    /// <summary>Returns true and deducts if sufficient funds exist.</summary>
    public bool TrySpend(int amount)
    {
        if (amount <= 0) return false;
        if (Gold  <  amount) return false;
        Gold -= amount;
        OnGoldChanged?.Invoke(Faction, Gold);
        return true;
    }

    /// <summary>Adds gold to the wallet.</summary>
    public void Earn(int amount)
    {
        if (amount <= 0) return;
        Gold += amount;
        OnGoldChanged?.Invoke(Faction, Gold);
    }
}
