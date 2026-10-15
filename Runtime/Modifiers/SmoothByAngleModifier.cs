using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender "Smooth by Angle": smooth shading except across edges sharper than <see cref="angle"/>.</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/SmoothByAngle.png")]
    [AddComponentMenu("Blend Property/Modifiers/Normals/Smooth by Angle")]
    public class SmoothByAngleModifier : BlendModifier
    {
        [Range(0, 180)] public float angle = 30f;

        public override void Apply(MeshData mesh)
        {
            var n = mesh.CornerNormals(angle, null);
            for (int fi = 0; fi < mesh.faces.Count; fi++)
                for (int c = 0; c < mesh.faces[fi].Count; c++)
                    mesh.faces[fi].a[c].normal = n[fi][c];
            mesh.customNormals = true;
        }
    }
}
