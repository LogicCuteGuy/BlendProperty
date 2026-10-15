// Draws BlendUdonHair strands: put it on the "Hair" renderer the bake makes. Each strand is a ribbon facing the camera,
// following a smooth curve (Catmull-Rom) through the simulated points, narrowing from Root Width to Tip Width.
// Shaded along the strand (Kajiya-Kay): soft diffuse plus a highlight from the main directional light, and ambient light.
Shader "Blend Property/Hair"
{
    Properties
    {
        _RootColor ("Root Color", Color) = (0.25, 0.15, 0.08, 1)
        _TipColor ("Tip Color", Color) = (0.6, 0.4, 0.2, 1)
        _SpecColor ("Highlight", Color) = (0.5, 0.45, 0.4, 1)
        _Shininess ("Shininess", Range(2, 256)) = 60
        // set by BlendUdonHair
        [HideInInspector] _State ("State", 2D) = "black" {}
        [HideInInspector] _StateSize ("State size", Vector) = (1, 1, 1, 1)
        [HideInInspector] _P ("Points per strand", Float) = 2
        [HideInInspector] _RootWidth ("Root Width", Float) = 0.004
        [HideInInspector] _TipWidth ("Tip Width", Float) = 0.001
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _State;
            float4 _StateSize, _RootColor, _TipColor, _SpecColor, _LightColor0;
            float _P, _RootWidth, _TipWidth, _Shininess;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;   // x = side 0 / 1, y = position along the strand 0..P-1
                float3 data : TEXCOORD1; // xy = root texel, z = random 0..1
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float3 tangent : TEXCOORD1;
                float2 t : TEXCOORD2; // x = 0 at the root, 1 at the tip; y = random
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 Fetch(float2 texel) { return tex2Dlod(_State, float4((texel + 0.5) / _StateSize.xy, 0, 0)).xyz; }
            // stored relative to the origin in the state's last column
            float3 Point(float2 root, float i) { return Fetch(root + float2(clamp(i, 0, _P - 1), 0)) + Fetch(float2(_StateSize.x - 1, 0)); }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float i = min(floor(v.uv.y), _P - 2), f = v.uv.y - i;
                float3 p0 = Point(v.data.xy, i - 1), p1 = Point(v.data.xy, i), p2 = Point(v.data.xy, i + 1), p3 = Point(v.data.xy, i + 2);
                if (i < 1) p0 = 2 * p1 - p2;             // extend the ends so the curve reaches them
                if (i + 2 > _P - 1) p3 = 2 * p2 - p1;
                float f2 = f * f, f3 = f2 * f;
                float3 pos = 0.5 * (2 * p1 + (p2 - p0) * f + (2 * p0 - 5 * p1 + 4 * p2 - p3) * f2 + (3 * p1 - p0 - 3 * p2 + p3) * f3);
                float3 tan = 0.5 * ((p2 - p0) + 2 * (2 * p0 - 5 * p1 + 4 * p2 - p3) * f + 3 * (3 * p1 - p0 - 3 * p2 + p3) * f2);
                tan = dot(tan, tan) > 1e-12 ? normalize(tan) : float3(0, 1, 0);
                float3 view = _WorldSpaceCameraPos - pos;
                float3 side = cross(tan, view);
                side = dot(side, side) > 1e-12 ? normalize(side) : float3(1, 0, 0);
                float along = v.uv.y / (_P - 1);
                pos += side * ((v.uv.x - 0.5) * lerp(_RootWidth, _TipWidth, along));
                o.pos = mul(UNITY_MATRIX_VP, float4(pos, 1));
                o.world = pos;
                o.tangent = tan;
                o.t = float2(along, v.data.z);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 T = normalize(i.tangent);
                float3 V = normalize(_WorldSpaceCameraPos - i.world);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 H = normalize(L + V);
                float tl = dot(T, L), th = dot(T, H);
                float diffuse = sqrt(saturate(1 - tl * tl)) * 0.75 + 0.25;
                float spec = pow(sqrt(saturate(1 - th * th)), _Shininess) * saturate(dot(L, V) * 0.5 + 0.75);
                float3 albedo = lerp(_RootColor.rgb, _TipColor.rgb, i.t.x) * (0.85 + 0.3 * i.t.y); // a little variation per strand
                float3 ambient = ShadeSH9(float4(normalize(V - T * dot(V, T)), 1));
                float3 col = albedo * (ambient + diffuse * _LightColor0.rgb) + spec * _SpecColor.rgb * _LightColor0.rgb;
                return float4(col * lerp(0.6, 1, i.t.x), 1); // roots a bit darker (self-shadowing)
            }
            ENDCG
        }
    }
}
