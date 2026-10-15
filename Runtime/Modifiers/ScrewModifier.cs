using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Sweeps the mesh's boundary edges (or all edges if it has none) around an axis.
    /// Unity meshes have no loose edges, so model the profile as a thin strip or open polyline of faces.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Screw.png")]
    [AddComponentMenu("Blend Property/Modifiers/Generate/Screw")]
    public class ScrewModifier : BlendModifier
    {
        public enum Axis { X, Y, Z }

        public Axis axis = Axis.Y;
        public float angle = 360f;
        [Min(1)] public int steps = 16;
        [Tooltip("Distance moved along the axis per iteration.")]
        public float screwOffset;
        [Min(1)] public int iterations = 1;
        [Tooltip("Optional axis object (its position and chosen axis).")]
        public Transform axisObject;
        [Tooltip("Use the Axis Object's distance along the axis as the height of one iteration.")]
        public bool objectScrew;
        [Tooltip("Spread U from 0 to 1 around the sweep.")]
        public bool stretchU = true;
        [Tooltip("Spread V from 0 to 1 along the profile.")]
        public bool stretchV;
        public bool keepOriginal;
        public bool flip;
        public bool merge = true;
        [Min(0)] public float mergeDistance = 0.001f;

        public override IEnumerable<Object> Dependencies() { if (axisObject) yield return axisObject; }

        public override void Apply(MeshData mesh)
        {
            Vector3 ax = axis == Axis.X ? Vector3.right : axis == Axis.Y ? Vector3.up : Vector3.forward;
            Vector3 center = Vector3.zero;
            float height = MeshUnits(screwOffset);
            if (axisObject)
            {
                var m = ToLocal(axisObject);
                center = m.MultiplyPoint3x4(Vector3.zero);
                ax = MeshData.Unit(m.MultiplyVector(ax));
                if (objectScrew) height = Vector3.Dot(center, ax);
            }

            // Profile edges with one corner attr per endpoint.
            var edgeFaces = mesh.EdgeFaces();
            bool hasBoundary = false;
            foreach (var kv in edgeFaces) if (kv.Value.Count == 1) { hasBoundary = true; break; }
            var edges = new List<(int a, int b, Attr aa, Attr ba, int sub)>();
            var seen = new HashSet<long>(MeshData.Keys);
            foreach (var f in mesh.faces)
                for (int c = 0; c < f.Count; c++)
                {
                    int c2 = (c + 1) % f.Count;
                    long k = MeshData.EdgeKey(f.v[c], f.v[c2]);
                    if (hasBoundary && edgeFaces[k].Count != 1) continue;
                    if (!seen.Add(k)) continue;
                    edges.Add((f.v[c], f.v[c2], f.a[c], f.a[c2], f.sub));
                }

            int total = steps * iterations;
            bool closed = Mathf.Abs(Mathf.Abs(angle) - 360f) < 1e-3f && Mathf.Abs(height) < 1e-6f;
            int rings = closed ? total : total + 1;
            int n = mesh.pos.Count;
            var basePos = mesh.pos.GetRange(0, n);
            var outPos = new List<Vector3>(n * rings);
            for (int r = 0; r < rings; r++)
            {
                float t = (float)r / steps; // in iterations
                var rot = Quaternion.AngleAxis(angle * t, ax);
                var shift = ax * (height * t);
                foreach (var p in basePos) outPos.Add(center + rot * (p - center) + shift);
            }

            var faces = keepOriginal ? new List<Face>(mesh.faces) : new List<Face>();
            float vMin = float.MaxValue, vMax = float.MinValue;
            foreach (var e in edges) { vMin = Mathf.Min(vMin, e.aa.uv0.y, e.ba.uv0.y); vMax = Mathf.Max(vMax, e.aa.uv0.y, e.ba.uv0.y); }
            float V(Attr at) => stretchV && vMax > vMin ? (at.uv0.y - vMin) / (vMax - vMin) : at.uv0.y;
            float U(Attr at, float u) => stretchU ? u : at.uv0.x;
            for (int r = 0; r < total; r++)
            {
                int r0 = r * n, r1 = ((r + 1) % rings) * n;
                float u0 = (float)r / total, u1 = (float)(r + 1) / total;
                foreach (var e in edges)
                {
                    var a0 = e.aa; a0.uv0 = new Vector2(U(e.aa, u0), V(e.aa));
                    var b0 = e.ba; b0.uv0 = new Vector2(U(e.ba, u0), V(e.ba));
                    var a1 = e.aa; a1.uv0 = new Vector2(U(e.aa, u1), V(e.aa));
                    var b1 = e.ba; b1.uv0 = new Vector2(U(e.ba, u1), V(e.ba));
                    var face = new Face(new[] { r0 + e.a, r0 + e.b, r1 + e.b, r1 + e.a }, new[] { a0, b0, b1, a1 }, e.sub);
                    if (flip) face.Reverse();
                    faces.Add(face);
                }
            }
            mesh.pos = outPos;
            mesh.faces = faces;
            mesh.Compact();
            if (merge) mesh.Weld(MeshUnits(mergeDistance));
        }
    }
}
