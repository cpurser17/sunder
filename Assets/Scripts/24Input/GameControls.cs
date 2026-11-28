using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Every keyboard control in the game, in one place, on the Input System.
/// Scripts read them from here, e.g. GameControls.General.Pause.WasPressedThisFrame(),
/// never with KeyCode or Input.GetKey.
///
/// Actions are grouped by where they apply (General, Camera, Gameplay, HUD,
/// LevelEditor), each with a readable label for the future Settings screen,
/// which can rebind any of them: the Input System's interactive rebinding
/// changes an action's binding, and Save() keeps the player's changes in
/// the settings file (GameSettings). Two actions in
/// the same group shouldn't share a key — Clashes() lists any that do.
///
/// Mouse buttons aren't rebindable — they're read through MouseButtons,
/// which can swap left and right for left-handed players. The pointer
/// position, mouse movement and wheel are read directly where they're used.
///
/// To add a control: add an action to the right group below, with a label
/// and default keys, then read it where it's needed.
/// </summary>
public static class GameControls
{
    public static InputActionAsset Asset { get; private set; }

    private static readonly Dictionary<InputAction, string> _labels = new();
    private static readonly Dictionary<InputAction, bool>   _rebindable = new();

    // ── General ────────────────────────────────────────────────────────

    public static class General
    {
        public static InputAction Back, Pause, Research, Minimap, RevealFog;
    }

    // ── Camera ─────────────────────────────────────────────────────────

    public static class Camera
    {
        /// <summary>Vector2: x = right, y = forward.</summary>
        public static InputAction Move, Faster, RotateLeft, RotateRight, OrbitModifier;
    }

    // ── Gameplay ───────────────────────────────────────────────────────

    public static class Gameplay
    {
        /// <summary>Held while building: sell instead.</summary>
        public static InputAction SellModifier;
        /// <summary>Held with right-click while carrying minions: drop them all.</summary>
        public static InputAction DropAllModifier;
        /// <summary>Held while clicking a research node: research it next.</summary>
        public static InputAction ResearchNextModifier;
    }

    // ── HUD footer ─────────────────────────────────────────────────────

    public static class Hud
    {
        public static InputAction RoomsTab, SpellsTab, TrapsDoorsTab, MinionsTab, Minimise;
        /// <summary>Slot digits 1–9 (index 0 = 1) for the tab, row, column sequence.</summary>
        public static readonly InputAction[] Slots = new InputAction[9];
    }

    // ── Level editor ───────────────────────────────────────────────────

    public static class LevelEditor
    {
        public static InputAction Undo, Redo, Save, ToggleRemove, BrushSmaller, BrushLarger, FrameLevel;
        /// <summary>Vector2: x = right, y = up the map.</summary>
        public static InputAction Pan;
        /// <summary>Ctrl/Cmd — held for shortcuts, so the camera stops panning meanwhile.</summary>
        public static InputAction ShortcutModifier;
        /// <summary>The tools in toolbar order: Tiles, Heart, Minions, Traps, Owner, Gates.</summary>
        public static readonly InputAction[] Tools = new InputAction[6];
    }

    // ── Building ───────────────────────────────────────────────────────

    static GameControls() => Build();

    /// <summary>Forces the controls to be set up (they are on first use anyway).</summary>
    public static void EnsureBuilt() { }

    /// <summary>
    /// Builds the controls afresh, with the player's saved keys, and forgets
    /// any menu or rebind state. Run at the start of every play session
    /// (GameSettings): the project enters Play mode without reloading
    /// scripts, so this class's state would otherwise carry over — and the
    /// Input System switches every action off when Play mode ends.
    /// </summary>
    public static void Rebuild()
    {
        if (Asset != null)
        {
            Asset.Disable();
            Object.Destroy(Asset);
        }
        _labels.Clear();
        _rebindable.Clear();
        _modals = 0;
        _backTakenFrame = _rebindEndedFrame = -1;
        Rebinding = false;
        Build();
    }

