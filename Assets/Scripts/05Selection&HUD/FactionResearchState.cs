using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One team's research for the mission: the points minions earn working
/// in the Library, the research web (ResearchWeb, from the imported
/// ResearchCatalogue), what's been researched, the queue, and the stat
/// changes and unlocks that research has given (Modifiers). One instance per
/// active team, created by GameManager2D alongside FactionWallet. Research
/// starts over every mission.
///
/// Points go into the node being researched: the first node in the queue
/// that can be researched now, or — with none — one the game picks (highest
/// AutoWeight, then cheapest). Each node keeps the points put into it, so
/// switching away and back loses nothing.
///
/// Announcements (each optional, played only once written in the
/// Announcements workbook): ResearchComplete, ResearchUnlocked (when the
/// node unlocks something), ResearchAutoChosen (the queue ran out), and a
/// node's own Announcement in place of the first two.
/// </summary>
public class FactionResearchState : MonoBehaviour, IUnlockState, IResearchUnlockState
{
    public const string AnnounceComplete   = "ResearchComplete";
    public const string AnnounceUnlocked   = "ResearchUnlocked";
    public const string AnnounceAutoChosen = "ResearchAutoChosen";

    public FactionID Faction { get; private set; }

    /// <summary>Stat changes and unlocks this team has — read by everything that reads a changeable stat.</summary>
    public FactionModifiers Modifiers { get; } = new();

    private readonly HashSet<string> _completed = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> _progress = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _queue = new();

    private ResearchWeb _web;
    private ResearchWeb.WebNode _current;
    private bool _currentIsAuto;

    /// <summary>Raised when the queue, the node being researched, or what's researched changes.</summary>
    public event System.Action OnChanged;
    /// <summary>Raised when a node is finished.</summary>
    public event System.Action<ResearchWeb.WebNode> OnResearched;

    private float _summonIntervalMultiplier = 1f;

    /// <summary>Multiplies MinionSummoner's summon interval. 1 = no change, below 1 = faster.</summary>
    public float SummonIntervalMultiplier =>
        Mathf.Max(0.05f, _summonIntervalMultiplier * Modifiers.Apply(ModifierTarget.Faction, "*", ModifierStats.SummonInterval, 1f));

    // ── Progress from room work ────────────────────────────────────────
    // Points minions produce working in rooms. Research points are spent on
    // the web; manufacture (Workshop) and prayer (Shrine) aren't spent yet.

    /// <summary>Research points earned and not yet put into a node (nothing to research, or between nodes).</summary>
    public float ResearchPoints    { get; private set; }
    /// <summary>Manufacture points from minions working in the Workshop.</summary>
    public float ManufacturePoints { get; private set; }
    /// <summary>Prayer points from minions praying at the Shrine.</summary>
    public float PrayerPoints      { get; private set; }

    public void AddResearchPoints(float amount)
    {
        if (amount <= 0f) return;
        ResearchPoints += Mathf.Max(0f, Modifiers.Apply(ModifierTarget.Faction, "*", ModifierStats.ResearchSpeed, amount));
    }
    public void AddManufacturePoints(float amount) { if (amount > 0f) ManufacturePoints += amount; }
    public void AddPrayerPoints(float amount)      { if (amount > 0f) PrayerPoints      += amount; }

    public void Initialise(FactionID faction) => Faction = faction;

    // ── The web ────────────────────────────────────────────────────────

    /// <summary>This team's research web (empty if research hasn't been imported, or the team has no faction).</summary>
    public ResearchWeb Web
    {
        get
        {
            if (_web != null) return _web;
            string content = GameManager2D.Instance?.GetFactionDefinition(Faction)?.factionContentId;
            if (string.IsNullOrEmpty(content)) return null;   // not resolved yet — try again later
            _web = ResearchWeb.Build(ResearchCatalogue.Load(), content);
            RebuildModifiers();
            return _web;
        }
    }

    public ResearchWeb.WebNode Current => _current;
    public bool CurrentIsAuto => _current != null && _currentIsAuto;
    public IReadOnlyList<string> Queue => _queue;

    public bool HasResearch(string researchId) =>
        !string.IsNullOrEmpty(researchId) && _completed.Contains(researchId);

    public bool IsDone(ResearchWeb.WebNode node) => node != null && _completed.Contains(node.Id);

    public float ProgressOf(ResearchWeb.WebNode node) =>
        node == null ? 0f : IsDone(node) ? node.Cost : _progress.TryGetValue(node.Id, out float p) ? p : 0f;

