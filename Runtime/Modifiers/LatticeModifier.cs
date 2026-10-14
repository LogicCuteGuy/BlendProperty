using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    // ponytail: trilinear FFD; Blender defaults to B-spline interpolation (smoother with high resolutions).
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Lattice.png")]
    [AddComponentMenu("Blend Modifier/Deform/Lattice")]
    public class LatticeModifier : BlendModifier
    {
        public BlendLattice lattice;
        [Range(0, 1)] public float strength = 1f;

        public override IEnumerable<Object> Dependencies() { if (lattice) yield return lattice; }

        public override void Apply(MeshData mesh)
        {
            if (!lattice) return;
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
