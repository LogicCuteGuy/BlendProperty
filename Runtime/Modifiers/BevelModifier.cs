using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Edge bevel limited by angle. Each face is inset away from its beveled edges, beveled edges become
    /// strips with a rounded profile, and vertex holes get an n-gon cap.
    /// ponytail: vertex caps are flat n-gons sorted by angle (Blender builds a rounded patch); partial bevels on
    /// high-valence saddle vertices can leave slivers. Upgrade path: port Blender's BoundVert/VMesh builder.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Bevel.png")]
    [AddComponentMenu("Blend Modifier/Generate/Bevel")]
    public class BevelModifier : BlendModifier
    {
        public enum Affect { Edges, Vertices }
        public enum WidthType { Offset, Width, Depth, Percent }
        public enum LimitMethod { None, Angle }

        [Tooltip("Edges: round off edges. Vertices: cut the corners off.")]
        public Affect affect = Affect.Edges;
        [Tooltip("Offset: distance from the edge along each face. Width: width of the new face. Depth: how deep the corner is cut. Percent: % of adjacent edge length.")]
        public WidthType widthType = WidthType.Offset;
        [Min(0), Tooltip("Metres (Percent: 0 to 100).")]
        public float width = 0.02f;
        [Range(1, 16)] public int segments = 1;
        [Range(0, 1), Tooltip("0.5 = round, 0 = flat chamfer pulled in, 1 = square corner.")]
        public float profile = 0.5f;
        [Tooltip("None: bevel every edge. Angle: only edges whose faces meet at more than Angle.")]
        public LimitMethod limitMethod = LimitMethod.Angle;
        [Range(0, 180), Tooltip("Only edges whose faces meet at an angle above this are beveled.")]
        public float angleLimit = 30f;
        [Tooltip("Keep each bevel from overlapping its neighbours (limited per edge).")]
        public bool clampOverlap = true;
        [Tooltip("Keep the original faces flat-shaded and let only the bevel be smooth (custom normals).")]
        public bool hardenNormals;
        [Tooltip("Material slot for the new faces (-1 = the adjacent face's).")]
        public int material = -1;

        public override void Apply(MeshData mesh)
        {
            float w = widthType == WidthType.Percent ? width : MeshUnits(width);
            if (affect == Affect.Vertices) BevelVertices(mesh, w, widthType, limitMethod == LimitMethod.None ? 0f : angleLimit, clampOverlap, material);
            else Bevel(mesh, w, segments, profile, angleLimit, clampOverlap, widthType, limitMethod, hardenNormals, material);
        }

        public static void Bevel(MeshData m, float width, int segments, float profile, float angleLimit, bool clampOverlap,
            WidthType widthType = WidthType.Offset, LimitMethod limitMethod = LimitMethod.Angle, bool hardenNormals = false, int material = -1)
        {
            var P = m.pos;
            var faces = m.faces;
            var fn = new Vector3[faces.Count];
            for (int i = 0; i < fn.Length; i++) fn[i] = MeshData.Unit(m.FaceNormal(faces[i]));
            var edgeFaces = m.EdgeFaces();
            var vertexNormal = m.VertexNormals();
            float cosLimit = Mathf.Cos(angleLimit * Mathf.Deg2Rad);

            var bevel = new HashSet<long>(MeshData.Keys);
            foreach (var kv in edgeFaces)
                if (kv.Value.Count == 2 && (limitMethod == LimitMethod.None || Vector3.Dot(fn[kv.Value[0]], fn[kv.Value[1]]) < cosLimit - 1e-6f))
                    bevel.Add(kv.Key);
            if (bevel.Count == 0 || width <= 0) return;

            var bevelVert = new HashSet<int>();
            foreach (var k in bevel) { bevelVert.Add(MeshData.EdgeA(k)); bevelVert.Add(MeshData.EdgeB(k)); }

            // per-edge offset from the chosen width type, clamped by that edge's own neighbourhood
            // (it used to clamp the whole mesh to its single shortest edge)
            var shortest = ShortestEdge(m, edgeFaces);
            var avgLen = AverageEdge(m, edgeFaces);
            var wE = new Dictionary<long, float>(MeshData.Keys);
            foreach (var k in bevel)
            {
                var fs = edgeFaces[k];
                int a = MeshData.EdgeA(k), b = MeshData.EdgeB(k);
                float gamma = Mathf.Acos(Mathf.Clamp(Vector3.Dot(fn[fs[0]], fn[fs[1]]), -1f, 1f)); // 0 = flat
                float d = widthType switch
                {
                    WidthType.Width => width / Mathf.Max(2f * Mathf.Cos(gamma * 0.5f), 0.1f),
                    WidthType.Depth => width / Mathf.Max(Mathf.Sin(gamma * 0.5f), 0.05f),
                    WidthType.Percent => width * 0.01f * 0.5f * (avgLen[a] + avgLen[b]),
                    _ => width,
                };
                if (clampOverlap) d = Mathf.Min(d, 0.45f * Mathf.Min(shortest[a], shortest[b]));
                wE[k] = d;
            }
            float W(int a, int b) => wE.TryGetValue(MeshData.EdgeKey(a, b), out var d) ? d : 0f;

            // 1) slide distances on non-beveled edges, averaged over the faces that request them
            var slideSum = new Dictionary<(int v, int other), float>();
            var slideCnt = new Dictionary<(int v, int other), int>();
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                for (int c = 0; c < f.Count; c++)
                {
                    int v = f.v[c], vp = f.v[(c + f.Count - 1) % f.Count], vn = f.v[(c + 1) % f.Count];
                    bool bp = bevel.Contains(MeshData.EdgeKey(vp, v)), bn = bevel.Contains(MeshData.EdgeKey(v, vn));
                    if (bp == bn) continue;
                    float sin = Mathf.Max(0.05f, Mathf.Sin(m.CornerAngle(fi, c)));
                    var key = bp ? (v, vn) : (v, vp);
                    float w = bp ? W(vp, v) : W(v, vn);
                    slideSum[key] = (slideSum.TryGetValue(key, out var s) ? s : 0) + w / sin;
                    slideCnt[key] = (slideCnt.TryGetValue(key, out var n) ? n : 0) + 1;
                }
            }
            var slideIdx = new Dictionary<(int, int), int>();
            int Slide(int v, int other)
            {
                if (slideIdx.TryGetValue((v, other), out int id)) return id;
                if (!slideCnt.TryGetValue((v, other), out int cnt)) return -1;
                var dir = P[other] - P[v];
                float d = Mathf.Min(slideSum[(v, other)] / cnt, dir.magnitude * 0.45f);
                id = P.Count; P.Add(P[v] + MeshData.Unit(dir) * d);
                slideIdx[(v, other)] = id;
                return id;
            }

            // 2) corner replacements
            var corner = new Dictionary<(int face, int v), List<int>>();
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                for (int c = 0; c < f.Count; c++)
                {
                    int v = f.v[c], vp = f.v[(c + f.Count - 1) % f.Count], vn = f.v[(c + 1) % f.Count];
                    var list = new List<int>(2);
                    if (bevelVert.Contains(v))
                    {
                        bool bp = bevel.Contains(MeshData.EdgeKey(vp, v)), bn = bevel.Contains(MeshData.EdgeKey(v, vn));
                        if (bp && bn)
                        {
                            // where both edges' offset lines meet: W(prev edge) from one, W(next edge) from the other
                            Vector3 up = MeshData.Unit(P[vp] - P[v]), un = MeshData.Unit(P[vn] - P[v]);
                            float sin = Mathf.Max(0.05f, Mathf.Sin(m.CornerAngle(fi, c)));
                            list.Add(P.Count); P.Add(P[v] + up * (W(v, vn) / sin) + un * (W(vp, v) / sin));
                        }
                        else if (bp) list.Add(Slide(v, vn));
                        else if (bn) list.Add(Slide(v, vp));
                        else
                        {
                            int sp = Slide(v, vp), sn = Slide(v, vn);
                            if (sp >= 0) list.Add(sp);
                            if (sp < 0 || sn < 0) list.Add(v);
                            if (sn >= 0) list.Add(sn);
                        }
                    }
                    else list.Add(v);
                    corner[(fi, v)] = list;
                }
            }

            var result = new List<Face>(faces.Count * 2);
            var ring = new Dictionary<int, List<int>>();
            var ringAttr = new Dictionary<int, Attr>();
            var ringSub = new Dictionary<int, int>();
            void AddRing(int v, int p, Attr a, int sub)
            {
                if (!ring.TryGetValue(v, out var l)) { ring[v] = l = new List<int>(); ringAttr[v] = a; ringSub[v] = sub; }
                if (!l.Contains(p)) l.Add(p);
            }

            int Mat(int sub) => material >= 0 ? material : sub;
            // 3) inset faces
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var f = faces[fi];
                var v = new List<int>(); var a = new List<Attr>();
                for (int c = 0; c < f.Count; c++)
                    foreach (var p in corner[(fi, f.v[c])])
                    {
                        if (v.Count > 0 && v[v.Count - 1] == p) continue;
                        v.Add(p); a.Add(f.a[c]);
                        if (bevelVert.Contains(f.v[c])) AddRing(f.v[c], p, f.a[c], f.sub);
                    }
                if (v.Count >= 3) result.Add(new Face(v.ToArray(), a.ToArray(), f.sub));
            }
            int insetCount = result.Count;

            // 4) strips along beveled edges
            foreach (var k in bevel)
            {
                var fs = edgeFaces[k];
                int f1 = fs[0], f2 = fs[1];
                int ea = MeshData.EdgeA(k), eb = MeshData.EdgeB(k);
                var ref_ = fn[f1] + fn[f2];
                var colA = Column(f1, f2, ea);
                var colB = Column(f1, f2, eb);
                for (int s = 0; s < segments; s++)
                {
                    var face = new Face(new[] { colA.p[s], colA.p[s + 1], colB.p[s + 1], colB.p[s] },
                                        new[] { colA.a[s], colA.a[s + 1], colB.a[s + 1], colB.a[s] }, Mat(faces[f1].sub));
                    if (Vector3.Dot(m.FaceNormal(face), ref_) < 0) face.Reverse();
                    result.Add(face);
                }
            }

            (int[] p, Attr[] a) Column(int f1, int f2, int v)
            {
                int p0 = corner[(f1, v)][0], p1 = corner[(f2, v)][0];
                Attr a0 = AttrAt(faces[f1], v), a1 = AttrAt(faces[f2], v);
                var ps = new int[segments + 1]; var at = new Attr[segments + 1];
                Vector3 A = P[p0], B = P[p1], mid = (A + B) * 0.5f;
                Vector3 ctrl = Vector3.LerpUnclamped(mid, P[v], profile * 2f);
                for (int s = 0; s <= segments; s++)
                {
                    float t = (float)s / segments;
                    at[s] = Attr.Lerp(a0, a1, t);
                    if (s == 0) ps[s] = p0;
                    else if (s == segments) ps[s] = p1;
                    else
                    {
                        ps[s] = P.Count;
                        P.Add((1 - t) * (1 - t) * A + 2 * t * (1 - t) * ctrl + t * t * B);
                        AddRing(v, ps[s], at[s], faces[f1].sub);
                    }
                }
                return (ps, at);
            }

            // 5) vertex caps
            foreach (var kv in ring)
            {
                int v = kv.Key;
                var pts = kv.Value;
                if (pts.Count < 3 || pts.Contains(v)) continue;
                Vector3 n = vertexNormal[v], c = Vector3.zero;
                foreach (var p in pts) c += P[p];
                c /= pts.Count;
                var t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                var t2 = Vector3.Cross(n, t1);
                pts.Sort((x, y) => Ang(P[x]).CompareTo(Ang(P[y])));
                float Ang(Vector3 p) { var d = p - c; return Mathf.Atan2(Vector3.Dot(d, t2), Vector3.Dot(d, t1)); }
                var attrs = new Attr[pts.Count];
                for (int i = 0; i < attrs.Length; i++) attrs[i] = ringAttr[v];
                var cap = new Face(pts.ToArray(), attrs, Mat(ringSub[v]));
                if (Vector3.Dot(m.FaceNormal(cap), n) < 0) cap.Reverse();

                // A non-planar cap (segments > 1, or 4+ beveled edges) would shade with one averaged normal
                // live but as bent triangles once applied: fan it around a center point instead.
                var cn = MeshData.Unit(m.FaceNormal(cap));
                float dev = 0, size = 0;
                foreach (var p in cap.v) { dev = Mathf.Max(dev, Mathf.Abs(Vector3.Dot(P[p] - c, cn))); size = Mathf.Max(size, (P[p] - c).magnitude); }
                if (dev <= 1e-4f * size) { result.Add(cap); continue; } // relative, so cm-scale meshes work too
                // ponytail: center on the profile's mid-height, not a true Blender corner patch grid.
                int center = P.Count;
                P.Add(Vector3.Lerp(c, P[v], segments > 1 ? profile : 0f));
                for (int i = 0; i < cap.Count; i++)
                    result.Add(new Face(new[] { center, cap.v[i], cap.v[(i + 1) % cap.Count] },
                                        new[] { ringAttr[v], cap.a[i], cap.a[(i + 1) % cap.Count] }, Mat(ringSub[v])));
            }

            m.faces = result;
            if (material >= 0) m.subMeshCount = Mathf.Max(m.subMeshCount, material + 1);
            if (hardenNormals) Harden(m, insetCount);
            m.Compact();

            static Attr AttrAt(Face f, int v) => f.a[System.Array.IndexOf(f.v, v)];
        }

        /// <summary>
        /// Blender Harden Normals: original faces keep their flat normal; bevel faces take the normal of the
        /// original face they touch, so only the bevel itself shades round.
        /// </summary>
        static void Harden(MeshData m, int insetCount)
        {
            var flat = new Vector3[m.pos.Count];
            for (int fi = 0; fi < insetCount; fi++)
            {
                var n = MeshData.Unit(m.FaceNormal(m.faces[fi]));
                foreach (var v in m.faces[fi].v) flat[v] += n;
            }
            var smooth = m.VertexNormals();
            for (int fi = 0; fi < m.faces.Count; fi++)
            {
                var f = m.faces[fi];
                var fnrm = MeshData.Unit(m.FaceNormal(f));
                for (int c = 0; c < f.Count; c++)
                    f.a[c].normal = fi < insetCount ? fnrm : flat[f.v[c]] != Vector3.zero ? MeshData.Unit(flat[f.v[c]]) : smooth[f.v[c]];
            }
            m.customNormals = true;
        }

        static float[] ShortestEdge(MeshData m, Dictionary<long, List<int>> edgeFaces)
        {
            var r = new float[m.pos.Count];
            for (int i = 0; i < r.Length; i++) r[i] = float.MaxValue;
            foreach (var k in edgeFaces.Keys)
            {
                int a = MeshData.EdgeA(k), b = MeshData.EdgeB(k);
                float l = (m.pos[a] - m.pos[b]).magnitude;
                r[a] = Mathf.Min(r[a], l); r[b] = Mathf.Min(r[b], l);
            }
            return r;
        }

        static float[] AverageEdge(MeshData m, Dictionary<long, List<int>> edgeFaces)
        {
            var sum = new float[m.pos.Count]; var cnt = new int[m.pos.Count];
            foreach (var k in edgeFaces.Keys)
            {
                int a = MeshData.EdgeA(k), b = MeshData.EdgeB(k);
                float l = (m.pos[a] - m.pos[b]).magnitude;
                sum[a] += l; sum[b] += l; cnt[a]++; cnt[b]++;
            }
            for (int i = 0; i < sum.Length; i++) sum[i] = cnt[i] > 0 ? sum[i] / cnt[i] : 0f;
            return sum;
        }

        /// <summary>
        /// Blender Bevel, Affect: Vertices. Every selected corner is cut off by a flat face
        /// <paramref name="width"/> along each of its edges.
        /// ponytail: one segment only (Blender also rounds vertex bevels with Segments).
        /// </summary>
        public static void BevelVertices(MeshData m, float width, WidthType widthType, float angleLimit, bool clampOverlap, int material = -1)
        {
            if (width <= 0) return;
            var P = m.pos;
            var edgeFaces = m.EdgeFaces();
            var shortest = ShortestEdge(m, edgeFaces);
            var avgLen = AverageEdge(m, edgeFaces);
            var fn = new Vector3[m.faces.Count];
            for (int i = 0; i < fn.Length; i++) fn[i] = MeshData.Unit(m.FaceNormal(m.faces[i]));

            // corners to cut: vertices on an edge sharper than the limit (limit 0 = every vertex)
            float cosLimit = Mathf.Cos(angleLimit * Mathf.Deg2Rad);
            var cut = new HashSet<int>();
            foreach (var kv in edgeFaces)
                if (angleLimit <= 0 || (kv.Value.Count == 2 && Vector3.Dot(fn[kv.Value[0]], fn[kv.Value[1]]) < cosLimit - 1e-6f))
                { cut.Add(MeshData.EdgeA(kv.Key)); cut.Add(MeshData.EdgeB(kv.Key)); }
            if (cut.Count == 0) return;

            var point = new Dictionary<(int v, int other), int>();
            int On(int v, int other)
            {
                if (point.TryGetValue((v, other), out int id)) return id;
                var dir = P[other] - P[v];
                float d = widthType == WidthType.Percent ? width * 0.01f * avgLen[v] : width;
                d = Mathf.Min(d, clampOverlap ? 0.45f * shortest[v] : dir.magnitude * 0.999f);
                id = P.Count; P.Add(P[v] + MeshData.Unit(dir) * d);
                point[(v, other)] = id;
                return id;
            }

            var result = new List<Face>(m.faces.Count * 2);
            var capNext = new Dictionary<int, Dictionary<int, (int to, Attr at)>>();
            var capSub = new Dictionary<int, int>();
            foreach (var f in m.faces)
            {
                var v = new List<int>(); var a = new List<Attr>();
                for (int c = 0; c < f.Count; c++)
                {
                    int x = f.v[c];
                    if (!cut.Contains(x)) { v.Add(x); a.Add(f.a[c]); continue; }
                    int cp = (c + f.Count - 1) % f.Count, cn = (c + 1) % f.Count;
                    int pPrev = On(x, f.v[cp]), pNext = On(x, f.v[cn]);
                    var aPrev = Attr.Lerp(f.a[c], f.a[cp], (P[pPrev] - P[x]).magnitude / Mathf.Max((P[f.v[cp]] - P[x]).magnitude, 1e-30f));
                    var aNext = Attr.Lerp(f.a[c], f.a[cn], (P[pNext] - P[x]).magnitude / Mathf.Max((P[f.v[cn]] - P[x]).magnitude, 1e-30f));
                    v.Add(pPrev); a.Add(aPrev); v.Add(pNext); a.Add(aNext);
                    // the cap runs the other way along this cut
                    if (!capNext.TryGetValue(x, out var chain)) capNext[x] = chain = new Dictionary<int, (int, Attr)>();
                    chain[pNext] = (pPrev, aNext);
                    capSub[x] = f.sub;
                }
                result.Add(new Face(v.ToArray(), a.ToArray(), f.sub));
            }

            foreach (var kv in capNext)
            {
                var chain = kv.Value;
                // start where nothing leads in (open corner on a boundary), else anywhere on the loop
                var into = new HashSet<int>();
                foreach (var e in chain.Values) into.Add(e.to);
                int start = -1;
                foreach (var k in chain.Keys) if (!into.Contains(k)) { start = k; break; }
                if (start < 0) foreach (var k in chain.Keys) { start = k; break; }
                var v = new List<int>(); var a = new List<Attr>();
                int cur = start;
                while (v.Count <= chain.Count)
                {
                    v.Add(cur);
                    if (!chain.TryGetValue(cur, out var nx)) { a.Add(a.Count > 0 ? a[a.Count - 1] : default); break; }
                    a.Add(nx.at);
                    cur = nx.to;
                    if (cur == start) break;
                }
                if (v.Count >= 3) result.Add(new Face(v.ToArray(), a.ToArray(), material >= 0 ? material : capSub[kv.Key]));
            }
            m.faces = result;
            m.subMeshCount = Mathf.Max(m.subMeshCount, material + 1);
            m.Compact();
        }
    }
}
