using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BlendProperty.Editor
{
    /// <summary>
    /// Scene-view icon for every Blend Property component, drawn by Unity's own gizmo icons, so it follows the Gizmos
    /// menu (3D Icons, icon size, Fade Gizmos) and a click selects the object. Several components on one object sit
    /// side by side in Inspector order. Physics components register in BlendPhysicsGizmos.
    /// </summary>
    [InitializeOnLoad]
    public static class BlendSceneIcons
    {
        const GizmoType Always = GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable;
        const float Gap = 1.1f; // icon widths between neighbours
        static readonly System.Type Annotation = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AnnotationUtility");

        static BlendSceneIcons()
        {
            // Unity also draws each script's [Icon] at the pivot, stacked under ours: turn those off once per project
            // (the user can turn them back on in the Gizmos menu)
            const string key = "BlendProperty.SceneIcons.UnityIconsOff";
            if (EditorPrefs.GetBool(key + Application.dataPath)) return;
            EditorApplication.delayCall += () =>
            {
                var set = Annotation?.GetMethod("SetIconEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (set == null) return;
                foreach (var t in TypeCache.GetTypesWithAttribute<IconAttribute>())
                    if (IsBlend(t)) set.Invoke(null, new object[] { 114, t.Name, 0 }); // 114 = MonoBehaviour
                EditorPrefs.SetBool(key + Application.dataPath, true);
            };
        }

        // one registration per type, so each gets its own row (and checkbox) in the Scene view's Gizmos menu
        [DrawGizmo(Always)] static void ArrayModifierIcon(ArrayModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void BevelModifierIcon(BevelModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void BooleanModifierIcon(BooleanModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void CastModifierIcon(CastModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void CurveModifierIcon(CurveModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void DecimateModifierIcon(DecimateModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void DisplaceModifierIcon(DisplaceModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void HookModifierIcon(HookModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void LaplacianSmoothModifierIcon(LaplacianSmoothModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void LatticeModifierIcon(LatticeModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void MirrorModifierIcon(MirrorModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void NormalEditModifierIcon(NormalEditModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void RemeshModifierIcon(RemeshModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void ScrewModifierIcon(ScrewModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void ShrinkwrapModifierIcon(ShrinkwrapModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void SimpleDeformModifierIcon(SimpleDeformModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void SmoothByAngleModifierIcon(SmoothByAngleModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void SmoothModifierIcon(SmoothModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void SolidifyModifierIcon(SolidifyModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void SubdivisionModifierIcon(SubdivisionModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void TriangulateModifierIcon(TriangulateModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void UVProjectModifierIcon(UVProjectModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void UVWarpModifierIcon(UVWarpModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void WarpModifierIcon(WarpModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void WaveModifierIcon(WaveModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void WeightedNormalModifierIcon(WeightedNormalModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void WeldModifierIcon(WeldModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void WireframeModifierIcon(WireframeModifier m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void BlendModifierStackIcon(BlendModifierStack m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void BlendLatticeIcon(BlendLattice m, GizmoType g) => Draw(m);
        [DrawGizmo(Always)] static void BlendPhysicsIcon(BlendPhysics m, GizmoType g) => Draw(m);

        static bool IsBlend(System.Type t) => t.Namespace != null && t.Namespace.StartsWith("BlendProperty") && System.Attribute.IsDefined(t, typeof(IconAttribute));

        static readonly MethodInfo getAnnotation = Annotation?.GetMethod("GetAnnotation", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static FieldInfo gizmoEnabled;

        /// <summary>The type's "gizmo" checkbox in the Scene view's Gizmos menu (the icon column stays off: Unity would stack its own icons).</summary>
        public static bool Shown(System.Type t)
        {
            if (getAnnotation == null) return true;
            var a = getAnnotation.Invoke(null, new object[] { 114, t.Name });
            gizmoEnabled ??= a.GetType().GetField("gizmoEnabled");
            return gizmoEnabled == null || (int)gizmoEnabled.GetValue(a) != 0;
        }

        public static void Draw(MonoBehaviour m)
        {
            var icon = (IconAttribute)System.Attribute.GetCustomAttribute(m.GetType(), typeof(IconAttribute));
            var cam = Camera.current;
            if (icon == null || !cam || !Shown(m.GetType())) return;
            int index = 0, count = 0;
            foreach (var c in m.GetComponents<MonoBehaviour>())
            {
                if (!c || !IsBlend(c.GetType()) || !Shown(c.GetType())) continue;
                if (c == m) index = count;
                count++;
            }
            Vector3 p = m.transform.position;
            if (count > 1) p += cam.transform.right * (IconWorldWidth(p, cam) * Gap * (index - (count - 1) * 0.5f));
            Gizmos.DrawIcon(p, icon.path, true, m.enabled ? Color.white : new Color(1f, 1f, 1f, 0.4f));
        }

        /// <summary>How wide Unity draws a gizmo icon at <paramref name="p"/>, in metres.</summary>
        static float IconWorldWidth(Vector3 p, Camera cam)
        {
            float depth = Mathf.Max(Vector3.Dot(p - cam.transform.position, cam.transform.forward), cam.nearClipPlane);
            float metresPerPixel = cam.orthographic ? 2f * cam.orthographicSize / cam.pixelHeight
                                                    : 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / cam.pixelHeight;
            float flat = 32f * metresPerPixel * EditorGUIUtility.pixelsPerPoint; // 2D icons: 32 points on screen
            bool use3d = (bool)(Annotation?.GetProperty("use3dGizmos", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? false);
            if (!use3d) return flat;
            // 3D icons: Icon Size is metres per texel (our icons are 64 px)
            float texel = (float)(Annotation.GetProperty("iconSize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) ?? 0f);
            return texel > 0f ? texel * 64f : flat;
        }
    }
}
