using UdonSharp;
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Runtime Wave (same maths as the Wave modifier) on this object's mesh, in Udon.
    /// Distances are metres. Runs on the CPU every frame while <see cref="animate"/> is on, so keep the mesh small
    /// (about 1,000 vertices); for big meshes use the "Blend Property/Wave" shader instead.
    /// One Blend Udon deformer per mesh.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    public class BlendUdonWave : UdonSharpBehaviour
    {
        [Tooltip("Advance Time every frame. Off: call Deform() yourself after changing fields.")]
        public bool animate = true;
        public bool motionX = true, motionZ = true;
        public bool cyclic = true;
        [Tooltip("Start position on the local XZ plane, in metres.")]
        public Vector2 start;
        public float height = 0.5f;
        public float width = 1.5f;
        public float narrowness = 1.5f;
        [Tooltip("Metres per second.")]
        public float speed = 0.25f;
        public float offset;
        public float time;
        [Tooltip("Recalculate normals after deforming. Splits along UV seams show as shading seams.")]
        public bool recalculateNormals = true;

        Mesh mesh;
        Vector3[] rest, work;

        void Start() { Deform(); }

        void Update()
        {
            if (!animate) return;
            time += Time.deltaTime;
            Deform();
        }

        public void Deform()
        {
            if (mesh == null)
            {
                mesh = GetComponent<MeshFilter>().mesh;
                rest = mesh.vertices;
                work = new Vector3[rest.Length];
            }
            Vector3 s = transform.lossyScale;
            float avg = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            float k = avg > 1e-30f ? 1f / avg : 1f;
            float h = height * k, w = Mathf.Max(width * k, 0.0001f), nar = narrowness / k;
            float sx = start.x * k, sz = start.y * k;
            float minFac = Mathf.Exp(-(w * nar) * (w * nar));
            float shift = (time - offset) * speed * k;
            for (int i = 0; i < rest.Length; i++)
            {
                Vector3 p = rest[i];
                float dx = p.x - sx, dz = p.z - sz;
                float dist = motionX && motionZ ? Mathf.Sqrt(dx * dx + dz * dz) : (motionX ? dx : dz);
                float x = dist - shift;
                if (cyclic) x = Mathf.Repeat(x + w, 2f * w) - w;
                if (x > -w && x < w && (motionX || motionZ))
                {
                    float a = x * nar;
                    p.y += (Mathf.Exp(-a * a) - minFac) * h;
                }
                work[i] = p;
            }
            mesh.vertices = work;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
