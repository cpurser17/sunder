using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>What an Unlock effect makes available.</summary>
public enum UnlockKind { Room, Spell, Minion, Trap, Door }

/// <summary>What a Stat effect changes the stats of.</summary>
public enum ModifierTarget { Minion, Spell, Room, Faction }

/// <summary>How a Stat effect changes a value: +5 adds, +10% adds a percentage, x1.5 multiplies.</summary>
public enum ModifierOp { Add, Percent, Multiply }

/// <summary>
/// One thing completing a research node does, as written in the Research
/// workbook's Effects column (several separated by ; or new lines):
///
///   Unlock(Room, Workshop)          a room, spell, minion, trap or door becomes available
///   Unlock(Spell, Lightning)
///   Unlock(Minion, T2F)
///   Stat(Minion:*, Health, +10%)    every minion's Health up 10%
///   Stat(Minion:T1F, Speed, +1)     one minion kind's Speed up 1
///   Stat(Spell:Heal, Cost, x0.8)    Heal costs 80%
///   Stat(Room:Treasury, Capacity, +25%)
///   Stat(Faction, ResearchSpeed, +20%)
///
/// Anything a node unlocks is locked until then — for the factions whose
/// web has that node. Things no node unlocks are available from the start.
/// The stats that can be changed are listed in ModifierStats.
/// </summary>
[Serializable]
public class ResearchEffect
{
    public bool           isUnlock;
    public UnlockKind     unlockKind;
    public string         unlockId;
    public ModifierTarget target;
    public string         targetId = "*";   // a minion/spell/room id, or * for all
    public string         stat;
    public ModifierOp     op;
    public float          value;

    public string UnlockKey => Key(unlockKind, unlockId);
    public static string Key(UnlockKind kind, string id) => $"{kind}:{id}".ToLowerInvariant();

    // ── Parsing ────────────────────────────────────────────────────────

