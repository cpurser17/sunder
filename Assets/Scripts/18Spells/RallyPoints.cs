using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Call to Arms: at most one rally point per faction. Its creatures within
/// the radius answer it (RallyBehaviour) — workers don't. Casting again
/// moves it. While it stands, its upkeep is taken every interval; a faction
/// that can't pay loses it and hears "CallToArmsEnded".
///
/// Scene setup: none — GameManager2D adds one.
/// </summary>
public class RallyPoints : MonoBehaviour
{
    public static RallyPoints Instance { get; private set; }

    [Header("Marker")]
    [SerializeField] private Color markerColour = new(1f, 0.25f, 0.2f, 1f);
    [SerializeField] private float ringWidth    = 0.05f;
    [SerializeField] private int   ringSegments = 64;

    private class Rally
    {
        public Vector3    Point;
        public float      RadiusTiles;
        public int        UpkeepGold;
        public float      UpkeepInterval;
        public float      NextUpkeepAt;
        public GameObject Marker;
    }

    private readonly Dictionary<FactionID, Rally> _rallies = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private float CellSize =>
        GameManager2D.Instance != null && GameManager2D.Instance.Grid != null ? GameManager2D.Instance.Grid.CellSize : 1f;

    // ── Queries ────────────────────────────────────────────────────────

    public bool IsActive(FactionID faction) => _rallies.ContainsKey(faction);

    /// <summary>The faction's rally point and radius (world units), if it has one.</summary>
    public bool TryGet(FactionID faction, out Vector3 point, out float radius)
    {
        if (_rallies.TryGetValue(faction, out var rally))
        {
            point  = rally.Point;
            radius = rally.RadiusTiles * CellSize;
            return true;
        }
        point = default; radius = 0f;
        return false;
    }

    /// <summary>True if the position is within the faction's rally radius.</summary>
    public bool InRange(FactionID faction, Vector3 position)
    {
        if (!TryGet(faction, out var point, out float radius)) return false;
        float dx = position.x - point.x, dz = position.z - point.z;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>True if the world point is on the faction's rally marker itself (within about half a tile).</summary>
    public bool IsOnMarker(FactionID faction, Vector3 worldPoint)
    {
        if (!_rallies.TryGetValue(faction, out var rally)) return false;
        float reach = 0.5f * CellSize;
        float dx = worldPoint.x - rally.Point.x, dz = worldPoint.z - rally.Point.z;
        return dx * dx + dz * dz <= reach * reach;
    }

    // ── Setting and clearing ───────────────────────────────────────────

    /// <summary>Puts the faction's rally point here, replacing any it had.</summary>
    public void Set(FactionID faction, Vector3 point, float radiusTiles, int upkeepGold, float upkeepInterval)
    {
        if (!_rallies.TryGetValue(faction, out var rally))
            _rallies[faction] = rally = new Rally { NextUpkeepAt = Time.time + Mathf.Max(0.5f, upkeepInterval) };

        rally.Point          = point;
        rally.RadiusTiles    = Mathf.Max(0f, radiusTiles);
        rally.UpkeepGold     = Mathf.Max(0, upkeepGold);
        rally.UpkeepInterval = Mathf.Max(0.5f, upkeepInterval);
        PlaceMarker(rally);
    }

    public void Clear(FactionID faction)
    {
        if (!_rallies.TryGetValue(faction, out var rally)) return;
        if (rally.Marker != null) Destroy(rally.Marker);
        _rallies.Remove(faction);
    }

    // ── Upkeep ─────────────────────────────────────────────────────────

    private readonly List<FactionID> _unpaid = new();

    private void Update()
    {
        _unpaid.Clear();
        foreach (var pair in _rallies)
        {
            var rally = pair.Value;
            if (Time.time < rally.NextUpkeepAt) continue;
            rally.NextUpkeepAt = Time.time + rally.UpkeepInterval;
            if (rally.UpkeepGold <= 0) continue;

            var wallet = GameManager2D.Instance != null ? GameManager2D.Instance.GetWallet(pair.Key) : null;
            if (wallet == null || !wallet.TrySpend(rally.UpkeepGold)) _unpaid.Add(pair.Key);
        }

        foreach (var faction in _unpaid)
        {
            Clear(faction);
            Announcer.Announce(faction, "CallToArmsEnded");
        }
    }

    // ── Marker ─────────────────────────────────────────────────────────

    /// <summary>A pole on the spot and a ring showing the radius.</summary>
    private void PlaceMarker(Rally rally)
    {
        float cell = CellSize;
        if (rally.Marker == null)
        {
            rally.Marker = new GameObject("RallyPoint");
            rally.Marker.transform.SetParent(transform, false);

            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(pole.GetComponent<Collider>());
            pole.name = "Pole";
            pole.transform.SetParent(rally.Marker.transform, false);
            pole.transform.localScale    = new Vector3(0.08f * cell, 0.5f * cell, 0.08f * cell);
            pole.transform.localPosition = new Vector3(0f, 0.5f * cell, 0f);
            var r = pole.GetComponent<Renderer>();
            if (r != null) r.material.color = markerColour;

            var ring = rally.Marker.AddComponent<LineRenderer>();
            ring.loop          = true;
            ring.useWorldSpace = false;
            ring.startWidth    = ring.endWidth = ringWidth * cell;
            ring.material      = new Material(Shader.Find("Unlit/Color")) { color = markerColour };
            ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ring.receiveShadows    = false;
        }

        rally.Marker.transform.position = rally.Point;
        var line = rally.Marker.GetComponent<LineRenderer>();
        SpellCaster.DrawCircle(line, rally.RadiusTiles * cell, ringSegments, 0.03f);
    }

    // ── Save / load ────────────────────────────────────────────────────

    public List<RallySaveData> Capture()
    {
        var list = new List<RallySaveData>();
        foreach (var pair in _rallies)
            list.Add(new RallySaveData
            {
                faction        = pair.Key,
                x              = pair.Value.Point.x,
                y              = pair.Value.Point.y,
                z              = pair.Value.Point.z,
                radiusTiles    = pair.Value.RadiusTiles,
                upkeepGold     = pair.Value.UpkeepGold,
                upkeepInterval = pair.Value.UpkeepInterval,
                secondsToUpkeep = Mathf.Max(0f, pair.Value.NextUpkeepAt - Time.time),
            });
        return list;
    }

    public void Restore(List<RallySaveData> saved)
    {
        foreach (var faction in new List<FactionID>(_rallies.Keys)) Clear(faction);
        if (saved == null) return;
        foreach (var s in saved)
        {
            Set(s.faction, new Vector3(s.x, s.y, s.z), s.radiusTiles, s.upkeepGold, s.upkeepInterval);
            _rallies[s.faction].NextUpkeepAt = Time.time + s.secondsToUpkeep;
        }
    }
}

[System.Serializable]
public class RallySaveData
{
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public FactionID faction;
    public float x, y, z;
    public float radiusTiles;
    public int   upkeepGold;
    public float upkeepInterval;
    public float secondsToUpkeep;
}
