using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender's Cell Fracture: cut a closed mesh into Voronoi pieces. Seeds are random points inside the mesh; each
    /// piece is the mesh intersected (Boolean) with its seed's cell, so non-convex meshes work. The cut faces go to a new
    /// last submesh (the interior material slot), with box-projected UVs in metres.
    /// </summary>
    // ponytail: O(pieces²) planes and one BSP Boolean per piece; fine for props up to a few thousand faces.
    public static class BlendFracture
    {
        /// <summary>Pieces as meshes centred on their own pivot; <paramref name="centres"/> = where each pivot sits in the source mesh.</summary>
        public static List<Mesh> Cut(Mesh src, int count, int seed, out List<Vector3> centres)
        {
            var data = MeshData.FromMesh(src, false);
            centres = new List<Vector3>();
            var result = new List<Mesh>();
            var tris = new List<Vector3>();
            foreach (var f in data.faces)
                foreach (var t in data.Triangulate(f)) { tris.Add(data.pos[f.v[t.x]]); tris.Add(data.pos[f.v[t.y]]); tris.Add(data.pos[f.v[t.z]]); }
            if (tris.Count == 0) return result;

            var bounds = data.GetBounds();
            var rng = new System.Random(seed * 7919 + 17);
            var seeds = new List<Vector3>();
            for (int tries = 0; seeds.Count < count && tries < count * 200; tries++)
            {
                var p = new Vector3(Lerp(bounds.min.x, bounds.max.x, rng), Lerp(bounds.min.y, bounds.max.y, rng), Lerp(bounds.min.z, bounds.max.z, rng));
                if (Inside(p, tris)) seeds.Add(p);
            }

            var box = bounds; box.Expand(bounds.size.magnitude * 0.1f + 1e-3f);
            int interior = data.subMeshCount;
            for (int i = 0; i < seeds.Count; i++)
            {
                var cell = Box(box);
                for (int j = 0; j < seeds.Count && cell.Count >= 4; j++)
                {
                    if (j == i) continue;
                    var n = (seeds[i] - seeds[j]).normalized;
                    cell = Clip(cell, n, Vector3.Dot(n, (seeds[i] + seeds[j]) * 0.5f));
                }
                if (cell.Count < 4) continue;

                var piece = data.Clone();
                BooleanModifier.Csg(piece, ToMeshData(cell, interior), BooleanModifier.Operation.Intersect);
                if (piece.faces.Count < 4) continue;
                piece.subMeshCount = interior + 1;
                var c = piece.GetBounds().center;
                for (int k = 0; k < piece.pos.Count; k++) piece.pos[k] -= c;
                piece.customNormals = true; // keep the source's smooth normals; cut faces carry their flat ones
                var mesh = new Mesh { name = $"{src.name}_piece{result.Count}" };
                piece.ToMesh(mesh, 30f);
                mesh.RecalculateTangents();
                result.Add(mesh);
                centres.Add(c);
            }
            return result;
        }

        static float Lerp(float a, float b, System.Random r) => a + (b - a) * (float)r.NextDouble();

        // ray parity along a skewed direction (avoids hitting edges exactly)
        static bool Inside(Vector3 p, List<Vector3> tris)
        {
            var d = new Vector3(1f, 0.00137f, 0.00071f).normalized;
            int hits = 0;
            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 a = tris[t], e1 = tris[t + 1] - a, e2 = tris[t + 2] - a;
                var h = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, h);
                if (Mathf.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var s = p - a;
                float u = Vector3.Dot(s, h) * inv;
                if (u < 0f || u > 1f) continue;
                var q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(d, q) * inv;
                if (v < 0f || u + v > 1f) continue;
                if (Vector3.Dot(e2, q) * inv > 0f) hits++;
            }
            return (hits & 1) == 1;
        }

        static List<List<Vector3>> Box(Bounds b)
        {
            Vector3 n = b.min, x = b.max;
            Vector3[] c =
            {
                new Vector3(n.x, n.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, x.y, n.z), new Vector3(n.x, x.y, n.z),
                new Vector3(n.x, n.y, x.z), new Vector3(x.x, n.y, x.z), new Vector3(x.x, x.y, x.z), new Vector3(n.x, x.y, x.z),
            };
            int[][] faces = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 2, 6, 7 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 } };
            var r = new List<List<Vector3>>();
            foreach (var f in faces) r.Add(new List<Vector3> { c[f[0]], c[f[1]], c[f[2]], c[f[3]] });
            return r;
        }

        /// <summary>Keep the part of a convex polyhedron with dot(n, p) >= d, closing the cut with a new face.</summary>
        static List<List<Vector3>> Clip(List<List<Vector3>> polys, Vector3 n, float d)
        {
            var r = new List<List<Vector3>>();
            var cut = new List<Vector3>();
            foreach (var poly in polys)
            {
                var o = new List<Vector3>();
                for (int i = 0; i < poly.Count; i++)
                {
                    Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
                    float sa = Vector3.Dot(n, a) - d, sb = Vector3.Dot(n, b) - d;
                    if (sa >= 0f) o.Add(a);
                    if ((sa >= 0f) != (sb >= 0f))
                    {
                        var p = Vector3.LerpUnclamped(a, b, sa / (sa - sb));
                        o.Add(p); cut.Add(p);
                    }
                }
                if (o.Count >= 3) r.Add(o);
            }
            if (cut.Count >= 3)
            {
                Vector3 c = Vector3.zero;
                foreach (var p in cut) c += p;
                c /= cut.Count;
                var u = Vector3.Cross(n, Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                var v = Vector3.Cross(n, u);
                cut.Sort((p, q) => Mathf.Atan2(Vector3.Dot(p - c, v), Vector3.Dot(p - c, u)).CompareTo(Mathf.Atan2(Vector3.Dot(q - c, v), Vector3.Dot(q - c, u))));
                var cap = new List<Vector3>();
                foreach (var p in cut) if (cap.Count == 0 || (p - cap[cap.Count - 1]).sqrMagnitude > 1e-12f) cap.Add(p);
                if (cap.Count > 1 && (cap[0] - cap[cap.Count - 1]).sqrMagnitude <= 1e-12f) cap.RemoveAt(cap.Count - 1);
                if (cap.Count >= 3) r.Add(cap);
            }
            return r;
        }

        static MeshData ToMeshData(List<List<Vector3>> polys, int sub)
        {
            var m = new MeshData();
            Vector3 centroid = Vector3.zero; int k = 0;
            foreach (var p in polys) foreach (var v in p) { centroid += v; k++; }
            centroid /= Mathf.Max(k, 1);
            foreach (var p in polys)
            {
                var v = new int[p.Count];
                for (int i = 0; i < p.Count; i++) { v[i] = m.pos.Count; m.pos.Add(p[i]); }
                var f = new Face(v, new Attr[p.Count], sub);
                var fc = m.FaceCenter(f);
                if (Vector3.Dot(m.FaceNormal(f), fc - centroid) < 0f) f.Reverse(); // outward
                var nrm = MeshData.Unit(m.FaceNormal(f));
                var an = new Vector3(Mathf.Abs(nrm.x), Mathf.Abs(nrm.y), Mathf.Abs(nrm.z));
                for (int i = 0; i < f.Count; i++)
                {
                    var q = m.pos[f.v[i]];
                    var uv = an.x >= an.y && an.x >= an.z ? new Vector2(q.z, q.y) : an.y >= an.z ? new Vector2(q.x, q.z) : new Vector2(q.x, q.y);
                    f.a[i] = new Attr { uv0 = uv, color = Color.white, normal = nrm };
                }
                m.faces.Add(f);
            }
            m.subMeshCount = sub + 1;
            m.Weld(1e-6f);
            return m;
        }
    }
}
