using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A group of related FactionDefinitions — e.g. an Underworld faction and the
/// human culture that worshipped it. Purely a grouping for later AI
/// relationship logic (a human faction might default to hating its own
/// pantheon's Underworld counterpart more than an unrelated one) — nothing
/// currently reads this at runtime, it exists so that content can be
/// authored with the grouping in place rather than retrofitted later.
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/Pantheon", fileName = "NewPantheon")]
public class Pantheon : ScriptableObject
{
    [Tooltip("Stable id. Not the display name — renaming this breaks anything that " +
             "references it by id in the future.")]
    public string pantheonId;
    public string displayName;
    [TextArea] public string description;

    [Tooltip("Every FactionDefinition belonging to this pantheon.")]
    public List<FactionDefinition> members = new();
}
