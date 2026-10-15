#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Floats Rigidbodies on a Blend Property/Ocean surface: each body is pushed up by how deep its centre is under the
    /// waves, slowed down in the water, and turned to lie along the wave slope. Put it on the object with the ocean's
    /// Renderer. It reads the ocean material every frame (so a BlendUdonPanel can change the sea) and works the waves
    /// out with the shader's maths, using only the Max Waves biggest ones (small ripples don't move a floating object).
    /// Local only (not synced): kinematic bodies (held, or owned by someone else under VRC Object Sync) are skipped.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/Buoyancy.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Ocean Buoyancy")]
    public class BlendUdonBuoyancy : UdonSharpBehaviour
    {
        [Tooltip("Bodies floating on this ocean.")]
        public Rigidbody[] bodies;
        [Min(0.01f), Tooltip("Metres: the height of a body. Fully under water at this depth below the surface.")]
        public float size = 0.5f;
        [Min(0f), Tooltip("Upward push when fully under water, as a multiple of gravity: 2 floats half out of the water.")]
        public float floatiness = 2f;
        [Min(0f), Tooltip("Per second: how quickly the water slows a body down.")]
        public float waterDrag = 1.5f;
        [Min(0f), Tooltip("Per second: how quickly the water stops a body spinning.")]
        public float waterAngularDrag = 2f;
        [Min(0f), Tooltip("How strongly a floating body turns to lie along the waves (0 = not at all).")]
        public float align = 4f;
        [Range(1, 32), Tooltip("Only the biggest waves are used: 12 is within a few cm of the drawn surface.")]
        public int maxWaves = 12;

        const float G = 9.81f;
        Material mat;
        Bounds area; // the mesh: no water outside it
        // wave table: k * direction (x, z), angular speed, amplitude, phase
        float[] kx = new float[64], kz = new float[64], ws = new float[64], amps = new float[64], phases = new float[64];
        int count;
        float choppy, timeScale;
        float lWaves = -1, lSpatial, lSmallest, lScale, lWind, lAlign, lDir, lDamp, lDepth, lSeed;
        Vector3 sample; // xyz of the last Sample: surface height, then normal x, z (local)

        void Start()
        {
            Renderer r = GetComponent<Renderer>();
            if (r == null) { enabled = false; return; }
            mat = r.material; // same instance a BlendUdonPanel on this renderer changes
            MeshFilter mf = GetComponent<MeshFilter>();
            area = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        void FixedUpdate()
        {
            if (mat == null || bodies == null) return;
            Refresh();
            float t = Time.timeSinceLevelLoad * timeScale; // the shader's _Time.y
            Transform tr = transform;
            for (int b = 0; b < bodies.Length; b++)
            {
                Rigidbody rb = bodies[b];
                if (rb == null || rb.isKinematic) continue;
                Vector3 local = tr.InverseTransformPoint(rb.worldCenterOfMass);
                if (local.x < area.min.x || local.x > area.max.x || local.z < area.min.z || local.z > area.max.z) continue;
                Sample(local.x, local.z, t);
                Vector3 surface = tr.TransformPoint(new Vector3(local.x, sample.x, local.z));
                float under = Mathf.Clamp01((surface.y - rb.worldCenterOfMass.y) / size + 0.5f);
                if (under <= 0f) continue;
                rb.AddForce(Vector3.up * (floatiness * G * under) - rb.velocity * (waterDrag * under), ForceMode.Acceleration);
                Vector3 normal = tr.TransformDirection(new Vector3(sample.y, 1f, sample.z)).normalized;
                rb.AddTorque((Vector3.Cross(rb.transform.up, normal) * align - rb.angularVelocity * waterAngularDrag) * under, ForceMode.Acceleration);
            }
        }

        /// <summary>Height and slope of the surface above local point (x, z) at shader time t, into sample.</summary>
        void Sample(float x, float z, float t)
        {
            // Gerstner waves move the water sideways: find the rest point that ends up at (x, z), once
            float px = x, pz = z;
            for (int pass = 0; pass < 2; pass++)
            {
                float dx = 0, dz = 0, h = 0, sx = 0, sz = 0, jxx = 1, jzz = 1;
                for (int i = 0; i < count; i++)
                {
                    float ph = kx[i] * px + kz[i] * pz - ws[i] * t + phases[i];
                    float s = Mathf.Sin(ph), c = Mathf.Cos(ph), a = amps[i], q = choppy * a;
                    float k = Mathf.Sqrt(kx[i] * kx[i] + kz[i] * kz[i]), qd = q / k; // q * direction
                    dx -= kx[i] * qd * s; dz -= kz[i] * qd * s;
                    h += a * c;
                    sx -= kx[i] * a * s; sz -= kz[i] * a * s;
                    jxx -= kx[i] * kx[i] / k * q * c; jzz -= kz[i] * kz[i] / k * q * c;
                }
                if (pass == 0) { px = x - dx; pz = z - dz; continue; }
                // slope of the displaced surface: dh / d(displaced x), roughly
                sample = new Vector3(h, -sx / Mathf.Max(jxx, 0.2f), -sz / Mathf.Max(jzz, 0.2f));
            }
        }

        /// <summary>Rebuild the wave table when the material's ocean settings changed. Same picks as the shader.</summary>
        void Refresh()
        {
            choppy = mat.GetFloat("_Choppiness");
            timeScale = mat.GetFloat("_TimeScale");
            float nw = mat.GetFloat("_Waves"), spatial = mat.GetFloat("_SpatialSize"), smallest = mat.GetFloat("_SmallestWave");
            float scale = mat.GetFloat("_WaveScale"), wind = mat.GetFloat("_WindVelocity"), alignment = mat.GetFloat("_Alignment");
            float dir = mat.GetFloat("_Direction"), damp = mat.GetFloat("_Damping"), depth = mat.GetFloat("_Depth"), seedF = mat.GetFloat("_Seed");
            if (nw == lWaves && spatial == lSpatial && smallest == lSmallest && scale == lScale && wind == lWind
                && alignment == lAlign && dir == lDir && damp == lDamp && depth == lDepth && seedF == lSeed) return;
            lWaves = nw; lSpatial = spatial; lSmallest = smallest; lScale = scale; lWind = wind;
            lAlign = alignment; lDir = dir; lDamp = damp; lDepth = depth; lSeed = seedF;

            int n = Mathf.Clamp((int)nw, 0, 64);
            float kMin = 2f * Mathf.PI / Mathf.Max(spatial, 0.01f);
            float range = Mathf.Log(Mathf.Max(spatial / Mathf.Max(smallest, 0.001f), 1.01f));
            float w0 = G / Mathf.Max(wind, 0.1f);
            long seed = (long)Mathf.Max(seedF, 0f) * 977L;
            count = 0;
            for (int i = 0; i < n; i++)
            {
                long h = (seed + i * 3L) & 0xFFFFFFFFL;
                float k = kMin * Mathf.Exp(range * (i + Hash(h)) / n);
                float dk = k * range / n;
                float e = Mathf.Exp(2f * Mathf.Min(k * depth, 20f));
                float w = Mathf.Sqrt(G * k * (e - 1f) / (e + 1f)); // tanh
                float S = 0.0081f * G * G / Mathf.Pow(w, 5f) * Mathf.Exp(-0.74f * Mathf.Pow(w0 / w, 4f));
                float a = Mathf.Sqrt(S * G / w * dk) * scale;
                float th = (Hash(h + 1) * 2f - 1f) * Mathf.PI / (1f + alignment);
                if (alignment > 0f && Mathf.Abs(th) > Mathf.PI * 0.5f) a *= 1f - damp;
                th += dir * Mathf.Deg2Rad;
                // keep the biggest maxWaves: replace the smallest kept one
                int slot = count;
                if (count >= maxWaves)
                {
                    slot = 0;
                    for (int j = 1; j < count; j++) if (amps[j] < amps[slot]) slot = j;
                    if (amps[slot] >= a) continue;
                }
                else count++;
                kx[slot] = Mathf.Cos(th) * k; kz[slot] = Mathf.Sin(th) * k;
                ws[slot] = w; amps[slot] = a; phases[slot] = Hash(h + 2) * 2f * Mathf.PI;
            }
        }

        // the shader's PCG hash, in 64-bit maths so it wraps like a GPU uint
        float Hash(long v)
        {
            long s = (v * 747796405L + 2891336453L) & 0xFFFFFFFFL;
            long w = (((s >> (int)((s >> 28) + 4)) ^ s) * 277803737L) & 0xFFFFFFFFL;
            return ((w >> 22) ^ w) / 4294967296f;
        }
    }
}
