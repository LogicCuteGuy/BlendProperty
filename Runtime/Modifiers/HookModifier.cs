using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender Hook: the mesh follows an object, measured from where that object was when it was assigned.
    /// No vertex groups here, so the area is chosen by radius around the bind position.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Hook.png")]
    [AddComponentMenu("Blend Modifier/Deform/Hook")]
    public class HookModifier : BlendModifier
    {
        public Transform target;
        public float strength = 1f;
        public WarpModifier.Falloff falloff = WarpModifier.Falloff.Smooth;
        [Tooltip("0 = whole mesh.")]
        [Min(0)] public float radius = 1f;

        [SerializeField, HideInInspector] Transform boundTo;
        [SerializeField, HideInInspector] Matrix4x4 bind = Matrix4x4.identity;

        public override IEnumerable<Object> Dependencies() { if (target) yield return target; }

        /// <summary>Blender "Reset": the target's current placement becomes the rest position.</summary>
        [ContextMenu("Reset Hook")]
        public void Rebind()
        {
            boundTo = target;
            bind = target ? ToLocal(target) : Matrix4x4.identity;
            MarkDirty();
        }

        public override void Apply(MeshData mesh)
        {
            if (!target) return;
            // a newly assigned target binds where it stands, so assigning it changes nothing (as in Blender)
            if (target != boundTo) { boundTo = target; bind = ToLocal(target); }
            if (strength == 0) return;
            var hook = ToLocal(target) * bind.inverse;
            Vector3 center = bind.GetColumn(3);
            float rad = MeshUnits(radius);
            for (int i = 0; i < mesh.pos.Count; i++)
            {
                var p = mesh.pos[i];
                float w = 1f;
                if (rad > 0)
                {
                    float x = 1f - (p - center).magnitude / rad;
                    if (x <= 0) continue;
                    w = WarpModifier.Weight(falloff, x);
                }
                mesh.pos[i] = Vector3.LerpUnclamped(p, hook.MultiplyPoint3x4(p), w * strength);
            }
        }
    }
}
