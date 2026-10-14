using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlendProperty
{
    /// <summary>Per face-corner attributes (Blender "loop" data).</summary>
    [Serializable]
    public struct Attr : IEquatable<Attr>
    {
        public Vector2 uv0, uv1;
        public Color color;
        public Vector3 normal;

        public bool Equals(Attr o) => uv0 == o.uv0 && uv1 == o.uv1 && color == o.color && normal == o.normal;
        public override bool Equals(object o) => o is Attr a && Equals(a);
        public override int GetHashCode() => HashCode.Combine(uv0, uv1, color, normal);

        public static Attr Lerp(Attr a, Attr b, float t) => new Attr
        {
            uv0 = Vector2.LerpUnclamped(a.uv0, b.uv0, t),
            uv1 = Vector2.LerpUnclamped(a.uv1, b.uv1, t),
            color = Color.LerpUnclamped(a.color, b.color, t),
            normal = Vector3.LerpUnclamped(a.normal, b.normal, t),
        };

        public static Attr Average(IList<Attr> list)
        {
            var r = new Attr();
            if (list.Count == 0) return r;
            foreach (var a in list) { r.uv0 += a.uv0; r.uv1 += a.uv1; r.color += a.color; r.normal += a.normal; }
            float k = 1f / list.Count;
            r.uv0 *= k; r.uv1 *= k; r.color *= k; r.normal *= k;
            return r;
        }
    }

    /// <summary>Polygon: position indices + matching corner attributes.</summary>
    public class Face
    {
        public int[] v;
        public Attr[] a;
        public int sub;

        public Face(int[] v, Attr[] a, int sub) { this.v = v; this.a = a; this.sub = sub; }
        public int Count => v.Length;
        public Face Clone() => new Face((int[])v.Clone(), (Attr[])a.Clone(), sub);

        public void Reverse() { Array.Reverse(v); Array.Reverse(a); }
    }

    /// <summary>
    /// Blender-like editable mesh: welded positions + polygons with per-corner attributes.
    /// Modifiers read and write this; the stack converts to/from UnityEngine.Mesh.
    /// </summary>
    public class MeshData
    {
        public List<Vector3> pos = new List<Vector3>();
        public List<Face> faces = new List<Face>();
        public int subMeshCount = 1;
        public bool hasUV1, hasColor;
        /// <summary>Set by Normals modifiers; export then uses Attr.normal as-is.</summary>
        public bool customNormals;

        public MeshData Clone()
        {
            var m = new MeshData { pos = new List<Vector3>(pos), subMeshCount = subMeshCount, hasUV1 = hasUV1, hasColor = hasColor, customNormals = customNormals };
            foreach (var f in faces) m.faces.Add(f.Clone());
            return m;
        }

        /// <summary>Append another MeshData (positions transformed by <paramref name="xf"/>).</summary>
        public void Append(MeshData o, Matrix4x4 xf, bool flip = false)
        {
            int baseIdx = pos.Count;
            if (pos.Capacity < pos.Count + o.pos.Count) pos.Capacity = Math.Max(pos.Count + o.pos.Count, pos.Capacity * 2);
            if (faces.Capacity < faces.Count + o.faces.Count) faces.Capacity = Math.Max(faces.Count + o.faces.Count, faces.Capacity * 2);
            foreach (var p in o.pos) pos.Add(xf.MultiplyPoint3x4(p));
            var nxf = xf.inverse.transpose;
            foreach (var f in o.faces)
            {
                int n = f.Count;
                var v = new int[n]; var a = new Attr[n];
                for (int i = 0; i < n; i++)
                {
                    int j = flip ? n - 1 - i : i;
                    v[i] = f.v[j] + baseIdx;
                    a[i] = f.a[j]; a[i].normal = Unit(nxf.MultiplyVector(a[i].normal));
                }
                faces.Add(new Face(v, a, f.sub));
            }
        }

        /// <summary>Append one copy of <paramref name="o"/> per matrix (mirrored copies are flipped).</summary>
        public void AppendCopies(MeshData o, List<Matrix4x4> xfs)
        {
            int n = xfs.Count, pc = o.pos.Count, fcnt = o.faces.Count, basePos = pos.Count;
            var P = new Vector3[n * pc];
            var F = new Face[n * fcnt];
            void Copy(int k)
            {
                var xf = xfs[k];
                var nxf = xf.inverse.transpose;
                bool flip = xf.determinant < 0;
                int bi = basePos + k * pc;
                for (int i = 0; i < pc; i++) P[k * pc + i] = xf.MultiplyPoint3x4(o.pos[i]);
                for (int fi = 0; fi < fcnt; fi++)
                {
                    var f = o.faces[fi];
                    int m = f.Count;
                    var v = new int[m]; var a = new Attr[m];
                    for (int i = 0; i < m; i++)
                    {
                        int j = flip ? m - 1 - i : i;
                        v[i] = f.v[j] + bi;
                        a[i] = f.a[j]; a[i].normal = Unit(nxf.MultiplyVector(a[i].normal));
                    }
                    F[k * fcnt + fi] = new Face(v, a, f.sub);
                }
            }
            for (int k = 0; k < n; k++) Copy(k);
            pos.AddRange(P);
            faces.AddRange(F);
        }

        // ---------------------------------------------------------------- conversion

        public static MeshData FromMesh(Mesh mesh, bool joinTriangles = true)
        {
            var d = new MeshData();
            if (mesh == null) return d;
            var verts = mesh.vertices;
            var nrm = mesh.normals;
            var uv0 = new List<Vector2>(); mesh.GetUVs(0, uv0);
            var uv1 = new List<Vector2>(); mesh.GetUVs(1, uv1);
            var col = mesh.colors;
            d.hasUV1 = uv1.Count == verts.Length;
            d.hasColor = col.Length == verts.Length;
            d.subMeshCount = Mathf.Max(1, mesh.subMeshCount);

            var weld = new Dictionary<Vector3, int>();
            var remap = new int[verts.Length];
            var attrs = new Attr[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                if (!weld.TryGetValue(verts[i], out int id)) { id = d.pos.Count; d.pos.Add(verts[i]); weld[verts[i]] = id; }
                remap[i] = id;
                attrs[i] = new Attr
                {
                    uv0 = i < uv0.Count ? uv0[i] : Vector2.zero,
                    uv1 = d.hasUV1 ? uv1[i] : Vector2.zero,
                    color = d.hasColor ? col[i] : Color.white,
                    normal = i < nrm.Length ? nrm[i] : Vector3.up,
                };
            }

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (remap[a] == remap[b] || remap[b] == remap[c] || remap[c] == remap[a]) continue;
                    d.faces.Add(new Face(new[] { remap[a], remap[b], remap[c] }, new[] { attrs[a], attrs[b], attrs[c] }, s));
                }
            }
            if (joinTriangles) d.JoinTrianglesToQuads();
            return d;
        }

        /// <summary>Writes into <paramref name="mesh"/> (cleared first).</summary>
        public void ToMesh(Mesh mesh, float autoSmoothAngle)
        {
            Vector3[][] cornerN = customNormals ? null : CornerNormals(autoSmoothAngle, null);

            int corners = 0;
            foreach (var f in faces) corners += f.Count;
            var outV = new List<Vector3>(corners);
            var outN = new List<Vector3>(corners);
            var outUV0 = new List<Vector2>(corners);
            var outUV1 = new List<Vector2>(hasUV1 ? corners : 0);
            var outC = new List<Color>(hasColor ? corners : 0);
            var subs = new List<int>[subMeshCount];
            for (int s = 0; s < subMeshCount; s++) subs[s] = new List<int>();

            // Dedupe corners per position: a short chain of the output vertices already made for it.
            // (A Dictionary keyed on (position, Attr) spent most of its time hashing the 52-byte Attr.)
            var head = new int[pos.Count];
            for (int i = 0; i < head.Length; i++) head[i] = -1;
            var next = new List<int>(corners);
            var outA = new List<Attr>(corners);
            var idx = new int[8];

            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                if (idx.Length < f.Count) idx = new int[f.Count];
                for (int c = 0; c < f.Count; c++)
                {
                    var at = f.a[c];
                    at.normal = cornerN != null ? cornerN[fi][c] : Unit(at.normal);
                    if (!hasUV1) at.uv1 = Vector2.zero;
                    if (!hasColor) at.color = Color.white;
                    int p = f.v[c], vi = head[p];
                    while (vi >= 0 && !Same(outA[vi], at)) vi = next[vi];
                    if (vi < 0)
                    {
                        vi = outV.Count;
                        outV.Add(pos[p]); outN.Add(at.normal); outUV0.Add(at.uv0);
                        if (hasUV1) outUV1.Add(at.uv1);
                        if (hasColor) outC.Add(at.color);
                        outA.Add(at); next.Add(head[p]); head[p] = vi;
                    }
                    idx[c] = vi;
                }
                var list = subs[Mathf.Clamp(f.sub, 0, subMeshCount - 1)];
                if (f.Count == 3 || f.Count == 4 && IsConvex(f))
                {
                    list.Add(idx[0]); list.Add(idx[1]); list.Add(idx[2]);
                    if (f.Count == 4) { list.Add(idx[0]); list.Add(idx[2]); list.Add(idx[3]); }
                }
                else foreach (var t in Triangulate(f)) { list.Add(idx[t.x]); list.Add(idx[t.y]); list.Add(idx[t.z]); }
            }

            mesh.Clear();
            mesh.indexFormat = outV.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(outV);
            mesh.SetNormals(outN);
            mesh.SetUVs(0, outUV0);
            if (hasUV1) mesh.SetUVs(1, outUV1);
            if (hasColor) mesh.SetColors(outC);
            mesh.subMeshCount = subMeshCount;
            for (int s = 0; s < subMeshCount; s++) mesh.SetTriangles(subs[s], s, false);
            mesh.RecalculateBounds();
            if (outV.Count > 0) mesh.RecalculateTangents();
        }

        static bool Same(in Attr a, in Attr b) =>
            a.normal.x == b.normal.x && a.normal.y == b.normal.y && a.normal.z == b.normal.z &&
            a.uv0.x == b.uv0.x && a.uv0.y == b.uv0.y && a.uv1.x == b.uv1.x && a.uv1.y == b.uv1.y &&
            a.color.r == b.color.r && a.color.g == b.color.g && a.color.b == b.color.b && a.color.a == b.color.a;

        // ---------------------------------------------------------------- geometry helpers

        /// <summary>
        /// Normalize without Unity's 1e-5 cutoff: Vector3.normalized returns zero below that length, which is
        /// every face normal of a centimetre-scale import (whole model ~0.01 units) and turned results black.
        /// </summary>
        public static Vector3 Unit(Vector3 v)
        {
            float m = v.magnitude;
            return m > 1e-30f ? v / m : Vector3.zero;
        }

        public static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        public static int EdgeA(long k) => (int)(k >> 32);
        public static int EdgeB(long k) => (int)(k & 0xffffffff);

        /// <summary>
        /// Use for every long edge/half-edge key: long's own hash is lo ^ hi, i.e. a ^ b, which collides
        /// massively on mesh indices and turns dictionaries quadratic.
        /// </summary>
        public static readonly IEqualityComparer<long> Keys = new KeyComparer();

        sealed class KeyComparer : IEqualityComparer<long>
        {
            public bool Equals(long a, long b) => a == b;
            public int GetHashCode(long k)
            {
                ulong x = (ulong)k;
                x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33;
                return (int)x ^ (int)(x >> 32);
            }
        }

        public Vector3 FaceNormal(Face f)
        {
            // Newell's method: robust for n-gons.
            Vector3 n = Vector3.zero;
            for (int i = 0; i < f.Count; i++)
            {
                Vector3 c = pos[f.v[i]], nx = pos[f.v[(i + 1) % f.Count]];
                n.x += (c.y - nx.y) * (c.z + nx.z);
                n.y += (c.z - nx.z) * (c.x + nx.x);
                n.z += (c.x - nx.x) * (c.y + nx.y);
            }
            return n; // length = 2 * area
        }

        public float FaceArea(Face f) => FaceNormal(f).magnitude * 0.5f;

        public Vector3 FaceCenter(Face f)
        {
            Vector3 c = Vector3.zero;
            foreach (var i in f.v) c += pos[i];
            return c / f.Count;
        }

        /// <summary>Edge → faces that use it (index into <see cref="faces"/>).</summary>
        public Dictionary<long, List<int>> EdgeFaces()
        {
            var map = new Dictionary<long, List<int>>(Keys);
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                for (int i = 0; i < f.Count; i++)
                {
                    long k = EdgeKey(f.v[i], f.v[(i + 1) % f.Count]);
                    if (!map.TryGetValue(k, out var l)) map[k] = l = new List<int>(2);
                    l.Add(fi);
                }
            }
            return map;
        }

        public HashSet<int>[] Neighbors()
        {
            var n = new HashSet<int>[pos.Count];
            for (int i = 0; i < n.Length; i++) n[i] = new HashSet<int>();
            foreach (var f in faces)
                for (int i = 0; i < f.Count; i++)
                {
                    int a = f.v[i], b = f.v[(i + 1) % f.Count];
                    n[a].Add(b); n[b].Add(a);
                }
            return n;
        }

        /// <summary>Smooth area-weighted normal per position.</summary>
        public Vector3[] VertexNormals()
        {
            var n = new Vector3[pos.Count];
            foreach (var f in faces)
            {
                var fn = FaceNormal(f);
                foreach (var i in f.v) n[i] += fn;
            }
            for (int i = 0; i < n.Length; i++) n[i] = n[i] != Vector3.zero ? Unit(n[i]) : Vector3.up;
            return n;
        }

        /// <summary>First corner attribute seen for each position (for UV lookups etc).</summary>
        public Attr[] PositionAttrs()
        {
            var r = new Attr[pos.Count];
            var seen = new bool[pos.Count];
            foreach (var f in faces)
                for (int c = 0; c < f.Count; c++)
                    if (!seen[f.v[c]]) { seen[f.v[c]] = true; r[f.v[c]] = f.a[c]; }
            return r;
        }

        public Bounds GetBounds()
        {
            if (pos.Count == 0) return new Bounds();
            var b = new Bounds(pos[0], Vector3.zero);
            foreach (var p in pos) b.Encapsulate(p);
            return b;
        }

        /// <summary>
        /// Per-corner normals, smoothed across edges whose dihedral angle is below <paramref name="angleDeg"/>
        /// (Blender "Smooth by Angle"). <paramref name="weight"/> returns a face-corner weight (default: corner angle).
        /// </summary>
        public Vector3[][] CornerNormals(float angleDeg, Func<int, int, float> weight)
        {
            int fc = faces.Count;
            var fn = new Vector3[fc];
            For(fc, i => fn[i] = Unit(FaceNormal(faces[i])));
            float cosLimit = Mathf.Cos(Mathf.Clamp(angleDeg, 0f, 180f) * Mathf.Deg2Rad);
            weight ??= CornerAngle;

            // Corner id = offset[face] + c.
            var offset = new int[fc + 1];
            for (int i = 0; i < fc; i++) offset[i + 1] = offset[i] + faces[i].Count;
            int nc = offset[fc];

            // Corners grouped by their position (CSR), so the twin of directed edge (a,b) is found by scanning
            // the few corners at b for one whose next position is a. Arrays instead of a hash: much faster.
            var cf = new int[nc]; var cc = new int[nc];
            var start = new int[pos.Count + 1];
            for (int fi = 0; fi < fc; fi++)
                for (int c = 0; c < faces[fi].Count; c++) { cf[offset[fi] + c] = fi; cc[offset[fi] + c] = c; start[faces[fi].v[c] + 1]++; }
            for (int i = 0; i < pos.Count; i++) start[i + 1] += start[i];
            var fill = new int[pos.Count];
            var byPos = new int[nc];
            for (int id = 0; id < nc; id++) { int p = faces[cf[id]].v[cc[id]]; byPos[start[p] + fill[p]++] = id; }

            // Smooth twin of each corner's outgoing edge (-1 = border or sharp), and its weighted face normal.
            var twin = new int[nc];
            var wn = new Vector3[nc];
            For(fc, fi =>
            {
                var f = faces[fi];
                for (int c = 0; c < f.Count; c++)
                {
                    int id = offset[fi] + c, a = f.v[c], b = f.v[(c + 1) % f.Count];
                    twin[id] = -1;
                    wn[id] = fn[fi] * weight(fi, c);
                    for (int k = start[b]; k < start[b + 1]; k++)
                    {
                        int o = byPos[k], gi = cf[o], gc = cc[o];
                        var g = faces[gi];
                        if (g.v[(gc + 1) % g.Count] != a) continue;
                        if (Vector3.Dot(fn[fi], fn[gi]) >= cosLimit) twin[id] = o;
                        break;
                    }
                }
            });

            // Union-find over corners that share a position across a smooth edge.
            var parent = new int[nc];
            for (int i = 0; i < nc; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
            for (int id = 0; id < nc; id++)
            {
                int o = twin[id];
                if (o < 0) continue;
                int fi = cf[id], c = cc[id], gi = cf[o], gc = cc[o];
                // In face fi: a at c, b at c+1. In neighbour: b at gc, a at gc+1.
                Union(id, offset[gi] + (gc + 1) % faces[gi].Count);
                Union(offset[fi] + (c + 1) % faces[fi].Count, o);
            }

            var root = new int[nc];
            var sum = new Vector3[nc];
            for (int id = 0; id < nc; id++) { root[id] = Find(id); sum[root[id]] += wn[id]; }

            var r = new Vector3[fc][];
            for (int fi = 0; fi < fc; fi++)
            {
                var res = r[fi] = new Vector3[faces[fi].Count];
                for (int c = 0; c < res.Length; c++)
                {
                    var n = sum[root[offset[fi] + c]];
                    res[c] = n.sqrMagnitude > 1e-12f ? Unit(n) : fn[fi];
                }
            }
            return r;
        }

        /// <summary>
        /// Parallel for over large ranges. Only for bodies that don't allocate: the editor's Boehm GC serializes
        /// allocation across threads, which made allocating loops slower in parallel than on one thread.
        /// </summary>
        static void For(int n, Action<int> body)
        {
            if (n < 4096) { for (int i = 0; i < n; i++) body(i); return; }
            System.Threading.Tasks.Parallel.For(0, n, body);
        }

        public float CornerAngle(int fi, int c)
        {
            var f = faces[fi];
            Vector3 p = pos[f.v[c]];
            Vector3 a = pos[f.v[(c + f.Count - 1) % f.Count]] - p, b = pos[f.v[(c + 1) % f.Count]] - p;
            return Vector3.Angle(a, b) * Mathf.Deg2Rad;
        }

        // ---------------------------------------------------------------- topology edits

        /// <summary>Merge positions closer than <paramref name="dist"/> (spatial hash), drop degenerate faces.</summary>
        public void Weld(float dist)
        {
            if (pos.Count == 0) return;
            float cell = Mathf.Max(dist, 1e-6f);
            float d2 = dist * dist;
            var grid = new Dictionary<Vector3Int, List<int>>();
            var target = new int[pos.Count];
            for (int i = 0; i < pos.Count; i++)
            {
                var p = pos[i];
                var k = Vector3Int.FloorToInt(p / cell);
                target[i] = i;
                for (int x = -1; x <= 1 && target[i] == i; x++)
                    for (int y = -1; y <= 1 && target[i] == i; y++)
                        for (int z = -1; z <= 1 && target[i] == i; z++)
                            if (grid.TryGetValue(k + new Vector3Int(x, y, z), out var l))
                                foreach (var j in l)
                                    if ((pos[j] - p).sqrMagnitude <= d2) { target[i] = j; break; }
                if (target[i] == i)
                {
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(i);
                }
            }
            Remap(target);
        }

        /// <summary>Remap position indices (target[i] = new owner of i), compact, remove degenerate corners/faces.</summary>
        public void Remap(int[] target)
        {
            var used = new int[pos.Count];
            for (int i = 0; i < used.Length; i++) used[i] = -1;
            var newPos = new List<Vector3>();
            var newFaces = new List<Face>();
            foreach (var f in faces)
            {
                var v = new List<int>(f.Count);
                var a = new List<Attr>(f.Count);
                for (int c = 0; c < f.Count; c++)
                {
                    int t = target[f.v[c]];
                    if (v.Count > 0 && v[v.Count - 1] == t) continue;
                    v.Add(t); a.Add(f.a[c]);
                }
                while (v.Count > 1 && v[0] == v[v.Count - 1]) { v.RemoveAt(v.Count - 1); a.RemoveAt(a.Count - 1); }
                if (v.Count < 3) continue;
                if (new HashSet<int>(v).Count != v.Count) continue; // pinched: drop
                for (int c = 0; c < v.Count; c++)
                {
                    if (used[v[c]] < 0) { used[v[c]] = newPos.Count; newPos.Add(pos[v[c]]); }
                    v[c] = used[v[c]];
                }
                newFaces.Add(new Face(v.ToArray(), a.ToArray(), f.sub));
            }
            pos = newPos;
            faces = newFaces;
        }

        /// <summary>Remove positions no face references.</summary>
        public void Compact()
        {
            var t = new int[pos.Count];
            for (int i = 0; i < t.Length; i++) t[i] = i;
            Remap(t);
        }

        /// <summary>Split every face into triangles.</summary>
        public void TriangulateAll()
        {
            var nf = new List<Face>(faces.Count * 2);
            foreach (var f in faces)
            {
                if (f.Count == 3) { nf.Add(f); continue; }
                foreach (var t in Triangulate(f))
                    nf.Add(new Face(new[] { f.v[t.x], f.v[t.y], f.v[t.z] }, new[] { f.a[t.x], f.a[t.y], f.a[t.z] }, f.sub));
            }
            faces = nf;
        }

        /// <summary>Triangle corner indices for a polygon (fan for convex, ear clipping otherwise).</summary>
        public List<Vector3Int> Triangulate(Face f)
        {
            var r = new List<Vector3Int>(f.Count - 2);
            if (f.Count == 3) { r.Add(new Vector3Int(0, 1, 2)); return r; }
            var n = FaceNormal(f);
            if (n.sqrMagnitude < 1e-20f) { for (int i = 1; i + 1 < f.Count; i++) r.Add(new Vector3Int(0, i, i + 1)); return r; }

            var idx = new List<int>(f.Count);
            for (int i = 0; i < f.Count; i++) idx.Add(i);
            int guard = f.Count * f.Count;
            while (idx.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    Vector3 a = pos[f.v[ia]], b = pos[f.v[ib]], c = pos[f.v[ic]];
                    if (Vector3.Dot(Vector3.Cross(b - a, c - b), n) <= 0) continue; // reflex
                    bool inside = false;
                    foreach (var j in idx)
                    {
                        if (j == ia || j == ib || j == ic) continue;
                        if (PointInTri(pos[f.v[j]], a, b, c, n)) { inside = true; break; }
                    }
                    if (inside) continue;
                    r.Add(new Vector3Int(ia, ib, ic));
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) r.Add(new Vector3Int(idx[0], idx[1], idx[2]));
            else for (int i = 1; i + 1 < idx.Count; i++) r.Add(new Vector3Int(idx[0], idx[i], idx[i + 1]));
            return r;
        }

        static bool PointInTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 n) =>
            Vector3.Dot(Vector3.Cross(b - a, p - a), n) >= 0 &&
            Vector3.Dot(Vector3.Cross(c - b, p - b), n) >= 0 &&
            Vector3.Dot(Vector3.Cross(a - c, p - c), n) >= 0;

        /// <summary>
        /// Blender "Tris to Quads": merge coplanar triangle pairs whose shared edge is the longest of both
        /// and whose corner attributes match across it.
        /// </summary>
        public void JoinTrianglesToQuads(float maxAngleDeg = 1f)
        {
            float cosLimit = Mathf.Cos(maxAngleDeg * Mathf.Deg2Rad);
            var edgeFaces = EdgeFaces();
            var merged = new bool[faces.Count];
            var result = new List<Face>(faces.Count);

            int LongestEdge(Face f)
            {
                int best = 0; float bl = -1;
                for (int i = 0; i < 3; i++)
                {
                    float l = (pos[f.v[i]] - pos[f.v[(i + 1) % 3]]).sqrMagnitude;
                    if (l > bl) { bl = l; best = i; }
                }
                return best;
            }

            for (int fi = 0; fi < faces.Count; fi++)
            {
                if (merged[fi]) continue;
                var f = faces[fi];
                if (f.Count != 3) { result.Add(f); continue; }
                int e = LongestEdge(f);
                int a = f.v[e], b = f.v[(e + 1) % 3];
                var list = edgeFaces[EdgeKey(a, b)];
                Face quad = null;
                if (list.Count == 2)
                {
                    int gi = list[0] == fi ? list[1] : list[0];
                    var g = faces[gi];
                    if (!merged[gi] && g.Count == 3 && g.sub == f.sub && gi > fi)
                    {
                        int ge = LongestEdge(g);
                        // g must have the same edge reversed as its longest
                        if (g.v[ge] == b && g.v[(ge + 1) % 3] == a &&
                            Vector3.Dot(Unit(FaceNormal(f)), Unit(FaceNormal(g))) >= cosLimit &&
                            f.a[e].Equals(g.a[(ge + 1) % 3]) && f.a[(e + 1) % 3].Equals(g.a[ge]))
                        {
                            int c = (e + 2) % 3, gc = (ge + 2) % 3;
                            // quad: a, (g opposite), b, (f opposite)
                            var q = new Face(new[] { f.v[e], g.v[gc], f.v[(e + 1) % 3], f.v[c] },
                                             new[] { f.a[e], g.a[gc], f.a[(e + 1) % 3], f.a[c] }, f.sub);
                            if (IsConvex(q)) { quad = q; merged[gi] = true; }
                        }
                    }
                }
                result.Add(quad ?? f);
            }
            faces = result;
        }

        bool IsConvex(Face f)
        {
            var n = FaceNormal(f);
            for (int i = 0; i < f.Count; i++)
            {
                Vector3 a = pos[f.v[i]], b = pos[f.v[(i + 1) % f.Count]], c = pos[f.v[(i + 2) % f.Count]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - b), n) <= 0) return false;
            }
            return true;
        }

        /// <summary>Keep the half-space dot(n,p) >= d, cutting faces on the plane (shared cut points).</summary>
        public void ClipPlane(Vector3 n, float d)
        {
            var cut = new Dictionary<long, int>(Keys);
            var newFaces = new List<Face>(faces.Count);
            float Side(int i) => Vector3.Dot(n, pos[i]) - d;
            foreach (var f in faces)
            {
                bool allIn = true, allOut = true;
                foreach (var i in f.v) { if (Side(i) < 0) allIn = false; else allOut = false; }
                if (allIn) { newFaces.Add(f); continue; }
                if (allOut) continue;
                var v = new List<int>(); var a = new List<Attr>();
                for (int c = 0; c < f.Count; c++)
                {
                    int c2 = (c + 1) % f.Count;
                    int i = f.v[c], j = f.v[c2];
                    float si = Side(i), sj = Side(j);
                    if (si >= 0) { v.Add(i); a.Add(f.a[c]); }
                    if ((si >= 0) != (sj >= 0))
                    {
                        float t = si / (si - sj);
                        long k = EdgeKey(i, j);
                        if (!cut.TryGetValue(k, out int ni))
                        {
                            ni = pos.Count; pos.Add(Vector3.LerpUnclamped(pos[i], pos[j], t)); cut[k] = ni;
                        }
                        v.Add(ni); a.Add(Attr.Lerp(f.a[c], f.a[c2], t));
                    }
                }
                if (v.Count >= 3) newFaces.Add(new Face(v.ToArray(), a.ToArray(), f.sub));
            }
            faces = newFaces;
            Compact();
        }
    }
}
