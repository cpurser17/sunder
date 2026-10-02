using UnityEngine;

/// <summary>
/// How one stat grows from level 1 to a minion's max level. Authored as four
/// numbers per stat on a faction's _Levels sheet; every level in between is
/// calculated, never stored.
///
///   t      = (level - 1) / (maxLevel - 1)       0 at level 1, 1 at maxLevel
///   shaped = t ^ (2 ^ curve)
///   value  = min + (max - min) * (1 + skew) * shaped
///
/// Curve bends the line without moving its ends: 0 is linear, positive is
/// back-loaded (small gains early), negative is front-loaded. Skew scales
/// the whole growth — 0.1 grows 10% further than max — so balance can be
/// nudged without rewriting the authored min/max.
///
/// This must stay identical to the preview formula in the workbook's
/// _Levels sheets and the explanation on its LevelFormula sheet.
/// </summary>
[System.Serializable]
public struct StatCurve
{
    public MinionStat stat;
    [Tooltip("Value at level 1.")]
    public float min;
    [Tooltip("Value at max level, before skew.")]
    public float max;
    [Tooltip("0 = linear, + = back-loaded, - = front-loaded.")]
    public float curve;
    [Tooltip("Scales total growth: 0.1 = 10% more, -0.1 = 10% less.")]
    public float skew;
    [Tooltip("Round to a whole number (from the Stats sheet's WholeNumber column).")]
    public bool wholeNumber;
    [Tooltip("True when this stat has a row on the _Levels sheet. False = not filled in " +
             "yet; min/max hold the Stats sheet default.")]
    public bool authored;

    public float Evaluate(int level, int maxLevel)
    {
        float t = maxLevel > 1
            ? Mathf.Clamp01((level - 1) / (float)(maxLevel - 1))
            : 1f;
        float shaped = Mathf.Pow(t, Mathf.Pow(2f, curve));
        float value  = min + (max - min) * (1f + skew) * shaped;
        return wholeNumber ? Mathf.Round(value) : value;
    }
}
