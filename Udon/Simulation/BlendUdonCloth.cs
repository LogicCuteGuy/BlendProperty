#if BLEND_UDON
using UdonSharp;
using VRC.SDK3.Rendering;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;
#else
using VRCAsyncGPUReadback = BlendProperty.Standalone.BlendReadback;
using VRCAsyncGPUReadbackRequest = BlendProperty.Standalone.BlendReadbackRequest;
using IUdonEventReceiver = BlendProperty.Standalone.BlendBehaviour;
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
using VRCShader = UnityEngine.Shader;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Runtime Blender Cloth / Soft Body simulated on the GPU (Custom Render Textures), for big or many cloths.
    /// Udon only sends a few settings a frame and copies the finished vertices back into the mesh, so the mesh keeps
    /// its own material and works with any shader. Pinning uses the vertex color red channel
    /// (1 = pinned, in between = soft body goal), or <see cref="goal"/> when the mesh has no vertex colors.
    /// <see cref="shape"/> > 0 makes a closed mesh a jelly that keeps its shape (shape matching); with no goal or pinned
    /// vertices it is a free body that falls, tumbles and can be picked up, and the transform follows it.
    /// Colliders: Sphere, Capsule and Box (up to 8 each). The mesh shows the result one frame late.
    /// Use ⋮ → Bake Cloth after changing the mesh, pinning or Goal. One Blend Udon deformer per mesh.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/Cloth.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Cloth")]
    public class BlendUdonCloth : UdonSharpBehaviour
    {
        [Tooltip("Run the simulation. Off (default): the mesh keeps its rest shape until turned on, e.g. from BlendUdonClothPanel.")]
        public bool simulate;
        [Tooltip("Skip simulating while no camera sees the mesh.")]
        public bool onlyWhenVisible = true;
        [Range(1, 64), Tooltip("GPU steps per 1/60 s, all the same size whatever the frame rate. Each spreads corrections one link further: more = stiffer, still cheap.")]
        public int substeps = 16;
        [Tooltip("Metres per second squared, world space.")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        [Tooltip("Constant acceleration, world space (cheap wind).")]
        public Vector3 wind;
        [Tooltip("Blender Force Fields acting on this cloth (up to 4).")]
        public BlendUdonForceField[] forceFields;
        [Range(0, 1), Tooltip("Velocity lost per 1/30 s, like Blender's Air Viscosity.")]
        public float airDamping = 0.02f;
        [Range(0, 1), Tooltip("Edge stiffness (Blender Tension / Structural).")]
        public float structural = 1f;
        [Range(0, 1), Tooltip("Resistance to folding (Blender Bending).")]
        public float bending = 0.3f;
        [Range(0, 1), Tooltip("How hard goal vertices are pulled back to their rest shape (Blender Goal Stiffness).")]
        public float goalStiffness = 0.5f;
        [Range(0, 1), Tooltip("Goal weight when the mesh has no vertex colors (baked). 0 = free cloth, 1 = pinned.")]
        public float goal;
        [Header("Soft Body")]
        [Range(0, 1), Tooltip("Pull toward the rest shape per 1/30 s, moved and turned to fit (shape matching): 0 = cloth, 1 = rigid. " +
                              "Needs a bake made with this version.")]
        public float shape;
        [Header("Collision")]
        public SphereCollider[] sphereColliders;
        public CapsuleCollider[] capsuleColliders;
        public BoxCollider[] boxColliders;
        [Tooltip("Metres kept between the cloth and colliders (Blender Collision Distance).")]
        public float collisionDistance = 0.015f;
        [Range(0, 1), Tooltip("Sliding lost per 1/30 s while touching a collider.")]
        public float friction;
        public bool recalculateNormals = true;

        [Header("Baked (⋮ → Bake Cloth)")]
        public CustomRenderTexture state;
        public CustomRenderTexture vertexTexture;
        public int vertexCount;
        [Tooltip("Baked with the Soft Body zones.")]
        public bool softBodyBaked;
        [Tooltip("No goal or pinned vertices: with Shape, the object moves with the body.")]
        public bool freeBody;
        public Vector3 restCom;

        Mesh mesh;
        Renderer rend;
        Material sim;
        Vector3[] work;
        Color[] readback;
        Vector4[] spheres, capA, capB, boxC, boxX, boxY, boxZ, fieldA, fieldB, fieldC;
        int resetFrames;
        int inFlight, stale;
        bool fresh;
        Vector3 startPos, setPos;
        // the simulation works relative to this (where the object was at the last reset): small numbers keep float precision.
        // ponytail: fixed until the next reset, so a body carried far away slowly loses precision again; re-anchor if that shows
        Vector3 origin;
        float pending;  // seconds not simulated yet
        Quaternion startRot, setRot;
        int idShape, idFriction, idScaleV, idRef, idHeld, idHeldCom, idHeldRot, idBoxC, idBoxX, idBoxY, idBoxZ, idBoxCount;
        int idObjectToWorld, idAccel, idKeep, idGoal, idStructural, idBending, idScale, idReset,
            idDistance, idSpheres, idSphereCount, idCapA, idCapB, idCapsuleCount, idFieldA, idFieldB, idFieldC, idFieldCount, idH2;

        void Start()
        {
            if (state == null || vertexTexture == null)
            {
                Debug.LogWarning("[BlendUdonCloth] Not baked: use ⋮ → Bake Cloth.", this);
                enabled = false;
                return;
            }
            mesh = GetComponent<MeshFilter>().mesh;
            mesh.MarkDynamic();
            rend = GetComponent<Renderer>();
            sim = state.material;
            work = new Vector3[vertexCount];
            readback = new Color[vertexTexture.width * vertexTexture.height];
            spheres = new Vector4[8]; capA = new Vector4[8]; capB = new Vector4[8];
            boxC = new Vector4[8]; boxX = new Vector4[8]; boxY = new Vector4[8]; boxZ = new Vector4[8];
            idShape = VRCShader.PropertyToID("_ShapeK");
            idFriction = VRCShader.PropertyToID("_Friction");
            idScaleV = VRCShader.PropertyToID("_ScaleV");
            idRef = VRCShader.PropertyToID("_Ref");
            idHeld = VRCShader.PropertyToID("_Held");
            idHeldCom = VRCShader.PropertyToID("_HeldCom");
            idHeldRot = VRCShader.PropertyToID("_HeldRot");
            idBoxC = VRCShader.PropertyToID("_BoxC");
            idBoxX = VRCShader.PropertyToID("_BoxX");
            idBoxY = VRCShader.PropertyToID("_BoxY");
            idBoxZ = VRCShader.PropertyToID("_BoxZ");
            idBoxCount = VRCShader.PropertyToID("_BoxCount");
            fieldA = new Vector4[4]; fieldB = new Vector4[4]; fieldC = new Vector4[4];
            idFieldA = VRCShader.PropertyToID("_FieldA");
            idFieldB = VRCShader.PropertyToID("_FieldB");
            idFieldC = VRCShader.PropertyToID("_FieldC");
            idFieldCount = VRCShader.PropertyToID("_FieldCount");
            idH2 = VRCShader.PropertyToID("_H2");
            idObjectToWorld = VRCShader.PropertyToID("_ObjectToWorld");
            idAccel = VRCShader.PropertyToID("_Accel");
            idKeep = VRCShader.PropertyToID("_Keep");
            idGoal = VRCShader.PropertyToID("_GoalStiffness");
            idStructural = VRCShader.PropertyToID("_Structural");
            idBending = VRCShader.PropertyToID("_Bending");
            idScale = VRCShader.PropertyToID("_Scale");
            idReset = VRCShader.PropertyToID("_Reset");
            idDistance = VRCShader.PropertyToID("_Distance");
            idSpheres = VRCShader.PropertyToID("_Spheres");
            idSphereCount = VRCShader.PropertyToID("_SphereCount");
            idCapA = VRCShader.PropertyToID("_CapsuleA");
            idCapB = VRCShader.PropertyToID("_CapsuleB");
            idCapsuleCount = VRCShader.PropertyToID("_CapsuleCount");
            startPos = transform.position;
            startRot = transform.rotation;
            ResetSimulation();
        }

        bool Free() { return shape > 0f && freeBody && softBodyBaked; }

        /// <summary>Snap back to the rest shape and drop all motion. A free soft body also goes back to where it started.</summary>
        public void ResetSimulation()
        {
            if (sim == null) return; // before Start
            resetFrames = 2;
            // readbacks already on their way hold the old shape, and so does this frame's: the vertex texture reads the
            // state a frame late. One that landed but isn't applied yet is dropped too.
            stale = inFlight + 1;
            fresh = false;
            if (Free()) transform.SetPositionAndRotation(startPos, startRot);
            setPos = transform.position;
            setRot = transform.rotation;
            origin = transform.position;
            pending = 0f;
        }

        // PostLateUpdate: after Animators, IK and pickups moved this object, so pins and the mesh match this frame
        public override void PostLateUpdate()
        {
            // a free body that something else (pickup, script) moved since last frame is held: it follows the transform
            bool free = Free();
            bool held = free && ((transform.position - setPos).sqrMagnitude > 1e-10f ||
                                 Mathf.Abs(Quaternion.Dot(transform.rotation, setRot)) < 0.9999999f);
            if (fresh)
            {
                fresh = false;
                ApplyReadback(free && simulate && !held && resetFrames == 0);
            }
            setPos = transform.position;
            setRot = transform.rotation;
            if (!simulate) return;
            if (onlyWhenVisible && !rend.isVisible && resetFrames == 0) return;

            // fixed-size steps: Verlet takes last step's motion as velocity, so a shorter step after a long one
            // changes the speed; uneven frames kept feeding the cloth energy and a resting jelly never settled
            int perTick = Mathf.Max(1, substeps);
            float h = 1f / (60f * perTick);
            int n = 1;
            if (resetFrames == 0)
            {
                pending += Mathf.Min(Time.deltaTime, 0.1f);
                n = (int)(pending / h);
                if (n > 4 * perTick) { n = 4 * perTick; pending = 0f; } // a long hitch: slow down rather than pile up
                else pending -= n * h;
                if (n == 0) return;
            }
            Vector3 s = transform.lossyScale;
            Matrix4x4 m = transform.localToWorldMatrix;
            Vector3 o = origin;
            m.m03 -= o.x; m.m13 -= o.y; m.m23 -= o.z;
            sim.SetMatrix(idObjectToWorld, m);
            sim.SetVector(idAccel, (gravity + wind) * (h * h));
            sim.SetFloat(idKeep, Mathf.Pow(1f - airDamping, h * 30f));
            sim.SetFloat(idGoal, goalStiffness);
            sim.SetFloat(idStructural, structural);
            sim.SetFloat(idBending, bending);
            sim.SetFloat(idScale, (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f);
            sim.SetFloat(idReset, resetFrames > 0 ? 1f : 0f);
            sim.SetFloat(idDistance, collisionDistance);
            // per 1/30 s, so the values feel the same at any substep count
            sim.SetFloat(idFriction, 1f - Mathf.Pow(1f - friction, h * 30f));
            sim.SetFloat(idShape, softBodyBaked && shape > 0f ? 1f - Mathf.Pow(1f - shape, h * 30f) : 0f);
            sim.SetVector(idScaleV, s);
            sim.SetVector(idRef, transform.position - o);
            sim.SetFloat(idHeld, free && (held || resetFrames > 0) ? 1f : 0f);
            sim.SetVector(idHeldCom, transform.TransformPoint(restCom) - o);
            Quaternion r = transform.rotation;
            sim.SetVector(idHeldRot, new Vector4(r.x, r.y, r.z, r.w));
            SetColliders();
            SetFields(h * h);
            if (resetFrames > 0) resetFrames--;
            state.Update(n);

            vertexTexture.Update(1);
            if (inFlight < 3) // one request a frame keeps the mesh updating every frame; the cap stops a pile-up
            {
                inFlight++;
                VRCAsyncGPUReadback.Request(vertexTexture, 0, TextureFormat.RGBAFloat, (IUdonEventReceiver)this);
            }
        }

        void SetFields(float h2)
        {
            int count = 0;
            if (forceFields != null)
                for (int i = 0; i < forceFields.Length && count < 4; i++)
                {
                    BlendUdonForceField f = forceFields[i];
                    if (f == null || !f.enabled || !f.gameObject.activeInHierarchy) continue;
                    Vector4 a = f.packA;
                    fieldA[count] = new Vector4(a.x - origin.x, a.y - origin.y, a.z - origin.z, a.w);
                    fieldB[count] = f.packB; fieldC[count] = f.packC;
                    count++;
                }
            sim.SetVectorArray(idFieldA, fieldA);
            sim.SetVectorArray(idFieldB, fieldB);
            sim.SetVectorArray(idFieldC, fieldC);
            sim.SetFloat(idFieldCount, count);
            sim.SetFloat(idH2, h2);
        }

        // only colliders within 1 m of this simulation take its 8 slots (with every player's body there are many)
        bool Near(Collider c)
        {
            if (rend == null) return true;
            Bounds b = new Bounds(rend.bounds.center, rend.bounds.size + Vector3.one * 2f); // 1 m around it (Expand() on a copy is lost in Udon)
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
                    Vector3 p = c.transform.TransformPoint(c.center) - origin;
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
                    // ponytail: radius uses the largest axis scale, like a uniformly scaled capsule
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

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            inFlight--;
            if (stale > 0) { stale--; return; }
            if (!request.hasError && request.TryGetData(readback, 0)) fresh = true; // applied in PostLateUpdate
        }

        void ApplyReadback(bool follow)
        {
            Color[] src = readback;
            if (follow) // the object takes the body's pose: centre and rotation from the Fit zone
            {
                Color c = src[vertexCount], q = src[vertexCount + 1];
                Quaternion r = new Quaternion(q.r, q.g, q.b, q.a);
                if (Mathf.Abs(q.r * q.r + q.g * q.g + q.b * q.b + q.a * q.a - 1f) < 0.01f)
                    transform.SetPositionAndRotation(origin + new Vector3(c.r, c.g, c.b) - r * Vector3.Scale(restCom, transform.lossyScale), r);
            }
            // world -> local with this frame's final transform (the data itself is a frame or two old)
            Matrix4x4 inv = transform.worldToLocalMatrix * Matrix4x4.Translate(origin);
            Vector3[] dst = work;
            for (int i = 0; i < vertexCount; i++)
            {
                Color c = src[i];
                dst[i] = inv.MultiplyPoint3x4(new Vector3(c.r, c.g, c.b));
            }
            mesh.vertices = dst;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Cloth")]
        void Bake()
        {
            var src = GetComponent<MeshFilter>().sharedMesh;
            if (src == null) { Debug.LogError("[BlendUdonCloth] No mesh.", this); return; }
            if (!src.isReadable) { Debug.LogError("[BlendUdonCloth] Enable Read/Write on the mesh import settings.", this); return; }
            var verts = src.vertices;
            var colors = src.colors;
            bool hasColors = colors.Length == verts.Length;
            int[] tris = src.triangles;

            // weld split vertices (UV seams) into particles
            var particleOf = new int[verts.Length];
            var restP = new System.Collections.Generic.List<Vector3>();
            var weight = new System.Collections.Generic.List<float>();
            var weld = new System.Collections.Generic.Dictionary<Vector3, int>();
            for (int i = 0; i < verts.Length; i++)
            {
                float w = hasColors ? colors[i].r : goal;
                if (!weld.TryGetValue(verts[i], out int id))
                {
                    id = restP.Count;
                    weld.Add(verts[i], id);
                    restP.Add(verts[i]);
                    weight.Add(w);
                }
                else weight[id] = Mathf.Max(weight[id], w);
                particleOf[i] = id;
            }
            int n = restP.Count;
            int T = Mathf.Max(2, Mathf.CeilToInt(Mathf.Sqrt(n))); // 2+: the Fit column uses rows 0 and 1
            const int E = 5; // Soft Body columns
            Vector3 com = Vector3.zero;
            float maxWeight = 0f;
            for (int i = 0; i < n; i++) { com += restP[i]; maxWeight = Mathf.Max(maxWeight, weight[i]); }
            com /= n;

            // links: edges first (structural), then the far corners of triangle pairs (bending, negative length)
            var links = new System.Collections.Generic.List<Vector2>[n];
            for (int i = 0; i < n; i++) links[i] = new System.Collections.Generic.List<Vector2>();
            var edges = new System.Collections.Generic.Dictionary<long, int>();
            var bends = new System.Collections.Generic.List<Vector2Int>();
            for (int f = 0; f + 2 < tris.Length; f += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = particleOf[tris[f + e]], b = particleOf[tris[f + (e + 1) % 3]], c = particleOf[tris[f + (e + 2) % 3]];
                    if (a == b) continue;
                    long key = (long)Mathf.Min(a, b) << 32 | (uint)Mathf.Max(a, b);
                    if (edges.TryGetValue(key, out int opp))
                    {
                        if (opp >= 0 && opp != c) bends.Add(new Vector2Int(opp, c));
                        edges[key] = -1;
                        continue;
                    }
                    edges[key] = c;
                    float len = Vector3.Distance(restP[a], restP[b]);
                    links[a].Add(new Vector2(b, len));
                    links[b].Add(new Vector2(a, len));
                }
            foreach (var p in bends)
            {
                float len = -Mathf.Max(Vector3.Distance(restP[p.x], restP[p.y]), 1e-6f);
                links[p.x].Add(new Vector2(p.y, len));
                links[p.y].Add(new Vector2(p.x, len));
            }
            int dropped = 0;

            var rest = new Texture2D(T, T, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Rest" };
            var linkTex = new Texture2D(T * 6, T, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Links" };
            var restPx = new Color[T * T];
            var linkPx = new Color[T * 6 * T];
            for (int i = 0; i < linkPx.Length; i++) linkPx[i] = new Color(-1, 0, -1, 0);
            for (int i = 0; i < n; i++)
            {
                int x = i % T, y = i / T;
                restPx[y * T + x] = new Color(restP[i].x, restP[i].y, restP[i].z, weight[i]);
                var l = links[i];
                if (l.Count > 12) dropped += l.Count - 12; // bending links come last, so they go first
                for (int k = 0; k < Mathf.Min(l.Count, 12); k++)
                {
                    int idx = y * T * 6 + x * 6 + k / 2;
                    var c = linkPx[idx];
                    if ((k & 1) == 0) { c.r = l[k].x; c.g = l[k].y; } else { c.b = l[k].x; c.a = l[k].y; }
                    linkPx[idx] = c;
                }
            }
            rest.SetPixels(restPx); rest.Apply(false, false);
            linkTex.SetPixels(linkPx); linkTex.Apply(false, false);

            int total = verts.Length + 2; // + body centre and rotation
            int VW = Mathf.CeilToInt(Mathf.Sqrt(total)), VH = Mathf.CeilToInt(total / (float)VW);
            var map = new Texture2D(VW, VH, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Map" };
            var mapPx = new Color[VW * VH];
            for (int i = 0; i < verts.Length; i++) mapPx[i] = new Color(particleOf[i] % T, particleOf[i] / T, 0, 0);
            mapPx[verts.Length] = new Color(2 * T + 4, 0, 0, 0);
            mapPx[verts.Length + 1] = new Color(2 * T + 4, 1, 0, 0);
            map.SetPixels(mapPx); map.Apply(false, false);

            var shader = Shader.Find("Hidden/Blend Property/Cloth Sim");
            var simMat = new Material(shader) { name = "Sim" };
            simMat.SetTexture("_Rest", rest);
            simMat.SetTexture("_Links", linkTex);
            simMat.SetFloat("_T", T);
            simMat.SetFloat("_Relax", 2f); // tuned: 2 stays stable under fast motion, 3 can blow up
            simMat.SetFloat("_Extra", E);
            simMat.SetFloat("_Count", n);
            simMat.SetVector("_RestCom", com);
            var stateTex = new CustomRenderTexture(T * 2 + E, T, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "State", material = simMat, shaderPass = 0, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
                updateZoneSpace = CustomRenderTextureUpdateZoneSpace.Normalized,
            };
            // each substep: Rows (pass 2), Fit (pass 3), Step (pass 0); every zone covers the whole texture
            int[] zonePasses = { 2, 3, 0 };
            var zones = new CustomRenderTextureUpdateZone[zonePasses.Length];
            for (int i = 0; i < zones.Length; i++)
                zones[i] = new CustomRenderTextureUpdateZone
                {
                    updateZoneCenter = new Vector3(0.5f, 0.5f, 0.5f), updateZoneSize = Vector3.one, rotation = 0f,
                    passIndex = zonePasses[i], needSwap = i > 0,
                };
            stateTex.SetUpdateZones(zones);
            var outMat = new Material(shader) { name = "Vertices" };
            outMat.SetTexture("_Map", map);
            outMat.SetTexture("_State", stateTex);
            outMat.SetFloat("_T", T);
            outMat.SetFloat("_Extra", E);
            outMat.SetFloat("_VW", VW);
            outMat.SetFloat("_VH", VH);
            var outTex = new CustomRenderTexture(VW, VH, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "Vertices", material = outMat, shaderPass = 1, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };

            // one asset per cloth, holding everything; rebaking replaces it
            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = state != null && UnityEditor.AssetDatabase.Contains(state)
                ? UnityEditor.AssetDatabase.GetAssetPath(state)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_cloth.asset");
            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(stateTex, path);
            var parts = new Object[] { outTex, simMat, outMat, rest, linkTex, map };
            foreach (Object o in parts) UnityEditor.AssetDatabase.AddObjectToAsset(o, path);
            // the state texture was written before its material was an asset: link and save it again so the link sticks
            stateTex.material = simMat;
            outTex.material = outMat;
            UnityEditor.EditorUtility.SetDirty(stateTex);
            foreach (Object o in parts) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();

            UnityEditor.Undo.RecordObject(this, "Bake Cloth");
            state = stateTex;
            vertexTexture = outTex;
            vertexCount = verts.Length;
            softBodyBaked = true;
            freeBody = maxWeight <= 0f;
            restCom = com;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonCloth] Baked {n} particles ({verts.Length} vertices) to {path}." +
                      (dropped > 0 ? $" {dropped} links over the 12-per-vertex limit were skipped." : ""), this);
        }
#endif
    }
}
