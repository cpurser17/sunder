using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

/// <summary>
/// Turns the MinionData workbook into plain records, validating as it goes.
/// Knows nothing about assets — MinionDataImporter writes the records into
/// ScriptableObjects. Problems never stop the parse: each becomes a warning
/// naming the sheet and row, and a sensible default is used, so one typo
/// doesn't block importing every other minion.
///
/// Sheets are found by name/suffix, so any number of factions work, split
/// however is most readable:
///   Stats, DamageTypes, Abilities   — shared lookups
///   *_Data      one row per minion (FactionID + MinionID is the key)
///   *_Levels    one row per minion per stat: Min, Max, Curve, Skew
///   *_Relations minion × minion feelings grid, one per faction
/// Columns are matched by header text, never position.
/// </summary>
public class MinionWorkbookParser
{
    // ── Records ────────────────────────────────────────────────────────

    public class StatInfo
    {
        public float Default;
        public bool  WholeNumber;
    }

    public class AbilityRecord
    {
        public string Id, DisplayName, Description, StatusEffect;
        public AbilityDefinition.AbilityKind      Kind;
        public AbilityDefinition.AbilityTargeting Targeting;
        public AbilityDefinition.AbilityDelivery  Delivery;
        public DamageType? DamageType;
        public float BasePower, ScalingFactor, Range, AreaRadius, Cooldown, CastTime;
        public MinionStat ScalingStat;
        public float StatusChance, StatusDuration, StatusMagnitude;
    }

    public class MinionRecord
    {
        public string FactionId, MinionId, DisplayName, Location;
        public MinionDefinition.MinionStance Stance;
        public int Tier;
        public TraversalCapability Movement;
        public List<MinionDefinition.DamageMultiplier> DamageTaken = new();
        public float ResearchPreference, TrainPreference, BuildPreference, PrayPreference, TorturePreference;
        public List<(int GainLevel, string AbilityId)> Abilities = new();
        public bool  Summonable;
        public bool  CanDoWorkerJobs;
        public float SummonWeight;
        public int   PopulationCost;
        public List<MinionDefinition.RoomRequirement> RequiredRooms = new();
        public List<string> RequiredResearch = new();
        public List<StatCurve> Stats = new();
        public List<(string OtherMinionId, float Feeling)> Relations = new();
        /// <summary>Columns not handled above, keyed by header — set onto same-named MinionDefinition fields.</summary>
        public Dictionary<string, string> Extras = new(StringComparer.OrdinalIgnoreCase);

        public string Key => MinionKey(FactionId, MinionId);
    }

    public static string MinionKey(string factionId, string minionId) => $"{factionId}_{minionId}";

    // ── Results ────────────────────────────────────────────────────────

    public int MaxLevel = 10;
    public readonly Dictionary<MinionStat, StatInfo> Stats = new();
    public readonly List<AbilityRecord> Abilities = new();
    public readonly List<MinionRecord>  Minions   = new();
    public readonly List<string> Warnings = new();
    public int SkippedBlankRows;

