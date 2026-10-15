#if !BLEND_UDON
using System;
using System.Collections;
using BlendProperty.Standalone;
using BlendProperty.Udon;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace BlendProperty.Tests
{
    public class StandaloneTests
    {
        GameObject go;
        [SetUp] public void SetUp() { go = new GameObject("Standalone test"); }
        [TearDown] public void TearDown() { UnityEngine.Object.DestroyImmediate(go); }

        [Test]
        public void AllPhysicsComponentsAreNativeBehaviours()
        {
            int count = 0;
            foreach (var type in typeof(BlendUdonCloth).Assembly.GetTypes())
                if (type.Namespace == "BlendProperty.Udon" && type.IsClass && !type.IsAbstract)
                {
                    Assert.IsTrue(typeof(BlendBehaviour).IsAssignableFrom(type), type.Name);
                    count++;
                }
            Assert.AreEqual(21, count);
        }

        [Test]
        public void FieldsAndEventsUseTheExistingPublicApi()
        {
            var brush = go.AddComponent<BlendUdonPaintBrush>();
            brush.SetProgramVariable("pressure", 0.25f);
            Assert.AreEqual(0.25f, brush.GetProgramVariable("pressure"));
            brush.SetProgramVariable("tool", 1);
            Assert.AreEqual(BlendPaintTool.Pen, brush.tool);
            brush.SendCustomEvent(nameof(brush.OnPickupUseDown));
            Assert.IsTrue(brush.useHeld);
            var trigger = go.AddComponent<BlendUdonNetworkEvent>();
            trigger.target = brush;
            trigger.eventName = nameof(brush.OnPickupUseUp);
            trigger.Send();
            Assert.IsFalse(brush.useHeld);
            Assert.Throws<MissingFieldException>(() => brush.GetProgramVariable("missing"));
            Assert.Throws<MissingMethodException>(() => brush.SendCustomEvent("missing"));
            Assert.Throws<ArgumentException>(() => brush.SetProgramVariable("pressure", "wrong type"));
            var panel = go.AddComponent<BlendUdonPanel>();
            Assert.AreEqual(false, panel.GetProgramVariable("ready"));
        }

        [Test]
        public void BrushDropEndsTheStroke()
        {
            var brush = go.AddComponent<BlendUdonPaintBrush>();
            brush.OnPickupUseDown();
            int stroke = brush.strokeId;
            brush.OnDrop();
            Assert.IsFalse(brush.useHeld);
            Assert.AreEqual(stroke + 1, brush.strokeId);
        }

        [Test]
        public void ViewpointWorksWithoutAHumanoidAvatar()
        {
            go.transform.position = new Vector3(1, 1.6f, 2);
            var player = new BlendPlayer(null, go.transform, true);
            Assert.IsTrue(player.IsValid());
            Assert.AreEqual(go.transform.position, player.GetTrackingData(BlendPlayer.TrackingDataType.Head).position);
            Assert.AreEqual(Vector3.zero, player.GetBonePosition(HumanBodyBones.LeftHand));
        }

        [Test]
        public void PlayerRegistrationDeduplicatesSimulationLists()
        {
            var player = go.AddComponent<BlendUdonPlayerColliders>();
            var template = new GameObject("Body Template");
            template.transform.SetParent(go.transform);
            template.AddComponent<BlendUdonPaintBrush>();
            template.SetActive(false);
            player.template = template;
            var canvas = go.AddComponent<BlendUdonDynamicPaint>();
            var original = go.AddComponent<BlendUdonPaintBrush>();
            canvas.brushes = new[] { original };
            player.simulations = new BlendBehaviour[] { canvas };
            player.simulationLists = new[] { 16 };
            var avatarObject = new GameObject("Avatar");
            avatarObject.transform.SetParent(go.transform);
            var avatar = avatarObject.AddComponent<Animator>();
            player.RegisterPlayer(null);
            player.RegisterPlayer(avatar);
            player.RegisterPlayer(avatar);
            Assert.AreEqual(2, canvas.brushes.Length);
        }

        [UnityTest]
        public IEnumerator NativeGpuReadbackCopiesFloatPixels()
        {
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("GPU readback unavailable on this device");
            var target = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGBFloat);
            target.Create();
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.Clear(false, true, new Color(0.25f, 0.5f, 0.75f, 1));
            RenderTexture.active = previous;
            try
            {
                var request = AsyncGPUReadback.Request(target, 0, TextureFormat.RGBAFloat);
                while (!request.done) yield return null;
                var wrapped = new BlendReadbackRequest(request);
                var pixels = new Color[4];
                Assert.IsFalse(wrapped.hasError);
                Assert.IsTrue(wrapped.TryGetData(pixels, 0));
                Assert.AreEqual(0.25f, pixels[0].r, 0.001f);
                Assert.AreEqual(0.75f, pixels[3].b, 0.001f);
                Assert.IsFalse(wrapped.TryGetData(new Color[1], 0));
                Assert.IsFalse(wrapped.TryGetData(null, 0));
                Assert.IsFalse(wrapped.TryGetData(pixels, -1));
                Assert.IsFalse(wrapped.TryGetData(pixels, 1));
            }
            finally { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
        }
    }
}
#endif
