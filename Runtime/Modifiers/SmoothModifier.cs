using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Smooth.png")]
    [AddComponentMenu("Blend Property/Modifiers/Deform/Smooth")]
    public class SmoothModifier : BlendModifier
    {
        public bool x = true, y = true, z = true;
        [Range(-10, 10)] public float factor = 0.5f;
        [Range(0, 200)] public int repeat = 1;

        public override void Apply(MeshData mesh)
        {
            var nb = mesh.Neighbors();
            var P = mesh.pos;
            var next = new Vector3[P.Count];
            for (int it = 0; it < repeat; it++)
            {
                for (int i = 0; i < P.Count; i++)
                {
                    if (nb[i].Count == 0) { next[i] = P[i]; continue; }
                    Vector3 avg = Vector3.zero;
                    foreach (var j in nb[i]) avg += P[j];
                    avg /= nb[i].Count;
                    var r = P[i] + (avg - P[i]) * factor;
                    next[i] = new Vector3(x ? r.x : P[i].x, y ? r.y : P[i].y, z ? r.z : P[i].z);
                }
                for (int i = 0; i < P.Count; i++) P[i] = next[i];
            }
        }
    }
}
