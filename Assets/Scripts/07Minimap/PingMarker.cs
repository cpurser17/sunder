using System.Collections;
using UnityEngine;

/// <summary>
/// Attached to a world-space ping marker GO.
/// Fades the marker out over its lifetime then destroys itself.
/// PingManager spawns and owns these.
/// </summary>
public class PingMarker : MonoBehaviour
{
    [Header("Lifetime")]
    [SerializeField] private float lifetime    = 30f;
    [SerializeField] private float fadeStart   = 25f; // when fade begins

    private Renderer[] _renderers;
    private Color      _baseColour;

    // ── Initialisation ─────────────────────────────────────────────────

    /// <summary>
    /// Called by PingManager immediately after Instantiate.
    /// Applies faction colour and starts the lifetime coroutine.
    /// </summary>
    public void Initialise(Color colour, float overrideLifetime = -1f)
    {
        if (overrideLifetime > 0f)
        {
            lifetime  = overrideLifetime;
            fadeStart = lifetime * 0.85f;
        }

        _baseColour = colour;
        _renderers  = GetComponentsInChildren<Renderer>();

        ApplyColour(colour);
        StartCoroutine(LifetimeRoutine());
    }

    // ── Lifetime ───────────────────────────────────────────────────────

    private IEnumerator LifetimeRoutine()
    {
        float elapsed = 0f;

        while (elapsed < lifetime)
        {
            elapsed += Time.deltaTime;

            if (elapsed > fadeStart)
            {
                float t     = (elapsed - fadeStart) / (lifetime - fadeStart);
                Color faded = _baseColour;
                faded.a     = Mathf.Lerp(1f, 0f, t);
                ApplyColour(faded);
            }

            yield return null;
        }

        Destroy(gameObject);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private void ApplyColour(Color colour)
    {
        foreach (var r in _renderers)
            r.material.color = colour;
    }
}
