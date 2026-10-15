// GPU side of BlendUdonDeformStack: the modifiers set to Runtime GPU (Wave, Simple Deform, Lattice, Hook, Warp),
// same maths as the modifiers, applied in stack order. Custom Render Texture passes, driven from Udon:
//   Deform: one texel per mesh vertex = its deformed local position, read back by Udon into the mesh.
//   Bounds: texel j = (min z, max z, min x, max x) of the vertices in modifier j's deform space (Simple Deform limits).
// The textures are made at build from the Modifier Stack.
Shader "Hidden/Blend Property/Deform Stack"
{
    Properties
    {
        _Rest ("Rest (xyz local)", 2D) = "black" {}
        _Bounds ("Bounds", 2D) = "black" {}
        _VW ("Vertex texture width", Float) = 1
        _VH ("Vertex texture height", Float) = 1
        _N ("Vertex count", Float) = 0
    }

    CGINCLUDE
    #include "UnityCustomRenderTexture.cginc"
    #define MAX_OPS 8
    sampler2D _Rest, _Bounds;
    float _VW, _VH, _N, _OpCount;
    // per modifier, already in mesh units (see BlendUdonDeformStack.Pack): x of _OpA.. by type
    float4 _OpType[MAX_OPS], _OpA[MAX_OPS], _OpB[MAX_OPS], _OpC[MAX_OPS];
    float4 _OpM[MAX_OPS * 8]; // two matrices per modifier, as rows
    float4 _Points[512];      // the Lattice's point offsets from its rest grid, U fastest

    float2 Texel(float i) { float y = floor((i + 0.5) / _VW); return float2(i - y * _VW, y); }
    float3 Rest(float i) { return tex2Dlod(_Rest, float4((Texel(i) + 0.5) / float2(_VW, _VH), 0, 0)).xyz; }
    float4x4 M(int op, int k) { int b = op * 8 + k * 4; return float4x4(_OpM[b], _OpM[b + 1], _OpM[b + 2], _OpM[b + 3]); }
    float3 Mul(float4x4 m, float3 p) { return mul(m, float4(p, 1)).xyz; }

    // WarpModifier.Weight
    float Weight(int f, float x)
    {
        if (f == 0) return 1;
        if (f == 1) return x;
        if (f == 3) return x * x;
        if (f == 4) return sqrt(2 * x - x * x);
        if (f == 5) return sqrt(x);
        return x * x * (3 - 2 * x);
    }

    float3 Wave(int o, float3 p)
    {
        float4 a = _OpA[o], b = _OpB[o], c = _OpC[o];
        float h = a.x, w = a.y, nar = a.z, shift = a.w;
        bool mx = c.x > 0.5, mz = c.y > 0.5;
        float dx = p.x - b.x, dz = p.z - b.y;
        float dist = mx && mz ? sqrt(dx * dx + dz * dz) : (mx ? dx : dz);
        float x = dist - shift;
        if (b.w > 0.5) { float m = x + w, len = 2 * w; x = m - floor(m / len) * len - w; }
        if (x > -w && x < w && (mx || mz)) { float q = x * nar; p.y += (exp(-q * q) - b.z) * h * c.z; }
        return p;
    }

    float3 SimpleDeform(int o, float3 o0, float4 bnd)
    {
        float4 a = _OpA[o], b = _OpB[o];
        float3 p = Mul(M(o, 0), o0);
        int mode = (int)round(a.x);
        bool bend = mode == 1;
        float lo = bend ? lerp(bnd.z, bnd.w, b.x) : lerp(bnd.x, bnd.y, b.x);
        float hi = bend ? lerp(bnd.z, bnd.w, b.y) : lerp(bnd.x, bnd.y, b.y);
        if (lo > hi) { float t0 = lo; lo = hi; hi = t0; }
        float k = a.y / max(hi - lo, 1e-6), factor = a.z;
        if (mode == 0)
        {
            float t = clamp(p.z, lo, hi) * k, s = sin(t), c = cos(t);
            p = float3(p.x * c - p.y * s, p.x * s + p.y * c, p.z);
        }
        else if (bend)
        {
            if (abs(k) >= 1e-7)
            {
                float x = clamp(p.x, lo, hi), dcut = p.x - x;
                float t = x * k, s = sin(t), c = cos(t), r = 1 / k;
                p = float3(-(p.y - r) * s + c * dcut, (p.y - r) * c + r + s * dcut, p.z);
            }
        }
        else if (mode == 2)
        {
            float sc = 1 + clamp(p.z, lo, hi) * factor;
            p = float3(p.x * sc, p.y * sc, p.z);
        }
        else
        {
            float z = clamp(p.z, lo, hi), dcut = p.z - z;
            float sc = z * z * factor - factor + 1;
            p = float3(p.x * sc, p.y * sc, z * (1 + factor) + dcut);
        }
        float3 res = Mul(M(o, 1), p);
        int lockMask = (int)round(b.z); // locks never apply to Bend (the build leaves them out)
        if (lockMask & 1) res.x = o0.x;
        if (lockMask & 2) res.y = o0.y;
        if (lockMask & 4) res.z = o0.z;
        return res;
    }

    float3 Lattice(int o, float3 p0)
    {
        float4 a = _OpA[o];
        int ru = (int)a.x, rv = (int)a.y, rw = (int)a.z;
        float3 lp = Mul(M(o, 0), p0);
        float3 g = (lp + 0.5) * float3(ru - 1, rv - 1, rw - 1);
        int3 c0 = clamp((int3)floor(g), 0, int3(ru, rv, rw) - 2);
        float3 f = g - c0;
        float3 d = 0;
        [unroll] for (int c = 0; c < 8; c++)
        {
            int3 off = int3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
            float3 wv = off == 1 ? f : 1 - f;
            int3 q = c0 + off;
            d += _Points[q.x + ru * (q.y + rv * q.z)].xyz * (wv.x * wv.y * wv.z);
        }
        return lerp(p0, Mul(M(o, 1), lp + d), a.w);
    }

    // Hook and Warp: blend toward a moved copy of the point, by falloff around a center
    float3 Pull(int o, float3 p)
    {
        float4 a = _OpA[o], b = _OpB[o];
        float w = 1;
        if (a.x > 0)
        {
            float x = 1 - length(p - b.xyz) / a.x;
            if (x <= 0) return p;
            w = Weight((int)round(a.y), x);
        }
        return lerp(p, Mul(M(o, 0), p), w * a.z);
    }

    // one modifier; Simple Deform reads its bounds texel unless measuring (then it is skipped)
    float3 Op(int o, float3 p, bool measuring)
    {
        int type = (int)round(_OpType[o].x);
        if (type == 0) return Wave(o, p);
        if (type == 1)
            return measuring ? p : SimpleDeform(o, p, tex2Dlod(_Bounds, float4((o + 0.5) / MAX_OPS, 0.5, 0, 0)));
        if (type == 2) return Lattice(o, p);
        return Pull(o, p);
    }
    ENDCG

    SubShader
    {
        Lighting Off Blend One Zero ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Deform"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 px = floor(IN.localTexcoord.xy * float2(_VW, _VH));
                float i = px.y * _VW + px.x;
                if (i >= _N) return 0;
                float3 p = Rest(i);
                for (int o = 0; o < (int)_OpCount; o++) p = Op(o, p, false);
                return float4(p, 1);
            }
            ENDCG
        }

        Pass
        {
            Name "Bounds"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            // ponytail: one thread walks every vertex through the modifiers above; an earlier Simple Deform is
            // skipped while measuring (it would read this texture). Fine for tens of thousands of vertices.
            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                int j = (int)floor(IN.localTexcoord.x * MAX_OPS);
                if (j >= (int)_OpCount || (int)round(_OpType[j].x) != 1) return 0;
                float4 b = float4(3.4e38, -3.4e38, 3.4e38, -3.4e38);
                for (float i = 0; i < _N; i++)
                {
                    float3 p = Rest(i);
                    for (int o = 0; o < j; o++) p = Op(o, p, true);
                    float3 c = Mul(M(j, 0), p);
                    b = float4(min(b.x, c.z), max(b.y, c.z), min(b.z, c.x), max(b.w, c.x));
                }
                return b;
            }
            ENDCG
        }
    }
}
