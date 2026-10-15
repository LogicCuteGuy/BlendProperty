using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BlendProperty.Tests
{
    public class RuntimeCompatibilityTests
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        GameObject go;
        int undoGroup;

        [SetUp]
        public void SetUp()
        {
            Undo.IncrementCurrentGroup();
            undoGroup = Undo.GetCurrentGroup();
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        }

        [TearDown]
        public void TearDown()
        {
            Undo.RevertAllDownToGroup(undoGroup);
            if (go) Object.DestroyImmediate(go);
        }

        Component Add(string name)
        {
            var type = Type.GetType("BlendProperty.Udon." + name + ", BlendProperty.Udon", true);
            var undo = Type.GetType("UdonSharpEditor.UdonSharpUndo, UdonSharp.Editor");
            return undo == null ? go.AddComponent(type) : (Component)undo.GetMethod("AddComponent", new[] { typeof(GameObject), typeof(Type) }).Invoke(null, new object[] { go, type });
        }

        static void Set(Component c, string field, object value) { c.GetType().GetField(field, Fields).SetValue(c, value); }
        static object Get(Component c, string field) { return c.GetType().GetField(field, Fields).GetValue(c); }
        static void Call(Component c, string method) { c.GetType().GetMethod(method, Fields).Invoke(c, null); }

        [Test]
        public void DeformRetainsMaterialsForWorldExport()
        {
            var deform = Add("BlendUdonDeformStack");
            var filter = go.GetComponent<MeshFilter>();
            var source = filter.sharedMesh;
            var bake = Type.GetType("BlendProperty.Udon.Editor.RuntimeModifiers, BlendProperty.Udon.Editor", true)
                .GetMethod("Bake", BindingFlags.Static | BindingFlags.NonPublic);
            bake.Invoke(null, new object[] { deform, source });
            var output = (CustomRenderTexture)Get(deform, "output");
            var bounds = (CustomRenderTexture)Get(deform, "bounds");
            var material = output.material;
            var boundsMaterial = bounds.material;
            var rest = material.GetTexture("_Rest");
            try
            {
                Assert.AreSame(material, Get(deform, "outputMaterial"));
                Assert.AreSame(boundsMaterial, Get(deform, "boundsMaterial"));
                var utility = Type.GetType("UdonSharpEditor.UdonSharpEditorUtility, UdonSharp.Editor");
                if (utility != null)
                    utility.GetMethod("CopyProxyToUdon", new[] { deform.GetType().BaseType }).Invoke(null, new object[] { deform });
                output.material = null;
                bounds.material = null;
                foreach (var component in go.GetComponents<Component>())
                    if (component.GetType().FullName == "VRC.Udon.UdonBehaviour")
                    {
                        ((ISerializationCallbackReceiver)component).OnBeforeSerialize();
                        var references = (System.Collections.Generic.List<Object>)component.GetType()
                            .GetField("publicVariablesUnityEngineObjects", Fields).GetValue(component);
                        Assert.IsTrue(references.Contains(material), "Deform material must be serialized into Udon.");
                        Assert.IsTrue(references.Contains(boundsMaterial), "Bounds material must be serialized into Udon.");
                    }
                Assert.IsNotNull(rest);
                // Udon proxies suppress Start in Edit Mode; the client build exercises its compiled entry point.
                if (utility == null)
                {
                    Call(deform, "Start");
                    Assert.AreSame(material, output.material);
                    Assert.AreSame(boundsMaterial, bounds.material);
                    Assert.IsTrue(((Behaviour)deform).enabled);
                }
            }
            finally
            {
                if (filter.sharedMesh != source) Object.DestroyImmediate(filter.sharedMesh);
                Object.DestroyImmediate(output);
                Object.DestroyImmediate(bounds);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(boundsMaterial);
                Object.DestroyImmediate(rest);
            }
        }

        [Test]
        public void SyncedPanelSkipsEmptyOptionalControlsWhenApplyingReceivedState()
        {
            var panel = Add("BlendUdonPanel");
            Set(panel, "ready", true);
            Set(panel, "toggles", new Toggle[0]);
            Set(panel, "sliders", new Slider[0]);
            var control = new GameObject("Optional slider", typeof(RectTransform), typeof(Slider));
            control.transform.SetParent(go.transform);
            var slider = control.GetComponent<Slider>();
            var synced = Add("BlendUdonSyncedPanel");
            Set(synced, "panel", panel);
            Set(synced, "toggles", new Toggle[] { null });
            Set(synced, "sliders", new[] { null, slider });
            Set(synced, "toggleValues", new[] { true });
            Set(synced, "sliderValues", new[] { 0f, 0.75f });
            Call(synced, "Pull");
            Assert.AreEqual(0.75f, slider.value);
            Assert.AreEqual(false, Get(synced, "pulling"));
        }
    }
}
