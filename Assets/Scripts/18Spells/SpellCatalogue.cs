using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every spell — generated from the SpellData workbook by Sunder > Import
/// Spell Data (SpellDataImporter), never edited by hand. Lives in a
/// Resources folder, so SpellSystem finds it with no wiring.
/// </summary>
[CreateAssetMenu(menuName = "Sunder/Spell Catalogue", fileName = "SpellCatalogue")]
public class SpellCatalogue : ScriptableObject
{
    /// <summary>Resources path SpellSystem loads from when none is assigned.</summary>
    public const string ResourcePath = "Spells/SpellCatalogue";

    public List<SpellDefinition> spells = new();

    /// <summary>
    /// Stand-ins used until the workbook has been imported, so Summon Worker
    /// keeps working in a fresh project. Same numbers as the starter workbook.
    /// </summary>
    public static List<SpellDefinition> BuiltIn() => new()
    {
        new SpellDefinition
        {
            id = "SummonWorker", displayName = "Summon Worker", targeting = SpellTargeting.Tile,
            castOn = SpellCastOn.Visible, cost = 25, power = 25, buttonRow = 1, buttonColumn = 1,
        },
        new SpellDefinition
        {
            id = "Heal", displayName = "Heal", targeting = SpellTargeting.OwnMinion,
            castOn = SpellCastOn.OwnTerritory, cost = 50, costPerLevel = 50, maxLevel = 5,
            chargeSeconds = 0.5f, power = 30, powerPerLevel = 30, buttonRow = 1, buttonColumn = 3,
        },
        new SpellDefinition
        {
            id = "Smite", displayName = "Smite", targeting = SpellTargeting.EnemyMinion,
            castOn = SpellCastOn.Visible, cost = 100, costPerLevel = 100, maxLevel = 5,
            chargeSeconds = 0.5f, power = 20, powerPerLevel = 20, damageType = DamageType.Lightning,
            buttonRow = 2, buttonColumn = 2,
        },
    };
}
