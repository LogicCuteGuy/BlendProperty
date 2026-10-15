// Draws a BlendUdonFluid smoke domain: put it on the domain's Cube. Ray-marches the density texture
// inside the object's box, lit by the main directional light with simple self-shadowing.
// Opaque objects inside the smoke cut it off when the camera depth texture exists
// (in VRChat: a realtime light with shadows in the scene).
Shader "Blend Property/Smoke"
{
    Properties
    {
        _Density ("Density (set by BlendUdonFluid)", 3D) = "black" {}
        _Color ("Smoke Color", Color) = (0.8, 0.8, 0.82, 1)
        _Absorption ("Thickness", Range(0, 100)) = 20
        _Shadow ("Self Shadow", Range(0, 1)) = 0.8
        _Ambient ("Ambient", Range(0, 1)) = 0.35
        [IntRange] _Steps ("Steps", Range(8, 128)) = 48
        [HDR] _FireColor ("Heat Glow", Color) = (0, 0, 0, 1)
        [HDR] _FlameColor ("Flame Color", Color) = (6, 2.2, 0.5, 1)
        [HDR] _FlameEdge ("Flame Edge Color", Color) = (2, 0.25, 0.05, 1)
        _FlameStrength ("Flame Brightness", Range(0, 20)) = 6
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            Blend One OneMinusSrcAlpha
            ZWrite Off ZTest Always Cull Front // back faces: still drawn with the camera inside the box

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _Density;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _Color, _FireColor, _FlameColor, _FlameEdge, _LightColor0;
            float _Absorption, _Shadow, _Ambient, _Steps, _FlameStrength;

            struct appdata { float4 vertex : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 objPos : TEXCOORD0;
                float4 screen : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.objPos = v.vertex.xyz;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            // ray / unit box (-0.5..0.5) in object space: x = entry, y = exit
            float2 Box(float3 ro, float3 rd)
            {
                float3 inv = 1 / rd;
                float3 t0 = (-0.5 - ro) * inv, t1 = (0.5 - ro) * inv;
                float3 tmin = min(t0, t1), tmax = max(t0, t1);
                return float2(max(max(tmin.x, tmin.y), tmin.z), min(min(tmax.x, tmax.y), tmax.z));
            }

            // r density, g heat, b fuel, a flame
            float4 Sample(float3 p) { return tex3Dlod(_Density, float4(p + 0.5, 0)); }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rd = normalize(i.objPos - camObj);
                float2 t = Box(camObj, rd);
                t.x = max(t.x, 0);

                // stop at opaque geometry (camera depth texture), converted to object-space ray length
                float3 rdWorld = mul((float3x3)unity_ObjectToWorld, rd);
                float worldPerObj = length(rdWorld);
                float3 viewDir = normalize(rdWorld);
                float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen)));
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                t.y = min(t.y, eye / max(dot(viewDir, camFwd), 1e-4) / worldPerObj);
                if (t.y <= t.x) discard;

                float3 lightObj = normalize(mul((float3x3)unity_WorldToObject, _WorldSpaceLightPos0.xyz));
                int steps = (int)_Steps;
                float dt = (t.y - t.x) / steps;
                // per-pixel jitter hides the slicing
                float jitter = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715))));
                float k = _Absorption * worldPerObj; // absorption per object-space unit
                float trans = 1;
                float3 col = 0;
                [loop] for (int s = 0; s < steps; s++)
                {
                    float3 p = camObj + rd * (t.x + (s + jitter) * dt);
                    float4 d = Sample(p);
                    // flame: light of its own, partly hidden by the smoke in front of it
                    if (d.a > 1e-3)
                        col += trans * lerp(_FlameEdge.rgb, _FlameColor.rgb, saturate(d.a * 1.5)) * d.a * _FlameStrength * dt * worldPerObj;
                    if (d.r < 1e-3) continue;
                    // light reaching this point: a few samples toward the light
                    float shadow = 0;
                    [unroll] for (int l = 1; l <= 4; l++) shadow += Sample(p + lightObj * (0.06 * l)).r;
                    float lit = exp(-shadow * 0.06 * k * _Shadow);
                    float a = 1 - exp(-d.r * k * dt);
                    float3 c = _Color.rgb * (_Ambient + lit * _LightColor0.rgb) + _FireColor.rgb * saturate(d.g);
                    col += trans * a * c;
                    trans *= 1 - a;
                    if (trans < 0.01) break;
                }
                return float4(col, 1 - trans);
            }
            ENDCG
        }
    }
}
