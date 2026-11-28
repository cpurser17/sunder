using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the research web's guide rings and the links between nodes, as one
/// UI mesh so it stays sharp at any zoom. Links between two nodes on the
/// same ring follow the ring as an arc; others are straight lines.
/// Positions are in the content's units (see ResearchScreen.Place).
/// </summary>
public class ResearchLinksGraphic : MaskableGraphic
{
    public struct Link
    {
        public float AngleA, RadiusA, AngleB, RadiusB;
        public Color Colour;
        public float Width;
    }

    public readonly List<Link> Links = new();
    public int   Rings;
    public float RingSpacing = 150f;
    public Color RingColour  = new(1f, 1f, 1f, 0.05f);

    public void Redraw() => SetVerticesDirty();

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        for (int r = 1; r <= Rings; r++) Arc(vh, r * RingSpacing, 0f, 360f, RingColour, 2f);
        foreach (var l in Links)
        {
            bool sameRing = Mathf.Abs(l.RadiusA - l.RadiusB) < 0.05f && l.RadiusA > 0.05f;
            if (sameRing)
            {
                float a = l.AngleA, b = l.AngleB;
                float delta = Mathf.DeltaAngle(a, b);   // the short way round
                Arc(vh, l.RadiusA * RingSpacing, a, a + delta, l.Colour, l.Width);
            }
            else Line(vh, ResearchScreen.Place(l.AngleA, l.RadiusA, RingSpacing),
                          ResearchScreen.Place(l.AngleB, l.RadiusB, RingSpacing), l.Colour, l.Width);
        }
    }

    private static void Arc(VertexHelper vh, float radius, float fromDeg, float toDeg, Color colour, float width)
    {
        int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(toDeg - fromDeg) / 4f));
        Vector2 prev = ResearchScreen.Place(fromDeg, radius, 1f);
        for (int i = 1; i <= steps; i++)
        {
            Vector2 next = ResearchScreen.Place(Mathf.Lerp(fromDeg, toDeg, i / (float)steps), radius, 1f);
            Line(vh, prev, next, colour, width);
            prev = next;
        }
    }

    private static void Line(VertexHelper vh, Vector2 a, Vector2 b, Color colour, float width)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 0.0001f) return;
        Vector2 n = new Vector2(-d.y, d.x).normalized * (width * 0.5f);
        int i = vh.currentVertCount;
        Color32 c = colour;
        vh.AddVert(a - n, c, Vector4.zero);
        vh.AddVert(a + n, c, Vector4.zero);
        vh.AddVert(b + n, c, Vector4.zero);
        vh.AddVert(b - n, c, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i, i + 2, i + 3);
    }
}
