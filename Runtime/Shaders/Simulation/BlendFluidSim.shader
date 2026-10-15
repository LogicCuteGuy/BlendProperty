// Gas (smoke) fluid for BlendUdonFluid: Blender's Fluid domain, type Gas, on a 3D grid of cubic cells.
// Everything lives in one 2D double-buffered Custom Render Texture that only reads its own previous result
// (a 3D CRT gets no previous frame from Unity, and CRTs reading each other in a loop log a warning every frame).
// Two side-by-side regions, each a W x H tile per Z slice:
//   left  (A): velocity xyz in cells per second, pressure in w
//   right (B): smoke density in r, heat in g, fuel in b, flame in a
// Its update zones (set by the bake) run in order each frame:
//   PressureFirst, then Pressure x (iterations - 1): Jacobi solve for the pressure that removes the velocity's
//            divergence, starting from zero (the stored velocity is already projected, so only new divergence is left)
//   Step:    A: velocity minus the pressure gradient, advected by itself, plus buoyancy, vorticity, force fields and
//               source velocity. B: density, heat and fuel advected by that velocity, sources add, Dissolve fades,
//               fuel burns into flame, heat and smoke (Blender Fire).
// A separate 3D CRT (pass Copy) copies region B for the Blend Property/Smoke shader's filtered sampling.
// Obstacles and the domain walls are solid. (Liquid domains use BlendLiquidSim.shader.)
Shader "Hidden/Blend Property/Fluid Sim"
{
    Properties
    {
        _Sim ("Simulation (for Copy)", 2D) = "black" {}
        // baked values: declared so the materials save them
        _Dims ("Cells per axis", Vector) = (32, 32, 32, 0)
        _Tiles ("Slices per atlas row", Float) = 1
    }

    CGINCLUDE
    #include "UnityCustomRenderTexture.cginc"
    #include "BlendForceField.cginc"
    sampler2D _Sim;
    float3 _Dims;
    float _Tiles;
    float4x4 _DomainToWorld, _WorldToDomain; // the domain object's local box (-0.5..0.5) <-> world
    float _Dt, _Reset;
    float _ObstacleCount;
    float4 _ObstacleA[8]; // xyz world centre, w radius
    float4 _ObstacleB[8]; // xyz world velocity
    float _FlowCount;
    float4 _FlowA[8];     // xyz world centre, w radius
    float4 _FlowB[8];     // xyz world velocity, w density
    float4 _FlowC[8];     // x heat, y 1 = set velocity, z fuel
    float2 _Keep;         // density, heat kept per step
    float4 _Fire;         // reaction speed (per second), smoke and heat per burnt fuel, flame kept per step
    float2 _Buoyancy;     // density, heat (Blender Buoyancy Density / Heat), in m/s² per unit
    float3 _Up;           // world up
    float _Vorticity;

    float2 RegionSize() { return float2(_Dims.x * _Tiles, _Dims.y * ceil(_Dims.z / _Tiles)); }
    float2 AtlasSize() { return RegionSize() * float2(2, 1); }

    // integer cell of region 0 (A) or 1 (B) -> atlas uv (+0.5 before floor: GPU division is a reciprocal multiply)
    float2 CellUV(float3 c, float region)
    {
        c = clamp(c, 0, _Dims - 1);
        float ty = floor((c.z + 0.5) / _Tiles), tx = c.z - ty * _Tiles;
        float2 px = float2(tx, ty) * _Dims.xy + c.xy + float2(region * RegionSize().x, 0);
        return (px + 0.5) / AtlasSize();
    }
    float4 Cell(sampler2D s, float3 c, float region) { return tex2Dlod(s, float4(CellUV(c, region), 0, 0)); }
    // nearest cell to uvw (0..1 across the domain)
    float4 Fetch(sampler2D s, float3 uvw, float region) { return Cell(s, floor(uvw * _Dims), region); }
    // trilinear between cells (advection back-trace)
    float4 FetchLerp(sampler2D s, float3 uvw, float region)
    {
        float3 g = uvw * _Dims - 0.5;
        float3 i = floor(g), f = g - i;
        float4 x00 = lerp(Cell(s, i, region), Cell(s, i + float3(1, 0, 0), region), f.x);
        float4 x10 = lerp(Cell(s, i + float3(0, 1, 0), region), Cell(s, i + float3(1, 1, 0), region), f.x);
        float4 x01 = lerp(Cell(s, i + float3(0, 0, 1), region), Cell(s, i + float3(1, 0, 1), region), f.x);
        float4 x11 = lerp(Cell(s, i + float3(0, 1, 1), region), Cell(s, i + float3(1, 1, 1), region), f.x);
        return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
    }

    // this atlas texel -> its region and the uvw of its cell; false for the unused tiles at the end
    bool ThisCell(v2f_customrendertexture IN, out float3 uvw, out float region)
    {
        float2 px = floor(IN.localTexcoord.xy * AtlasSize());
        region = px.x >= RegionSize().x ? 1 : 0;
        px.x -= region * RegionSize().x;
        float2 tile = floor((px + 0.5) / _Dims.xy);
        float3 c = float3(px - tile * _Dims.xy, tile.y * _Tiles + tile.x);
        uvw = (c + 0.5) / _Dims;
        return c.z < _Dims.z;
    }

    float3 WorldOf(float3 uvw) { return mul(_DomainToWorld, float4(uvw - 0.5, 1)).xyz; }
    float3 ToCells(float3 worldVec) { return mul((float3x3)_WorldToDomain, worldVec) * _Dims; }
    float3 ToWorld(float3 cellsVec) { return mul((float3x3)_DomainToWorld, cellsVec / _Dims); }

    // -1 = fluid, -2 = outside the domain, else the obstacle index
    int Solid(float3 uvw)
    {
        if (any(uvw < 0) || any(uvw > 1)) return -2;
        float3 w = WorldOf(uvw);
        [loop] for (int i = 0; i < 8; i++)
        {
            if (i >= (int)_ObstacleCount) break;
            float3 d = w - _ObstacleA[i].xyz;
            if (dot(d, d) < _ObstacleA[i].w * _ObstacleA[i].w) return i;
        }
        return -1;
    }

    // velocity with solid cells set to the solid's velocity (walls: 0)
    float3 V(float3 uvw)
    {
        int s = Solid(uvw);
        if (s == -2) return 0;
        if (s >= 0) return ToCells(_ObstacleB[s].xyz);
        return Fetch(_SelfTexture2D, uvw, 0).xyz;
    }

    // pressure with solid neighbours mirroring the centre (no flow through walls)
    float P(float3 uvw, float centre) { return Solid(uvw) != -1 ? centre : Fetch(_SelfTexture2D, uvw, 0).w; }

    float3 Grad(float3 uvw)
    {
        float3 d = 1 / _Dims;
        float c = Fetch(_SelfTexture2D, uvw, 0).w;
        return 0.5 * float3(P(uvw + float3(d.x, 0, 0), c) - P(uvw - float3(d.x, 0, 0), c),
                            P(uvw + float3(0, d.y, 0), c) - P(uvw - float3(0, d.y, 0), c),
                            P(uvw + float3(0, 0, d.z), c) - P(uvw - float3(0, 0, d.z), c));
    }

    // the divergence-free velocity, at a cell and between cells
    float3 U(float3 uvw) { return Fetch(_SelfTexture2D, uvw, 0).xyz - Grad(uvw); }
    float3 ULerp(float3 uvw) { return FetchLerp(_SelfTexture2D, uvw, 0).xyz - Grad(uvw); }

    // smooth sphere: 1 inside half the radius, 0 at the radius
    float Falloff(float3 w, float4 a) { return 1 - smoothstep(a.w * 0.5, a.w, distance(w, a.xyz)); }

    // one Jacobi iteration; first: the old pressure counts as 0
    float4 Pressure(v2f_customrendertexture IN, bool first)
    {
        float3 uvw; float region;
        bool inside = ThisCell(IN, uvw, region);
        float4 self = Fetch(_SelfTexture2D, uvw, region);
        if (region > 0.5) return self; // density: untouched
        if (!inside || _Reset > 0.5 || Solid(uvw) != -1) return float4(self.xyz, 0);
        float3 d = 1 / _Dims;
        float3 dx = float3(d.x, 0, 0), dy = float3(0, d.y, 0), dz = float3(0, 0, d.z);
        float div = 0.5 * (V(uvw + dx).x - V(uvw - dx).x + V(uvw + dy).y - V(uvw - dy).y + V(uvw + dz).z - V(uvw - dz).z);
        float sum = 0;
        if (!first)
        {
            float c = self.w;
            sum = P(uvw + dx, c) + P(uvw - dx, c) + P(uvw + dy, c) + P(uvw - dy, c) + P(uvw + dz, c) + P(uvw - dz, c);
        }
        return float4(self.xyz, (sum - div) / 6);
    }

    float3 Curl(float3 uvw)
    {
        float3 d = 1 / _Dims;
        float3 xp = V(uvw + float3(d.x, 0, 0)), xm = V(uvw - float3(d.x, 0, 0));
        float3 yp = V(uvw + float3(0, d.y, 0)), ym = V(uvw - float3(0, d.y, 0));
        float3 zp = V(uvw + float3(0, 0, d.z)), zm = V(uvw - float3(0, 0, d.z));
        return 0.5 * float3((yp.z - ym.z) - (zp.y - zm.y), (zp.x - zm.x) - (xp.z - xm.z), (xp.y - xm.y) - (yp.x - ym.x));
    }

    ENDCG

    SubShader
    {
        Lighting Off Blend One Zero ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "PressureFirst"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return Pressure(IN, true); }
            ENDCG
        }

        Pass
        {
            Name "Pressure"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5
            float4 frag(v2f_customrendertexture IN) : SV_Target { return Pressure(IN, false); }
            ENDCG
        }

        Pass
        {
            Name "Step"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            float4 frag(v2f_customrendertexture IN) : SV_Target
            {
                float3 uvw; float region;
                if (!ThisCell(IN, uvw, region) || _Reset > 0.5 || Solid(uvw) != -1) return 0;
                float3 w = WorldOf(uvw);
                float3 back = uvw - U(uvw) * _Dt / _Dims;

                if (region > 0.5)
                {
                    // density, heat, fuel, flame
                    float4 s = FetchLerp(_SelfTexture2D, back, 1);
                    s.rg *= _Keep;
                    [loop] for (int i = 0; i < 8; i++)
                    {
                        if (i >= (int)_FlowCount) break;
                        float f = Falloff(w, _FlowA[i]);
                        if (f <= 0) continue;
                        s.rgb = max(s.rgb, float3(_FlowB[i].w, _FlowC[i].x, _FlowC[i].z) * f);
                    }
                    // fuel burns at Reaction Speed: the burn lights the flame and leaves heat and smoke
                    float rate = s.b * _Fire.x;
                    float burn = min(s.b, rate * _Dt);
                    s.b -= burn;
                    s.r += burn * _Fire.y;
                    s.g += burn * _Fire.z;
                    s.a = max(s.a * _Fire.w, saturate(rate));
                    return s;
                }

                // velocity
                float3 v = ULerp(back);
                float2 s0 = Fetch(_SelfTexture2D, uvw, 1).rg;
                float3 acc = _Up * dot(s0, _Buoyancy) + FieldAccel(w, ToWorld(v));
                v += ToCells(acc) * _Dt;

                // vorticity confinement: push along the curl's gradient to keep small swirls alive
                if (_Vorticity > 0)
                {
                    float3 d = 1 / _Dims;
                    float3 eta = 0.5 * float3(length(Curl(uvw + float3(d.x, 0, 0))) - length(Curl(uvw - float3(d.x, 0, 0))),
                                              length(Curl(uvw + float3(0, d.y, 0))) - length(Curl(uvw - float3(0, d.y, 0))),
                                              length(Curl(uvw + float3(0, 0, d.z))) - length(Curl(uvw - float3(0, 0, d.z))));
                    float len = length(eta);
                    if (len > 1e-5) v += _Vorticity * cross(eta / len, Curl(uvw)) * _Dt;
                }

                [loop] for (int j = 0; j < 8; j++)
                {
                    if (j >= (int)_FlowCount) break;
                    float f = Falloff(w, _FlowA[j]) * _FlowC[j].y;
                    if (f > 0) v = lerp(v, ToCells(_FlowB[j].xyz), f);
                }
                return float4(v, 0);
            }
            ENDCG
        }

        Pass
        {
            Name "Copy"
            CGPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment frag
            #pragma target 3.5

            // a 3D target: localTexcoord.xyz is this cell's uvw
            float4 frag(v2f_customrendertexture IN) : SV_Target { return Fetch(_Sim, IN.localTexcoord.xyz, 1); }
            ENDCG
        }
    }
}
