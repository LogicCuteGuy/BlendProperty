using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Displace.png")]
    [AddComponentMenu("Blend Modifier/Deform/Displace")]
    public class DisplaceModifier : BlendModifier
    {
        public enum Direction { Normal, X, Y, Z, RGBToXYZ, CustomNormal }
        public enum Coordinates { UV, Local, Object, Global }
        public enum Space { Local, Global }

        [Tooltip("Optional. Empty = Perlin noise.")]
        public Texture2D texture;
        public Coordinates coordinates = Coordinates.UV;
        [Tooltip("For Object coordinates.")]
        public Transform coordinateObject;
        public Vector2 tiling = Vector2.one;
        public float noiseScale = 4f;
        public Direction direction = Direction.Normal;
        [Tooltip("X, Y, Z, RGB to XYZ: displace along the object's own axes or the world axes.")]
        public Space space = Space.Local;
        public float strength = 1f;
        [Range(0, 1)] public float midlevel = 0.5f;

        public override IEnumerable<Object> Dependencies() { if (coordinateObject) yield return coordinateObject; }

        public override void Apply(MeshData mesh)
        {
            if (strength == 0) return;
            float amount = MeshUnits(strength);
            var meters = transform.lossyScale; // Local coordinates sample the texture per metre, not per mesh unit
            var vn = mesh.VertexNormals();
            if (direction == Direction.CustomNormal && mesh.customNormals)
            {
                // average the per-corner custom normals of each vertex (Blender: Custom Normal)
                var sum = new Vector3[mesh.pos.Count];
                foreach (var f in mesh.faces) for (int c = 0; c < f.Count; c++) sum[f.v[c]] += f.a[c].normal;
                for (int i = 0; i < sum.Length; i++) if (sum[i] != Vector3.zero) vn[i] = MeshData.Unit(sum[i]);
            }
            // world axes expressed in this object's space
            Vector3 ax = Vector3.right, ay = Vector3.up, az = Vector3.forward;
            if (space == Space.Global)
            {
                ax = transform.InverseTransformDirection(Vector3.right);
                ay = transform.InverseTransformDirection(Vector3.up);
                az = transform.InverseTransformDirection(Vector3.forward);
            }
            var attrs = mesh.PositionAttrs();
            var tex = texture ? Readable(texture) : null;
            var toObj = coordinateObject ? ToLocal(coordinateObject).inverse : Matrix4x4.identity;
            try
            {
                for (int i = 0; i < mesh.pos.Count; i++)
                {
                    var p = mesh.pos[i];
                    Vector2 uv;
                    if (coordinates == Coordinates.UV) uv = attrs[i].uv0;
                    else if (coordinates == Coordinates.Object) uv = toObj.MultiplyPoint3x4(p);
                    else if (coordinates == Coordinates.Global) { var w = transform.TransformPoint(p); uv = new Vector2(w.x, w.z); }
                    else uv = new Vector2(p.x * meters.x, p.z * meters.z);
                    uv = Vector2.Scale(uv, tiling);
                    Color c = tex ? tex.GetPixelBilinear(uv.x, uv.y)
                        : Color.white * Mathf.PerlinNoise(uv.x * noiseScale + 1000f, uv.y * noiseScale + 1000f);
                    float h = (c.r + c.g + c.b) / 3f - midlevel;
                    Vector3 d = direction switch
                    {
                        Direction.Normal or Direction.CustomNormal => vn[i] * h,
                        Direction.X => ax * h,
                        Direction.Y => ay * h,
                        Direction.Z => az * h,
                        _ => ax * (c.r - midlevel) + ay * (c.g - midlevel) + az * (c.b - midlevel),
                    };
                    mesh.pos[i] = p + d * amount;
                }
            }
            finally { if (tex && tex != texture) DestroyImmediate(tex); }
        }

        /// <summary>Copy through a RenderTexture so non-readable textures work in the editor.</summary>
        public static Texture2D Readable(Texture2D src)
        {
            if (src.isReadable) return src;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true) { wrapMode = src.wrapMode };
            t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            t.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return t;
        }
    }
}
