using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace BlendProperty.Tests
{
    public class DynamicPaintTests
    {
        readonly List<Object> objects = new List<Object>();
        Material material;
        Texture2D positions;
        readonly Dictionary<RenderTexture, int> passes = new Dictionary<RenderTexture, int>();
        Vector4[] a, b, c, d, e, f;

        T Keep<T>(T value) where T : Object { objects.Add(value); return value; }

        [SetUp]
        public void SetUp()
        {
            var shader = Shader.Find("Hidden/Blend Property/Dynamic Paint");
            Assert.IsNotNull(shader);
            Assert.IsTrue(shader.isSupported);
            Assert.IsFalse(ShaderUtil.ShaderHasError(shader));
            material = Keep(new Material(shader));
            positions = Keep(new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true));
            positions.filterMode = FilterMode.Point;
            var px = new Color[32 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++) px[y * 32 + x] = new Color((x + 0.5f) / 32 - 0.5f, (y + 0.5f) / 32 - 0.5f, 0, 1);
            positions.SetPixels(px); positions.Apply();
            material.SetTexture("_Pos", positions);
            material.SetMatrix("_ObjectToWorld", Matrix4x4.identity);
            a = new Vector4[8]; b = new Vector4[8]; c = new Vector4[8];
            d = new Vector4[8]; e = new Vector4[8]; f = new Vector4[8];
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in objects) Object.DestroyImmediate(o);
            objects.Clear();
            passes.Clear();
        }

        Texture2D Tip(Color left, Color right)
        {
            var texture = Keep(new Texture2D(2, 1, TextureFormat.RGBAFloat, false, true));
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Repeat; // shader must clip, even with repeating source images
            texture.SetPixels(new[] { left, right }); texture.Apply();
            return texture;
        }

        RenderTexture Target(int pass)
        {
            var target = Keep(new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear));
            target.Create();
            var previous = RenderTexture.active;
            RenderTexture.active = target; GL.Clear(false, true, Color.clear); RenderTexture.active = previous;
            passes.Add(target, pass);
            return target;
        }

        void Upload(int count)
        {
            material.SetFloat("_BrushCount", count);
            material.SetVectorArray("_BrushA", a); material.SetVectorArray("_BrushB", b);
            material.SetVectorArray("_BrushC", c); material.SetVectorArray("_BrushD", d);
            material.SetVectorArray("_BrushE", e); material.SetVectorArray("_BrushF", f);
        }

        void Step(RenderTexture target)
        {
            // Render the real CRT pass directly: EditMode tests do not run the native CRT update loop.
            var previous = RenderTexture.GetTemporary(target.descriptor);
            var commands = new CommandBuffer();
            try
            {
                Graphics.CopyTexture(target, previous);
                material.SetTexture("_SelfTexture2D", previous);
                var centers = new Vector4[16]; centers[0] = new Vector4(0.5f, 0.5f, 0.5f, 0);
                var sizes = new Vector4[16]; sizes[0] = new Vector4(1, 1, 1, 0);
                material.SetVectorArray("CustomRenderTextureCenters", centers);
                material.SetVectorArray("CustomRenderTextureSizesAndRotations", sizes);
                material.SetVector("CustomRenderTextureParameters", Vector4.zero);
                material.SetVector("_CustomRenderTextureInfo", new Vector4(32, 32, 1, 0));
                commands.SetRenderTarget(target);
                commands.DrawProcedural(Matrix4x4.identity, material, passes[target], MeshTopology.Triangles, 6);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally { commands.Release(); RenderTexture.ReleaseTemporary(previous); }
        }

        Color Pixel(RenderTexture target, int x, int y = 16)
        {
            var previous = RenderTexture.active;
            var read = Keep(new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true));
            try
            {
                RenderTexture.active = target;
                read.ReadPixels(new Rect(x, y, 1, 1), 0, 0); read.Apply();
                return read.GetPixel(0, 0);
            }
            finally { RenderTexture.active = previous; }
        }

        [Test]
        public void TipMask_AffectsPaintAndWetness_WithoutReadWrite()
        {
            a[0] = new Vector4(0, 0, 0, 0.45f); b[0] = new Vector4(1, 0, 0, 1);
            c[0] = new Vector4(0, 1, 0, 0);
            e[0] = new Vector4(1f / 0.9f, 0, 0, 0); f[0] = new Vector4(0, 1f / 0.9f, 0, 0);
            var tip = Tip(Color.white, new Color(1, 1, 1, 0));
            tip.Apply(false, true);
            material.SetTexture("_Tip0", tip); Upload(1);
            var paint = Target(0); var wet = Target(2);
            Step(paint); Step(wet);
            Assert.Greater(Pixel(paint, 9).a, 0.95f);
            Assert.Less(Pixel(paint, 22).a, 0.01f);
            Assert.Greater(Pixel(wet, 9).r, 0.95f);
            Assert.Less(Pixel(wet, 22).r, 0.01f);

            material.SetTexture("_Tip0", Tip(Color.black, Color.white));
            var grayscale = Target(0);
            Step(grayscale);
            Assert.Less(Pixel(grayscale, 9).a, 0.01f);
            Assert.Greater(Pixel(grayscale, 22).a, 0.95f);
        }

        [Test]
        public void EightBrushes_KeepIndependentColourTextures()
        {
            for (int i = 0; i < 8; i++)
            {
                float x = (i * 4 + 2.5f) / 32 - 0.5f;
                a[i] = new Vector4(x, 0, 0, 0.04f); d[i] = new Vector4(x, 0, 0, 0);
                b[i] = Vector4.one; c[i] = new Vector4(0, 1, 1, 0);
                e[i] = new Vector4(12.5f, 0, 0, 0); f[i] = new Vector4(0, 12.5f, 0, 0);
                var colour = i % 2 == 0 ? Color.red : Color.blue;
                material.SetTexture("_Tip" + i, Tip(colour, colour));
            }
            Upload(8);
            var paint = Target(0);
            Step(paint);
            for (int i = 0; i < 8; i++)
            {
                var pixel = Pixel(paint, i * 4 + 2);
                Assert.Greater(pixel.a, 0.95f, "slot " + i);
                Assert.That(pixel.r, Is.EqualTo(i % 2 == 0 ? 1 : 0).Within(0.01f), "slot " + i);
                Assert.That(pixel.b, Is.EqualTo(i % 2 == 0 ? 0 : 1).Within(0.01f), "slot " + i);
            }
        }

        [Test]
        public void SweptHardStroke_FillsGaps_AndEraserRemovesPaintAndWetness()
        {
            a[0] = new Vector4(0.3f, 0, 0, 0.06f); d[0] = new Vector4(-0.3f, 0, 0, 0);
            b[0] = new Vector4(1, 0, 0, 1); Upload(1);
            var paint = Target(0); var wet = Target(2);
            Step(paint); Step(wet);
            Assert.Greater(Pixel(paint, 16).a, 0.95f, "midpoint between frames must be painted");
            Assert.Less(Pixel(paint, 16, 22).a, 0.01f);
            b[0].w = -1; Upload(1);
            Step(paint); Step(wet);
            Assert.Less(Pixel(paint, 16).a, 0.01f);
            Assert.Less(Pixel(wet, 16).r, 0.01f);
        }

        [Test]
        public void OpacityAccumulates_ClearResets_AndZeroRadiusIsEmpty()
        {
            a[0] = new Vector4(0, 0, 0, 0.4f); b[0] = new Vector4(1, 0, 0, 0.25f); Upload(1);
            var paint = Target(0);
            Step(paint);
            Assert.That(Pixel(paint, 16).a, Is.EqualTo(0.25f).Within(0.01f));
            Step(paint);
            Assert.That(Pixel(paint, 16).a, Is.EqualTo(0.4375f).Within(0.01f));
            material.SetFloat("_Clear", 1);
            Step(paint);
            Assert.Less(Pixel(paint, 16).a, 0.01f);
            material.SetFloat("_Clear", 0); a[0].w = 0; Upload(1);
            Step(paint);
            Assert.Less(Pixel(paint, 16).a, 0.01f);
        }

        [Test]
        public void Composite_PaintsTransparentBase_AndEraseRestoresTexture()
        {
            a[0] = new Vector4(0, 0, 0, 0.45f); b[0] = new Vector4(1, 0, 0, 0.5f); Upload(1);
            var paint = Target(0); Step(paint);
            material.SetTexture("_Paint", paint);
            material.SetTexture("_Base", Tip(new Color(0, 0, 1, 0.25f), Color.green));
            material.SetVector("_BaseST", new Vector4(1, 1, 0, 0));
            material.SetTexture("_Wet", Texture2D.blackTexture);
            var output = Target(1); Step(output);
            var left = Pixel(output, 9);
            Assert.That(left.a, Is.EqualTo(0.625f).Within(0.01f));
            Assert.That(left.r, Is.EqualTo(0.8f).Within(0.01f));
            Assert.That(left.b, Is.EqualTo(0.2f).Within(0.01f));
            b[0].w = -1; Upload(1); Step(paint); Step(output);
            left = Pixel(output, 9);
            Assert.That(left.a, Is.EqualTo(0.25f).Within(0.01f));
            Assert.That(left.b, Is.EqualTo(1f).Within(0.01f));
            Assert.That(Pixel(output, 22).g, Is.EqualTo(1f).Within(0.01f));
        }

        [Test]
        public void Airbrush_TextureMaskBuildsUpEquallyAt30And60Hz()
        {
            a[0] = new Vector4(0, 0, 0, 0.4f); b[0] = new Vector4(1, 0, 0, 0.25f);
            c[0] = new Vector4(0, 1, 0, 1);
            e[0] = new Vector4(1.25f, 0, 0, 0); f[0] = new Vector4(0, 1.25f, 0, 0);
            material.SetTexture("_Tip0", Tip(new Color(0.5f, 0.5f, 0.5f, 1), new Color(0.5f, 0.5f, 0.5f, 1)));
            Upload(1);
            var fast = Target(0); Step(fast); Step(fast);
            c[0].w = 2; Upload(1);
            var slow = Target(0); Step(slow);
            Assert.That(Pixel(slow, 16).a, Is.EqualTo(0.234375f).Within(0.001f));
            Assert.That(Pixel(slow, 16).a, Is.EqualTo(Pixel(fast, 16).a).Within(0.001f));
        }

        // Reflection keeps the main package's shader tests usable without the optional VRChat assemblies.
        Component ControlBehaviour(GameObject go, string name)
        {
            var type = System.Type.GetType("BlendProperty.Udon." + name + ", BlendProperty.Udon");
            if (type == null) Assert.Ignore("VRChat/Udon is not installed.");
            // A raw C# component exercises the controls; UdonSharp suppresses callbacks on Editor proxies.
            return go.AddComponent(type);
        }

        static void Field(Component component, string name, object value)
        {
            var field = component.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            field.SetValue(component, field.FieldType.IsEnum ? System.Enum.ToObject(field.FieldType, value) : value);
        }

        static void Call(Component component, string name) => component.GetType().GetMethod(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Invoke(component, null);

        static void PreviousFrame(Component canvas)
        {
            var frames = (int[])canvas.GetType().GetField("lastFrames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(canvas);
            for (int i = 0; i < frames.Length; i++) frames[i] = Time.frameCount - 1;
        }

        [Test]
        public void RuntimeControls_TipPressurePickupAndStrokeBoundaries()
        {
            var go = Keep(GameObject.CreatePrimitive(PrimitiveType.Quad)); go.hideFlags = HideFlags.HideAndDontSave;
            go.GetComponent<Renderer>().sharedMaterial = Keep(new Material(material.shader));
            var canvas = ControlBehaviour(go, "BlendUdonDynamicPaint");
            var brushObject = Keep(new GameObject("Test brush") { hideFlags = HideFlags.HideAndDontSave });
            brushObject.transform.position = Vector3.one * 100;
            var brush = ControlBehaviour(brushObject, "BlendUdonPaintBrush");
            var nib = Keep(new GameObject("Test nib") { hideFlags = HideFlags.HideAndDontSave });
            Field(brush, "tip", nib.transform); Field(brush, "requireUse", true);
            Field(brush, "opacity", 0.5f); Field(brush, "pressure", 0.5f); Field(brush, "color", new Color(1, 0, 0, 0.5f));
            Field(brush, "angle", 90f);
            var brushes = System.Array.CreateInstance(brush.GetType(), 1); brushes.SetValue(brush, 0);
            Field(canvas, "brushes", brushes); Field(canvas, "textureProperty", "_Base");
            foreach (string name in new[] { "paint", "wetmap", "output" })
            {
                var rt = Keep(new CustomRenderTexture(32, 32));
                rt.material = name == "paint" ? material : Keep(new Material(material));
                Field(canvas, name, rt);
            }
            LogAssert.Expect(LogType.Error, "Instantiating material due to calling renderer.material during edit mode. This will leak materials into the scene. You most likely want to use renderer.sharedMaterial instead.");
            Call(canvas, "Start"); Keep(go.GetComponent<Renderer>().sharedMaterial);
            Call(canvas, "PostLateUpdate"); // initial clear
            Call(canvas, "PostLateUpdate");
            Assert.AreEqual(0, material.GetFloat("_BrushCount"));
            Call(brush, "OnPickupUseDown"); Call(canvas, "PostLateUpdate");
            Assert.AreEqual(1, material.GetFloat("_BrushCount"), "use the nib, not the distant brush root");
            Assert.That(material.GetVectorArray("_BrushB")[0].w, Is.EqualTo(0.125f).Within(0.001f));
            Assert.That(material.GetVectorArray("_BrushE")[0].y, Is.EqualTo(5f).Within(0.001f), "rotate texture around nib Z");
            Assert.That(material.GetVectorArray("_BrushF")[0].x, Is.EqualTo(-5f).Within(0.001f));

            Field(brush, "tool", 1); PreviousFrame(canvas);
            Call(canvas, "PostLateUpdate");
            Assert.AreEqual(1, material.GetFloat("_BrushCount"), "switching tools starts a new stroke");
            PreviousFrame(canvas);
            Call(canvas, "PostLateUpdate");
            Assert.AreEqual(0, material.GetFloat("_BrushCount"), "stationary Pen must not build up");
            nib.transform.position = new Vector3(0.2f, 0, 0); PreviousFrame(canvas);
            Call(canvas, "PostLateUpdate");
            Assert.AreEqual(1, material.GetFloat("_BrushCount"));
            Assert.AreEqual(0, material.GetVectorArray("_BrushD")[0].x, "moving pen connects from the prior nib position");
            Assert.AreEqual(0, material.GetVectorArray("_BrushC")[0].x, "Pen has a hard edge");

            Call(brush, "OnPickupUseUp"); Call(brush, "OnPickupUseDown");
            nib.transform.position = new Vector3(-0.2f, 0, 0); PreviousFrame(canvas);
            Call(canvas, "PostLateUpdate");
            Assert.AreEqual(-0.2f, material.GetVectorArray("_BrushD")[0].x, "a new trigger stroke must not bridge");
            Field(brush, "tool", 3); Call(brush, "_StartDrawing"); PreviousFrame(canvas);
            Call(canvas, "PostLateUpdate"); Assert.AreEqual(1, material.GetFloat("_BrushCount"));
            PreviousFrame(canvas); Call(canvas, "PostLateUpdate");
            Assert.AreEqual(0, material.GetFloat("_BrushCount"), "Stamp applies once");
            Call(canvas, "ClearPaint"); Call(canvas, "PostLateUpdate");
            Call(brush, "_StartDrawing"); Call(canvas, "PostLateUpdate");
            Assert.AreEqual(1, material.GetFloat("_BrushCount"), "clear must allow a new stamp");

            Field(brush, "tool", 2); Field(canvas, "lastUpdate", Time.time - 1f / 30f);
            Call(canvas, "PostLateUpdate");
            Assert.That(material.GetVectorArray("_BrushB")[0].w, Is.EqualTo(0.125f).Within(0.001f));
            Assert.That(material.GetVectorArray("_BrushC")[0].w, Is.EqualTo(2f).Within(0.01f), "Airbrush sends elapsed time for per-texel buildup");
            Field(brush, "pressure", 0f); Call(canvas, "PostLateUpdate");
            Assert.AreEqual(0, material.GetFloat("_BrushCount"));
            Field(brush, "pressure", 1f); Call(brush, "OnDrop"); Call(canvas, "PostLateUpdate");
            Assert.AreEqual(0, material.GetFloat("_BrushCount"), "dropping lifts the pen");
        }
    }
}
