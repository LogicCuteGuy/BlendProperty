// Shared footprint for paint and wetness. RGB is the optional tip colour; alpha is coverage.
sampler2D _Tip0, _Tip1, _Tip2, _Tip3, _Tip4, _Tip5, _Tip6, _Tip7;
float4 _Tip0_TexelSize, _Tip1_TexelSize, _Tip2_TexelSize, _Tip3_TexelSize, _Tip4_TexelSize, _Tip5_TexelSize, _Tip6_TexelSize, _Tip7_TexelSize; // zw = width, height in pixels
float4 _BrushA[8]; // xyz current world position, w radius
float4 _BrushB[8]; // rgb linear colour, w signed application alpha (negative erases)
float4 _BrushC[8]; // x softness, y textured, z use texture colour, w Airbrush elapsed 60 Hz steps (0 = normal)
float4 _BrushD[8]; // xyz previous world position
float4 _BrushE[8], _BrushF[8]; // rotated tip right/up divided by diameter

float4 SampleTip(int b, float2 uv)
{
    float4 p = float4(uv, 0, 0);
    float4 tip = 1;
    if (b == 0) tip = tex2Dlod(_Tip0, p);
    else if (b == 1) tip = tex2Dlod(_Tip1, p);
    else if (b == 2) tip = tex2Dlod(_Tip2, p);
    else if (b == 3) tip = tex2Dlod(_Tip3, p);
    else if (b == 4) tip = tex2Dlod(_Tip4, p);
    else if (b == 5) tip = tex2Dlod(_Tip5, p);
    else if (b == 6) tip = tex2Dlod(_Tip6, p);
    else if (b == 7) tip = tex2Dlod(_Tip7, p);
    return tip;
}

float2 TipPixels(int b)
{
    float4 s = 1;
    if (b == 0) s = _Tip0_TexelSize;
    else if (b == 1) s = _Tip1_TexelSize;
    else if (b == 2) s = _Tip2_TexelSize;
    else if (b == 3) s = _Tip3_TexelSize;
    else if (b == 4) s = _Tip4_TexelSize;
    else if (b == 5) s = _Tip5_TexelSize;
    else if (b == 6) s = _Tip6_TexelSize;
    else if (b == 7) s = _Tip7_TexelSize;
    return s.zw;
}

float4 BrushSample(int b, float3 world)
{
    float3 start = _BrushD[b].xyz;
    float3 segment = _BrushA[b].xyz - start;
    float t = saturate(dot(world - start, segment) / max(dot(segment, segment), 1e-10));
    float3 delta = world - (start + segment * t);
    float r = _BrushA[b].w;
    if (r <= 0) return 0;
    float soft = saturate(_BrushC[b].x);
    if (_BrushC[b].y < 0.5)
    {
        // plain round brush
        float d = length(delta);
        if (d >= r) return 0;
        float f = soft > 1e-5 ? 1 - smoothstep(r * (1 - soft), r, d) : 1;
        return float4(1, 1, 1, f);
    }
    // picture tip: the whole picture, not cut to a circle. Its longer side is the brush diameter and its
    // proportions are kept (a wide picture is shorter than it is wide, a tall one narrower).
    float2 q = float2(dot(delta, _BrushE[b].xyz), dot(delta, _BrushF[b].xyz));
    float2 px = TipPixels(b);
    float aspect = (px.x > 0 && px.y > 0) ? px.x / px.y : 1;
    if (aspect > 1) q.y *= aspect; else q.x /= aspect;
    if (any(abs(q) > 0.5)) return 0;
    float m = max(abs(q.x), abs(q.y)) * 2; // 0 at the centre, 1 at the picture's edge
    float f = soft > 1e-5 ? 1 - smoothstep(1 - soft, 1, m) : 1;
    float4 tip = SampleTip(b, q + 0.5);
    if (_BrushC[b].z > 0.5) return float4(tip.rgb, tip.a * f);
    return float4(1, 1, 1, dot(tip.rgb, float3(0.2126, 0.7152, 0.0722)) * tip.a * f);
}

float BrushAlpha(int b, float mask)
{
    float a = saturate(abs(_BrushB[b].a) * mask);
    return _BrushC[b].w > 0 ? 1 - pow(1 - a, _BrushC[b].w) : a;
}
