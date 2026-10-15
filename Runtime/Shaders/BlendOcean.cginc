// Blender's Ocean motion for any Built-in shader (lilToon custom, your own surface/vert-frag shader, ...).
// Same maths as Blend Property/Ocean (local XZ plane, lifting Y). To add it to your shader:
//   1. Properties (copy the [Header(Ocean)] and [Header(Foam)] blocks from BlendOcean.shader):
//        _Waves, _TimeScale, _SpatialSize, _SmallestWave, _WaveScale, _Choppiness, _WindVelocity, _Alignment,
//        _Direction, _Damping, _Depth, _Seed (and _Foam, _FoamCoverage if you use foam)
//   2. #include "Packages/com.logiccuteguy.blendproperty/Runtime/Shaders/BlendOcean.cginc" and #pragma target 3.5
//   3. First line of your vertex function: BlendOcean(v.vertex, v.normal);
//      Also call it in the ShadowCaster pass, and add "DisableBatching"="True" to Tags.
//   4. Optional foam / per-pixel detail: pass the rest position (v.vertex.xz before step 3) to the fragment, then
//        float3 n; float foam = BlendOceanPixel(rest, n);   // n = local-space normal, foam 0..1
//      and lerp your albedo towards white by foam. Without it the normal is per vertex (needs a denser mesh).
// If your shader already declares one of these names (e.g. _Direction, _Depth), rename it there.
#ifndef BLEND_OCEAN_INCLUDED
#define BLEND_OCEAN_INCLUDED

#include "UnityCG.cginc"

float _Waves, _TimeScale, _SpatialSize, _SmallestWave, _WaveScale, _Choppiness, _WindVelocity, _Alignment;
float _Direction, _Damping, _Depth, _Seed, _Foam, _FoamCoverage;

float BlendOceanHash(uint v)
{
    uint s = v * 747796405u + 2891336453u;
    uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
    return float((w >> 22u) ^ w) / 4294967296.0;
}

// displacement at rest point p, and the derivatives of the displaced point along local x and z
void BlendOceanWaves(float2 p, out float3 disp, out float3 tx, out float3 tz)
{
    const float g = 9.81;
    disp = 0; tx = float3(1, 0, 0); tz = float3(0, 0, 1);
    float t = _Time.y * _TimeScale;
    float kMin = UNITY_TWO_PI / max(_SpatialSize, 0.01);
    float range = log(max(_SpatialSize / max(_SmallestWave, 0.001), 1.01));
    float w0 = g / max(_WindVelocity, 0.1);
    int n = (int)_Waves;
    uint seed = (uint)max(_Seed, 0) * 977u;
    [loop] for (int i = 0; i < 64; i++)
    {
        if (i >= n) break;
        uint h = seed + (uint)i * 3u;
        // one wave per slice of sizes, spaced evenly in log size, with energy from the spectrum for that slice
        float k = kMin * exp(range * (i + BlendOceanHash(h)) / n);
        float dk = k * range / n;
        float w = sqrt(g * k * tanh(min(k * _Depth, 20)));
        float S = 0.0081 * g * g / pow(w, 5) * exp(-0.74 * pow(w0 / w, 4));
        float a = sqrt(S * g / w * dk) * _WaveScale; // a² = 2 S(ω) dω, dω = g / 2ω dk
        float th = (BlendOceanHash(h + 1) * 2 - 1) * UNITY_PI / (1 + _Alignment);
        if (_Alignment > 0 && abs(th) > UNITY_HALF_PI) a *= 1 - _Damping;
        th += radians(_Direction);
        float2 d = float2(cos(th), sin(th));
        float s, c;
        sincos(k * dot(d, p) - w * t + BlendOceanHash(h + 2) * UNITY_TWO_PI, s, c);
        float q = _Choppiness * a;
        disp += float3(-d.x * q * s, a * c, -d.y * q * s);
        tx += k * float3(-d.x * d.x * q * c, -d.x * a * s, -d.x * d.y * q * c);
        tz += k * float3(-d.x * d.y * q * c, -d.y * a * s, -d.y * d.y * q * c);
    }
}

void BlendOcean(inout float4 vertex, inout float3 normal)
{
    float3 disp, tx, tz;
    BlendOceanWaves(vertex.xz, disp, tx, tz);
    vertex.xyz += disp;
    normal = normalize(cross(tz, tx));
}

// per-pixel local-space normal and foam (0..1) at rest position p
float BlendOceanPixel(float2 p, out float3 normal)
{
    float3 disp, tx, tz;
    BlendOceanWaves(p, disp, tx, tz);
    normal = normalize(cross(tz, tx));
    float jacobian = tx.x * tz.z - tx.z * tz.x; // 1 = flat, below 0 = the crest folds over
    // measured at Choppiness 1: 5 % of the sea is below 0.78, 20 % below 0.89, so Coverage 0..1 spans 0.6..0.9
    return _Foam > 0.5 ? saturate((0.6 + 0.3 * _FoamCoverage - jacobian) * 10) : 0;
}

#endif
