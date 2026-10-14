using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender Boolean (Union / Difference / Intersect) against another object's mesh.
    /// Both meshes should be closed (watertight) for a clean result.
    /// </summary>
    // ponytail: BSP CSG (csg.js), O(n²)-ish; fine for props, slow past a few thousand faces.
    // Leaves T-junctions where faces were cut (invisible after welding, Blender's Exact solver avoids them).
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Boolean.png")]
    [AddComponentMenu("Blend Modifier/Generate/Boolean")]
    public class BooleanModifier : BlendModifier
    {
        public enum Operation { Intersect, Union, Difference }
        public enum OperandType { Object, Collection }

        public Operation operation = Operation.Difference;
        [Tooltip("Object: one cutter. Collection: every mesh under the Collection object.")]
        public OperandType operandType = OperandType.Object;
        [Tooltip("Object whose MeshFilter (or Blend result) is the second operand. Hide its renderer yourself, like Blender's cutter.")]
        public MeshFilter target;
        [Tooltip("Collection: all MeshFilters under this object (itself included) are operands, applied one after another.")]
        public Transform collection;
        [Tooltip("Submesh (material slot) the target's faces get.")]
        [Min(0)] public int targetMaterial;

        IEnumerable<MeshFilter> Operands()
        {
            if (operandType == OperandType.Object) { if (target) yield return target; yield break; }
            if (!collection) yield break;
            foreach (var f in collection.GetComponentsInChildren<MeshFilter>()) yield return f;
        }

        public override IEnumerable<Object> Dependencies()
        {
            foreach (var f in Operands()) yield return f;
        }

        public override void Apply(MeshData mesh)
        {
            foreach (var t in Operands())
            {
                if (!t || !t.sharedMesh || t.gameObject == gameObject) continue;
                var other = MeshData.FromMesh(t.sharedMesh, false);
                var xf = ToLocal(t.transform);
                for (int i = 0; i < other.pos.Count; i++) other.pos[i] = xf.MultiplyPoint3x4(other.pos[i]);
                foreach (var f in other.faces) f.sub = targetMaterial;
                mesh.subMeshCount = Mathf.Max(mesh.subMeshCount, targetMaterial + 1);
                Csg(mesh, other, operation);
            }
        }

        /// <summary>Replace <paramref name="a"/> with (a op b). Both in the same space.</summary>
        public static void Csg(MeshData a, MeshData b, Operation op)
        {
            var A = new Node(); A.Build(Polys(a)); A.SetBounds(a.GetBounds());
            var B = new Node(); B.Build(Polys(b)); B.SetBounds(b.GetBounds());
            switch (op)
            {
                case Operation.Union:
                    A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert();
                    break;
                case Operation.Difference:
                    A.Invert(); A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert();
                    break;
                default:
                    A.Invert(); B.ClipTo(A); B.Invert(); A.ClipTo(B); B.ClipTo(A);
                    break;
            }
            // csg.js ends with A.Build(B.All()) (+ A.Invert()) so the result can feed another operation;
            // we only need the polygons, and that Build would split B's polygons again for nothing.
            var result = A.All();
            result.AddRange(B.All());
            if (op != Operation.Union) foreach (var p in result) p.Flip();

            a.pos.Clear(); a.faces.Clear();
            a.hasUV1 |= b.hasUV1; a.hasColor |= b.hasColor;
            foreach (var p in result)
            {
                var v = new int[p.p.Count];
                for (int i = 0; i < v.Length; i++) { v[i] = a.pos.Count; a.pos.Add(p.p[i]); }
                a.faces.Add(new Face(v, p.a.ToArray(), p.sub));
            }
            a.Weld(1e-5f);
        }

        // ------------------------------------------------------------------ BSP

        const float Eps = 1e-5f;

        class Poly
        {
            public List<Vector3> p; public List<Attr> a; public int sub;
            public Vector3 n; public float w;

            public Poly(List<Vector3> p, List<Attr> a, int sub, Vector3 n, float w) { this.p = p; this.a = a; this.sub = sub; this.n = n; this.w = w; }

            public void Flip()
            {
                p.Reverse(); a.Reverse();
                for (int i = 0; i < a.Count; i++) { var t = a[i]; t.normal = -t.normal; a[i] = t; }
                n = -n; w = -w;
            }
        }

        static List<Poly> Polys(MeshData m)
        {
            // Triangles only: a slightly bent quad straddles its own plane and never settles in the tree.
            var r = new List<Poly>(m.faces.Count * 2);
            foreach (var f in m.faces)
                foreach (var t in m.Triangulate(f))
                {
                    Vector3 a = m.pos[f.v[t.x]], b = m.pos[f.v[t.y]], c = m.pos[f.v[t.z]];
                    var n = MeshData.Unit(Vector3.Cross(b - a, c - a));
                    if (n == Vector3.zero) continue;
                    r.Add(new Poly(new List<Vector3> { a, b, c }, new List<Attr> { f.a[t.x], f.a[t.y], f.a[t.z] }, f.sub, n, Vector3.Dot(n, (a + b + c) / 3f)));
                }
            return r;
        }

        // csg.js splitPolygon: coplanar polys go to the front/back list matching their facing.
        static void Split(Vector3 n, float w, Poly poly, List<Poly> coFront, List<Poly> coBack, List<Poly> front, List<Poly> back)
        {
            const int Co = 0, Fr = 1, Bk = 2, Sp = 3;
            int type = 0;
            System.Span<int> types = poly.p.Count <= 64 ? stackalloc int[poly.p.Count] : new int[poly.p.Count];
            for (int i = 0; i < types.Length; i++)
            {
                float t = Vector3.Dot(n, poly.p[i]) - w;
                types[i] = t < -Eps ? Bk : t > Eps ? Fr : Co;
                type |= types[i];
            }
            switch (type)
            {
                case Co: (Vector3.Dot(n, poly.n) > 0 ? coFront : coBack).Add(poly); return;
                case Fr: front.Add(poly); return;
                case Bk: back.Add(poly); return;
            }
            Debug.Assert(type == Sp);
            var fp = new List<Vector3>(); var fa = new List<Attr>();
            var bp = new List<Vector3>(); var ba = new List<Attr>();
            for (int i = 0; i < poly.p.Count; i++)
            {
                int j = (i + 1) % poly.p.Count;
                int ti = types[i], tj = types[j];
                Vector3 vi = poly.p[i], vj = poly.p[j];
                if (ti != Bk) { fp.Add(vi); fa.Add(poly.a[i]); }
                if (ti != Fr) { bp.Add(vi); ba.Add(poly.a[i]); }
                if ((ti | tj) == Sp)
                {
                    float t = (w - Vector3.Dot(n, vi)) / Vector3.Dot(n, vj - vi);
                    var v = Vector3.LerpUnclamped(vi, vj, t);
                    var at = Attr.Lerp(poly.a[i], poly.a[j], t);
                    fp.Add(v); fa.Add(at); bp.Add(v); ba.Add(at);
                }
            }
            if (fp.Count >= 3) front.Add(new Poly(fp, fa, poly.sub, poly.n, poly.w));
            if (bp.Count >= 3) back.Add(new Poly(bp, ba, poly.sub, poly.n, poly.w));
        }

        // Every walk is iterative: a convex mesh makes a chain as deep as its face count.
        class Node
        {
            bool hasPlane; Vector3 n; float w;
            Node front, back;
            List<Poly> polys = new List<Poly>();
            // Root only: the solid's box, and whether the tree currently means its complement.
            Bounds bounds; bool hasBounds, inverted;

            public void SetBounds(Bounds b) { b.Expand(Eps * 4); bounds = b; hasBounds = true; }

            /// <summary>Polygon entirely outside the solid's box: the tree would only split it and keep it whole.</summary>
            bool Outside(Poly p)
            {
                Vector3 min = p.p[0], max = p.p[0];
                foreach (var v in p.p) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
                Vector3 bmin = bounds.min, bmax = bounds.max;
                return max.x < bmin.x || max.y < bmin.y || max.z < bmin.z || min.x > bmax.x || min.y > bmax.y || min.z > bmax.z;
            }

            IEnumerable<Node> Nodes()
            {
                var stack = new Stack<Node>(); stack.Push(this);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    yield return x;
                    if (x.front != null) stack.Push(x.front);
                    if (x.back != null) stack.Push(x.back);
                }
            }

            public void Invert()
            {
                inverted = !inverted;
                foreach (var x in Nodes())
                {
                    foreach (var p in x.polys) p.Flip();
                    x.n = -x.n; x.w = -x.w;
                    (x.front, x.back) = (x.back, x.front);
                }
            }

            /// <summary>Remove the parts of <paramref name="list"/> inside this solid.</summary>
            List<Poly> Clip(List<Poly> list)
            {
                var result = new List<Poly>(list.Count);
                var frags = new List<Poly>();
                var f = new List<Poly>(2); var b = new List<Poly>(2);
                var stack = new Stack<(Node, Poly)>();
                foreach (var p0 in list)
                {
                    // Outside the box = outside the solid: kept, or dropped when the tree is inverted (complement).
                    if (hasBounds && Outside(p0)) { if (!inverted) result.Add(p0); continue; }
                    frags.Clear();
                    bool dropped = false;
                    stack.Push((this, p0));
                    while (stack.Count > 0)
                    {
                        var (x, p) = stack.Pop();
                        if (!x.hasPlane) { frags.Add(p); continue; }
                        f.Clear(); b.Clear();
                        Split(x.n, x.w, p, f, b, f, b);
                        foreach (var q in f) if (x.front != null) stack.Push((x.front, q)); else frags.Add(q);
                        foreach (var q in b) if (x.back != null) stack.Push((x.back, q)); else dropped = true; // solid: drop
                    }
                    // Nothing removed: the pieces only came from planes that pass by; keep the whole polygon.
                    if (!dropped && frags.Count > 1) result.Add(p0);
                    else result.AddRange(frags);
                }
                return result;
            }

            public void ClipTo(Node bsp)
            {
                foreach (var x in Nodes()) x.polys = bsp.Clip(x.polys);
            }

            public List<Poly> All()
            {
                var r = new List<Poly>();
                foreach (var x in Nodes()) r.AddRange(x.polys);
                return r;
            }

            public void Build(List<Poly> list)
            {
                var stack = new Stack<(Node, List<Poly>)>(); stack.Push((this, list));
                while (stack.Count > 0)
                {
                    var (x, l) = stack.Pop();
                    if (l.Count == 0) continue;
                    int first = 0;
                    if (!x.hasPlane)
                    {
                        // The plane's own polygon always lands here, even if float error says otherwise: guarantees progress.
                        x.hasPlane = true; x.n = l[0].n; x.w = l[0].w;
                        x.polys.Add(l[0]); first = 1;
                    }
                    var f = new List<Poly>(); var b = new List<Poly>();
                    for (int k = first; k < l.Count; k++) Split(x.n, x.w, l[k], x.polys, x.polys, f, b);
                    if (f.Count > 0) stack.Push((x.front ??= new Node(), f));
                    if (b.Count > 0) stack.Push((x.back ??= new Node(), b));
                }
            }
        }
    }
}
