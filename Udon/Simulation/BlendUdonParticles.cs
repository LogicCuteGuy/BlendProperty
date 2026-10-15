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
    public enum BlendUdonEmitFrom { Verts, Faces }
    public enum BlendUdonRenderAs { Halo, Object }
    public enum BlendUdonParticleShape { Mesh, Sphere, Hemisphere, Cone, Box, Circle, Edge }
    public enum BlendUdonSimulationSpace { World, Local }
    public enum BlendUdonBillboard { Billboard, Stretched, Horizontal, Vertical }

    /// <summary>
    /// GPU particle system: Blender's Particle System (Emitter) with Unity Particle System modules. Particles are born
    /// from a Shape (this mesh, or a sphere, cone, box, ...) at a Rate, in Bursts or by Emit(), live Lifetime seconds,
    /// are pushed by gravity, noise, Force Fields and drag, bounce off Sphere, Capsule and Box colliders, and change
    /// colour, size, rotation and texture frame over their life. Drawn by the "Particles" renderer the bake makes
    /// (shader Blend Property/Particles), so tens of thousands are fine and Udon only sends a few settings a frame.
    /// Move or animate this object to move the emitter. The inspector's Preview plays it in the Scene view. Changing
    /// Max Particles, Emit From, Render As or Instance Mesh rebakes. Local only (not synced): each player sees their own.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/Particles.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Particle System")]
    public class BlendUdonParticles : UdonSharpBehaviour
    {
        [Header("Main")]
        [Tooltip("New particles are born while on (Unity Play On Awake / Play, Stop). Off: the ones in the air finish their lives.")]
        public bool emitting = true;
        [Min(0.05f), Tooltip("Seconds of one cycle: Bursts repeat every Duration while Looping.")]
        public float duration = 5f;
        [Tooltip("Off: emission stops after Duration (Play starts it again).")]
        public bool looping = true;
        [Min(0.01f), Tooltip("Seconds each particle lives (Start Lifetime).")]
        public float lifetime = 3f;
        [Range(0, 1), Tooltip("Some particles die sooner, by up to this share of Lifetime (Blender Lifetime Randomness).")]
        public float lifetimeRandom;
        [Tooltip("Start Speed: m/s along the Shape's direction (Sphere and Circle: outward, Cone: along its opening, " +
                 "Box and Edge: up, Mesh: the surface normal; Blender Normal).")]
        public float normal = 1f;
        [Range(0, 1), Tooltip("Some particles are slower, by up to this share of Start Speed.")]
        public float speedRandom;
        [Min(0f), Tooltip("Start Size. Halo: metres across (Blender Display Size). Object: scale of Instance Mesh (1 = its own size).")]
        public float size = 0.05f;
        [Range(0, 1), Tooltip("Some particles are smaller, by up to this share (Blender Randomize Size).")]
        public float sizeRandom;
        [Tooltip("Start Color: each particle gets a random colour between this and Start Color 2 (times the material's colour).")]
        public Color startColor = Color.white;
        public Color startColor2 = Color.white;
        [Tooltip("Start Rotation of the quads, degrees.")]
        public float startRotation;
        [Min(0f), Tooltip("Each particle starts turned by up to ± this many more degrees.")]
        public float startRotationRandom;
        [Tooltip("World: particles stay where they were born when the emitter moves. Local: they move and turn with it.")]
        public BlendUdonSimulationSpace simulationSpace;
        [Tooltip("Metres per second squared, world space.")]
        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Header("Emission")]
        [Tooltip("On: Max Particles / Lifetime per second, a steady stream that keeps them all in the air (Blender). Off: Rate.")]
        public bool autoRate = true;
        [Min(0f), Tooltip("Particles born per second (Rate over Time), when Auto Rate is off.")]
        public float rate = 10f;
        [Min(0f), Tooltip("Particles born per metre the emitter moves (Rate over Distance).")]
        public float rateOverDistance;
        [Tooltip("Bursts: x = time (s into Duration), y = count, z = cycles (0 = every Interval until Duration ends), w = interval (s).")]
        public Vector4[] bursts;

        [Header("Shape")]
        [Tooltip("Where particles are born, in this object's space. Mesh: this object's mesh (Emit From). The others point up (+Y).")]
        public BlendUdonParticleShape shape;
        [Min(0f), Tooltip("Radius of the Sphere, Hemisphere, Cone base and Circle; half length of the Edge.")]
        public float shapeRadius = 0.5f;
        [Range(0, 1), Tooltip("0: born on the surface or rim only. 1: anywhere inside.")]
        public float radiusThickness = 1f;
        [Range(0, 90), Tooltip("Cone: degrees the particles spread out from straight up at its rim.")]
        public float coneAngle = 25f;
        [Tooltip("Box: its size in metres.")]
        public Vector3 boxSize = Vector3.one;

        [Header("Velocity")]
        [Tooltip("m/s along this object's axes at birth (Blender Object Aligned X / Y / Z, Unity axes).")]
        public Vector3 objectAligned;
        [Tooltip("Share of the emitter's own speed passed on (Blender Object Velocity / Unity Inherit Velocity).")]
        public float objectVelocity;
        [Min(0f), Tooltip("m/s in a random direction at birth, up to this (Blender Randomize).")]
        public float random;
        [Tooltip("m/s added to every particle's motion all its life, world space (Velocity over Lifetime).")]
        public Vector3 velocityOverLifetime;
        [Min(0f), Tooltip("Particles never go faster than this, m/s (Limit Velocity). 0 = no limit.")]
        public float maxSpeed;

        [Header("Forces")]
        [Tooltip("Blender Force Fields acting on the particles (up to 4).")]
        public BlendUdonForceField[] forceFields;
        [Range(0, 1), Tooltip("Velocity lost per 1/30 s (Blender Damp / Unity Drag).")]
        public float damping;
        [Min(0f), Tooltip("Random jitter (Blender Brownian).")]
        public float brownian;
        [Min(0f), Tooltip("Noise: m/s² of a smooth swirling push.")]
        public float noiseStrength;
        [Min(0f), Tooltip("Noise: swirls per metre (higher = smaller swirls).")]
        public float noiseFrequency = 1f;
        [Tooltip("Noise: how fast the swirls change.")]
        public float noiseScrollSpeed = 0.5f;
        [Range(1, 8), Tooltip("GPU steps per frame. More stops fast particles passing through thin colliders.")]
        public int substeps = 2;

        [Header("Over Lifetime")]
        [Tooltip("On: colour follows the gradient over each particle's life (times the material's Color). Off: the material's Color to End Color.")]
        public bool useColorOverLifetime;
        public Gradient colorOverLifetime = new Gradient();
        [Tooltip("On: size follows the curve over each particle's life (0..1 across, value times Size). Off: the material's End Size.")]
        public bool useSizeOverLifetime;
        public AnimationCurve sizeOverLifetime = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        [Tooltip("Rotation over Lifetime: degrees per second the quads turn (Render As Object: the material's Spin).")]
        public float rotationSpeed;
        [Min(0f), Tooltip("Each particle turns up to ± this many degrees per second faster or slower.")]
        public float rotationSpeedRandom;

        [Header("Texture Sheet Animation")]
        [Min(1), Tooltip("Columns of frames in the material's texture (1 x 1 = no animation). Turn off Round on the material for sheets.")]
        public int tilesX = 1;
        [Min(1), Tooltip("Rows of frames, played left to right, top to bottom.")]
        public int tilesY = 1;
        [Min(0f), Tooltip("Times the frames play over a particle's life.")]
        public float sheetCycles = 1f;
        [Tooltip("Each particle starts on a random frame.")]
        public bool randomStartFrame;

        [Header("Renderer")]
        [Tooltip("Billboard: faces the camera. Stretched: long along its motion. Horizontal: lies flat. Vertical: upright, facing the camera.")]
        public BlendUdonBillboard renderMode;
        [Min(0f), Tooltip("Stretched: length as a share of Size.")]
        public float lengthScale = 2f;
        [Min(0f), Tooltip("Stretched: extra length per m/s of speed.")]
        public float speedScale;
        [Tooltip("Draws the particles (made by the bake), with a Blend Property/Particles material.")]
        public Renderer particleRenderer;

        [Header("Collision")]
        public SphereCollider[] sphereColliders;
        public CapsuleCollider[] capsuleColliders;
        public BoxCollider[] boxColliders;
        [Tooltip("Only colliders on these layers are hit.")]
        public LayerMask collisionLayers = -1;

        [Header("Baked (changing these rebakes)")]
        [Delayed, Min(1), Tooltip("Max Particles: alive at once at most (Blender Number). When all are alive, new ones replace the oldest.")]
        public int number = 2000;
        [Tooltip("Shape Mesh: born on the mesh's vertices or anywhere on its faces.")]
        public BlendUdonEmitFrom emitFrom = BlendUdonEmitFrom.Faces;
        [Tooltip("Halo: a camera-facing quad. Object: a copy of Instance Mesh per particle, lit (Blender Render As " +
                 "Object / Particle Instance; Unity Mesh render mode).")]
        public BlendUdonRenderAs renderAs;
        [Tooltip("Object: the mesh drawn for each particle (needs Read/Write). Number × its vertices must stay under 4 million.")]
        public Mesh instanceMesh;
        public CustomRenderTexture state;

        Material sim, draw;
        // each collider's Blend Udon Collision (bounce, friction, kill), looked up again when a list changes
        SphereCollider[] surfSpheresOf; CapsuleCollider[] surfCapsulesOf; BoxCollider[] surfBoxesOf;
        BlendUdonCollision[] sphereCol, capsuleCol, boxCol;
        Vector4[] spheres, capA, capB, boxC, boxX, boxY, boxZ, fieldA, fieldB, fieldC, colorKeys, sizeKeys, sphereSurf, capSurf, boxSurf;
        float simTime, playTime, accum;
        int frame, head, pending;
        bool reset;
        Vector3 lastPos;
        int idObjectToSim, idSimToWorld, idWorldToSim, idBaseVel, idEmitterVel, idVelOverLife, idGravity, idSimTime, idDt,
            idReset, idFrame, idHead, idEmitCount, idEmitDt, idLifetime, idLifeRandom, idNormal, idSpeedRandom, idRandom,
            idKeep, idBrownian, idMaxSpeed, idNoise, idShape, idShapeRadius, idThickness, idConeAngle, idBoxSize,
            idRadius, idSphereSurf, idCapSurf, idBoxSurf, idSize, idSizeRandom,
            idRenderMode, idStretch, idRotation, idSheet, idStartColor, idStartColor2, idUseColorGrad, idColorGrad,
            idUseSizeCurve, idSizeCurve,
            idSpheres, idSphereCount, idCapA, idCapB, idCapsuleCount, idBoxC, idBoxX, idBoxY, idBoxZ, idBoxCount,
            idFieldA, idFieldB, idFieldC, idFieldCount;

        void Start()
        {
            if (state == null || particleRenderer == null)
            {
                Debug.LogWarning("[BlendUdonParticles] Not baked: use ⋮ → Bake Particles.", this);
                enabled = false;
                return;
            }
            Init(particleRenderer.material);
        }

        /// <summary>Gets ready to simulate, drawing with drawMaterial (the editor's Preview passes the shared one).</summary>
        public void Init(Material drawMaterial)
        {
            sim = state.material;
            draw = drawMaterial;
            draw.SetTexture("_State", state); // any Blend Property/Particles material works
            spheres = new Vector4[8]; capA = new Vector4[8]; capB = new Vector4[8];
            boxC = new Vector4[8]; boxX = new Vector4[8]; boxY = new Vector4[8]; boxZ = new Vector4[8];
            fieldA = new Vector4[4]; fieldB = new Vector4[4]; fieldC = new Vector4[4];
            colorKeys = new Vector4[8]; sizeKeys = new Vector4[8];
            sphereSurf = new Vector4[8]; capSurf = new Vector4[8]; boxSurf = new Vector4[8];
            idObjectToSim = VRCShader.PropertyToID("_ObjectToSim");
            idSimToWorld = VRCShader.PropertyToID("_SimToWorld");
            idWorldToSim = VRCShader.PropertyToID("_WorldToSim");
            idBaseVel = VRCShader.PropertyToID("_BaseVel");
            idEmitterVel = VRCShader.PropertyToID("_EmitterVel");
            idVelOverLife = VRCShader.PropertyToID("_VelOverLife");
            idGravity = VRCShader.PropertyToID("_Gravity");
            idSimTime = VRCShader.PropertyToID("_SimTime");
            idDt = VRCShader.PropertyToID("_Dt");
            idReset = VRCShader.PropertyToID("_Reset");
            idFrame = VRCShader.PropertyToID("_Frame");
            idHead = VRCShader.PropertyToID("_Head");
            idEmitCount = VRCShader.PropertyToID("_EmitCount");
            idEmitDt = VRCShader.PropertyToID("_EmitDt");
            idLifetime = VRCShader.PropertyToID("_Lifetime");
            idLifeRandom = VRCShader.PropertyToID("_LifeRandom");
            idNormal = VRCShader.PropertyToID("_Normal");
            idSpeedRandom = VRCShader.PropertyToID("_SpeedRandom");
            idRandom = VRCShader.PropertyToID("_Random");
            idKeep = VRCShader.PropertyToID("_Keep");
            idBrownian = VRCShader.PropertyToID("_Brownian");
            idMaxSpeed = VRCShader.PropertyToID("_MaxSpeed");
            idNoise = VRCShader.PropertyToID("_Noise");
            idShape = VRCShader.PropertyToID("_Shape");
            idShapeRadius = VRCShader.PropertyToID("_ShapeRadius");
            idThickness = VRCShader.PropertyToID("_Thickness");
            idConeAngle = VRCShader.PropertyToID("_ConeAngle");
            idBoxSize = VRCShader.PropertyToID("_BoxSize");
            idRadius = VRCShader.PropertyToID("_Radius");
            idSphereSurf = VRCShader.PropertyToID("_SphereSurf");
            idCapSurf = VRCShader.PropertyToID("_CapsuleSurf");
            idBoxSurf = VRCShader.PropertyToID("_BoxSurf");
            idSize = VRCShader.PropertyToID("_Size");
            idSizeRandom = VRCShader.PropertyToID("_SizeRandom");
            idRenderMode = VRCShader.PropertyToID("_RenderMode");
            idStretch = VRCShader.PropertyToID("_Stretch");
            idRotation = VRCShader.PropertyToID("_Rotation");
            idSheet = VRCShader.PropertyToID("_Sheet");
            idStartColor = VRCShader.PropertyToID("_StartColor");
            idStartColor2 = VRCShader.PropertyToID("_StartColor2");
            idUseColorGrad = VRCShader.PropertyToID("_UseColorGrad");
            idColorGrad = VRCShader.PropertyToID("_ColorGrad");
            idUseSizeCurve = VRCShader.PropertyToID("_UseSizeCurve");
            idSizeCurve = VRCShader.PropertyToID("_SizeCurve");
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

        /// <summary>Remove every particle and start emitting again from the beginning (Unity Clear + restart).</summary>
        public void ResetSimulation()
        {
            reset = true;
            simTime = 0f;
            playTime = 0f;
            accum = 0f;
            pending = 0;
            head = 0;
            lastPos = transform.position;
        }

        public void StartEmitting() { emitting = true; }
        public void StopEmitting() { emitting = false; }
        /// <summary>Start emitting from the start of Duration (Bursts at time 0 fire again), keeping particles in the air.</summary>
        public void Play() { emitting = true; playTime = 0f; }
        /// <summary>Stop emitting; the particles in the air finish their lives.</summary>
        public void Stop() { emitting = false; }
        /// <summary>Remove every particle.</summary>
        public void Clear() { ResetSimulation(); }
        /// <summary>Emit count particles now (next frame), even while not emitting.</summary>
        public void Emit(int count) { pending += Mathf.Max(0, count); }
        /// <summary>Emit one Burst: 30 particles. For other counts call Emit(count).</summary>
        public void EmitBurst() { Emit(30); }

        // PostLateUpdate: after pickups and Animators moved the emitter and colliders
        public override void PostLateUpdate() { Step(Time.deltaTime); }

        /// <summary>Simulate frameDt seconds. Called every frame; the editor's Preview calls it too.</summary>
        public void Step(float frameDt)
        {
            float dt = Mathf.Clamp(frameDt, 0f, 1f / 20f); // a hitch doesn't throw particles through colliders
            int n = Mathf.Max(1, substeps);
            float h = dt / n;
            Vector3 pos = transform.position;
            Vector3 move = reset ? Vector3.zero : pos - lastPos;
            Vector3 emitterVel = frameDt > 0f ? move / frameDt : Vector3.zero;
            lastPos = pos;
            simTime += dt;
            frame = frame >= 0xFFFFFF ? 1 : frame + 1;
            int count = reset ? 0 : Births(dt, move.magnitude);

            bool local = simulationSpace == BlendUdonSimulationSpace.Local;
            Matrix4x4 l2w = transform.localToWorldMatrix;
            Matrix4x4 worldToSim = local ? transform.worldToLocalMatrix : Matrix4x4.identity;
            Vector3 baseVel = transform.TransformDirection(objectAligned) + (local ? Vector3.zero : emitterVel * objectVelocity);
            sim.SetMatrix(idObjectToSim, local ? Matrix4x4.identity : l2w);
            sim.SetMatrix(idSimToWorld, local ? l2w : Matrix4x4.identity);
            sim.SetMatrix(idWorldToSim, worldToSim);
            sim.SetVector(idBaseVel, worldToSim.MultiplyVector(baseVel));
            sim.SetVector(idEmitterVel, local ? Vector3.zero : emitterVel);
            sim.SetVector(idVelOverLife, worldToSim.MultiplyVector(velocityOverLifetime));
            sim.SetVector(idGravity, gravity);
            sim.SetFloat(idSimTime, simTime);
            sim.SetFloat(idDt, h);
            sim.SetFloat(idReset, reset ? 1f : 0f);
            sim.SetFloat(idFrame, frame);
            sim.SetFloat(idHead, head);
            sim.SetFloat(idEmitCount, count);
            sim.SetFloat(idEmitDt, dt);
            sim.SetFloat(idLifetime, lifetime);
            sim.SetFloat(idLifeRandom, lifetimeRandom);
            sim.SetFloat(idNormal, normal);
            sim.SetFloat(idSpeedRandom, speedRandom);
            sim.SetFloat(idRandom, random);
            sim.SetFloat(idKeep, Mathf.Pow(1f - damping, h * 30f));
            sim.SetFloat(idBrownian, brownian);
            sim.SetFloat(idMaxSpeed, maxSpeed);
            sim.SetVector(idNoise, new Vector4(noiseStrength, noiseFrequency, noiseScrollSpeed, 0f));
            sim.SetFloat(idShape, (float)(int)shape);
            sim.SetFloat(idShapeRadius, shapeRadius);
            sim.SetFloat(idThickness, radiusThickness);
            sim.SetFloat(idConeAngle, coneAngle * Mathf.Deg2Rad);
            sim.SetVector(idBoxSize, boxSize);
            sim.SetFloat(idRadius, size * 0.5f);
            SetColliders();
            SetFields();
            SetLook(local ? l2w : Matrix4x4.identity);
            state.Update(reset ? 1 : n);
            head = (head + count) % Mathf.Max(1, number);
            reset = false;
        }

        // particles born this frame: Rate, Rate over Distance, Bursts, Emit()
        int Births(float dt, float moved)
        {
            float t0 = playTime;
            playTime += dt;
            float d = Mathf.Max(duration, 0.05f);
            if (emitting && (looping || t0 < d))
            {
                accum += (autoRate ? number / Mathf.Max(lifetime, 0.01f) : rate) * dt + rateOverDistance * moved;
                if (!looping) accum += Bursts(t0, Mathf.Min(playTime, d));
                else
                {
                    float a = t0 % d, b = a + dt;
                    accum += Bursts(a, Mathf.Min(b, d));
                    if (b > d) accum += Bursts(0f, b - d);
                }
            }
            accum += pending;
            pending = 0;
            int count = (int)accum;
            accum -= count;
            return Mathf.Min(count, Mathf.Max(1, number));
        }

        // burst particles due in [a, b) seconds of the cycle
        float Bursts(float a, float b)
        {
            float total = 0f;
            if (bursts == null) return total;
            for (int i = 0; i < bursts.Length; i++)
            {
                Vector4 bu = bursts[i];
                if (bu.w <= 0f) { if (bu.x >= a && bu.x < b) total += bu.y; continue; } // once
                int first = Mathf.Max(0, Mathf.CeilToInt((a - bu.x) / bu.w));
                int last = Mathf.CeilToInt((b - bu.x) / bu.w) - 1;
                if (bu.z >= 1f) last = Mathf.Min(last, (int)bu.z - 1);
                if (last >= first) total += (last - first + 1) * bu.y;
            }
            return total;
        }

        // the draw material's per-particle look: size, colour, rotation, texture sheet, render mode
        void SetLook(Matrix4x4 simToWorld)
        {
            for (int k = 0; k < 8; k++)
            {
                float t = k / 7f;
                Color c = colorOverLifetime != null ? colorOverLifetime.Evaluate(t).linear : Color.white;
                colorKeys[k] = new Vector4(c.r, c.g, c.b, c.a);
                sizeKeys[k] = new Vector4(sizeOverLifetime != null ? sizeOverLifetime.Evaluate(t) : 1f, 0f, 0f, 0f);
            }
            draw.SetMatrix(idSimToWorld, simToWorld);
            draw.SetFloat(idSize, size);
            draw.SetFloat(idSizeRandom, sizeRandom);
            draw.SetFloat(idLifetime, lifetime);
            draw.SetFloat(idRenderMode, (float)(int)renderMode);
            draw.SetVector(idStretch, new Vector4(lengthScale, speedScale, 0f, 0f));
            draw.SetVector(idRotation, new Vector4(startRotation, startRotationRandom, rotationSpeed, rotationSpeedRandom) * Mathf.Deg2Rad);
            draw.SetVector(idSheet, new Vector4(tilesX, tilesY, sheetCycles, randomStartFrame ? 1f : 0f));
            draw.SetColor(idStartColor, startColor);
            draw.SetColor(idStartColor2, startColor2);
            draw.SetFloat(idUseColorGrad, useColorOverLifetime ? 1f : 0f);
            draw.SetVectorArray(idColorGrad, colorKeys);
            draw.SetFloat(idUseSizeCurve, useSizeOverLifetime ? 1f : 0f);
            draw.SetVectorArray(idSizeCurve, sizeKeys);
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
            if (particleRenderer == null) return true;
            Bounds b = new Bounds(particleRenderer.bounds.center, particleRenderer.bounds.size + Vector3.one * 2f); // 1 m around it (Expand() on a copy is lost in Udon)
            return b.Intersects(c.bounds) && (collisionLayers.value & (1 << c.gameObject.layer)) != 0;
        }

        BlendUdonCollision Find(Collider c) { return c == null ? null : c.GetComponent<BlendUdonCollision>(); }

        Vector4 Surface(BlendUdonCollision s)
        {
            if (s == null || !s.enabled) return new Vector4(0.3f, 0.2f, 0f, 0f);
            return new Vector4(s.bounce, s.friction, s.killOnHit ? 1f : 0f, 0f);
        }

        void SetColliders()
        {
            if (surfSpheresOf != sphereColliders)
            {
                surfSpheresOf = sphereColliders;
                sphereCol = new BlendUdonCollision[sphereColliders == null ? 0 : sphereColliders.Length];
                for (int i = 0; i < sphereCol.Length; i++) sphereCol[i] = Find(sphereColliders[i]);
            }
            if (surfCapsulesOf != capsuleColliders)
            {
                surfCapsulesOf = capsuleColliders;
                capsuleCol = new BlendUdonCollision[capsuleColliders == null ? 0 : capsuleColliders.Length];
                for (int i = 0; i < capsuleCol.Length; i++) capsuleCol[i] = Find(capsuleColliders[i]);
            }
            if (surfBoxesOf != boxColliders)
            {
                surfBoxesOf = boxColliders;
                boxCol = new BlendUdonCollision[boxColliders == null ? 0 : boxColliders.Length];
                for (int i = 0; i < boxCol.Length; i++) boxCol[i] = Find(boxColliders[i]);
            }
            int count = 0;
            if (sphereColliders != null)
                for (int i = 0; i < sphereColliders.Length && count < 8; i++)
                {
                    SphereCollider c = sphereColliders[i];
                    if (c == null || !c.enabled || !c.gameObject.activeInHierarchy || !Near(c)) continue;
                    Vector3 cs = c.transform.lossyScale;
                    float r = c.radius * Mathf.Max(Mathf.Abs(cs.x), Mathf.Max(Mathf.Abs(cs.y), Mathf.Abs(cs.z)));
                    Vector3 p = c.transform.TransformPoint(c.center);
                    sphereSurf[count] = Surface(sphereCol[i]);
                    spheres[count++] = new Vector4(p.x, p.y, p.z, r);
                }
            sim.SetVectorArray(idSpheres, spheres);
            sim.SetVectorArray(idSphereSurf, sphereSurf);
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
                    Vector3 a = c.transform.TransformPoint(c.center + axis * half);
                    Vector3 b = c.transform.TransformPoint(c.center - axis * half);
                    capA[count] = new Vector4(a.x, a.y, a.z, r);
                    capB[count] = new Vector4(b.x, b.y, b.z, 0f);
                    capSurf[count] = Surface(capsuleCol[i]);
                    count++;
                }
            sim.SetVectorArray(idCapA, capA);
            sim.SetVectorArray(idCapSurf, capSurf);
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
                    Vector3 p = t.TransformPoint(c.center);
                    boxC[count] = new Vector4(p.x, p.y, p.z, 0f);
                    boxX[count] = Axis(t.right, c.size.x * Mathf.Abs(cs.x));
                    boxY[count] = Axis(t.up, c.size.y * Mathf.Abs(cs.y));
                    boxZ[count] = Axis(t.forward, c.size.z * Mathf.Abs(cs.z));
                    boxSurf[count] = Surface(boxCol[i]);
                    count++;
                }
            sim.SetVectorArray(idBoxC, boxC);
            sim.SetVectorArray(idBoxSurf, boxSurf);
            sim.SetVectorArray(idBoxX, boxX);
            sim.SetVectorArray(idBoxY, boxY);
            sim.SetVectorArray(idBoxZ, boxZ);
            sim.SetFloat(idBoxCount, count);
        }

        // world axis, w = half the world size along it
        Vector4 Axis(Vector3 a, float size) { return new Vector4(a.x, a.y, a.z, size * 0.5f); }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Particles")]
        void Bake()
        {
            // Shape Mesh: 4096 birth points on this mesh, random vertices or area-weighted random points on the
            // triangles. The other shapes don't need a mesh (the points stay at the centre).
            var mf = GetComponent<MeshFilter>();
            var src = mf != null ? mf.sharedMesh : null;
            string meshProblem = src == null ? "No mesh (add a MeshFilter, or pick another Shape)." :
                !src.isReadable ? "Enable Read/Write on the mesh import settings." : null;
            Vector3[] verts = meshProblem == null ? src.vertices : new Vector3[0];
            int[] tris = meshProblem == null ? src.triangles : new int[0];
            if (meshProblem == null && (verts.Length == 0 || (emitFrom == BlendUdonEmitFrom.Faces && tris.Length == 0)))
                meshProblem = "The mesh has nothing to emit from.";
            if (meshProblem != null && shape == BlendUdonParticleShape.Mesh) { Debug.LogError("[BlendUdonParticles] " + meshProblem, this); return; }
            var norms = meshProblem == null ? src.normals : null;
            if (norms != null && norms.Length != verts.Length) norms = null;

            const int S = 4096;
            var rng = new System.Random(12345);
            var spawnPx = new Color[128 * 64];
            float[] cdf = null;
            if (meshProblem == null && emitFrom == BlendUdonEmitFrom.Faces)
            {
                cdf = new float[tris.Length / 3];
                float total = 0f;
                for (int f = 0; f < cdf.Length; f++)
                {
                    Vector3 a = verts[tris[f * 3]], b = verts[tris[f * 3 + 1]], c = verts[tris[f * 3 + 2]];
                    total += Vector3.Cross(b - a, c - a).magnitude;
                    cdf[f] = total;
                }
                if (total <= 0f)
                {
                    if (shape == BlendUdonParticleShape.Mesh) { Debug.LogError("[BlendUdonParticles] The mesh's triangles have no area.", this); return; }
                    meshProblem = "no area";
                }
            }
            for (int s = 0; s < S && meshProblem == null; s++)
            {
                Vector3 p, nrm;
                if (cdf == null)
                {
                    int v = rng.Next(verts.Length);
                    p = verts[v];
                    nrm = norms != null ? norms[v] : Vector3.zero;
                }
                else
                {
                    int f = System.Array.BinarySearch(cdf, (float)rng.NextDouble() * cdf[cdf.Length - 1]);
                    if (f < 0) f = ~f;
                    f = Mathf.Min(f, cdf.Length - 1);
                    int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                    float u = (float)rng.NextDouble(), w = (float)rng.NextDouble();
                    if (u + w > 1f) { u = 1f - u; w = 1f - w; }
                    p = verts[i0] + (verts[i1] - verts[i0]) * u + (verts[i2] - verts[i0]) * w;
                    nrm = norms != null ? (norms[i0] * (1f - u - w) + norms[i1] * u + norms[i2] * w)
                                        : Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                }
                if (nrm.sqrMagnitude > 0f) nrm.Normalize();
                int x = s & 63, y = s >> 6;
                spawnPx[y * 128 + x] = new Color(p.x, p.y, p.z, 1f);
                spawnPx[y * 128 + 64 + x] = new Color(nrm.x, nrm.y, nrm.z, 0f);
            }
            var spawn = new Texture2D(128, 64, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Spawn" };
            spawn.SetPixels(spawnPx); spawn.Apply(false, false);

            int N = Mathf.Max(1, number);
            int W = Mathf.CeilToInt(Mathf.Sqrt(N)), H = Mathf.CeilToInt(N / (float)W);
            var simMat = new Material(Shader.Find("Hidden/Blend Property/Particle Sim")) { name = "Sim" };
            simMat.SetTexture("_Spawn", spawn);
            simMat.SetFloat("_W", W);
            simMat.SetFloat("_H", H);
            simMat.SetFloat("_Count", N);
            var stateTex = new CustomRenderTexture(W * 2, H, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "State", material = simMat, shaderPass = 0, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };

            bool asObject = renderAs == BlendUdonRenderAs.Object;
            if (asObject && (instanceMesh == null || !instanceMesh.isReadable || (long)instanceMesh.vertexCount * N > 4000000))
            { Debug.LogError("[BlendUdonParticles] Render As Object needs a Read/Write Instance Mesh with Number × vertices under 4 million.", this); return; }
            // one camera-facing quad per particle; the vertex shader places it, so the vertices only carry indices
            var quadV = new Vector3[N * 4];
            var quadUV = new Vector2[N * 4];
            var quadData = new System.Collections.Generic.List<Vector3>(N * 4);
            var quadI = new int[N * 6];
            for (int i = 0; i < N; i++)
            {
                var d = new Vector3((i % W + 0.5f) / (2 * W), (i / W + 0.5f) / H, (float)rng.NextDouble());
                for (int k = 0; k < 4; k++) quadData.Add(d);
                quadUV[i * 4] = new Vector2(0, 0); quadUV[i * 4 + 1] = new Vector2(1, 0);
                quadUV[i * 4 + 2] = new Vector2(1, 1); quadUV[i * 4 + 3] = new Vector2(0, 1);
                quadI[i * 6] = i * 4; quadI[i * 6 + 1] = i * 4 + 1; quadI[i * 6 + 2] = i * 4 + 2;
                quadI[i * 6 + 3] = i * 4; quadI[i * 6 + 4] = i * 4 + 2; quadI[i * 6 + 5] = i * 4 + 3;
            }
            var quads = new Mesh { name = "Particles", indexFormat = N * 4 > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            quads.vertices = quadV;
            quads.uv = quadUV;
            quads.SetUVs(1, quadData);
            quads.triangles = quadI;
            if (asObject) quads = Instances(N, W, H, rng);
            // ponytail: culling box from how far particles can get in one Lifetime; enlarge the renderer's mesh bounds if they pop
            float reach = Reach();
            quads.bounds = new Bounds(Vector3.zero, Vector3.one * (2f * reach));

            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = state != null && UnityEditor.AssetDatabase.Contains(state)
                ? UnityEditor.AssetDatabase.GetAssetPath(state)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_particles.asset");

            // the renderer: reuse it (and its material), or make a child with a default material
            Material drawMat = particleRenderer != null ? particleRenderer.sharedMaterial : null;
            string shaderName = asObject ? "Blend Property/Particle Instance" : "Blend Property/Particles";
            // Render As Object also keeps your own material when its shader includes BlendParticleInstance.cginc (has _State)
            bool keepOwn = asObject && drawMat != null && drawMat.shader != null && drawMat.shader.name != "Blend Property/Particles" && drawMat.HasProperty("_State");
            bool ownMat = drawMat == null || drawMat.shader == null || (drawMat.shader.name != shaderName && !keepOwn) || (UnityEditor.AssetDatabase.GetAssetPath(drawMat) == path);
            if (ownMat)
            {
                var previous = drawMat;
                drawMat = new Material(Shader.Find(shaderName)) { name = "Particles" };
                if (asObject) BlendProperty.MaterialTransfer.Copy(previous, drawMat); // keep the look of a material on another shader
                else
                {
                    drawMat.SetColor("_Color", new Color(1f, 0.8f, 0.4f, 1f));
                    drawMat.SetColor("_EndColor", new Color(1f, 0.3f, 0.1f, 0f));
                }
            }
            drawMat.SetTexture("_State", stateTex);

            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(stateTex, path);
            var parts = ownMat ? new Object[] { simMat, spawn, quads, drawMat } : new Object[] { simMat, spawn, quads };
            foreach (Object o in parts) UnityEditor.AssetDatabase.AddObjectToAsset(o, path);
            stateTex.material = simMat; // written before its material was an asset: link again so it sticks
            UnityEditor.EditorUtility.SetDirty(stateTex);
            foreach (Object o in parts) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();

            Renderer r = particleRenderer;
            if (r == null)
            {
                var go = new GameObject("Particles");
                UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Bake Particles");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>();
                r = go.AddComponent<MeshRenderer>();
            }
            UnityEditor.Undo.RecordObject(r, "Bake Particles");
            UnityEditor.Undo.RecordObject(r.GetComponent<MeshFilter>(), "Bake Particles");
            r.GetComponent<MeshFilter>().sharedMesh = quads;
            r.sharedMaterial = drawMat;
            r.shadowCastingMode = asObject ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = asObject;
            r.lightProbeUsage = asObject ? UnityEngine.Rendering.LightProbeUsage.BlendProbes : UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = asObject ? UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes : UnityEngine.Rendering.ReflectionProbeUsage.Off;

            UnityEditor.Undo.RecordObject(this, "Bake Particles");
            state = stateTex;
            particleRenderer = r;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonParticles] Baked {N} particles to {path}.", this);
        }

        // Render As Object: N copies of Instance Mesh, each vertex tagged with its particle (uv1 = state texel, random)
        Mesh Instances(int N, int W, int H, System.Random rng)
        {
            var src = instanceMesh;
            var sv = src.vertices; var sn = src.normals; var tris = src.triangles;
            var suv = new System.Collections.Generic.List<Vector2>(); src.GetUVs(0, suv);
            int vc = sv.Length;
            var v = new Vector3[N * vc]; var n = new Vector3[N * vc]; var uv = new Vector2[N * vc];
            var data = new Vector3[N * vc]; var idx = new int[N * tris.Length];
            for (int i = 0; i < N; i++)
            {
                var d = new Vector3((i % W + 0.5f) / (2 * W), (i / W + 0.5f) / H, (float)rng.NextDouble());
                int b = i * vc;
                for (int k = 0; k < vc; k++)
                {
                    v[b + k] = sv[k];
                    n[b + k] = k < sn.Length ? sn[k] : Vector3.up;
                    uv[b + k] = k < suv.Count ? suv[k] : Vector2.zero;
                    data[b + k] = d;
                }
                for (int k = 0; k < tris.Length; k++) idx[i * tris.Length + k] = b + tris[k];
            }
            var m = new Mesh { name = "Particles", indexFormat = N * vc > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.vertices = v; m.normals = n; m.uv = uv;
            m.SetUVs(1, data);
            m.triangles = idx;
            float reach = Reach() + size * src.bounds.extents.magnitude;
            m.bounds = new Bounds(Vector3.zero, Vector3.one * (2f * reach));
            return m;
        }
        // how far particles can get in one Lifetime (culling box)
        float Reach()
        {
            float speed = Mathf.Abs(normal) + objectAligned.magnitude + random + velocityOverLifetime.magnitude + noiseStrength * lifetime;
            if (maxSpeed > 0f) speed = Mathf.Min(speed, maxSpeed);
            float shapeSize = Mathf.Max(shapeRadius, boxSize.magnitude * 0.5f);
            return Mathf.Min(500f, speed * lifetime + 0.5f * gravity.magnitude * lifetime * lifetime + shapeSize + 2f);
        }
#endif
    }
}
