using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds every FactionDefinition and provides O(1) lookup by its
/// factionContentId. Same pattern as TileRegistry. GameManager2D resolves
/// each active seat's FactionSetup.contentFactionId through this to get the
/// FactionDefinition that seat is actually playing.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/FactionRegistry", fileName = "FactionRegistry")]
public class FactionRegistry : ScriptableObject
{
    [SerializeField] private List<FactionDefinition> definitions = new();

    private Dictionary<string, FactionDefinition> _map;

    public void Initialise()
    {
        _map = new Dictionary<string, FactionDefinition>();
        foreach (var def in definitions)
        {
            if (def == null || string.IsNullOrEmpty(def.factionContentId)) continue;
            _map[def.factionContentId] = def;
        }
    }

    /// <summary>Every definition, in the order listed on the asset.</summary>
    public IReadOnlyList<FactionDefinition> Definitions => definitions;

    /// <summary>The minion with this id in this content faction, or null.</summary>
    public MinionDefinition FindMinion(string factionContentId, string minionId) =>
        GetDefinition(factionContentId)?.FindMinion(minionId);

    public FactionDefinition GetDefinition(string factionContentId)
    {
        if (string.IsNullOrEmpty(factionContentId)) return null;
        return _map != null && _map.TryGetValue(factionContentId, out var def) ? def : null;
    }
}
