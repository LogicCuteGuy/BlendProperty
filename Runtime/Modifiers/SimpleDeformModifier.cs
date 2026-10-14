using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Simple Deform. Twist/Bend angle is the total over the limit span (Blender normalizes the same way).</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/SimpleDeform.png")]
    [AddComponentMenu("Blend Modifier/Deform/Simple Deform")]
    public class SimpleDeformModifier : BlendModifier
    {
        public enum Mode { Twist, Bend, Taper, Stretch }
        public enum Axis { X, Y, Z }

        public Mode mode = Mode.Twist;
        [Tooltip("Twist / Bend, degrees.")]
        public float angle = 45f;
        [Tooltip("Taper / Stretch.")]
        public float factor = 0.5f;
        public Axis axis = Axis.Y;
        [Tooltip("Optional origin / orientation for the deformation.")]
        public Transform origin;
        [Range(0, 1)] public float lowerLimit = 0f;
        [Range(0, 1)] public float upperLimit = 1f;
        [Tooltip("Twist, Taper, Stretch: keep these local coordinates unchanged (the deform axis itself is ignored).")]
        public bool lockX, lockY, lockZ;

        public override IEnumerable<Object> Dependencies() { if (origin) yield return origin; }

        public override void Apply(MeshData mesh)
        {
            // local -> origin space -> swizzle so the deform axis is Z
            var toOrigin = origin ? ToLocal(origin).inverse : Matrix4x4.identity;
            var swz = Matrix4x4.identity;
            if (axis == Axis.X) swz = Rows(new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0));      // (y,z,x)
            else if (axis == Axis.Y) swz = Rows(new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0)); // (z,x,y)
            var to = swz * toOrigin;
            var from = to.inverse;

            int n = mesh.pos.Count;
            if (n == 0) return;
            var co = new Vector3[n];
            float zmin = float.MaxValue, zmax = float.MinValue, xmin = float.MaxValue, xmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                co[i] = to.MultiplyPoint3x4(mesh.pos[i]);
                zmin = Mathf.Min(zmin, co[i].z); zmax = Mathf.Max(zmax, co[i].z);
                xmin = Mathf.Min(xmin, co[i].x); xmax = Mathf.Max(xmax, co[i].x);
            }
            bool bend = mode == Mode.Bend; // Blender: bend measures along X and bends around Z
            float lo = bend ? Mathf.Lerp(xmin, xmax, lowerLimit) : Mathf.Lerp(zmin, zmax, lowerLimit);
            float hi = bend ? Mathf.Lerp(xmin, xmax, upperLimit) : Mathf.Lerp(zmin, zmax, upperLimit);
            if (lo > hi) (lo, hi) = (hi, lo);
            float k = angle * Mathf.Deg2Rad / Mathf.Max(hi - lo, 1e-6f);

            for (int i = 0; i < n; i++)
            {
                var p = co[i];
                switch (mode)
                {
                    case Mode.Twist:
                    {
                        float t = Mathf.Clamp(p.z, lo, hi) * k, s = Mathf.Sin(t), c = Mathf.Cos(t);
                        p = new Vector3(p.x * c - p.y * s, p.x * s + p.y * c, p.z);
                        break;
                    }
                    case Mode.Bend:
                    {
                        if (Mathf.Abs(k) < 1e-7f) break;
                        float x = Mathf.Clamp(p.x, lo, hi), dcut = p.x - x;
                        float t = x * k, s = Mathf.Sin(t), c = Mathf.Cos(t), r = 1f / k;
                        p = new Vector3(-(p.y - r) * s, (p.y - r) * c + r, p.z) + new Vector3(c, s, 0) * dcut;
                        break;
                    }
                    case Mode.Taper:
                    {
                        float sc = 1f + Mathf.Clamp(p.z, lo, hi) * factor;
                        p = new Vector3(p.x * sc, p.y * sc, p.z);
                        break;
                    }
                    case Mode.Stretch:
                    {
                        float z = Mathf.Clamp(p.z, lo, hi), dcut = p.z - z;
                        float sc = z * z * factor - factor + 1f;
                        p = new Vector3(p.x * sc, p.y * sc, z * (1f + factor) + dcut);
                        break;
                    }
                }
                var res = from.MultiplyPoint3x4(p);
                if (mode != Mode.Bend)
                {
                    var o = mesh.pos[i];
                    if (lockX && axis != Axis.X) res.x = o.x;
                    if (lockY && axis != Axis.Y) res.y = o.y;
                    if (lockZ && axis != Axis.Z) res.z = o.z;
                }
                mesh.pos[i] = res;
            }
        }

        static Matrix4x4 Rows(Vector3 r0, Vector3 r1, Vector3 r2)
        {
            var m = Matrix4x4.identity;
            m.SetRow(0, r0); m.SetRow(1, r1); m.SetRow(2, r2);
            return m;
        }
    }
}
