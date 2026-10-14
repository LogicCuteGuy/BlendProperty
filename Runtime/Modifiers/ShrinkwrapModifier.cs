using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Shrinkwrap: snap vertices onto another object's surface.</summary>
    // ponytail: brute force over every target triangle (O(verts × tris)); add a BVH if targets get big.
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Shrinkwrap.png")]
    [AddComponentMenu("Blend Modifier/Deform/Shrinkwrap")]
    public class ShrinkwrapModifier : BlendModifier
    {
        public enum Mode { NearestSurfacePoint, Project, NearestVertex }
        public enum SnapMode { OnSurface, OutsideSurface, AboveSurface, Inside, Outside }
        public enum Cull { Off, Front, Back }

        public MeshFilter target;
        public Mode mode = Mode.NearestSurfacePoint;
        [Tooltip("On Surface: offset back toward the original position. Outside Surface: offset to the outside. " +
                 "Above Surface: offset along the target's smooth normal. Inside / Outside: only move vertices that are outside / inside the target.")]
        public SnapMode snapMode = SnapMode.OnSurface;
        [Tooltip("Distance kept from the surface.")]
        public float offset;
        [Range(0, 1)] public float strength = 1f;

        [Tooltip("Project: project along these local axes (none = along the vertex normal).")]
        public bool projectX, projectY, projectZ;
        [Tooltip("Project: allowed directions along the axis.")]
        public bool positive = true, negative = true;
        [Tooltip("Project: ignore hits on the front (or back) side of target faces.")]
        public Cull faceCull = Cull.Off;
        [Tooltip("Project: swap the culled side for hits in the negative direction.")]
        public bool invertCull;
        [Min(0), Tooltip("Project: maximum ray distance (0 = unlimited).")]
        public float limit;
        [Tooltip("Project: a second object to project onto as well.")]
        public MeshFilter auxiliaryTarget;

        public override IEnumerable<Object> Dependencies()
        {
            if (target) yield return target;
            if (auxiliaryTarget && mode == Mode.Project) yield return auxiliaryTarget;
        }

        class Target
        {
            public Vector3[] v, n;
            public int[] t;
        }

        Target Load(MeshFilter f)
        {
            if (!f || !f.sharedMesh || f.gameObject == gameObject) return null;
            var xf = ToLocal(f.transform);
            var nxf = xf.inverse.transpose;
            var m = f.sharedMesh;
            var r = new Target { v = m.vertices, n = m.normals, t = m.triangles };
            if (r.t.Length == 0) return null;
            for (int i = 0; i < r.v.Length; i++) r.v[i] = xf.MultiplyPoint3x4(r.v[i]);
            if (r.n.Length != r.v.Length) r.n = null;
            else for (int i = 0; i < r.n.Length; i++) r.n[i] = MeshData.Unit(nxf.MultiplyVector(r.n[i]));
            return r;
        }

        struct Hit { public Vector3 p, n, smooth; public float dist; }

        public override void Apply(MeshData mesh)
        {
            var main = Load(target);
            if (main == null) return;
            float offset = MeshUnits(this.offset);
            var aux = mode == Mode.Project ? Load(auxiliaryTarget) : null;
            var vn = mode == Mode.Project ? mesh.VertexNormals() : null;
            Vector3 axisDir = new Vector3(projectX ? 1 : 0, projectY ? 1 : 0, projectZ ? 1 : 0);
            bool useAxis = axisDir != Vector3.zero;
            axisDir = MeshData.Unit(axisDir);

            for (int i = 0; i < mesh.pos.Count; i++)
            {
                var p = mesh.pos[i];
                Hit h;
                if (mode == Mode.NearestVertex)
                {
                    // Blender: no snap mode here, the offset is along the line to the vertex
                    int best = 0; float bd = float.MaxValue;
                    for (int j = 0; j < main.v.Length; j++) { float d = (main.v[j] - p).sqrMagnitude; if (d < bd) { bd = d; best = j; } }
                    var dir = MeshData.Unit(p - main.v[best]);
                    mesh.pos[i] = Vector3.LerpUnclamped(p, main.v[best] + dir * offset, strength);
                    continue;
                }
                bool found;
                if (mode == Mode.Project)
                {
                    var d = useAxis ? axisDir : vn[i];
                    found = ProjectOnto(main, p, d, out h);
                    if (aux != null && ProjectOnto(aux, p, d, out var h2) && (!found || h2.dist < h.dist)) { h = h2; found = true; }
                }
                else found = Nearest(main, p, out h);
                if (!found) continue;

                var toP = p - h.p;
                bool outside = Vector3.Dot(toP, h.n) > 0;
                var line = toP != Vector3.zero ? MeshData.Unit(toP) : h.n;
                Vector3 goal;
                switch (snapMode)
                {
                    case SnapMode.OutsideSurface: goal = h.p + (Vector3.Dot(line, h.n) < 0 ? -line : line) * offset; break;
                    case SnapMode.AboveSurface: goal = h.p + h.smooth * offset; break;
                    case SnapMode.Inside:
                        if (!outside && toP.magnitude >= offset) continue; // already inside, deep enough
                        goal = h.p + (Vector3.Dot(line, h.n) > 0 ? -line : line) * offset;
                        break;
                    case SnapMode.Outside:
                        if (outside && toP.magnitude >= offset) continue;
                        goal = h.p + (Vector3.Dot(line, h.n) < 0 ? -line : line) * offset;
                        break;
                    default: goal = h.p + line * offset; break;
                }
                mesh.pos[i] = Vector3.LerpUnclamped(p, goal, strength);
            }
        }

        static bool Nearest(Target T, Vector3 p, out Hit h)
        {
            h = default; h.dist = float.MaxValue;
            bool found = false;
            for (int k = 0; k < T.t.Length; k += 3)
            {
                Vector3 a = T.v[T.t[k]], b = T.v[T.t[k + 1]], c = T.v[T.t[k + 2]];
                var q = Closest(p, a, b, c);
                float d = (q - p).magnitude;
                if (d >= h.dist) continue;
                h.dist = d; h.p = q; found = true;
                Fill(T, k, ref h);
            }
            return found;
        }

        bool ProjectOnto(Target T, Vector3 p, Vector3 dir, out Hit h)
        {
            h = default; h.dist = limit > 0 ? MeshUnits(limit) : float.MaxValue;
            bool found = false;
            for (int k = 0; k < T.t.Length; k += 3)
            {
                Vector3 a = T.v[T.t[k]], b = T.v[T.t[k + 1]], c = T.v[T.t[k + 2]];
                if (!Ray(p, dir, a, b, c, out float s)) continue;
                if (s > 0 ? !positive : s < 0 ? !negative : false) continue;
                if (faceCull != Cull.Off)
                {
                    // front = the ray arrives against the face normal
                    bool front = Vector3.Dot(dir * Mathf.Sign(s == 0 ? 1 : s), Vector3.Cross(b - a, c - a)) < 0;
                    var cull = faceCull;
                    if (invertCull && s < 0) cull = cull == Cull.Front ? Cull.Back : Cull.Front;
                    if (cull == Cull.Front ? front : !front) continue;
                }
                float d = Mathf.Abs(s);
                if (d >= h.dist) continue;
                h.dist = d; h.p = p + dir * s; found = true;
                Fill(T, k, ref h);
            }
            return found;
        }

        /// <summary>Face normal, and the target's smooth normal interpolated at the hit point.</summary>
        static void Fill(Target T, int k, ref Hit h)
        {
            Vector3 a = T.v[T.t[k]], b = T.v[T.t[k + 1]], c = T.v[T.t[k + 2]];
            h.n = MeshData.Unit(Vector3.Cross(b - a, c - a));
            if (T.n == null) { h.smooth = h.n; return; }
            Vector3 v0 = b - a, v1 = c - a, v2 = h.p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-30f) { h.smooth = h.n; return; }
            float wb = (d11 * d20 - d01 * d21) / den, wc = (d00 * d21 - d01 * d20) / den, wa = 1f - wb - wc;
            h.smooth = MeshData.Unit(T.n[T.t[k]] * wa + T.n[T.t[k + 1]] * wb + T.n[T.t[k + 2]] * wc);
            if (h.smooth == Vector3.zero) h.smooth = h.n;
        }

        /// <summary>Möller–Trumbore, both ray directions; <paramref name="s"/> is signed distance.</summary>
        static bool Ray(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float s)
        {
            s = 0;
            Vector3 e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-30f) return false;
            float inv = 1f / det;
            Vector3 sv = o - a;
            float u = Vector3.Dot(sv, h) * inv;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(sv, e1);
            float w = Vector3.Dot(d, q) * inv;
            if (w < 0 || u + w > 1) return false;
            s = Vector3.Dot(e2, q) * inv;
            return true;
        }

        /// <summary>Closest point on triangle (Ericson, Real-Time Collision Detection 5.1.5).</summary>
        public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float den = 1f / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }
    }
}