    private static void Build()
    {
        Asset = ScriptableObject.CreateInstance<InputActionAsset>();
        Asset.name = "GameControls";

        var general = Asset.AddActionMap("General");
        General.Back      = Button(general, "Back", "Back / cancel / close", "<Keyboard>/escape");
        General.Pause     = Button(general, "Pause", "Pause", "<Keyboard>/p");
        General.Research  = Button(general, "Research", "Research screen", "<Keyboard>/r");
        General.Minimap   = Button(general, "Minimap", "Full-screen map", "<Keyboard>/capsLock");
        General.RevealFog = Button(general, "RevealFog", "Reveal the map (debug builds only)", "<Keyboard>/f9");

        var camera = Asset.AddActionMap("Camera");
        Camera.Move          = Vector(camera, "Move", "Move the camera", "<Keyboard>/w", "<Keyboard>/s", "<Keyboard>/a", "<Keyboard>/d");
        Camera.Faster        = Button(camera, "Faster", "Move the camera faster (hold)", "<Keyboard>/leftShift");
        Camera.RotateLeft    = Button(camera, "RotateLeft", "Turn the camera left", "<Keyboard>/q");
        Camera.RotateRight   = Button(camera, "RotateRight", "Turn the camera right", "<Keyboard>/e");
        Camera.OrbitModifier = Button(camera, "OrbitModifier", "Orbit with right mouse (hold)", "<Keyboard>/ctrl");

        var gameplay = Asset.AddActionMap("Gameplay");
        Gameplay.SellModifier         = Button(gameplay, "SellModifier", "Sell while building (hold)", "<Keyboard>/alt");
        Gameplay.DropAllModifier      = Button(gameplay, "DropAllModifier", "Drop everything held, with right-click (hold)", "<Keyboard>/shift");
        Gameplay.ResearchNextModifier = Button(gameplay, "ResearchNextModifier", "Research next, when clicking a node (hold)", "<Keyboard>/shift");
        Gameplay.ResearchNextModifier.AddBinding("<Keyboard>/ctrl");

        var hud = Asset.AddActionMap("HUD");
        Hud.RoomsTab      = Button(hud, "RoomsTab", "Rooms tab (then row, column)", "<Keyboard>/tab");
        Hud.SpellsTab     = Button(hud, "SpellsTab", "Spells tab (then row, column)", null);
        Hud.TrapsDoorsTab = Button(hud, "TrapsDoorsTab", "Traps & Doors tab (then row, column)", null);
        Hud.MinionsTab    = Button(hud, "MinionsTab", "Minions tab", null);
        Hud.Minimise      = Button(hud, "Minimise", "Fold the HUD away", null);
        for (int d = 1; d <= 9; d++)
        {
            Hud.Slots[d - 1] = Button(hud, $"Slot{d}", $"Slot {d}", $"<Keyboard>/{d}");
            Hud.Slots[d - 1].AddBinding($"<Keyboard>/numpad{d}");
        }

        var editor = Asset.AddActionMap("LevelEditor");
        LevelEditor.Undo = Button(editor, "Undo", "Undo", null);
        Chord(LevelEditor.Undo, "<Keyboard>/ctrl", "<Keyboard>/z");
        Chord(LevelEditor.Undo, "<Keyboard>/leftMeta", "<Keyboard>/z");
        LevelEditor.Redo = Button(editor, "Redo", "Redo", null);
        Chord(LevelEditor.Redo, "<Keyboard>/ctrl", "<Keyboard>/y");
        Chord(LevelEditor.Redo, "<Keyboard>/ctrl", "<Keyboard>/shift", "<Keyboard>/z");
        Chord(LevelEditor.Redo, "<Keyboard>/leftMeta", "<Keyboard>/shift", "<Keyboard>/z");
        LevelEditor.Save = Button(editor, "Save", "Save", null);
        Chord(LevelEditor.Save, "<Keyboard>/ctrl", "<Keyboard>/s");
        Chord(LevelEditor.Save, "<Keyboard>/leftMeta", "<Keyboard>/s");
        string[] tools = { "Tiles", "Heart", "Minions", "Traps & doors", "Owner", "Gates" };
        for (int i = 0; i < tools.Length; i++)
            LevelEditor.Tools[i] = Button(editor, $"Tool{i + 1}", $"{tools[i]} tool", $"<Keyboard>/{i + 1}");
        LevelEditor.ToggleRemove = Button(editor, "ToggleRemove", "Switch place / remove", "<Keyboard>/r");
        LevelEditor.BrushSmaller = Button(editor, "BrushSmaller", "Smaller brush", "<Keyboard>/leftBracket");
        LevelEditor.BrushLarger  = Button(editor, "BrushLarger", "Bigger brush", "<Keyboard>/rightBracket");
        LevelEditor.FrameLevel   = Button(editor, "FrameLevel", "Frame the whole level", "<Keyboard>/f");
        LevelEditor.Pan = Vector(editor, "Pan", "Pan the camera", "<Keyboard>/w", "<Keyboard>/s", "<Keyboard>/a", "<Keyboard>/d");
        LevelEditor.Pan.AddCompositeBinding("2DVector")
            .With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow")
            .With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
        LevelEditor.ShortcutModifier = Button(editor, "ShortcutModifier", "Shortcut key (hold)", "<Keyboard>/ctrl", rebindable: false);
        LevelEditor.ShortcutModifier.AddBinding("<Keyboard>/leftMeta");
        LevelEditor.ShortcutModifier.AddBinding("<Keyboard>/rightMeta");

        string saved = GameSettings.Current.bindings;
        if (!string.IsNullOrEmpty(saved))
        {
            try { Asset.LoadBindingOverridesFromJson(saved); }
            catch (System.Exception e) { Debug.LogWarning($"[GameControls] Couldn't read saved key bindings — using the defaults. {e.Message}"); }
        }
        Asset.Enable();
    }

