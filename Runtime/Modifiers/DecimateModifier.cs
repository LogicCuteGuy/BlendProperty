using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Decimate.png")]
    [AddComponentMenu("Blend Modifier/Generate/Decimate")]
    public class DecimateModifier : BlendModifier
    {
        public enum Mode { Collapse, Planar, UnSubdivide }

        public Mode mode = Mode.Collapse;
        [Range(0, 1)] public float ratio = 0.5f;
        [Tooltip("Collapse: keep the triangles (off = rejoin flat triangle pairs into quads).")]
        public bool triangulate;
        [Range(0, 180)] public float angleLimit = 5f;
        [Tooltip("Planar: don't dissolve across material borders.")]
        public bool delimitMaterial = true;
        [Tooltip("Planar: don't dissolve across UV seams.")]
        public bool delimitUV;
        [Range(1, 10), Tooltip("Un-Subdivide: how many times. Two iterations undo one subdivision level.")]
        public int iterations = 2;

        public override void Apply(MeshData mesh)
        {
            if (mode == Mode.Planar) Planar(mesh, angleLimit, delimitMaterial, delimitUV);
            else if (mode == Mode.UnSubdivide) { for (int i = 0; i < iterations / 2 && UnSubdivide(mesh); i++) { } }
            else
            {
                Collapse(mesh, ratio);
                if (!triangulate) mesh.JoinTrianglesToQuads();
            }
        }

        // ------------------------------------------------------------------ collapse (QEM)

        // ponytail: greedy quadric edge collapse choosing best of {a, b, midpoint}; no 3x3 solve, no UV-seam weighting.
        public static void Collapse(MeshData m, float ratio)
        {
            if (ratio >= 1f) return;
            m.TriangulateAll();
            var P = m.pos;
            int nv = P.Count;
            int target = Mathf.Max(1, Mathf.RoundToInt(m.faces.Count * ratio));

            var Q = new Matrix4x4[nv];
            var vertFaces = new List<int>[nv];
            for (int i = 0; i < nv; i++) vertFaces[i] = new List<int>();
            for (int fi = 0; fi < m.faces.Count; fi++)
            {
                var f = m.faces[fi];
                var n = MeshData.Unit(m.FaceNormal(f));
                var K = Plane(n, -Vector3.Dot(n, P[f.v[0]]));
                foreach (var v in f.v) { Add(ref Q[v], K); vertFaces[v].Add(fi); }
            }
            // boundary edges: perpendicular penalty planes so borders keep their shape
            foreach (var kv in m.EdgeFaces())
            {
                if (kv.Value.Count != 1) continue;
                int a = MeshData.EdgeA(kv.Key), b = MeshData.EdgeB(kv.Key);
                var fn = MeshData.Unit(m.FaceNormal(m.faces[kv.Value[0]]));
                var n = MeshData.Unit(Vector3.Cross(P[b] - P[a], fn));
                var K = Scale(Plane(n, -Vector3.Dot(n, P[a])), 100f);
                Add(ref Q[a], K); Add(ref Q[b], K);
            }

            var parent = new int[nv];
            for (int i = 0; i < nv; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var dead = new bool[m.faces.Count];
            var version = new int[nv];
            int alive = m.faces.Count;

            var heap = new MinHeap();
            void Push(int a, int b)
            {
                var (cost, _) = Best(a, b);
                heap.Push(cost, a, b, version[a], version[b]);
            }
            (float cost, Vector3 p) Best(int a, int b)
            {
                var q = Sum(Q[a], Q[b]);
                Vector3 pa = P[a], pb = P[b], pm = (pa + pb) * 0.5f;
                float ca = Err(q, pa), cb = Err(q, pb), cm = Err(q, pm);
                if (cm <= ca && cm <= cb) return (cm, pm);
                return ca <= cb ? (ca, pa) : (cb, pb);
            }
            foreach (var kv in m.EdgeFaces()) Push(MeshData.EdgeA(kv.Key), MeshData.EdgeB(kv.Key));

            while (alive > target && heap.Count > 0)
            {
                var e = heap.Pop();
                int a = e.a, b = e.b;
                if (Find(a) != a || Find(b) != b || version[a] != e.va || version[b] != e.vb) continue;
                var (_, np) = Best(a, b);
                if (Flips(a, b, np) || Flips(b, a, np)) continue;

                // collapse b into a
                parent[b] = a;
                P[a] = np;
                Q[a] = Sum(Q[a], Q[b]);
                version[a]++;
                foreach (var fi in vertFaces[b])
                {
                    if (dead[fi]) continue;
                    var f = m.faces[fi];
                    bool hasA = false;
                    for (int c = 0; c < 3; c++) if (Find(f.v[c]) == a && f.v[c] != b) hasA = true;
                    for (int c = 0; c < 3; c++) if (f.v[c] == b) f.v[c] = a;
                    if (hasA) { dead[fi] = true; alive--; }
                    else vertFaces[a].Add(fi);
                }
                var neigh = new HashSet<int>();
                foreach (var fi in vertFaces[a])
                    if (!dead[fi]) foreach (var v in m.faces[fi].v) if (v != a) neigh.Add(Find(v));
                foreach (var v in neigh) Push(a, v);
            }

            bool Flips(int keep, int gone, Vector3 np)
            {
                foreach (var fi in vertFaces[keep])
                {
                    if (dead[fi]) continue;
                    var f = m.faces[fi];
                    bool touchesGone = false;
                    foreach (var v in f.v) if (Find(v) == gone) touchesGone = true;
                    if (touchesGone) continue; // will be removed
                    Vector3 p0 = P[Find(f.v[0])], p1 = P[Find(f.v[1])], p2 = P[Find(f.v[2])];
                    var before = Vector3.Cross(p1 - p0, p2 - p0);
                    for (int c = 0; c < 3; c++) if (Find(f.v[c]) == keep) { if (c == 0) p0 = np; else if (c == 1) p1 = np; else p2 = np; }
                    var after = Vector3.Cross(p1 - p0, p2 - p0);
                    if (Vector3.Dot(MeshData.Unit(before), MeshData.Unit(after)) < 0.2f) return true;
                }
                return false;
            }

            var faces = new List<Face>(alive);
            for (int fi = 0; fi < m.faces.Count; fi++)
            {
                if (dead[fi]) continue;
                var f = m.faces[fi];
                for (int c = 0; c < 3; c++) f.v[c] = Find(f.v[c]);
                faces.Add(f);
            }
            m.faces = faces;
            m.Compact();
        }

        static Matrix4x4 Plane(Vector3 n, float d)
        {
            var v = new Vector4(n.x, n.y, n.z, d);
            var M = new Matrix4x4();
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) M[r, c] = v[r] * v[c];
            return M;
        }
        static void Add(ref Matrix4x4 a, Matrix4x4 b) { for (int i = 0; i < 16; i++) a[i] += b[i]; }
        static Matrix4x4 Sum(Matrix4x4 a, Matrix4x4 b) { Add(ref a, b); return a; }
        static Matrix4x4 Scale(Matrix4x4 a, float s) { for (int i = 0; i < 16; i++) a[i] *= s; return a; }
        static float Err(Matrix4x4 q, Vector3 p)
        {
            var v = new Vector4(p.x, p.y, p.z, 1);
            return Vector4.Dot(v, q * v);
        }

        class MinHeap
        {
            readonly List<(float cost, int a, int b, int va, int vb)> h = new List<(float, int, int, int, int)>();
            public int Count => h.Count;
            public void Push(float cost, int a, int b, int va, int vb)
            {
                h.Add((cost, a, b, va, vb));
                int i = h.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (h[p].cost <= h[i].cost) break; (h[p], h[i]) = (h[i], h[p]); i = p; }
            }
            public (float cost, int a, int b, int va, int vb) Pop()
            {
                var top = h[0];
                h[0] = h[h.Count - 1]; h.RemoveAt(h.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, s = i;
                    if (l < h.Count && h[l].cost < h[s].cost) s = l;
                    if (r < h.Count && h[r].cost < h[s].cost) s = r;
                    if (s == i) break;
                    (h[s], h[i]) = (h[i], h[s]); i = s;
                }
                return top;
            }
        }

        // ------------------------------------------------------------------ planar

        /// <summary>Dissolve edges between near-coplanar faces into n-gons, then drop collinear border vertices.</summary>
        public static void Planar(MeshData m, float angleDeg, bool delimitMaterial = true, bool delimitUV = false)
        {
            float cosLimit = Mathf.Cos(angleDeg * Mathf.Deg2Rad);
            var fn = new Vector3[m.faces.Count];
            for (int i = 0; i < fn.Length; i++) fn[i] = MeshData.Unit(m.FaceNormal(m.faces[i]));
            var edgeFaces = m.EdgeFaces();

            // flood-fill coplanar regions (same submesh)
            var region = new int[m.faces.Count];
            for (int i = 0; i < region.Length; i++) region[i] = -1;
            var regions = new List<List<int>>();
            for (int s = 0; s < m.faces.Count; s++)
            {
                if (region[s] >= 0) continue;
                var list = new List<int> { s };
                region[s] = regions.Count;
                for (int q = 0; q < list.Count; q++)
                {
                    var f = m.faces[list[q]];
                    for (int c = 0; c < f.Count; c++)
                        foreach (var g in edgeFaces[MeshData.EdgeKey(f.v[c], f.v[(c + 1) % f.Count])])
                            if (region[g] < 0 && (!delimitMaterial || m.faces[g].sub == f.sub) && Vector3.Dot(fn[g], fn[s]) >= cosLimit
                                && (!delimitUV || SameUV(f, c, m.faces[g])))
                            { region[g] = regions.Count; list.Add(g); }
                }
                regions.Add(list);
            }

            var result = new List<Face>();
            foreach (var list in regions)
            {
                var poly = list.Count > 1 ? Outline(m, list) : null;
                if (poly == null) { foreach (var fi in list) result.Add(m.faces[fi]); continue; }
                result.Add(poly);
            }
            m.faces = result;
            RemoveCollinear(m);
            m.Compact();
        }

        /// <summary>Do both faces carry the same UVs on their shared edge (corner c → c+1 of <paramref name="f"/>)?</summary>
        static bool SameUV(Face f, int c, Face g)
        {
            int a = f.v[c], b = f.v[(c + 1) % f.Count];
            int ia = System.Array.IndexOf(g.v, a), ib = System.Array.IndexOf(g.v, b);
            if (ia < 0 || ib < 0) return true;
            return (f.a[c].uv0 - g.a[ia].uv0).sqrMagnitude < 1e-10f && (f.a[(c + 1) % f.Count].uv0 - g.a[ib].uv0).sqrMagnitude < 1e-10f;
        }

        // ------------------------------------------------------------------ un-subdivide

        /// <summary>
        /// Undo one Catmull-Clark level on an all-quad mesh. Each subdivided quad is original corner → edge point →
        /// face point → edge point, so vertices are 2-coloured over edges (edge points vs the rest), then over quad
        /// diagonals (originals vs face points). Every face point's ring of quads becomes one original face.
        /// Returns false (mesh untouched) when the topology isn't a subdivided grid.
        /// </summary>
        public static bool UnSubdivide(MeshData m)
        {
            int n = m.pos.Count;
            foreach (var f in m.faces) if (f.Count != 4) return false;
            var nb = m.Neighbors();
            var diag = new List<int>[n];
            for (int i = 0; i < n; i++) diag[i] = new List<int>();
            foreach (var f in m.faces)
            {
                diag[f.v[0]].Add(f.v[2]); diag[f.v[2]].Add(f.v[0]);
                diag[f.v[1]].Add(f.v[3]); diag[f.v[3]].Add(f.v[1]);
            }

            var edgeFaces = m.EdgeFaces();
            var boundary = new bool[n];
            foreach (var kv in edgeFaces) if (kv.Value.Count == 1) boundary[MeshData.EdgeA(kv.Key)] = boundary[MeshData.EdgeB(kv.Key)] = true;
            // edge points are always regular (valence 4 inside, 3 on a boundary); originals often aren't
            bool Irregular(int v) => nb[v].Count != (boundary[v] ? 3 : 4);

            // 1) edge points (kind 1) vs originals + face points (kind 0), alternating along edges
            var kind = new int[n];
            for (int i = 0; i < n; i++) kind[i] = -1;
            for (int s = 0; s < n; s++)
            {
                if (kind[s] >= 0 || nb[s].Count == 0) continue;
                kind[s] = 0;
                var comp = new List<int>();
                var q = new Queue<int>(); q.Enqueue(s);
                while (q.Count > 0)
                {
                    int v = q.Dequeue(); comp.Add(v);
                    foreach (var w in nb[v])
                    {
                        if (kind[w] < 0) { kind[w] = 1 - kind[v]; q.Enqueue(w); }
                        else if (kind[w] == kind[v]) return false;
                    }
                }
                int irr0 = 0, irr1 = 0;
                foreach (var v in comp) if (Irregular(v)) { if (kind[v] == 0) irr0++; else irr1++; }
                if (irr1 > irr0) foreach (var v in comp) kind[v] = 1 - kind[v];
            }

            // 2) among kind-0 vertices: originals (2) vs face points (3), alternating along diagonals
            for (int s = 0; s < n; s++)
            {
                if (kind[s] != 0) continue;
                var comp = new List<int>();
                kind[s] = 2;
                var q = new Queue<int>(); q.Enqueue(s);
                while (q.Count > 0)
                {
                    int v = q.Dequeue(); comp.Add(v);
                    foreach (var w in diag[v])
                    {
                        if (kind[w] == 1) continue;
                        if (kind[w] == 0) { kind[w] = kind[v] == 2 ? 3 : 2; q.Enqueue(w); }
                        else if (kind[w] == kind[v]) return false;
                    }
                }
                // pick which colour is the face points: no boundary vertices, and valence 4 more often
                int Score(int k)
                {
                    int sc = 0;
                    foreach (var v in comp) if (kind[v] == k) { if (boundary[v]) sc -= 1000; if (nb[v].Count == 4) sc++; else sc -= 1; }
                    return sc;
                }
                if (Score(2) > Score(3)) foreach (var v in comp) kind[v] = kind[v] == 2 ? 3 : 2;
            }

            // 3) rebuild: walk the quads around each face point
            var around = new Dictionary<int, List<(Face f, int i)>>();
            foreach (var f in m.faces)
                for (int i = 0; i < 4; i++)
                    if (kind[f.v[i]] == 3)
                    {
                        if (!around.TryGetValue(f.v[i], out var l)) around[f.v[i]] = l = new List<(Face, int)>();
                        l.Add((f, i));
                    }
            var result = new List<Face>();
            foreach (var kv in around)
            {
                var quads = kv.Value;
                var v = new List<int>(); var a = new List<Attr>();
                var cur = quads[0];
                for (int step = 0; step < quads.Count; step++)
                {
                    var (f, i) = cur;
                    if (kind[f.v[(i + 2) % 4]] != 2) return false;
                    v.Add(f.v[(i + 2) % 4]); a.Add(f.a[(i + 2) % 4]);
                    int b = f.v[(i + 3) % 4]; // the next quad starts F → b
                    int found = quads.FindIndex(x => x.f.v[(x.i + 1) % 4] == b);
                    if (found < 0) break;
                    cur = quads[found];
                    if (cur.f == quads[0].f) break;
                }
                if (v.Count < 3) return false;
                result.Add(new Face(v.ToArray(), a.ToArray(), quads[0].f.sub));
            }
            if (result.Count == 0) return false;
            m.faces = result;
            m.Compact();
            return true;
        }

        static Face Outline(MeshData m, List<int> list)
        {
            var next = new Dictionary<int, (int to, Attr at)>();
            var edgeCount = new Dictionary<long, int>(MeshData.Keys);
            foreach (var fi in list)
            {
                var f = m.faces[fi];
                for (int c = 0; c < f.Count; c++)
                {
                    long k = MeshData.EdgeKey(f.v[c], f.v[(c + 1) % f.Count]);
                    edgeCount[k] = edgeCount.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            }
            foreach (var fi in list)
            {
                var f = m.faces[fi];
                for (int c = 0; c < f.Count; c++)
                {
                    int a = f.v[c], b = f.v[(c + 1) % f.Count];
                    if (edgeCount[MeshData.EdgeKey(a, b)] != 1) continue;
                    if (next.ContainsKey(a)) return null; // pinched outline
                    next[a] = (b, f.a[c]);
                }
            }
            if (next.Count < 3) return null;
            var v = new List<int>(); var at = new List<Attr>();
            int start = -1;
            foreach (var k in next.Keys) { start = k; break; }
            int cur = start;
            do
            {
                if (!next.TryGetValue(cur, out var e)) return null;
                v.Add(cur); at.Add(e.at);
                cur = e.to;
                if (v.Count > next.Count) return null;
            } while (cur != start);
            if (v.Count != next.Count) return null; // holes / multiple loops
            return new Face(v.ToArray(), at.ToArray(), m.faces[list[0]].sub);
        }

        static void RemoveCollinear(MeshData m)
        {
            // valence-2 border vertices on a straight line can go (only if every face using them agrees)
            var useCount = new int[m.pos.Count];
            foreach (var f in m.faces) foreach (var v in f.v) useCount[v]++;
            foreach (var f in m.faces)
            {
                if (f.Count <= 3) continue;
                var v = new List<int>(f.v); var a = new List<Attr>(f.a);
                for (int c = 0; c < v.Count && v.Count > 3;)
                {
                    Vector3 p = m.pos[v[c]], prev = m.pos[v[(c + v.Count - 1) % v.Count]], nx = m.pos[v[(c + 1) % v.Count]];
                    bool straight = Vector3.Cross(MeshData.Unit(p - prev), MeshData.Unit(nx - p)).sqrMagnitude < 1e-8f;
                    if (straight && useCount[v[c]] == 1) { v.RemoveAt(c); a.RemoveAt(c); }
                    else c++;
                }
                f.v = v.ToArray(); f.a = a.ToArray();
            }
        }
    }
}
