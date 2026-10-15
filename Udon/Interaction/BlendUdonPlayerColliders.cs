#if BLEND_UDON
using UdonSharp;
using VRC.SDKBase;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
using VRCPlayerApi = BlendProperty.Standalone.BlendPlayer;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Every player's body as colliders for the simulations: head, chest, hips, hands and feet (spheres), upper and lower
    /// arms and legs (capsules), moved onto each avatar's bones every frame and sized to its height. A body is made at
    /// runtime for each player who joins and removed when they leave, and every simulation in the scene gets it (the GPU
    /// ones count only colliders near them). Turn parts on or off and size them below. Triggers, so they never push
    /// players or rigid bodies. The Body Template child and the Simulations list are filled in by the editor.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [DefaultExecutionOrder(-100)] // before the simulations read the colliders in PostLateUpdate
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/PlayerColliders.png")]
    [AddComponentMenu("Blend Property/Physics/Interaction/Player Colliders")]
    public class BlendUdonPlayerColliders : UdonSharpBehaviour
    {
        [Tooltip("Off: only your own body. On: everyone's.")]
        public bool allPlayers = true;
        [Tooltip("Sizes follow each avatar's height (the radii are for a 1.6 m eye height).")]
        public bool scaleWithAvatar = true;

        [Header("Parts")]
        public bool head = true;
        public bool chest = true;
        public bool hips = true;
        public bool hands = true;
        public bool feet = true;
        public bool arms = true;
        public bool legs = true;

        [Header("Radius (metres)")]
        public float headRadius = 0.11f;
        public float chestRadius = 0.15f;
        public float hipsRadius = 0.14f;
        public float handRadius = 0.05f;
        public float footRadius = 0.06f;
        public float upperArmRadius = 0.05f;
        public float lowerArmRadius = 0.04f;
        public float upperLegRadius = 0.08f;
        public float lowerLegRadius = 0.06f;

        [Header("Filled in by the editor")]
        [Tooltip("Inactive child copied for each player: 7 sphere parts then 8 capsule parts.")]
        public GameObject template;
        [Tooltip("Every Blend Udon simulation in the scene; their collider lists get the bodies at runtime.")]
        public UdonSharpBehaviour[] simulations;
        [Tooltip("Per simulation, which lists it has: 1 sphereColliders, 2 capsuleColliders, 4 colliders, 8 effectors, 16 brushes.")]
        public int[] simulationLists;
        [Tooltip("Every Cell Fracture in the scene: your hands and feet break it when they hit it faster than its Break Speed.")]
        public BlendUdonFracture[] fractures;

        const int SpheresPerBody = 7, CapsulesPerBody = 8;
        VRCPlayerApi[] owners = new VRCPlayerApi[0];
        GameObject[] bodies = new GameObject[0];
        SphereCollider[] spheres = new SphereCollider[0];
        CapsuleCollider[] capsules = new CapsuleCollider[0];
        BlendUdonPaintBrush[] brushes = new BlendUdonPaintBrush[0]; // hands and feet paint and make waves
        // each simulation's own (hand-set) lists, before the bodies were added
        object[] ownSpheres, ownCapsules, ownColliders, ownEffectors, ownBrushes;
        Collider[] fractureCols;
        Vector3[] lastHit = new Vector3[4]; // your hands and feet last frame
        bool hitReady;

#if !BLEND_UDON
        [Header("Standalone players")]
        [Tooltip("Humanoid Animators to follow. The first is the local player. RegisterPlayer / UnregisterPlayer support runtime changes.")]
        public Animator[] avatars = new Animator[0];
        [Tooltip("Optional local viewpoint (desktop camera or XR head). Also works without an Animator.")]
        public Transform viewpoint;

        public void RegisterPlayer(Animator avatar)
        {
            if (!avatar) return;
            foreach (var player in owners) if (player.avatar == avatar) return;
            OnPlayerJoined(new BlendPlayer(avatar, owners.Length == 0 ? viewpoint : null, owners.Length == 0));
        }

        public void UnregisterPlayer(Animator avatar)
        {
            foreach (var player in owners)
                if (player.avatar == avatar) { OnPlayerLeft(player); return; }
        }
#endif

        void Start()
        {
            Init();
#if !BLEND_UDON
            if (!enabled) return;
            if (avatars != null) foreach (var avatar in avatars) RegisterPlayer(avatar);
            if (owners.Length == 0 && viewpoint) OnPlayerJoined(new BlendPlayer(null, viewpoint, true));
#endif
        }

        // Start, or the first join if that comes first
        void Init()
        {
            if (ownSpheres != null) return;
            if (template == null) { Debug.LogWarning("[BlendUdonPlayerColliders] No Body Template: re-add it from GameObject → Blend Physics → Player Colliders.", this); enabled = false; return; }
            if (simulations == null || simulationLists == null || simulationLists.Length != simulations.Length)
            {
                simulations = new UdonSharpBehaviour[0];
                simulationLists = new int[0];
            }
            int n = simulations.Length;
            ownSpheres = new object[n]; ownCapsules = new object[n]; ownColliders = new object[n]; ownEffectors = new object[n]; ownBrushes = new object[n];
            for (int i = 0; i < n; i++)
            {
                UdonSharpBehaviour s = simulations[i];
                if (s == null) continue;
                // only lists it has: asking for a missing one logs an error
                int has = simulationLists[i];
                if ((has & 1) != 0) ownSpheres[i] = s.GetProgramVariable("sphereColliders");
                if ((has & 2) != 0) ownCapsules[i] = s.GetProgramVariable("capsuleColliders");
                if ((has & 4) != 0) ownColliders[i] = s.GetProgramVariable("colliders");
                if ((has & 8) != 0) ownEffectors[i] = s.GetProgramVariable("effectors");
                if ((has & 16) != 0) ownBrushes[i] = s.GetProgramVariable("brushes");
            }
            if (fractures == null) fractures = new BlendUdonFracture[0];
            fractureCols = new Collider[fractures.Length];
            for (int i = 0; i < fractures.Length; i++) if (fractures[i] != null) fractureCols[i] = fractures[i].GetComponent<Collider>();
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            Init();
            if (!enabled || player == null || (!allPlayers && !player.isLocal)) return;
            GameObject body = Instantiate(template);
            body.transform.SetParent(transform, false);
            body.name = player.displayName;
            body.SetActive(true);
            int n = bodies.Length;
            VRCPlayerApi[] o = new VRCPlayerApi[n + 1];
            GameObject[] b = new GameObject[n + 1];
            // you first: your own simulations care most about your own body
            int at = player.isLocal ? 0 : n;
            int j = 0;
            for (int i = 0; i <= n; i++)
            {
                if (i == at) { o[i] = player; b[i] = body; continue; }
                o[i] = owners[j]; b[i] = bodies[j]; j++;
            }
            owners = o; bodies = b;
            Rebuild();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            int k = -1;
            for (int i = 0; i < owners.Length; i++) if (owners[i] == player) k = i;
            if (k < 0) return;
            Destroy(bodies[k]);
            VRCPlayerApi[] o = new VRCPlayerApi[owners.Length - 1];
            GameObject[] b = new GameObject[owners.Length - 1];
            int j = 0;
            for (int i = 0; i < owners.Length; i++)
            {
                if (i == k) continue;
                o[j] = owners[i]; b[j] = bodies[i]; j++;
            }
            owners = o; bodies = b;
            Rebuild();
        }

        // flat part lists, then every simulation gets its own colliders plus all bodies
        void Rebuild()
        {
            spheres = new SphereCollider[bodies.Length * SpheresPerBody];
            capsules = new CapsuleCollider[bodies.Length * CapsulesPerBody];
            int nb = 0;
            for (int k = 0; k < bodies.Length; k++) nb += bodies[k].GetComponentsInChildren<BlendUdonPaintBrush>(true).Length;
            brushes = new BlendUdonPaintBrush[nb];
            nb = 0;
            for (int k = 0; k < bodies.Length; k++)
            {
                BlendUdonPaintBrush[] bb = bodies[k].GetComponentsInChildren<BlendUdonPaintBrush>(true);
                for (int i = 0; i < bb.Length; i++) brushes[nb++] = bb[i];
                SphereCollider[] s = bodies[k].GetComponentsInChildren<SphereCollider>(true);
                CapsuleCollider[] c = bodies[k].GetComponentsInChildren<CapsuleCollider>(true);
                for (int i = 0; i < SpheresPerBody && i < s.Length; i++) spheres[k * SpheresPerBody + i] = s[i];
                for (int i = 0; i < CapsulesPerBody && i < c.Length; i++) capsules[k * CapsulesPerBody + i] = c[i];
            }
            for (int i = 0; i < simulations.Length; i++)
            {
                UdonSharpBehaviour sim = simulations[i];
                if (sim == null) continue;
                if (ownSpheres[i] != null) sim.SetProgramVariable("sphereColliders", JoinSpheres((SphereCollider[])ownSpheres[i]));
                if (ownCapsules[i] != null) sim.SetProgramVariable("capsuleColliders", JoinCapsules((CapsuleCollider[])ownCapsules[i]));
                if (ownColliders[i] != null) sim.SetProgramVariable("colliders", JoinAll((Collider[])ownColliders[i]));
                if (ownEffectors[i] != null) sim.SetProgramVariable("effectors", JoinSpheres((SphereCollider[])ownEffectors[i]));
                if (ownBrushes[i] != null) sim.SetProgramVariable("brushes", JoinBrushes((BlendUdonPaintBrush[])ownBrushes[i]));
            }
            hitReady = false;
        }

        BlendUdonPaintBrush[] JoinBrushes(BlendUdonPaintBrush[] own)
        {
            BlendUdonPaintBrush[] r = new BlendUdonPaintBrush[own.Length + brushes.Length];
            for (int i = 0; i < own.Length; i++) r[i] = own[i];
            for (int i = 0; i < brushes.Length; i++) r[own.Length + i] = brushes[i];
            return r;
        }

        SphereCollider[] JoinSpheres(SphereCollider[] own)
        {
            SphereCollider[] r = new SphereCollider[own.Length + spheres.Length];
            for (int i = 0; i < own.Length; i++) r[i] = own[i];
            for (int i = 0; i < spheres.Length; i++) r[own.Length + i] = spheres[i];
            return r;
        }

        CapsuleCollider[] JoinCapsules(CapsuleCollider[] own)
        {
            CapsuleCollider[] r = new CapsuleCollider[own.Length + capsules.Length];
            for (int i = 0; i < own.Length; i++) r[i] = own[i];
            for (int i = 0; i < capsules.Length; i++) r[own.Length + i] = capsules[i];
            return r;
        }

        Collider[] JoinAll(Collider[] own)
        {
            Collider[] r = new Collider[own.Length + spheres.Length + capsules.Length];
            for (int i = 0; i < own.Length; i++) r[i] = own[i];
            for (int i = 0; i < spheres.Length; i++) r[own.Length + i] = spheres[i];
            for (int i = 0; i < capsules.Length; i++) r[own.Length + spheres.Length + i] = capsules[i];
            return r;
        }

        public override void PostLateUpdate()
        {
            if (bodies.Length == 0) return;
            float inv = 1f / Mathf.Max(Mathf.Abs(transform.lossyScale.x), 1e-6f);
            for (int k = 0; k < bodies.Length; k++)
            {
                VRCPlayerApi p = owners[k];
                bool valid = p != null && p.IsValid();
                float s = valid && scaleWithAvatar ? Mathf.Max(0.1f, p.GetAvatarEyeHeightAsMeters() / 1.6f) : 1f;
                Vector3 local = Vector3.one * (s * inv);
                for (int i = 0; i < SpheresPerBody; i++) PlaceSphere(spheres[k * SpheresPerBody + i], i, valid ? p : null, local);
                for (int i = 0; i < CapsulesPerBody; i++) PlaceCapsule(capsules[k * CapsulesPerBody + i], i, valid ? p : null, local, s);
            }
            HitFractures();
            // the worlds' physics only catches up with moved colliders at its next step: the simulations' bounds
            // tests and ClosestPoint need the new places now
            Physics.SyncTransforms();
        }

        // your own hands and feet (spheres 3-6 of the first body) break a Cell Fracture they hit fast enough;
        // only yours, so a networked break is sent once, by the player who hit it
        void HitFractures()
        {
            if (fractures.Length == 0 || owners.Length == 0 || owners[0] == null || !owners[0].isLocal) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            for (int j = 0; j < 4; j++)
            {
                SphereCollider c = spheres[3 + j];
                if (c == null || !c.enabled) { hitReady = false; continue; }
                Vector3 p = c.transform.position;
                float speed = (p - lastHit[j]).magnitude / dt;
                lastHit[j] = p;
                if (!hitReady) continue;
                float r = c.radius * c.transform.lossyScale.x;
                for (int i = 0; i < fractures.Length; i++)
                {
                    BlendUdonFracture f = fractures[i];
                    Collider fc = fractureCols[i];
                    if (f == null || fc == null || !fc.enabled || !f.breakOnImpact || speed < f.breakSpeed) continue;
                    if ((fc.ClosestPoint(p) - p).sqrMagnitude <= r * r) f.Break();
                }
            }
            hitReady = true;
        }

        void PlaceSphere(SphereCollider c, int i, VRCPlayerApi p, Vector3 local)
        {
            if (c == null) return;
            bool on = p != null && (i == 0 ? head : i == 1 ? chest : i == 2 ? hips : i <= 4 ? hands : feet);
            Vector3 a = Vector3.zero, b = Vector3.zero;
            if (on)
            {
                a = p.GetBonePosition(SphereBone(i, false));
                b = i == 0 ? p.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position : p.GetBonePosition(SphereBone(i, true));
                if (a == Vector3.zero) a = b; // no humanoid head: the viewpoint
                on = a != Vector3.zero; // the avatar has no such bone
            }
            c.enabled = on;
            if (!on) return;
            Transform t = c.transform;
            t.position = b != Vector3.zero ? (a + b) * 0.5f : a; // halfway between its two bones (Head: head bone and eyes)
            t.localScale = local;
            c.radius = i == 0 ? headRadius : i == 1 ? chestRadius : i == 2 ? hipsRadius : i <= 4 ? handRadius : footRadius;
        }

        void PlaceCapsule(CapsuleCollider c, int i, VRCPlayerApi p, Vector3 local, float s)
        {
            if (c == null) return;
            bool on = p != null && (i < 4 ? arms : legs);
            Vector3 a = Vector3.zero, b = Vector3.zero;
            if (on)
            {
                a = p.GetBonePosition(CapsuleBone(i, false));
                b = p.GetBonePosition(CapsuleBone(i, true));
                on = a != Vector3.zero && b != Vector3.zero;
            }
            c.enabled = on;
            if (!on) return;
            Vector3 d = b - a;
            float len = d.magnitude;
            Transform t = c.transform;
            t.position = (a + b) * 0.5f;
            t.rotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, d) : Quaternion.identity;
            t.localScale = local;
            c.radius = i < 2 ? upperArmRadius : i < 4 ? lowerArmRadius : i < 6 ? upperLegRadius : lowerLegRadius;
            c.direction = 1;
            c.height = len / s + 2f * c.radius; // bone to bone plus the round ends
        }

        HumanBodyBones SphereBone(int i, bool tip)
        {
            switch (i)
            {
                case 0: return HumanBodyBones.Head;
                case 1: return tip ? HumanBodyBones.Neck : HumanBodyBones.Spine;
                case 2: return HumanBodyBones.Hips;
                case 3: return tip ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.LeftHand;
                case 4: return tip ? HumanBodyBones.RightMiddleProximal : HumanBodyBones.RightHand;
                case 5: return tip ? HumanBodyBones.LeftToes : HumanBodyBones.LeftFoot;
                default: return tip ? HumanBodyBones.RightToes : HumanBodyBones.RightFoot;
            }
        }

        HumanBodyBones CapsuleBone(int i, bool tip)
        {
            switch (i)
            {
                case 0: return tip ? HumanBodyBones.LeftLowerArm : HumanBodyBones.LeftUpperArm;
                case 1: return tip ? HumanBodyBones.RightLowerArm : HumanBodyBones.RightUpperArm;
                case 2: return tip ? HumanBodyBones.LeftHand : HumanBodyBones.LeftLowerArm;
                case 3: return tip ? HumanBodyBones.RightHand : HumanBodyBones.RightLowerArm;
                case 4: return tip ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.LeftUpperLeg;
                case 5: return tip ? HumanBodyBones.RightLowerLeg : HumanBodyBones.RightUpperLeg;
                case 6: return tip ? HumanBodyBones.LeftFoot : HumanBodyBones.LeftLowerLeg;
                default: return tip ? HumanBodyBones.RightFoot : HumanBodyBones.RightLowerLeg;
            }
        }
    }
}
