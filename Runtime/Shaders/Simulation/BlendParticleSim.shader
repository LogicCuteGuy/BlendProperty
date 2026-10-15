// GPU particles for BlendUdonParticles (Blender Particle System Emitter + Unity Particle System modules). One Custom
// Render Texture pass, driven from Udon. State texture (2W x H), one texel per particle in each half:
//   left:  position (simulation space: world, or the emitter's local space), normalized age (0 = born, 1+ = dead)
//   right: velocity (simulation space), frame it was born on (seeds its randoms, here and in the draw shaders)
// Slots are a ring: each frame Udon says how many are born (_EmitCount) from slot _Head on, so the rate, bursts and
// Emit() all just move the head. When every slot is alive the oldest are reused (Max Particles).
// Shapes: Mesh uses a baked sample of the emitter surface (_Spawn: 64 x 64 positions, then 64 x 64 normals); the
// others (Sphere, Hemisphere, Cone, Box, Circle, Edge) are made here, in object space, pointing up (+Y).
Shader "Hidden/Blend Property/Particle Sim"
{
    Properties
    {
        _Spawn ("Spawn points (positions | normals)", 2D) = "black" {}
        // baked values: declared so the material saves them
        _W ("State width (per half)", Float) = 1
        _H ("State height", Float) = 1
        _Count ("Particle count", Float) = 0
    }
    SubShader
    {
        Lighting Off Blend One Zero ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Step"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCustomRenderTexture.cginc"
            #include "BlendForceField.cginc"

            sampler2D _Spawn;
            float _W, _H, _Count;
            float4x4 _ObjectToSim, _SimToWorld, _WorldToSim;
            float3 _BaseVel;    // sim space: object-aligned + emitter velocity share
            float3 _EmitterVel; // sim space: the emitter's motion, so a frame's births spread along its path
            float3 _VelOverLife; // sim space, added to the motion (Unity Velocity over Lifetime, linear)
            float3 _Gravity;    // world
            float _SimTime, _Dt, _Reset, _Frame, _Head, _EmitCount, _EmitDt;
            float _Lifetime, _LifeRandom, _Normal, _SpeedRandom, _Random, _Keep, _Brownian, _MaxSpeed;
            float _Shape, _ShapeRadius, _Thickness, _ConeAngle;
            float3 _BoxSize;
            float4 _Noise; // strength, frequency, scroll speed
            float _Radius;
            float _SphereCount, _CapsuleCount, _BoxCount;
            float4 _Spheres[8];  // xyz centre, w radius (world)
            float4 _CapsuleA[8]; // xyz end A, w radius
            float4 _CapsuleB[8]; // xyz end B
            float4 _BoxC[8];     // xyz world centre
            float4 _BoxX[8], _BoxY[8], _BoxZ[8]; // xyz world axis, w half extent
            float4 _SphereSurf[8], _CapsuleSurf[8], _BoxSurf[8]; // per collider: x bounce, y friction, z kill

            float4 Fetch(sampler2D s, float2 texel, float2 size) { return tex2Dlod(s, float4((texel + 0.5) / size, 0, 0)); }

            uint Hash(uint x)
            {
                x ^= x >> 16; x *= 0x7feb352du;
                x ^= x >> 15; x *= 0x846ca68bu;
                x ^= x >> 16;
                return x;
            }
            float Rand(uint x) { return Hash(x) * (1.0 / 4294967296.0); }
            float3 RandDir(uint s)
            {
                float z = Rand(s) * 2 - 1, a = Rand(s + 1) * 6.2831853;
                float r = sqrt(saturate(1 - z * z));
                return float3(r * cos(a), r * sin(a), z);
            }

            // birth point and direction in object space (Unity Shape module; Radius Thickness 0 = surface, 1 = volume)
            void Spawn(uint seed, out float3 p, out float3 d)
            {
                float u1 = Rand(seed + 21), u2 = Rand(seed + 22), u3 = Rand(seed + 23);
                float a = u1 * 6.2831853;
                float3 radial = float3(cos(a), 0, sin(a));
                float disk = lerp(1, sqrt(u3), _Thickness);
                if (_Shape < 0.5) // Mesh
                {
                    uint s = Hash(seed) & 4095u;
                    float2 st = float2(s & 63u, s >> 6);
                    p = Fetch(_Spawn, st, float2(128, 64)).xyz;
                    d = Fetch(_Spawn, st + float2(64, 0), float2(128, 64)).xyz;
                }
                else if (_Shape < 2.5) // Sphere, Hemisphere: outward
                {
                    d = RandDir(seed + 24);
                    if (_Shape > 1.5) d.y = abs(d.y);
                    p = d * _ShapeRadius * lerp(1, pow(u3, 1.0 / 3.0), _Thickness);
                }
                else if (_Shape < 3.5) // Cone: up, opening by Angle towards the rim
                {
                    p = radial * _ShapeRadius * disk;
                    float s, c; sincos(_ConeAngle * disk, s, c);
                    d = float3(0, c, 0) + radial * s;
                }
                else if (_Shape < 4.5) { p = (float3(u1, u2, u3) - 0.5) * _BoxSize; d = float3(0, 1, 0); } // Box: up
                else if (_Shape < 5.5) { p = radial * _ShapeRadius * disk; d = radial; }                 // Circle: outward
                else { p = float3((u1 * 2 - 1) * _ShapeRadius, 0, 0); d = float3(0, 1, 0); }           // Edge: up
            }

            float3 PushOut(float3 x, float3 c, float r, inout float3 nrm)
            {
                float3 d = x - c;
                float l = length(d);
                if (l >= r || l <= 1e-6) return x;
                nrm = d / l;
                return c + nrm * r;
            }

            // out of the box through its nearest face
            float3 PushBox(float3 x, int b, inout float3 nrm)
            {
                float3 d = x - _BoxC[b].xyz;
                float3 ax = _BoxX[b].xyz, ay = _BoxY[b].xyz, az = _BoxZ[b].xyz;
                float3 l = float3(dot(d, ax), dot(d, ay), dot(d, az));
                float3 pen = float3(_BoxX[b].w, _BoxY[b].w, _BoxZ[b].w) + _Radius - abs(l);
                if (any(pen <= 0)) return x;
                float3 n; float depth;
                if (pen.x < pen.y && pen.x < pen.z) { n = ax * sign(l.x); depth = pen.x; }
                else if (pen.y < pen.z) { n = ay * sign(l.y); depth = pen.y; }
                else { n = az * sign(l.z); depth = pen.z; }
                nrm = n;
                return x + n * depth;
            }

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 size = float2(2 * _W, _H);
                float2 px = floor(IN.localTexcoord.xy * size);
                bool isVel = px.x >= _W;
                float2 c = float2(isVel ? px.x - _W : px.x, px.y);
                float i = c.y * _W + c.x;
                if (i >= _Count || _Reset > 0.5) return float4(0, 0, 0, isVel ? 0 : 2); // dead

                float4 p = Fetch(_SelfTexture2D, c, size);
                float4 v = Fetch(_SelfTexture2D, c + float2(_W, 0), size);
                uint id = (uint)i;
                float j = fmod(i - _Head + _Count, _Count); // its place among this frame's births
                if (j < _EmitCount && v.w != _Frame) // born this frame (once, not again in later substeps)
                {
                    uint seed = Hash(id * 747796405u + (uint)_Frame * 2891336453u);
                    float life = max(_Lifetime, 1e-3) * (1 - _LifeRandom * Rand(seed + 7));
                    float3 lp, ld;
                    Spawn(seed, lp, ld);
                    float3 dir = mul((float3x3)_ObjectToSim, ld);
                    dir = dot(dir, dir) > 1e-12 ? normalize(dir) : 0;
                    float speed = _Normal * (1 - _SpeedRandom * Rand(seed + 11));
                    float3 vel = dir * speed + _BaseVel + RandDir(seed + 3) * (_Random * Rand(seed + 5));
                    float age = _EmitDt * (1 - (j + 0.5) / max(_EmitCount, 1)); // the frame's first births are the oldest
                    float3 pos = mul(_ObjectToSim, float4(lp, 1)).xyz + (vel - _EmitterVel) * age;
                    return isVel ? float4(vel, _Frame) : float4(pos, age / life);
                }
                if (p.w >= 1) return isVel ? v : p; // dead

                uint seed = Hash(id * 747796405u + (uint)v.w * 2891336453u);
                float life = max(_Lifetime, 1e-3) * (1 - _LifeRandom * Rand(seed + 7));
                float3 xw = mul(_SimToWorld, float4(p.xyz, 1)).xyz;
                float3 acc = _Gravity + FieldAccel(xw, mul((float3x3)_SimToWorld, v.xyz));
                if (_Noise.x > 0) acc += FieldTurbulence(xw * _Noise.y + id % 7u, _SimTime * _Noise.z) * _Noise.x;
                float3 vel = v.xyz + mul((float3x3)_WorldToSim, acc) * _Dt;
                if (_Brownian > 0) vel += RandDir(Hash(id * 1103515245u + (uint)_Frame)) * (_Brownian * sqrt(_Dt));
                vel *= _Keep;
                float sp = length(vel);
                if (_MaxSpeed > 0 && sp > _MaxSpeed) vel *= _MaxSpeed / sp; // Unity Limit Velocity
                float3 x = p.xyz + (vel + _VelOverLife) * _Dt;
                float age = p.w + _Dt / life;

                // collide in world space
                float3 nrm = 0, surf = 0, hn;
                xw = mul(_SimToWorld, float4(x, 1)).xyz;
                for (int sc = 0; sc < 8; sc++)
                {
                    if (sc >= (int)_SphereCount) break;
                    hn = 0;
                    xw = PushOut(xw, _Spheres[sc].xyz, _Spheres[sc].w + _Radius, hn);
                    if (dot(hn, hn) > 0) { nrm = hn; surf = _SphereSurf[sc].xyz; }
                }
                for (int cc = 0; cc < 8; cc++)
                {
                    if (cc >= (int)_CapsuleCount) break;
                    float3 a = _CapsuleA[cc].xyz, ab = _CapsuleB[cc].xyz - a;
                    float t = saturate(dot(xw - a, ab) / max(dot(ab, ab), 1e-9));
                    hn = 0;
                    xw = PushOut(xw, a + ab * t, _CapsuleA[cc].w + _Radius, hn);
                    if (dot(hn, hn) > 0) { nrm = hn; surf = _CapsuleSurf[cc].xyz; }
                }
                for (int b = 0; b < 8; b++)
                {
                    if (b >= (int)_BoxCount) break;
                    hn = 0;
                    xw = PushBox(xw, b, hn);
                    if (dot(hn, hn) > 0) { nrm = hn; surf = _BoxSurf[b].xyz; }
                }
                if (dot(nrm, nrm) > 0) // hit: bounce off (Blender Damping = 1 - Bounce) and lose some sliding, per the last collider hit
                {
                    x = mul(_WorldToSim, float4(xw, 1)).xyz;
                    nrm = normalize(mul((float3x3)_WorldToSim, nrm));
                    if (surf.z > 0.5) age = 2;
                    float vn = dot(vel, nrm);
                    if (vn < 0) vel = (vel - nrm * vn) * (1 - surf.y) - nrm * (vn * surf.x);
                }
                return isVel ? float4(vel, v.w) : float4(x, age);
            }
            ENDCG
        }
    }
}
