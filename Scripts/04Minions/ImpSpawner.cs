using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Handles imp summoning: cost calculation, click-to-place, and registration
/// with ImpTaskManager.
///
/// Cost formula: cost = b + k * c
///   b = baseCost     (default 25 — cost of the first imp)
///   c = costIncrement (default 25 — added per existing active imp)
///   k = current active imp count (0 when no imps exist)
///
/// Usage
/// -----
/// 1. Player clicks "Summon Imp" HUD button → sets IsSummonModeActive = true.
/// 2. Player left-clicks a valid world cell → imp spawns there, cost deducted.
/// 3. Right-click or pressing the button again cancels summon mode.
///
/// Scene setup
/// -----------
/// 1. Create child GO under GameManager. Name it "ImpSpawner_Player".
/// 2. Attach ImpSpawner. Set faction, assign impPrefab, gridManager, mainCamera.
/// 3. Wire the Summon Imp HUD button to ToggleSummonMode().
/// </summary>
public class ImpSpawner : MonoBehaviour
{
    // ── Per-faction registry ───────────────────────────────────────────
    // One spawner per faction, so a dying AI imp decrements the AI count
    // rather than the player's.
    private static readonly System.Collections.Generic.Dictionary<FactionID, ImpSpawner>
        _registry = new();

    public static ImpSpawner GetForFaction(FactionID faction) =>
        _registry.TryGetValue(faction, out var s) ? s : null;

    /// <summary>
    /// True while any spawner is awaiting a placement click. Other click
    /// handlers (dig selection) check this so a summon click is not also
    /// consumed as a drag-select.
    /// </summary>
    public static bool AnySummonModeActive
    {
        get
        {
            foreach (var s in _registry.Values)
                if (s != null && s.IsSummonModeActive) return true;
            return false;
        }
    }

    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Identity")]
    [SerializeField] private FactionID faction = FactionID.Player;

    [Header("References")]
    [SerializeField] private GameObject    impPrefab;
    [SerializeField] private GridManager2D gridManager;
    [SerializeField] private Camera        mainCamera;
    [SerializeField] private ImpTaskManager taskManager;

    [Header("Placement")]
    [Tooltip("Gap kept between the spawned token edge and the tile boundary, "
             + "in world units.")]
    [SerializeField] private float spawnEdgeGap = 0.02f;

    [Header("Cost Formula: cost = baseCost + activeImps * costIncrement")]
    [SerializeField] private int baseCost      = 25;
    [SerializeField] private int costIncrement = 25;

    // ── Runtime ────────────────────────────────────────────────────────
    private bool _summonModeActive;
    private TraversalCapability? _prefabCapability;
    private int  _activeImpCount;

