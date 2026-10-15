// BlendUdonForceField in shaders: up to 4 fields, packed by the component (packA/B/C).
// Same maths as BlendUdonForceField.Accel. Returns world acceleration (m/s²) at world point p moving at velocity v (m/s).
#ifndef BLEND_FORCE_FIELD_INCLUDED
#define BLEND_FORCE_FIELD_INCLUDED
float _FieldCount;
float4 _FieldA[4], _FieldB[4], _FieldC[4];

// smooth swirling field, about -1..1 per axis, drifting over time (cheap enough for Udon too)
float3 FieldTurbulence(float3 q, float t)
{
    return float3(sin(q.y * 1.7 + t * 1.3 + sin(q.z * 2.3 + t)),
                  sin(q.z * 1.9 + t * 1.1 + sin(q.x * 2.1 - t * 0.7)),
                  sin(q.x * 1.5 + t * 1.7 + sin(q.y * 2.5 + t * 0.9)));
}

float3 FieldAccel(float3 p, float3 v)
{
    float3 acc = 0;
    [loop] for (int f = 0; f < 4; f++)
    {
        if (f >= (int)_FieldCount) break;
        float4 a = _FieldA[f], b = _FieldB[f], c = _FieldC[f];
        if (c.z < 0.5) continue;
        float3 d = p - a.xyz;
        float dist = length(d);
        if (c.y > 0 && dist > c.y) continue;
        float fall = c.x > 0 ? pow(1 + dist, -c.x) : 1;
        float s = b.w * fall;
        if (a.w > 4.5) { acc -= (d * b.w + v * c.w) * fall; continue; }               // Harmonic: spring + damping
        if (a.w > 3.5) { acc -= v * (b.w + c.w * length(v)) * fall; continue; }       // Drag: linear + quadratic
        if (a.w > 2.5) { acc += FieldTurbulence(d / max(c.w, 0.01), _Time.y) * s; continue; } // Turbulence
        float3 dir = a.w < 0.5 ? d : (a.w < 1.5 ? b.xyz : cross(b.xyz, d));         // Force, Wind, Vortex
        float len = length(dir);
        if (len > 1e-6) acc += dir * (s / len);
    }
    return acc;
}
#endif
