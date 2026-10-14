using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.TestTools;

namespace BlendProperty.Tests
{
    /// <summary>Live editor path: delayed rebuild, baked asset persistence, ProBuilder resync and Apply.</summary>
    public class ProBuilderLiveTests
    {
        static IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        static void Cleanup(int undoGroup, GameObject go, params Object[] assets)
        {
            Undo.RevertAllDownToGroup(undoGroup); // keep the runner's undo replay from resurrecting objects
            foreach (var a in assets)
                if (a && EditorUtility.IsPersistent(a)) AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(a));
            if (go) Object.DestroyImmediate(go);
        }

        [UnityTest] public IEnumerator Component_RebuildsAndPersists()
        {
            int undo = Undo.GetCurrentGroup();
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var sub = go.AddComponent<SubdivisionModifier>();
            sub.levels = 1;
            yield return Frames(3);
            var stack = go.GetComponent<BlendModifierStack>();
            var filter = go.GetComponent<MeshFilter>();
            try
            {
                Assert.AreSame(stack.Output, filter.sharedMesh);
                Assert.IsTrue(EditorUtility.IsPersistent(stack.Output));
                Assert.Greater(filter.sharedMesh.vertexCount, 24);

                sub.enabled = false; // viewport toggle
                yield return Frames(3);
                Assert.AreEqual(24, filter.sharedMesh.vertexCount); // disabled modifier = plain cube
            }
            finally { Cleanup(undo, go, stack ? stack.Output : null); }
        }

        [UnityTest] public IEnumerator ProBuilder_ResyncAndApply()
        {
            int undo = Undo.GetCurrentGroup();
            var pb = ShapeGenerator.CreateShape(ShapeType.Cube);
            var go = pb.gameObject;
            var array = go.AddComponent<ArrayModifier>();
            array.count = 2;
            yield return Frames(3);
            var stack = go.GetComponent<BlendModifierStack>();
            var filter = go.GetComponent<MeshFilter>();
            Mesh output = stack.Output;
            try
            {
                int baseVerts = pb.vertexCount;
                Assert.AreSame(output, filter.sharedMesh);

                // ProBuilder rewrites the filter on refresh; the stack must win again on the next rebuild
                pb.ToMesh(); pb.Refresh();
                Assert.AreNotSame(output, filter.sharedMesh);
                stack.Rebuild(true);
                Assert.AreSame(output, filter.sharedMesh);

                Editor.BlendModifierCommands.Apply(array);
                Assert.IsFalse(go.GetComponent<BlendModifierStack>());
                Assert.IsTrue(go.GetComponent<ProBuilderMesh>());
                Assert.Greater(pb.vertexCount, baseVerts); // geometry now lives in ProBuilder, still editable
                Assert.AreEqual(2f, pb.GetComponent<MeshFilter>().sharedMesh.bounds.size.x, 1e-3f);
            }
            finally { Cleanup(undo, go, output); }
        }
    }
}
