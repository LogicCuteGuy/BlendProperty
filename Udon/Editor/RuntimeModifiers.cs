#if BLEND_UDON
using UdonSharpEditor;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
using UdonSharpUndo = BlendProperty.Standalone.Editor.NativeUndo;
using UdonSharpEditorUtility = BlendProperty.Standalone.Editor.NativeEditorUtility;
using UdonSharpGUI = BlendProperty.Standalone.Editor.NativeGUI;
#endif
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using BlendProperty.Editor;

namespace BlendProperty.Udon.Editor
{
    /// <summary>
    /// At build (and entering Play mode), a Modifier Stack whose Wave / Simple Deform / Lattice / Hook / Warp modifiers
    /// have Runtime on ships its mesh without them and gets a BlendUdonDeformStack that runs them live. Control
    /// Panels pointing at one of those modifiers are pointed at the new component.
    /// </summary>
    [InitializeOnLoad]
    static class RuntimeModifiers
    {
        const int MaxOps = 8;

        static RuntimeModifiers() { BlendModifierCommands.BuildRuntime = Build; }

        static Mesh Build(BlendModifierStack stack)
        {
            var mods = stack.Modifiers().Where(m => m.enabled && m.Runtime != BlendModifier.RuntimeMode.Off).ToList();
            if (mods.Count == 0 || !stack.RuntimeBase) return null;
            if (mods.Count > MaxOps) Debug.LogWarning($"[Blend Property] '{stack.name}': only the first {MaxOps} Runtime modifiers run in the world.", stack);
            mods = mods.Take(MaxOps).ToList();

            var go = stack.gameObject;
            var d = (BlendUdonDeformStack)UdonSharpUndo.AddComponent(go, typeof(BlendUdonDeformStack));
            int n = mods.Count;
            d.opNames = new string[n]; d.opTypes = new int[n];
            d.opA = new Vector4[n]; d.opB = new Vector4[n]; d.opC = new Vector4[n]; d.opD = new Vector4[n];
            d.opT0 = new Transform[n]; d.opT1 = new Transform[n]; d.opBind = new Vector4[n * 4];
            d.latticePoints = new Vector4[0];
            var names = new Dictionary<string, int>();

            for (int i = 0; i < n; i++)
            {
                var m = mods[i];
                string label = ObjectNames.NicifyVariableName(m.GetType().Name.Replace("Modifier", ""));
                names[label] = names.TryGetValue(label, out int seen) ? seen + 1 : 1;
                d.opNames[i] = names[label] > 1 ? $"{label} {names[label]}" : label;
                switch (m)
                {
                    case WaveModifier w:
                        d.opTypes[i] = BlendUdonDeformStack.Wave;
                        d.opA[i] = new Vector4(w.height, w.width, w.narrowness, w.speed);
                        d.opB[i] = new Vector4(w.start.x, w.start.y, w.offset, w.time);
                        d.opC[i] = new Vector4(B(w.motionX), B(w.motionZ), B(w.cyclic), B(w.animate));
                        d.opD[i] = new Vector4(w.life, w.damping, 0, 0);
                        d.opT0[i] = w.startObject;
                        if (w.alongNormals) Debug.LogWarning($"[Blend Property] '{stack.name}': Wave Along Normals isn't live in the world; it lifts along Y there.", w);
                        break;
                    case SimpleDeformModifier s:
                        d.opTypes[i] = BlendUdonDeformStack.SimpleDeform;
                        d.opA[i] = new Vector4((int)s.mode, s.angle, s.factor, (int)s.axis);
                        int locks = (s.lockX && s.axis != SimpleDeformModifier.Axis.X ? 1 : 0) | (s.lockY && s.axis != SimpleDeformModifier.Axis.Y ? 2 : 0)
                                  | (s.lockZ && s.axis != SimpleDeformModifier.Axis.Z ? 4 : 0);
                        d.opB[i] = new Vector4(s.lowerLimit, s.upperLimit, locks, 0);
                        d.opT0[i] = s.origin;
                        break;
                    case LatticeModifier l:
                        d.opTypes[i] = BlendUdonDeformStack.Lattice;
                        if (!l.lattice || d.latticePoints.Length > 0)
                        {
                            if (l.lattice) Debug.LogWarning($"[Blend Property] '{stack.name}': only one live Lattice per mesh; this one stays still.", l);
                            d.opA[i] = new Vector4(2, 2, 2, 0); // no lattice: strength 0
                            break;
                        }
                        var lat = l.lattice;
                        lat.SyncChildren();
                        lat.EnsurePoints();
                        if (lat.UsesChildren) { d.latticeCage = lat.transform; d.latticeOp = i; }
                        d.opA[i] = new Vector4(lat.resolutionU, lat.resolutionV, lat.resolutionW, l.strength);
                        d.opT0[i] = lat.transform;
                        d.latticePoints = new Vector4[lat.points.Length];
                        for (int w = 0; w < lat.resolutionW; w++)
                            for (int v = 0; v < lat.resolutionV; v++)
                                for (int u = 0; u < lat.resolutionU; u++)
                                {
                                    int k = lat.Index(u, v, w);
                                    d.latticePoints[k] = lat.points[k] - lat.Rest(u, v, w);
                                }
                        break;
                    case HookModifier h:
                        d.opTypes[i] = BlendUdonDeformStack.Hook;
                        d.opA[i] = new Vector4(h.strength, h.radius, (int)h.falloff, 0);
                        var bind = h.BindMatrix;
                        d.opB[i] = bind.GetColumn(3);
                        var inv = bind.inverse;
                        for (int r = 0; r < 4; r++) d.opBind[i * 4 + r] = inv.GetRow(r);
                        d.opT0[i] = h.target;
                        break;
                    case WarpModifier wp:
                        d.opTypes[i] = BlendUdonDeformStack.Warp;
                        d.opA[i] = new Vector4(wp.strength, wp.radius, (int)wp.falloff, 0);
                        d.opT0[i] = wp.from;
                        d.opT1[i] = wp.to;
                        if (wp.preserveVolume) Debug.LogWarning($"[Blend Property] '{stack.name}': Warp Preserve Volume isn't live in the world.", wp);
                        break;
                }
            }

            var baseMesh = stack.RuntimeBase;
            if (d.latticePoints.Length > 512)
                Debug.LogWarning($"[Blend Property] '{stack.name}': a live Lattice has at most 512 points (8 × 8 × 8); the Runtime modifiers stay still.", stack);
            else Bake(d, baseMesh);
            UdonSharpEditorUtility.CopyProxyToUdon(d);

            foreach (var p in Object.FindObjectsByType<BlendUdonPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                int op = mods.IndexOf(p.modifier as BlendModifier);
                if (op < 0) continue;
                p.deform = d;
                p.deformOp = op;
                UdonSharpEditorUtility.CopyProxyToUdon(p);
            }
            return baseMesh;
        }

