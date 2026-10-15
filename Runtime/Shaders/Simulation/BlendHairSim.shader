// GPU hair for BlendUdonHair (Blender Hair + Hair Dynamics). One Custom Render Texture pass per substep, from Udon.
// Strands of P = segments + 1 points sit side by side in rows: point j of strand (b, y) is texel (b * P + j, y).
// State texture (2 * HW + 1 x H): left half = position, right half = previous position, both relative to the origin in
// the last column (the object's position when they were written). Near zero, floats keep the tiny per-substep motion:
// in world space 15 m from the scene origin, rounding alone kept the hair circling about a millimetre a frame.
// Each texel solves its strand from the root out to its own point (follow the leader): Verlet step with a spring toward
// the rest shape (Stiffness), exact segment length, then collisions. The root stays on the mesh.
// The textures are made by the component's "Bake Hair" menu.
Shader "Hidden/Blend Property/Hair Sim"
{
    Properties
    {
        _Rest ("Rest points (xyz local)", 2D) = "black" {}
        // baked values: declared so the material saves them
        _P ("Points per strand", Float) = 2
        _HW ("State width (per half)", Float) = 1
        _H ("State height", Float) = 1
        _Count ("Strand count", Float) = 0
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

            sampler2D _Rest;
            float _P, _HW, _H, _Count;
            float4x4 _ObjectToWorld; // translation relative to _Origin
            float3 _Origin;          // world position the state is stored relative to this frame (colliders too)
            float3 _Accel;   // (gravity + wind) * h^2
            float _Keep, _H2, _Spring, _Reset, _Distance; // _Spring = stiffness ω² * h²
            float _SphereCount, _CapsuleCount, _BoxCount;
            float4 _Spheres[8];  // xyz centre, w radius (relative to _Origin, like all collider positions)
            float4 _CapsuleA[8]; // xyz end A, w radius
            float4 _CapsuleB[8]; // xyz end B
            float4 _BoxC[8];     // xyz world centre
            float4 _BoxX[8], _BoxY[8], _BoxZ[8]; // xyz world axis, w half extent

            float4 Fetch(sampler2D s, float2 texel, float2 size) { return tex2Dlod(s, float4((texel + 0.5) / size, 0, 0)); }

            float3 PushOut(float3 x, float3 c, float r)
            {
                float3 d = x - c;
                float l = length(d);
                return l >= r || l <= 1e-6 ? x : c + d * (r / l);
            }

            // out of the box through its nearest face
            float3 PushBox(float3 x, int b)
            {
                float3 d = x - _BoxC[b].xyz;
                float3 ax = _BoxX[b].xyz, ay = _BoxY[b].xyz, az = _BoxZ[b].xyz;
                float3 l = float3(dot(d, ax), dot(d, ay), dot(d, az));
                float3 pen = float3(_BoxX[b].w, _BoxY[b].w, _BoxZ[b].w) + _Distance - abs(l);
                if (any(pen <= 0)) return x;
                if (pen.x < pen.y && pen.x < pen.z) return x + ax * (sign(l.x) * pen.x);
                if (pen.y < pen.z) return x + ay * (sign(l.y) * pen.y);
                return x + az * (sign(l.z) * pen.z);
            }

            float3 Collide(float3 x)
            {
                for (int s = 0; s < 8; s++)
                {
                    if (s >= (int)_SphereCount) break;
                    x = PushOut(x, _Spheres[s].xyz, _Spheres[s].w + _Distance);
                }
                for (int c = 0; c < 8; c++)
                {
                    if (c >= (int)_CapsuleCount) break;
                    float3 a = _CapsuleA[c].xyz, ab = _CapsuleB[c].xyz - a;
                    float t = saturate(dot(x - a, ab) / max(dot(ab, ab), 1e-9));
                    x = PushOut(x, a + ab * t, _CapsuleA[c].w + _Distance);
                }
                for (int b = 0; b < 8; b++)
                {
                    if (b >= (int)_BoxCount) break;
                    x = PushBox(x, b);
                }
                return x;
            }

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float2 size = float2(2 * _HW + 1, _H);
                float2 px = floor(IN.localTexcoord.xy * size);
                if (px.x >= 2 * _HW) return float4(_Origin, 1);
                // the object moved since the stored data: shift it once, whatever the substep count
                float3 shift = Fetch(_SelfTexture2D, float2(2 * _HW, 0), size).xyz - _Origin;
                bool isPrev = px.x >= _HW;
                float x = isPrev ? px.x - _HW : px.x;
                float b = floor((x + 0.5) / _P); // +0.5: GPU division is a reciprocal multiply
                int j = (int)(x - b * _P);
                if (px.y * floor(_HW / _P) + b >= _Count) return 0;
                float2 root = float2(b * _P, px.y);

                float3 restJ = Fetch(_Rest, float2(x, px.y), float2(_HW, _H)).xyz;
                if (_Reset > 0.5 || j == 0) return float4(mul(_ObjectToWorld, float4(restJ, 1)).xyz, 1);

                // solve one point past this one: its length correction damps this point (Dynamic FTL, Müller et al. 2012).
                // Without it the tips keep circling: follow-the-leader alone feeds energy into the free end.
                int last = min(j + 1, (int)_P - 1);
                float3 restPrev = Fetch(_Rest, root, float2(_HW, _H)).xyz;
                float3 xi = mul(_ObjectToWorld, float4(restPrev, 1)).xyz;
                float3 xj = xi, dNext = 0;
                [loop] for (int i = 1; i <= last; i++)
                {
                    float2 t = root + float2(i, 0);
                    float3 p = Fetch(_SelfTexture2D, t, size).xyz + shift;
                    float3 q = Fetch(_SelfTexture2D, t + float2(_HW, 0), size).xyz + shift;
                    float3 rest = Fetch(_Rest, t, float2(_HW, _H)).xyz;
                    float3 seg = mul((float3x3)_ObjectToWorld, rest - restPrev);
                    restPrev = rest;
                    float len = length(seg);
                    // Stiffness: a spring to the rest point fixed to the object, not to the rest direction from the parent
                    // (a goal that moves with the parent keeps re-exciting the strand, and the tips never settle)
                    float3 goal = mul(_ObjectToWorld, float4(rest, 1)).xyz;
                    float3 n = p + (p - q) * _Keep + _Accel + (goal - p) * _Spring + FieldAccel(p + _Origin, (p - q) * rsqrt(max(_H2, 1e-12))) * _H2;
                    float3 d = n - xi;
                    float l = length(d);
                    float3 m = xi + (l > 1e-6 ? d * (len / l) : seg);
                    if (i > j) { dNext = m - n; break; }
                    xi = Collide(m);
                    xj = xi;
                }
                // previous position: velocity next substep = (x - prev) minus 0.9 of the next point's correction
                if (isPrev) return float4(Fetch(_SelfTexture2D, float2(x, px.y), size).xyz + shift + dNext * 0.9, 1);
                return float4(xj, 1);
            }
            ENDCG
        }
    }
}
