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
    /// Puts objects back where they were at start (e.g. thrown pickups, a moved force field).
    /// Hook a Button's On Click → SendCustomEvent "ResetObjects". Objects with VRC Object Sync are respawned for
    /// everyone (this player takes ownership); the rest move back for this player only.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/ResetObjects.png")]
    [AddComponentMenu("Blend Property/Physics/Interaction/Reset Objects")]
    public class BlendUdonResetObjects : UdonSharpBehaviour
    {
        public Transform[] objects;

        Vector3[] startPos;
        Quaternion[] startRot;

        void Start()
        {
            startPos = new Vector3[objects.Length];
            startRot = new Quaternion[objects.Length];
            for (int i = 0; i < objects.Length; i++)
                if (objects[i] != null) { startPos[i] = objects[i].position; startRot[i] = objects[i].rotation; }
        }

        public void ResetObjects()
        {
            for (int i = 0; i < objects.Length; i++)
            {
                Transform t = objects[i];
                if (t == null) continue;
#if BLEND_UDON
                VRCPickup pickup = (VRCPickup)t.GetComponent(typeof(VRCPickup));
                if (pickup != null) pickup.Drop();
                VRCObjectSync sync = (VRCObjectSync)t.GetComponent(typeof(VRCObjectSync));
                if (sync != null)
                {
                    Networking.SetOwner(Networking.LocalPlayer, t.gameObject);
                    sync.Respawn();
                    continue;
                }
#endif
                t.SetPositionAndRotation(startPos[i], startRot[i]);
                Rigidbody rb = t.GetComponent<Rigidbody>();
                if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
        }
    }
}
