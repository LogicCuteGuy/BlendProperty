using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    // ponytail: trilinear FFD; Blender defaults to B-spline interpolation (smoother with high resolutions).
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Lattice.png")]
    [AddComponentMenu("Blend Property/Modifiers/Deform/Lattice")]
    public class LatticeModifier : BlendModifier
    {
        public BlendLattice lattice;
        [Range(0, 1)] public float strength = 1f;
        [Tooltip("VRChat: Off = baked once. GPU = keeps it live in the world on the GPU (shows a frame or two late), following " +
                 "moving objects. Runs after the other modifiers, so keep it at the bottom of the stack.")]
        public RuntimeMode runtime;
        public override RuntimeMode Runtime => runtime;

        public override IEnumerable<Object> Dependencies()
        {
            if (!lattice) yield break;
            yield return lattice;
            if (lattice.UsesChildren) foreach (Transform c in lattice.transform) yield return c;
        }

        public override void Apply(MeshData mesh)
        {
            if (!lattice) return;
            lattice.SyncChildren();
            var toLattice = lattice.transform.worldToLocalMatrix * transform.localToWorldMatrix;
            var fromLattice = toLattice.inverse;
            for (int i = 0; i < mesh.pos.Count; i++)
            {
                var lp = toLattice.MultiplyPoint3x4(mesh.pos[i]);
                var d = fromLattice.MultiplyPoint3x4(lattice.Deform(lp));
                mesh.pos[i] = Vector3.LerpUnclamped(mesh.pos[i], d, strength);
            }
        }
    }
}
