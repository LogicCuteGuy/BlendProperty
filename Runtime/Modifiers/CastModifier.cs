using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Cast.png")]
    [AddComponentMenu("Blend Modifier/Deform/Cast")]
    public class CastModifier : BlendModifier
    {
        public enum Shape { Sphere, Cylinder, Cuboid }

        public Shape shape = Shape.Sphere;
        public bool x = true, y = true, z = true;
        [Range(-2, 2)] public float factor = 0.5f;
        [Min(0), Tooltip("0 = affect everything.")] public float radius;
        [Min(0), Tooltip("0 = auto (average distance).")] public float size;
        [Tooltip("Use Radius as the size (smoother when Radius limits the effect).")]
        public bool sizeFromRadius;
        [Tooltip("Optional center object.")] public Transform control;

        public override IEnumerable<Object> Dependencies() { if (control) yield return control; }

        public override void Apply(MeshData mesh)
        {
            int n = mesh.pos.Count;
            if (n == 0) return;
            var to = control ? ToLocal(control).inverse : Matrix4x4.identity;
            var from = to.inverse;
            var co = new Vector3[n];
            for (int i = 0; i < n; i++) co[i] = to.MultiplyPoint3x4(mesh.pos[i]);

            // Cylinder ignores the Y axis (Unity is Y-up; Blender uses Z).
            Vector3 Flat(Vector3 p) => shape == Shape.Cylinder ? new Vector3(p.x, 0, p.z) : p;

            float avg = 0; Vector3 half = Vector3.zero;
            foreach (var p in co) { avg += Flat(p).magnitude; half += new Vector3(Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z)); }
            avg /= n; half /= n;
            float rad = MeshUnits(radius), siz = MeshUnits(size);
            float sz = sizeFromRadius && rad > 0 ? rad : siz > 0 ? siz : avg;
            if (sizeFromRadius && rad > 0 || siz > 0) half = Vector3.one * sz;

            for (int i = 0; i < n; i++)
            {
                var p = co[i];
                var f = Flat(p);
                if (rad > 0 && f.magnitude > rad) continue;
                Vector3 target;
                if (shape == Shape.Cuboid)
                {
                    float m = Mathf.Max(Mathf.Abs(p.x) / Mathf.Max(half.x, 1e-6f), Mathf.Abs(p.y) / Mathf.Max(half.y, 1e-6f), Mathf.Abs(p.z) / Mathf.Max(half.z, 1e-6f));
                    target = m > 1e-6f ? p / m : p;
                }
                else
                {
                    target = f != Vector3.zero ? MeshData.Unit(f) * sz : f;
                    if (shape == Shape.Cylinder) target.y = p.y;
                }
                var r = Vector3.LerpUnclamped(p, target, factor);
                if (!x) r.x = p.x;
                if (!y) r.y = p.y;
                if (!z) r.z = p.z;
                mesh.pos[i] = from.MultiplyPoint3x4(r);
            }
        }
    }
}
