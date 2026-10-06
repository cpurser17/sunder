using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Holds every TrapDoorDefinition with lookup by kind and typeId — same
/// pattern as TileRegistry and FactionRegistry.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/TrapDoorRegistry", fileName = "TrapDoorRegistry")]
public class TrapDoorRegistry : ScriptableObject
{
    [SerializeField] private List<TrapDoorDefinition> definitions = new();

    /// <summary>Every definition, in the order listed on the asset.</summary>
    public IReadOnlyList<TrapDoorDefinition> Definitions => definitions;

    public TrapDoorDefinition GetDefinition(TrapDoorKind kind, string typeId)
    {
        if (string.IsNullOrEmpty(typeId)) return null;
        foreach (var def in definitions)
            if (def != null && def.kind == kind &&
                string.Equals(def.typeId, typeId, System.StringComparison.OrdinalIgnoreCase))
                return def;
        return null;
    }
}
