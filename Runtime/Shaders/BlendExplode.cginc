// Blender's Explode motion for any Built-in shader (lilToon custom, your own surface/vert-frag shader, ...).
// BlendUdonExplode keeps a material whose shader has _ExplodeTime, so add this to your shader and it explodes too:
//   1. Properties: [HideInInspector] _ExplodeTime ("Explode Time", Float) = -1
//   2. #include "Packages/com.logiccuteguy.blendproperty/Runtime/Shaders/BlendExplode.cginc"
//   3. First line of your vertex function: BlendExplode(v.vertex, v.normal, v.texcoord2, v.texcoord3);
//      (mesh UV channel 2 = piece centre + random, channel 3 = face normal; the bake writes both. Your shader must not use those channels.)
//      Also call it in the ShadowCaster pass so the shadows follow, and use Cull Off: pieces are open shells.
#ifndef BLEND_EXPLODE_INCLUDED
#define BLEND_EXPLODE_INCLUDED

#include "UnityCG.cginc"

float _ExplodeTime, _Speed, _Random, _Spin, _Floor, _Lifetime;
float4 _Gravity;

float3 BlendExplodeRand3(float r) { return frac(sin(float3(r * 91.7, r * 47.3, r * 13.1) * 437.585) * 1.7) * 2 - 1; }

float3 BlendExplodeRotate(float3 v, float3 axis, float a)
{
    float s, c; sincos(a, s, c);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1 - c);
}

void BlendExplode(inout float4 vertex, inout float3 normal, float4 uv2, float4 uv3)
{
    if (_ExplodeTime < 0 && _ExplodeTime > -2) return; // -1 = whole; below -2 = exploded before this player joined
    float t = max(_Time.y - _ExplodeTime, 0);
    float r = uv2.w;                                             // random 0..1 per piece
    float3 c = mul(unity_ObjectToWorld, float4(uv2.xyz, 1)).xyz; // piece centre
    float3 n = mul((float3x3)unity_ObjectToWorld, uv3.xyz);
    n = dot(n, n) > 1e-12 ? normalize(n) : 0;
    float3 vel = n * (_Speed * (0.5 + r)) + BlendExplodeRand3(r) * _Random;
    float3 g = _Gravity.xyz;
    // stop when the centre reaches the floor (only for pieces that start above it, with gravity pulling down)
    float tt = t;
    float h = c.y - _Floor;
    if (h > 0 && g.y < 0)
    {
        float land = (-vel.y - sqrt(vel.y * vel.y - 2 * g.y * h)) / g.y; // later root of h + vy t + g t²/2 = 0
        tt = min(t, land);
    }
    float3 axis = normalize(BlendExplodeRand3(r + 0.37) + 1e-4);
    float angle = _Spin * (0.5 + r) * tt;
    float scale = _Lifetime > 0 ? saturate((_Lifetime - t) / (0.25 * _Lifetime)) : 1; // shrink away at the end
    float3 w = mul(unity_ObjectToWorld, float4(vertex.xyz, 1)).xyz;
    w = c + vel * tt + 0.5 * g * tt * tt + BlendExplodeRotate(w - c, axis, angle) * scale;
    vertex.xyz = mul(unity_WorldToObject, float4(w, 1)).xyz;
    float3 wn = BlendExplodeRotate(UnityObjectToWorldNormal(normal), axis, angle);
    normal = normalize(mul((float3x3)unity_WorldToObject, wn));
}

#endif