    /// <summary>Linked to something researched (or a Start node) — whether or not its Requires rule holds yet.</summary>
    public bool IsOpen(ResearchWeb.WebNode node)
    {
        if (node == null || IsDone(node)) return false;
        if (node.Start) return true;
        foreach (var l in node.Links) if (IsDone(l)) return true;
        return false;
    }

    /// <summary>Open and its Requires rule holds — points can go into it now.</summary>
    public bool CanResearch(ResearchWeb.WebNode node) =>
        IsOpen(node) && (node.Requires == null || node.Requires.IsBlank || node.Requires.IsMet(this));

    // ── The queue ──────────────────────────────────────────────────────

    /// <summary>Queues a node, and whatever route leads to it, at the end. False if it can't be reached.</summary>
    public bool Enqueue(string id)
    {
        var web = Web;
        var node = web?.Find(id);
        if (node == null || IsDone(node)) return false;
        var route = web.RouteTo(node, n => IsDone(n) || _queue.Contains(n.Id));
        if (route == null) return false;
        foreach (var n in route) if (!_queue.Contains(n.Id)) _queue.Add(n.Id);
        if (!_queue.Contains(node.Id)) _queue.Add(node.Id);
        Changed();
        return true;
    }

    /// <summary>Puts a node, and the route to it, at the front of the queue — research it next.</summary>
    public bool EnqueueFirst(string id)
    {
        var web = Web;
        var node = web?.Find(id);
        if (node == null || IsDone(node)) return false;
        var route = web.RouteTo(node, IsDone);
        if (route == null) return false;
        _queue.RemoveAll(q => route.Exists(n => string.Equals(n.Id, q, System.StringComparison.OrdinalIgnoreCase)));
        for (int i = 0; i < route.Count; i++) _queue.Insert(i, route[i].Id);
        Changed();
        return true;
    }

    /// <summary>Takes a node off the queue (progress on it is kept).</summary>
    public void Dequeue(string id)
    {
        if (_queue.RemoveAll(q => string.Equals(q, id, System.StringComparison.OrdinalIgnoreCase)) > 0) Changed();
    }

    /// <summary>Moves a queued node to another place in the queue (0 = next).</summary>
    public void MoveInQueue(string id, int index)
    {
        int at = _queue.FindIndex(q => string.Equals(q, id, System.StringComparison.OrdinalIgnoreCase));
        if (at < 0) return;
        string item = _queue[at];
        _queue.RemoveAt(at);
        _queue.Insert(Mathf.Clamp(index, 0, _queue.Count), item);
        Changed();
    }

    public void ClearQueue()
    {
        if (_queue.Count == 0) return;
        _queue.Clear();
        Changed();
    }

    private void Changed()
    {
        PickCurrent(announce: false);
        OnChanged?.Invoke();
    }

    // ── Researching ────────────────────────────────────────────────────

    private void Update()
    {
        var web = Web;
        if (web == null || web.Nodes.Count == 0) return;

        // The node being researched can change without the queue changing
        // (a Requires rule starts or stops holding), so it's chosen each frame.
        PickCurrent(announce: true);

        int guard = 0;
        while (_current != null && ResearchPoints > 0f && guard++ < 16)
        {
            float have = ProgressOf(_current);
            float put  = Mathf.Min(ResearchPoints, _current.Cost - have);
            ResearchPoints -= put;
            _progress[_current.Id] = have + put;
            if (have + put >= _current.Cost - 0.0001f)
            {
                Complete(_current);
                PickCurrent(announce: true);
            }
        }
    }

    private void PickCurrent(bool announce)
    {
        var web = Web;
        if (web == null) { _current = null; return; }

        foreach (var id in _queue)
        {
            var n = web.Find(id);
            if (n != null && CanResearch(n))
            {
                if (_current != n) { _current = n; _currentIsAuto = false; OnChanged?.Invoke(); }
                return;
            }
        }

        if (_current != null && _currentIsAuto && CanResearch(_current)) return;

        ResearchWeb.WebNode best = null;
        foreach (var n in web.Nodes)
        {
            if (n.Data.autoWeight <= 0f || !CanResearch(n)) continue;
            if (best == null || n.Data.autoWeight > best.Data.autoWeight ||
                (Mathf.Approximately(n.Data.autoWeight, best.Data.autoWeight) && n.Cost < best.Cost))
                best = n;
        }
        if (best == _current) return;
        _current = best;
        _currentIsAuto = best != null;
        // Only worth saying once there's research going on (not at the start, before a Library).
        if (best != null && announce && _queue.Count == 0 && _completed.Count > 0)
            Announcer.AnnounceIfWritten(Faction, AnnounceAutoChosen);
        OnChanged?.Invoke();
    }

