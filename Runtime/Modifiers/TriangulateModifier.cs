using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Triangulate.png")]
    [AddComponentMenu("Blend Modifier/Generate/Triangulate")]
    public class TriangulateModifier : BlendModifier
    {
        public enum QuadMethod { Beauty, Fixed, FixedAlternate, ShortestDiagonal, LongestDiagonal }
        public enum NgonMethod { Beauty, Clip }

        [Tooltip("Beauty: split for the best-shaped triangles. Fixed: vertices 1–3. Fixed Alternate: 2–4.")]
        public QuadMethod quadMethod = QuadMethod.Beauty;
        [Tooltip("Clip: ear clipping (what the viewport shows). Beauty: ear clipping, then edge flips for better-shaped triangles.")]
        public NgonMethod ngonMethod = NgonMethod.Beauty;
        [Tooltip("Only faces with at least this many vertices are split (5 = keep quads).")]
        [Min(4)] public int minimumVertices = 4;

        public override void Apply(MeshData mesh) => Triangulate(mesh, quadMethod, ngonMethod, minimumVertices);

        public static void Triangulate(MeshData m, QuadMethod quad, NgonMethod ngon, int minimumVertices)
        {
            var nf = new List<Face>(m.faces.Count * 2);
            foreach (var f in m.faces)
            {
                if (f.Count < Mathf.Max(4, minimumVertices)) { nf.Add(f); continue; }
                List<Vector3Int> tris;
                if (f.Count == 4) tris = Quad(m, f, quad);
                else
                {
                    tris = m.Triangulate(f);
                    if (ngon == NgonMethod.Beauty) Beautify(m, f, tris);
                }
                foreach (var t in tris)
                    nf.Add(new Face(new[] { f.v[t.x], f.v[t.y], f.v[t.z] }, new[] { f.a[t.x], f.a[t.y], f.a[t.z] }, f.sub));
            }
            m.faces = nf;
        }

        static List<Vector3Int> Quad(MeshData m, Face f, QuadMethod method)
        {
            Vector3 P(int i) => m.pos[f.v[i]];
            var a = new List<Vector3Int> { new Vector3Int(0, 1, 2), new Vector3Int(0, 2, 3) }; // diagonal 1–3
            var b = new List<Vector3Int> { new Vector3Int(0, 1, 3), new Vector3Int(1, 2, 3) }; // diagonal 2–4
            // a concave quad has only one diagonal inside it
            var n = m.FaceNormal(f);
            bool Valid(List<Vector3Int> t) => t.TrueForAll(x => Vector3.Dot(Vector3.Cross(P(x.y) - P(x.x), P(x.z) - P(x.x)), n) > 0);
            bool okA = Valid(a), okB = Valid(b);
            if (okA != okB) return okA ? a : b;
            switch (method)
            {
                case QuadMethod.Fixed: return a;
                case QuadMethod.FixedAlternate: return b;
                case QuadMethod.ShortestDiagonal: return (P(0) - P(2)).sqrMagnitude <= (P(1) - P(3)).sqrMagnitude ? a : b;
                case QuadMethod.LongestDiagonal: return (P(0) - P(2)).sqrMagnitude >= (P(1) - P(3)).sqrMagnitude ? a : b;
                default: return MinAngle(m, f, a) >= MinAngle(m, f, b) ? a : b;
            }
        }

        static float MinAngle(MeshData m, Face f, List<Vector3Int> tris)
        {
            float best = 180f;
            foreach (var t in tris) best = Mathf.Min(best, MinAngle(m.pos[f.v[t.x]], m.pos[f.v[t.y]], m.pos[f.v[t.z]]));
            return best;
        }

        static float MinAngle(Vector3 a, Vector3 b, Vector3 c) =>
            Mathf.Min(Vector3.Angle(b - a, c - a), Mathf.Min(Vector3.Angle(a - b, c - b), Vector3.Angle(a - c, b - c)));

        /// <summary>Flip inner diagonals while that raises the smallest angle of the two triangles (local Delaunay).</summary>
        static void Beautify(MeshData m, Face f, List<Vector3Int> tris)
        {
            Vector3 P(int i) => m.pos[f.v[i]];
            var n = m.FaceNormal(f);
            for (int pass = 0, changed = 1; changed > 0 && pass < f.Count * 4; pass++)
            {
                changed = 0;
                for (int i = 0; i < tris.Count; i++)
                    for (int j = i + 1; j < tris.Count; j++)
                    {
                        // shared edge (p, q): tri i = (p, q, r), tri j = (q, p, s)
                        if (!Shared(tris[i], tris[j], out int p, out int q, out int r, out int s)) continue;
                        var t1 = new Vector3Int(r, s, q); var t2 = new Vector3Int(s, r, p);
                        bool convex = Vector3.Dot(Vector3.Cross(P(t1.y) - P(t1.x), P(t1.z) - P(t1.x)), n) > 0
                                   && Vector3.Dot(Vector3.Cross(P(t2.y) - P(t2.x), P(t2.z) - P(t2.x)), n) > 0;
                        if (!convex) continue;
                        float before = Mathf.Min(MinAngle(P(p), P(q), P(r)), MinAngle(P(q), P(p), P(s)));
                        float after = Mathf.Min(MinAngle(P(r), P(s), P(q)), MinAngle(P(s), P(r), P(p)));
                        if (after <= before + 1e-3f) continue;
                        tris[i] = t1; tris[j] = t2; changed++;
                    }
            }
        }

        static bool Shared(Vector3Int a, Vector3Int b, out int p, out int q, out int r, out int s)
        {
            int[] A = { a.x, a.y, a.z }, B = { b.x, b.y, b.z };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (A[i] == B[(j + 1) % 3] && A[(i + 1) % 3] == B[j]) // edge A[i]→A[i+1] runs B[j+1]→B[j]
                    {
                        p = A[i]; q = A[(i + 1) % 3]; r = A[(i + 2) % 3]; s = B[(j + 2) % 3];
                        return true;
                    }
            p = q = r = s = -1;
            return false;
        }
    }
}
