// Blender's Ocean modifier on the GPU: a moving sea on the mesh's local XZ plane, lifting Y. No script needed.
// Instead of Blender's FFT grid it adds up Waves Gerstner waves picked from the Pierson-Moskowitz spectrum for the
// Wind Velocity (sizes from Spatial Size down to Smallest Wave), so heights are in metres and it never repeats.
// Choppiness pushes the vertices sideways into sharp crests; foam shows where they pinch (low Jacobian).
// Normals and foam are worked out per pixel, so a coarse mesh still looks detailed; the shape needs vertices
// (a subdivided plane). Distances are in the mesh's own units. Built-in render pipeline.
Shader "Blend Property/Ocean"
{
    Properties
    {
        _Color ("Water Color", Color) = (0.02, 0.09, 0.14, 1)
        _Glossiness ("Smoothness", Range(0, 1)) = 0.92
        [Header(Ocean)]
        _Waves ("Waves (quality)", Range(4, 64)) = 32
        _TimeScale ("Time Scale", Float) = 1
        _SpatialSize ("Spatial Size (largest wave)", Float) = 50
        _SmallestWave ("Smallest Wave", Float) = 0.3
        _WaveScale ("Wave Scale", Float) = 1
        _Choppiness ("Choppiness", Range(0, 2)) = 1
        _WindVelocity ("Wind Velocity (m/s)", Float) = 30
        _Alignment ("Alignment", Range(0, 10)) = 0
        _Direction ("Direction (degrees)", Float) = 0
        _Damping ("Damping (against the wind)", Range(0, 1)) = 0.5
        _Depth ("Depth", Float) = 200
        _Seed ("Random Seed", Float) = 0
        [Header(Foam)]
        [Toggle] _Foam ("Generate Foam", Float) = 1
        _FoamCoverage ("Foam Coverage", Range(0, 1)) = 0.5
        _FoamColor ("Foam Color", Color) = (0.9, 0.95, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma target 3.5

        fixed4 _Color, _FoamColor;
        half _Glossiness;
        #include "BlendOcean.cginc"

        struct Input { float2 rest; };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.rest = v.vertex.xz;
            BlendOcean(v.vertex, v.normal);
            // fixed basis: tangent space = local (x, z, y), so the per-pixel normal maps straight in
            v.normal = float3(0, 1, 0);
            v.tangent = float4(1, 0, 0, -1);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float3 n;
            float foam = BlendOceanPixel(IN.rest, n);
            o.Normal = float3(n.x, n.z, n.y);
            o.Albedo = lerp(_Color.rgb, _FoamColor.rgb, foam);
            o.Smoothness = lerp(_Glossiness, 0.3, foam);
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
