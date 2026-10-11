using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The local player's mouse for spells. A spell button arms a spell (via
/// HUDController2D); while armed:
///
///   Hold LMB          — charge: the level ticks up every ChargeSeconds, up
///                       to the spell's MaxLevel and never past what you can
///                       afford. The cursor shows the level and cost.
///   Release LMB       — cast, at whatever is under the cursor NOW (the mouse
///                       can roam while charging). An invalid target there
///                       casts nothing and costs nothing.
///   RMB while charging — cancel the cast; the spell stays armed.
///   RMB / Escape      — put the spell away (the button deselects) — unless
///                       the spell uses the click itself (right-clicking a
///                       Call to Arms marker calls the rally off).
///
/// A spell with a radius shows it as a ring at the cursor.
///
/// The spell stays armed after casting, so it can be cast again. Hovering
/// over your own minions still makes them pause, as with the bare hand.
///
/// Scene setup: none — GameManager2D adds one.
/// </summary>
public class SpellCaster : MonoBehaviour
{
    public static SpellCaster Instance { get; private set; }

    /// <summary>True while the local player has a spell armed.</summary>
    public static bool AnyArmed => Instance != null && Instance._armed != null;

    [SerializeField] private FactionID localPlayer = FactionID.Player;
    [Tooltip("Leave empty to use Camera.main.")]
    [SerializeField] private Camera mainCamera;

    [Header("Aiming")]
    [Tooltip("Extra distance beyond a token's radius that still counts as over it.")]
    [SerializeField] private float minionPadding = 0.1f;

    [Header("Cursor label")]
    [SerializeField] private Vector2 labelOffset = new(18f, -18f);
    [SerializeField] private float   labelFontSize = 18f;
    [SerializeField] private Color   validColour   = Color.white;
    [SerializeField] private Color   invalidColour = new(1f, 0.45f, 0.4f, 1f);

    [Header("Radius ring")]
    [SerializeField] private float ringWidth    = 0.05f;
    [SerializeField] private int   ringSegments = 64;

    private SpellDefinition _armed;
    private bool  _charging;
    private float _chargeTime;
    private int   _level;

    private RectTransform   _canvas;
    private TextMeshProUGUI _label;
    private LineRenderer    _ring;

    public SpellDefinition Armed => _armed;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        BuildLabel();
        BuildRing();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Arming ─────────────────────────────────────────────────────────

    /// <summary>Arms a spell (HUDController2D calls this when its button is selected).</summary>
    public void Arm(SpellDefinition spell)
    {
        if (_armed == spell) return;
        _armed    = spell;
        _charging = false;
    }

    public void Disarm()
    {
        _armed    = null;
        _charging = false;
        ShowLabel(false);
        ShowRing(false);
    }

    /// <summary>Puts the spell away, deselecting its button too.</summary>
    private void PutAway()
    {
        if (HUDController2D.Instance != null) HUDController2D.Instance.RequestDeselect();
        Disarm();
    }

    // ── Input ──────────────────────────────────────────────────────────

    private void Update()
    {
        if (_armed == null) return;
        if (mainCamera == null) mainCamera = Camera.main;
        if (PauseController.IsPaused || mainCamera == null)
        {
            // Let go while paused: that cast is off.
            if (_charging && !Input.GetMouseButton(0)) _charging = false;
            ShowLabel(false);
            ShowRing(false);
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape)) { PutAway(); return; }

        var spells = SpellSystem.Instance;
        if (spells == null) return;

        if (_charging)
        {
            if (Input.GetMouseButtonDown(1)) { _charging = false; }
            else
            {
                Charge(spells);
                if (Input.GetMouseButtonUp(0) || !Input.GetMouseButton(0))
                {
                    _charging = false;
                    spells.TryCast(TargetUnderCursor(_level));
                }
            }
        }
        else if (Input.GetMouseButtonDown(1))
        {
            var effect = SpellSystem.EffectFor(_armed);
            var aimed  = TargetUnderCursor(1);
            if (effect == null || aimed.Cell == null || !effect.OnRightClick(localPlayer, aimed.Point)) { PutAway(); return; }
        }
        else if (Input.GetMouseButtonDown(0) && !PointerOverUI())
        {
            StartCharge(spells);
        }