    /// <summary>Finishes a node: its effects apply and it comes off the queue.</summary>
    public void Complete(ResearchWeb.WebNode node)
    {
        if (node == null || IsDone(node)) return;
        _completed.Add(node.Id);
        if (node.ReplacesId != null) _completed.Add(node.ReplacesId);   // counts as the node it replaced
        _progress.Remove(node.Id);
        _queue.RemoveAll(q => string.Equals(q, node.Id, System.StringComparison.OrdinalIgnoreCase));
        if (_current == node) _current = null;

        foreach (var e in node.Effects) Modifiers.Add(e, notify: false);
        Modifiers.NotifyChanged();

        bool unlocks = node.Effects.Exists(e => e.isUnlock);
        Announcer.AnnounceIfWritten(Faction, !string.IsNullOrEmpty(node.Data.announcement) ? node.Data.announcement
                                            : unlocks ? AnnounceUnlocked : AnnounceComplete);
        OnResearched?.Invoke(node);
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Marks research done without a node in the web — e.g. by a script.
    /// A node with that id has its effects apply too.
    /// </summary>
    public void CompleteResearch(string researchId)
    {
        if (string.IsNullOrEmpty(researchId)) return;
        var node = Web?.Find(researchId);
        if (node != null) Complete(node);
        else _completed.Add(researchId);
    }

    private void RebuildModifiers()
    {
        Modifiers.Clear();
        if (_web == null) return;
        Modifiers.SetLockable(_web.UnlockKeys());
        foreach (var n in _web.Nodes)
            if (IsDone(n))
                foreach (var e in n.Effects) Modifiers.Add(e, notify: false);
        Modifiers.NotifyChanged();
    }

    // ── What Requires rules can ask ────────────────────────────────────

    public int BiggestRoom(TileType type)
    {
        var grid = GameManager2D.Instance != null ? GameManager2D.Instance.Grid : null;
        if (grid == null) return 0;
        int biggest = 0;
        foreach (var room in grid.GetRoomsForFaction(Faction))
            if (room.TileType == type) biggest = Mathf.Max(biggest, room.Cells.Count);
        return biggest;
    }

    // Campaign conditions never hold inside a mission.
    bool  IUnlockState.IsComplete(string missionId) => false;
    bool  IUnlockState.HasCollected(string missionId, string itemId) => false;
    bool  IUnlockState.TryBest(string missionId, string stat, bool lowest, out float value) { value = 0f; return false; }
    float IUnlockState.Total(string stat) => 0f;
    bool  IUnlockState.IsCampaignComplete(string campaignId) => false;

    // ── Save / load ────────────────────────────────────────────────────

    public FactionProgressSaveData Capture()
    {
        var data = new FactionProgressSaveData
        {
            completedResearch = new List<string>(_completed),
            researchPoints    = ResearchPoints,
            manufacturePoints = ManufacturePoints,
            prayerPoints      = PrayerPoints,
            researchQueue     = new List<string>(_queue),
            researchProgress  = new List<ResearchProgressSaveData>(),
        };
        foreach (var pair in _progress)
            data.researchProgress.Add(new ResearchProgressSaveData { id = pair.Key, points = pair.Value });
        return data;
    }

    public void Restore(FactionProgressSaveData data)
    {
        if (data == null) return;
        _completed.Clear();
        if (data.completedResearch != null)
            foreach (var id in data.completedResearch) if (!string.IsNullOrEmpty(id)) _completed.Add(id);
        _progress.Clear();
        if (data.researchProgress != null)
            foreach (var p in data.researchProgress) if (p != null && !string.IsNullOrEmpty(p.id)) _progress[p.id] = p.points;
        _queue.Clear();
        if (data.researchQueue != null) _queue.AddRange(data.researchQueue);
        ResearchPoints    = data.researchPoints;
        ManufacturePoints = data.manufacturePoints;
        PrayerPoints      = data.prayerPoints;
        _current = null;
        if (_web != null) RebuildModifiers();   // else when the web is first built
        OnChanged?.Invoke();
    }

    public void SetSummonIntervalMultiplier(float multiplier) =>
        _summonIntervalMultiplier = Mathf.Max(0.05f, multiplier);
}
