using UnityEngine;

namespace BlendProperty
{
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Weld.png")]
    [AddComponentMenu("Blend Property/Modifiers/Generate/Weld")]
    public class WeldModifier : BlendModifier
    {
        public enum Mode { All, Connected }

        [Tooltip("All: merge any vertices within Distance, loose parts included. Connected: only merge along edges shorter than Distance.")]
        public Mode mode = Mode.All;
        [Min(0)] public float distance = 0.001f;

        public override void Apply(MeshData mesh)
        {
            if (mode == Mode.All) mesh.Weld(MeshUnits(distance));
            else WeldConnected(mesh, MeshUnits(distance));
        }

        /// <summary>Collapse every edge no longer than <paramref name="dist"/>; separate parts never merge.</summary>
        public static void WeldConnected(MeshData m, float dist)
        {
            var parent = new int[m.pos.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            float d2 = dist * dist;
            foreach (var kv in m.EdgeFaces())
            {
                int a = MeshData.EdgeA(kv.Key), b = MeshData.EdgeB(kv.Key);
                if ((m.pos[a] - m.pos[b]).sqrMagnitude <= d2) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            }
            var target = new int[parent.Length];
            for (int i = 0; i < target.Length; i++) target[i] = Find(i);
            m.Remap(target);
        }
    }
}
