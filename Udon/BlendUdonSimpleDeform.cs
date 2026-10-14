using UdonSharp;
using UnityEngine;

namespace BlendProperty.Udon
{
    public enum BlendUdonDeformMode { Twist, Bend, Taper, Stretch }
    public enum BlendUdonAxis { X, Y, Z }

    /// <summary>
    /// Runtime Simple Deform (same maths as the Simple Deform modifier) on this object's mesh, in Udon.
    /// Deforms once on Start; call Deform() after changing fields, or turn on <see cref="everyFrame"/>
    /// to follow an animated <see cref="origin"/> or angle. One Blend Udon deformer per mesh.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    public class BlendUdonSimpleDeform : UdonSharpBehaviour
    {
        public BlendUdonDeformMode mode = BlendUdonDeformMode.Twist;
        [Tooltip("Twist / Bend, degrees.")]
        public float angle = 45f;
        [Tooltip("Taper / Stretch.")]
        public float factor = 0.5f;
        public BlendUdonAxis axis = BlendUdonAxis.Y;
        [Tooltip("Optional origin / orientation for the deformation.")]
        public Transform origin;
        [Range(0, 1)] public float lowerLimit = 0f;
        [Range(0, 1)] public float upperLimit = 1f;
        [Tooltip("Re-deform every frame (animated angle, factor or origin). Costs CPU per vertex.")]
        public bool everyFrame;
        public bool recalculateNormals = true;

        Mesh mesh;
        Vector3[] rest, work, co;

        void Start() { Deform(); }

        void Update()
        {
            if (everyFrame) Deform();
        }

        // deform axis -> Z (same swizzle as the editor modifier)
        Vector3 To(Vector3 p)
        {
            if (axis == BlendUdonAxis.X) return new Vector3(p.y, p.z, p.x);
            if (axis == BlendUdonAxis.Y) return new Vector3(p.z, p.x, p.y);
            return p;
        }

        Vector3 From(Vector3 q)
        {
            if (axis == BlendUdonAxis.X) return new Vector3(q.z, q.x, q.y);
            if (axis == BlendUdonAxis.Y) return new Vector3(q.y, q.z, q.x);
            return q;
        }

        public void Deform()
        {
            if (mesh == null)
            {
                mesh = GetComponent<MeshFilter>().mesh;
                rest = mesh.vertices;
                work = new Vector3[rest.Length];
                co = new Vector3[rest.Length];
            }
            int n = rest.Length;
            if (n == 0) return;
            Matrix4x4 toOrigin = Matrix4x4.identity;
            if (origin != null) toOrigin = (transform.worldToLocalMatrix * origin.localToWorldMatrix).inverse;
            Matrix4x4 fromOrigin = toOrigin.inverse;

            float zmin = float.MaxValue, zmax = float.MinValue, xmin = float.MaxValue, xmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 c = To(toOrigin.MultiplyPoint3x4(rest[i]));
                co[i] = c;
                if (c.z < zmin) zmin = c.z;
                if (c.z > zmax) zmax = c.z;
                if (c.x < xmin) xmin = c.x;
                if (c.x > xmax) xmax = c.x;
            }
            bool bend = mode == BlendUdonDeformMode.Bend;
            float lo = bend ? Mathf.Lerp(xmin, xmax, lowerLimit) : Mathf.Lerp(zmin, zmax, lowerLimit);
            float hi = bend ? Mathf.Lerp(xmin, xmax, upperLimit) : Mathf.Lerp(zmin, zmax, upperLimit);
            if (lo > hi) { float t0 = lo; lo = hi; hi = t0; }
            float k = angle * Mathf.Deg2Rad / Mathf.Max(hi - lo, 1e-6f);

            for (int i = 0; i < n; i++)
            {
                Vector3 p = co[i];
                if (mode == BlendUdonDeformMode.Twist)
                {
                    float t = Mathf.Clamp(p.z, lo, hi) * k, s = Mathf.Sin(t), c = Mathf.Cos(t);
                    p = new Vector3(p.x * c - p.y * s, p.x * s + p.y * c, p.z);
                }
                else if (bend)
                {
                    if (Mathf.Abs(k) >= 1e-7f)
                    {
                        float x = Mathf.Clamp(p.x, lo, hi), dcut = p.x - x;
                        float t = x * k, s = Mathf.Sin(t), c = Mathf.Cos(t), r = 1f / k;
                        p = new Vector3(-(p.y - r) * s + c * dcut, (p.y - r) * c + r + s * dcut, p.z);
                    }
                }
                else if (mode == BlendUdonDeformMode.Taper)
                {
                    float sc = 1f + Mathf.Clamp(p.z, lo, hi) * factor;
                    p = new Vector3(p.x * sc, p.y * sc, p.z);
                }
                else
                {
                    float z = Mathf.Clamp(p.z, lo, hi), dcut = p.z - z;
                    float sc = z * z * factor - factor + 1f;
                    p = new Vector3(p.x * sc, p.y * sc, z * (1f + factor) + dcut);
                }
                work[i] = fromOrigin.MultiplyPoint3x4(From(p));
            }
            mesh.vertices = work;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
