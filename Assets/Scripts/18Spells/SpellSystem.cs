using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The rules every spell shares, for every faction: which spells exist (the
/// SpellCatalogue from the SpellData workbook), what they cost, where they
/// may be cast, cooldowns, and paying. SpellCaster turns the local player's
/// mouse into casts; an AI would call TryCast directly.
///
/// A cast is checked in full before anything happens — effect, cooldown,
/// where it may be cast, the target, the gold — and paid for only once the
/// effect has worked, so a failed cast costs nothing. Short of gold, the
/// caster hears "NotEnoughGold".
///
/// Scene setup: none — GameManager2D adds one.
/// </summary>
public class SpellSystem : MonoBehaviour
{
    public static SpellSystem Instance { get; private set; }

    [Tooltip("Empty = the catalogue in Resources built by Sunder > Import Spell Data.")]
    [SerializeField] private SpellCatalogue catalogue;

    private readonly List<SpellDefinition> _spells = new();
    // Faction → spell id → Time.time it's ready again.
    private readonly Dictionary<FactionID, Dictionary<string, float>> _readyAt = new();

    public IReadOnlyList<SpellDefinition> Spells => _spells;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        LoadCatalogue();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void LoadCatalogue()
    {
        if (catalogue == null) catalogue = Resources.Load<SpellCatalogue>(SpellCatalogue.ResourcePath);
        _spells.Clear();
        if (catalogue != null && catalogue.spells.Count > 0) _spells.AddRange(catalogue.spells);
        else
        {
            _spells.AddRange(SpellCatalogue.BuiltIn());
            Debug.Log("[SpellSystem] No spell catalogue yet — using built-in Summon Worker, Heal and Smite. " +
                      "Run Sunder > Import Spell Data to use the SpellData workbook.");
        }
    }

    // ── Queries ────────────────────────────────────────────────────────

    public SpellDefinition Find(string id)
    {
        foreach (var spell in _spells)
            if (string.Equals(spell.id, id, System.StringComparison.OrdinalIgnoreCase)) return spell;
        return null;
    }

    /// <summary>The spell's effect script, or null if it hasn't been written yet.</summary>
    public static SpellEffect EffectFor(SpellDefinition spell) =>
        spell != null ? SpellEffect.Find(spell.EffectId) : null;

    public int CostOf(FactionID caster, SpellDefinition spell, int level)
    {
        var effect = EffectFor(spell);
        return effect != null ? Mathf.Max(0, effect.Cost(caster, spell, spell.Clamp(level))) : spell.CostAt(level);
    }

    public float CooldownLeft(FactionID caster, SpellDefinition spell) =>
        _readyAt.TryGetValue(caster, out var byId) && byId.TryGetValue(spell.id, out float t)
            ? Mathf.Max(0f, t - Time.time) : 0f;

    /// <summary>
    /// Whether the target can take the spell — everything but the gold.
    /// False for a spell with no effect script, one cooling down, or a target
    /// outside where the spell may be cast.
    /// </summary>
    public bool CanCast(in SpellTarget target)
    {
        var effect = EffectFor(target.Spell);
        if (effect == null) return false;
        if (CooldownLeft(target.Caster, target.Spell) > 0f) return false;
        if (!InCastRange(target)) return false;
        return effect.CanTarget(target);
    }

    private static bool InCastRange(in SpellTarget target)
    {
        switch (target.Spell.castOn)
        {
            case SpellCastOn.OwnTerritory:
                return target.Cell != null && target.Cell.Owner == target.Caster;
            case SpellCastOn.Visible:
                if (target.Spell.targeting == SpellTargeting.None) return true;
                return target.Cell != null &&
                       (FogOfWar.Instance == null || FogOfWar.Instance.IsVisible(target.Caster, target.Cell));
            default:
                return true;
        }
    }

    // ── Casting ────────────────────────────────────────────────────────

    /// <summary>
    /// Casts if everything allows it, and pays. Short of gold, the caster
    /// hears "NotEnoughGold". Returns true if the spell was cast.
    /// </summary>
    public bool TryCast(SpellTarget target)
    {
        if (target.Spell == null) return false;
        target.Level = target.Spell.Clamp(target.Level);
        if (!CanCast(target)) return false;

        var wallet = GameManager2D.Instance != null ? GameManager2D.Instance.GetWallet(target.Caster) : null;
        int cost   = CostOf(target.Caster, target.Spell, target.Level);
        if (cost > 0 && (wallet == null || wallet.Gold < cost))
        {
            Announcer.Announce(target.Caster, "NotEnoughGold");
            return false;
        }

        // Paid once it has worked: gold can't simply be handed back, as it
        // may have come from the reserve.
        if (!EffectFor(target.Spell).Cast(target)) return false;
        if (cost > 0) wallet.TrySpend(cost);

        if (target.Spell.cooldown > 0f)
        {
            if (!_readyAt.TryGetValue(target.Caster, out var byId)) _readyAt[target.Caster] = byId = new();
            byId[target.Spell.id] = Time.time + target.Spell.cooldown;
        }
        return true;
    }

    /// <summary>True if the caster has the gold for this spell at this level.</summary>
    public bool CanAfford(FactionID caster, SpellDefinition spell, int level)
    {
        int cost = CostOf(caster, spell, level);
        if (cost <= 0) return true;
        var wallet = GameManager2D.Instance != null ? GameManager2D.Instance.GetWallet(caster) : null;
        return wallet != null && wallet.Gold >= cost;
    }

    // ── Save / load ────────────────────────────────────────────────────

    /// <summary>Cooldowns still running, as seconds left.</summary>
    public List<SpellCooldownSaveData> Capture()
    {
        var list = new List<SpellCooldownSaveData>();
        foreach (var pair in _readyAt)
            foreach (var spell in pair.Value)
            {
                float left = spell.Value - Time.time;
                if (left > 0f) list.Add(new SpellCooldownSaveData { faction = pair.Key, spell = spell.Key, secondsLeft = left });
            }
        return list;
    }

    public void Restore(List<SpellCooldownSaveData> saved)
    {
        _readyAt.Clear();
        if (saved == null) return;
        foreach (var s in saved)
        {
            if (!_readyAt.TryGetValue(s.faction, out var byId)) _readyAt[s.faction] = byId = new();
            byId[s.spell] = Time.time + s.secondsLeft;
        }
    }
}

[System.Serializable]
public class SpellCooldownSaveData
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID faction;
    public string    spell;
    public float     secondsLeft;
}
