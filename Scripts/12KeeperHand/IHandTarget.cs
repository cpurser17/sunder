using UnityEngine;

/// <summary>
/// Anything the Keeper's hand can pick up, drop and slap. Implemented by
/// ImpController and CreatureController, which register themselves with
/// KeeperHand while enabled.
///
/// The hand owns only the mechanics — what's under the cursor, the held
/// stack, where a drop is legal. What being picked up, dropped or slapped
/// MEANS to a minion (dropping its job, reassessing on landing, getting
/// angry) lives on the minion, behind these calls.
/// </summary>
public interface IHandTarget
{
    FactionID Faction { get; }
    GridAgent Agent   { get; }
    Transform transform { get; }

    /// <summary>False once dead; the hand forgets it and never offers it again.</summary>
    bool IsAlive { get; }

    /// <summary>True between OnPickedUp and OnDropped.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// Lifted into the hand. The minion drops whatever it was doing and
    /// holds no work or behaviour preference while held; the hand moves it.
    /// </summary>
    void OnPickedUp();

    /// <summary>
    /// Set down at a position the hand has already checked is legal for it.
    /// The minion reassesses what to do from there.
    /// </summary>
    void OnDropped(Vector3 worldPosition, GridCell cell);

    /// <summary>Slapped by the hand: hurt a little, angered, and briefly faster at work.</summary>
    void OnSlapped(in HandSlap slap);
}

/// <summary>Tuning for one slap, passed from KeeperHand to the minion slapped.</summary>
public readonly struct HandSlap
{
    /// <summary>Damage as a fraction of the minion's max health.</summary>
    public readonly float DamageFraction;
    /// <summary>False keeps a slap from taking the last point of health.</summary>
    public readonly bool  CanKill;
    /// <summary>Anger added, on a 0-1 scale.</summary>
    public readonly float AngerGain;
    /// <summary>Work speed multiplier while the slap's boost lasts.</summary>
    public readonly float WorkSpeedMultiplier;
    /// <summary>Seconds the boost lasts. A new slap restarts it.</summary>
    public readonly float BoostDuration;

    public HandSlap(float damageFraction, bool canKill, float angerGain,
                    float workSpeedMultiplier, float boostDuration)
    {
        DamageFraction      = damageFraction;
        CanKill             = canKill;
        AngerGain           = angerGain;
        WorkSpeedMultiplier = workSpeedMultiplier;
        BoostDuration       = boostDuration;
    }

    /// <summary>Damage this slap deals to a minion with the given health.</summary>
    public float DamageFor(float currentHealth, float maxHealth)
    {
        float damage = Mathf.Max(1f, maxHealth * DamageFraction);
        return CanKill ? damage : Mathf.Min(damage, Mathf.Max(0f, currentHealth - 1f));
    }
}
