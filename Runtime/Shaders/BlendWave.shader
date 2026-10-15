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
        #include "BlendWave.cginc"

        struct Input { float2 uv_MainTex; };

        void vert(inout appdata_full v) { BlendWave(v.vertex, v.normal); }

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
