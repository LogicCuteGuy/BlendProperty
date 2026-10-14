using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>Blender Wave on the XZ plane, lifting along Y (Unity is Y-up). Baked at <see cref="time"/> (no runtime animation: the result is a static mesh).</summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Wave.png")]
    [AddComponentMenu("Blend Modifier/Deform/Wave")]
    public class WaveModifier : BlendModifier
    {
        public bool motionX = true, motionZ = true;
        [Tooltip("Displace along vertex normals instead of local Y.")]
        public bool alongNormals;
        [Tooltip("Along Normals: only these local axes of the normal are used.")]
        public bool normalX = true, normalY = true, normalZ = true;
        public bool cyclic = true;
        [Tooltip("Start position on the XZ plane.")]
        public Vector2 start;
        [Tooltip("Optional start position object.")]
        public Transform startObject;
        public float height = 0.5f;
        [Min(0.0001f)] public float width = 1.5f;
        public float narrowness = 1.5f;
        public float speed = 0.25f;
        public float offset;
        public float time;
        [Min(0), Tooltip("How long the wave runs (in Time units) before it starts damping. 0 = forever.")]
        public float life;
        [Min(0), Tooltip("Time over which the wave fades to nothing after Life.")]
        public float damping = 10f;

        public override IEnumerable<Object> Dependencies() { if (startObject) yield return startObject; }

        public override void Apply(MeshData mesh)
        {
            if (!motionX && !motionZ) return;
            // height, width, narrowness, speed and start are in metres; the mesh is in its own units
            float k = MeshUnits(1f);
            float height = this.height * k, width = this.width * k, narrowness = this.narrowness / k, speed = this.speed * k;
            var sp = startObject ? ToLocal(startObject).MultiplyPoint3x4(Vector3.zero) : MeshUnits(new Vector3(start.x, 0, start.y));
            var vn = alongNormals ? mesh.VertexNormals() : null;
            float minFac = Mathf.Exp(-(width * narrowness) * (width * narrowness));
            float shift = (time - offset) * speed;
            float fade = 1f;
            if (life > 0 && time - offset > life)
                fade = damping > 0 ? Mathf.Clamp01(1f - (time - offset - life) / damping) : 0f;
            if (fade <= 0) return;
            var axes = new Vector3(normalX ? 1 : 0, normalY ? 1 : 0, normalZ ? 1 : 0);
            for (int i = 0; i < mesh.pos.Count; i++)
            {
                var p = mesh.pos[i];
                float dx = p.x - sp.x, dy = p.z - sp.z;
                float dist = motionX && motionZ ? Mathf.Sqrt(dx * dx + dy * dy) : motionX ? dx : dy;
                float x = dist - shift;
                if (cyclic) x = Mathf.Repeat(x + width, 2f * width) - width;
                if (x <= -width || x >= width) continue;
                float a = x * narrowness;
                float amp = (Mathf.Exp(-a * a) - minFac) * height * fade;
                mesh.pos[i] = p + (alongNormals ? Vector3.Scale(vn[i], axes) : Vector3.up) * amp;
            }
        }
    }
}
