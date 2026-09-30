using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// The Keeper's hand: how the local player's mouse handles minions directly.
///
/// Input
/// -----
///   LMB on a minion         — pick it up. Up to <see cref="capacity"/> held.
///   RMB while holding       — drop the most recently picked-up minion.
///   Shift + RMB             — drop every held minion at once.
///   RMB, hand empty         — slap the minion under the cursor: a little
///                             damage, some anger, a temporary work speed boost.
///   Moving over a minion    — it pauses briefly so it can be grabbed. A
///                             minion walking under a still cursor does not.
///
/// Only the local player's own minions are grabbed or slapped, and minions
/// are only dropped on floor the local player owns that they can stand on
/// safely.
///
/// Sharing the mouse
/// -----------------
/// Dig selection also uses LMB/RMB. Before starting a drag, other click
/// handlers ask <see cref="OwnsMouseButton"/>; while that's true the click is
/// the hand's. The answer is worked out once per frame, so it's the same
/// whichever component's Update runs first.
///
/// The hand stands down entirely — no grabs, slaps, drops or hover pauses —
/// while a HUD buy/sell button is active, a summon placement is pending, the
/// fullscreen minimap is open, or the cursor is over UI. Minions already held
/// stay held and keep following the cursor.
///
/// Scene setup
/// -----------
/// None required: GameManager2D adds one if the scene has none. Add it
/// yourself (anywhere in the Gameplay Scene) to tune it in the Inspector.
/// </summary>
public class KeeperHand : MonoBehaviour
{
    public static KeeperHand Instance { get; private set; }

    /// <summary>Raised whenever a minion is picked up or dropped.</summary>
    public event System.Action OnHeldChanged;

    // ── Target registry ────────────────────────────────────────────────
    private static readonly List<IHandTarget> _targets = new();

    /// <summary>Called by minions from OnEnable.</summary>
    public static void Register(IHandTarget target)
    {
        if (!_targets.Contains(target)) _targets.Add(target);
    }

    /// <summary>Called by minions from OnDisable.</summary>
    public static void Unregister(IHandTarget target) => _targets.Remove(target);

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Dependencies")]
    [Tooltip("Leave empty to use Camera.main.")]
    [SerializeField] private Camera        mainCamera;
    [Tooltip("Leave empty to use GameManager2D's grid.")]
    [SerializeField] private GridManager2D gridManager;

    [Header("Local Player")]
    [SerializeField] private FactionID localPlayer = FactionID.Player;

    [Header("Holding")]
    [Tooltip("Most minions the hand can hold at once.")]
    [SerializeField] private int   capacity   = 8;
    [Tooltip("Height above the grid held minions dangle at, in world units.")]
    [SerializeField] private float heldHeight = 1.5f;
    [Tooltip("Radius of the little cluster held minions hang in, in world units.")]
    [SerializeField] private float heldSpread = 0.2f;

    [Header("Hover")]
    [Tooltip("Extra distance beyond a token's radius that still counts as over it.")]
    [SerializeField] private float hoverPadding        = 0.05f;
    [Tooltip("Seconds a minion pauses when the cursor moves onto it.")]
    [SerializeField] private float hoverPauseDuration  = 0.6f;
    [Tooltip("Pixels the cursor must move in a frame to count as moving. " +
             "Stops sensor jitter pausing minions that walk under a still cursor.")]
    [SerializeField] private float mouseMoveThreshold  = 1f;

    [Header("Dropping")]
    [Tooltip("Radius, in world units, minions dropped together with Shift+RMB " +
             "are spread over so they don't land on one point.")]
    [SerializeField] private float dropAllSpread = 0.2f;

    [Header("Slap")]
    [Tooltip("Damage per slap as a fraction of the minion's max health.")]
    [Range(0f, 1f)]
    [SerializeField] private float slapDamageFraction  = 0.05f;
    [Tooltip("Off: a slap never takes a minion's last point of health.")]
    [SerializeField] private bool  slapCanKill         = false;
    [Tooltip("Anger added per slap, on a 0-1 scale.")]
    [Range(0f, 1f)]
    [SerializeField] private float slapAngerGain       = 0.2f;
    [Tooltip("Work speed multiplier while a slap's boost lasts.")]
    [SerializeField] private float slapWorkSpeedMultiplier = 1.5f;
    [Tooltip("Seconds a slap's work speed boost lasts. Slapping again restarts it.")]
    [SerializeField] private float slapBoostDuration   = 10f;

    // ── Runtime ────────────────────────────────────────────────────────
    // Last element is the most recently picked up — dropped first.
    private readonly List<IHandTarget> _held = new();

    // Per-frame state, computed once by EnsureFrameState.
    private int         _stateFrame = -1;
    private bool        _standDown;
    private IHandTarget _hovered;
    private bool        _claimsLmb;
    private bool        _claimsRmb;

