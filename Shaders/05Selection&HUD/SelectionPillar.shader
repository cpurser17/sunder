// Standalone alpha-blended vertex-colour shader for the selection pillar box.
// Alpha is driven entirely by the vertex colour's alpha channel, which is set
// per-vertex in SelectionBoxVisuals: bottom vertices = full alpha, top = zero.
// This produces a smooth glow-pillar gradient over any underlying geometry.
//
// Cull Off    — visible from inside and below the box.
// ZWrite Off  — doesn't write to depth buffer so it composites correctly
//               over 3D assets without clipping them.
// ZTest LEqual — still depth-tests against the scene so it doesn't draw
//                through walls that are in front of it.
Shader "Dungeon2D/SelectionPillar"
{
    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
        }

        ZWrite Off
        ZTest  LEqual
        Cull   Off
        Blend  SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
