using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender UV Project: planar UVs from a projector object (its local XY, centred, 1 unit = 1 UV tile).</summary>
    // ponytail: one orthographic projector; Blender also takes several projectors and camera perspective.
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/UVProject.png")]
    [AddComponentMenu("Blend Modifier/Edit/UV Project")]
    public class UVProjectModifier : BlendModifier
    {
        [Tooltip("Projects along its local Z. Empty = this object, projecting along Z.")]
        public Transform projector;
        public Vector2 scale = Vector2.one;
        [Range(0, 1)] public int channel;

        public override IEnumerable<Object> Dependencies() { if (projector) yield return projector; }

        public override void Apply(MeshData mesh)
        {
            // no projector: one UV tile per metre of this object
            var toProj = projector ? ToLocal(projector).inverse : Matrix4x4.Scale(transform.lossyScale);
            foreach (var f in mesh.faces)
                for (int c = 0; c < f.Count; c++)
                {
                    var q = toProj.MultiplyPoint3x4(mesh.pos[f.v[c]]);
                    var uv = new Vector2(q.x * scale.x + 0.5f, q.y * scale.y + 0.5f);
                    if (channel == 0) f.a[c].uv0 = uv; else f.a[c].uv1 = uv;
                }
            if (channel == 1) mesh.hasUV1 = true;
        }
    }
}
