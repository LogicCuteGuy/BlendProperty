using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender's Physics tab as one component: click Cloth, Soft Body, Fluid, Force Field, Collision, Rigid Body and the
    /// rest to add or remove that physics on this object. It adds the matching Blend Udon component, bakes it, and links
    /// it with the other Physics objects the way Blender does: every simulation feels every Force Field, collides with
    /// every object that has Collision on, and so on (Scope limits this to one parent). Objects set up by hand, without
    /// this component, are left alone. Setup component for standalone and VRChat; the components it adds are
    /// what run in the world.
    /// </summary>
    [DisallowMultipleComponent]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/PhysicsTab.png")]
    [AddComponentMenu("Blend Property/Physics/Physics Tab")]
    public class BlendPhysics : MonoBehaviour
#if BLEND_VRC
        , VRC.SDKBase.IEditorOnly
#endif
    {
        public enum LinkScope { Scene, Parent }

        [Tooltip("Blender Collision: this object's colliders are listed in every simulation with a Physics component.")]
        public bool collision;
        [Tooltip("What this object links with (Blender Effector Collection). Scene: everything. Parent: only objects under " +
                 "this object's parent, so one corner of a world doesn't push or collide with another.")]
        public LinkScope scope;
        [HideInInspector, Tooltip("Materials before Ocean or a Fluid domain replaced them, put back when that is turned off.")]
        public Material[] previousMaterials;
    }
}
