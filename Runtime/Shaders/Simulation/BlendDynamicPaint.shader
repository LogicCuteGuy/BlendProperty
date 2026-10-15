// Dynamic Paint for BlendUdonDynamicPaint (Blender Canvas, Paint surface), in UV space on the GPU.
// Custom Render Texture passes, driven from Udon:
//   Paint: double-buffered, rgb = paint colour (linear), a = coverage. Brushes add / erase, Dissolve fades.
//   Composite: the material's own texture with the paint over it, darkened where wet; this is what the material shows.
//   Wet: double-buffered, r = wetness (Blender Wetmap). Brushes wet the surface, it dries over Dry Time.
// _Pos (baked by "Bake Dynamic Paint") holds each texel's local position, a = 1 inside the mesh's UV islands.
Shader "Hidden/Blend Property/Dynamic Paint"
{
    Properties
    {
        _Pos ("Position map (xyz local)", 2D) = "black" {}
        _Paint ("Paint", 2D) = "black" {}
        _Base ("Base", 2D) = "white" {}
        _Wet ("Wetmap", 2D) = "black" {}
        _Tip0 ("Brush tip 0", 2D) = "white" {}
        _Tip1 ("Brush tip 1", 2D) = "white" {}
        _Tip2 ("Brush tip 2", 2D) = "white" {}
        _Tip3 ("Brush tip 3", 2D) = "white" {}
        _Tip4 ("Brush tip 4", 2D) = "white" {}
        _Tip5 ("Brush tip 5", 2D) = "white" {}
        _Tip6 ("Brush tip 6", 2D) = "white" {}
        _Tip7 ("Brush tip 7", 2D) = "white" {}
    }

    SubShader
    {
        Lighting Off Blend One Zero ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Paint"
            CGPROGRAM
            #include "UnityCustomRenderTexture.cginc"
            #include "BlendPaintBrush.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            sampler2D _Pos;
            float4x4 _ObjectToWorld;
            float _Fade, _Clear;   // coverage lost this update; 1 = wipe
            float _BrushCount;

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 uv = IN.localTexcoord.xy;
                float4 paint = tex2Dlod(_SelfTexture2D, float4(uv, 0, 0));
                if (_Clear > 0.5) return 0;
                float4 pm = tex2Dlod(_Pos, float4(uv, 0, 0));
                if (pm.a < 0.5) return 0;
                paint.a = max(0, paint.a - _Fade);
                float3 w = mul(_ObjectToWorld, float4(pm.xyz, 1)).xyz;
                [loop] for (int b = 0; b < 8; b++)
                {
                    if (b >= (int)_BrushCount) break;
                    float4 tip = BrushSample(b, w);
                    float s = _BrushB[b].a;
                    float a = BrushAlpha(b, tip.a);
                    if (s < 0) { paint.a = max(0, paint.a - a); continue; }
                    // "over" blend: new paint on top of the old
                    float outA = a + paint.a * (1 - a);
                    paint.rgb = outA > 1e-5 ? (_BrushB[b].rgb * tip.rgb * a + paint.rgb * paint.a * (1 - a)) / outA : paint.rgb;
                    paint.a = outA;
                }
                return paint;
            }
            ENDCG
        }

        Pass
        {
            Name "Composite"
            CGPROGRAM
            #include "UnityCustomRenderTexture.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            sampler2D _Paint, _Base, _Wet;
            float4 _BaseST; // the material's tiling / offset for its own texture
            float _WetDark; // how much fully wet areas darken

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 uv = IN.localTexcoord.xy;
                float4 base = tex2D(_Base, uv * _BaseST.xy + _BaseST.zw);
                float4 paint = tex2D(_Paint, uv);
                float wet = tex2D(_Wet, uv).r;
                float alpha = paint.a + base.a * (1 - paint.a);
                float3 rgb = (paint.rgb * paint.a + base.rgb * base.a * (1 - paint.a)) / max(alpha, 1e-5);
                return float4(rgb * (1 - _WetDark * wet), alpha);
            }
            ENDCG
        }

        Pass
        {
            Name "Wet"
            CGPROGRAM
            #include "UnityCustomRenderTexture.cginc"
            #include "BlendPaintBrush.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            sampler2D _Pos;
            float4x4 _ObjectToWorld;
            float _Dry, _Clear;    // wetness lost this update; 1 = wipe
            float _BrushCount;

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 uv = IN.localTexcoord.xy;
                float wet = tex2Dlod(_SelfTexture2D, float4(uv, 0, 0)).r;
                if (_Clear > 0.5) return 0;
                float4 pm = tex2Dlod(_Pos, float4(uv, 0, 0));
                if (pm.a < 0.5) return 0;
                wet = max(0, wet - _Dry);
                float3 w = mul(_ObjectToWorld, float4(pm.xyz, 1)).xyz;
                [loop] for (int b = 0; b < 8; b++)
                {
                    if (b >= (int)_BrushCount) break;
                    float a = BrushAlpha(b, BrushSample(b, w).a);
                    wet = _BrushB[b].a < 0 ? max(0, wet - a) : max(wet, a);
                }
                return wet;
            }
            ENDCG
        }
    }
}
