#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Blender Collision settings for this object's Sphere, Capsule or Box collider: how particles bounce off it,
    /// slide on it and whether they die on it. Particles that list the collider use these; a collider without one
    /// bounces 0.3, friction 0.2, no kill. Can be changed at runtime.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [AddComponentMenu("Blend Property/Physics/Simulation/Collision")]
    public class BlendUdonCollision : UdonSharpBehaviour
    {
        [Range(0, 1), Tooltip("Share of the speed into this collider kept as bounce (Blender 1 - Damping).")]
        public float bounce = 0.3f;
        [Range(0, 1), Tooltip("Share of the sliding speed lost on each hit (Blender Friction).")]
        public float friction = 0.2f;
        [Tooltip("Particles die when they hit this collider (Blender Kill Particles).")]
        public bool killOnHit;
    }
}
