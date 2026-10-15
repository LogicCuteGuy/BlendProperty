// Draws a BlendUdonFluid Liquid domain: put it on the domain's Cube. Ray-marches the liquid fraction inside the
// object's box to its surface (half full), then shades it like water: sky reflection with Fresnel, a sun highlight,
// and a tint that thickens with the depth of liquid behind the surface.
// Opaque objects inside the box cut it off when the camera depth texture exists
// (in VRChat: a realtime light with shadows in the scene).
Shader "Blend Property/Liquid"
{
    Properties
    {
        _Density ("Liquid (set by BlendUdonFluid)", 3D) = "black" {}
        _Color ("Color", Color) = (0.1, 0.45, 0.6, 1)
        _Absorption ("Murkiness", Range(0, 50)) = 6
        _Smoothness ("Smoothness", Range(0, 1)) = 0.95
        _Ambient ("Ambient", Range(0, 1)) = 0.4
        [IntRange] _Steps ("Steps", Range(16, 160)) = 64
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
            float4 _Color, _LightColor0;
            float _Absorption, _Smoothness, _Ambient, _Steps;

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

            float L(float3 p) { return tex3Dlod(_Density, float4(p + 0.5, 0)).r; }

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

                // march to the first point over half full, then refine between the last two samples
                int steps = (int)_Steps;
                float dt = (t.y - t.x) / steps;
                float jitter = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715))));
                float tHit = -1, prev = t.x;
                [loop] for (int s = 0; s <= steps; s++)
                {
                    float tt = min(t.x + (s + jitter * (s > 0)) * dt, t.y);
                    if (L(camObj + rd * tt) > 0.5)
                    {
                        float a = prev, b = tt;
                        [unroll] for (int r = 0; r < 5; r++)
                        {
                            float m = 0.5 * (a + b);
                            if (L(camObj + rd * m) > 0.5) b = m; else a = m;
                        }
                        tHit = b;
                        break;
                    }
                    prev = tt;
                }
                if (tHit < 0) discard;
                float3 p = camObj + rd * tHit;

                // normal: the liquid fraction's gradient, object -> world (gradients transform by the inverse);
                // liquid already there where the ray enters the box: the box wall is the surface
                float e = 0.04;
                float3 g = float3(L(p + float3(e, 0, 0)) - L(p - float3(e, 0, 0)),
                                  L(p + float3(0, e, 0)) - L(p - float3(0, e, 0)),
                                  L(p + float3(0, 0, e)) - L(p - float3(0, 0, e)));
                if (tHit <= t.x + 1e-4 && t.x > 0)
                {
                    float3 a = abs(p);
                    g = -sign(p) * (a.x > a.y && a.x > a.z ? float3(1, 0, 0) : (a.y > a.z ? float3(0, 1, 0) : float3(0, 0, 1)));
                }
                float3 n = -mul(g, (float3x3)unity_WorldToObject);
                n = dot(n, n) > 1e-10 ? normalize(n) : -viewDir;
                float3 v = -viewDir;

                // liquid between the surface and the far side: deeper looks darker / more coloured
                float thick = 0;
                float dt2 = (t.y - tHit) / 12;
                [unroll] for (int k = 0; k < 12; k++) thick += saturate(L(p + rd * ((k + 0.5) * dt2)) * 2 - 0.5) * dt2;
                float body = 1 - exp(-thick * worldPerObj * _Absorption);

                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float diffuse = saturate(dot(n, lightDir)) * 0.5 + 0.5;
                float3 tint = _Color.rgb * (_Ambient + diffuse * _LightColor0.rgb);
                float fresnel = 0.02 + 0.98 * pow(1 - saturate(dot(n, v)), 5);
                float3 refl = reflect(-v, n);
                float3 env = DecodeHDR(UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, refl, (1 - _Smoothness) * 6), unity_SpecCube0_HDR);
                float shine = exp2(_Smoothness * 11 + 1);
                float3 spec = _LightColor0.rgb * pow(saturate(dot(n, normalize(lightDir + v))), shine) * _Smoothness;

                float alpha = lerp(body * _Color.a, 1, fresnel);
                float3 col = tint * body * _Color.a * (1 - fresnel) + env * fresnel + spec;
                return float4(col, saturate(alpha));
            }
            ENDCG
        }
    }
}
