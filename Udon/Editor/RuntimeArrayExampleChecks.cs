#if BLEND_UDON
using System;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace BlendProperty.Udon.Editor
{
    public static class RuntimeArrayExampleChecks
    {
        public static void Run()
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play Mode in the Runtime Generator example first.");
            var examples = UnityEngine.Object.FindObjectsOfType<BlendUdonRuntimeArray>();
            if (examples.Length == 0) throw new InvalidOperationException("No runtime array examples are active.");
            int checks = 0;
            foreach (var example in examples)
            {
                UdonBehaviour udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(example);
                Mesh source = (Mesh)udon.GetProgramVariable("source");
                var originalVertices = source.vertices;
                int savedCount = (int)udon.GetProgramVariable("count");
                bool savedCircle = (bool)udon.GetProgramVariable("circle");
                float savedSpacing = (float)udon.GetProgramVariable("spacing");
                float savedRadius = (float)udon.GetProgramVariable("radius");
                Mesh output = example.GetComponent<MeshFilter>().sharedMesh;
                try
                {
                    foreach (bool circle in new[] { false, true })
                    foreach (int requested in new[] { -1, 0, 1, 4, 32, int.MaxValue })
                    {
                        udon.SetProgramVariable("circle", circle);
                        udon.SetProgramVariable("count", requested);
                        udon.SetProgramVariable("spacing", 0.75f);
                        udon.SetProgramVariable("radius", 1.5f);
                        udon.SendCustomEvent("Generate");
                        int copies = Mathf.Clamp(requested, 1, Mathf.Min(32, 65535 / source.vertexCount));
                        Mesh mesh = example.GetComponent<MeshFilter>().sharedMesh;
                        Require(mesh != null && mesh != source && mesh == output, "Output must reuse its own mesh.");
                        Require(mesh.vertexCount == source.vertexCount * copies, "Vertex count / count clamp failed.");
                        Require(mesh.triangles.Length == source.triangles.Length * copies, "Triangle count failed.");
                        var vertices = mesh.vertices;
                        for (int copy = 0; copy < copies; copy++)
                        {
                            float angle = 2f * Mathf.PI * copy / copies;
                            Vector3 offset = circle
                                ? new Vector3(Mathf.Cos(angle) * 1.5f, 0f, -Mathf.Sin(angle) * 1.5f)
                                : new Vector3((copy - (copies - 1) / 2f) * 0.75f, 0f, 0f);
                            Quaternion rotation = circle ? Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.up) : Quaternion.identity;
                            for (int v = 0; v < originalVertices.Length; v++)
                                Require(Vector3.Distance(vertices[copy * originalVertices.Length + v], rotation * originalVertices[v] + offset) < 0.0001f,
                                    "Copy placement / circle rotation failed.");
                        }
                        foreach (int index in mesh.triangles) Require(index >= 0 && index < mesh.vertexCount, "Triangle index out of range.");
                        checks++;
                    }
                    udon.SetProgramVariable("source", null);
                    udon.SendCustomEvent("Generate");
                    Require(example.GetComponent<MeshFilter>().sharedMesh == output, "Invalid input replaced the previous output.");
                    Require(source.vertexCount == originalVertices.Length && source.vertices[0] == originalVertices[0], "Source mesh was modified.");
                }
                finally
                {
                    udon.SetProgramVariable("source", source);
                    udon.SetProgramVariable("count", savedCount);
                    udon.SetProgramVariable("circle", savedCircle);
                    udon.SetProgramVariable("spacing", savedSpacing);
                    udon.SetProgramVariable("radius", savedRadius);
                    udon.SendCustomEvent("Generate");
                }
            }
            Debug.Log("[Runtime Array] Passed " + checks + " Udon generation checks, plus invalid-input and source-preservation checks.");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

#endif
