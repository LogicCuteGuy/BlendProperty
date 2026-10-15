using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Smoothing that keeps its volume (Taubin λ|μ): unlike Smooth, repeated passes don't shrink the mesh.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/LaplacianSmooth.png")]
    [AddComponentMenu("Blend Property/Modifiers/Deform/Laplacian Smooth")]
    public class LaplacianSmoothModifier : BlendModifier
    {
        [Range(-10, 10), Tooltip("Negative values exaggerate the shape instead of smoothing it.")]
        public float factor = 0.5f;
        [Range(0, 200)] public int repeat = 5;
        [Tooltip("Taubin smoothing: alternate an inflate pass so the mesh keeps its size.")]
        public bool preserveVolume = true;
        [Range(0, 1), Tooltip("Smoothing applied to open (boundary) vertices, relative to factor.")]
        public float border;
        public bool x = true, y = true, z = true;

        public override void Apply(MeshData mesh)
        {
            if (x && y && z) { Relax(mesh, factor, repeat, preserveVolume, border); return; }
            var before = mesh.pos.ToArray();
            Relax(mesh, factor, repeat, preserveVolume, border);
            for (int i = 0; i < before.Length; i++)
            {
                var p = mesh.pos[i];
                mesh.pos[i] = new Vector3(x ? p.x : before[i].x, y ? p.y : before[i].y, z ? p.z : before[i].z);
            }
        }

        public static void Relax(MeshData m, float factor, int repeat, bool preserveVolume, float border)
        {
            if (factor == 0 || repeat <= 0) return;
            var nb = m.Neighbors();
            var P = m.pos;
            var scale = new float[P.Count];
            for (int i = 0; i < scale.Length; i++) scale[i] = 1f;
            foreach (var kv in m.EdgeFaces())
                if (kv.Value.Count == 1) scale[MeshData.EdgeA(kv.Key)] = scale[MeshData.EdgeB(kv.Key)] = border;

            // Taubin: μ chosen so the pass-band is k = 1/λ + 1/μ = 0.1.
            float mu = -1f / (1f / factor - 0.1f);
            var next = new Vector3[P.Count];
            for (int it = 0; it < repeat; it++)
            {
                Pass(factor);
                if (preserveVolume && factor > 0 && factor < 10f) Pass(mu); // Taubin needs 0 < λ < 1/kPB
            }

            void Pass(float f)
            {
                for (int i = 0; i < P.Count; i++)
                {
                    if (nb[i].Count == 0) { next[i] = P[i]; continue; }
                    Vector3 avg = Vector3.zero;
                    foreach (var j in nb[i]) avg += P[j];
                    next[i] = P[i] + (avg / nb[i].Count - P[i]) * (f * scale[i]);
                }
                for (int i = 0; i < P.Count; i++) P[i] = next[i];
            }
        }
    }
}
