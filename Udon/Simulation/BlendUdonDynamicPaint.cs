#if BLEND_UDON
using UdonSharp;
using VRC.SDKBase;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
using VRCShader = UnityEngine.Shader;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Blender Dynamic Paint canvas (Paint surface) on the GPU: BlendUdonPaintBrush objects paint this mesh where they
    /// come within their radius, and Dissolve fades the paint out again. Paint lives in the mesh's UV space, composited
    /// over the material's own texture, so any shader works and nothing is read back to Udon. Brushes also leave the
    /// surface wet (Blender Wetmap): wet areas darken and dry over Dry Time. Use ⋮ → Bake Dynamic Paint.
    /// The mesh needs non-overlapping UVs inside 0..1 (overlapping islands get the same paint). Local only (not synced).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/DynamicPaintCanvas.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Dynamic Paint Canvas")]
    public class BlendUdonDynamicPaint : UdonSharpBehaviour
    {
        [Tooltip("Up to 8 brushes paint at once (the nearest ones are not picked: the first 8 in range).")]
        public BlendUdonPaintBrush[] brushes;
        [Min(0f), Tooltip("Seconds for paint to fade away (Blender Dissolve). 0 = paint stays.")]
        public float dissolve;
        [Min(0f), Tooltip("Seconds for wet areas to dry (Blender Wetmap). 0 = no wetness.")]
        public float dryTime = 4f;
        [Range(0, 1), Tooltip("How much fully wet areas darken.")]
        public float wetDarkening = 0.5f;
        [Tooltip("The material texture the paint is shown on.")]
        public string textureProperty = "_MainTex";
        [Header("Baked (⋮ → Bake Dynamic Paint)")]
        [Range(64, 2048), Tooltip("Paint texture size in pixels. Rebake after changing.")]
        public int resolution = 512;
        public CustomRenderTexture paint;
        public CustomRenderTexture output;
        public CustomRenderTexture wetmap;

        Renderer rend;
        Material paintMat, wetMat, outMat;
        Vector4[] brushA, brushB, brushC, brushD, brushE, brushF;
        BlendUdonPaintBrush[] lastBrushes;
        BlendPaintTool[] lastTools;
        Vector3[] lastPositions;
        int[] lastFrames, lastStrokes, idTips;
        bool[] lastTouching;
        float lastUpdate, paintedUntil;
        bool clear;
        int idObjectToWorld, idFade, idClear, idCount, idA, idB, idC, idD, idE, idF, idDry, idWetDark;

        void Start()
        {
            if (paint == null || output == null || wetmap == null || paint.material == null || output.material == null || wetmap.material == null)
            {
                Debug.LogWarning("[BlendUdonDynamicPaint] Not baked: use ⋮ → Bake Dynamic Paint.", this);
                enabled = false;
                return;
            }
            rend = GetComponent<Renderer>();
            if (rend == null || rend.sharedMaterial == null || !rend.sharedMaterial.HasProperty(textureProperty))
            {
                Debug.LogWarning("[BlendUdonDynamicPaint] Assign a material with the selected Texture Property.", this);
                enabled = false;
                return;
            }
            Material mat = rend.material;
            outMat = output.material;
            Texture baseTex = mat.HasProperty(textureProperty) ? mat.GetTexture(textureProperty) : null;
            if (baseTex != null) outMat.SetTexture("_Base", baseTex);
            Vector2 sc = mat.GetTextureScale(textureProperty), of = mat.GetTextureOffset(textureProperty);
            outMat.SetVector("_BaseST", new Vector4(sc.x, sc.y, of.x, of.y));
            mat.SetTexture(textureProperty, output);
            mat.SetTextureScale(textureProperty, Vector2.one);
            mat.SetTextureOffset(textureProperty, Vector2.zero);

            paintMat = paint.material;
            wetMat = wetmap.material;
            idDry = VRCShader.PropertyToID("_Dry");
            idWetDark = VRCShader.PropertyToID("_WetDark");
            brushA = new Vector4[8]; brushB = new Vector4[8]; brushC = new Vector4[8];
            brushD = new Vector4[8]; brushE = new Vector4[8]; brushF = new Vector4[8];
            idTips = new int[8];
            for (int i = 0; i < 8; i++) idTips[i] = VRCShader.PropertyToID("_Tip" + i);
            idObjectToWorld = VRCShader.PropertyToID("_ObjectToWorld");
            idFade = VRCShader.PropertyToID("_Fade");
            idClear = VRCShader.PropertyToID("_Clear");
            idCount = VRCShader.PropertyToID("_BrushCount");
            idA = VRCShader.PropertyToID("_BrushA");
            idB = VRCShader.PropertyToID("_BrushB");
            idC = VRCShader.PropertyToID("_BrushC");
            idD = VRCShader.PropertyToID("_BrushD");
            idE = VRCShader.PropertyToID("_BrushE");
            idF = VRCShader.PropertyToID("_BrushF");
            lastUpdate = Time.time;
            clear = true; // start clean
        }

        /// <summary>Wipe all paint.</summary>
        public void ClearPaint() { clear = true; }

        // PostLateUpdate: after pickups and Animators moved the brushes
        public override void PostLateUpdate()
        {
            if (paintMat == null) return;
            float now = Time.time;
            int n = 0;
            if (brushes != null && !clear)
            {
                if (lastBrushes == null || lastBrushes.Length != brushes.Length)
                {
                    lastBrushes = new BlendUdonPaintBrush[brushes.Length];
                    lastTools = new BlendPaintTool[brushes.Length];
                    lastPositions = new Vector3[brushes.Length];
                    lastFrames = new int[brushes.Length];
                    lastStrokes = new int[brushes.Length];
                    lastTouching = new bool[brushes.Length];
                }
                Bounds bb = rend.bounds;
                for (int i = 0; i < brushes.Length && n < 8; i++)
                {
                    BlendUdonPaintBrush b = brushes[i];
                    if (b == null || !b.enabled || !b.gameObject.activeInHierarchy || !b.drawing ||
                        (b.requireUse && !b.useHeld) || !(b.pressure > 0f) || !(b.radius > 0f)) continue;
                    Transform tip = b.tip != null ? b.tip : b.transform;
                    Vector3 p = tip.position;
                    float r = b.radius;
                    Vector3 previous = transform.TransformPoint(lastPositions[i]);
                    bool continuous = lastBrushes[i] == b && lastTools[i] == b.tool && lastFrames[i] == Time.frameCount - 1 && lastStrokes[i] == b.strokeId;
                    // shortcut: jumps over 1 m (or 8 radii) start a new stroke; add explicit teleport signals if needed.
                    if (!continuous || Vector3.Distance(p, previous) > Mathf.Max(1f, r * 8f)) { previous = p; continuous = false; }
                    bool touching = bb.SqrDistance(p) < r * r;
                    bool wasTouching = continuous && lastTouching[i];
                    lastBrushes[i] = b;
                    lastTools[i] = b.tool;
                    lastPositions[i] = transform.InverseTransformPoint(p);
                    lastFrames[i] = Time.frameCount;
                    lastStrokes[i] = b.strokeId;
                    lastTouching[i] = touching;
                    bool stamp = b.tool == BlendPaintTool.Stamp;
                    if (stamp && wasTouching) continue;
                    if (stamp) previous = p;
                    Vector3 travel = p - previous;
                    if (!touching)
                    {
                        Bounds swept = bb;
                        swept.Expand(r * 2f);
                        float enter;
                        if (stamp || travel.sqrMagnitude < 1e-10f || !swept.IntersectRay(new Ray(previous, travel.normalized), out enter) || enter > travel.magnitude) continue;
                    }
                    if (b.tool == BlendPaintTool.Pen && continuous && wasTouching && travel.sqrMagnitude < 1e-10f) continue;
                    Color c = b.color.linear;
                    float flow = Mathf.Clamp01(b.strength);
                    float alpha = flow * Mathf.Clamp01(b.opacity) * Mathf.Clamp01(b.pressure) * Mathf.Clamp01(c.a);
                    float rate = b.tool == BlendPaintTool.Airbrush ? Mathf.Max(0f, now - lastUpdate) * 60f : 0f;
                    if (b.tool == BlendPaintTool.Airbrush && rate <= 0f) continue;
                    if (alpha <= 0f) continue;
                    brushA[n] = new Vector4(p.x, p.y, p.z, r);
                    brushB[n] = new Vector4(c.r, c.g, c.b, b.erase ? -alpha : alpha);
                    brushC[n] = new Vector4(b.tool == BlendPaintTool.Pen ? 0f : Mathf.Clamp01(b.softness), b.tipTexture != null ? 1f : 0f, b.useTextureColor ? 1f : 0f, rate);
                    brushD[n] = new Vector4(previous.x, previous.y, previous.z, 0f);
                    float angle = b.angle * Mathf.Deg2Rad;
                    Vector3 right = (tip.right * Mathf.Cos(angle) + tip.up * Mathf.Sin(angle)) / (2f * r);
                    Vector3 up = (tip.up * Mathf.Cos(angle) - tip.right * Mathf.Sin(angle)) / (2f * r);
                    brushE[n] = new Vector4(right.x, right.y, right.z, 0f);
                    brushF[n] = new Vector4(up.x, up.y, up.z, 0f);
                    paintMat.SetTexture(idTips[n], b.tipTexture);
                    wetMat.SetTexture(idTips[n], b.tipTexture);
                    n++;
                }
            }
            bool wet = dryTime > 0f && wetDarkening > 0f;
            if (n > 0) paintedUntil = now + Mathf.Max(dissolve, wet ? dryTime : 0f);
            // nothing touching, nothing fading or drying: the texture stays as it is and costs nothing
            if (n == 0 && !clear && now > paintedUntil) { lastUpdate = now; return; }

            paintMat.SetMatrix(idObjectToWorld, transform.localToWorldMatrix);
            paintMat.SetFloat(idFade, dissolve > 0f ? (now - lastUpdate) / dissolve : 0f);
            paintMat.SetFloat(idClear, clear ? 1f : 0f);
            paintMat.SetFloat(idCount, n);
            paintMat.SetVectorArray(idA, brushA);
            paintMat.SetVectorArray(idB, brushB);
            paintMat.SetVectorArray(idC, brushC);
            paintMat.SetVectorArray(idD, brushD);
            paintMat.SetVectorArray(idE, brushE);
            paintMat.SetVectorArray(idF, brushF);
            outMat.SetFloat(idWetDark, wet ? wetDarkening : 0f);
            if (wet || clear)
            {
                wetMat.SetMatrix(idObjectToWorld, transform.localToWorldMatrix);
                wetMat.SetFloat(idDry, wet ? (now - lastUpdate) / dryTime : 1f);
                wetMat.SetFloat(idClear, clear ? 1f : 0f);
                wetMat.SetFloat(idCount, n);
                wetMat.SetVectorArray(idA, brushA);
                wetMat.SetVectorArray(idB, brushB);
                wetMat.SetVectorArray(idC, brushC);
                wetMat.SetVectorArray(idD, brushD);
                wetMat.SetVectorArray(idE, brushE);
                wetMat.SetVectorArray(idF, brushF);
                wetmap.Update(1);
            }
            clear = false;
            lastUpdate = now;
            paint.Update(1);
            output.Update(1);
        }

#if UNITY_EDITOR && !COMPILER_UDONSHARP
        [ContextMenu("Bake Dynamic Paint")]
        void Bake()
        {
            var src = GetComponent<MeshFilter>().sharedMesh;
            if (src == null) { Debug.LogError("[BlendUdonDynamicPaint] No mesh.", this); return; }
            if (!src.isReadable) { Debug.LogError("[BlendUdonDynamicPaint] Enable Read/Write on the mesh import settings.", this); return; }
            var uvs = src.uv;
            if (uvs == null || uvs.Length == 0) { Debug.LogError("[BlendUdonDynamicPaint] The mesh has no UVs.", this); return; }
            int R = resolution;
            var pos = PositionMap(src.vertices, uvs, src.triangles, R, out int outside);
            if (outside > 0) Debug.LogWarning($"[BlendUdonDynamicPaint] {outside} triangles have UVs outside 0..1 and can't be painted.", this);
            var posTex = new Texture2D(R, R, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Positions" };
            posTex.SetPixels(pos); posTex.Apply(false, false);

            var shader = Shader.Find("Hidden/Blend Property/Dynamic Paint");
            var paintMat = new Material(shader) { name = "Paint" };
            paintMat.SetTexture("_Pos", posTex);
            var paintTex = new CustomRenderTexture(R, R, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "Paint", material = paintMat, shaderPass = 0, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnLoad, initializationColor = Color.clear,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            var wetMat = new Material(shader) { name = "Wet" };
            wetMat.SetTexture("_Pos", posTex);
            var wetTex = new CustomRenderTexture(R, R, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                name = "Wetmap", material = wetMat, shaderPass = 2, doubleBuffered = true, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnLoad, initializationColor = Color.clear,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false,
            };
            var outMat = new Material(shader) { name = "Composite" };
            outMat.SetTexture("_Paint", paintTex);
            outMat.SetTexture("_Wet", wetTex);
            var outTex = new CustomRenderTexture(R, R, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Dynamic Paint", material = outMat, shaderPass = 1, updateMode = CustomRenderTextureUpdateMode.OnDemand,
                initializationMode = CustomRenderTextureUpdateMode.OnLoad, initializationColor = Color.white,
                filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = true, autoGenerateMips = true,
            };

            const string folder = "Assets/BlendProperty/Baked";
            if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/BlendProperty")) UnityEditor.AssetDatabase.CreateFolder("Assets", "BlendProperty");
            if (!UnityEditor.AssetDatabase.IsValidFolder(folder)) UnityEditor.AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
            string path = output != null && UnityEditor.AssetDatabase.Contains(output)
                ? UnityEditor.AssetDatabase.GetAssetPath(output)
                : UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}_dynamicpaint.asset");
            UnityEditor.AssetDatabase.DeleteAsset(path);
            UnityEditor.AssetDatabase.CreateAsset(outTex, path);
            var parts = new Object[] { paintTex, wetTex, outMat, paintMat, wetMat, posTex };
            foreach (Object o in parts) UnityEditor.AssetDatabase.AddObjectToAsset(o, path);
            foreach (Object o in parts) UnityEditor.EditorUtility.SetDirty(o);
            UnityEditor.AssetDatabase.SaveAssets();
            // textures written before their materials were assets lose the link: set them again
            outTex.material = outMat;
            paintTex.material = paintMat;
            wetTex.material = wetMat;
            UnityEditor.EditorUtility.SetDirty(wetTex);
            UnityEditor.EditorUtility.SetDirty(outTex);
            UnityEditor.EditorUtility.SetDirty(paintTex);
            UnityEditor.AssetDatabase.SaveAssets();

            UnityEditor.Undo.RecordObject(this, "Bake Dynamic Paint");
            paint = paintTex;
            output = outTex;
            wetmap = wetTex;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[BlendUdonDynamicPaint] Baked a {R}x{R} canvas to {path}.", this);
        }

        // Rasterise the triangles in UV space: each texel gets the local position under it (a = 1),
        // then grow the islands a few texels so bilinear filtering doesn't pull in empty texels at the seams.
        internal static Color[] PositionMap(Vector3[] verts, Vector2[] uvs, int[] tris, int R, out int outside)
        {
            var px = new Color[R * R];
            outside = 0;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Vector2 a = uvs[i0] * R, b = uvs[i1] * R, c = uvs[i2] * R;
                if (Mathf.Min(a.x, b.x, c.x) < -0.01f * R || Mathf.Max(a.x, b.x, c.x) > 1.01f * R ||
                    Mathf.Min(a.y, b.y, c.y) < -0.01f * R || Mathf.Max(a.y, b.y, c.y) > 1.01f * R) { outside++; continue; }
                float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                if (Mathf.Abs(area) < 1e-12f) continue;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x))), x1 = Mathf.Min(R - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x)));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y))), y1 = Mathf.Min(R - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y)));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = ((b.x - p.x) * (c.y - p.y) - (c.x - p.x) * (b.y - p.y)) / area;
                        float w1 = ((c.x - p.x) * (a.y - p.y) - (a.x - p.x) * (c.y - p.y)) / area;
                        float w2 = 1f - w0 - w1;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                        Vector3 q = verts[i0] * w0 + verts[i1] * w1 + verts[i2] * w2;
                        px[y * R + x] = new Color(q.x, q.y, q.z, 1f);
                    }
            }
            // ponytail: 3 texels of island padding; raise it if mipmapped paint shows seams far away
            for (int pass = 0; pass < 3; pass++)
            {
                var src = (Color[])px.Clone();
                for (int y = 0; y < R; y++)
                    for (int x = 0; x < R; x++)
                    {
                        if (src[y * R + x].a > 0.5f) continue;
                        Color sum = default; int n = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int xx = x + dx, yy = y + dy;
                                if (xx < 0 || yy < 0 || xx >= R || yy >= R) continue;
                                Color s = src[yy * R + xx];
                                if (s.a > 0.5f) { sum += s; n++; }
                            }
                        if (n > 0) px[y * R + x] = new Color(sum.r / n, sum.g / n, sum.b / n, 1f);
                    }
            }
            return px;
        }
#endif
    }
}
