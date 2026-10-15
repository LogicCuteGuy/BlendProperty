#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>A small runtime mesh generation example. Local to each player; call Generate after changing settings.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("")]
    public class BlendUdonRuntimeArray : UdonSharpBehaviour
    {
        [Tooltip("A readable triangle mesh with one submesh. Its vertices are used in local space.")]
        public Mesh source;
        public bool circle;
        [Range(1, 32)] public int count = 8;
        [Min(0)] public float spacing = 0.65f;
        [Min(0)] public float radius = 1.5f;

        Mesh generated;

        void Start() { Generate(); }

        void OnDestroy() { if (generated != null) Destroy(generated); }

        public void Generate()
        {
            if (source == null || !source.isReadable || source.vertexCount == 0 ||
                source.subMeshCount != 1 || source.GetTopology(0) != UnityEngine.MeshTopology.Triangles)
            {
                Debug.LogWarning("[Runtime Array] Assign a readable, nonempty triangle mesh with one submesh.");
                return;
            }
            // shortcut: small CPU demo capped at 32 copies / 65,535 vertices; use baked or GPU arrays for large scenes.
            int vertexCount = source.vertexCount;
            if (vertexCount > 65535)
            {
                Debug.LogWarning("[Runtime Array] Source exceeds the example's 65,535 vertex limit.");
                return;
            }
            int copies = Mathf.Clamp(count, 1, Mathf.Min(32, 65535 / vertexCount));
            float step = Mathf.Clamp(spacing, 0f, 10f);
            float r = Mathf.Clamp(radius, 0f, 10f);
            if (float.IsNaN(step) || float.IsNaN(r)) return;

            Vector3[] srcVertices = source.vertices;
            Vector3[] srcNormals = source.normals;
            Vector2[] srcUV = source.uv;
            int[] srcTriangles = source.triangles;
            Vector3[] vertices = new Vector3[vertexCount * copies];
            Vector3[] normals = new Vector3[vertices.Length];
            Vector2[] uv = new Vector2[vertices.Length];
            int[] triangles = new int[srcTriangles.Length * copies];

            for (int i = 0; i < copies; i++)
            {
                Quaternion rotation = circle ? Quaternion.Euler(0f, 360f * i / copies, 0f) : Quaternion.identity;
                Vector3 offset = circle ? rotation * Vector3.right * r
                    : Vector3.right * ((i - (copies - 1) * 0.5f) * step);
                int start = i * vertexCount;
                for (int v = 0; v < vertexCount; v++)
                {
                    vertices[start + v] = rotation * srcVertices[v] + offset;
                    if (srcNormals.Length == vertexCount) normals[start + v] = rotation * srcNormals[v];
                    if (srcUV.Length == vertexCount) uv[start + v] = srcUV[v];
                }
                for (int t = 0; t < srcTriangles.Length; t++)
                    triangles[i * srcTriangles.Length + t] = start + srcTriangles[t];
            }

            if (generated == null)
            {
                generated = new Mesh();
                generated.name = "Runtime Array";
                GetComponent<MeshFilter>().sharedMesh = generated;
            }
            generated.Clear();
            generated.vertices = vertices;
            generated.uv = uv;
            generated.triangles = triangles;
            if (srcNormals.Length == vertexCount) generated.normals = normals;
            else generated.RecalculateNormals();
            generated.RecalculateBounds();
        }
    }
}
