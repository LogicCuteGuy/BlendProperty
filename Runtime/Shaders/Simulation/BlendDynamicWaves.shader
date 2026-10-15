// Dynamic Paint Waves and Displace for BlendUdonDynamicWaves (Blender Canvas, Waves / Displace surface), in UV space on the GPU.
// Custom Render Texture passes, driven from Udon:
//   Waves: double-buffered, r = height (local units, along the surface normal), g = vertical speed,
//          b = how deep the brushes reached into this texel last step.
//          Brushes push the water aside as they move (only the change in their depth is added, so no water is
//          lost while one sits in it); a wave equation spreads it.
//   Vertices: one texel per mesh vertex = rest position + normal x height, read back by Udon into the mesh.
// _Pos (baked) holds each texel's local position, a = 1 inside the mesh's UV islands.
Shader "Hidden/Blend Property/Dynamic Waves"
{
    Properties
    {
        _Pos ("Position map (xyz local)", 2D) = "black" {}
        _Waves ("Waves", 2D) = "black" {}
        _VertA ("Vertices: rest xyz, u", 2D) = "black" {}
        _VertB ("Vertices: normal xyz, v", 2D) = "black" {}
        // baked values: declared so the materials save them
        _VW ("Vertex texture width", Float) = 1
        _VH ("Vertex texture height", Float) = 1
        _N ("Vertex count", Float) = 0
    }

    SubShader
    {
        Lighting Off Blend One Zero ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Waves"
            CGPROGRAM
            #include "UnityCustomRenderTexture.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            sampler2D _Pos;
            float4x4 _ObjectToWorld;
            float _K;          // (speed * dt / texel size)^2 per substep, kept under 0.5
            float _Dt;         // substep, seconds
            float _Damping;    // speed kept per substep
            float _Spring;     // pull back to rest per substep
            float _Smooth;     // blend toward the neighbours' average per substep (Blender Smoothness)
            float _Scale;      // world units per local unit
            float _Reset;
            float _Mode;       // 0 Waves, 1 Displace
            float _Keep;       // Displace: dent kept per update (Dissolve)
            float _BrushCount;
            float4 _BrushA[8]; // xyz world centre, w radius
            float4 _BrushB[8]; // x strength (wave factor)

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                // snap to this texel's centre: every lookup below must land on exact texels, or the stencil goes
                // lopsided and the waves blow up
                float2 size = _CustomRenderTextureInfo.xy;
                float2 uv = (floor(IN.localTexcoord.xy * size) + 0.5) / size;
                float2 t = 1 / size;
                float4 pm = tex2Dlod(_Pos, float4(uv, 0, 0));
                if (pm.a < 0.5) return 0;

                // brushes: how deep they reach into this texel now
                float3 w = mul(_ObjectToWorld, float4(pm.xyz, 1)).xyz;
                float depth = 0;
                [loop] for (int b = 0; b < 8; b++)
                {
                    if (b >= (int)_BrushCount) break;
                    float r = _BrushA[b].w;
                    float d = distance(w, _BrushA[b].xyz);
                    if (d < r) depth = min(depth, -(r - d) * _BrushB[b].x / _Scale);
                }
                if (_Reset > 0.5) return float4(0, 0, depth, 0); // flat, with brushes already sitting in it

                float2 s = tex2Dlod(_SelfTexture2D, float4(uv, 0, 0)).rg;
                // Displace: keep the deepest press, slowly recovering (Blender Displace surface)
                if (_Mode > 0.5) return float4(min(s.r * _Keep, depth), 0, depth, 0);
                float h = s.r, v = s.g;

                // neighbours outside the UV islands mirror this texel (no wave leaves the edge)
                float sum = 0;
                float2 o[4] = { float2(t.x, 0), float2(-t.x, 0), float2(0, t.y), float2(0, -t.y) };
                [unroll] for (int i = 0; i < 4; i++)
                {
                    float2 q = uv + o[i];
                    bool inside = all(q > 0) && all(q < 1) && tex2Dlod(_Pos, float4(q, 0, 0)).a > 0.5;
                    sum += inside ? tex2Dlod(_SelfTexture2D, float4(q, 0, 0)).r : h;
                }
                v = (v + (sum - 4 * h) * _K / _Dt - h * _Spring / _Dt) * _Damping;
                h += v * _Dt;
                h = lerp(h, sum * 0.25, _Smooth);

                // the change in brush depth since last step displaces the water
                h += depth - tex2Dlod(_SelfTexture2D, float4(uv, 0, 0)).b;
                return float4(h, v, depth, 0);
            }
            ENDCG
        }

        Pass
        {
            Name "Vertices"
            CGPROGRAM
            #include "UnityCustomRenderTexture.cginc"
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            sampler2D _Waves, _VertA, _VertB;
            float _VW, _VH, _N;

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 px = floor(IN.localTexcoord.xy * float2(_VW, _VH));
                if (px.y * _VW + px.x >= _N) return 0;
                float2 uv = (px + 0.5) / float2(_VW, _VH);
                float4 a = tex2Dlod(_VertA, float4(uv, 0, 0)), b = tex2Dlod(_VertB, float4(uv, 0, 0));
                float h = tex2Dlod(_Waves, float4(a.w, b.w, 0, 0)).r;
                return float4(a.xyz + b.xyz * h, 1);
            }
            ENDCG
        }
    }
}