    public bool IsSummonModeActive => _summonModeActive;
    public int  CurrentSummonCost  => baseCost + _activeImpCount * costIncrement;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        _registry[faction] = this;
    }

    private void OnDestroy()
    {
        if (_registry.TryGetValue(faction, out var s) && s == this)
            _registry.Remove(faction);
    }

    private void Update()
    {
        if (!_summonModeActive) return;

        // Cancel on right-click or Escape.
        if (Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
        {
            SetSummonMode(false);
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            // Mode stays open — the player can place several imps in a row and
            // leaves deliberately via right-click, Escape, or the button.
            TrySpawnImp();
        }
    }

    // ── Public API ─────────────────────────────────────────────────────

    /// <summary>Toggles summon mode on/off. Wire to the Summon Imp HUD button.</summary>
    public void ToggleSummonMode() => SetSummonMode(!_summonModeActive);

    /// <summary>Exits summon mode on every faction's spawner.</summary>
    public static void CancelAllSummonModes()
    {
        foreach (var s in _registry.Values)
            if (s != null && s.IsSummonModeActive) s.SetSummonMode(false);
    }

    /// <summary>
    /// Enters or exits summon mode.
    ///
    /// Button highlighting is owned by HUDController2D so the summon button
    /// behaves identically to the buy/sell buttons. When the mode is exited
    /// from here — right-click or Escape rather than the button — the HUD is
    /// told so it can clear the highlight.
    /// </summary>
    public void SetSummonMode(bool active)
    {
        if (_summonModeActive == active) return;
        _summonModeActive = active;

        if (!active)
            HUDController2D.Instance?.NotifySummonModeExited(faction);
    }

    // ── Spawning ───────────────────────────────────────────────────────

    private void TrySpawnImp()
    {
        // Check funds before attempting placement.
        var wallet = GameManager2D.Instance?.GetWallet(faction);
        if (wallet == null) return;

        int cost = CurrentSummonCost;
        if (!wallet.TrySpend(cost))
        {
            Debug.Log($"[ImpSpawner] Not enough gold. Need {cost}, have {wallet.Gold}.");
            return;
        }

        // Raycast to find click position.
        if (!TryGetGridCell(out int x, out int y, out Vector3 clickPoint))
        {
            // Refund if click missed the grid.
            wallet.Earn(cost);
            return;
        }

        var cell = gridManager.GetCell(x, y);
        if (cell == null || !IsValidSpawnCell(cell))
        {
            wallet.Earn(cost);
            Debug.Log("[ImpSpawner] Invalid spawn location — must be an owned tile " +
                      "or unclaimed Cave that the imp can stand on.");
            return;
        }

        SpawnImp(cell, clickPoint);
    }

    private void SpawnImp(GridCell cell, Vector3 clickPoint)
    {
        Vector3 centre = gridManager.CellToWorld(cell.X, cell.Y);

        var go  = Instantiate(impPrefab, centre, Quaternion.identity);
        go.name = $"Imp_{faction}_{_activeImpCount}";

        var imp = go.GetComponent<ImpController>();
        if (imp == null)
        {
            Debug.LogError("[ImpSpawner] Imp prefab missing ImpController component.");
            Destroy(go);
            return;
        }

        // GridAgent measures its radius in Awake, which has already run as part
        // of Instantiate, so the clamp below can use the real token size.
        go.transform.position = ClampInsideCell(clickPoint, cell, centre,
                                                go.GetComponent<GridAgent>());

        imp.Initialise(faction, taskManager);
        _activeImpCount++;
        taskManager.RegisterImp(imp);

        Debug.Log($"[ImpSpawner] Spawned imp {_activeImpCount} for {faction}. " +
                  $"Next cost: {CurrentSummonCost}g.");
    }

    /// <summary>
    /// Valid spawn tiles are either:
    ///   - a tile this faction owns, or
    ///   - unclaimed Cave, so imps can be seeded into open cavern before it
    ///     has been claimed.
    ///
    /// In both cases the imp must actually be able to stand there. Ownership
    /// alone is not enough — Wall is owned but impassable, and an imp placed
    /// inside it would be stuck in solid rock. Passability comes from the
    /// prefab's own capability rather than being assumed, so this stays correct
    /// if imps ever become amphibious or flying.
    /// </summary>
    private bool IsValidSpawnCell(GridCell cell)
    {
        bool owned = cell.Owner == faction &&
                     gridManager.GetCategory(cell.TileType) == TileCategory.Owned;

        // Cave is Unaligned by definition, so it cannot be expressed as an
        // ownership test and needs its own branch.
        bool unclaimedCave = cell.TileType == TileType.Cave &&
                             cell.Owner    == FactionID.Unaligned;

        if (!owned && !unclaimedCave) return false;

        return TraversalRules.CanPathOn(cell.TileType, PrefabCapability, faction);
    }

    /// <summary>
    /// Traversal capability declared on the imp prefab's GridAgent.
    /// Cached after the first read; falls back to LandOnly if absent.
    /// </summary>
    private TraversalCapability PrefabCapability
    {
        get
        {
            if (_prefabCapability.HasValue) return _prefabCapability.Value;

            var agent = impPrefab != null ? impPrefab.GetComponent<GridAgent>() : null;
            _prefabCapability = agent != null ? agent.Capability
                                              : TraversalCapability.LandOnly;
            return _prefabCapability.Value;
        }
    }

    /// <summary>
    /// Resolves the click to a grid cell AND keeps the exact world point that
    /// was hit, so the imp can be placed where the player actually clicked
    /// rather than snapped to the middle of the tile.
    /// </summary>
    private bool TryGetGridCell(out int x, out int y, out Vector3 hitPoint)
    {
        x = y = 0;
        hitPoint = Vector3.zero;

        Ray          ray  = mainCamera.ScreenPointToRay(Input.mousePosition);
        RaycastHit[] hits = Physics.RaycastAll(ray);

        foreach (var hit in hits)
        {
            if (hit.collider.gameObject != gridManager.gameObject) continue;
            hitPoint = hit.point;
            return gridManager.WorldToCell(hit.point, out x, out y);
        }
        return false;
    }

    /// <summary>
    /// Pulls a click point inward so the spawned token sits fully inside its
    /// cell and clear of any wall.
    ///
    /// Two limits apply. The token must stay within the cell, or WorldToCell
    /// would report it in a neighbouring tile. It must also respect the cell's
    /// clearance, since clicking hard against the edge of a tile that backs
    /// onto stone would otherwise drop the token partly inside the rock.
    /// </summary>
    private Vector3 ClampInsideCell(Vector3 point, GridCell cell,
                                    Vector3 centre, GridAgent agent)
    {
        float radius = agent != null ? agent.Radius : 0f;
        float half   = gridManager.CellSize * 0.5f;

        float maxOffset = half - radius - spawnEdgeGap;

        // Clearance is measured from the cell centre to the nearest wall, so
        // subtracting the radius gives how far the centre may travel before the
        // token edge would touch. Applied symmetrically — conservative, but it
        // only bites in cells that actually border solid rock.
        var clearance = gridManager.Clearance;
        if (clearance != null && agent != null)
        {
            float c = clearance.GetClearance(cell, agent.Capability);
            maxOffset = Mathf.Min(maxOffset, c - radius);
        }

        maxOffset = Mathf.Max(0f, maxOffset);

        Vector3 offset = point - centre;
        offset.x = Mathf.Clamp(offset.x, -maxOffset, maxOffset);
        offset.z = Mathf.Clamp(offset.z, -maxOffset, maxOffset);
        offset.y = 0f;

        return centre + offset;
    }

    // ── Imp death notification ─────────────────────────────────────────

    /// <summary>Called by ImpController.Die() to decrement the active count.</summary>
    public void NotifyImpDied() => _activeImpCount = Mathf.Max(0, _activeImpCount - 1);
}
