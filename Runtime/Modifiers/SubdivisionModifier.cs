using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Subdivision.png")]
    [AddComponentMenu("Blend Property/Modifiers/Generate/Subdivision Surface")]
    public class SubdivisionModifier : BlendModifier
    {
        public enum Mode { CatmullClark, Simple }

        public Mode mode = Mode.CatmullClark;
        [Range(0, 6)] public int levels = 1;
        [Tooltip("Keep open boundaries smooth (Blender default). Off = boundary corners stay sharp.")]
        public bool smoothBoundary = true;

        public override void Apply(MeshData mesh)
        {
            for (int i = 0; i < levels; i++) Subdivide(mesh, mode == Mode.Simple, smoothBoundary);
        }

        /// <summary>One level: every n-gon becomes n quads. Attributes are interpolated linearly.</summary>
        public static void Subdivide(MeshData m, bool simple, bool smoothBoundary)
        {
            var P = m.pos;
            int nv = P.Count;
            var edgeFaces = m.EdgeFaces();

            // face points
            var facePt = new int[m.faces.Count];
            var newPos = new List<Vector3>(P);
            for (int fi = 0; fi < m.faces.Count; fi++) { facePt[fi] = newPos.Count; newPos.Add(m.FaceCenter(m.faces[fi])); }

            // edge points
            var edgePt = new Dictionary<long, int>(edgeFaces.Count, MeshData.Keys);
            foreach (var kv in edgeFaces)
            {
                int a = MeshData.EdgeA(kv.Key), b = MeshData.EdgeB(kv.Key);
                Vector3 p = (P[a] + P[b]) * 0.5f;
                if (!simple && kv.Value.Count == 2)
                    p = (P[a] + P[b] + newPos[facePt[kv.Value[0]]] + newPos[facePt[kv.Value[1]]]) * 0.25f;
                edgePt[kv.Key] = newPos.Count;
                newPos.Add(p);
            }

            // vertex points
            if (!simple)
            {
                var faceSum = new Vector3[nv]; var faceCnt = new int[nv];
                var edgeSum = new Vector3[nv]; var edgeCnt = new int[nv];
                var bndSum = new Vector3[nv]; var bndCnt = new int[nv];
                for (int fi = 0; fi < m.faces.Count; fi++)
                    foreach (var v in m.faces[fi].v) { faceSum[v] += newPos[facePt[fi]]; faceCnt[v]++; }
                foreach (var kv in edgeFaces)
                {
                    int a = MeshData.EdgeA(kv.Key), b = MeshData.EdgeB(kv.Key);
                    var mid = (P[a] + P[b]) * 0.5f;
                    edgeSum[a] += mid; edgeCnt[a]++; edgeSum[b] += mid; edgeCnt[b]++;
                    if (kv.Value.Count != 2) { bndSum[a] += P[b]; bndCnt[a]++; bndSum[b] += P[a]; bndCnt[b]++; }
                }
                for (int v = 0; v < nv; v++)
                {
                    if (faceCnt[v] == 0) continue;
                    if (bndCnt[v] > 0)
                    {
                        if (bndCnt[v] == 2 && smoothBoundary) newPos[v] = (P[v] * 6f + bndSum[v]) / 8f;
                        continue; // corner / non-manifold / sharp boundary: keep
                    }
                    float n = edgeCnt[v];
                    var Q = faceSum[v] / faceCnt[v];
                    var R = edgeSum[v] / edgeCnt[v];
                    newPos[v] = (Q + 2f * R + (n - 3f) * P[v]) / n;
                }
            }

            var faces = new List<Face>(m.faces.Count * 4);
            for (int fi = 0; fi < m.faces.Count; fi++)
            {
                var f = m.faces[fi];
                int k = f.Count;
                var center = Attr.Average(f.a);
                for (int c = 0; c < k; c++)
                {
                    int prev = (c + k - 1) % k, next = (c + 1) % k;
                    int eNext = edgePt[MeshData.EdgeKey(f.v[c], f.v[next])];
                    int ePrev = edgePt[MeshData.EdgeKey(f.v[prev], f.v[c])];
                    faces.Add(new Face(
                        new[] { f.v[c], eNext, facePt[fi], ePrev },
                        new[] { f.a[c], Attr.Lerp(f.a[c], f.a[next], 0.5f), center, Attr.Lerp(f.a[prev], f.a[c], 0.5f) },
                        f.sub));
                }
            }
            m.pos = newPos;
            m.faces = faces;
        }
    }
}
