using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// What an unlock rule can ask about the player's progress. CampaignProgress
/// answers it for one campaign playthrough (a save slot); CampaignProfile
/// for things across playthroughs (campaigns completed).
/// </summary>
public interface IUnlockState
{
    bool  IsComplete(string missionId);
    bool  HasCollected(string missionId, string itemId);
    /// <summary>The lowest (or highest) value of a stat recorded for a mission, if any.</summary>
    bool  TryBest(string missionId, string stat, bool lowest, out float value);
    /// <summary>A stat summed over every mission played in this campaign.</summary>
    float Total(string stat);
    bool  IsCampaignComplete(string campaignId);
}

/// <summary>Names a condition keyword for unlock rules — found automatically, like spell effects.</summary>
[AttributeUsage(AttributeTargets.Class)]
public class UnlockConditionAttribute : Attribute
{
    public readonly string Keyword;
    public UnlockConditionAttribute(string keyword) => Keyword = keyword;
}

/// <summary>
/// One kind of condition in an unlock rule, e.g. Complete(M04). One small
/// class per keyword, tagged [UnlockCondition("Keyword")].
/// </summary>
public abstract class UnlockCondition
{
    /// <summary>Checks the arguments and comparison when the rule is read. Null if fine, else what's wrong.</summary>
    public abstract string Validate(IReadOnlyList<string> args, string op);

    /// <summary>Whether the condition holds. op/value are null when the rule has no comparison.</summary>
    public abstract bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state);

    /// <summary>Mission ids among the arguments, so the importer can check they exist.</summary>
    public virtual IEnumerable<string> MissionsReferenced(IReadOnlyList<string> args) { yield break; }

    /// <summary>Stat names among the arguments, so the importer can check the game records them.</summary>
    public virtual IEnumerable<string> StatsReferenced(IReadOnlyList<string> args) { yield break; }

    protected static bool Compare(float a, string op, float b) => op switch
    {
        ">=" => a >= b, "<=" => a <= b, ">" => a > b, "<" => a < b,
        "==" => Math.Abs(a - b) < 1e-4f, "!=" => Math.Abs(a - b) >= 1e-4f,
        _    => false,
    };

    // ── Registry ───────────────────────────────────────────────────────

    private static Dictionary<string, UnlockCondition> _byKeyword;

    /// <summary>The condition for a keyword. Campaign&lt;Stat&gt; (e.g. CampaignKills) is a campaign total.</summary>
    public static UnlockCondition Find(string keyword)
    {
        if (_byKeyword == null) Discover();
        if (_byKeyword.TryGetValue(keyword, out var c)) return c;
        if (keyword.Length > 8 && keyword.StartsWith("Campaign", StringComparison.OrdinalIgnoreCase))
            return CampaignTotalCondition.Instance;
        return null;
    }

    private static void Discover()
    {
        _byKeyword = new Dictionary<string, UnlockCondition>(StringComparer.OrdinalIgnoreCase);
        Type[] types;
        try { types = typeof(UnlockCondition).Assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }   // skip any type that won't load
        foreach (var type in types)
        {
            if (type == null || type.IsAbstract || !typeof(UnlockCondition).IsAssignableFrom(type)) continue;
            var tag = (UnlockConditionAttribute)Attribute.GetCustomAttribute(type, typeof(UnlockConditionAttribute));
            if (tag != null && !_byKeyword.ContainsKey(tag.Keyword))
                _byKeyword[tag.Keyword] = (UnlockCondition)Activator.CreateInstance(type);
        }
    }
}

/// <summary>
/// A mission's or campaign's unlock rule, read from the workbook's Requires
/// column. Conditions are combined with &amp; (and), | (or), ! (not) and
/// brackets; &amp; binds tighter than |.
///
///   Complete(M04)
///   Complete(M10) &amp; CampaignKills &gt;= 500
///   Complete(M07) &amp; (Best(M07, MinionsLost) &lt;= 0 | Collected(M07, Crown))
///
/// A blank rule always holds. Parse never throws: a rule with a mistake
/// has an Error, and never holds.
/// </summary>
public class UnlockRule
{
    public string Text  { get; }
    public string Error { get; private set; }
    public bool   IsBlank => _root == null && Error == null;

    private readonly Node _root;

    // ── Tree ───────────────────────────────────────────────────────────

    private abstract class Node { public abstract bool Eval(IUnlockState s); }

    private sealed class And : Node
    {
        public Node A, B;
        public override bool Eval(IUnlockState s) => A.Eval(s) && B.Eval(s);
    }

    private sealed class Or : Node
    {
        public Node A, B;
        public override bool Eval(IUnlockState s) => A.Eval(s) || B.Eval(s);
    }

