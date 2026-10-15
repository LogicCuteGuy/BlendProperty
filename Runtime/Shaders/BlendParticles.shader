// Draws a BlendUdonParticles system: put it on the "Particles" renderer the bake makes. Each quad of the baked mesh
// reads its particle from the state texture and faces the camera (Blender Halo / Unity Billboard; the component's
// Render Mode can also stretch it along its motion, or lay it flat or upright). Colour fades from Color to End Color
// over the particle's life, or follows the component's Color over Lifetime gradient (times Color). Additive 0 = normal
// blending, 1 = glowing (adds light). Unlit. Rotation, Texture Sheet and Start Color come from the component.
Shader "Blend Property/Particles"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1, 1, 1, 1)
        _EndColor ("End Color", Color) = (1, 1, 1, 0)
        _EndSize ("End Size", Range(0, 4)) = 1
        [Toggle] _Round ("Round", Float) = 1
        _Softness ("Edge Softness", Range(0, 1)) = 0.5
        _Additive ("Additive", Range(0, 1)) = 0
        // set by BlendUdonParticles
        [HideInInspector] _State ("State", 2D) = "black" {}
        [HideInInspector] _Size ("Size", Float) = 0.05
        [HideInInspector] _SizeRandom ("Size Random", Float) = 0
        [HideInInspector] _Lifetime ("Lifetime", Float) = 1
        [HideInInspector] _RenderMode ("Render Mode", Float) = 0
        [HideInInspector] _Stretch ("Length Scale, Speed Scale", Vector) = (2, 0, 0, 0)
        [HideInInspector] _Rotation ("Start, Start Random, Speed, Speed Random (radians)", Vector) = (0, 0, 0, 0)
        [HideInInspector] _Sheet ("Tiles X, Tiles Y, Cycles, Random Start", Vector) = (1, 1, 1, 0)
        [HideInInspector] _StartColor ("Start Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _StartColor2 ("Start Color 2", Color) = (1, 1, 1, 1)
        [HideInInspector] _UseColorGrad ("Use Color over Lifetime", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "DisableBatching" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha // premultiplied: covers both normal and additive
            ZWrite Off Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "BlendParticleInstance.cginc" // _State, _Size, size over life, hashing

            sampler2D _MainTex;
            float4 _MainTex_ST, _Color, _EndColor, _StartColor, _StartColor2, _Stretch, _Rotation, _Sheet;
            float _Round, _Softness, _Additive, _Lifetime, _RenderMode, _UseColorGrad;
            float4 _ColorGrad[8]; // Color over Lifetime at life 0, 1/7 .. 1 (linear)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;   // quad corner
                float3 data : TEXCOORD1; // xy = state texel, z = random 0..1
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 uv : TEXCOORD0; // xy = texture, zw = quad corner 0..1
                float4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float4 s = tex2Dlod(_State, float4(v.data.xy, 0, 0));
                float4 sv = tex2Dlod(_State, float4(v.data.x + 0.5, v.data.y, 0, 0)); // velocity, birth frame
                uint seed = BlendParticleHash((uint)sv.w * 747796405u + (uint)(v.data.z * 16777215.0)); // new each life
                float life = saturate(s.w);
                float size = s.w < 1 ? _Size * (1 - _SizeRandom * v.data.z) * BlendParticleSizeOverLife(life) : 0; // dead: no area
                float3 center = mul(_SimToWorld, float4(s.xyz, 1)).xyz;
                float3 toCam = _WorldSpaceCameraPos - center;
                float2 corner = v.uv - 0.5;

                float ang = _Rotation.x + _Rotation.y * (BlendParticleRand(seed) * 2 - 1)
                          + (_Rotation.z + _Rotation.w * (BlendParticleRand(seed + 1) * 2 - 1)) * life * _Lifetime;
                float sn, cs; sincos(ang, sn, cs);
                float2 rc = float2(corner.x * cs - corner.y * sn, corner.x * sn + corner.y * cs);
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz; // Billboard
                if (_RenderMode > 2.5) // Vertical: upright, turning only around Y to face the camera
                {
                    up = float3(0, 1, 0);
                    right = normalize(cross(up, float3(toCam.x, 0, toCam.z) + float3(1e-5, 0, 0)));
                }
                else if (_RenderMode > 1.5) { right = float3(1, 0, 0); up = float3(0, 0, 1); } // Horizontal: flat, facing up
                float3 world = center + (right * rc.x + up * rc.y) * size;
                if (_RenderMode > 0.5 && _RenderMode < 1.5) // Stretched: long side along the motion, broad side to the camera
                {
                    float3 vel = mul((float3x3)_SimToWorld, sv.xyz);
                    float speed = length(vel);
                    float3 axis = speed > 1e-4 ? vel / speed : UNITY_MATRIX_V[1].xyz;
                    float3 side = cross(axis, toCam);
                    side = dot(side, side) > 1e-10 ? normalize(side) : UNITY_MATRIX_V[0].xyz;
                    world = center + side * (corner.x * size) + axis * (corner.y * size * (_Stretch.x + speed * _Stretch.y));
                }
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1));

                // Texture Sheet Animation: frames left to right, top to bottom
                float2 tiles = max(1, floor(_Sheet.xy));
                float frames = tiles.x * tiles.y;
                float f = floor(frac(life * _Sheet.z + BlendParticleRand(seed + 2) * _Sheet.w) * frames);
                float2 cell = float2(fmod(f, tiles.x), tiles.y - 1 - floor(f / tiles.x));
                o.uv = float4(TRANSFORM_TEX((v.uv + cell) / tiles, _MainTex), v.uv);

                float4 overLife = _UseColorGrad > 0.5 ? _Color * BlendParticleCurve(_ColorGrad, life) : lerp(_Color, _EndColor, life);
                o.color = overLife * lerp(_StartColor, _StartColor2, BlendParticleRand(seed + 3));
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv.xy) * i.color;
                if (_Round > 0.5)
                {
                    float r = length(i.uv.zw * 2 - 1);
                    c.a *= saturate((1 - r) / max(_Softness, 0.02));
                }
                return float4(c.rgb * c.a, c.a * (1 - _Additive));
            }
            ENDCG
        }
    }
}
