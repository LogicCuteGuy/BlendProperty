using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/UVWarp.png")]
    [AddComponentMenu("Blend Property/Modifiers/Edit/UV Warp")]
    public class UVWarpModifier : BlendModifier
    {
        public Vector2 center = new Vector2(0.5f, 0.5f);
        public Vector2 offset;
        public Vector2 scale = Vector2.one;
        [Range(-180, 180)] public float rotation;
        [Range(0, 1)] public int channel;

        public override void Apply(MeshData mesh)
        {
            float r = rotation * Mathf.Deg2Rad, cos = Mathf.Cos(r), sin = Mathf.Sin(r);
            foreach (var f in mesh.faces)
                for (int c = 0; c < f.Count; c++)
                {
                    var d = Vector2.Scale((channel == 0 ? f.a[c].uv0 : f.a[c].uv1) - center, scale);
                    var uv = new Vector2(d.x * cos - d.y * sin, d.x * sin + d.y * cos) + center + offset;
                    if (channel == 0) f.a[c].uv0 = uv; else f.a[c].uv1 = uv;
                }
        }
    }
}
