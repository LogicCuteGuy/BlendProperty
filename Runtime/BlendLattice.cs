using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Lattice cage for <see cref="LatticeModifier"/>. Unit cube (-0.5..0.5) in this transform's space;
    /// scale/rotate the GameObject to fit, then drag the points (scene handles when selected).
    /// </summary>
    [ExecuteAlways]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/LatticeObject.png")]
    [AddComponentMenu("Blend Modifier/Lattice Object")]
    public class BlendLattice : MonoBehaviour
#if BLEND_VRC
        , VRC.SDKBase.IEditorOnly
#endif
    {
        [Range(2, 16)] public int resolutionU = 2, resolutionV = 2, resolutionW = 2;
        [HideInInspector] public Vector3[] points;

        public int Index(int u, int v, int w) => u + resolutionU * (v + resolutionV * w);

        public Vector3 Rest(int u, int v, int w) => new Vector3(
            (float)u / (resolutionU - 1) - 0.5f, (float)v / (resolutionV - 1) - 0.5f, (float)w / (resolutionW - 1) - 0.5f);

        public void EnsurePoints()
        {
            int count = resolutionU * resolutionV * resolutionW;
            if (points != null && points.Length == count) return;
            points = new Vector3[count];
            ResetPoints();
        }

        public void ResetPoints()
        {
            for (int w = 0; w < resolutionW; w++)
                for (int v = 0; v < resolutionV; v++)
                    for (int u = 0; u < resolutionU; u++)
                        points[Index(u, v, w)] = Rest(u, v, w);
        }

        /// <summary>Deform a point given in lattice space (trilinear FFD; outside points use the edge cells).</summary>
        public Vector3 Deform(Vector3 p)
        {
            EnsurePoints();
            Vector3 g = new Vector3((p.x + 0.5f) * (resolutionU - 1), (p.y + 0.5f) * (resolutionV - 1), (p.z + 0.5f) * (resolutionW - 1));
            int u0 = Mathf.Clamp(Mathf.FloorToInt(g.x), 0, resolutionU - 2);
            int v0 = Mathf.Clamp(Mathf.FloorToInt(g.y), 0, resolutionV - 2);
            int w0 = Mathf.Clamp(Mathf.FloorToInt(g.z), 0, resolutionW - 2);
            float fu = g.x - u0, fv = g.y - v0, fw = g.z - w0;
            Vector3 d = Vector3.zero;
            for (int k = 0; k < 8; k++)
            {
                int du = k & 1, dv = (k >> 1) & 1, dw = (k >> 2) & 1;
                float wgt = (du == 1 ? fu : 1 - fu) * (dv == 1 ? fv : 1 - fv) * (dw == 1 ? fw : 1 - fw);
                int u = u0 + du, v = v0 + dv, w = w0 + dw;
                d += (points[Index(u, v, w)] - Rest(u, v, w)) * wgt;
            }
            return p + d;
        }

        void OnValidate() { EnsurePoints(); }

        void OnDrawGizmos()
        {
            EnsurePoints();
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.8f);
            for (int w = 0; w < resolutionW; w++)
                for (int v = 0; v < resolutionV; v++)
                    for (int u = 0; u < resolutionU; u++)
                    {
                        var p = points[Index(u, v, w)];
                        if (u + 1 < resolutionU) Gizmos.DrawLine(p, points[Index(u + 1, v, w)]);
                        if (v + 1 < resolutionV) Gizmos.DrawLine(p, points[Index(u, v + 1, w)]);
                        if (w + 1 < resolutionW) Gizmos.DrawLine(p, points[Index(u, v, w + 1)]);
                    }
        }
    }
}
