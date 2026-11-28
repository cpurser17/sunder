using System;
using System.Collections.Generic;

/// <summary>
/// One team's stat changes and unlocks — from research now, and later from
/// anything else that grants them (prayer, torture, strategic mode). Every
/// place the game reads a changeable stat passes it through Apply, so a
/// change shows up everywhere at once. Owned by FactionResearchState.
///
/// Changes stack as (value + adds) × (1 + percentages) × multipliers.
/// </summary>
public class FactionModifiers
{
    private readonly Dictionary<string, List<ResearchEffect>> _byStat = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unlocked = new();
    private readonly HashSet<string> _lockable = new();

    /// <summary>Raised when anything changes, e.g. so the HUD can enable a newly unlocked room.</summary>
    public event Action OnChanged;

    /// <summary>True once any minion stat is changed — lets the hot minion stat path skip the lookup until then.</summary>
    public bool HasMinionChanges { get; private set; }

    private static string StatKey(ModifierTarget target, string stat) => $"{target}:{stat}";

    // ── Building ───────────────────────────────────────────────────────

    public void Clear()
    {
        _byStat.Clear();
        _unlocked.Clear();
        _lockable.Clear();
        HasMinionChanges = false;
        OnChanged?.Invoke();
    }

    /// <summary>Things that are locked until something unlocks them (for research: whatever a node in the web unlocks).</summary>
    public void SetLockable(IEnumerable<string> unlockKeys)
    {
        _lockable.Clear();
        foreach (var k in unlockKeys) _lockable.Add(k);
        OnChanged?.Invoke();
    }

    public void Add(ResearchEffect effect, bool notify = true)
    {
        if (effect == null) return;
        if (effect.isUnlock) _unlocked.Add(effect.UnlockKey);
        else
        {
            string key = StatKey(effect.target, effect.stat);
            if (!_byStat.TryGetValue(key, out var list)) _byStat[key] = list = new List<ResearchEffect>();
            list.Add(effect);
            if (effect.target == ModifierTarget.Minion) HasMinionChanges = true;
        }
        if (notify) OnChanged?.Invoke();
    }

    public void NotifyChanged() => OnChanged?.Invoke();

    // ── Reading ────────────────────────────────────────────────────────

    public bool IsLocked(UnlockKind kind, string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        string key = ResearchEffect.Key(kind, id);
        return _lockable.Contains(key) && !_unlocked.Contains(key);
    }

    /// <summary>The value after every change to that stat on that target (id = a minion/spell/room id).</summary>
    public float Apply(ModifierTarget target, string id, string stat, float value)
    {
        if (!_byStat.TryGetValue(StatKey(target, stat), out var list)) return value;
        float add = 0f, percent = 0f, multiply = 1f;
        foreach (var e in list)
        {
            if (e.targetId != "*" && !string.Equals(e.targetId, id, StringComparison.OrdinalIgnoreCase)) continue;
            switch (e.op)
            {
                case ModifierOp.Add:      add      += e.value; break;
                case ModifierOp.Percent:  percent  += e.value; break;
                case ModifierOp.Multiply: multiply *= e.value; break;
            }
        }
        return (value + add) * Math.Max(0f, 1f + percent / 100f) * multiply;
    }

    // ── Shortcuts for the code that reads stats ────────────────────────

    public static FactionModifiers Of(FactionID faction) =>
        GameManager2D.Instance != null ? GameManager2D.Instance.GetResearch(faction)?.Modifiers : null;

    public static float Get(FactionID faction, ModifierTarget target, string id, string stat, float value) =>
        Of(faction)?.Apply(target, id, stat, value) ?? value;

    public static int GetInt(FactionID faction, ModifierTarget target, string id, string stat, int value) =>
        (int)Math.Round(Get(faction, target, id, stat, value));

    public static bool Locked(FactionID faction, UnlockKind kind, string id) =>
        Of(faction)?.IsLocked(kind, id) ?? false;
}
