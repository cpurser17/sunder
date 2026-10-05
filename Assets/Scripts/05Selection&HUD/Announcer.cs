using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Warnings and announcements to human players — "Your lair is too small",
/// and later "You have no gold", "Your Dungeon Heart is under attack"…
/// Every system asks for one by ID; this decides whether, when and how it's
/// given:
///
///   Per player     each human player has their own channel (cooldowns,
///                  queue, voice). AI factions get none — announcing to one
///                  does nothing.
///   Cooldown       a warning won't repeat to a player within its cooldown,
///                  so a dozen minions without a bed make one announcement.
///   One voice      lines never overlap: while one plays, others queue,
///                  highest priority first, a short gap between lines.
///   Expiry         a queued warning still waiting after maxQueueSeconds is
///                  dropped — it's no longer news.
///   Text           shown when its line starts (at once if it has no audio).
///
/// For now the lines are listed in the Inspector (catalogue below) and text
/// goes to the HUD footer's popup. Planned: an Announcements workbook
/// (random variants incl. rare lines, other languages), a bottom-right
/// message feed, a log with jump-to-event for urgent warnings, and separate
/// volume channels. Callers won't change — they only ever pass an ID.
///
/// Scene setup: none — GameManager2D adds one. Add it to GameManager
/// yourself to edit the catalogue and assign audio.
/// </summary>
public class Announcer : MonoBehaviour
{
    public enum Priority { Low, Normal, High, Critical }

    [System.Serializable]
    public class Entry
    {
        public string    id;
        [TextArea] public string text;
        [Tooltip("Spoken line. Optional — without one the text shows straight away.")]
        public AudioClip clip;
        public Priority  priority = Priority.Normal;
        [Tooltip("Seconds before this warning can be given to the same player again.")]
        public float     cooldown = 30f;
    }

    public static Announcer Instance { get; private set; }

    /// <summary>Raised whenever an announcement is given (player, id, text).</summary>
    public static event System.Action<FactionID, string, string> OnAnnounced;

    [SerializeField] private List<Entry> catalogue = DefaultCatalogue();

    /// <summary>
    /// Every announcement the code uses, with placeholder text. Any missing
    /// from a catalogue saved in the scene are added at startup, so new ones
    /// work before anyone has edited them.
    /// </summary>
    private static List<Entry> DefaultCatalogue() => new()
    {
        new Entry { id = "LairTooSmall",     text = "Your lair is too small.",     priority = Priority.Normal, cooldown = 30f },
        new Entry { id = "HatcheryTooSmall", text = "Your hatchery is too small.", priority = Priority.Normal, cooldown = 30f },
    };

    [Header("Delivery")]
    [Tooltip("Seconds text stays up once shown.")]
    [SerializeField] private float textHoldSeconds = 4f;
    [Tooltip("Silence between one line and the next.")]
    [SerializeField] private float gapBetweenLines = 0.5f;
    [Tooltip("A queued warning older than this is dropped instead of played.")]
    [SerializeField] private float maxQueueSeconds = 10f;
    [Tooltip("Where text appears for now. Empty = the HUD footer's popup.")]
    [SerializeField] private HotkeyPopup popup;

    private class Pending
    {
        public Entry Entry;
        public float QueuedAt;
    }

    /// <summary>One human player's announcements: their cooldowns, queue and voice.</summary>
    private class Channel
    {
        public FactionID   Player;
        public AudioSource Voice;
        public float       VoiceFreeAt;
        public readonly Dictionary<string, float> LastGiven = new();
        public readonly List<Pending> Queue = new();
    }

    private readonly Dictionary<FactionID, Channel> _channels = new();
    private readonly Dictionary<string, Entry>      _byId     = new(System.StringComparer.OrdinalIgnoreCase);

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        foreach (var e in catalogue)
            if (e != null && !string.IsNullOrEmpty(e.id)) _byId[e.id] = e;
        foreach (var e in DefaultCatalogue())
            if (!_byId.ContainsKey(e.id)) { _byId[e.id] = e; catalogue.Add(e); }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        foreach (var channel in _channels.Values) Pump(channel);
    }

    // ── Players ────────────────────────────────────────────────────────

    /// <summary>Gives each human player a channel; AI factions get none. Called by GameManager2D on load.</summary>
    public void SetHumanPlayers(IEnumerable<FactionID> players)
    {
        foreach (var c in _channels.Values) if (c.Voice != null) Destroy(c.Voice.gameObject);
        _channels.Clear();

        foreach (var player in players)
        {
            if (_channels.ContainsKey(player)) continue;
            var voice = new GameObject($"Voice_{player}").AddComponent<AudioSource>();
            voice.transform.SetParent(transform, false);
            voice.playOnAwake = false;
            _channels[player] = new Channel { Player = player, Voice = voice };
        }
    }

    // ── Announcing ─────────────────────────────────────────────────────

    /// <summary>Gives a warning to a faction's player, if it's human. Safe to call for anyone, any time.</summary>
    public static void Announce(FactionID faction, string id) => Instance?.Give(faction, id);

    private void Give(FactionID faction, string id)
    {
        if (!_channels.TryGetValue(faction, out var channel)) return;   // AI, or not a player
        if (!_byId.TryGetValue(id, out var entry))
        {
            Debug.LogWarning($"[Announcer] No announcement with id '{id}' in the catalogue.");
            return;
        }

        float now = Time.unscaledTime;
        if (channel.LastGiven.TryGetValue(id, out float at) && now - at < entry.cooldown) return;
        if (channel.Queue.Exists(p => p.Entry == entry)) return;
        channel.LastGiven[id] = now;

        channel.Queue.Add(new Pending { Entry = entry, QueuedAt = now });
        // Highest priority first; oldest first within a priority.
        channel.Queue.Sort((a, b) => a.Entry.priority != b.Entry.priority
            ? b.Entry.priority.CompareTo(a.Entry.priority)
            : a.QueuedAt.CompareTo(b.QueuedAt));
        Pump(channel);
    }

    /// <summary>Starts the next queued line once the voice is free, dropping any that went stale.</summary>
    private void Pump(Channel channel)
    {
        float now = Time.unscaledTime;
        channel.Queue.RemoveAll(p => now - p.QueuedAt > maxQueueSeconds);
        if (channel.Queue.Count == 0 || now < channel.VoiceFreeAt) return;

        var next = channel.Queue[0];
        channel.Queue.RemoveAt(0);

        if (next.Entry.clip != null && channel.Voice != null)
        {
            channel.Voice.PlayOneShot(next.Entry.clip);
            channel.VoiceFreeAt = now + next.Entry.clip.length + gapBetweenLines;
        }

        var target = popup != null ? popup : HudFooter.Instance != null ? HudFooter.Instance.Popup : null;
        if (target != null) target.Show($"<b>{next.Entry.text}</b>", textHoldSeconds);

        Debug.Log($"[Announcer] {channel.Player}: {next.Entry.text}");
        OnAnnounced?.Invoke(channel.Player, next.Entry.id, next.Entry.text);
    }
}
