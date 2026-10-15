using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Mirror.png")]
    [AddComponentMenu("Blend Property/Modifiers/Generate/Mirror")]
    public class MirrorModifier : BlendModifier
    {
        public bool x = true, y, z;
        [Tooltip("Cut away geometry on the mirrored side first.")]
        public bool bisectX, bisectY, bisectZ;
        [Tooltip("Mirror the other side instead.")]
        public bool flipX, flipY, flipZ;
        [Tooltip("Mirror across this object's origin/axes instead of the modified object's.")]
        public Transform mirrorObject;
        public bool merge = true;
        [Min(0)] public float mergeDistance = 0.001f;
        [Min(0), Tooltip("Bisect: also remove geometry closer than this to the mirror plane.")]
        public float bisectDistance;
        [Tooltip("Mirror the UVs of the copy across the middle of the texture.")]
        public bool flipU, flipV;
        [Tooltip("Shift the mirrored copy's UVs (e.g. off the texture for baking).")]
        public Vector2 uvOffset;

        public override IEnumerable<Object> Dependencies() { if (mirrorObject) yield return mirrorObject; }

        public override void Apply(MeshData mesh)
        {
            var toMirror = mirrorObject ? ToLocal(mirrorObject).inverse : Matrix4x4.identity; // local -> mirror space
            var fromMirror = toMirror.inverse;
            bool[] on = { x, y, z }, bisect = { bisectX, bisectY, bisectZ }, flip = { flipX, flipY, flipZ };

            for (int axis = 0; axis < 3; axis++)
            {
                if (!on[axis]) continue;
                Vector3 axisN = Vector3.zero; axisN[axis] = 1;
                if (bisect[axis])
                {
                    // keep side in mirror space: +axis normally, -axis when flipped
                    var nLocal = MeshData.Unit(fromMirror.MultiplyVector(flip[axis] ? -axisN : axisN));
                    var origin = fromMirror.MultiplyPoint3x4(Vector3.zero);
                    mesh.ClipPlane(nLocal, Vector3.Dot(nLocal, origin) + MeshUnits(bisectDistance));
                }
                var s = Vector3.one; s[axis] = -1;
                var xf = fromMirror * Matrix4x4.Scale(s) * toMirror;
                var copy = mesh.Clone();
                if (flipU || flipV || uvOffset != Vector2.zero)
                    foreach (var f in copy.faces)
                        for (int c = 0; c < f.Count; c++)
                        {
                            if (flipU) f.a[c].uv0.x = 1 - f.a[c].uv0.x;
                            if (flipV) f.a[c].uv0.y = 1 - f.a[c].uv0.y;
                            f.a[c].uv0 += uvOffset;
                        }
                mesh.Append(copy, xf, flip: true);
                if (merge) mesh.Weld(MeshUnits(mergeDistance));
            }
        }
    }
}
