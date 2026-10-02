using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Handles all in-game save triggers and routes to GameManager2D.
///
/// Three public entry points:
///   TriggerAutosave() — overwrites autosave.json. Call on a timer.
///   TriggerSave()     — routes to overwrite / fork based on loaded state.
///   TriggerSaveAs()   — always appends / creates new save.
///
/// Thumbnail capture happens inside a coroutine (requires end-of-frame)
/// before the save write. The save write itself is synchronous.
///
/// Scene setup
/// -----------
/// 1. Attach to any persistent GO in the Gameplay Scene (e.g. GameManager).
/// 2. Wire the three optional UI buttons to TriggerSave / TriggerSaveAs /
///    TriggerAutosave via their OnClick events.
/// 3. Enable autoSave and set autoSaveIntervalSeconds for background saving.
/// 4. Wire feedbackLabel for a "Game saved" confirmation message.
/// </summary>
public class SaveTrigger : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────
    [Header("Auto Save")]
    [SerializeField] private bool  autoSave                = false;
    [SerializeField] private float autoSaveIntervalSeconds = 120f;

    [Header("Cooldown")]
    [Tooltip("Minimum seconds between any two non-autosave saves.")]
    [SerializeField] private float saveCooldownSeconds = 2f;

    [Header("Thumbnail")]
    [Tooltip("Whether to capture a screenshot thumbnail on each save.")]
    [SerializeField] private bool captureThumbnail = true;

    [Header("Feedback (optional)")]
    [SerializeField] private TextMeshProUGUI feedbackLabel;
    [SerializeField] private float           feedbackDuration = 2.5f;

    // ── Runtime ────────────────────────────────────────────────────────
    private float     _lastSaveTime = -999f;
    private bool      _saveInProgress;
    private Coroutine _feedbackCoroutine;
    private Coroutine _autoSaveCoroutine;

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Start()
    {
        if (feedbackLabel != null)
            feedbackLabel.gameObject.SetActive(false);

        if (autoSave)
            _autoSaveCoroutine = StartCoroutine(AutoSaveLoop());
    }

    private void OnDestroy()
    {
        if (_autoSaveCoroutine != null) StopCoroutine(_autoSaveCoroutine);
        if (_feedbackCoroutine  != null) StopCoroutine(_feedbackCoroutine);
    }

    // ── Public entry points ────────────────────────────────────────────

    /// <summary>
    /// Triggers the autosave. Bypasses cooldown — autosave is always allowed.
    /// Routes to GameManager2D.Autosave().
    /// Wire to a timer or call from a checkpoint script.
    /// </summary>
    public void TriggerAutosave()
    {
        if (_saveInProgress) return;
        StartCoroutine(ExecuteSave(SaveType.Autosave));
    }

    /// <summary>
    /// Triggers a Save.
    /// Routes to overwrite, append, or fork based on what was loaded.
    /// Wire to the "Save" button OnClick event.
    /// </summary>
    public void TriggerSave()
    {
        if (_saveInProgress) return;
        if (!CheckCooldown()) return;
        StartCoroutine(ExecuteSave(SaveType.Save));
    }

    /// <summary>
    /// Triggers a Save As.
    /// Always appends a new file (or same as Save for mid-branch until UI exists).
    /// Wire to the "Save As" button OnClick event.
    /// </summary>
    public void TriggerSaveAs()
    {
        if (_saveInProgress) return;
        if (!CheckCooldown()) return;
        StartCoroutine(ExecuteSave(SaveType.SaveAs));
    }

    // ── Auto-save loop ─────────────────────────────────────────────────

    private IEnumerator AutoSaveLoop()
    {
        yield return new WaitForSeconds(autoSaveIntervalSeconds);
        while (true)
        {
            TriggerAutosave();
            yield return new WaitForSeconds(autoSaveIntervalSeconds);
        }
    }

    // ── Core coroutine ─────────────────────────────────────────────────

    private enum SaveType { Autosave, Save, SaveAs }

    private IEnumerator ExecuteSave(SaveType type)
    {
        _saveInProgress = true;

        // Capture thumbnail at end of frame so the screen is fully rendered.
        Texture2D thumbnail = null;
        if (captureThumbnail)
        {
            yield return new WaitForEndOfFrame();
            thumbnail = ScreenCapture.CaptureScreenshotAsTexture();
        }

        // Perform the save on the main thread (synchronous file I/O).
        if (GameManager2D.Instance == null)
        {
            Debug.LogWarning("[SaveTrigger] GameManager2D.Instance is null.");
            _saveInProgress = false;
            yield break;
        }

        bool success = true;
        switch (type)
        {
            case SaveType.Autosave:
                GameManager2D.Instance.Autosave(thumbnail);
                ShowFeedback("Autosaved.", Color.grey);
                break;

            case SaveType.Save:
                GameManager2D.Instance.Save(thumbnail: thumbnail);
                ShowFeedback("Game saved.", Color.white);
                break;

            case SaveType.SaveAs:
                GameManager2D.Instance.SaveAs(thumbnail: thumbnail);
                ShowFeedback("Saved as new file.", Color.white);
                break;
        }

        if (thumbnail != null)
            Destroy(thumbnail); // free the temporary texture

        if (success && type != SaveType.Autosave)
            _lastSaveTime = Time.unscaledTime;

        _saveInProgress = false;
    }

    // ── Cooldown ───────────────────────────────────────────────────────

    private bool CheckCooldown()
    {
        if (Time.unscaledTime - _lastSaveTime < saveCooldownSeconds)
        {
            Debug.Log("[SaveTrigger] Save skipped — cooldown active.");
            ShowFeedback("Please wait...", Color.yellow);
            return false;
        }
        return true;
    }

    // ── Feedback ───────────────────────────────────────────────────────

    private void ShowFeedback(string message, Color colour)
    {
        if (feedbackLabel == null) return;
        if (_feedbackCoroutine != null) StopCoroutine(_feedbackCoroutine);
        _feedbackCoroutine = StartCoroutine(FeedbackRoutine(message, colour));
    }

    private IEnumerator FeedbackRoutine(string message, Color colour)
    {
        feedbackLabel.text  = message;
        feedbackLabel.color = colour;
        feedbackLabel.gameObject.SetActive(true);

        float fadeStart = feedbackDuration * 0.75f;
        float elapsed   = 0f;

        while (elapsed < feedbackDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            if (elapsed > fadeStart)
            {
                float t = (elapsed - fadeStart) / (feedbackDuration - fadeStart);
                feedbackLabel.color = new Color(colour.r, colour.g, colour.b, 1f - t);
            }
            yield return null;
        }

        feedbackLabel.gameObject.SetActive(false);
        _feedbackCoroutine = null;
    }
}