    private sealed class Not : Node
    {
        public Node A;
        public override bool Eval(IUnlockState s) => !A.Eval(s);
    }

    private sealed class Leaf : Node
    {
        public UnlockCondition Condition;
        public List<string>    Args = new();
        public string          Op;
        public float           Value;
        public override bool Eval(IUnlockState s) => Condition.Test(Args, Op, Value, s);
    }

    private readonly List<Leaf> _leaves = new();

    // ── Parsing ────────────────────────────────────────────────────────

    private UnlockRule(string text)
    {
        Text = text ?? "";
        if (string.IsNullOrWhiteSpace(Text)) return;
        try
        {
            _tokens = Tokenise(Text);
            _root   = ParseOr();
            if (_pos < _tokens.Count) throw new FormatException($"unexpected \"{_tokens[_pos]}\"");
        }
        catch (FormatException e)
        {
            Error  = e.Message;
            _root  = null;
            _leaves.Clear();
        }
        _tokens = null;
    }

    public static UnlockRule Parse(string text) => new(text);

    /// <summary>Whether the rule holds. Blank: yes. A rule with an error: no.</summary>
    public bool IsMet(IUnlockState state) =>
        Error == null && (_root == null || (state != null && _root.Eval(state)));

    /// <summary>Every mission id the rule mentions.</summary>
    public IEnumerable<string> MissionsReferenced()
    {
        foreach (var leaf in _leaves)
            foreach (var id in leaf.Condition.MissionsReferenced(leaf.Args)) yield return id;
    }

    /// <summary>Every stat name the rule mentions.</summary>
    public IEnumerable<string> StatsReferenced()
    {
        foreach (var leaf in _leaves)
            foreach (var stat in leaf.Condition.StatsReferenced(leaf.Args)) yield return stat;
    }

    private List<string> _tokens;
    private int _pos;

    private string Peek => _pos < _tokens.Count ? _tokens[_pos] : null;
    private string Next() => _pos < _tokens.Count ? _tokens[_pos++] : throw new FormatException("the rule ends too soon");

    private void Expect(string token)
    {
        string t = Next();
        if (t != token) throw new FormatException($"expected \"{token}\" but found \"{t}\"");
    }

    private Node ParseOr()
    {
        var node = ParseAnd();
        while (Peek == "|") { _pos++; node = new Or { A = node, B = ParseAnd() }; }
        return node;
    }

    private Node ParseAnd()
    {
        var node = ParseUnary();
        while (Peek == "&") { _pos++; node = new And { A = node, B = ParseUnary() }; }
        return node;
    }

    private Node ParseUnary()
    {
        if (Peek == "!") { _pos++; return new Not { A = ParseUnary() }; }
        if (Peek == "(") { _pos++; var inner = ParseOr(); Expect(")"); return inner; }
        return ParseCondition();
    }

    private Node ParseCondition()
    {
        string keyword = Next();
        if (!IsWord(keyword)) throw new FormatException($"expected a condition but found \"{keyword}\"");

        var condition = UnlockCondition.Find(keyword);
        if (condition == null) throw new FormatException($"unknown condition \"{keyword}\"");

        var leaf = new Leaf { Condition = condition };
        if (Peek == "(")
        {
            _pos++;
            if (Peek != ")")
            {
                while (true)
                {
                    string arg = Next();
                    if (!IsWord(arg) && !IsNumber(arg)) throw new FormatException($"\"{arg}\" can't be an argument");
                    leaf.Args.Add(arg);
                    if (Peek == ",") { _pos++; continue; }
                    break;
                }
            }
            Expect(")");
        }

        // Campaign totals carry their stat in the keyword: CampaignKills → Kills.
        if (condition == CampaignTotalCondition.Instance) leaf.Args.Insert(0, keyword.Substring(8));

        if (Peek != null && IsComparison(Peek))
        {
            leaf.Op = Next();
            string number = Next();
            if (!float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out leaf.Value))
                throw new FormatException($"\"{leaf.Op}\" needs a number after it, not \"{number}\"");
        }

        string problem = condition.Validate(leaf.Args, leaf.Op);
        if (problem != null) throw new FormatException($"{keyword}: {problem}");

