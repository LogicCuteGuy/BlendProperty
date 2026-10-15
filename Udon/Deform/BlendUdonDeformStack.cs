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
    /// The live part of a Modifier Stack in VRChat: the Wave, Simple Deform, Lattice, Hook and Warp modifiers whose
    /// Runtime is GPU, applied in stack order on top of the baked mesh, on the GPU (the mesh shows it a frame or two late). Made at build from the stack (you don't add
    /// it yourself). Re-deforms when a wave animates, a followed object moves, or a setting changes.
    /// From your own Udon: <c>var d = GetComponent&lt;BlendUdonDeformStack&gt;(); int w = d.FindOp("Wave");
    /// d.SetFloat(w, "height", 1f);</c>. Field names are the modifier's (height, angle, strength...).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/GPUDeform.png")]
    [AddComponentMenu("")] // made at build from the Modifier Stack
    public class BlendUdonDeformStack : UdonSharpBehaviour
    {
        public const int Wave = 0, SimpleDeform = 1, Lattice = 2, Hook = 3, Warp = 4;
        const int MaxOps = 8;

        public bool recalculateNormals = true;
        [Header("Modifiers (filled at build)")]
        public string[] opNames;
        public int[] opTypes;
        // settings in the modifier's own units, by type (see Slot):
        // Wave A (height, width, narrowness, speed) B (startX, startZ, offset, time) C (motionX, motionZ, cyclic, animate) D (life, damping)
        // Simple Deform A (mode, angle, factor, axis) B (lowerLimit, upperLimit, lock mask)
        // Lattice A (U, V, W, strength); Hook / Warp A (strength, radius, falloff) B (center, mesh units)
        public Vector4[] opA, opB, opC, opD;
        [Tooltip("Wave start object, Simple Deform origin, Lattice cage, Hook target, Warp from.")]
        public Transform[] opT0;
        [Tooltip("Warp to.")]
        public Transform[] opT1;
        [Tooltip("Hook: rows of the inverse rest placement, 4 per modifier.")]
        public Vector4[] opBind;
        [Tooltip("The Lattice's point offsets from its rest grid, U fastest.")]
        public Vector4[] latticePoints;
        [Tooltip("The Lattice's cage when its children are the points: read every frame so they can move.")]
        public Transform latticeCage;
        public int latticeOp;
        [Header("GPU (baked at build)")]
        public CustomRenderTexture output;
        public CustomRenderTexture bounds;
        // Scene-generated render textures lose their material links in world bundles; retain and restore them explicitly.
        [HideInInspector] public Material outputMaterial, boundsMaterial;
        public int vertexCount;

        Mesh mesh;
        Vector3[] rest, cur;
        Color[] readback;
        Material mat;
        Vector4[] pType, pA, pB, pC, pM, lastM, gpuPoints;
        Vector3 lastScale;
        bool dirty, readNext, running;
        int inFlight, count;
        int idOpCount, idType, idA, idB, idC, idM, idPoints;

        void Start()
        {
            mesh = GetComponent<MeshFilter>().mesh;
            mesh.MarkDynamic();
            rest = mesh.vertices;
            cur = new Vector3[rest.Length];
            count = opTypes == null ? 0 : Mathf.Min(opTypes.Length, MaxOps);
            pType = new Vector4[MaxOps]; pA = new Vector4[MaxOps]; pB = new Vector4[MaxOps]; pC = new Vector4[MaxOps];
            pM = new Vector4[MaxOps * 8]; lastM = new Vector4[MaxOps * 8];
            gpuPoints = new Vector4[512];
            if (latticePoints != null)
                for (int i = 0; i < latticePoints.Length && i < 512; i++) gpuPoints[i] = latticePoints[i];
            if (output != null && outputMaterial != null) output.material = outputMaterial;
            if (bounds != null && boundsMaterial != null) bounds.material = boundsMaterial;
            if (!GpuBaked())
            {
                Debug.LogWarning("[BlendUdonDeformStack] Not baked for this mesh: the Runtime modifiers stay still.", this);
                enabled = false;
                return;
            }
            {
                mat = output.material;
                readback = new Color[output.width * output.height];
                idOpCount = VRCShader.PropertyToID("_OpCount");
                idType = VRCShader.PropertyToID("_OpType");
                idA = VRCShader.PropertyToID("_OpA");
                idB = VRCShader.PropertyToID("_OpB");
                idC = VRCShader.PropertyToID("_OpC");
                idM = VRCShader.PropertyToID("_OpM");
                idPoints = VRCShader.PropertyToID("_Points");
                mat.SetVectorArray(idPoints, gpuPoints);
            }
            dirty = true;
        }

        void OnEnable() { running = true; dirty = true; }

        void OnDisable()
        {
            running = false;
            if (mesh != null && rest != null) { mesh.vertices = rest; mesh.RecalculateNormals(); mesh.RecalculateBounds(); }
        }

        bool GpuBaked()
        {
            if (output == null || bounds == null || rest == null || vertexCount != rest.Length) return false;
            if (output.material == null || bounds.material == null) return false;
            return latticePoints == null || latticePoints.Length <= 512;
        }

        // ------------------------------------------------------------------ scripting

        /// <summary>Index of the modifier called <paramref name="name"/> (e.g. "Wave", "Simple Deform 2"), or -1.</summary>
        public int FindOp(string name)
        {
            for (int i = 0; i < count; i++) if (opNames[i] == name) return i;
            return -1;
        }

        /// <summary>Re-deform on the next frame (after changing settings yourself).</summary>
        public void Deform() { dirty = true; }

        public void SetFloat(int op, string field, float value)
        {
            int s = Slot(op, field);
            if (s < 0) return;
            Vector4 v = Row(op, s >> 2);
            v[s & 3] = value;
            if ((s >> 2) == 0) opA[op] = v;
            else if ((s >> 2) == 1) opB[op] = v;
            else if ((s >> 2) == 2) opC[op] = v;
            else opD[op] = v;
            dirty = true;
        }

        public float GetFloat(int op, string field)
        {
            int s = Slot(op, field);
            return s < 0 ? 0f : Row(op, s >> 2)[s & 3];
        }

        public void SetBool(int op, string field, bool value) { SetFloat(op, field, value ? 1f : 0f); }
        public bool GetBool(int op, string field) { return GetFloat(op, field) > 0.5f; }

        Vector4 Row(int op, int r)
        {
            if (r == 0) return opA[op];
            if (r == 1) return opB[op];
            if (r == 2) return opC[op];
            return opD[op];
        }

        /// <summary>Where a modifier field lives: row * 4 + component, or -1.</summary>
        int Slot(int op, string f)
        {
            if (op < 0 || op >= count) return -1;
            int t = opTypes[op];
            if (t == Wave)
            {
                if (f == "height") return 0; if (f == "width") return 1; if (f == "narrowness") return 2; if (f == "speed") return 3;
                if (f == "startX") return 4; if (f == "startZ") return 5; if (f == "offset") return 6; if (f == "time") return 7;
                if (f == "motionX") return 8; if (f == "motionZ") return 9; if (f == "cyclic") return 10; if (f == "animate") return 11;
                if (f == "life") return 12; if (f == "damping") return 13;
            }
            else if (t == SimpleDeform)
            {
                if (f == "mode") return 0; if (f == "angle") return 1; if (f == "factor") return 2; if (f == "axis") return 3;
                if (f == "lowerLimit") return 4; if (f == "upperLimit") return 5;
            }
            else if (t == Lattice) { if (f == "strength") return 3; }
            else { if (f == "strength") return 0; if (f == "radius") return 1; if (f == "falloff") return 2; }
            return -1;
        }

        // ------------------------------------------------------------------ update

        // PostLateUpdate: after Animators and pickups moved the followed objects
        public override void PostLateUpdate()
        {
            if (!running || mat == null) return;
            if (readNext && inFlight < 3)
            {
                readNext = false;
                inFlight++;
                VRCAsyncGPUReadback.Request(output, 0, TextureFormat.RGBAFloat, (IUdonEventReceiver)this);
            }

            float dt = Time.deltaTime;
            for (int o = 0; o < count; o++)
                if (opTypes[o] == Wave && opC[o].w > 0.5f)
                {
                    Vector4 b = opB[o];
                    b.w += dt;
                    opB[o] = b;
                    dirty = true;
                }
            Pack();
            if (latticeCage != null && ReadCage()) dirty = true;
            for (int i = 0; i < count * 8; i++)
                if (pM[i] != lastM[i]) { dirty = true; lastM[i] = pM[i]; }
            if (transform.lossyScale != lastScale) { lastScale = transform.lossyScale; dirty = true; }
            if (!dirty) return;
            dirty = false;

            {
                mat.SetFloat(idOpCount, count);
                mat.SetVectorArray(idType, pType);
                mat.SetVectorArray(idA, pA);
                mat.SetVectorArray(idB, pB);
                mat.SetVectorArray(idC, pC);
                mat.SetVectorArray(idM, pM);
                Material bm = bounds.material;
                bm.SetFloat(idOpCount, count);
                bm.SetVectorArray(idType, pType);
                bm.SetVectorArray(idA, pA);
                bm.SetVectorArray(idB, pB);
                bm.SetVectorArray(idC, pC);
                bm.SetVectorArray(idM, pM);
                bm.SetVectorArray(idPoints, gpuPoints);
                bounds.Update(1); // before output: the deform pass reads it
                output.Update(1);
                readNext = true;
            }
        }

        /// <summary>Cage children's offsets from the rest grid into gpuPoints. True when any moved.</summary>
        bool ReadCage()
        {
            Vector4 a = opA[latticeOp];
            int ru = Mathf.RoundToInt(a.x), rv = Mathf.RoundToInt(a.y), rw = Mathf.RoundToInt(a.z);
            int n = Mathf.Min(Mathf.Min(ru * rv * rw, latticeCage.childCount), 512);
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int u = i % ru, v = (i / ru) % rv, w = i / (ru * rv);
                Vector3 o = latticeCage.GetChild(i).localPosition
                            - new Vector3((float)u / (ru - 1) - 0.5f, (float)v / (rv - 1) - 0.5f, (float)w / (rw - 1) - 0.5f);
                Vector4 d = new Vector4(o.x, o.y, o.z, 0f);
                if (d != gpuPoints[i]) { gpuPoints[i] = d; changed = true; }
            }
            if (changed) mat.SetVectorArray(idPoints, gpuPoints);
            return changed;
        }

        // matrices from another object's space into this mesh's
        Matrix4x4 ToLocal(Transform t)
        {
            return t != null ? transform.worldToLocalMatrix * t.localToWorldMatrix : Matrix4x4.identity;
        }

        void PutM(int op, int k, Matrix4x4 m)
        {
            int b = op * 8 + k * 4;
            pM[b] = m.GetRow(0); pM[b + 1] = m.GetRow(1); pM[b + 2] = m.GetRow(2); pM[b + 3] = m.GetRow(3);
        }

        /// <summary>Settings into mesh units and matrices for this frame (pA.. and pM), same for CPU and GPU.</summary>
        void Pack()
        {
            Vector3 s = transform.lossyScale;
            float avg = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            float k = avg > 1e-30f ? 1f / avg : 1f;
            for (int o = 0; o < count; o++)
            {
                int t = opTypes[o];
                Vector4 a = opA[o], b = opB[o], c = opC[o];
                pType[o] = new Vector4(t, 0, 0, 0);
                if (t == Wave)
                {
                    float w = Mathf.Max(a.y * k, 0.0001f), nar = a.z / k;
                    float elapsed = b.w - b.z, fade = 1f;
                    Vector4 d = opD[o];
                    if (d.x > 0f && elapsed > d.x) fade = d.y > 0f ? Mathf.Clamp01(1f - (elapsed - d.x) / d.y) : 0f;
                    float sx = b.x / Mathf.Max(Mathf.Abs(s.x), 1e-30f), sz = b.y / Mathf.Max(Mathf.Abs(s.z), 1e-30f);
                    if (opT0[o] != null) { Vector3 sp = ToLocal(opT0[o]).GetColumn(3); sx = sp.x; sz = sp.z; }
                    pA[o] = new Vector4(a.x * k, w, nar, elapsed * a.w * k);
                    pB[o] = new Vector4(sx, sz, Mathf.Exp(-(w * nar) * (w * nar)), c.z);
                    pC[o] = new Vector4(c.x, c.y, fade, 0f);
                }
                else if (t == SimpleDeform)
                {
                    // deform space = origin space with the deform axis swizzled to Z, built from columns
                    // (SetRow on a local matrix doesn't stick in Udon)
                    Matrix4x4 sw = Matrix4x4.identity;
                    Vector4 w4 = new Vector4(0, 0, 0, 1);
                    int axis = Mathf.RoundToInt(a.w);
                    if (axis == 0) sw = new Matrix4x4(new Vector4(0, 0, 1, 0), new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0), w4);
                    else if (axis == 1) sw = new Matrix4x4(new Vector4(0, 1, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(1, 0, 0, 0), w4);
                    Matrix4x4 to = sw * ToLocal(opT0[o]).inverse;
                    PutM(o, 0, to);
                    PutM(o, 1, to.inverse);
                    pA[o] = new Vector4(a.x, a.y * Mathf.Deg2Rad, a.z, a.w);
                    pB[o] = new Vector4(b.x, b.y, Mathf.RoundToInt(a.x) == 1 ? 0f : b.z, 0f);
                }
                else if (t == Lattice)
                {
                    Matrix4x4 cage = ToLocal(opT0[o]);
                    PutM(o, 0, cage.inverse);
                    PutM(o, 1, cage);
                    pA[o] = a;
                }
                else
                {
                    Matrix4x4 m;
                    if (t == Hook)
                    {
                        int r = o * 4;
                        Vector4 r0 = opBind[r], r1 = opBind[r + 1], r2 = opBind[r + 2], r3 = opBind[r + 3];
                        Matrix4x4 bindInv = new Matrix4x4(new Vector4(r0.x, r1.x, r2.x, r3.x), new Vector4(r0.y, r1.y, r2.y, r3.y),
                            new Vector4(r0.z, r1.z, r2.z, r3.z), new Vector4(r0.w, r1.w, r2.w, r3.w));
                        m = ToLocal(opT0[o]) * bindInv;
                        pB[o] = b;
                    }
                    else
                    {
                        Matrix4x4 f = ToLocal(opT0[o]);
                        m = ToLocal(opT1[o]) * f.inverse;
                        pB[o] = f.GetColumn(3);
                    }
                    PutM(o, 0, m);
                    pA[o] = new Vector4(a.y * k, a.z, a.x, 0f); // radius, falloff, strength (shader order)
                }
            }
        }

        // ------------------------------------------------------------------ GPU readback

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            inFlight--;
            if (!running) return; // switched off while this was in flight
            if (request.hasError || !request.TryGetData(readback, 0)) return;
            for (int i = 0; i < vertexCount; i++)
            {
                Color c = readback[i];
                cur[i] = new Vector3(c.r, c.g, c.b);
            }
            mesh.vertices = cur;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
