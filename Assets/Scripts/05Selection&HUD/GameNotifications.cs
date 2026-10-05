using UnityEngine;

/// <summary>
/// Messages to the local player — "Your lair is too small" and the like —
/// shown as a brief popup with an optional spoken line, in the spirit of
/// Dungeon Keeper's narrator.
///
/// Each message has a key, and one key won't repeat within repeatCooldown
/// seconds, so a dozen minions failing to find a bed in the same moment make
/// one announcement, not twelve.
///
/// Popup: the one assigned here, else the HUD footer's hotkey popup. Audio:
/// played through an AudioSource on this object (added if missing).
///
/// Scene setup: none — GameManager2D adds one if the scene has none. Add it
/// yourself to give it its own popup or tune the timings.
/// </summary>
public class GameNotifications : MonoBehaviour
{
    public static GameNotifications Instance { get; private set; }

    /// <summary>Raised for every announcement made (key, message).</summary>
    public static event System.Action<string, string> OnNotified;

    [Tooltip("Where messages appear. Empty = the HUD footer's hotkey popup.")]
    [SerializeField] private HotkeyPopup popup;
    [Tooltip("Seconds a message stays up before fading.")]
    [SerializeField] private float holdSeconds = 3f;
    [Tooltip("Seconds before the same message can be announced again.")]
    [SerializeField] private float repeatCooldown = 30f;
    [SerializeField] private FactionID localPlayer = FactionID.Player;

    private readonly System.Collections.Generic.Dictionary<string, float> _lastShown = new();
    private AudioSource _audio;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Announces a message to the given faction — only the local player's
    /// are shown. Returns true if it was announced (not on cooldown).
    /// </summary>
    public bool Notify(FactionID faction, string key, string message, AudioClip clip = null)
    {
        if (faction != localPlayer) return false;
        if (_lastShown.TryGetValue(key, out float at) && Time.unscaledTime - at < repeatCooldown) return false;
        _lastShown[key] = Time.unscaledTime;

        var target = popup != null ? popup : HudFooter.Instance != null ? HudFooter.Instance.Popup : null;
        if (target != null) target.Show($"<b>{message}</b>", holdSeconds);

        if (clip != null)
        {
            if (_audio == null && !TryGetComponent(out _audio)) _audio = gameObject.AddComponent<AudioSource>();
            _audio.PlayOneShot(clip);
        }

        Debug.Log($"[Notify] {message}");
        OnNotified?.Invoke(key, message);
        return true;
    }
}