        _leaves.Add(leaf);
        return leaf;
    }

    private static bool IsComparison(string t) => t is ">=" or "<=" or ">" or "<" or "==" or "!=";
    private static bool IsWord(string t)   => t.Length > 0 && (char.IsLetter(t[0]) || t[0] == '_' || char.IsDigit(t[0])) && !IsNumber(t);
    private static bool IsNumber(string t) => float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    /// <summary>Splits a rule into words, numbers and symbols. Words may start with a digit (01U).</summary>
    private static List<string> Tokenise(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
            {
                var sb = new StringBuilder();
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    sb.Append(text[i++]);
                tokens.Add(sb.ToString());
                continue;
            }

            if ((c == '>' || c == '<' || c == '=' || c == '!') && i + 1 < text.Length && text[i + 1] == '=')
            {
                tokens.Add(text.Substring(i, 2));
                i += 2;
                continue;
            }
            if (c == '&' && i + 1 < text.Length && text[i + 1] == '&') { tokens.Add("&"); i += 2; continue; }
            if (c == '|' && i + 1 < text.Length && text[i + 1] == '|') { tokens.Add("|"); i += 2; continue; }
            if ("()&|!,<>".IndexOf(c) >= 0) { tokens.Add(c.ToString()); i++; continue; }

            throw new FormatException($"unexpected character '{c}'");
        }
        return tokens;
    }
}

// ── Conditions ─────────────────────────────────────────────────────────

/// <summary>Complete(M04): that mission has been won in this playthrough.</summary>
[UnlockCondition("Complete")]
public class CompleteCondition : UnlockCondition
{
    public override string Validate(IReadOnlyList<string> args, string op) =>
        args.Count != 1 ? "needs one mission id, e.g. Complete(M04)" :
        op != null      ? "takes no comparison" : null;

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state) =>
        state.IsComplete(args[0]);

    public override IEnumerable<string> MissionsReferenced(IReadOnlyList<string> args) { yield return args[0]; }
}

/// <summary>Collected(M04, GoldenFleece): an item was found and kept (the mission won) in that mission.</summary>
[UnlockCondition("Collected")]
public class CollectedCondition : UnlockCondition
{
    public override string Validate(IReadOnlyList<string> args, string op) =>
        args.Count != 2 ? "needs a mission id and an item, e.g. Collected(M04, GoldenFleece)" :
        op != null      ? "takes no comparison" : null;

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state) =>
        state.HasCollected(args[0], args[1]);

    public override IEnumerable<string> MissionsReferenced(IReadOnlyList<string> args) { yield return args[0]; }
}

/// <summary>
/// Best(M07, MinionsLost) &lt;= 0: the best result any attempt at the mission
/// recorded for a stat. With &lt; or &lt;= the lowest is used, with &gt; or &gt;=
/// the highest — so it reads "has any attempt managed this?".
/// </summary>
[UnlockCondition("Best")]
public class BestCondition : UnlockCondition
{
    public override string Validate(IReadOnlyList<string> args, string op) =>
        args.Count != 2 ? "needs a mission id and a stat, e.g. Best(M07, MinionsLost) <= 0" :
        op is not (">=" or "<=" or ">" or "<") ? "needs a comparison: >=, <=, > or <" : null;

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state)
    {
        bool lowest = op == "<" || op == "<=";
        return state.TryBest(args[0], args[1], lowest, out float best) && Compare(best, op, value);
    }

    public override IEnumerable<string> MissionsReferenced(IReadOnlyList<string> args) { yield return args[0]; }
    public override IEnumerable<string> StatsReferenced(IReadOnlyList<string> args)    { yield return args[1]; }
}

/// <summary>CompleteCampaign(01U): that whole campaign has been finished, in any playthrough.</summary>
[UnlockCondition("CompleteCampaign")]
public class CompleteCampaignCondition : UnlockCondition
{
    public override string Validate(IReadOnlyList<string> args, string op) =>
        args.Count != 1 ? "needs one campaign id, e.g. CompleteCampaign(01U)" :
        op != null      ? "takes no comparison" : null;

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state) =>
        state.IsCampaignComplete(args[0]);
}

/// <summary>
/// Campaign&lt;Stat&gt; &gt;= N, e.g. CampaignKills &gt;= 500: a stat summed over
/// every mission played this campaign. Not tagged — UnlockCondition.Find
/// hands it any keyword starting with "Campaign" that isn't otherwise taken.
/// </summary>
public class CampaignTotalCondition : UnlockCondition
{
    public static readonly CampaignTotalCondition Instance = new();

    public override string Validate(IReadOnlyList<string> args, string op) =>
        args.Count != 1 ? "takes no brackets, e.g. CampaignKills >= 500" :
        op == null      ? "needs a comparison, e.g. CampaignKills >= 500" : null;

    public override bool Test(IReadOnlyList<string> args, string op, float value, IUnlockState state) =>
        Compare(state.Total(args[0]), op, value);

    public override IEnumerable<string> StatsReferenced(IReadOnlyList<string> args) { yield return args[0]; }
}
