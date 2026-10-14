// Animated Wave on the GPU (same maths as the Wave modifier, radial on the local XZ plane, lifting Y).
// No script needed, so it works in VRChat worlds, standalone builds and any mesh size. Built-in render pipeline.
// Distances are in the mesh's own units. Subdivide flat meshes first (Subdivision modifier) so there are vertices to move.
Shader "Blend Property/Wave"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0
        [Header(Wave)]
        _Height ("Height", Float) = 0.5
        _Width ("Width", Float) = 1.5
        _Narrowness ("Narrowness", Float) = 1.5
        _Speed ("Speed (units per second)", Float) = 0.25
        _Start ("Start (X, Z)", Vector) = (0,0,0,0)
        [Toggle] _MotionX ("Motion X", Float) = 1
        [Toggle] _MotionZ ("Motion Z", Float) = 1
        [Toggle] _Cyclic ("Cyclic", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "DisableBatching"="True" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;
        half _Glossiness, _Metallic;
        float _Height, _Width, _Narrowness, _Speed, _MotionX, _MotionZ, _Cyclic;
        float4 _Start;

        struct Input { float2 uv_MainTex; };

        float Lift(float3 p)
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

        void vert(inout appdata_full v)
        {
            float3 p = v.vertex.xyz;
            v.vertex.y += Lift(p);
            // ponytail: tilt the normal by the wave slope; exact for up-facing surfaces, approximate on sides.
            const float e = 0.01;
            float hx = Lift(p + float3(e, 0, 0)) - Lift(p - float3(e, 0, 0));
            float hz = Lift(p + float3(0, 0, e)) - Lift(p - float3(0, 0, e));
            float3 slope = normalize(float3(-hx, 2 * e, -hz));
            v.normal = normalize(v.normal + slope - float3(0, 1, 0));
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