        UpdateLabel(spells);
        UpdateRing(spells);
    }

    private void StartCharge(SpellSystem spells)
    {
        if (!spells.CanAfford(localPlayer, _armed, 1))
        {
            Announcer.Announce(localPlayer, "NotEnoughGold");
            return;
        }
        _charging   = true;
        _chargeTime = 0f;
        _level      = 1;
    }

    /// <summary>Raises the level with time held — stopping at the most the player can afford.</summary>
    private void Charge(SpellSystem spells)
    {
        if (_armed.maxLevel <= 1) return;
        _chargeTime += Time.deltaTime;
        int wanted = _armed.Clamp(1 + Mathf.FloorToInt(_chargeTime / Mathf.Max(0.05f, _armed.chargeSeconds)));
        while (_level < wanted && spells.CanAfford(localPlayer, _armed, _level + 1)) _level++;
    }

    // ── Aiming ─────────────────────────────────────────────────────────

    /// <summary>The cast as it would be aimed at the cursor right now.</summary>
    private SpellTarget TargetUnderCursor(int level)
    {
        var target = new SpellTarget { Caster = localPlayer, Spell = _armed, Level = level };
        var grid   = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (grid == null) return target;

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        foreach (var hit in Physics.RaycastAll(ray))
        {
            if (hit.collider.gameObject != grid.gameObject) continue;
            target.Point = hit.point;
            if (grid.WorldToCell(hit.point, out int x, out int y)) target.Cell = grid.GetCell(x, y);
            break;
        }

        if (_armed.targeting == SpellTargeting.OwnMinion || _armed.targeting == SpellTargeting.EnemyMinion)
        {
            target.Minion = MinionUnderCursor(ray, _armed.targeting == SpellTargeting.OwnMinion);
            target.Cell   = null;
            if (target.Minion != null)
            {
                target.Point = target.Minion.transform.position;
                if (grid.WorldToCell(target.Point, out int mx, out int my)) target.Cell = grid.GetCell(mx, my);
            }
        }
        return target;
    }

    /// <summary>The nearest live, unheld minion under the cursor — the caster's own, or an enemy.</summary>
    private MinionController MinionUnderCursor(Ray ray, bool own)
    {
        if (Mathf.Abs(ray.direction.y) < 1e-4f) return null;

        MinionController best = null;
        float bestSq = float.MaxValue;
        foreach (var m in MinionController.All)
        {
            if (m == null || !m.IsTargetable) continue;
            if (own ? m.Faction != localPlayer : !CombatSystem.IsEnemy(localPlayer, m.Faction)) continue;

            Vector3 pos = m.transform.position;
            float   t   = (pos.y - ray.origin.y) / ray.direction.y;
            if (t < 0f) continue;
            Vector3 hit = ray.origin + ray.direction * t;
            float dx = hit.x - pos.x, dz = hit.z - pos.z;
            float sq = dx * dx + dz * dz;
            float reach = m.HandRadius + minionPadding;
            if (sq > reach * reach || sq >= bestSq) continue;
            best = m; bestSq = sq;
        }
        return best;
    }

    private static bool PointerOverUI() =>
        EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

    // ── Cursor label ───────────────────────────────────────────────────

    private void BuildLabel()
    {
        var go = new GameObject("SpellCursorCanvas", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;   // above the HUD
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;
        _canvas = (RectTransform)go.transform;

        var labelGo = new GameObject("SpellCursorLabel", typeof(RectTransform));
        labelGo.transform.SetParent(_canvas, false);
        var rect = (RectTransform)labelGo.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot     = new Vector2(0f, 1f);
        rect.sizeDelta = new Vector2(400f, 60f);
        _label = labelGo.AddComponent<TextMeshProUGUI>();
        _label.fontSize      = labelFontSize;
        _label.fontStyle     = FontStyles.Bold;
        _label.alignment     = TextAlignmentOptions.TopLeft;
        _label.raycastTarget = false;
        labelGo.SetActive(false);
    }

    // ── Radius ring ────────────────────────────────────────────────────

    private void BuildRing()
    {
        var go = new GameObject("SpellRadiusRing");
        go.transform.SetParent(transform, false);
        _ring = go.AddComponent<LineRenderer>();
        _ring.loop              = true;
        _ring.useWorldSpace     = false;
        _ring.material          = new Material(Shader.Find("Unlit/Color"));
        _ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _ring.receiveShadows    = false;
        _ring.enabled           = false;
    }

    private void ShowRing(bool on)
    {
        if (_ring != null && _ring.enabled != on) _ring.enabled = on;
    }

    /// <summary>The area the spell would cover at its current level, round the cursor.</summary>
    private void UpdateRing(SpellSystem spells)
    {
        if (_ring == null || _armed == null) return;
        int level = _charging ? _level : 1;
        float tiles = _armed.RadiusAt(level);
        var target = TargetUnderCursor(level);
        if (tiles <= 0f || target.Cell == null || (PointerOverUI() && !_charging)) { ShowRing(false); return; }

        float cell = GameManager2D.Instance.Grid.CellSize;
        _ring.transform.position = target.Point;
        _ring.startWidth = _ring.endWidth = ringWidth * cell;
        _ring.material.color = spells.CanCast(target) && spells.CanAfford(localPlayer, _armed, level) ? validColour : invalidColour;
        DrawCircle(_ring, tiles * cell, ringSegments, 0.03f);
        ShowRing(true);
    }

    /// <summary>Lays a LineRenderer (local space) out as a flat circle, lifted slightly off the ground.</summary>
    public static void DrawCircle(LineRenderer line, float radius, int segments, float lift)
    {
        segments = Mathf.Max(8, segments);
        line.positionCount = segments;
        for (int i = 0; i < segments; i++)
        {
            float a = i * Mathf.PI * 2f / segments;
            line.SetPosition(i, new Vector3(Mathf.Cos(a) * radius, lift, Mathf.Sin(a) * radius));
        }
    }

    private void ShowLabel(bool on)
    {
        if (_label != null && _label.gameObject.activeSelf != on) _label.gameObject.SetActive(on);
    }

    /// <summary>Spell name, level and cost by the cursor — red when the cast wouldn't go off here.</summary>
    private void UpdateLabel(SpellSystem spells)
    {
        if (_label == null || _armed == null) { ShowLabel(false); return; }
        if (PointerOverUI() && !_charging)    { ShowLabel(false); return; }

        int   level    = _charging ? _level : 1;
        int   cost     = spells.CostOf(localPlayer, _armed, level);
        float cooldown = spells.CooldownLeft(localPlayer, _armed);

        string text = _armed.displayName;
        if (_armed.maxLevel > 1) text += $"  Lv {level}/{_armed.maxLevel}";
        if (cost > 0)            text += $"  {cost}g";
        if (cooldown > 0f)       text += $"\n<size=75%>Ready in {Mathf.CeilToInt(cooldown)}s</size>";

        _label.text  = text;
        _label.color = spells.CanCast(TargetUnderCursor(level)) && spells.CanAfford(localPlayer, _armed, level)
                     ? validColour : invalidColour;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvas, Input.mousePosition, null, out Vector2 local))
            ((RectTransform)_label.transform).anchoredPosition = local + labelOffset;
        ShowLabel(true);
    }
}
