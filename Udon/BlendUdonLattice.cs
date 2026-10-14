using UdonSharp;
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Runtime Lattice (trilinear, same maths as the Lattice modifier) on this object's mesh, in Udon.
    /// The cage is a Transform whose children are the control points, in U-fastest order
    /// (index = u + U * (v + V * w)), placed in cage-local space where the rest grid spans -0.5..0.5.
    /// Move, animate or pick up the points at runtime; the mesh re-deforms only when a point moved.
    /// Use the component's ⋮ menu → Create Cage Points to build them (copies a Lattice Object's points if the cage has one).
    /// One Blend Udon deformer per mesh.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    public class BlendUdonLattice : UdonSharpBehaviour
    {
        public Transform cage;
        [Range(2, 16)] public int resolutionU = 2, resolutionV = 2, resolutionW = 2;
        [Range(0, 1)] public float strength = 1f;
        [Tooltip("Check the points every frame and re-deform when one moved. Off: call Deform() yourself.")]
        public bool everyFrame = true;
        public bool recalculateNormals = true;

        Mesh mesh;
        Vector3[] rest, work, pts, last;
        Matrix4x4 lastMatrix;

        void Start() { Deform(); }

        void Update()
        {
            if (!everyFrame || cage == null || mesh == null) return;
            if (!ReadPoints() && lastMatrix == ToLattice()) return;
            Deform();
        }

        Matrix4x4 ToLattice() { return cage.worldToLocalMatrix * transform.localToWorldMatrix; }

        /// <summary>Copy the child positions into pts. True when any moved since the last call.</summary>
        bool ReadPoints()
        {
            int count = resolutionU * resolutionV * resolutionW;
            if (pts == null || pts.Length != count) { pts = new Vector3[count]; last = new Vector3[count]; }
            bool changed = false;
            int children = cage.childCount;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = i < children ? cage.GetChild(i).localPosition : RestAt(i);
                if (p != last[i]) changed = true;
                pts[i] = p;
                last[i] = p;
            }
            return changed;
        }

        Vector3 RestAt(int i)
        {
            int u = i % resolutionU, v = (i / resolutionU) % resolutionV, w = i / (resolutionU * resolutionV);
            return new Vector3((float)u / (resolutionU - 1) - 0.5f, (float)v / (resolutionV - 1) - 0.5f, (float)w / (resolutionW - 1) - 0.5f);
        }

        public void Deform()
        {
            if (cage == null) return;
            if (mesh == null)
            {
                mesh = GetComponent<MeshFilter>().mesh;
                rest = mesh.vertices;
                work = new Vector3[rest.Length];
            }
            ReadPoints();
            Matrix4x4 to = ToLattice();
            lastMatrix = to;
            Matrix4x4 from = to.inverse;
            int ru = resolutionU, rv = resolutionV, rw = resolutionW;
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 lp = to.MultiplyPoint3x4(rest[i]);
                float gx = (lp.x + 0.5f) * (ru - 1), gy = (lp.y + 0.5f) * (rv - 1), gz = (lp.z + 0.5f) * (rw - 1);
                int u0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, ru - 2);
                int v0 = Mathf.Clamp(Mathf.FloorToInt(gy), 0, rv - 2);
                int w0 = Mathf.Clamp(Mathf.FloorToInt(gz), 0, rw - 2);
                float fu = gx - u0, fv = gy - v0, fw = gz - w0;
                Vector3 d = Vector3.zero;
                for (int c = 0; c < 8; c++)
                {
                    int du = c & 1, dv = (c >> 1) & 1, dw = (c >> 2) & 1;
                    float wgt = (du == 1 ? fu : 1 - fu) * (dv == 1 ? fv : 1 - fv) * (dw == 1 ? fw : 1 - fw);
                    int idx = (u0 + du) + ru * ((v0 + dv) + rv * (w0 + dw));
                    d += (pts[idx] - RestAt(idx)) * wgt;
                }
                work[i] = Vector3.LerpUnclamped(rest[i], from.MultiplyPoint3x4(lp + d), strength);
            }
            mesh.vertices = work;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Create Cage Points")]
        void CreateCagePoints()
        {
            if (cage == null)
            {
                cage = new GameObject(name + " Cage").transform;
                UnityEditor.Undo.RegisterCreatedObjectUndo(cage.gameObject, "Create Cage");
                cage.SetParent(transform, false);
            }
            var src = cage.GetComponent<BlendLattice>();
            if (src != null)
            {
                src.EnsurePoints();
                resolutionU = src.resolutionU; resolutionV = src.resolutionV; resolutionW = src.resolutionW;
            }
            for (int i = cage.childCount - 1; i >= 0; i--) UnityEditor.Undo.DestroyObjectImmediate(cage.GetChild(i).gameObject);
            int count = resolutionU * resolutionV * resolutionW;
            for (int i = 0; i < count; i++)
            {
                var p = new GameObject("Point " + i).transform;
                UnityEditor.Undo.RegisterCreatedObjectUndo(p.gameObject, "Create Cage Points");
                p.SetParent(cage, false);
                p.localPosition = src != null ? src.points[i] : RestAt(i);
            }
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
