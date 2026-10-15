// Blender's Explode modifier on the GPU, for BlendUdonExplode: every face of the baked mesh is its own piece and,
// once exploded, flies out along its normal (plus a random push), falls with gravity, tumbles, and stops where it
// lands on the floor height. Worked out from the time alone, so it costs nothing while waiting and Udon only sends
// the start time. Lit like Standard; both sides drawn, since the pieces are open shells. The motion itself is in
// BlendExplode.cginc, so your own shader can include it instead (BlendUdonExplode then keeps your material).
Shader "Blend Property/Explode"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo", 2D) = "white" {}
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        [HDR] _EmissionColor ("Emission", Color) = (0, 0, 0, 1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        // set by BlendUdonExplode
        [HideInInspector] _ExplodeTime ("Explode Time", Float) = -1
        [HideInInspector] _Speed ("Speed", Float) = 3
        [HideInInspector] _Random ("Random", Float) = 1.5
        [HideInInspector] _Spin ("Spin", Float) = 6
        [HideInInspector] _Gravity ("Gravity", Vector) = (0, -9.81, 0, 0)
        [HideInInspector] _Floor ("Floor", Float) = -100000
        [HideInInspector] _Lifetime ("Lifetime", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }
        Cull Off

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma target 3.5

        #include "BlendExplode.cginc"

        sampler2D _MainTex, _BumpMap, _EmissionMap;
        fixed4 _Color, _EmissionColor;
        half _Glossiness, _Metallic, _BumpScale;

        struct Input { float2 uv_MainTex; };

        void vert(inout appdata_full v) { BlendExplode(v.vertex, v.normal, v.texcoord2, v.texcoord3); }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Normal = UnpackScaleNormal(tex2D(_BumpMap, IN.uv_MainTex), _BumpScale);
            o.Emission = tex2D(_EmissionMap, IN.uv_MainTex).rgb * _EmissionColor.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