    private Vector3     _lastMousePosition;
    private IHandTarget _lastPaused;

    public int                        Capacity  => capacity;
    public int                        HeldCount => _held.Count;
    public IReadOnlyList<IHandTarget> Held      => _held;

    /// <summary>The minion under the cursor this frame that the hand could grab or slap.</summary>
    public IHandTarget Hovered { get { EnsureFrameState(); return _hovered; } }

    /// <summary>
    /// True if the hand is taking this frame's click of the given button
    /// (0 = LMB, 1 = RMB). Other click handlers check this before acting.
    /// </summary>
    public static bool OwnsMouseButton(int button) =>
        Instance != null && Instance.Claims(button);

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _lastMousePosition = Input.mousePosition;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        ForgetLostMinions();
        EnsureFrameState();
        PauseHoveredMinion();

        if (_standDown) return;

        if (Input.GetMouseButtonDown(0) && _claimsLmb)
        {
            PickUp(_hovered);
        }
        else if (Input.GetMouseButtonDown(1) && _claimsRmb)
        {
            if (_held.Count == 0)            Slap(_hovered);
            else if (ShiftHeld)              DropAll();
            else                             DropMostRecent();
        }
    }

    private void LateUpdate() => PositionHeldMinions();

    private static bool ShiftHeld =>
        Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    // ── Frame state ────────────────────────────────────────────────────

    private bool Claims(int button)
    {
        EnsureFrameState();
        return button switch
        {
            0 => _claimsLmb,
            1 => _claimsRmb,
            _ => false,
        };
    }

    /// <summary>
    /// Works out, once per frame, what the hand is over and which clicks it
    /// takes. Evaluated lazily so the answer is identical for every caller
    /// this frame — including after the hand has acted on it, when a grab
    /// has already changed the held count.
    /// </summary>
    private void EnsureFrameState()
    {
        if (_stateFrame == Time.frameCount) return;
        _stateFrame = Time.frameCount;

        ResolveDependencies();

        _standDown = mainCamera == null || gridManager == null || ShouldStandDown();
        _hovered   = _standDown ? null : FindHovered();
        _claimsLmb = _hovered != null && _held.Count < capacity;
        _claimsRmb = !_standDown && (_held.Count > 0 || _hovered != null);
    }

    private static bool ShouldStandDown()
    {
        if (HUDController2D.Instance != null && HUDController2D.Instance.AnyButtonActive) return true;
        if (ImpSpawner.AnySummonModeActive) return true;
        if (MinimapFullscreen.IsOpen)       return true;
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    private void ResolveDependencies()
    {
        if (mainCamera == null) mainCamera = Camera.main;
        if (gridManager == null && GameManager2D.Instance != null)
            gridManager = GameManager2D.Instance.Grid;
    }

    // ── Hover ──────────────────────────────────────────────────────────

    /// <summary>
    /// The local player's minion whose token the cursor is over, nearest
    /// first. Tokens have no colliders, so each is tested against the cursor
    /// ray where it meets the token's own height — accurate at any camera angle.
    /// </summary>
    private IHandTarget FindHovered()
    {
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        if (Mathf.Abs(ray.direction.y) < 1e-4f) return null;

        IHandTarget best     = null;
        float       bestDist = float.MaxValue;

        foreach (var target in _targets)
        {
            if (!IsGrabbable(target)) continue;

            Vector3 pos = target.transform.position;
            float   t   = (pos.y - ray.origin.y) / ray.direction.y;
            if (t < 0f) continue;

            Vector3 hit = ray.origin + ray.direction * t;
            float   dx  = hit.x - pos.x;
            float   dz  = hit.z - pos.z;
            float   distSq = dx * dx + dz * dz;

            float reach = target.Agent.Radius + hoverPadding;
            if (distSq > reach * reach || distSq >= bestDist) continue;

            best     = target;
            bestDist = distSq;
        }

        return best;
    }

    private bool IsGrabbable(IHandTarget target) =>
        !IsGone(target) && target.IsAlive && !target.IsHeld &&
        target.Faction == localPlayer && target.Agent != null;

    /// <summary>
    /// Pauses a minion when the cursor MOVES onto it, once per visit. One
    /// walking under a still cursor isn't paused, and wiggling the cursor
    /// over the same minion can't hold it indefinitely.
    /// </summary>
    private void PauseHoveredMinion()
    {
        Vector3 mouse = Input.mousePosition;
        bool mouseMoved = (mouse - _lastMousePosition).sqrMagnitude >
                          mouseMoveThreshold * mouseMoveThreshold;
        _lastMousePosition = mouse;

        if (_hovered == null) { _lastPaused = null; return; }
        if (!mouseMoved || _hovered == _lastPaused) return;

        _hovered.Agent.Pause(hoverPauseDuration);
        _lastPaused = _hovered;
    }

    // ── Pick up ────────────────────────────────────────────────────────

    private void PickUp(IHandTarget target)
    {
        if (target == null || _held.Count >= capacity) return;

        target.OnPickedUp();
        _held.Add(target);
        OnHeldChanged?.Invoke();
    }

    // ── Drop ───────────────────────────────────────────────────────────

    private void DropMostRecent()
    {
        if (!TryGetGroundPoint(out Vector3 point)) return;

        var target = _held[_held.Count - 1];
        if (!TryDrop(target, point)) return;

        _held.RemoveAt(_held.Count - 1);
        OnHeldChanged?.Invoke();
    }

    /// <summary>
    /// Drops everything held, most recent first, spread around the cursor so
    /// they don't land on a single point. A minion whose spread spot is
    /// illegal tries the cursor point itself; if that's illegal too (e.g. a
    /// land minion over a bridge gap it can't stand on) it stays in the hand.
    /// </summary>
    private void DropAll()
    {
        if (!TryGetGroundPoint(out Vector3 point)) return;

        int  count   = _held.Count;
        bool changed = false;

        for (int i = count - 1; i >= 0; i--)
        {
            var     target = _held[i];
            Vector3 spot   = point + SpreadOffset(count - 1 - i, count, dropAllSpread);

            if (!TryDrop(target, spot) && !TryDrop(target, point)) continue;

            _held.RemoveAt(i);
            changed = true;
        }

        if (changed) OnHeldChanged?.Invoke();
    }

    private bool TryDrop(IHandTarget target, Vector3 point)
    {
        if (!gridManager.WorldToCell(point, out int x, out int y)) return false;
        var cell = gridManager.GetCell(x, y);
        if (!CanDropOn(target, cell)) return false;

        target.OnDropped(point, cell);
        return true;
    }

    /// <summary>
    /// A minion may be set down on floor the local player owns, that it can
    /// stand on without harm, and that has room for its token.
    /// </summary>
    private bool CanDropOn(IHandTarget target, GridCell cell)
    {
        if (cell == null || cell.Owner != localPlayer) return false;

        var agent = target.Agent;
        if (!TraversalRules.CanOccupy(cell.TileType, agent.Capability, target.Faction)) return false;
        if (TraversalRules.IsHazardousFor(cell.TileType, agent.Capability))              return false;

        var clearance = gridManager.Clearance;
        return clearance == null || clearance.Fits(cell, agent.Capability, agent.Radius);
    }

    // ── Slap ───────────────────────────────────────────────────────────

    private void Slap(IHandTarget target)
    {
        if (target == null) return;

        target.OnSlapped(new HandSlap(slapDamageFraction, slapCanKill, slapAngerGain,
                                      slapWorkSpeedMultiplier, slapBoostDuration));
    }

    // ── Held minions ───────────────────────────────────────────────────

    /// <summary>Hangs held minions in a small cluster under the cursor, newest on top.</summary>
    private void PositionHeldMinions()
    {
        if (_held.Count == 0 || mainCamera == null) return;

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        if (Mathf.Abs(ray.direction.y) < 1e-4f) return;

        float planeY = (gridManager != null ? gridManager.transform.position.y : 0f) + heldHeight;
        float t      = (planeY - ray.origin.y) / ray.direction.y;
        if (t < 0f) return;

        Vector3 anchor = ray.origin + ray.direction * t;

        for (int i = 0; i < _held.Count; i++)
        {
            var target = _held[i];
            if (IsGone(target)) continue;

            Vector3 pos = anchor + SpreadOffset(i, _held.Count, heldSpread);
            pos.y += i * 0.02f;   // newest drawn on top
            target.transform.position = pos;
        }
    }

    /// <summary>Forgets held minions that died or were destroyed while in the hand.</summary>
    private void ForgetLostMinions()
    {
        bool changed = false;
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            var target = _held[i];
            if (!IsGone(target) && target.IsAlive) continue;

            _held.RemoveAt(i);
            changed = true;
        }

        if (changed) OnHeldChanged?.Invoke();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Point i of n spread evenly round a circle — golden-angle placement, so
    /// any count packs evenly. The first point is the centre.
    /// </summary>
    private static Vector3 SpreadOffset(int i, int n, float radius)
    {
        if (i == 0 || n <= 1) return Vector3.zero;

        float angle = i * 2.399963f;
        float r     = radius * Mathf.Sqrt(i / (float)(n - 1));
        return new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
    }

    /// <summary>The cursor's point on the grid, via the grid's own collider as dig selection does.</summary>
    private bool TryGetGroundPoint(out Vector3 point)
    {
        point = default;
        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        foreach (var hit in Physics.RaycastAll(ray))
        {
            if (hit.collider.gameObject != gridManager.gameObject) continue;
            point = hit.point;
            return true;
        }
        return false;
    }

    /// <summary>True if the minion's GameObject has been destroyed.</summary>
    private static bool IsGone(IHandTarget target) =>
        target is not Object obj || obj == null;
}
