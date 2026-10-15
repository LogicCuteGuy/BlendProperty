// Liquid for BlendUdonFluid (Blender domain type Liquid): Position Based Fluids (Macklin & Müller 2013) on particles.
// Positions are moved until every particle has its rest density, instead of pushing with a pressure, so the liquid
// stays incompressible and calm at any step length (no springy bounce).
// One 2D double-buffered Custom Render Texture of five S x S regions side by side, particle i at texel (i % S, i / S):
//   0: position (metres in the box, 0..size) xyz, w 1 = in use
//   1: velocity (metres/s along the box axes) xyz, w the density constraint's lambda
//   2: predicted position xyz
//   3: (grid cell key, particle index), sorted by key with a bitonic sort (one zone per sort step)
//   4: per grid cell: first and end index of its particles in the sorted list
// Zones per step: Predict, Sort x (log2 N)(log2 N + 1)/2, Cells, (Lambda, Delta) x iterations, Velocity, Viscosity.
// A 3D CRT (pass Gather) turns the particles into the liquid fraction the Blend Property/Liquid material draws.
Shader "Hidden/Blend Property/Liquid Sim"
{
    Properties
    {
        _Sim ("Simulation (for Gather)", 2D) = "black" {}
        // baked values: declared so the materials save them
        _S ("Particle texture side", Float) = 64
        _Size ("Box size in metres", Vector) = (1, 1, 1, 0)
        _Grid ("Grid cells per axis", Vector) = (1, 1, 1, 0)
        _Spacing ("Particle spacing", Float) = 0.05
        _H ("Kernel radius", Float) = 0.1
        _Rest ("Rest density", Float) = 1
        _Eps ("Relaxation", Float) = 1
        _Scorr ("Surface spacing", Float) = 0
        _SortStart ("First sort zone", Float) = 1
    }

    CGINCLUDE
    #include "UnityCustomRenderTexture.cginc"
    #include "BlendForceField.cginc"
    sampler2D _Sim;
    float _S, _Spacing, _H, _Rest, _Eps, _Scorr, _SortStart;
    float3 _Size, _Grid;
    float4x4 _DomainToWorld, _WorldToDomain; // the domain object's local box (-0.5..0.5) <-> world
    float _Dt, _Reset, _Fill, _Seed, _Viscosity;
    float3 _Gravity;      // world m/s²
    float _ObstacleCount;
    float4 _ObstacleA[8]; // xyz world centre, w radius
    float _FlowCount;
    float4 _FlowA[8];     // xyz world centre, w radius
    float4 _FlowB[8];     // xyz world velocity
    float4 _FlowC[8];     // y 1 = start with the velocity, z first particle to switch on, w how many

    uint N() { return (uint)_S * (uint)_S; }
    uint Cells() { return (uint)(_Grid.x * _Grid.y * _Grid.z); }

    float4 Load(sampler2D t, uint i, uint region)
    {
        float2 px = float2(region * (uint)_S + i % (uint)_S, i / (uint)_S) + 0.5;
        return tex2Dlod(t, float4(px / float2(5 * _S, _S), 0, 0));
    }
    float4 Get(uint i, uint region) { return Load(_SelfTexture2D, i, region); }

    // this texel -> its region and particle (or grid cell) index
    void Here(v2f_customrendertexture IN, out uint region, out uint i)
    {
        uint2 px = (uint2)(IN.localTexcoord.xy * float2(5 * _S, _S));
        region = px.x / (uint)_S;
        i = px.x - region * (uint)_S + px.y * (uint)_S;
    }

    float3 ToBox(float3 w) { return (mul(_WorldToDomain, float4(w, 1)).xyz + 0.5) * _Size; }
    float3 ToBoxVec(float3 w) { return mul((float3x3)_WorldToDomain, w) * _Size; }
    float3 WorldOf(float3 q) { return mul(_DomainToWorld, float4(q / _Size - 0.5, 1)).xyz; }
    float3 ToWorldVec(float3 v) { return mul((float3x3)_DomainToWorld, v / _Size); }

    // SPH kernels (Müller 2003): Poly6 for density, Spiky's gradient for the position corrections
    float Poly6(float r2)
    {
        float d = max(_H * _H - r2, 0);
        return 315 / (64 * UNITY_PI * pow(_H, 9)) * d * d * d;
    }
    float3 Spiky(float3 d)
    {
        float r = length(d);
        if (r >= _H || r < 1e-6) return 0;
        float a = _H - r;
        return -45 / (UNITY_PI * pow(_H, 6)) * a * a * d / r;
    }

    // the box walls count as liquid beyond them, so particles at a wall aren't squashed together for lack of
    // neighbours. Kernel weight past a wall at distance u (in kernel radii) of a uniform half-space, Poly6 exactly:
    // F(u) = 1/2 - 315/256 (u - 4u³/3 + 6u⁵/5 - 4u⁷/7 + u⁹/9); returns the walls' density / rest density, grad: its gradient
    float Walls(float3 p, out float3 grad)
    {
        float sum = 0;
        grad = 0;
        [unroll] for (int k = 0; k < 6; k++)
        {
            int ax = k % 3;
            float3 n = 0;
            n[ax] = k < 3 ? 1 : -1;                              // into the box
            float u = (k < 3 ? p[ax] : _Size[ax] - p[ax]) / _H;
            if (u >= 1) continue;
            u = max(u, 0);
            float u2 = u * u;
            sum += 0.5 - 315.0 / 256 * u * (1 + u2 * (-4.0 / 3 + u2 * (6.0 / 5 + u2 * (-4.0 / 7 + u2 / 9))));
            float a = 1 - u2;
            grad -= 315.0 / 256 / _H * a * a * a * a * n;
        }
        return sum;
    }

    int3 CellOf(float3 p) { return clamp((int3)floor(p / _H), 0, (int3)_Grid - 1); }
    uint Key(int3 c) { return c.x + (uint)_Grid.x * (c.y + (uint)_Grid.y * c.z); }

    // loop over the particles j in the 27 grid cells around p (ponytail: at most 48 per cell, a crowded cell skips some)
    #define NEIGHBOURS_BEGIN(tex, p) { int3 c0_ = CellOf(p); \
        [loop] for (uint n_ = 0; n_ < 27; n_++) { \
            int3 c_ = c0_ + int3(n_ % 3, (n_ / 3) % 3, n_ / 9) - 1; \
            if (any(c_ < 0) || any(c_ >= (int3)_Grid)) continue; \
            float2 se_ = Load(tex, Key(c_), 4).xy; \
            uint end_ = (uint)min(se_.y, se_.x + 48); \
            [loop] for (uint k_ = (uint)se_.x; k_ < end_; k_++) { uint j = (uint)Load(tex, k_, 3).y;
    #define NEIGHBOURS_END } } }

    float Hash(uint n)
    {
        n = (n << 13) ^ n;
        n = n * (n * n * 15731u + 789221u) + 1376312589u;
        return (n & 0x7fffffffu) / 2147483647.0;
    }

    // walls and effectors push the point out
    float3 Collide(float3 p)
    {
        float r = _Spacing * 0.5;
        [loop] for (int k = 0; k < 8; k++)
        {
            if (k >= (int)_ObstacleCount) break;
            float3 o = ToBox(_ObstacleA[k].xyz), d = p - o;
            float l = length(d), R = _ObstacleA[k].w + r;
            if (l < R) p = o + (l > 1e-6 ? d / l : float3(0, 1, 0)) * R;
        }
        return clamp(p, r, _Size - r);
    }

    // position (w: in use) and velocity at the start of the step:
    // a reset stacks particles up to Fill Level, Liquid sources switch unused ones on inside their sphere
    void Start(uint i, out float4 x, out float3 v)
    {
        x = Get(i, 0);
        v = Get(i, 1).xyz;
        if (_Reset > 0.5)
        {
            uint3 n = (uint3)max(floor(_Size / _Spacing), 1);
            uint3 c = uint3(i % n.x, i / (n.x * n.z), (i / n.x) % n.z);
            float3 jitter = (float3(Hash(i * 3), Hash(i * 3 + 1), Hash(i * 3 + 2)) - 0.5) * 0.02;
            float3 p = (c + 0.5 + jitter) * _Spacing;
            x = float4(p, c.y < n.y && p.y < _Fill * _Size.y ? 1 : 0);
            v = 0;
            return;
        }
        if (x.w > 0.5) return;
        [loop] for (int f = 0; f < 8; f++)
        {
            if (f >= (int)_FlowCount) break;
            if ((i + N() - (uint)_FlowC[f].z) % N() >= (uint)_FlowC[f].w) continue;
            uint s = i * 7 + (uint)_Seed * 7919;
            float3 dir = normalize(float3(Hash(s), Hash(s + 1), Hash(s + 2)) - 0.5 + 1e-4);
            float3 q = ToBox(_FlowA[f].xyz + dir * _FlowA[f].w * pow(Hash(s + 3), 1.0 / 3));
            if (any(q < 0) || any(q > _Size)) continue; // the part of the source outside the box adds nothing
            x = float4(Collide(q), 1);
            v = _FlowC[f].y > 0.5 ? ToBoxVec(_FlowB[f].xyz) : 0;
            return;
        }
    }

    // gravity, force fields, then where the particle would go
    float3 Predict(float4 x, inout float3 v)
    {
        if (x.w < 0.5 || _Dt <= 0) return x.xyz;
        v += ToBoxVec(_Gravity + FieldAccel(WorldOf(x.xyz), ToWorldVec(v))) * _Dt;
        // ponytail: speed capped at 2 kernel radii per step so a hard hit can't blow particles apart
        float vmax = 2 * _H / _Dt, sp = length(v);
        if (sp > vmax) v *= vmax / sp;
        return Collide(x.xyz + v * _Dt);
    }

    float4 PredictPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        if (region == 4) return Get(i, 4);
        float4 x; float3 v;
        Start(i, x, v);
        if (region == 0) return x;
        float3 p = Predict(x, v);
        if (region == 1) return float4(v, 0);
        if (region == 2) return float4(p, 0);
        return float4(x.w > 0.5 ? Key(CellOf(p)) : Cells(), i, 0, 0); // unused particles sort to the end
    }

    // one bitonic compare-exchange step; the zone index says which (stage k, distance j)
    float4 SortPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        float4 self = Get(i, region);
        if (region != 3) return self;
        int s = (int)IN.primitiveID - (int)_SortStart;
        uint k = 2, j = 1;
        [loop] for (uint t = 1; t <= 16; t++)
        {
            if (s < (int)t) { k = 1u << t; j = 1u << (t - 1 - s); break; }
            s -= t;
        }
        uint l = i ^ j;
        float4 other = Get(l, 3);
        bool selfFirst = self.x < other.x || (self.x == other.x && self.y < other.y);
        bool keepMin = (l > i) == ((i & k) == 0);
        return selfFirst == keepMin ? self : other;
    }

    uint LowerBound(float key)
    {
        uint lo = 0, hi = N();
        [loop] for (int it = 0; it < 17; it++)
        {
            if (lo >= hi) break;
            uint mid = (lo + hi) / 2;
            if (Get(mid, 3).x < key) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    float4 CellsPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        if (region != 4) return Get(i, region);
        return i < Cells() ? float4(LowerBound(i), LowerBound(i + 1), 0, 0) : 0;
    }

    // lambda: how far the particle's density is over the rest density, scaled by how much moving fixes it
    float4 LambdaPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        float4 self = Get(i, region);
        if (region != 1 || Get(i, 0).w < 0.5) return self;
        float3 p = Get(i, 2).xyz;
        float rho = 0, sum2 = 0;
        float3 gi = 0;
        NEIGHBOURS_BEGIN(_SelfTexture2D, p)
            float3 d = p - Load(_SelfTexture2D, j, 2).xyz;
            rho += Poly6(dot(d, d));
            float3 g = Spiky(d) / _Rest;
            gi += g;
            sum2 += dot(g, g);
        NEIGHBOURS_END
        // only pushes apart: spray and the surface don't clump into blobs
        float3 gw;
        float C = max(rho / _Rest + Walls(p, gw) - 1, 0);
        gi += gw;
        return float4(self.xyz, -C / (sum2 + dot(gi, gi) + _Eps));
    }

    float4 DeltaPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        float4 self = Get(i, region);
        if (region != 2 || Get(i, 0).w < 0.5) return self;
        float3 p = self.xyz, dp = 0;
        float li = Get(i, 1).w, wq = Poly6(0.04 * _H * _H);
        NEIGHBOURS_BEGIN(_SelfTexture2D, p)
            if (j == i) continue;
            float3 d = p - Load(_SelfTexture2D, j, 2).xyz;
            float w = Poly6(dot(d, d)) / wq;
            // small extra push between close particles (Monaghan's artificial pressure): even spacing at the surface
            dp += (li + Load(_SelfTexture2D, j, 1).w - _Scorr * w * w * w * w) * Spiky(d);
        NEIGHBOURS_END
        float3 gw;
        Walls(p, gw);
        return float4(Collide(p + dp / _Rest + li * gw), self.w);
    }

    float4 VelocityPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        float4 self = Get(i, region);
        if (region != 1 || _Dt <= 0) return self;
        float4 x = Get(i, 0);
        return x.w > 0.5 ? float4((Get(i, 2).xyz - x.xyz) / _Dt, 0) : 0;
    }

    // XSPH viscosity (each particle takes on some of its neighbours' average velocity), then the move is kept
    float4 ViscosityPass(v2f_customrendertexture IN)
    {
        uint region, i; Here(IN, region, i);
        float4 self = Get(i, region);
        float4 x = Get(i, 0);
        if (region == 0) return float4(Get(i, 2).xyz, x.w);
        if (region != 1 || x.w < 0.5) return self;
        float3 p = Get(i, 2).xyz, sum = 0;
        float wsum = 0;
        NEIGHBOURS_BEGIN(_SelfTexture2D, p)
            float3 d = p - Load(_SelfTexture2D, j, 2).xyz;
            float w = Poly6(dot(d, d));
            sum += (Load(_SelfTexture2D, j, 1).xyz - self.xyz) * w;
            wsum += w;
        NEIGHBOURS_END
        return float4(self.xyz + _Viscosity * sum / max(wsum, 1e-6), 0);
    }
    ENDCG

    SubShader
    {
        Lighting Off Blend One Zero ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Predict"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return PredictPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Sort"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return SortPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Cells"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return CellsPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Lambda"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return LambdaPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Delta"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return DeltaPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Velocity"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return VelocityPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Viscosity"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return ViscosityPass(IN); }
            ENDCG
        }
        Pass
        {
            Name "Gather"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            // a 3D target: localTexcoord.xyz is this cell's uvw. Liquid fraction from the particles' density,
            // scaled up so a lone drop still shows as a small bead (the surface sits a little outside the outer particles)
            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float3 q = IN.localTexcoord.xyz * _Size;
                float rho = 0;
                NEIGHBOURS_BEGIN(_Sim, q)
                    float4 x = Load(_Sim, j, 0);
                    float3 d = q - x.xyz;
                    rho += Poly6(dot(d, d));
                NEIGHBOURS_END
                return saturate(rho / _Rest * 5);
            }
            ENDCG
        }
    }
}