    // Headers the Data sheets handle explicitly; anything else is an "extra".
    private static readonly HashSet<string> KnownDataHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "FactionID", "MinionID", "DisplayName", "Stance", "Movement",
        "ResearchPreference", "TrainPreference", "BuildPreference", "PrayPreference", "TorturePreference",
        "Gain1", "Ability1", "Gain2", "Ability2", "Gain3", "Ability3", "Gain4", "Ability4", "Gain5", "Ability5",
        "Summonable", "WorkerJobs", "SummonWeight", "PopulationCost", "RequiredRooms", "MinRoomTiles", "RequiredResearch",
    };

    private static readonly Regex TierPattern = new(@"^T(\d+)", RegexOptions.IgnoreCase);

    public static MinionWorkbookParser Parse(XlsxReader book)
    {
        var p = new MinionWorkbookParser();
        p.ReadMaxLevel(book);
        p.ReadStats(book);
        p.ReadDamageTypes(book);
        p.ReadAbilities(book);
        foreach (var sheet in SheetsEndingWith(book, "_Data"))      p.ReadDataSheet(sheet);
        foreach (var sheet in SheetsEndingWith(book, "_Levels"))    p.ReadLevelsSheet(sheet);
        p.FillMissingStats();
        foreach (var sheet in SheetsEndingWith(book, "_Relations")) p.ReadRelationsSheet(sheet);
        p.CheckAbilityReferences();
        p.CheckWorkers();
        return p;
    }

    private static IEnumerable<XlsxReader.Sheet> SheetsEndingWith(XlsxReader book, string suffix) =>
        book.Sheets.Where(s => s.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private void Warn(XlsxReader.Row row, string message) =>
        Warnings.Add(row != null ? $"{row.SheetName} row {row.ExcelRow}: {message}" : message);

    // ── Lookup sheets ──────────────────────────────────────────────────

    private void ReadMaxLevel(XlsxReader book)
    {
        string v = book.GetDefinedValue("MaxLevel");
        if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) && max >= 1)
            MaxLevel = max;
        else if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 1)
            MaxLevel = (int)d;
        else
            Warnings.Add($"No MaxLevel named cell found (LevelFormula sheet) — using {MaxLevel}.");
    }

    private void ReadStats(XlsxReader book)
    {
        var sheet = book.GetSheet("Stats");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "StatID") : null;
        if (table == null)
            Warnings.Add("No Stats sheet (with a StatID header) — every stat defaults to 0 and is not rounded.");
        else
            foreach (var row in table.Rows)
            {
                if (!TryEnum(row["StatID"], out MinionStat stat))
                {
                    Warn(row, $"Stat '{row["StatID"]}' isn't in MinionStat.cs — add it there for the game to use it.");
                    continue;
                }
                Stats[stat] = new StatInfo
                {
                    Default     = Float(row, "Default", 0f),
                    WholeNumber = Bool(row, "WholeNumber", false),
                };
            }

        foreach (MinionStat stat in Enum.GetValues(typeof(MinionStat)))
            if (!Stats.ContainsKey(stat))
            {
                if (table != null) Warnings.Add($"Stats sheet has no row for {stat} (in MinionStat.cs) — defaulting to 0.");
                Stats[stat] = new StatInfo();
            }
    }

    private void ReadDamageTypes(XlsxReader book)
    {
        var sheet = book.GetSheet("DamageTypes");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "DamageTypeID") : null;
        if (table == null) return;

        var seen = new HashSet<DamageType>();
        foreach (var row in table.Rows)
        {
            if (!TryEnum(row["DamageTypeID"], out DamageType type))
            {
                Warn(row, $"Damage type '{row["DamageTypeID"]}' isn't in DamageType.cs — add it there for the game to use it.");
                continue;
            }
            seen.Add(type);
            string category = row["Category"];
            if (category != null && category.Equals("Physical", StringComparison.OrdinalIgnoreCase) != type.IsPhysical())
                Warn(row, $"{type} is '{category}' here but {(type.IsPhysical() ? "Physical" : "Magical")} in DamageType.IsPhysical — update the code to match.");
        }
        foreach (DamageType type in Enum.GetValues(typeof(DamageType)))
            if (!seen.Contains(type))
                Warnings.Add($"DamageTypes sheet has no row for {type} (in DamageType.cs).");
    }

    private void ReadAbilities(XlsxReader book)
    {
        var sheet = book.GetSheet("Abilities");
        var table = sheet != null ? XlsxReader.ReadTable(sheet, "AbilityID") : null;
        if (table == null) return;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            string id = row["AbilityID"];
            if (id == null) { Warn(row, "Ability has no AbilityID — skipped."); continue; }
            if (!ids.Add(id)) { Warn(row, $"Duplicate AbilityID '{id}' — skipped."); continue; }

            var a = new AbilityRecord
            {
                Id              = id,
                DisplayName     = row["DisplayName"] ?? id,
                Description     = row["Description"] ?? "",
                Kind            = ParseEnum(row, "Kind", AbilityDefinition.AbilityKind.Attack),
                Targeting       = ParseEnum(row, "Targeting", AbilityDefinition.AbilityTargeting.SingleEnemy),
                Delivery        = ParseEnum(row, "Delivery", AbilityDefinition.AbilityDelivery.Melee),
                BasePower       = Float(row, "BasePower", 0f),
                ScalingStat     = ParseEnum(row, "ScalingStat", MinionStat.Strength),
                ScalingFactor   = Float(row, "ScalingFactor", 0f),
                Range           = Float(row, "Range", 1f),
                AreaRadius      = Float(row, "AreaRadius", 0f),
                Cooldown        = Float(row, "Cooldown", 1f),
                CastTime        = Float(row, "CastTime", 0f),
                StatusEffect    = row["StatusEffect"] ?? "",
                StatusChance    = Float(row, "StatusChance", 0f),
                StatusDuration  = Float(row, "StatusDuration", 0f),
                StatusMagnitude = Float(row, "StatusMagnitude", 0f),
            };
            if (row.Has("DamageType")) a.DamageType = ParseEnum(row, "DamageType", DamageType.Slash);
            Abilities.Add(a);
        }
    }

    // ── Per-faction sheets ─────────────────────────────────────────────

    private void ReadDataSheet(XlsxReader.Sheet sheet)
    {
        var table = XlsxReader.ReadTable(sheet, "FactionID");
        if (table == null) { Warnings.Add($"{sheet.Name}: no header row starting with FactionID — sheet skipped."); return; }

        var unusedHeaders = table.Headers.Where(h => !KnownDataHeaders.Contains(h) &&
                                                     !h.StartsWith("Dmg_", StringComparison.OrdinalIgnoreCase) &&
                                                     FindField(typeof(MinionDefinition), h) == null).ToList();
        if (unusedHeaders.Count > 0)
            Warnings.Add($"{sheet.Name}: column(s) {string.Join(", ", unusedHeaders)} have no matching field on MinionDefinition and are ignored.");

        foreach (var row in table.Rows)
        {
            string faction = row["FactionID"], minion = row["MinionID"];
            if (faction == null || minion == null) { Warn(row, "Missing FactionID or MinionID — skipped."); continue; }
            if (!row.Has("DisplayName")) { SkippedBlankRows++; continue; } // placeholder row not filled in yet

            var m = new MinionRecord
            {
                FactionId   = faction,
                MinionId    = minion,
                DisplayName = row["DisplayName"],
                Location    = $"{row.SheetName} row {row.ExcelRow}",
                Stance      = ParseEnum(row, "Stance", MinionDefinition.MinionStance.Fighter),
                Movement    = ParseEnum(row, "Movement", TraversalCapability.LandOnly),
                ResearchPreference = Float(row, "ResearchPreference", 0f),
                TrainPreference    = Float(row, "TrainPreference", 0f),
                BuildPreference    = Float(row, "BuildPreference", 0f),
                PrayPreference     = Float(row, "PrayPreference", 0f),
                TorturePreference  = Float(row, "TorturePreference", 0f),
                SummonWeight       = Float(row, "SummonWeight", 1f),
                PopulationCost     = Int(row, "PopulationCost", 1),
            };
            var tier = TierPattern.Match(minion);
            m.Tier = tier.Success ? int.Parse(tier.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

            // Commanders, workers and the general arrive by other means.
            bool summonableByDefault = m.Stance != MinionDefinition.MinionStance.Commander &&
                                       m.Stance != MinionDefinition.MinionStance.Worker &&
                                       m.Stance != MinionDefinition.MinionStance.General;
            m.Summonable = Bool(row, "Summonable", summonableByDefault);

            // Workers always do worker jobs; anyone else (enemy diggers,
            // helpful utility minions) only if the sheet says so.
            m.CanDoWorkerJobs = Bool(row, "WorkerJobs", false) || m.Stance == MinionDefinition.MinionStance.Worker;

            if (Minions.Any(x => x.Key == m.Key))
            {
                Warn(row, $"Duplicate minion {faction} {minion} — skipped (first one kept).");
                continue;
            }

            foreach (var header in table.Headers)
            {
                if (header.StartsWith("Dmg_", StringComparison.OrdinalIgnoreCase))
                {
                    if (!row.Has(header)) continue;
                    string typeName = header.Substring(4);
                    if (!TryEnum(typeName, out DamageType type))
                    { Warn(row, $"Column {header}: '{typeName}' isn't in DamageType.cs — ignored."); continue; }
                    float mult = Float(row, header, 1f);
                    if (!NearlyEqual(mult, 1f))
                        m.DamageTaken.Add(new MinionDefinition.DamageMultiplier { type = type, multiplier = mult });
                }
                else if (!KnownDataHeaders.Contains(header) && row.Has(header))
                    m.Extras[header] = row[header];
            }

            for (int i = 1; i <= 5; i++)
            {
                string ability = row[$"Ability{i}"];
                if (ability == null) continue;
                if (!row.Has($"Gain{i}")) Warn(row, $"Ability{i} '{ability}' has no Gain{i} level — gained at level 1.");
                m.Abilities.Add((Int(row, $"Gain{i}", 1), ability));
            }

            var rooms    = SplitList(row["RequiredRooms"]);
            var minTiles = SplitList(row["MinRoomTiles"]);
            if (minTiles.Count > 0 && minTiles.Count != rooms.Count)
                Warn(row, $"MinRoomTiles has {minTiles.Count} value(s) but RequiredRooms has {rooms.Count} — they pair up in order.");
            for (int i = 0; i < rooms.Count; i++)
            {
                if (!TryEnum(rooms[i], out TileType roomType))
                { Warn(row, $"RequiredRooms: '{rooms[i]}' isn't a TileType — ignored."); continue; }
                int tiles = 0;
                if (i < minTiles.Count && !int.TryParse(minTiles[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out tiles))
                    Warn(row, $"MinRoomTiles: '{minTiles[i]}' isn't a whole number — any size allowed.");
                m.RequiredRooms.Add(new MinionDefinition.RoomRequirement { roomType = roomType, minTiles = tiles });
            }
            m.RequiredResearch = SplitList(row["RequiredResearch"]);

            Minions.Add(m);
        }
    }

    private void ReadLevelsSheet(XlsxReader.Sheet sheet)
    {
        var table = XlsxReader.ReadTable(sheet, "FactionID");
        if (table == null) { Warnings.Add($"{sheet.Name}: no header row starting with FactionID — sheet skipped."); return; }

        foreach (var row in table.Rows)
        {
            string faction = row["FactionID"], minion = row["MinionID"], statName = row["Stat"];
            if (faction == null || minion == null || statName == null) continue;
            if (!row.Has("Min") && !row.Has("Max")) continue; // not filled in yet

            var m = Minions.FirstOrDefault(x => x.Key == MinionKey(faction, minion));
            if (m == null) { Warn(row, $"{faction} {minion} has no filled-in row on a _Data sheet — ignored."); continue; }
            if (!TryEnum(statName, out MinionStat stat))
            { Warn(row, $"Stat '{statName}' isn't in MinionStat.cs — ignored."); continue; }
            if (!row.Has("Min") || !row.Has("Max"))
                Warn(row, $"{statName} needs both Min and Max — the blank one is treated as equal to the other.");

            float min = Float(row, "Min", Float(row, "Max", 0f));
            float max = Float(row, "Max", min);
            var curve = new StatCurve
            {
                stat        = stat,
                min         = min,
                max         = max,
                curve       = Float(row, "Curve", 0f),
                skew        = Float(row, "Skew", 0f),
                wholeNumber = Stats[stat].WholeNumber,
                authored    = true,
            };

            int existing = m.Stats.FindIndex(s => s.stat == stat);
            if (existing >= 0) { Warn(row, $"{faction} {minion} already has a {stat} row — this one wins."); m.Stats[existing] = curve; }
            else m.Stats.Add(curve);
        }
    }

    /// <summary>Every minion gets every stat; gaps use the Stats sheet default and are reported per minion.</summary>
    private void FillMissingStats()
    {
        var all = (MinionStat[])Enum.GetValues(typeof(MinionStat));
        foreach (var m in Minions)
        {
            var missing = all.Where(s => m.Stats.All(c => c.stat != s)).ToList();
            foreach (var stat in missing)
                m.Stats.Add(new StatCurve { stat = stat, min = Stats[stat].Default, max = Stats[stat].Default,
                                            wholeNumber = Stats[stat].WholeNumber });
            m.Stats.Sort((a, b) => a.stat.CompareTo(b.stat));

            if (missing.Count == all.Length)
                Warnings.Add($"{m.FactionId} {m.MinionId}: no _Levels rows at all — every stat uses its default.");
            else if (missing.Count > 0)
                Warnings.Add($"{m.FactionId} {m.MinionId}: no _Levels row for {string.Join(", ", missing)} — using defaults.");
        }
    }

    private void ReadRelationsSheet(XlsxReader.Sheet sheet)
    {
        string faction = sheet.Name.Substring(0, sheet.Name.Length - "_Relations".Length);
        var table = XlsxReader.ReadTable(sheet, "MinionID");
        if (table == null) { Warnings.Add($"{sheet.Name}: no header row starting with MinionID — sheet skipped."); return; }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            string from = row["MinionID"];
            if (from == null) continue;
            var m = Minions.FirstOrDefault(x => x.Key == MinionKey(faction, from));

            foreach (var other in table.Headers.Skip(1))
            {
                if (!row.Has(other)) continue;
                if (m == null)
                {
                    if (reported.Add(from)) Warn(row, $"{faction} {from} has no filled-in _Data row — its relations are ignored.");
                    break;
                }
                if (Minions.All(x => x.Key != MinionKey(faction, other)))
                {
                    if (reported.Add(other)) Warnings.Add($"{sheet.Name}: {faction} {other} has no filled-in _Data row — relations towards it are ignored.");
                    continue;
                }
                if (string.Equals(other, from, StringComparison.OrdinalIgnoreCase)) continue;
                m.Relations.Add((other, Float(row, other, 0f)));
            }
        }
    }

    /// <summary>Each faction's imp comes from its one Worker row.</summary>
    private void CheckWorkers()
    {
        foreach (var faction in Minions.GroupBy(m => m.FactionId, StringComparer.OrdinalIgnoreCase))
        {
            var workers = faction.Where(m => m.Stance == MinionDefinition.MinionStance.Worker).ToList();
            if (workers.Count == 0)
                Warnings.Add($"{faction.Key}: no Worker row — its imps will use the imp prefab's own token and stats.");
            else if (workers.Count > 1)
                Warnings.Add($"{faction.Key}: {workers.Count} Worker rows ({string.Join(", ", workers.Select(w => w.MinionId))}) — " +
                             $"imps use the first, {workers[0].MinionId}.");
        }
    }

    /// <summary>The Worker row the faction's imps are built from, or null.</summary>
    public MinionRecord WorkerFor(string factionId) =>
        Minions.FirstOrDefault(m => string.Equals(m.FactionId, factionId, StringComparison.OrdinalIgnoreCase) &&
                                    m.Stance == MinionDefinition.MinionStance.Worker);

    private void CheckAbilityReferences()
    {
        var ids = new HashSet<string>(Abilities.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var m in Minions)
            foreach (var (_, id) in m.Abilities)
                if (!ids.Contains(id))
                    Warnings.Add($"{m.Location}: ability '{id}' isn't on the Abilities sheet — it will be left empty.");
    }

    // ── Extras (columns matched to MinionDefinition fields by name) ────

    public static FieldInfo FindField(Type type, string header) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(f => string.Equals(f.Name, header, StringComparison.OrdinalIgnoreCase) && IsSimple(f.FieldType));

    private static bool IsSimple(Type t) =>
        t == typeof(string) || t == typeof(int) || t == typeof(float) || t == typeof(bool) || t.IsEnum;

    /// <summary>Sets same-named simple fields (int/float/bool/string/enum) from extra columns. Returns problems.</summary>
    public static List<string> ApplyExtras(object target, MinionRecord record)
    {
        var problems = new List<string>();
        foreach (var pair in record.Extras)
        {
            var field = FindField(target.GetType(), pair.Key);
            if (field == null) continue; // already reported once per sheet
            if (TryConvert(pair.Value, field.FieldType, out object value)) field.SetValue(target, value);
            else problems.Add($"{record.Location}: {pair.Key} '{pair.Value}' isn't a valid {field.FieldType.Name} — left unchanged.");
        }
        return problems;
    }

    private static bool TryConvert(string text, Type type, out object value)
    {
        value = null;
        if (type == typeof(string)) { value = text; return true; }
        if (type == typeof(float) && TryFloat(text, out float f)) { value = f; return true; }
        if (type == typeof(int) && TryFloat(text, out float i) && Math.Abs(i - Math.Round(i)) < 1e-4) { value = (int)Math.Round(i); return true; }
        if (type == typeof(bool) && TryBool(text, out bool b)) { value = b; return true; }
        if (type.IsEnum)
        {
            var name = Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name != null) { value = Enum.Parse(type, name); return true; }
        }
        return false;
    }

    // ── Cell parsing helpers ───────────────────────────────────────────

    private float Float(XlsxReader.Row row, string header, float fallback)
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryFloat(text, out float v)) return v;
        Warn(row, $"{header} '{text}' isn't a number — using {fallback.ToString(CultureInfo.InvariantCulture)}.");
        return fallback;
    }

    private int Int(XlsxReader.Row row, string header, int fallback)
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryFloat(text, out float v) && Math.Abs(v - Math.Round(v)) < 1e-4) return (int)Math.Round(v);
        Warn(row, $"{header} '{text}' isn't a whole number — using {fallback}.");
        return fallback;
    }

    private bool Bool(XlsxReader.Row row, string header, bool fallback)
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryBool(text, out bool v)) return v;
        Warn(row, $"{header} '{text}' isn't TRUE or FALSE — using {fallback.ToString().ToUpperInvariant()}.");
        return fallback;
    }

    private T ParseEnum<T>(XlsxReader.Row row, string header, T fallback) where T : struct, Enum
    {
        string text = row[header];
        if (text == null) return fallback;
        if (TryEnum(text, out T v)) return v;
        Warn(row, $"{header} '{text}' isn't one of: {string.Join(", ", System.Enum.GetNames(typeof(T)))} — using {fallback}.");
        return fallback;
    }

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        // Names only — Enum.TryParse would also accept "3" or "1,2".
        string name = System.Enum.GetNames(typeof(T)).FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
        return name != null && System.Enum.TryParse(name, out value);
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryBool(string text, out bool value)
    {
        switch (text.Trim().ToUpperInvariant())
        {
            case "TRUE": case "YES": case "Y": case "1": value = true;  return true;
            case "FALSE": case "NO": case "N": case "0": value = false; return true;
            default: value = false; return false;
        }
    }

    private static List<string> SplitList(string text) =>
        text == null ? new List<string>()
                     : text.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                           .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static bool NearlyEqual(float a, float b) => Math.Abs(a - b) < 1e-6f;
}
