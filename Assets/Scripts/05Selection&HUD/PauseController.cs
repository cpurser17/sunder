using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Pauses the game when P is pressed (again to resume).
///
/// Paused, game time stops (Time.timeScale 0): minions, workers, chickens,
/// payday clocks, hunger and tiredness all freeze, and game sound pauses.
/// The camera still moves and orbits, so the player can look around, and
/// the HUD still works. The Keeper's hand can't pick up, drop or slap
/// anything while paused. A "PAUSED" banner shows across the screen.
///
/// Anything that must keep running while paused should use unscaled time
/// (Time.unscaledTime / unscaledDeltaTime), as the camera and HUD popups do.
///
/// Scene setup: none — GameManager2D adds one. Add it yourself to change
/// the key or the banner text.
/// </summary>
public class PauseController : MonoBehaviour
{
    public static bool IsPaused { get; private set; }

    /// <summary>Raised when the game pauses (true) or resumes (false).</summary>
    public static event System.Action<bool> OnPauseChanged;

    [SerializeField] private KeyCode pauseKey   = KeyCode.P;
    [SerializeField] private string  pausedText = "PAUSED";

    private GameObject _banner;
    private float      _resumeScale = 1f;

    private void Awake()
    {
        // A fresh scene always starts running, whatever the last one left behind.
        Time.timeScale       = 1f;
        AudioListener.pause  = false;
        IsPaused             = false;
        BuildBanner();
    }

    private void Start()
    {
        // Let Cinemachine keep blending and damping the camera while game time is stopped.
        var cam = Camera.main;
        if (cam != null && cam.TryGetComponent(out Unity.Cinemachine.CinemachineBrain brain))
            brain.IgnoreTimeScale = true;
    }

    private void OnDestroy()
    {
        if (IsPaused) SetPaused(false);
    }

    private void Update()
    {
        if (IsTyping() || !Input.GetKeyDown(pauseKey)) return;
        SetPaused(!IsPaused);
    }

    public void SetPaused(bool paused)
    {
        if (paused == IsPaused) return;
        IsPaused = paused;

        if (paused)
        {
            _resumeScale   = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
        }
        else Time.timeScale = _resumeScale;

        AudioListener.pause = paused;
        if (_banner != null) _banner.SetActive(paused);
        OnPauseChanged?.Invoke(paused);
    }

    private void BuildBanner()
    {
        var canvasGo = new GameObject("PauseCanvas", typeof(RectTransform));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;   // above the HUD
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;

        _banner = new GameObject("PausedBanner", typeof(RectTransform));
        _banner.transform.SetParent(canvasGo.transform, false);
        var rect = (RectTransform)_banner.transform;
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(0f, 90f);
        var bg = _banner.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.55f);
        bg.raycastTarget = false;   // the HUD stays clickable

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(_banner.transform, false);
        var textRect = (RectTransform)textGo.transform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = textRect.offsetMax = Vector2.zero;
        var text = textGo.AddComponent<TextMeshProUGUI>();
        text.text          = pausedText;
        text.fontSize      = 56f;
        text.alignment     = TextAlignmentOptions.Center;
        text.color         = Color.white;
        text.raycastTarget = false;

        _banner.SetActive(false);
    }

    private static bool IsTyping()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && (selected.GetComponent<TMP_InputField>() != null ||
                                    selected.GetComponent<InputField>() != null);
    }
}
