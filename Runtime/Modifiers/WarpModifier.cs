using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Warp: moves geometry by the offset between two objects, fading out with distance from "from".</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Warp.png")]
    [AddComponentMenu("Blend Modifier/Deform/Warp")]
    public class WarpModifier : BlendModifier
    {
        public enum Falloff { Constant, Linear, Smooth, Sharp, Sphere, Root }

        public Transform from, to;
        [Tooltip("Blend rotation instead of the matrix, so partly-rotated areas keep their size.")]
        public bool preserveVolume;
        public float strength = 1f;
        public Falloff falloff = Falloff.Smooth;
        [Tooltip("0 = no falloff (whole mesh).")]
        [Min(0)] public float radius = 1f;

        public override IEnumerable<Object> Dependencies() { if (from) yield return from; if (to) yield return to; }

        public override void Apply(MeshData mesh)
        {
            if (!from || !to || strength == 0) return;
            Matrix4x4 f = ToLocal(from), t = ToLocal(to);
            var warp = t * f.inverse;
            var fInv = f.inverse;
            Vector3 center = f.GetColumn(3);
            float rad = MeshUnits(radius);
            for (int i = 0; i < mesh.pos.Count; i++)
            {
                var p = mesh.pos[i];
                float w = 1f;
                if (rad > 0)
                {
                    float x = 1f - (p - center).magnitude / rad;
                    if (x <= 0) continue;
                    w = Weight(falloff, x);
                }
                w *= strength;
                if (preserveVolume)
                {
                    // the point rides a frame blended between "from" and "to" (position lerp, rotation slerp)
                    var q = fInv.MultiplyPoint3x4(p);
                    var frame = Matrix4x4.TRS(Vector3.LerpUnclamped(f.GetColumn(3), t.GetColumn(3), w),
                        Quaternion.SlerpUnclamped(f.rotation, t.rotation, w), Vector3.LerpUnclamped(f.lossyScale, t.lossyScale, w));
                    mesh.pos[i] = frame.MultiplyPoint3x4(q);
                }
                else mesh.pos[i] = Vector3.LerpUnclamped(p, warp.MultiplyPoint3x4(p), w);
            }
        }

        /// <summary>Blender falloff curve; <paramref name="x"/> is 1 at the center, 0 at the radius.</summary>
        public static float Weight(Falloff falloff, float x) => falloff switch
        {
            Falloff.Constant => 1f,
            Falloff.Linear => x,
            Falloff.Sharp => x * x,
            Falloff.Sphere => Mathf.Sqrt(2 * x - x * x),
            Falloff.Root => Mathf.Sqrt(x),
            _ => x * x * (3f - 2f * x),
        };
    }
}
