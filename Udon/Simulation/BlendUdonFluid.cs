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
    public enum BlendUdonFluidType { Gas, Liquid }

    /// <summary>
    /// Blender Fluid domain, type Gas (smoke and fire) or Liquid, simulated on the GPU in this object's box (a Cube,
    /// scaled to the size you want). BlendUdonFluidFlow objects add smoke, fire or liquid; Sphere Colliders listed as
    /// Effectors block and push it; BlendUdonForceField objects blow it. Gas: hot smoke rises (Buoyancy), Vorticity keeps
    /// it curly, Dissolve fades it. Liquid: water particles (Position Based Fluids) that fall under Gravity and
    /// splash around the box.
    /// Draw it with the Blend Property/Smoke or Blend Property/Liquid material on the same Cube. Use ⋮ → Bake Fluid Domain.
    /// Udon only uploads settings, nothing is read back. Local only (not synced).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/FluidDomain.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Fluid Domain")]
    public class BlendUdonFluid : UdonSharpBehaviour
    {
        [Tooltip("Gas: smoke and fire. Liquid: water (Blender Domain Type). Rebake after changing, to get the right material.")]
        public BlendUdonFluidType domainType;
        [Tooltip("Run the simulation. Off: freeze.")]
        public bool simulate;
        [Tooltip("Skip simulating while no camera sees the domain.")]
        public bool onlyWhenVisible = true;
        [Range(0, 4), Tooltip("Simulation speed (Blender Time Scale).")]
        public float timeScale = 1f;
        [Header("Smoke")]
        [Tooltip("Up to 8 sources.")]
        public BlendUdonFluidFlow[] flows;
        [Range(-5, 5), Tooltip("How much smoke density rises, m/s² per unit (Blender Buoyancy Density). Negative sinks.")]
        public float buoyancyDensity = 0.5f;
        [Range(-10, 10), Tooltip("How much heat rises, m/s² per unit (Blender Heat).")]
        public float buoyancyHeat = 3f;
        [Range(0, 10), Tooltip("Keeps small swirls alive (Blender Vorticity).")]
        public float vorticity = 2f;
        [Min(0f), Tooltip("Seconds for smoke to fade away (Blender Dissolve). 0 = never.")]
        public float dissolve = 4f;
        [Min(0.05f), Tooltip("Seconds for heat to cool off.")]
        public float coolTime = 1.5f;
        [Header("Fire")]
        [Range(0, 10), Tooltip("How fast fuel burns, per second (Blender Reaction Speed).")]
        public float reactionSpeed = 3f;
        [Range(0, 4), Tooltip("Smoke made per unit of burnt fuel (Blender Flame Smoke).")]
        public float flameSmoke = 1f;
        [Range(0, 10), Tooltip("Heat made per unit of burnt fuel: makes the flames rise (Blender Temperature Maximum).")]
        public float flameHeat = 4f;
        [Min(0.01f), Tooltip("Seconds a flame glows after its fuel is gone.")]
        public float flameFade = 0.15f;
        [Header("Liquid")]
        [Tooltip("World m/s² (Blender Gravity).")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);
        [Range(0, 1), Tooltip("After a reset the box is filled with liquid up to this height (0..1).")]
        public float fillLevel = 0.3f;
        [Range(0, 1), Tooltip("How much each bit of water moves with its neighbours: higher calms splashes sooner (Blender Viscosity).")]
        public float viscosity = 0.1f;
        [Header("Effectors")]
        [Tooltip("Up to 8 Sphere Colliders the smoke flows around; moving ones push it (Blender Effector).")]
        public SphereCollider[] effectors;
        [Tooltip("Up to 4 Blender Force Fields acting on the smoke.")]
        public BlendUdonForceField[] forceFields;
        [Header("Baked (⋮ → Bake Fluid Domain)")]
        [Range(16, 96), Tooltip("Cells along the longest side. Rebake after changing it or the box's proportions.")]
        public int resolution = 40;
        [Range(4, 60), Tooltip("Gas: pressure solver iterations per frame. More = less squashy, costs GPU. Rebake after changing.")]
        public int pressureIterations = 20;
        [Tooltip("Liquid: most water particles (4096, 16384 or 65536; fewer = faster, chunkier). The box holds this many when full. Rebake after changing.")]
        public int maxParticles = 16384;
        [Range(1, 8), Tooltip("Liquid: density solver iterations per step. More = less squashy, costs GPU. Rebake after changing.")]
        public int solverIterations = 3;
        [HideInInspector] public float particleSpacing;
        [HideInInspector] public int particleCount;
        [Tooltip("Velocity, pressure, density and heat.")]
        public CustomRenderTexture simulation;
        [Tooltip("3D copy of the density, drawn by the Blend Property/Smoke material.")]
        public CustomRenderTexture volume;

        Renderer rend;
        Material simMat;
        Vector4[] flowA, flowB, flowC, obsA, obsB, fieldA, fieldB, fieldC;
        Vector3[] lastFlowPos, lastObsPos;
        float[] emit;
        int cursor;
        bool reset;
        int idD2W, idW2D, idDt, idReset, idObsCount, idObsA, idObsB, idFlowCount, idFlowA, idFlowB, idFlowC,
            idBuoyancy, idUp, idVorticity, idKeep, idFieldA, idFieldB, idFieldC, idFieldCount, idFire,
            idGravity, idFill, idSeed, idViscosity;

        void Start()
        {
            if (simulation == null || volume == null)
            {
                Debug.LogWarning("[BlendUdonFluid] Not baked: use ⋮ → Bake Fluid Domain.", this);
                enabled = false;
                return;
            }
            rend = GetComponent<Renderer>();
            simMat = simulation.material;
            flowA = new Vector4[8]; flowB = new Vector4[8]; flowC = new Vector4[8];
            obsA = new Vector4[8]; obsB = new Vector4[8];
            fieldA = new Vector4[4]; fieldB = new Vector4[4]; fieldC = new Vector4[4];
            lastFlowPos = new Vector3[flows != null ? Mathf.Max(8, flows.Length) : 8]; lastObsPos = new Vector3[effectors != null ? Mathf.Max(8, effectors.Length) : 8]; // by list index
            emit = new float[8];
            idD2W = VRCShader.PropertyToID("_DomainToWorld");
            idW2D = VRCShader.PropertyToID("_WorldToDomain");
            idDt = VRCShader.PropertyToID("_Dt");
            idReset = VRCShader.PropertyToID("_Reset");
            idObsCount = VRCShader.PropertyToID("_ObstacleCount");
            idObsA = VRCShader.PropertyToID("_ObstacleA");
            idObsB = VRCShader.PropertyToID("_ObstacleB");
            idFlowCount = VRCShader.PropertyToID("_FlowCount");
            idFlowA = VRCShader.PropertyToID("_FlowA");
            idFlowB = VRCShader.PropertyToID("_FlowB");
            idFlowC = VRCShader.PropertyToID("_FlowC");
            idBuoyancy = VRCShader.PropertyToID("_Buoyancy");
            idUp = VRCShader.PropertyToID("_Up");
            idVorticity = VRCShader.PropertyToID("_Vorticity");
            idKeep = VRCShader.PropertyToID("_Keep");
            idFire = VRCShader.PropertyToID("_Fire");
            idFieldA = VRCShader.PropertyToID("_FieldA");
            idFieldB = VRCShader.PropertyToID("_FieldB");
            idFieldC = VRCShader.PropertyToID("_FieldC");
            idFieldCount = VRCShader.PropertyToID("_FieldCount");
            idGravity = VRCShader.PropertyToID("_Gravity");
            idFill = VRCShader.PropertyToID("_Fill");
            idSeed = VRCShader.PropertyToID("_Seed");
            idViscosity = VRCShader.PropertyToID("_Viscosity");
            reset = true; // start empty
        }

        /// <summary>Remove all smoke and motion (Liquid: back to the Fill Level).</summary>
        public void ResetSimulation() { reset = true; }

        // only effectors within 1 m of this domain take its 8 slots (with every player's body there are many)
        bool Near(Collider c)
        {
            if (rend == null) return true;
            Bounds b = new Bounds(rend.bounds.center, rend.bounds.size + Vector3.one * 2f); // 1 m around it (Expand() on a copy is lost in Udon)
            return b.Intersects(c.bounds);
        }

        // PostLateUpdate: after pickups and Animators moved the sources and effectors
        public override void PostLateUpdate()
        {
            if (!simulate && !reset) return;
            if (onlyWhenVisible && !rend.isVisible && !reset) return;
            float frame = Mathf.Max(Time.deltaTime, 1e-4f);
            bool liquid = domainType == BlendUdonFluidType.Liquid;
            float dt = simulate ? Mathf.Min(frame, 1f / 30f) * timeScale : 0f;
            // liquid: steps of up to 1/45 s (at most 3 a frame, past that it slows down)
            int steps = 1;
            if (liquid && dt > 0f)
            {
                steps = Mathf.Clamp(Mathf.CeilToInt(dt * 45f - 0.01f), 1, 3);
                dt = Mathf.Min(dt / steps, 1f / 45f);
            }

            if (reset && liquid && particleSpacing > 0f)
            {
                // the reset fills the first particles layer by layer (as the shader does): new water starts after them
                Vector3 size = transform.lossyScale;
                int nx = Mathf.Max(1, Mathf.FloorToInt(Mathf.Abs(size.x) / particleSpacing));
                int ny = Mathf.Max(1, Mathf.FloorToInt(Mathf.Abs(size.y) / particleSpacing));
                int nz = Mathf.Max(1, Mathf.FloorToInt(Mathf.Abs(size.z) / particleSpacing));
                int layers = Mathf.Clamp(Mathf.CeilToInt(fillLevel * Mathf.Abs(size.y) / particleSpacing - 0.5f), 0, ny);
                cursor = Mathf.Min(nx * nz * layers, particleCount) % particleCount;
            }
            int nFlow = 0;
            if (flows != null)
                for (int i = 0; i < flows.Length && nFlow < 8; i++)
                {
                    BlendUdonFluidFlow f = flows[i];
                    if (f == null || !f.enabled || !f.gameObject.activeInHierarchy) continue;
                    int type = (int)f.flowType;
                    if (liquid && type != 3) continue;
                    Vector3 p = f.transform.position;
                    Vector3 v = f.useInitialVelocity ? f.initialVelocity + (p - lastFlowPos[i]) / frame * f.source : Vector3.zero;
                    if (reset) v = f.useInitialVelocity ? f.initialVelocity : Vector3.zero;
                    lastFlowPos[i] = p;
                    flowA[nFlow] = new Vector4(p.x, p.y, p.z, f.radius);
                    flowB[nFlow] = new Vector4(v.x, v.y, v.z, type == 1 ? 0f : (type == 3 ? 1f : f.density));
                    if (liquid)
                    {
                        // a tap as wide as the sphere: switch on the next unused particles at its flow rate
                        float speed = f.useInitialVelocity ? Mathf.Max(f.initialVelocity.magnitude, 0.5f) : 1f;
                        float cell = particleSpacing * particleSpacing * particleSpacing;
                        emit[nFlow] += Mathf.PI * f.radius * f.radius * speed / cell * dt * steps;
                        int count = Mathf.Min((int)emit[nFlow], particleCount / 8);
                        emit[nFlow] = Mathf.Min(emit[nFlow] - count, 1f);
                        flowC[nFlow] = new Vector4(0f, f.useInitialVelocity ? 1f : 0f, cursor, count);
                        cursor = (cursor + count) % particleCount;
                    }
                    else
                        flowC[nFlow] = new Vector4(f.temperature, f.useInitialVelocity ? 1f : 0f, type == 0 ? 0f : f.fuel, 0f);
                    nFlow++;
                }
            int nObs = 0;
            if (effectors != null)
                for (int i = 0; i < effectors.Length && nObs < 8; i++)
                {
                    SphereCollider c = effectors[i];
                    if (i >= lastObsPos.Length) { Vector3[] grown = new Vector3[effectors.Length]; System.Array.Copy(lastObsPos, grown, lastObsPos.Length); lastObsPos = grown; } // players' bodies added at runtime
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !Near(c)) continue;
                    Vector3 cs = c.transform.lossyScale;
                    float r = c.radius * Mathf.Max(Mathf.Abs(cs.x), Mathf.Max(Mathf.Abs(cs.y), Mathf.Abs(cs.z)));
                    Vector3 p = c.transform.TransformPoint(c.center);
                    Vector3 v = reset ? Vector3.zero : (p - lastObsPos[i]) / frame;
                    lastObsPos[i] = p;
                    obsA[nObs] = new Vector4(p.x, p.y, p.z, r);
                    obsB[nObs] = new Vector4(v.x, v.y, v.z, 0f);
                    nObs++;
                }
            int nField = 0;
            if (forceFields != null)
                for (int i = 0; i < forceFields.Length && nField < 4; i++)
                {
                    BlendUdonForceField f = forceFields[i];
                    if (f == null || !f.enabled || !f.gameObject.activeInHierarchy) continue;
                    fieldA[nField] = f.packA; fieldB[nField] = f.packB; fieldC[nField] = f.packC;
                    nField++;
                }

            Matrix4x4 d2w = transform.localToWorldMatrix, w2d = transform.worldToLocalMatrix;
            Material m = simMat;
            m.SetMatrix(idD2W, d2w);
            m.SetMatrix(idW2D, w2d);
            m.SetFloat(idDt, dt);
            m.SetFloat(idReset, reset ? 1f : 0f);
            m.SetFloat(idObsCount, nObs);
            m.SetVectorArray(idObsA, obsA);
            m.SetVectorArray(idObsB, obsB);
            m.SetFloat(idFlowCount, simulate ? nFlow : 0);
            m.SetVectorArray(idFlowA, flowA);
            m.SetVectorArray(idFlowB, flowB);
            m.SetVectorArray(idFlowC, flowC);
            m.SetVector(idKeep, liquid ? Vector4.one : new Vector4(dissolve > 0f ? Mathf.Exp(-dt / dissolve) : 1f, Mathf.Exp(-dt / coolTime), 0f, 0f));
            m.SetVector(idFire, liquid ? Vector4.zero : new Vector4(reactionSpeed, flameSmoke, flameHeat, Mathf.Exp(-dt / flameFade)));
            m.SetVector(idBuoyancy, liquid ? Vector4.zero : new Vector4(buoyancyDensity, buoyancyHeat, 0f, 0f));
            m.SetVector(idUp, Vector3.up);
            m.SetFloat(idVorticity, liquid ? 0f : vorticity);
            m.SetVector(idGravity, gravity);
            m.SetFloat(idFill, fillLevel);
            m.SetFloat(idSeed, Time.frameCount % 100000);
            m.SetFloat(idViscosity, viscosity);
            m.SetVectorArray(idFieldA, fieldA);
            m.SetVectorArray(idFieldB, fieldB);
            m.SetVectorArray(idFieldC, fieldC);
            m.SetFloat(idFieldCount, nField);

            // one update runs the baked zones (gas: pressure iterations, then the step; liquid: one particle step)
            simulation.Update(steps);
            volume.Update(1);
            reset = false;
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Fluid Domain")]
        void Bake()
        {
            Vector3 s = transform.lossyScale;
            s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            float max = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            // cubic cells: the longest side gets Resolution cells
            int dx = Mathf.Max(4, Mathf.RoundToInt(resolution * s.x / max));
            int dy = Mathf.Max(4, Mathf.RoundToInt(resolution * s.y / max));
            int dz = Mathf.Max(4, Mathf.RoundToInt(resolution * s.z / max));
            var dims = new Vector4(dx, dy, dz, 0);

            bool liquid = domainType == BlendUdonFluidType.Liquid;
            var shader = Shader.Find(liquid ? "Hidden/Blend Property/Liquid Sim" : "Hidden/Blend Property/Fluid Sim");
            var simMat = new Material(shader) { name = "Simulation" };
            var copyMat = new Material(shader) { name = liquid ? "Gather" : "Copy" };
            int tiles = 0, side = 0, rw, rh;
            var props = new System.Collections.Generic.Dictionary<string, Vector4>();
            System.Collections.Generic.List<int> passes = new System.Collections.Generic.List<int>();
            if (liquid)
            {
                // particles: N = side² (a power of 4, for the bitonic sort), five side x side regions
                side = maxParticles <= 4096 ? 64 : (maxParticles <= 16384 ? 128 : 256);
                int count = side * side, log = Mathf.RoundToInt(Mathf.Log(count, 2));
                // spacing: N particles fill the whole box; kernel radius 2 spacings, grid cells one kernel radius
                float spacing = Mathf.Pow(s.x * s.y * s.z / count, 1f / 3f), h = 2f * spacing;
                var grid = new Vector3(Mathf.CeilToInt(s.x / h), Mathf.CeilToInt(s.y / h), Mathf.CeilToInt(s.z / h));
                // rest density and the solver's scale, from a full lattice (same kernels as the shader)
                float poly = 315f / (64f * Mathf.PI * Mathf.Pow(h, 9)), spiky = 45f / (Mathf.PI * Mathf.Pow(h, 6));
                float rest = 0f;
                var grads = new System.Collections.Generic.List<Vector3>();
                for (int z = -2; z <= 2; z++) for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++)
                {
                    Vector3 d = new Vector3(x, y, z) * spacing;
                    float len = d.magnitude;
                    if (len >= h) continue;
                    rest += poly * Mathf.Pow(h * h - len * len, 3);
                    if (len > 1e-6f) grads.Add(-spiky * (h - len) * (h - len) * d / len);
                }
                float g2 = 0f;
                foreach (Vector3 g in grads) g2 += (g / rest).sqrMagnitude;
                passes.Add(0);
                for (int i = 0; i < log * (log + 1) / 2; i++) passes.Add(1);
                passes.Add(2);
                for (int i = 0; i < solverIterations; i++) { passes.Add(3); passes.Add(4); }
                passes.Add(5); passes.Add(6);
                props["_S"] = Vector4.one * side;
                props["_Size"] = s;
                props["_Grid"] = grid;
                props["_Spacing"] = Vector4.one * spacing;
                props["_H"] = Vector4.one * h;
                props["_Rest"] = Vector4.one * rest;
                props["_Eps"] = Vector4.one * (0.01f * g2);
                props["_Scorr"] = Vector4.one * (0.1f / g2);
                props["_SortStart"] = Vector4.one;
                rw = side * 5; rh = side;
                UnityEditor.Undo.RecordObject(this, "Bake Fluid Domain");
                particleSpacing = spacing; particleCount = count;
            }
            else
            {
                // two regions side by side (velocity + pressure | density + heat), each a W x H tile per Z slice
                tiles = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(dz * dy / (2f * dx))), 1, dz);
                rw = dx * tiles * 2; rh = dy * Mathf.CeilToInt(dz / (float)tiles);
                // each update: PressureFirst, Pressure x (iterations - 1), Step
                int n = Mathf.Max(2, pressureIterations);
                for (int i = 0; i < n; i++) passes.Add(i == 0 ? 0 : 1);
                passes.Add(2);
                props["_Dims"] = dims;
                props["_Tiles"] = Vector4.one * tiles;
            }
            var simTex = new CustomRenderTexture(rw, rh, liquid ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "Simulation", material = simMat, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnLoad, initializationColor = Color.clear,
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
                updateZoneSpace = CustomRenderTextureUpdateZoneSpace.Normalized,
            };
            // every zone covers the whole texture (each pass copies the parts it doesn't change)
            var zones = new CustomRenderTextureUpdateZone[passes.Count];
            for (int i = 0; i < zones.Length; i++)
                zones[i] = new CustomRenderTextureUpdateZone
                {
                    updateZoneCenter = new Vector3(0.5f, 0.5f, 0.5f), updateZoneSize = Vector3.one, rotation = 0f,
                    passIndex = passes[i], needSwap = i > 0,
                };
            simTex.SetUpdateZones(zones);
            var volTex = new CustomRenderTexture(dx, dy, liquid ? RenderTextureFormat.RHalf : RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = liquid ? "Liquid Volume" : "Smoke Volume", dimension = UnityEngine.Rendering.TextureDimension.Tex3D, volumeDepth = dz,
                material = copyMat, shaderPass = liquid ? 7 : 3, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnLoad, initializationColor = Color.clear,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            void Link()
            {
                foreach (var m in new[] { simMat, copyMat })
                    foreach (var kv in props)
                    {
                        if (kv.Key == "_Size" || kv.Key == "_Grid" || kv.Key == "_Dims") m.SetVector(kv.Key, kv.Value);
                        else m.SetFloat(kv.Key, kv.Value.x);
                    }
                copyMat.SetTexture("_Sim", simTex);
            }
            Link();

            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = volume != null && UnityEditor.AssetDatabase.Contains(volume)
                ? UnityEditor.AssetDatabase.GetAssetPath(volume)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_fluid.asset");
            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(volTex, path);
            var parts = new Object[] { simTex, simMat, copyMat };
            foreach (Object o in parts) UnityEditor.AssetDatabase.AddObjectToAsset(o, path);
            foreach (Object o in parts) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();
            // textures written before their materials were assets lose the link: set them again
            simTex.material = simMat; volTex.material = copyMat;
            Link();
            foreach (Object o in new Object[] { volTex, simTex, simMat, copyMat }) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();

            // the domain shows the smoke / liquid: point its material at the volume, or give it one
            var r = GetComponent<Renderer>();
            if (r != null)
            {
                var smoke = r.sharedMaterial;
                string look = domainType == BlendUdonFluidType.Liquid ? "Blend Property/Liquid" : "Blend Property/Smoke";
                if (smoke == null || smoke.shader.name != look)
                {
                    smoke = new Material(Shader.Find(look)) { name = domainType.ToString() };
                    UnityEditor.AssetDatabase.AddObjectToAsset(smoke, path);
                    UnityEditor.Undo.RecordObject(r, "Bake Fluid Domain");
                    r.sharedMaterial = smoke;
                }
                smoke.SetTexture("_Density", volTex);
                UnityEditor.EditorUtility.SetDirty(smoke);
                UnityEditor.AssetDatabase.SaveAssets();
            }

            UnityEditor.Undo.RecordObject(this, "Bake Fluid Domain");
            simulation = simTex; volume = volTex;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonFluid] Baked a {dx}x{dy}x{dz} domain to {path}.", this);
        }
#endif
    }
}
