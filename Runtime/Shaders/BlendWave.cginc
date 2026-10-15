// Blender's Wave motion for any Built-in shader (lilToon custom, your own surface/vert-frag shader, ...).
// Same maths as Blend Property/Wave (radial on the local XZ plane, lifting Y). To add it to your shader:
//   1. Properties (same names as Blend Property/Wave, so BlendUdonPanel sliders work on it too):
//        _Height ("Height", Float) = 0.5
//        _Width ("Width", Float) = 1.5
//        _Narrowness ("Narrowness", Float) = 1.5
//        _Speed ("Speed (units per second)", Float) = 0.25
//        _Start ("Start (X, Z)", Vector) = (0,0,0,0)
//        [Toggle] _MotionX ("Motion X", Float) = 1
//        [Toggle] _MotionZ ("Motion Z", Float) = 1
//        [Toggle] _Cyclic ("Cyclic", Float) = 1
//   2. #include "Packages/com.logiccuteguy.blendproperty/Runtime/Shaders/BlendWave.cginc"
//   3. First line of your vertex function: BlendWave(v.vertex, v.normal);
//      Also call it in the ShadowCaster pass so the shadows follow, and add "DisableBatching"="True" to Tags
//      (batching merges meshes into world space and the wave would use the wrong origin).
// If your shader already declares one of these names (e.g. _Speed, also used by BlendExplode.cginc), rename it there.
#ifndef BLEND_WAVE_INCLUDED
#define BLEND_WAVE_INCLUDED

#include "UnityCG.cginc"

float _Height, _Width, _Narrowness, _Speed, _MotionX, _MotionZ, _Cyclic;
float4 _Start;

float BlendWaveLift(float3 p)
{
    float w = max(_Width, 1e-4);
    float dx = p.x - _Start.x, dz = p.z - _Start.y;
    float dist = (_MotionX > 0.5 && _MotionZ > 0.5) ? sqrt(dx * dx + dz * dz) : (_MotionX > 0.5 ? dx : dz);
    float x = dist - _Time.y * _Speed;
    if (_Cyclic > 0.5) { float m = x + w; x = m - 2 * w * floor(m / (2 * w)) - w; }
    if (x <= -w || x >= w || (_MotionX < 0.5 && _MotionZ < 0.5)) return 0;
    float a = x * _Narrowness, b = w * _Narrowness;
    return (exp(-a * a) - exp(-b * b)) * _Height;
}

void BlendWave(inout float4 vertex, inout float3 normal)
{
    float3 p = vertex.xyz;
    vertex.y += BlendWaveLift(p);
    // ponytail: tilt the normal by the wave slope; exact for up-facing surfaces, approximate on sides.
    const float e = 0.01;
    float hx = BlendWaveLift(p + float3(e, 0, 0)) - BlendWaveLift(p - float3(e, 0, 0));
    float hz = BlendWaveLift(p + float3(0, 0, e)) - BlendWaveLift(p - float3(0, 0, e));
    float3 slope = normalize(float3(-hx, 2 * e, -hz));
    normal = normalize(normal + slope - float3(0, 1, 0));
}

#endif
