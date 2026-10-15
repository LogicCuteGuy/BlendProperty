#if BLEND_UDON
using UdonSharp;
using VRC.SDKBase;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
using VRCShader = UnityEngine.Shader;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Blender Hair with Hair Dynamics on the GPU: Number strands grow from this mesh's surface along its normals and
    /// swing with gravity, Force Fields and the object's motion, keeping their shape by Stiffness and stopping at
    /// Sphere, Capsule and Box colliders (add this object's own collider so hair lies on it instead of passing through).
    /// Drawn by the "Hair" renderer the bake makes (shader Blend Property/Hair), so thousands of strands are fine and
    /// Udon only sends a few settings a frame. Use ⋮ → Bake Hair after changing Number, Segments, Hair Length or the
    /// mesh. Local only (not synced).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/Hair.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Hair")]
    public class BlendUdonHair : UdonSharpBehaviour
    {
        [Tooltip("Run Hair Dynamics. Off: the strands stay in their rest shape and move rigidly with the object.")]
        public bool simulate = true;
        [Tooltip("Skip simulating while no camera sees the hair.")]
        public bool onlyWhenVisible = true;
        [Range(1, 32), Tooltip("GPU steps per 1/60 s. More = stiffer and better collisions.")]
        public int substeps = 4;
        [Tooltip("Metres per second squared, world space.")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        [Tooltip("Constant acceleration, world space (cheap wind).")]
        public Vector3 wind;
        [Tooltip("Blender Force Fields acting on the hair (up to 4).")]
        public BlendUdonForceField[] forceFields;
        [Range(0, 1), Tooltip("Spring back toward the rest shape (Blender Stiffness): 0 = limp, 0.3 = fur that stands up, 1 = stiff as combed.")]
        public float stiffness = 0.3f;
        [Range(0, 1), Tooltip("Velocity lost per 1/30 s, like Blender's Air Damping.")]
        public float airDamping = 0.05f;
        [Header("Collision")]
        public SphereCollider[] sphereColliders;
        public CapsuleCollider[] capsuleColliders;
        public BoxCollider[] boxColliders;
        [Tooltip("Metres kept between the hair and colliders.")]
        public float collisionDistance = 0.005f;
        [Header("Display")]
        [Min(0f), Tooltip("Strand width at the root, metres (Blender Diameter Root). Colours are on the material.")]
        public float rootWidth = 0.004f;
        [Min(0f), Tooltip("Strand width at the tip, metres (Blender Tip).")]
        public float tipWidth = 0.001f;
        [Tooltip("Draws the strands (made by the bake), with a Blend Property/Hair material.")]
        public Renderer hairRenderer;

        [Header("Baked (⋮ → Bake Hair)")]
        [Min(1), Tooltip("Strands (Blender Number). Rebake after changing.")]
        public int number = 2000;
        [Range(1, 15), Tooltip("Points per strand minus one (Blender Segments). Rebake after changing.")]
        public int segments = 6;
        [Min(0.001f), Tooltip("Metres, at scale 1 (Blender Hair Length). Rebake after changing.")]
        public float hairLength = 0.15f;
        public CustomRenderTexture state;

        Material sim, draw;
        Vector4[] spheres, capA, capB, boxC, boxX, boxY, boxZ, fieldA, fieldB, fieldC;
        int resetFrames;
        float pending; // seconds not simulated yet
        Vector3 origin;
        int idOrigin, idObjectToWorld, idAccel, idKeep, idH2, idStiffness, idReset, idDistance, idRootWidth, idTipWidth,
            idSpheres, idSphereCount, idCapA, idCapB, idCapsuleCount, idBoxC, idBoxX, idBoxY, idBoxZ, idBoxCount,
            idFieldA, idFieldB, idFieldC, idFieldCount;

        void Start()
        {
            if (state == null || hairRenderer == null)
            {
                Debug.LogWarning("[BlendUdonHair] Not baked: use ⋮ → Bake Hair.", this);
                enabled = false;
                return;
            }
            sim = state.material;
            draw = hairRenderer.material;
            draw.SetTexture("_State", state); // any Blend Property/Hair material works
            draw.SetVector("_StateSize", new Vector4(state.width, state.height, 0f, 0f));
            draw.SetFloat("_P", sim.GetFloat("_P")); // baked count, even if Segments changed since
            spheres = new Vector4[8]; capA = new Vector4[8]; capB = new Vector4[8];
            boxC = new Vector4[8]; boxX = new Vector4[8]; boxY = new Vector4[8]; boxZ = new Vector4[8];
            fieldA = new Vector4[4]; fieldB = new Vector4[4]; fieldC = new Vector4[4];
            idObjectToWorld = VRCShader.PropertyToID("_ObjectToWorld");
            idOrigin = VRCShader.PropertyToID("_Origin");
            idAccel = VRCShader.PropertyToID("_Accel");
            idKeep = VRCShader.PropertyToID("_Keep");
            idH2 = VRCShader.PropertyToID("_H2");
            idStiffness = VRCShader.PropertyToID("_Spring");
            idReset = VRCShader.PropertyToID("_Reset");
            idDistance = VRCShader.PropertyToID("_Distance");
            idRootWidth = VRCShader.PropertyToID("_RootWidth");
            idTipWidth = VRCShader.PropertyToID("_TipWidth");
            idSpheres = VRCShader.PropertyToID("_Spheres");
            idSphereCount = VRCShader.PropertyToID("_SphereCount");
            idCapA = VRCShader.PropertyToID("_CapsuleA");
            idCapB = VRCShader.PropertyToID("_CapsuleB");
            idCapsuleCount = VRCShader.PropertyToID("_CapsuleCount");
            idBoxC = VRCShader.PropertyToID("_BoxC");
            idBoxX = VRCShader.PropertyToID("_BoxX");
            idBoxY = VRCShader.PropertyToID("_BoxY");
            idBoxZ = VRCShader.PropertyToID("_BoxZ");
            idBoxCount = VRCShader.PropertyToID("_BoxCount");
            idFieldA = VRCShader.PropertyToID("_FieldA");
            idFieldB = VRCShader.PropertyToID("_FieldB");
            idFieldC = VRCShader.PropertyToID("_FieldC");
            idFieldCount = VRCShader.PropertyToID("_FieldCount");
            ResetSimulation();
        }

        /// <summary>Snap every strand back to its rest shape and drop all motion.</summary>
        public void ResetSimulation() { resetFrames = 2; } // both buffers

        // PostLateUpdate: after Animators, IK and pickups moved this object, so the roots match this frame
        public override void PostLateUpdate()
        {
            draw.SetFloat(idRootWidth, rootWidth);
            draw.SetFloat(idTipWidth, tipWidth);
            bool rest = !simulate || resetFrames > 0;
            if (!rest && onlyWhenVisible && !hairRenderer.isVisible) return;

            // fixed-size steps: Verlet takes last step's motion as velocity, so a shorter step after a long one
            // would speed the hair up; uneven frames kept feeding it energy
            int perTick = Mathf.Max(1, substeps);
            float h = 1f / (60f * perTick);
            int n = 1;
            if (rest) pending = 0f;
            else
            {
                pending += Mathf.Min(Time.deltaTime, 0.1f);
                n = (int)(pending / h);
                if (n > 4 * perTick) { n = 4 * perTick; pending = 0f; } // a long hitch: slow down rather than pile up
                else pending -= n * h;
                if (n == 0) return;
            }
            // everything relative to the object's position, so the GPU works with small numbers (see the sim shader)
            Vector3 o = transform.position;
            origin = o;
            Matrix4x4 m = transform.localToWorldMatrix;
            m.m03 = 0f; m.m13 = 0f; m.m23 = 0f;
            sim.SetMatrix(idObjectToWorld, m);
            sim.SetVector(idOrigin, o);
            sim.SetFloat(idReset, rest ? 1f : 0f);
            sim.SetVector(idAccel, (gravity + wind) * (h * h));
            sim.SetFloat(idH2, h * h);
            sim.SetFloat(idKeep, Mathf.Pow(1f - airDamping, h * 30f));
            // a spring of 80 * stiffness rad/s: same sag at any substep count (1 = about 2 mm, 0.1 = about 10 cm on 15 cm hair)
            float w = 80f * stiffness;
            sim.SetFloat(idStiffness, w * w * h * h);
            sim.SetFloat(idDistance, collisionDistance);
            SetColliders();
            SetFields();
            if (resetFrames > 0) resetFrames--;
            state.Update(n);
        }

        void SetFields()
        {
            int count = 0;
            if (forceFields != null)
                for (int i = 0; i < forceFields.Length && count < 4; i++)
                {
                    BlendUdonForceField f = forceFields[i];
                    if (f == null || !f.enabled || !f.gameObject.activeInHierarchy) continue;
                    fieldA[count] = f.packA; fieldB[count] = f.packB; fieldC[count] = f.packC;
                    count++;
                }
            sim.SetVectorArray(idFieldA, fieldA);
            sim.SetVectorArray(idFieldB, fieldB);
            sim.SetVectorArray(idFieldC, fieldC);
            sim.SetFloat(idFieldCount, count);
        }

        // same packing as BlendUdonCloth
        // only colliders within 1 m of this simulation take its 8 slots (with every player's body there are many)
        bool Near(Collider c)
        {
            if (hairRenderer == null) return true;
            Bounds b = new Bounds(hairRenderer.bounds.center, hairRenderer.bounds.size + Vector3.one * 2f); // 1 m around it (Expand() on a copy is lost in Udon)
            return b.Intersects(c.bounds);
        }

        void SetColliders()
        {
            int count = 0;
            if (sphereColliders != null)
                for (int i = 0; i < sphereColliders.Length && count < 8; i++)
                {
                    SphereCollider c = sphereColliders[i];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !Near(c)) continue;
                    Vector3 cs = c.transform.lossyScale;
                    float r = c.radius * Mathf.Max(Mathf.Abs(cs.x), Mathf.Max(Mathf.Abs(cs.y), Mathf.Abs(cs.z)));
                    Vector3 p = c.transform.TransformPoint(c.center);
                    p -= origin;
                    spheres[count++] = new Vector4(p.x, p.y, p.z, r);
                }
            sim.SetVectorArray(idSpheres, spheres);
            sim.SetFloat(idSphereCount, count);

            count = 0;
            if (capsuleColliders != null)
                for (int i = 0; i < capsuleColliders.Length && count < 8; i++)
                {
                    CapsuleCollider c = capsuleColliders[i];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !Near(c)) continue;
                    Vector3 cs = c.transform.lossyScale;
                    Vector3 axis = c.direction == 0 ? Vector3.right : (c.direction == 1 ? Vector3.up : Vector3.forward);
                    float axisScale = c.direction == 0 ? Mathf.Abs(cs.x) : (c.direction == 1 ? Mathf.Abs(cs.y) : Mathf.Abs(cs.z));
                    float r = c.radius * Mathf.Max(Mathf.Abs(cs.x), Mathf.Max(Mathf.Abs(cs.y), Mathf.Abs(cs.z)));
                    float half = Mathf.Max(0f, c.height * axisScale * 0.5f - r) / Mathf.Max(axisScale, 1e-6f);
                    Vector3 a = c.transform.TransformPoint(c.center + axis * half) - origin;
                    Vector3 b = c.transform.TransformPoint(c.center - axis * half) - origin;
                    capA[count] = new Vector4(a.x, a.y, a.z, r);
                    capB[count] = new Vector4(b.x, b.y, b.z, 0f);
                    count++;
                }
            sim.SetVectorArray(idCapA, capA);
            sim.SetVectorArray(idCapB, capB);
            sim.SetFloat(idCapsuleCount, count);

            count = 0;
            if (boxColliders != null)
                for (int i = 0; i < boxColliders.Length && count < 8; i++)
                {
                    BoxCollider c = boxColliders[i];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !Near(c)) continue;
                    Transform t = c.transform;
                    Vector3 cs = t.lossyScale;
                    Vector3 p = t.TransformPoint(c.center) - origin;
                    boxC[count] = new Vector4(p.x, p.y, p.z, 0f);
                    boxX[count] = Axis(t.right, c.size.x * Mathf.Abs(cs.x));
                    boxY[count] = Axis(t.up, c.size.y * Mathf.Abs(cs.y));
                    boxZ[count] = Axis(t.forward, c.size.z * Mathf.Abs(cs.z));
                    count++;
                }
            sim.SetVectorArray(idBoxC, boxC);
            sim.SetVectorArray(idBoxX, boxX);
            sim.SetVectorArray(idBoxY, boxY);
            sim.SetVectorArray(idBoxZ, boxZ);
            sim.SetFloat(idBoxCount, count);
        }

        // world axis, w = half the world size along it
        Vector4 Axis(Vector3 a, float size) { return new Vector4(a.x, a.y, a.z, size * 0.5f); }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Hair")]
        void Bake()
        {
            var src = GetComponent<MeshFilter>().sharedMesh;
            if (src == null) { Debug.LogError("[BlendUdonHair] No mesh.", this); return; }
            if (!src.isReadable) { Debug.LogError("[BlendUdonHair] Enable Read/Write on the mesh import settings.", this); return; }
            var verts = src.vertices;
            var norms = src.normals;
            if (norms.Length != verts.Length) norms = null;
            int[] tris = src.triangles;
            var cdf = new float[tris.Length / 3];
            float area = 0f;
            for (int f = 0; f < cdf.Length; f++)
            {
                Vector3 a = verts[tris[f * 3]], b = verts[tris[f * 3 + 1]], c = verts[tris[f * 3 + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude;
                cdf[f] = area;
            }
            if (area <= 0f) { Debug.LogError("[BlendUdonHair] The mesh has no triangles to grow hair from.", this); return; }

            int N = Mathf.Max(1, number), P = Mathf.Clamp(segments, 1, 15) + 1;
            int G = Mathf.CeilToInt(Mathf.Sqrt(N)), H = Mathf.CeilToInt(N / (float)G); // G strands per row
            int HW = G * P;
            if (HW * 2 + 1 > 16384) { Debug.LogError("[BlendUdonHair] Too many strands for this many segments.", this); return; }

            // straight strands along the surface normal, area-weighted random roots; length is local, so object scale scales it
            Vector3 s = transform.lossyScale;
            float localLen = hairLength / Mathf.Max(1e-6f, (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f);
            var rng = new System.Random(12345);
            var restPx = new Color[HW * H];
            var rand = new float[N];
            for (int k = 0; k < N; k++)
            {
                int f = System.Array.BinarySearch(cdf, (float)rng.NextDouble() * area);
                if (f < 0) f = ~f;
                f = Mathf.Min(f, cdf.Length - 1);
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                float u = (float)rng.NextDouble(), w = (float)rng.NextDouble();
                if (u + w > 1f) { u = 1f - u; w = 1f - w; }
                Vector3 p = verts[i0] + (verts[i1] - verts[i0]) * u + (verts[i2] - verts[i0]) * w;
                Vector3 nrm = norms != null ? norms[i0] * (1f - u - w) + norms[i1] * u + norms[i2] * w
                                            : Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                nrm = nrm.sqrMagnitude > 0f ? nrm.normalized : Vector3.up;
                float len = localLen * (0.85f + 0.3f * (float)rng.NextDouble()); // a little uneven, like real hair
                rand[k] = (float)rng.NextDouble();
                int bx = k % G, y = k / G;
                for (int j = 0; j < P; j++)
                {
                    Vector3 q = p + nrm * (len * j / (P - 1));
                    restPx[y * HW + bx * P + j] = new Color(q.x, q.y, q.z, 0f);
                }
            }
            var rest = new Texture2D(HW, H, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Rest" };
            rest.SetPixels(restPx); rest.Apply(false, false);

            var simMat = new Material(Shader.Find("Hidden/Blend Property/Hair Sim")) { name = "Sim" };
            simMat.SetTexture("_Rest", rest);
            simMat.SetFloat("_P", P);
            simMat.SetFloat("_HW", HW);
            simMat.SetFloat("_H", H);
            simMat.SetFloat("_Count", N);
            var stateTex = new CustomRenderTexture(HW * 2 + 1, H, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "State", material = simMat, shaderPass = 0, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };

            // one ribbon per strand: two vertices per row, 2 rows per segment; the vertex shader places them
            int rows = 2 * (P - 1) + 1;
            var vtx = new Vector3[N * rows * 2];
            var uv = new Vector2[vtx.Length];
            var data = new System.Collections.Generic.List<Vector3>(vtx.Length);
            var idx = new int[N * (rows - 1) * 6];
            int t = 0;
            for (int k = 0; k < N; k++)
            {
                var d = new Vector3((k % G) * P, k / G, rand[k]);
                int b = k * rows * 2;
                for (int r = 0; r < rows; r++)
                {
                    float along = r * 0.5f;
                    uv[b + r * 2] = new Vector2(0f, along);
                    uv[b + r * 2 + 1] = new Vector2(1f, along);
                    data.Add(d); data.Add(d);
                    if (r == rows - 1) continue;
                    int v0 = b + r * 2;
                    idx[t++] = v0; idx[t++] = v0 + 2; idx[t++] = v0 + 1;
                    idx[t++] = v0 + 1; idx[t++] = v0 + 2; idx[t++] = v0 + 3;
                }
            }
            var ribbons = new Mesh { name = "Hair", indexFormat = vtx.Length > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            ribbons.vertices = vtx;
            ribbons.uv = uv;
            ribbons.SetUVs(1, data);
            ribbons.triangles = idx;
            // ponytail: culling box = the mesh grown by two hair lengths; enlarge it if swinging hair pops out of view
            var bounds = src.bounds;
            bounds.Expand(localLen * 4f);
            ribbons.bounds = bounds;

            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = state != null && UnityEditor.AssetDatabase.Contains(state)
                ? UnityEditor.AssetDatabase.GetAssetPath(state)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_hair.asset");

            // the renderer: reuse it (and its material), or make a child with a default material
            Material drawMat = hairRenderer != null ? hairRenderer.sharedMaterial : null;
            bool ownMat = drawMat == null || UnityEditor.AssetDatabase.GetAssetPath(drawMat) == path;
            if (ownMat) drawMat = new Material(Shader.Find("Blend Property/Hair")) { name = "Hair" };
            drawMat.SetTexture("_State", stateTex);
            drawMat.SetVector("_StateSize", new Vector4(HW * 2 + 1, H, 0f, 0f));
            drawMat.SetFloat("_P", P);

            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(stateTex, path);
            var parts = ownMat ? new Object[] { simMat, rest, ribbons, drawMat } : new Object[] { simMat, rest, ribbons };
            foreach (Object o in parts) UnityEditor.AssetDatabase.AddObjectToAsset(o, path);
            stateTex.material = simMat; // written before its material was an asset: link again so it sticks
            UnityEditor.EditorUtility.SetDirty(stateTex);
            foreach (Object o in parts) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();

            Renderer rend = hairRenderer;
            if (rend == null)
            {
                var go = new GameObject("Hair");
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Bake Hair");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>();
                rend = go.AddComponent<MeshRenderer>();
            }
            UnityEditor.Undo.RecordObject(rend, "Bake Hair");
            UnityEditor.Undo.RecordObject(rend.GetComponent<MeshFilter>(), "Bake Hair");
            rend.GetComponent<MeshFilter>().sharedMesh = ribbons;
            rend.sharedMaterial = drawMat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            UnityEditor.Undo.RecordObject(this, "Bake Hair");
            state = stateTex;
            hairRenderer = rend;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonHair] Baked {N} strands of {P - 1} segments to {path}.", this);
        }
#endif
    }
}