    private static InputAction Button(InputActionMap map, string name, string label, string binding, bool rebindable = true)
    {
        var action = map.AddAction(name, InputActionType.Button);
        if (!string.IsNullOrEmpty(binding)) action.AddBinding(binding);
        _labels[action] = label;
        _rebindable[action] = rebindable;
        return action;
    }

    private static InputAction Vector(InputActionMap map, string name, string label, string up, string down, string left, string right)
    {
        var action = map.AddAction(name, InputActionType.Value, expectedControlLayout: "Vector2");
        action.AddCompositeBinding("2DVector").With("Up", up).With("Down", down).With("Left", left).With("Right", right);
        _labels[action] = label;
        _rebindable[action] = true;
        return action;
    }

    /// <summary>A key with one or two keys held first, e.g. Ctrl+Z or Ctrl+Shift+Z.</summary>
    private static void Chord(InputAction action, string modifier, string key) =>
        action.AddCompositeBinding("OneModifier").With("Modifier", modifier).With("Binding", key);

    private static void Chord(InputAction action, string modifier1, string modifier2, string key) =>
        action.AddCompositeBinding("TwoModifiers").With("Modifier1", modifier1).With("Modifier2", modifier2).With("Binding", key);

    // ── For the Settings screen ────────────────────────────────────────

    /// <summary>The control's name in menus, e.g. "Turn the camera left".</summary>
    public static string LabelOf(InputAction action) =>
        action != null && _labels.TryGetValue(action, out string label) ? label : action?.name;

    /// <summary>Whether the player may change the control's keys.</summary>
    public static bool IsRebindable(InputAction action) =>
        action != null && _rebindable.TryGetValue(action, out bool r) && r;

    /// <summary>The control's keys as shown to the player, e.g. "Ctrl+Z" or "W/A/S/D".</summary>
    public static string KeysOf(InputAction action) => action?.GetBindingDisplayString() ?? "";

    /// <summary>Keeps the player's changed keys (in the settings file).</summary>
    public static void Save()
    {
        GameSettings.Current.bindings = Asset.SaveBindingOverridesAsJson();
        GameSettings.Save();
    }

    /// <summary>Back to every default key.</summary>
    public static void ResetToDefaults()
    {
        Asset.RemoveAllBindingOverrides();
        GameSettings.Current.bindings = null;
    }

    /// <summary>True while the Settings screen is waiting for a key to bind — nothing else should react to it.</summary>
    public static bool Rebinding { get; set; }

    // ── Back (Escape) and menus ────────────────────────────────────────
    // Several things answer Escape — putting a spell away, closing the
    // research screen, the in-game menu. Whatever answers first in a frame
    // takes it with TakeBack(), so one press does one thing. The in-game
    // menu asks last (LateUpdate), so it opens only when nothing else used it.

    private static int _backTakenFrame = -1;
    private static int _modals;
    private static int _rebindEndedFrame = -1;