        static float B(bool b) => b ? 1f : 0f;

        /// <summary>The GPU textures for <paramref name="d"/>, kept in the scene (not assets).</summary>
        static void Bake(BlendUdonDeformStack d, Mesh mesh)
        {
            var verts = mesh.vertices;
            int VW = Mathf.CeilToInt(Mathf.Sqrt(verts.Length)), VH = Mathf.CeilToInt(verts.Length / (float)VW);
            var rest = new Texture2D(VW, VH, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Rest" };
            var px = new Color[VW * VH];
            for (int i = 0; i < verts.Length; i++) px[i] = new Color(verts[i].x, verts[i].y, verts[i].z, 1);
            rest.SetPixels(px); rest.Apply(false, false);

            var shader = Shader.Find("Hidden/Blend Property/Deform Stack");
            Material Mat(string name)
            {
                var m = new Material(shader) { name = name };
                m.SetTexture("_Rest", rest);
                m.SetFloat("_VW", VW); m.SetFloat("_VH", VH); m.SetFloat("_N", verts.Length);
                return m;
            }
            var boundsMat = Mat("Bounds");
            var boundsTex = new CustomRenderTexture(MaxOps, 1, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "Bounds", material = boundsMat, shaderPass = 1, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            var outMat = Mat("Deform");
            outMat.SetTexture("_Bounds", boundsTex);
            var outTex = new CustomRenderTexture(VW, VH, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "Deform", material = outMat, shaderPass = 0, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnDemand, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            d.output = outTex;
            d.bounds = boundsTex;
            d.outputMaterial = outMat;
            d.boundsMaterial = boundsMat;
            d.vertexCount = verts.Length;
        }
    }
}
