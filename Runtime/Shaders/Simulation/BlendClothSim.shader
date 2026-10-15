// GPU cloth for BlendUdonGPUCloth. Custom Render Texture passes, driven from Udon:
//   Step:     state texture (2T + E x T): first T columns = position, next T = previous position, one texel per
//             particle. One Verlet substep + one Jacobi pass over the particle's links (averaged, so it can't blow up)
//             + Soft Body shape matching + collisions.
//   Rows, Fit (Soft Body, E = 5 extra columns, run as update zones before Step): column 2T+k holds per-row sums of the
//             predicted positions (k = 0) and of p q^T (k = 1..3, the columns of A); column 2T+4 holds the body's
//             centre (row 0) and rotation (row 1), fitted to A like Müller et al. 2016. Bakes from before Soft Body
//             have E = 0 and no zones.
//   Positions are world space minus Udon's origin (where the object was at its last reset), so they stay small
//   numbers and float precision doesn't make a resting body creep far from the world origin.
//   Vertices: one texel per mesh vertex = its particle's position, read back by Udon (on Soft Body bakes the
//             last two texels are the body's centre and rotation).
// The textures are made by the component's "Bake GPU Cloth" menu.
Shader "Hidden/Blend Property/Cloth Sim"
{
    Properties
    {
        _Rest ("Rest (xyz local, a pin weight)", 2D) = "black" {}
        _Links ("Links (index, length) x2", 2D) = "black" {}
        _Map ("Vertex to particle", 2D) = "black" {}
        _State ("State", 2D) = "black" {}
        // baked values: declared so the material saves them
        _T ("Particle grid size", Float) = 1
        _Relax ("Jacobi relaxation", Float) = 2
        _VW ("Vertex texture width", Float) = 1
        _VH ("Vertex texture height", Float) = 1
        _Extra ("Extra state columns", Float) = 0
        _Count ("Particle count", Float) = 0
        _RestCom ("Rest centre (local)", Vector) = (0, 0, 0, 0)
    }

    CGINCLUDE
    #include "UnityCustomRenderTexture.cginc"
    #include "BlendForceField.cginc"
    sampler2D _Rest, _Links, _Map, _State;
    float _T; // particle grid size: particles live in a T x T square
    float _Extra, _Count;
    float3 _RestCom;
    float4x4 _ObjectToWorld;
    float3 _Accel;    // (gravity + wind) * h^2
    float _Keep;      // 1 - air damping for this substep
    float _GoalStiffness, _Reset, _H2, _ShapeK;
    float3 _ScaleV;   // lossy scale, for the rest offsets q

    float4 Fetch(sampler2D s, float2 texel, float2 size) { return tex2Dlod(s, float4((texel + 0.5) / size, 0, 0)); }
    float2 StateSize() { return float2(2 * _T + _Extra, _T); }
    float3 Rotate(float4 q, float3 v) { return v + 2 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

    // Position of particle i (rest texel r) after integration and goal pull, before the shape pull and constraints.
    float3 Inertial(float2 i, float4 r)
    {
        float3 target = mul(_ObjectToWorld, float4(r.xyz, 1)).xyz;
        if (_Reset > 0.5 || r.a >= 0.999) return target;
        float3 p = Fetch(_SelfTexture2D, i, StateSize()).xyz;
        float3 q = Fetch(_SelfTexture2D, i + float2(_T, 0), StateSize()).xyz;
        float3 n = p + (p - q) * _Keep + _Accel + FieldAccel(p, (p - q) * rsqrt(max(_H2, 1e-12))) * _H2;
        return lerp(n, target, r.a * _GoalStiffness);
    }
    ENDCG

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

            float _Structural, _Bending, _Relax, _Scale, _Distance, _Friction;
            float _SphereCount, _CapsuleCount, _BoxCount;
            float4 _Spheres[8];  // xyz centre, w radius (world)
            float4 _CapsuleA[8]; // xyz end A, w radius
            float4 _CapsuleB[8]; // xyz end B
            float4 _BoxC[8];     // xyz world centre
            float4 _BoxX[8], _BoxY[8], _BoxZ[8]; // xyz world axis, w half extent

            // Position of particle i after integration, before constraints. w = pin / goal weight.
            float3 Predict(float2 i, out float w)
            {
                float4 r = Fetch(_Rest, i, _T.xx);
                w = r.a;
                float3 n = Inertial(i, r);
                if (_ShapeK > 0 && _Reset < 0.5 && w < 0.999) // Soft Body: toward the rest shape, moved and turned to fit
                {
                    float3 com = Fetch(_SelfTexture2D, float2(2 * _T + 4, 0), StateSize()).xyz;
                    float4 rot = Fetch(_SelfTexture2D, float2(2 * _T + 4, 1), StateSize());
                    n = lerp(n, com + Rotate(rot, _ScaleV * (r.xyz - _RestCom)), _ShapeK);
                }
                return n;
            }

            void Link(float idx, float len, float3 x, inout float3 sum, inout float cnt)
            {
                if (idx < 0) return;
                float wj;
                float row = floor((idx + 0.5) / _T); // +0.5: GPU division is a reciprocal multiply
                float3 xj = Predict(float2(idx - row * _T, row), wj);
                float k = len < 0 ? _Bending : _Structural; // negative length marks a bending link
                float3 d = xj - x;
                float l = length(d);
                if (l < 1e-6 || k <= 0) return;
                float share = wj >= 0.999 ? 1 : 0.5; // a pinned neighbour doesn't take its half
                sum += d * ((l - abs(len) * _Scale) / l * k * share);
                cnt += 1;
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
                float3 pen = float3(_BoxX[b].w, _BoxY[b].w, _BoxZ[b].w) + _Distance - abs(l);
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
                float2 px = floor(IN.localTexcoord.xy * StateSize());
                if (px.x >= 2 * _T) return Fetch(_SelfTexture2D, px, StateSize()); // Soft Body columns
                bool isPrev = px.x >= _T;
                float2 i = float2(isPrev ? px.x - _T : px.x, px.y);

                if (isPrev) // previous position = position before this substep
                {
                    if (_Reset > 0.5) return float4(mul(_ObjectToWorld, float4(Fetch(_Rest, i, _T.xx).xyz, 1)).xyz, 1);
                    return Fetch(_SelfTexture2D, i, StateSize());
                }

                float w;
                float3 x = Predict(i, w);
                if (_Reset > 0.5 || w >= 0.999) return float4(x, 1);

                float3 sum = 0;
                float cnt = 0;
                [unroll] for (int s = 0; s < 6; s++)
                {
                    float4 L = Fetch(_Links, float2(i.x * 6 + s, i.y), float2(6 * _T, _T));
                    Link(L.x, L.y, x, sum, cnt);
                    Link(L.z, L.w, x, sum, cnt);
                }
                x += sum * (_Relax / max(cnt, 1));

                float3 nrm = 0;
                for (int c = 0; c < 8; c++)
                {
                    if (c >= (int)_SphereCount) break;
                    x = PushOut(x, _Spheres[c].xyz, _Spheres[c].w + _Distance, nrm);
                }
                for (int k = 0; k < 8; k++)
                {
                    if (k >= (int)_CapsuleCount) break;
                    float3 a = _CapsuleA[k].xyz, ab = _CapsuleB[k].xyz - a;
                    float t = saturate(dot(x - a, ab) / max(dot(ab, ab), 1e-9));
                    x = PushOut(x, a + ab * t, _CapsuleA[k].w + _Distance, nrm);
                }
                for (int b = 0; b < 8; b++)
                {
                    if (b >= (int)_BoxCount) break;
                    x = PushBox(x, b, nrm);
                }
                if (_Friction > 0 && dot(nrm, nrm) > 0) // touching: lose part of this substep's sliding
                {
                    float3 dx = x - Fetch(_SelfTexture2D, i, StateSize()).xyz;
                    x -= (dx - nrm * dot(dx, nrm)) * _Friction;
                }
                return float4(x, 1);
            }
            ENDCG
        }

        Pass
        {
            Name "Vertices"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            float _VW, _VH; // vertex texture size

            // origin-relative position: Udon converts to local space when the readback lands, with the transform of that
            // moment, so the readback delay can't push the cloth into colliders while the object moves
            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 px = floor(IN.localTexcoord.xy * float2(_VW, _VH));
                float4 m = Fetch(_Map, px, float2(_VW, _VH)); // xy = state texel
                return Fetch(_State, m.xy, StateSize());
            }
            ENDCG
        }

        Pass
        {
            Name "Rows"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            float3 _Ref; // the object's position: sums relative to it stay small, so floats keep their precision

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 px = floor(IN.localTexcoord.xy * StateSize());
                float k = px.x - 2 * _T;
                if (_ShapeK <= 0 || k < 0 || k > 3.5) return Fetch(_SelfTexture2D, px, StateSize());
                float3 sum = 0;
                for (int x = 0; x < (int)_T; x++)
                {
                    if (px.y * _T + x >= _Count) break;
                    float2 i = float2(x, px.y);
                    float4 r = Fetch(_Rest, i, _T.xx);
                    float3 d = Inertial(i, r) - _Ref;
                    float3 q = _ScaleV * (r.xyz - _RestCom);
                    sum += k < 0.5 ? d : d * (k < 1.5 ? q.x : (k < 2.5 ? q.y : q.z));
                }
                return float4(sum, 0);
            }
            ENDCG
        }

        Pass
        {
            Name "Fit"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            float3 _Ref, _HeldCom;
            float4 _HeldRot;
            float _Held;

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 px = floor(IN.localTexcoord.xy * StateSize());
                float4 self = Fetch(_SelfTexture2D, px, StateSize());
                if (_ShapeK <= 0 || px.x != 2 * _T + 4 || px.y > 1) return self;
                if (_Held > 0.5) return px.y < 0.5 ? float4(_HeldCom, 1) : _HeldRot; // pickup, script or reset moved it
                float3 s0 = 0, a0 = 0, a1 = 0, a2 = 0;
                for (int y = 0; y < (int)_T; y++)
                {
                    s0 += Fetch(_SelfTexture2D, float2(2 * _T, y), StateSize()).xyz;
                    a0 += Fetch(_SelfTexture2D, float2(2 * _T + 1, y), StateSize()).xyz;
                    a1 += Fetch(_SelfTexture2D, float2(2 * _T + 2, y), StateSize()).xyz;
                    a2 += Fetch(_SelfTexture2D, float2(2 * _T + 3, y), StateSize()).xyz;
                }
                if (px.y < 0.5) return float4(_Ref + s0 / max(_Count, 1), 1);
                // columns of A = sum (p - com) q^T = sum (p - ref) q^T, as sum q = 0.
                // Its rotation part (Müller et al. 2016), warm-started from last substep's rotation.
                float4 r = dot(self, self) > 0.5 ? self : _HeldRot;
                for (int it = 0; it < 8; it++)
                {
                    float3 r0 = Rotate(r, float3(1, 0, 0)), r1 = Rotate(r, float3(0, 1, 0)), r2 = Rotate(r, float3(0, 0, 1));
                    float3 omega = (cross(r0, a0) + cross(r1, a1) + cross(r2, a2)) /
                                   (abs(dot(r0, a0) + dot(r1, a1) + dot(r2, a2)) + 1e-9);
                    float w = length(omega);
                    if (w < 1e-6) break;
                    float4 dq = float4(omega / w * sin(w * 0.5), cos(w * 0.5));
                    r = float4(dq.w * r.xyz + r.w * dq.xyz + cross(dq.xyz, r.xyz), dq.w * r.w - dot(dq.xyz, r.xyz)); // dq * r
                }
                return normalize(r);
            }
            ENDCG
        }
    }
}
