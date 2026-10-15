using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Wireframe: every edge becomes a solid frame strip.</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Wireframe.png")]
    [AddComponentMenu("Blend Property/Modifiers/Generate/Wireframe")]
    public class WireframeModifier : BlendModifier
    {
        [Min(0)] public float thickness = 0.02f;
        [Range(-1, 1)] public float offset = -1f;
        [Tooltip("Keep the original faces as well (Blender: Replace Original off).")]
        public bool replaceOriginal = true;
        [Tooltip("Keep strips the same width at sharp corners.")]
        public bool even = true;
        [Tooltip("Strip width follows edge length: Thickness becomes a fraction of each edge (0.1 = 10%).")]
        public bool relative;
        [Tooltip("Material slot offset for the frame (0 = the face's own material).")]
        public int materialOffset;

        // Relative: Thickness is a fraction of each edge's length, not a distance, so it isn't converted from metres
        public override void Apply(MeshData mesh) => Wireframe(mesh, MeshUnits(thickness), offset, replaceOriginal, even, relative ? thickness : 0f, materialOffset);

        public static void Wireframe(MeshData m, float thickness, float offset, bool replaceOriginal,
            bool even = true, float relativeFactor = 0f, int materialOffset = 0)
        {
            if (thickness <= 0) return;
            var P = m.pos;
            var original = m.faces;
            var frames = new List<Face>(original.Count * 4);
            int maxSub = m.subMeshCount - 1;
            foreach (var f in original)
            {
                int sub = Mathf.Max(0, f.sub + materialOffset);
                maxSub = Mathf.Max(maxSub, sub);
                // inset every corner along its bisector so each edge strip is `thickness` wide
                var inner = new int[f.Count];
                for (int c = 0; c < f.Count; c++)
                {
                    Vector3 v = P[f.v[c]];
                    Vector3 toPrev = P[f.v[(c + f.Count - 1) % f.Count]] - v, toNext = P[f.v[(c + 1) % f.Count]] - v;
                    var dir = MeshData.Unit(toPrev) + MeshData.Unit(toNext);
                    float half = Vector3.Angle(toPrev, toNext) * 0.5f * Mathf.Deg2Rad;
                    float t = relativeFactor > 0 ? relativeFactor * 0.5f * (toPrev.magnitude + toNext.magnitude) : thickness;
                    float d = even ? t / Mathf.Max(Mathf.Sin(half), 0.05f) : t;
                    d = Mathf.Min(d, 0.5f * Mathf.Min(toPrev.magnitude, toNext.magnitude) / Mathf.Max(Mathf.Cos(half), 0.05f));
                    inner[c] = P.Count;
                    P.Add(dir.sqrMagnitude > 1e-12f ? v + MeshData.Unit(dir) * d : v);
                }
                for (int c = 0; c < f.Count; c++)
                {
                    int c2 = (c + 1) % f.Count;
                    frames.Add(new Face(new[] { f.v[c], f.v[c2], inner[c2], inner[c] },
                                        new[] { f.a[c], f.a[c2], f.a[c2], f.a[c] }, sub));
                }
            }
            m.faces = frames;
            m.subMeshCount = maxSub + 1;
            SolidifyModifier.Solidify(m, thickness, offset, evenThickness: even, fillRim: true, flipNormals: false);
            if (!replaceOriginal) m.faces.AddRange(original.ConvertAll(f => f.Clone()));
        }
    }
}
