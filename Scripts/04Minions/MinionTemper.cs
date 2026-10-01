using UnityEngine;

/// <summary>
/// A minion's short-term temper: how angry it is, and the burst of work
/// speed a slap from the Keeper's hand buys. Shared by workers and creatures so
/// both respond to the hand the same way.
///
/// Anger is groundwork for the mood system — nothing reads it yet beyond
/// exposing it. It decays on its own, computed lazily from the time of the
/// last change, so nothing has to tick it.
/// </summary>
public class MinionTemper
{
    private readonly float _angerDecayPerSecond;

    private float _anger;
    private float _angerSetAt;
    private float _boostMultiplier = 1f;
    private float _boostUntil;

    public MinionTemper(float angerDecayPerSecond) =>
        _angerDecayPerSecond = Mathf.Max(0f, angerDecayPerSecond);

    /// <summary>Current anger, 0 (calm) to 1 (furious).</summary>
    public float Anger =>
        Mathf.Clamp01(_anger - (Time.time - _angerSetAt) * _angerDecayPerSecond);

    /// <summary>Multiplier on work speed — above 1 while a slap's boost lasts.</summary>
    public float WorkSpeedMultiplier => Time.time < _boostUntil ? _boostMultiplier : 1f;

    public void ApplySlap(in HandSlap slap)
    {
        _anger      = Mathf.Clamp01(Anger + slap.AngerGain);
        _angerSetAt = Time.time;

        _boostMultiplier = Mathf.Max(1f, slap.WorkSpeedMultiplier);
        _boostUntil      = Time.time + slap.BoostDuration;
    }
}
