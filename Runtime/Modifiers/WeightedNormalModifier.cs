using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender Weighted Normal: bigger faces (or wider corners) pull shared normals toward themselves,
    /// the classic hard-surface shading fix.
    /// ponytail: weight = value^(weight/50) on values normalized to the largest one; Blender's exact mode
    /// ranks faces into buckets. Threshold rounds the normalized values so near-equal faces weigh the same.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/WeightedNormal.png")]
    [AddComponentMenu("Blend Modifier/Normals/Weighted Normal")]
    public class WeightedNormalModifier : BlendModifier
    {
        public enum Mode { FaceArea, CornerAngle, FaceAreaAndAngle }

        public Mode mode = Mode.FaceArea;
        [Range(1, 100)] public float weight = 50f;
        [Range(0, 10), Tooltip("Faces whose areas/angles differ by less than this (relative to the largest) weigh the same.")]
        public float threshold = 0.01f;
        [Tooltip("Respect sharp edges (uses the stack's smooth angle). Off = smooth across everything.")]
        public bool keepSharp = true;
        [Range(0, 180)] public float sharpAngle = 30f;

        public override void Apply(MeshData mesh)
        {
            float power = weight / 50f;
            int fc = mesh.faces.Count;
            var area = new float[fc];
            float maxArea = 0f, maxAngle = Mathf.PI;
            for (int i = 0; i < fc; i++) { area[i] = mesh.FaceArea(mesh.faces[i]); maxArea = Mathf.Max(maxArea, area[i]); }
            if (maxArea <= 0) return;
            float Round(float v) => threshold > 0 ? Mathf.Max(Mathf.Round(v / threshold) * threshold, threshold) : Mathf.Max(v, 1e-6f);
            float W(int fi, int c)
            {
                // normalized first, so tiny (centimetre-scale) meshes weigh the same as metre-scale ones
                float v = mode switch
                {
                    Mode.FaceArea => area[fi] / maxArea,
                    Mode.CornerAngle => mesh.CornerAngle(fi, c) / maxAngle,
                    _ => area[fi] / maxArea * (mesh.CornerAngle(fi, c) / maxAngle),
                };
                return Mathf.Pow(Round(v), power);
            }
            var n = mesh.CornerNormals(keepSharp ? sharpAngle : 180f, W);
            for (int fi = 0; fi < fc; fi++)
                for (int c = 0; c < mesh.faces[fi].Count; c++)
                    mesh.faces[fi].a[c].normal = n[fi][c];
            mesh.customNormals = true;
        }
    }
}
