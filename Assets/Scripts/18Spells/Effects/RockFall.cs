using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Placeholder look for Cave-In: a handful of rocks drop onto the area and
/// vanish. Swap for a proper effect when there's art for it.
/// </summary>
public class RockFall : MonoBehaviour
{
    private const float FallSeconds = 0.5f;
    private const float LingerSeconds = 0.6f;

    private readonly List<(Transform rock, Vector3 from, Vector3 to)> _rocks = new();
    private float _age;

    public static void Spawn(Vector3 centre, float radius, float cellSize)
    {
        var go   = new GameObject("RockFall");
        var fall = go.AddComponent<RockFall>();
        int count = Mathf.Clamp(Mathf.RoundToInt(radius / Mathf.Max(0.01f, cellSize) * 6f), 4, 30);

        for (int i = 0; i < count; i++)
        {
            var rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(rock.GetComponent<Collider>());
            rock.transform.SetParent(go.transform, false);
            float size = Random.Range(0.1f, 0.25f) * cellSize;
            rock.transform.localScale = Vector3.one * size;
            rock.transform.rotation   = Random.rotation;
            var r = rock.GetComponent<Renderer>();
            if (r != null) r.material.color = new Color(0.45f, 0.4f, 0.35f);

            Vector2 o  = Random.insideUnitCircle * radius;
            Vector3 to = centre + new Vector3(o.x, size * 0.5f, o.y);
            Vector3 from = to + Vector3.up * Random.Range(2f, 3f) * cellSize;
            rock.transform.position = from;
            fall._rocks.Add((rock.transform, from, to));
        }
    }

    private void Update()
    {
        _age += Time.deltaTime;
        float t = Mathf.Clamp01(_age / FallSeconds);
        foreach (var (rock, from, to) in _rocks)
            if (rock != null) rock.position = Vector3.Lerp(from, to, t * t);
        if (_age >= FallSeconds + LingerSeconds) Destroy(gameObject);
    }
}
