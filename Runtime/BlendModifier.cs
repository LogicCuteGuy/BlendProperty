using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Base for every Blender-style modifier. Stack order = component order in the Inspector;
    /// the component checkbox is the Blender viewport toggle. Editor-only: stripped from builds.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(BlendModifierStack))]
    public abstract class BlendModifier : MonoBehaviour
#if BLEND_VRC
        , VRC.SDKBase.IEditorOnly
#endif
    {
        /// <summary>Modify <paramref name="mesh"/> in place (object-local space).</summary>
        public abstract void Apply(MeshData mesh);

        /// <summary>Scene objects whose transform/data changes should re-run this modifier.</summary>
        public virtual IEnumerable<Object> Dependencies() { yield break; }

        protected BlendModifierStack Stack => GetComponent<BlendModifierStack>();

        /// <summary>
        /// Distances on modifiers are in Unity units (metres in the scene), not mesh units: an FBX imported in
        /// centimetres with Scale 100 still bevels 0.02 = 2 cm. This converts metres to this mesh's own units.
        /// ponytail: non-uniform scale uses the average axis scale for plain distances.
        /// </summary>
        protected float MeshUnits(float meters)
        {
            var s = transform.lossyScale;
            float k = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            return k > 1e-30f ? meters / k : meters;
        }

        /// <summary>A metre offset along this object's local axes, in mesh units (per axis).</summary>
        protected Vector3 MeshUnits(Vector3 meters)
        {
            var s = transform.lossyScale;
            float D(float m, float k) => Mathf.Abs(k) > 1e-30f ? m / Mathf.Abs(k) : m;
            return new Vector3(D(meters.x, s.x), D(meters.y, s.y), D(meters.z, s.z));
        }

        /// <summary>Matrix from <paramref name="t"/>'s space into this object's local space.</summary>
        protected Matrix4x4 ToLocal(Transform t) => t ? transform.worldToLocalMatrix * t.localToWorldMatrix : Matrix4x4.identity;

        protected virtual void OnValidate() => MarkDirty();
        protected virtual void OnEnable() => MarkDirty();
        protected virtual void OnDisable() => MarkDirty();

        public void MarkDirty()
        {
            var s = GetComponent<BlendModifierStack>();
            if (s) s.MarkDirty();
        }
    }
}
