using TMPro;
using UnityEngine;

/// <summary>
/// The small, low-profile prompt shown while a footer hotkey sequence is
/// typed: which rows a tab has, then what's in the chosen row, then what was
/// picked. Shows instantly, holds, then fades. Never blocks clicks.
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class HotkeyPopup : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI text;
    [Tooltip("Seconds to fade out once the hold time is up.")]
    [SerializeField] private float fadeDuration = 0.25f;

    private CanvasGroup _group;
    private float       _hideAt;

    private void Awake()
    {
        _group = GetComponent<CanvasGroup>();
        _group.alpha          = 0f;
        _group.blocksRaycasts = false;
        _group.interactable   = false;
    }

    /// <summary>Shows the message (TextMeshPro rich text) for holdSeconds, then fades.</summary>
    public void Show(string message, float holdSeconds)
    {
        if (text != null) text.text = message;
        _group.alpha = 1f;
        _hideAt      = Time.unscaledTime + holdSeconds;
    }

    public void Hide() => _hideAt = Time.unscaledTime;

    private void Update()
    {
        if (_group.alpha <= 0f || Time.unscaledTime < _hideAt) return;
        _group.alpha = fadeDuration > 0f
            ? Mathf.MoveTowards(_group.alpha, 0f, Time.unscaledDeltaTime / fadeDuration)
            : 0f;
    }
}
