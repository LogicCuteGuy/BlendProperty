#if BLEND_UDON
using UdonSharp;
using VRC.SDK3.Components;
using VRC.SDKBase;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Blender Cell Fracture: ⋮ → Bake Fracture cuts this mesh into Voronoi pieces (cut faces get the Interior Material),
    /// each a hidden Rigidbody child. At runtime the object breaks when something hits it hard enough, or when Break()
    /// is called: it hides itself and lets the pieces fly, carrying its speed plus an outward push. ResetFracture()
    /// puts it back together. Networked: a break reaches every player (the pieces then fall on each player's own
    /// physics, so they land a little differently). Players who join later see it broken (the pieces drop from where
    /// they were).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)] // Continuous: works next to VRC Object Sync
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/CellFracture.png")]
    [AddComponentMenu("Blend Property/Physics/Destruction/Cell Fracture")]
    public class BlendUdonFracture : UdonSharpBehaviour
    {
        [Tooltip("Break when something hits this object (or it hits something) faster than Break Speed.")]
        public bool breakOnImpact = true;
        [Min(0f), Tooltip("m/s of impact needed to break it.")]
        public float breakSpeed = 4f;
        [Min(0f), Tooltip("m/s each piece is pushed out from the centre when it breaks.")]
        public float explosion = 1.5f;
        [Tooltip("Break() and ResetFracture() reach every player, late joiners included. Off: only the player who called it.")]
        public bool networked = true;

        [Header("Baked (⋮ → Bake Fracture)")]
        [Range(2, 64), Tooltip("Number of pieces (Blender Source Limit). Rebake after changing.")]
        public int pieceCount = 12;
        [Tooltip("Different random cuts. Rebake after changing.")]
        public int seed;
        [Tooltip("Material on the cut faces. Empty: the mesh's own material.")]
        public Material interiorMaterial;
        public Transform piecesRoot;
        public Rigidbody[] pieces;

        Vector3[] restPos;
        Quaternion[] restRot;
        Renderer rend;
        Collider[] cols;
        Rigidbody body;
        bool broken;
        [UdonSynced] bool syncedBroken;

        void Start()
        {
            if (piecesRoot == null || pieces == null || pieces.Length == 0)
            { Debug.LogWarning("[BlendUdonFracture] Not baked: use ⋮ → Bake Fracture.", this); enabled = false; return; }
            rend = GetComponent<Renderer>();
            cols = GetComponents<Collider>();
            body = GetComponent<Rigidbody>();
            restPos = new Vector3[pieces.Length];
            restRot = new Quaternion[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                if (pieces[i] == null) continue;
                restPos[i] = pieces[i].transform.localPosition;
                restRot[i] = pieces[i].transform.localRotation;
            }
            piecesRoot.gameObject.SetActive(false);
            if (networked) OnDeserialization(); // late joiner: already broken
        }

        void OnCollisionEnter(Collision collision)
        {
            if (!breakOnImpact || broken || restPos == null) return;
            if (collision.relativeVelocity.magnitude < breakSpeed) return;
#if BLEND_UDON
            if (networked && !Networking.IsOwner(gameObject)) return; // the owner tells everyone
#endif
            Break();
        }

        public void Break()
        {
            BreakLocal();
            Share(true);
        }

        public void ResetFracture()
        {
            ResetLocal();
            Share(false);
        }

        void Share(bool on)
        {
            if (!networked) return;
#if BLEND_UDON
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
#endif
            syncedBroken = on;
        }

        public override void OnDeserialization()
        {
            if (restPos == null || syncedBroken == broken) return;
            if (syncedBroken) BreakLocal();
            else ResetLocal();
        }

        public void BreakLocal()
        {
            if (broken || restPos == null) return;
            broken = true;
#if BLEND_UDON
            VRCPickup pickup = (VRCPickup)GetComponent(typeof(VRCPickup));
            if (pickup != null) { pickup.Drop(); pickup.pickupable = false; }
#endif
            Vector3 vel = Vector3.zero, ang = Vector3.zero;
            if (body != null) { vel = body.velocity; ang = body.angularVelocity; body.isKinematic = true; }
            if (rend != null) rend.enabled = false;
            for (int i = 0; i < cols.Length; i++) if (cols[i] != null) cols[i].enabled = false;

            Vector3 centre = transform.position;
            piecesRoot.gameObject.SetActive(true);
            for (int i = 0; i < pieces.Length; i++)
            {
                Rigidbody p = pieces[i];
                if (p == null) continue;
                Transform t = p.transform;
                t.SetParent(null, true); // free of the (now hidden) object
                Vector3 c = p.worldCenterOfMass;
                Vector3 outward = c - centre;
                outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : Random.onUnitSphere;
                p.isKinematic = false;
                p.velocity = vel + Vector3.Cross(ang, c - centre) + outward * explosion;
                p.angularVelocity = ang;
            }
        }

        public void ResetLocal()
        {
            if (!broken || restPos == null) return;
            broken = false;
            for (int i = 0; i < pieces.Length; i++)
            {
                Rigidbody p = pieces[i];
                if (p == null) continue;
                p.velocity = Vector3.zero;
                p.angularVelocity = Vector3.zero;
                p.isKinematic = true;
                Transform t = p.transform;
                t.SetParent(piecesRoot, false);
                t.localPosition = restPos[i];
                t.localRotation = restRot[i];
            }
            piecesRoot.gameObject.SetActive(false);
            if (rend != null) rend.enabled = true;
            for (int i = 0; i < cols.Length; i++) if (cols[i] != null) cols[i].enabled = true;
            if (body != null) { body.isKinematic = false; body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
#if BLEND_UDON
            VRCPickup pickup = (VRCPickup)GetComponent(typeof(VRCPickup));
            if (pickup != null) pickup.pickupable = true;
#endif
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Fracture")]
        void Bake()
        {
            var src = GetComponent<MeshFilter>().sharedMesh;
            if (src == null) { Debug.LogError("[BlendUdonFracture] No mesh.", this); return; }
            if (!src.isReadable) { Debug.LogError("[BlendUdonFracture] Enable Read/Write on the mesh import settings.", this); return; }
            var meshes = BlendFracture.Cut(src, pieceCount, seed, out var centres);
            if (meshes.Count == 0) { Debug.LogError("[BlendUdonFracture] No pieces: is the mesh closed?", this); return; }

            Unbake();
            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_fracture.asset");
            UnityEditor.AssetDatabase.CreateAsset(meshes[0], path);
            for (int i = 1; i < meshes.Count; i++) UnityEditor.AssetDatabase.AddObjectToAsset(meshes[i], path);
            UnityEditor.AssetDatabase.SaveAssets();

            var r = GetComponent<Renderer>();
            var mats = new System.Collections.Generic.List<Material>(r != null ? r.sharedMaterials : new Material[0]);
            while (mats.Count < src.subMeshCount) mats.Add(mats.Count > 0 ? mats[mats.Count - 1] : null);
            mats.Add(interiorMaterial != null ? interiorMaterial : (mats.Count > 0 ? mats[0] : null));
            var ownBody = GetComponent<Rigidbody>();
            float totalMass = ownBody != null ? ownBody.mass : 1f;
            float total = 0f;
            foreach (var m in meshes) total += m.bounds.size.x * m.bounds.size.y * m.bounds.size.z;

            var root = new GameObject("Fracture Pieces");
            UnityEditor.Undo.RegisterCreatedObjectUndo(root, "Bake Fracture");
            root.transform.SetParent(transform, false);
            var bodies = new Rigidbody[meshes.Count];
            for (int i = 0; i < meshes.Count; i++)
            {
                var go = new GameObject($"Piece {i}");
                go.layer = gameObject.layer;
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = centres[i];
                go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = meshes[i];
                mc.convex = true;
                var rb = go.AddComponent<Rigidbody>();
                var b = meshes[i].bounds;
                rb.mass = Mathf.Max(0.01f, totalMass * b.size.x * b.size.y * b.size.z / Mathf.Max(total, 1e-9f));
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.isKinematic = true;
                bodies[i] = rb;
            }
            root.SetActive(false);

            UnityEditor.Undo.RecordObject(this, "Bake Fracture");
            piecesRoot = root.transform;
            pieces = bodies;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonFracture] Baked {meshes.Count} pieces to {path}.", this);
        }

        [ContextMenu("Unbake Fracture")]
        void Unbake()
        {
            if (piecesRoot != null)
            {
                var mf = piecesRoot.GetComponentInChildren<MeshFilter>(true);
                string old = mf != null && mf.sharedMesh != null ? UnityEditor.AssetDatabase.GetAssetPath(mf.sharedMesh) : null;
                UnityEditor.Undo.DestroyObjectImmediate(piecesRoot.gameObject);
                if (!string.IsNullOrEmpty(old) && old.StartsWith("Assets/BlendProperty/Baked/")) UnityEditor.AssetDatabase.DeleteAsset(old);
            }
            UnityEditor.Undo.RecordObject(this, "Unbake Fracture");
            piecesRoot = null;
            pieces = new Rigidbody[0];
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
