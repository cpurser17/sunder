using UnityEngine;

/// <summary>
/// A minion's short-term temper: how angry it is, and the burst of work
/// speed a slap from the Keeper's hand buys. Shared by workers and creatures so
/// both respond to the hand the same way.
///
/// Anger has two kinds of part:
///   slaps        decays on its own, computed lazily from the time of the
///                last change, so nothing has to tick it
///   grievances   lasting anger, one value per cause (unpaid wages,
///                exhaustion, no bed…). Each is set and cleared by whatever
///                owns that cause; they don't fade on their own.
/// All placeholders until the anger/mood behaviour is designed properly.
/// Anger is groundwork for the mood system — nothing acts on it yet (later:
/// fighting other minions, deserting).
/// </summary>
public class MinionTemper
{
    /// <summary>Causes of lasting anger.</summary>
    public enum Grievance { UnpaidWages, Exhaustion, NoBed, Starving, Idle }

    private readonly float _angerDecayPerSecond;
    private readonly float[] _grievances = new float[System.Enum.GetValues(typeof(Grievance)).Length];

    private float _anger;
    private float _angerSetAt;
    private float _boostMultiplier = 1f;
    private float _boostUntil;

    public MinionTemper(float angerDecayPerSecond) =>
        _angerDecayPerSecond = Mathf.Max(0f, angerDecayPerSecond);

    /// <summary>Current anger, 0 (calm) to 1 (furious).</summary>
    public float Anger
    {
        get
        {
            float total = SlapAnger;
            foreach (float g in _grievances) total += g;
            return Mathf.Clamp01(total);
        }
    }

    private float SlapAnger =>
        Mathf.Clamp01(_anger - (Time.time - _angerSetAt) * _angerDecayPerSecond);

    public float GetGrievance(Grievance cause) => _grievances[(int)cause];

    public void SetGrievance(Grievance cause, float value) =>
        _grievances[(int)cause] = Mathf.Clamp01(value);

    public void AddGrievance(Grievance cause, float amount) =>
        SetGrievance(cause, GetGrievance(cause) + amount);

    public void ClearGrievances()
    {
        for (int i = 0; i < _grievances.Length; i++) _grievances[i] = 0f;
    }

    /// <summary>Multiplier on work speed — above 1 while a slap's boost lasts.</summary>
    public float WorkSpeedMultiplier => Time.time < _boostUntil ? _boostMultiplier : 1f;

    public void ApplySlap(in HandSlap slap)
    {
        _anger      = Mathf.Clamp01(SlapAnger + slap.AngerGain);
        _angerSetAt = Time.time;

        _boostMultiplier = Mathf.Max(1f, slap.WorkSpeedMultiplier);
        _boostUntil      = Time.time + slap.BoostDuration;
    }
}
