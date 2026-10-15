// BlendUdonParticles Render As Object for any Built-in shader (lilToon custom, your own surface/vert-frag shader, ...).
// Same maths as Blend Property/Particle Instance. Bake keeps the particle renderer's material when its shader has _State,
// so put your material on the baked "Particles" renderer and Bake again. To add it to your shader:
//   1. Properties:
//        _EndSize ("End Size", Range(0, 4)) = 1
//        [Toggle] _AlignVelocity ("Align To Velocity", Float) = 0
//        _Spin ("Spin (turns per life)", Float) = 1
//        [HideInInspector] _State ("State", 2D) = "black" {}
//        [HideInInspector] _Size ("Size", Float) = 1
//        [HideInInspector] _SizeRandom ("Size Random", Float) = 0
//   2. #include "Packages/com.logiccuteguy.blendproperty/Runtime/Shaders/BlendParticleInstance.cginc" and #pragma target 3.5
//   3. First line of your vertex function: BlendParticleInstance(v.vertex, v.normal, v.texcoord1);
//      (mesh UV channel 1 = particle texel + random; the bake writes it. Your shader must not use UV1, so no lightmaps.)
//      Also call it in the ShadowCaster pass, and add "DisableBatching"="True" to Tags.
// If your shader already declares one of these names (e.g. _Spin, also used by BlendExplode.cginc), rename it there.
// BlendUdonParticles also sets _SimToWorld, _UseSizeCurve and _SizeCurve (Size over Lifetime) every frame.
#ifndef BLEND_PARTICLE_INSTANCE_INCLUDED
#define BLEND_PARTICLE_INSTANCE_INCLUDED

#include "UnityCG.cginc"

sampler2D _State;
float _EndSize, _AlignVelocity, _Spin, _Size, _SizeRandom, _UseSizeCurve;
float4x4 _SimToWorld; // the particles' simulation space (world, or the emitter's local space) to world
float4 _SizeCurve[8]; // x: Size over Lifetime at life 0, 1/7 .. 1

uint BlendParticleHash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}
float BlendParticleRand(uint x) { return BlendParticleHash(x) * (1.0 / 4294967296.0); }

// 8 evenly spaced keys over life 0..1
float4 BlendParticleCurve(float4 keys[8], float life)
{
    float f = saturate(life) * 7;
    int k = min((int)f, 6);
    return lerp(keys[k], keys[k + 1], f - k);
}

// life multiplier of Size: Size over Lifetime curve, or End Size
float BlendParticleSizeOverLife(float life)
{
    return _UseSizeCurve > 0.5 ? BlendParticleCurve(_SizeCurve, life).x : lerp(1, _EndSize, life);
}

float3 BlendParticleRotate(float3 v, float3 axis, float a)
{
    float s, c; sincos(a, s, c);
    return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1 - c);
}

void BlendParticleInstance(inout float4 vertex, inout float3 normal, float4 uv1)
{
    float3 d = uv1.xyz; // xy = state texel (position half), z = random 0..1
    float4 s = tex2Dlod(_State, float4(d.xy, 0, 0));
    float3 vel = mul((float3x3)_SimToWorld, tex2Dlod(_State, float4(d.x + 0.5, d.y, 0, 0)).xyz); // velocity half
    float life = saturate(s.w);
    float size = s.w < 1 ? _Size * (1 - _SizeRandom * d.z) * BlendParticleSizeOverLife(life) : 0; // dead: no area
    float3 p = vertex.xyz, n = normal;
    float spin = (d.z + _Spin * life) * 6.2831853;
    if (_AlignVelocity > 0.5 && dot(vel, vel) > 1e-8)
    {
        float3 f = normalize(vel);
        float3 up = abs(f.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
        float3 x = normalize(cross(up, f)), y = cross(f, x);
        p = BlendParticleRotate(p, float3(0, 0, 1), spin); n = BlendParticleRotate(n, float3(0, 0, 1), spin); // roll around the motion
        p = x * p.x + y * p.y + f * p.z; n = x * n.x + y * n.y + f * n.z;
    }
    else
    {
        float3 axis = normalize(frac(sin(d.z * float3(91.7, 47.3, 13.1)) * 437.585) * 2 - 1 + 1e-4);
        p = BlendParticleRotate(p, axis, spin); n = BlendParticleRotate(n, axis, spin);
    }
    float3 w = mul(_SimToWorld, float4(s.xyz, 1)).xyz + p * size;
    vertex = float4(mul(unity_WorldToObject, float4(w, 1)).xyz, 1);
    normal = normalize(mul((float3x3)unity_WorldToObject, n));
}

#endif