    /// <summary>True (once per press) if Back was pressed this frame and nothing has taken it yet.</summary>
    public static bool TakeBack()
    {
        if (!General.Back.WasPressedThisFrame() || Rebinding) return false;
        if (_backTakenFrame == Time.frameCount || _rebindEndedFrame == Time.frameCount) return false;
        _backTakenFrame = Time.frameCount;
        return true;
    }

    /// <summary>Call when a rebind finishes or is cancelled — the Escape that cancelled it isn't a Back.</summary>
    public static void EndRebinding()
    {
        Rebinding = false;
        _rebindEndedFrame = Time.frameCount;
    }

    /// <summary>A full-screen menu (the in-game menu, Settings) is open: the game's own controls stand down.</summary>
    public static bool ModalOpen => _modals > 0;

    public static void PushModal()
    {
        if (_modals++ == 0) SetGameplayEnabled(false);
    }

    public static void PopModal()
    {
        if (_modals == 0) return;
        if (--_modals == 0) SetGameplayEnabled(true);
    }

    private static void SetGameplayEnabled(bool on)
    {
        foreach (var name in new[] { "Camera", "Gameplay", "HUD", "LevelEditor" })
        {
            var map = Asset.FindActionMap(name);
            if (map == null) continue;
            if (on) map.Enable(); else map.Disable();
        }
        foreach (var a in new[] { General.Pause, General.Research, General.Minimap, General.RevealFog })
            if (on) a.Enable(); else a.Disable();
    }

    /// <summary>Pairs of controls in the same group that share a key (by path).</summary>
    public static List<(InputAction a, InputAction b, string key)> Clashes()
    {
        var clashes = new List<(InputAction, InputAction, string)>();
        foreach (var map in Asset.actionMaps)
        {
            var byKey = new Dictionary<string, InputAction>();
            foreach (var action in map.actions)
            {
                // "Hold" modifiers deliberately share Shift/Ctrl/Alt — they never apply at the same moment.
                if (action.name.EndsWith("Modifier")) continue;
                foreach (var binding in action.bindings)
                {
                    if (binding.isComposite || string.IsNullOrEmpty(binding.effectivePath)) continue;
                    if (byKey.TryGetValue(binding.effectivePath, out var other) && other != action)
                    {
                        // Modifier-style controls deliberately share Shift/Ctrl with chords.
                        if (binding.isPartOfComposite) continue;
                        clashes.Add((other, action, binding.effectivePath));
                    }
                    else byKey[binding.effectivePath] = action;
                }
            }
        }
        return clashes;
    }

    // ── Helpers for the code that reads controls ───────────────────────

    /// <summary>The HUD footer's tab hotkey for a tab, by its name; null if the tab has no control here.</summary>
    public static InputAction TabAction(string tabName) => tabName switch
    {
        "Rooms"         => Hud.RoomsTab,
        "Spells"        => Hud.SpellsTab,
        "Traps & Doors" => Hud.TrapsDoorsTab,
        "Minions"       => Hud.MinionsTab,
        _               => null,
    };

    /// <summary>The slot digit (1–9) pressed this frame, else 0.</summary>
    public static int SlotPressed()
    {
        for (int i = 0; i < Hud.Slots.Length; i++)
            if (Hud.Slots[i].WasPressedThisFrame()) return i + 1;
        return 0;
    }
}

/// <summary>
/// Mouse buttons, by role rather than by side: Primary is normally the left
/// button, Secondary the right. Swap (a future setting) exchanges them for
/// left-handed players. The middle button is always the middle.
/// </summary>
public static class MouseButtons
{
    public static bool Swap;

    private static int P => Swap ? 1 : 0;
    private static int S => Swap ? 0 : 1;

    public static bool PrimaryDown     => Input.GetMouseButtonDown(P);
    public static bool PrimaryHeld     => Input.GetMouseButton(P);
    public static bool PrimaryUp       => Input.GetMouseButtonUp(P);
    public static bool SecondaryDown   => Input.GetMouseButtonDown(S);
    public static bool SecondaryHeld   => Input.GetMouseButton(S);
    public static bool SecondaryUp     => Input.GetMouseButtonUp(S);
    public static bool MiddleDown      => Input.GetMouseButtonDown(2);
    public static bool MiddleHeld      => Input.GetMouseButton(2);
    public static bool MiddleUp        => Input.GetMouseButtonUp(2);
}
