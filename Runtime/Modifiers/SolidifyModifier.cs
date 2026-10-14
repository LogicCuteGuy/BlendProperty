using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Solidify (Simple mode).</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Solidify.png")]
    [AddComponentMenu("Blend Modifier/Generate/Solidify")]
    public class SolidifyModifier : BlendModifier
    {
        public float thickness = 0.01f;
        [Range(-1, 1)]
        [Tooltip("-1 = shell grows inward, 1 = outward, 0 = centered.")]
        public float offset = -1f;
        [Tooltip("Keep thickness even at sharp corners (Blender: Even Thickness).")]
        public bool evenThickness;
        public bool fillRim = true;
        [Tooltip("Keep only the new shell and the rim (drop the original surface).")]
        public bool onlyRim;
        public bool flipNormals;
        [Range(0, 2), Tooltip("Limit the offset to this many times the shortest adjacent edge, to avoid self-intersection. 0 = off.")]
        public float clamp;
        [Tooltip("Material slot offset for the new shell (0 = same material as the original face).")]
        public int materialOffset;
        [Tooltip("Material slot offset for the rim faces.")]
        public int rimMaterialOffset;

        public override void Apply(MeshData mesh) =>
            Solidify(mesh, MeshUnits(thickness), offset, evenThickness, fillRim, flipNormals, onlyRim, clamp, materialOffset, rimMaterialOffset);

        public static void Solidify(MeshData mesh, float thickness, float offset, bool evenThickness, bool fillRim, bool flipNormals,
            bool onlyRim = false, float clamp = 0f, int materialOffset = 0, int rimMaterialOffset = 0)
        {
            var vn = mesh.VertexNormals();
            if (evenThickness)
            {
                // Scale by 1/cos of the angle to adjacent faces (approximate Blender's even thickness).
                var minDot = new float[vn.Length];
                for (int i = 0; i < minDot.Length; i++) minDot[i] = 1f;
                foreach (var f in mesh.faces)
                {
                    var fn = MeshData.Unit(mesh.FaceNormal(f));
                    foreach (var i in f.v) minDot[i] = Mathf.Min(minDot[i], Mathf.Max(0.25f, Vector3.Dot(fn, vn[i])));
                }
                for (int i = 0; i < vn.Length; i++) vn[i] /= minDot[i];
            }

            int n = mesh.pos.Count;
            float outer = thickness * (offset + 1f) * 0.5f, inner = thickness * (offset - 1f) * 0.5f;
            if (clamp > 0)
            {
                // shortest edge at each vertex caps how far it may move
                var shortest = new float[n];
                for (int i = 0; i < n; i++) shortest[i] = float.MaxValue;
                foreach (var kv in mesh.EdgeFaces())
                {
                    int a = MeshData.EdgeA(kv.Key), b = MeshData.EdgeB(kv.Key);
                    float l = (mesh.pos[a] - mesh.pos[b]).magnitude;
                    shortest[a] = Mathf.Min(shortest[a], l); shortest[b] = Mathf.Min(shortest[b], l);
                }
                float reach = Mathf.Max(Mathf.Abs(outer), Mathf.Abs(inner));
                for (int i = 0; i < n; i++)
                {
                    float move = reach * vn[i].magnitude;
                    if (move > clamp * shortest[i] && move > 0) vn[i] *= clamp * shortest[i] / move;
                }
            }
            var edgeFaces = mesh.EdgeFaces();
            var orig = mesh.faces;
            var faces = new List<Face>(orig.Count * 2);
            int maxSub = mesh.subMeshCount - 1;
            int Sub(int s, int off) { int r = Mathf.Max(0, s + off); maxSub = Mathf.Max(maxSub, r); return r; }

            for (int i = 0; i < n; i++) mesh.pos.Add(mesh.pos[i] + vn[i] * inner);
            for (int i = 0; i < n; i++) mesh.pos[i] += vn[i] * outer;

            foreach (var f in orig)
            {
                if (!onlyRim) faces.Add(f);
                var g = f.Clone();
                for (int c = 0; c < g.Count; c++) { g.v[c] += n; g.a[c].normal = -g.a[c].normal; }
                g.Reverse();
                g.sub = Sub(f.sub, materialOffset);
                faces.Add(g);
            }

            if (fillRim || onlyRim)
                foreach (var f in orig)
                    for (int c = 0; c < f.Count; c++)
                    {
                        int c2 = (c + 1) % f.Count, a = f.v[c], b = f.v[c2];
                        if (edgeFaces[MeshData.EdgeKey(a, b)].Count != 1) continue;
                        // boundary edge a->b on outer shell; rim quad b, a, a', b'
                        faces.Add(new Face(new[] { b, a, a + n, b + n }, new[] { f.a[c2], f.a[c], f.a[c], f.a[c2] }, Sub(f.sub, rimMaterialOffset)));
                    }

            if (flipNormals) foreach (var f in faces) f.Reverse();
            mesh.faces = faces;
            mesh.subMeshCount = maxSub + 1;
            if (onlyRim) mesh.Compact();
        }
    }
}
