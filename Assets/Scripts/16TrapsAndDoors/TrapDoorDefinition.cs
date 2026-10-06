using UnityEngine;

/// <summary>Whether a TrapDoorDefinition is a trap or a door. Append only.</summary>
public enum TrapDoorKind { Trap, Door }

/// <summary>
/// One trap or door type. Placeholder content for now: identity and how it
/// looks in the level editor. Behaviour (triggering, locking, HP, Workshop
/// build cost…) hangs off here once traps and doors are fleshed out.
///
/// A placed trap or door has no owner of its own in the editor — it always
/// belongs to whoever owns the tile under it (see TrapDoorPlacement).
/// </summary>
[CreateAssetMenu(menuName = "Dungeon2D/TrapDoorDefinition", fileName = "NewTrapDoorDefinition")]
public class TrapDoorDefinition : ScriptableObject
{
    [Header("Identity")]
    public TrapDoorKind kind;
    [Tooltip("Stable id referenced by TrapDoorPlacement.typeId in level files. " +
             "Not the display name — renaming this breaks existing level files.")]
    public string typeId;
    public string displayName;
    [TextArea] public string description;

    [Header("Level editor")]
    [Tooltip("Shown on the marker and palette. Empty = a block of Editor Colour.")]
    public Sprite icon;
    public Color editorColour = Color.grey;
}