    /// <summary>Splits an Effects cell into its effects: on ; and new lines, and on commas outside brackets.</summary>
    public static List<string> SplitCell(string text)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return parts;
        int depth = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            char c = i < text.Length ? text[i] : ';';
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if ((c == ';' || c == '\n' || c == '\r' || (c == ',' && depth == 0)))
            {
                string part = text.Substring(start, i - start).Trim();
                if (part.Length > 0) parts.Add(part);
                start = i + 1;
            }
        }
        return parts;
    }

    /// <summary>Reads one effect. Returns null and an error message if it can't.</summary>
    public static ResearchEffect Parse(string text, out string error)
    {
        error = null;
        text = text?.Trim() ?? "";
        int open = text.IndexOf('('), close = text.LastIndexOf(')');
        if (open <= 0 || close < open) { error = "should look like Unlock(Room, Workshop) or Stat(Minion:*, Health, +10%)"; return null; }

        string keyword = text.Substring(0, open).Trim();
        var args = text.Substring(open + 1, close - open - 1).Split(',').Select(a => a.Trim()).ToList();

        if (keyword.Equals("Unlock", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count != 2 || args[1].Length == 0) { error = "Unlock needs a kind and an id, e.g. Unlock(Room, Workshop)"; return null; }
            if (!TryEnum(args[0], out UnlockKind kind)) { error = $"'{args[0]}' isn't one of: {Names<UnlockKind>()}"; return null; }
            return new ResearchEffect { isUnlock = true, unlockKind = kind, unlockId = args[1] };
        }

        if (keyword.Equals("Stat", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count != 3) { error = "Stat needs a target, a stat and a change, e.g. Stat(Minion:*, Health, +10%)"; return null; }
            string[] t = args[0].Split(':');
            if (!TryEnum(t[0].Trim(), out ModifierTarget target)) { error = $"'{t[0]}' isn't one of: {Names<ModifierTarget>()}"; return null; }
            string id = t.Length > 1 && t[1].Trim().Length > 0 ? t[1].Trim() : "*";
            if (target == ModifierTarget.Faction) id = "*";

            string stat = ModifierStats.Canonical(target, args[1]);
            if (stat == null) { error = $"{target} has no stat '{args[1]}' the game can change — it can change: {string.Join(", ", ModifierStats.For(target))}"; return null; }
            if (!TryChange(args[2], out var op, out float value)) { error = $"'{args[2]}' should be like +5, -5, +10%, -10% or x1.5"; return null; }
            return new ResearchEffect { target = target, targetId = id, stat = stat, op = op, value = value };
        }

        error = $"'{keyword}' isn't Unlock or Stat";
        return null;
    }

    private static bool TryChange(string text, out ModifierOp op, out float value)
    {
        op = ModifierOp.Add;
        value = 0f;
        text = text.Replace(" ", "");
        if (text.Length == 0) return false;
        if (text[0] == 'x' || text[0] == 'X' || text[0] == '×' || text[0] == '*')
        {
            op = ModifierOp.Multiply;
            return float.TryParse(text.Substring(1), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        if (text.EndsWith("%"))
        {
            op = ModifierOp.Percent;
            text = text.Substring(0, text.Length - 1);
        }
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        string name = Enum.GetNames(typeof(T)).FirstOrDefault(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
        return name != null && Enum.TryParse(name, out value);
    }

    private static string Names<T>() where T : struct, Enum => string.Join(", ", Enum.GetNames(typeof(T)));

    // ── Describing ─────────────────────────────────────────────────────

    /// <summary>In words, for the research screen, e.g. "+10% Health for all minions". nameOf turns ids into names.</summary>
    public string Describe(Func<UnlockKind, string, string> nameOf = null)
    {
        if (isUnlock)
            return $"Unlocks {(nameOf?.Invoke(unlockKind, unlockId) ?? unlockId)} ({unlockKind.ToString().ToLowerInvariant()})";

        string change = op switch
        {
            ModifierOp.Multiply => $"×{value.ToString("0.##", CultureInfo.InvariantCulture)}",
            ModifierOp.Percent  => $"{(value >= 0 ? "+" : "")}{value.ToString("0.##", CultureInfo.InvariantCulture)}%",
            _                   => $"{(value >= 0 ? "+" : "")}{value.ToString("0.##", CultureInfo.InvariantCulture)}",
        };
        string who = target switch
        {
            ModifierTarget.Faction => "",
            _ when targetId == "*" => target switch
            {
                ModifierTarget.Minion => " for all minions",
                ModifierTarget.Spell  => " for all spells",
                _                     => " for all rooms",
            },
            _ => $" for {(nameOf?.Invoke(ToUnlockKind(target), targetId) ?? targetId)}",
        };
        return $"{change} {ModifierStats.Label(stat)}{who}";
    }

    private static UnlockKind ToUnlockKind(ModifierTarget t) => t switch
    {
        ModifierTarget.Spell => UnlockKind.Spell,
        ModifierTarget.Room  => UnlockKind.Room,
        _                    => UnlockKind.Minion,
    };

    public override string ToString() =>
        isUnlock ? $"Unlock({unlockKind}, {unlockId})" : $"Stat({target}:{targetId}, {stat}, {op} {value})";
}

/// <summary>
/// The stats research (and later other things) can change, per target. A stat
/// is only listed here once the game reads it through FactionModifiers —
/// adding a new one is a line here plus the line in the code that reads it.
/// </summary>
public static class ModifierStats
{
    // Spells
    public const string Cost      = "Cost";
    public const string Cooldown  = "Cooldown";
    public const string Power     = "Power";
    public const string Radius    = "Radius";
    public const string Duration  = "Duration";
    // Rooms (Cost too)
    public const string Capacity  = "Capacity";
    // Faction-wide
    public const string SummonInterval = "SummonInterval";
    public const string ResearchSpeed  = "ResearchSpeed";

    private static readonly string[] SpellStats   = { Cost, Cooldown, Power, Radius, Duration };
    private static readonly string[] RoomStats    = { Cost, Capacity };
    private static readonly string[] FactionStats = { SummonInterval, ResearchSpeed };

    public static IEnumerable<string> For(ModifierTarget target) => target switch
    {
        ModifierTarget.Minion => Enum.GetNames(typeof(MinionStat)),
        ModifierTarget.Spell  => SpellStats,
        ModifierTarget.Room   => RoomStats,
        _                     => FactionStats,
    };

    /// <summary>The stat's proper spelling, or null if the game can't change it.</summary>
    public static string Canonical(ModifierTarget target, string stat) =>
        For(target).FirstOrDefault(s => s.Equals(stat?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"SummonInterval" → "summon interval".</summary>
    public static string Label(string stat)
    {
        if (string.IsNullOrEmpty(stat)) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < stat.Length; i++)
        {
            if (i > 0 && char.IsUpper(stat[i]) && !char.IsUpper(stat[i - 1])) sb.Append(' ');
            sb.Append(stat[i]);
        }
        string s = sb.ToString();
        return s.StartsWith("Skill ") ? s.Substring(6) + " skill" : s;
    }
}
