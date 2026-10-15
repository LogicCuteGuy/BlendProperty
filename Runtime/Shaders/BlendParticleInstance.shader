// Draws a BlendUdonParticles system with Render As = Object (Blender's Particle Instance): the bake copies Instance
// Mesh once per particle, and each copy reads its particle from the state texture, so it moves, turns and is lit
// like a normal object. Align To Velocity points the mesh's forward (+Z) along the particle's motion; otherwise each
// copy gets a random turn, plus Spin turns over its life. Size scales the mesh (1 = its own size), End Size over life.
Shader "Blend Property/Particle Instance"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
        _EndSize ("End Size", Range(0, 4)) = 1
        [Toggle] _AlignVelocity ("Align To Velocity", Float) = 0
        _Spin ("Spin (turns per life)", Float) = 1
        // set by BlendUdonParticles
        [HideInInspector] _State ("State", 2D) = "black" {}
        [HideInInspector] _Size ("Size", Float) = 1
        [HideInInspector] _SizeRandom ("Size Random", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma target 3.5

        sampler2D _MainTex;
        fixed4 _Color;
        half _Glossiness, _Metallic;
        #include "BlendParticleInstance.cginc"

        struct Input { float2 uv_MainTex; };

        void vert(inout appdata_full v) { BlendParticleInstance(v.vertex, v.normal, v.texcoord1); }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
