#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    public enum BlendUdonFluidFlowType { Smoke, Fire, FireAndSmoke, Liquid }

    /// <summary>
    /// Blender Fluid Flow (behaviour Inflow): fills a sphere of Radius around this object with smoke, fuel that burns
    /// into flame (Fire), both, or liquid (in a Liquid domain), in every BlendUdonFluid domain that lists it. Put it on a pickup to carry it around.
    /// Turn the component off to stop it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/FluidFlow.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Fluid Flow")]
    public class BlendUdonFluidFlow : UdonSharpBehaviour
    {
        [Tooltip("Smoke, Fire (fuel that burns into flame, heat and smoke), both, or Liquid for a Liquid domain (Blender Flow Type).")]
        public BlendUdonFluidFlowType flowType;
        [Min(0.01f), Tooltip("Metres.")]
        public float radius = 0.15f;
        [Range(0, 1), Tooltip("Smoke density at the centre (Blender Density).")]
        public float density = 1f;
        [Range(-2, 2), Tooltip("Heat at the centre: hot smoke rises (Blender Temperature Difference).")]
        public float temperature = 1f;
        [Range(0, 2), Tooltip("Fire: fuel at the centre (Blender Flame Rate).")]
        public float fuel = 1f;
        [Tooltip("Push the smoke with Initial Velocity (+ this object's own motion × Source).")]
        public bool useInitialVelocity;
        [Tooltip("World space, m/s.")]
        public Vector3 initialVelocity = new Vector3(0f, 1f, 0f);
        [Range(0, 2), Tooltip("How much of this object's own velocity the smoke takes (Blender Source).")]
        public float source = 1f;
    }
}
