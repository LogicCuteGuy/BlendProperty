using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Deform along a path. Unity has no curve object, so the path is a Catmull-Rom spline through
    /// <see cref="points"/> (any Transforms, e.g. empty children). Position along <see cref="deformAxis"/>
    /// maps to distance along the path, like Blender's Curve modifier.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Curve.png")]
    [AddComponentMenu("Blend Modifier/Deform/Curve")]
    public class CurveModifier : BlendModifier
    {
        public enum Axis { PosX, PosY, PosZ, NegX, NegY, NegZ }

        public Transform[] points = new Transform[0];
        public bool closed;
        public Axis deformAxis = Axis.PosX;
        [Tooltip("Twist reference: the path's up vector at the start (object space).")]
        public Vector3 up = Vector3.up;
        [Range(4, 64)] public int samplesPerSegment = 16;

        public override IEnumerable<Object> Dependencies()
        {
            if (points != null) foreach (var p in points) if (p) yield return p;
        }

        public override void Apply(MeshData mesh)
        {
            var ctrl = new List<Vector3>();
            if (points != null) foreach (var t in points) if (t) ctrl.Add(transform.InverseTransformPoint(t.position));
            if (ctrl.Count < 2) return;

            // sample spline + arc length + rotation-minimizing frames
            int segs = closed ? ctrl.Count : ctrl.Count - 1;
            var pts = new List<Vector3>();
            for (int s = 0; s < segs; s++)
                for (int k = 0; k < samplesPerSegment; k++)
                    pts.Add(CatmullRom(ctrl, s, (float)k / samplesPerSegment, closed));
            pts.Add(closed ? pts[0] : ctrl[ctrl.Count - 1]);

            int n = pts.Count;
            var len = new float[n];
            var tan = new Vector3[n];
            var nor = new Vector3[n];
            for (int i = 1; i < n; i++) len[i] = len[i - 1] + (pts[i] - pts[i - 1]).magnitude;
            for (int i = 0; i < n; i++)
            {
                var d = pts[Mathf.Min(i + 1, n - 1)] - pts[Mathf.Max(i - 1, 0)];
                tan[i] = d != Vector3.zero ? MeshData.Unit(d) : Vector3.right;
            }
            nor[0] = MeshData.Unit(Vector3.ProjectOnPlane(up, tan[0]));
            if (nor[0].sqrMagnitude < 0.5f) nor[0] = MeshData.Unit(Vector3.ProjectOnPlane(Vector3.forward, tan[0]));
            for (int i = 1; i < n; i++)
                nor[i] = (Quaternion.FromToRotation(tan[i - 1], tan[i]) * nor[i - 1]).normalized;

            int ax = (int)deformAxis % 3;
            float sign = (int)deformAxis >= 3 ? -1f : 1f;
            // the other two axes map to (normal, binormal)
            int a1 = ax == 0 ? 1 : 0, a2 = ax == 2 ? 1 : 2;
            float total = len[n - 1];

            for (int i = 0; i < mesh.pos.Count; i++)
            {
                var p = mesh.pos[i];
                float s = p[ax] * sign;
                Vector3 c, t, nn;
                if (closed && total > 0) s = Mathf.Repeat(s, total);
                if (s <= 0) { t = tan[0]; nn = nor[0]; c = pts[0] + t * s; }
                else if (s >= total) { t = tan[n - 1]; nn = nor[n - 1]; c = pts[n - 1] + t * (s - total); }
                else
                {
                    int j = System.Array.BinarySearch(len, s);
                    if (j < 0) j = ~j;
                    j = Mathf.Clamp(j, 1, n - 1);
                    float f = (s - len[j - 1]) / Mathf.Max(len[j] - len[j - 1], 1e-9f);
                    c = Vector3.Lerp(pts[j - 1], pts[j], f);
                    t = Vector3.Slerp(tan[j - 1], tan[j], f).normalized;
                    nn = Vector3.Slerp(nor[j - 1], nor[j], f).normalized;
                }
                var b = Vector3.Cross(t, nn);
                mesh.pos[i] = c + nn * p[a1] + b * p[a2] * sign;
            }
        }

        internal static Vector3 CatmullRom(List<Vector3> c, int seg, float t, bool closed)
        {
            int n = c.Count;
            Vector3 P(int i) => closed ? c[((i % n) + n) % n] : c[Mathf.Clamp(i, 0, n - 1)];
            Vector3 p0 = P(seg - 1), p1 = P(seg), p2 = P(seg + 1), p3 = P(seg + 2);
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
    }
}
