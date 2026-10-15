#if BLEND_UDON
using UdonSharp;
using VRC.SDKBase;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Blender Explode modifier: call Explode() and every face flies out along its normal, tumbles, falls and lands on
    /// the floor; ResetExplode() puts it back. Runs in the shader (Blend Property/Explode) from the start time alone,
    /// so it costs Udon nothing. Use ⋮ → Bake Explode once (splits the faces apart and swaps in Explode materials);
    /// ⋮ → Unbake Explode restores the mesh and materials. Networked: one call explodes it for everyone in the instance,
    /// and players who join later see the pieces already landed (synced as a server time).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)] // Continuous: works next to VRC Object Sync
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/Explode.png")]
    [AddComponentMenu("Blend Property/Physics/Destruction/Explode")]
    public class BlendUdonExplode : UdonSharpBehaviour
    {
        [Tooltip("m/s out along each face's normal (each piece gets 50-150 % of it).")]
        public float speed = 3f;
        [Min(0f), Tooltip("m/s in a random direction on top.")]
        public float random = 1.5f;
        [Tooltip("Radians per second each piece tumbles.")]
        public float spin = 6f;
        [Tooltip("Metres per second squared, world space.")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        [Tooltip("Pieces stop where they reach this height below the object's pivot (metres). Off: they fall forever.")]
        public bool useFloor = true;
        public float floorOffset;
        [Min(0f), Tooltip("Seconds until the pieces shrink away. 0 = they stay until ResetExplode().")]
        public float lifetime;
        [Tooltip("Turn this object's colliders off while exploded.")]
        public bool disableColliders = true;
        [Tooltip("Explode() and ResetExplode() reach every player, late joiners included. Off: only the player who called it.")]
        public bool networked = true;

        [Header("Baked (⋮ → Bake Explode)")]
        public Mesh source;
        public Material[] sourceMaterials;

        Renderer rend;
        Material[] mats;
        Collider[] cols;
        bool exploded;
        [UdonSynced] double explodedAt = -1; // server time it exploded, -1 = whole

        void Start()
        {
            rend = GetComponent<Renderer>();
            Material first = rend != null ? rend.sharedMaterial : null;
            if (first == null || !first.HasProperty("_ExplodeTime")) { Debug.LogWarning("[BlendUdonExplode] Not baked: use ⋮ → Bake Explode.", this); enabled = false; return; }
            mats = rend.materials;
            cols = GetComponents<Collider>();
            Send(-1f);
            if (networked) OnDeserialization(); // late joiner: already exploded
        }

        public void Explode()
        {
            ExplodeLocal();
            Share(ServerTime());
        }

        public void ResetExplode()
        {
            ResetLocal();
            Share(-1);
        }

        double ServerTime()
        {
#if BLEND_UDON
            return Networking.GetServerTimeInSeconds();
#else
            return Time.timeSinceLevelLoadAsDouble;
#endif
        }

        void Share(double at)
        {
            if (!networked) return;
#if BLEND_UDON
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
#endif
            explodedAt = at;
        }

        public override void OnDeserialization()
        {
            if (mats == null) return; // Start applies it
            if (explodedAt < 0) { if (exploded) ResetLocal(); }
            else if (!exploded) ExplodeAt(Time.timeSinceLevelLoad - (float)(ServerTime() - explodedAt));
        }

        public void ExplodeLocal() { ExplodeAt(Time.timeSinceLevelLoad); } // the shader's _Time.y

        void ExplodeAt(float start)
        {
            if (mats == null || exploded) return;
            exploded = true;
            if (start < 0f && start > -2f) start = 0f; // the shader reads -2..0 as whole
            Send(start);
            SetColliders(!disableColliders);
        }

        public void ResetLocal()
        {
            if (mats == null) return;
            exploded = false;
            Send(-1f);
            SetColliders(true);
        }

        void Send(float start)
        {
            float floor = useFloor ? transform.position.y - floorOffset : -100000f;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                m.SetFloat("_ExplodeTime", start);
                m.SetFloat("_Speed", speed);
                m.SetFloat("_Random", random);
                m.SetFloat("_Spin", spin);
                m.SetVector("_Gravity", gravity);
                m.SetFloat("_Floor", floor);
                m.SetFloat("_Lifetime", lifetime);
            }
        }

        void SetColliders(bool on)
        {
            for (int i = 0; i < cols.Length; i++) if (cols[i] != null) cols[i].enabled = on;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Explode")]
        void Bake()
        {
            var mf = GetComponent<MeshFilter>();
            var r = GetComponent<Renderer>();
            if (source == null) source = mf.sharedMesh;
            if (source == null) { Debug.LogError("[BlendUdonExplode] No mesh.", this); return; }
            if (!source.isReadable) { Debug.LogError("[BlendUdonExplode] Enable Read/Write on the mesh import settings.", this); return; }
            if (r != null && (sourceMaterials == null || sourceMaterials.Length == 0)) sourceMaterials = r.sharedMaterials;

            // every triangle gets its own 3 vertices, carrying its centre + random (uv2) and normal (uv3)
            var verts = source.vertices; var norms = source.normals; var tangents = source.tangents;
            var uv0 = new System.Collections.Generic.List<Vector2>(); source.GetUVs(0, uv0);
            var cols = source.colors;
            var oV = new System.Collections.Generic.List<Vector3>(); var oN = new System.Collections.Generic.List<Vector3>();
            var oT = new System.Collections.Generic.List<Vector4>(); var oUV = new System.Collections.Generic.List<Vector2>();
            var oC = new System.Collections.Generic.List<Color>();
            var oU2 = new System.Collections.Generic.List<Vector4>(); var oU3 = new System.Collections.Generic.List<Vector4>();
            var rng = new System.Random(4321);
            var subs = new int[source.subMeshCount][];
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var tris = source.GetTriangles(s);
                var outT = new int[tris.Length];
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                    Vector3 centre = (a + b + c) / 3f;
                    Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                    float rnd = (float)rng.NextDouble();
                    for (int k = 0; k < 3; k++)
                    {
                        int i = tris[t + k];
                        outT[t + k] = oV.Count;
                        oV.Add(verts[i]);
                        oN.Add(i < norms.Length ? norms[i] : n);
                        if (tangents.Length == verts.Length) oT.Add(tangents[i]);
                        oUV.Add(i < uv0.Count ? uv0[i] : Vector2.zero);
                        if (cols.Length == verts.Length) oC.Add(cols[i]);
                        oU2.Add(new Vector4(centre.x, centre.y, centre.z, rnd));
                        oU3.Add(n);
                    }
                }
                subs[s] = outT;
            }
            var mesh = new Mesh { name = source.name + "_explode", indexFormat = oV.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(oV); mesh.SetNormals(oN);
            if (oT.Count == oV.Count) mesh.SetTangents(oT);
            mesh.SetUVs(0, oUV);
            if (oC.Count == oV.Count) mesh.SetColors(oC);
            mesh.SetUVs(2, oU2); mesh.SetUVs(3, oU3);
            mesh.subMeshCount = subs.Length;
            for (int s = 0; s < subs.Length; s++) mesh.SetTriangles(subs[s], s);
            // ponytail: culling box from how far a piece can fly in 3 s (or Lifetime); enlarge it if pieces pop out of view
            float T = lifetime > 0f ? lifetime : 3f;
            float reach = Mathf.Min(200f, (Mathf.Abs(speed) * 1.5f + random) * T + 0.5f * gravity.magnitude * T * T);
            mesh.RecalculateBounds();
            var bounds = mesh.bounds; bounds.Expand(2f * reach); mesh.bounds = bounds;

            // keep materials whose shader already explodes (includes BlendExplode.cginc); others get an Explode copy
            // with their colour, textures, smoothness and emission carried over (Standard, URP, lilToon, Poiyomi, ...)
            var shader = Shader.Find("Blend Property/Explode");
            var mats = new Material[sourceMaterials != null ? sourceMaterials.Length : 0];
            var made = new System.Collections.Generic.List<Material>();
            for (int i = 0; i < mats.Length; i++)
            {
                var src = sourceMaterials[i];
                if (src != null && src.HasProperty("_ExplodeTime")) { mats[i] = src; continue; }
                var m = new Material(shader) { name = (src != null ? src.name : "Material") + "_explode" };
                BlendProperty.MaterialTransfer.Copy(src, m);
                mats[i] = m;
                made.Add(m);
            }

            string path = BakedPath("explode", mf.sharedMesh != source ? mf.sharedMesh : null);
            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(mesh, path);
            foreach (var m in made) UnityEditor.AssetDatabase.AddObjectToAsset(m, path);
            UnityEditor.AssetDatabase.SaveAssets();

            UnityEditor.Undo.RecordObject(mf, "Bake Explode");
            mf.sharedMesh = mesh;
            if (r != null) { UnityEditor.Undo.RecordObject(r, "Bake Explode"); r.sharedMaterials = mats; }
            UnityEditor.Undo.RecordObject(this, "Bake Explode");
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonExplode] Baked {oV.Count / 3} pieces to {path}.", this);
        }

        [ContextMenu("Unbake Explode")]
        void Unbake()
        {
            if (source == null) return;
            var mf = GetComponent<MeshFilter>();
            var r = GetComponent<Renderer>();
            UnityEditor.Undo.RecordObject(mf, "Unbake Explode");
            mf.sharedMesh = source;
            if (r != null && sourceMaterials != null && sourceMaterials.Length > 0) { UnityEditor.Undo.RecordObject(r, "Unbake Explode"); r.sharedMaterials = sourceMaterials; }
            UnityEditor.Undo.RecordObject(this, "Unbake Explode");
            source = null;
            sourceMaterials = null;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        // reuse the asset the previous bake made, else a new one in Assets/BlendProperty/Baked
        string BakedPath(string kind, Object previous)
        {
            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            return previous != null && UnityEditor.AssetDatabase.Contains(previous)
                ? UnityEditor.AssetDatabase.GetAssetPath(previous)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_{kind}.asset");
        }
#endif
    }
}
