using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Normal Edit: point normals away from a center (Radial) or toward a target (Directional).</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/NormalEdit.png")]
    [AddComponentMenu("Blend Modifier/Normals/Normal Edit")]
    public class NormalEditModifier : BlendModifier
    {
        public enum Mode { Radial, Directional }
        public enum Mix { Copy, Add, Multiply, Subtract }

        public Mode mode = Mode.Radial;
        [Tooltip("Radial center / directional target. Empty = object origin (+offset).")]
        public Transform target;
        public Vector3 offset;
        [Tooltip("Directional: all normals parallel to (target - origin).")]
        public bool parallel;
        public Mix mix = Mix.Copy;
        [Range(0, 1)] public float mixFactor = 1f;
        [Range(0, 180), Tooltip("New normals may differ from the original by at most this angle (180 = no limit).")]
        public float maxAngle = 180f;

        public override IEnumerable<Object> Dependencies() { if (target) yield return target; }

        public override void Apply(MeshData mesh)
        {
            Vector3 center = (target ? ToLocal(target).MultiplyPoint3x4(Vector3.zero) : Vector3.zero) + MeshUnits(offset);
            bool hadCustom = mesh.customNormals;
            Vector3[][] old = hadCustom ? null : mesh.CornerNormals(GetComponent<BlendModifierStack>().AutoSmoothAngle, null);
            for (int fi = 0; fi < mesh.faces.Count; fi++)
            {
                var f = mesh.faces[fi];
                for (int c = 0; c < f.Count; c++)
                {
                    var p = mesh.pos[f.v[c]];
                    Vector3 nn = mode == Mode.Radial ? p - center : parallel ? center : center - p;
                    nn = nn != Vector3.zero ? MeshData.Unit(nn) : Vector3.up;
                    var o = hadCustom ? f.a[c].normal.normalized : old[fi][c];
                    var m = mix switch
                    {
                        Mix.Add => (o + nn).normalized,
                        Mix.Multiply => Vector3.Scale(o, nn).normalized,
                        Mix.Subtract => MeshData.Unit(o - nn) != Vector3.zero ? MeshData.Unit(o - nn) : o,
                        _ => nn,
                    };
                    var result = Vector3.Slerp(o, m, mixFactor);
                    if (maxAngle < 180f && Vector3.Angle(o, result) > maxAngle)
                        result = Vector3.RotateTowards(o, result, maxAngle * Mathf.Deg2Rad, 0f);
                    f.a[c].normal = result;
                }
            }
            mesh.customNormals = true;
        }
    }
}
