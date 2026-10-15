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
    public enum BlendUdonDynamicSurface { Waves, Displace }

    /// <summary>
    /// Blender Dynamic Paint canvas with a Waves or Displace surface. Waves: BlendUdonPaintBrush objects push the
    /// surface aside as they move and the ripples spread across it. Displace: brushes leave dents that stay, or
    /// recover over Dissolve seconds. Simulated on the GPU in the mesh's UV space and written back into the mesh, so any
    /// shader works. The mesh needs enough vertices to show ripples and non-overlapping UVs inside 0..1.
    /// Use ⋮ → Bake Dynamic Waves. Local only (not synced). One Blend Udon deformer per mesh.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/DynamicWaves.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Dynamic Waves")]
    public class BlendUdonDynamicWaves : UdonSharpBehaviour
    {
        [Tooltip("Waves: ripples. Displace: lasting dents (Blender Surface Type).")]
        public BlendUdonDynamicSurface surfaceType;
        [Tooltip("Up to 8 brushes at once. Waves: moving brushes push the water aside. Displace: brushes press dents. Strength scales how much (Blender Factor).")]
        public BlendUdonPaintBrush[] brushes;
        [Header("Waves")]
        [Min(0.01f), Tooltip("How fast ripples travel, m/s (Blender Speed).")]
        public float speed = 1f;
        [Min(0f), Tooltip("How fast ripples die out, per second (Blender Damping).")]
        public float damping = 0.6f;
        [Min(0f), Tooltip("Pull back to the flat surface (Blender Spring).")]
        public float spring = 0.5f;
        [Range(0, 10), Tooltip("Rounds off sharp peaks (Blender Smoothness).")]
        public float smoothness = 0.3f;
        [Header("Displace")]
        [Min(0f), Tooltip("Seconds for dents to flatten out again (Blender Dissolve). 0 = they stay.")]
        public float dissolve = 10f;
        [Space]
        [Range(0, 4), Tooltip("Simulation speed (Blender Timescale).")]
        public float timeScale = 1f;
        [Tooltip("Recalculate normals after each update.")]
        public bool recalculateNormals = true;
        [Header("Baked (⋮ → Bake Dynamic Waves)")]
        [Range(16, 1024), Tooltip("Wave texture size in pixels: about the mesh's vertex count across, or ripples finer than the vertices show as spikes. Rebake after changing.")]
        public int resolution = 64;
        public CustomRenderTexture waves;
        public CustomRenderTexture vertexTexture;
        public int vertexCount;
        [Tooltip("Local-space size of one wave texel, from the bake.")]
        public float texelSize = 0.01f;

        Mesh mesh;
        Renderer rend;
        Material waveMat;
        Vector3[] work, restVerts;
        Color[] readback;
        Vector4[] brushA, brushB;
        float activeUntil, accumulator;
        bool reset, readNext, running;
        int inFlight;
        int idObjectToWorld, idK, idDt, idDamping, idSpring, idSmooth, idScale, idReset, idCount, idA, idB, idMode, idKeep;

        void Start()
        {
            if (waves == null || vertexTexture == null)
            {
                Debug.LogWarning("[BlendUdonDynamicWaves] Not baked: use ⋮ → Bake Dynamic Waves.", this);
                enabled = false;
                return;
            }
            mesh = GetComponent<MeshFilter>().mesh;
            mesh.MarkDynamic();
            restVerts = mesh.vertices;
            rend = GetComponent<Renderer>();
            waveMat = waves.material;
            work = new Vector3[vertexCount];
            readback = new Color[vertexTexture.width * vertexTexture.height];
            brushA = new Vector4[8]; brushB = new Vector4[8];
            idObjectToWorld = VRCShader.PropertyToID("_ObjectToWorld");
            idK = VRCShader.PropertyToID("_K");
            idDt = VRCShader.PropertyToID("_Dt");
            idDamping = VRCShader.PropertyToID("_Damping");
            idSpring = VRCShader.PropertyToID("_Spring");
            idSmooth = VRCShader.PropertyToID("_Smooth");
            idMode = VRCShader.PropertyToID("_Mode");
            idKeep = VRCShader.PropertyToID("_Keep");
            idScale = VRCShader.PropertyToID("_Scale");
            idReset = VRCShader.PropertyToID("_Reset");
            idCount = VRCShader.PropertyToID("_BrushCount");
            idA = VRCShader.PropertyToID("_BrushA");
            idB = VRCShader.PropertyToID("_BrushB");
            reset = true;
        }

        void OnEnable() { running = true; reset = true; }

        void OnDisable()
        {
            running = false;
            if (mesh != null) { mesh.vertices = restVerts; mesh.RecalculateNormals(); mesh.RecalculateBounds(); }
        }

        /// <summary>Flatten the surface.</summary>
        public void ResetWaves() { reset = true; }

        // PostLateUpdate: after pickups and Animators moved the brushes
        public override void PostLateUpdate()
        {
            // last frame's update is drawn by now: copy it back
            if (readNext && inFlight < 3)
            {
                readNext = false;
                inFlight++;
                VRCAsyncGPUReadback.Request(vertexTexture, 0, TextureFormat.RGBAFloat, (IUdonEventReceiver)this);
            }
            if (!running || mesh == null) return;

            int n = 0;
            if (brushes != null)
            {
                Bounds bb = rend.bounds;
                for (int i = 0; i < brushes.Length && n < 8; i++)
                {
                    BlendUdonPaintBrush b = brushes[i];
                    if (b == null || !b.enabled || !b.gameObject.activeInHierarchy || !b.drawing ||
                        (b.requireUse && !b.useHeld) || !(b.pressure > 0f) || !(b.radius > 0f)) continue;
                    Vector3 p = b.tip != null ? b.tip.position : b.transform.position;
                    float r = b.radius;
                    if (bb.SqrDistance(p) >= r * r) continue; // can't reach this mesh
                    brushA[n] = new Vector4(p.x, p.y, p.z, r);
                    brushB[n] = new Vector4(b.strength * Mathf.Clamp01(b.pressure), 0f, 0f, 0f);
                    n++;
                }
            }
            float now = Time.time;
            bool displace = surfaceType == BlendUdonDynamicSurface.Displace;
            // keep updating a while after the last touch (until ripples die out / dents recover), then stop costing anything
            if (n > 0) activeUntil = now + (displace ? (dissolve > 0f ? dissolve * 5f : 0f) : Mathf.Min(20f, 6f / Mathf.Max(damping + spring * 0.5f, 0.1f)));
            if (n == 0 && !reset && now > activeUntil) return;

            Vector3 s = transform.lossyScale;
            float scale = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            waveMat.SetFloat(idMode, displace ? 1f : 0f);
            if (displace)
            {
                float dt = Mathf.Min(Time.deltaTime, 1f / 15f) * timeScale;
                waveMat.SetMatrix(idObjectToWorld, transform.localToWorldMatrix);
                waveMat.SetFloat(idKeep, dissolve > 0f ? Mathf.Exp(-dt / dissolve) : 1f);
                waveMat.SetFloat(idScale, scale);
                waveMat.SetFloat(idReset, reset ? 1f : 0f);
                waveMat.SetFloat(idCount, n);
                waveMat.SetVectorArray(idA, brushA);
                waveMat.SetVectorArray(idB, brushB);
                waves.Update(1);
                vertexTexture.Update(1);
                readNext = true;
                reset = false;
                return;
            }
            float dx = Mathf.Max(texelSize * scale, 1e-5f);
            // fixed substeps: a step size that changes from frame to frame pumps energy into the finest ripples
            // until they blow up. Stable while speed * step / texel stays under ~0.7.
            float h = Mathf.Min(1f / 60f, 0.5f * dx / Mathf.Max(speed, 0.01f));
            accumulator += Mathf.Min(Time.deltaTime, 1f / 15f) * timeScale;
            int steps = Mathf.Min(Mathf.FloorToInt(accumulator / h), 32);
            accumulator = Mathf.Min(accumulator - steps * h, h); // drop the backlog under load: slow down instead of lagging
            if (steps == 0 && !reset) return;
            float c = speed * h / dx;
            waveMat.SetMatrix(idObjectToWorld, transform.localToWorldMatrix);
            waveMat.SetFloat(idK, Mathf.Min(c * c, 0.5f));
            waveMat.SetFloat(idDt, Mathf.Max(h, 1e-5f));
            waveMat.SetFloat(idDamping, Mathf.Exp(-damping * h));
            waveMat.SetFloat(idSpring, spring * h * h);
            waveMat.SetFloat(idSmooth, 1f - Mathf.Exp(-smoothness * 10f * h));
            waveMat.SetFloat(idScale, scale);
            waveMat.SetFloat(idReset, reset ? 1f : 0f);
            waveMat.SetFloat(idCount, n);
            waveMat.SetVectorArray(idA, brushA);
            waveMat.SetVectorArray(idB, brushB);
            waves.Update(reset ? 1 : steps);
            vertexTexture.Update(1);
            readNext = true;
            reset = false;
        }

        public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)
        {
            inFlight--;
            if (!running) return; // switched off while this was in flight
            if (request.hasError || !request.TryGetData(readback, 0)) return;
            Color[] src = readback;
            Vector3[] dst = work;
            for (int i = 0; i < vertexCount; i++)
            {
                Color c = src[i];
                dst[i] = new Vector3(c.r, c.g, c.b);
            }
            mesh.vertices = dst;
            if (recalculateNormals) mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Dynamic Waves")]
        void Bake()
        {
            var src = GetComponent<MeshFilter>().sharedMesh;
            if (src == null) { Debug.LogError("[BlendUdonDynamicWaves] No mesh.", this); return; }
            if (!src.isReadable) { Debug.LogError("[BlendUdonDynamicWaves] Enable Read/Write on the mesh import settings.", this); return; }
            var uvs = src.uv;
            if (uvs == null || uvs.Length == 0) { Debug.LogError("[BlendUdonDynamicWaves] The mesh has no UVs.", this); return; }
            var verts = src.vertices;
            var normals = src.normals;
            if (normals == null || normals.Length != verts.Length) { src.RecalculateNormals(); normals = src.normals; }
            var tris = src.triangles;
            int R = resolution;

            var pos = BlendUdonDynamicPaint.PositionMap(verts, uvs, tris, R, out int outside);
            if (outside > 0) Debug.LogWarning($"[BlendUdonDynamicWaves] {outside} triangles have UVs outside 0..1 and get no waves.", this);
            var posTex = new Texture2D(R, R, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Positions" };
            posTex.SetPixels(pos); posTex.Apply(false, false);

            // local size of one texel: mesh area spread over the texels it covers
            float area = 0;
            for (int t = 0; t < tris.Length; t += 3)
                area += Vector3.Cross(verts[tris[t + 1]] - verts[tris[t]], verts[tris[t + 2]] - verts[tris[t]]).magnitude * 0.5f;
            int covered = 0;
            foreach (var c in pos) if (c.a > 0.5f) covered++; // ponytail: includes the seam padding, so slightly small
            float texel = Mathf.Sqrt(area / Mathf.Max(covered, 1));

            int VW = Mathf.CeilToInt(Mathf.Sqrt(verts.Length)), VH = Mathf.CeilToInt(verts.Length / (float)VW);
            var a = new Color[VW * VH];
            var b = new Color[VW * VH];
            for (int i = 0; i < verts.Length; i++)
            {
                a[i] = new Color(verts[i].x, verts[i].y, verts[i].z, uvs[i].x);
                b[i] = new Color(normals[i].x, normals[i].y, normals[i].z, uvs[i].y);
            }
            var vertA = new Texture2D(VW, VH, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Vertices A" };
            var vertB = new Texture2D(VW, VH, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Vertices B" };
            vertA.SetPixels(a); vertA.Apply(false, false);
            vertB.SetPixels(b); vertB.Apply(false, false);

            var shader = Shader.Find("Hidden/Blend Property/Dynamic Waves");
            var waveMat = new Material(shader) { name = "Waves" };
            waveMat.SetTexture("_Pos", posTex);
            var waveTex = new CustomRenderTexture(R, R, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "Waves", material = waveMat, shaderPass = 0, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnLoad, initializationColor = Color.clear,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            var vertMat = new Material(shader) { name = "Vertices" };
            vertMat.SetTexture("_Waves", waveTex);
            vertMat.SetTexture("_VertA", vertA);
            vertMat.SetTexture("_VertB", vertB);
            vertMat.SetFloat("_VW", VW); vertMat.SetFloat("_VH", VH); vertMat.SetFloat("_N", verts.Length);
            var vertTex = new CustomRenderTexture(VW, VH, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "Dynamic Waves", material = vertMat, shaderPass = 1, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };

            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = vertexTexture != null && UnityEditor.AssetDatabase.Contains(vertexTexture)
                ? UnityEditor.AssetDatabase.GetAssetPath(vertexTexture)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_dynamicwaves.asset");
            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(vertTex, path);
            var parts = new Object[] { waveTex, waveMat, vertMat, posTex, vertA, vertB };
            foreach (Object o in parts) UnityEditor.AssetDatabase.AddObjectToAsset(o, path);
            foreach (Object o in parts) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();
            // textures written before their materials were assets lose the link: set them again
            vertTex.material = vertMat;
            waveTex.material = waveMat;
            UnityEditor.EditorUtility.SetDirty(vertTex);
            UnityEditor.EditorUtility.SetDirty(waveTex);
            UnityEditor.AssetDatabase.SaveAssets();

            UnityEditor.Undo.RecordObject(this, "Bake Dynamic Waves");
            waves = waveTex;
            vertexTexture = vertTex;
            vertexCount = verts.Length;
            texelSize = texel;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonDynamicWaves] Baked {verts.Length} vertices and a {R}x{R} wave texture to {path}.", this);
        }
#endif
    }
}
