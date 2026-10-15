#if BLEND_UDON
using UdonSharp;
using UdonSharpEditor;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
using UdonSharpUndo = BlendProperty.Standalone.Editor.NativeUndo;
using UdonSharpEditorUtility = BlendProperty.Standalone.Editor.NativeEditorUtility;
using UdonSharpGUI = BlendProperty.Standalone.Editor.NativeGUI;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlendProperty.Udon.Editor
{
    /// <summary>
    /// Inspector for every Blend Udon component: [Header] groups become foldout cards, the Sphere / Capsule / Box collider
    /// lists show as one list (drop anything, it is sorted by shape), and [ContextMenu] commands such as Bake get buttons.
    /// The runtime keeps three lists because the GPU packs each shape differently, 8 of each at a time.
    /// </summary>
    class BlendUdonEditor : UnityEditor.Editor
    {
        static readonly string[] ColliderFields = { "sphereColliders", "capsuleColliders", "boxColliders" };
        static readonly Type[] ColliderTypes = { typeof(SphereCollider), typeof(CapsuleCollider), typeof(BoxCollider) };
        static readonly string[] ShapeNames = { "Sphere", "Capsule", "Box" };
        static readonly Color Accent = new Color(0.91f, 0.49f, 0.05f); // Blender orange
        static string version;
        static Color HeaderTint => EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.05f) : new Color(0f, 0f, 0f, 0.06f);

        static GUIStyle titleStyle, dropStyle;

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(targets)) return;
            titleStyle ??= new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            dropStyle ??= new GUIStyle(EditorStyles.helpBox) { alignment = TextAnchor.MiddleCenter, fontSize = 11 };

            serializedObject.Update();
            bool merged = targets.Length == 1 && ColliderFields.All(f => serializedObject.FindProperty(f) != null);
            foreach (var (title, props) in Groups())
            {
                bool open = BeginCard(title);
                if (open)
                    for (int i = 0; i < props.Count; i++)
                    {
                        var p = props[i];
                        if (merged && ColliderFields.Contains(p.name))
                        {
                            if (p.name == ColliderFields.First(f => props.Any(q => q.name == f))) DrawColliders();
                            continue;
                        }
                        if (DrawField(p)) continue;
                        Draw(p, i == 0 && Field(p.name)?.GetCustomAttribute<HeaderAttribute>() != null);
                    }
                EndCard();
            }
            if (serializedObject.ApplyModifiedProperties())
                foreach (var t in targets) UdonSharpEditorUtility.CopyProxyToUdon((UdonSharpBehaviour)t);

            DrawCommands();
            Footer();
        }

        /// <summary>Draw a field yourself (return true), or leave it to the default card layout.</summary>
        protected virtual bool DrawField(SerializedProperty p) => false;

        /// <summary>Small credit line under a Blend Property inspector.</summary>
        public static void Footer()
        {
            version ??= UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BlendUdonEditor).Assembly).version;
            GUILayout.Space(6);
            GUILayout.Label($"Blend Property v{version}  ·  by LogicCuteGuy", EditorStyles.centeredGreyMiniLabel);
        }

        // ------------------------------------------------------------------ groups

        List<(string title, List<SerializedProperty> props)> Groups()
        {
            var groups = new List<(string, List<SerializedProperty>)>();
            var it = serializedObject.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (it.propertyPath == "m_Script") continue;
                var header = Field(it.name)?.GetCustomAttribute<HeaderAttribute>();
                if (header != null || groups.Count == 0) groups.Add((header?.header ?? "Settings", new List<SerializedProperty>()));
                groups[groups.Count - 1].Item2.Add(it.Copy());
            }
            return groups;
        }

        FieldInfo Field(string name)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        bool BeginCard(string title)
        {
            string key = $"BlendUdonEditor.{target.GetType().Name}.{title}";
            bool open = SessionState.GetBool(key, true);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            Rect r = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, HeaderTint);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 3f, r.height), Accent);
            bool now = EditorGUI.Foldout(new Rect(r.x + 16f, r.y + 2f, r.width - 20f, 18f), open, title, true, titleStyle);
            if (now != open) SessionState.SetBool(key, now);
            if (now) GUILayout.Space(3);
            return now;
        }

        static void EndCard()
        {
            EditorGUILayout.EndVertical();
            GUILayout.Space(2);
        }

        /// <summary>The card title already shows the [Header], so it is clipped off the field.</summary>
        static void Draw(SerializedProperty p, bool hideHeader)
        {
            if (!hideHeader) { EditorGUILayout.PropertyField(p, true); return; }
            float hide = EditorGUIUtility.singleLineHeight * 1.5f; // HeaderDrawer's height
            float h = EditorGUI.GetPropertyHeight(p, true) - hide;
            Rect r = EditorGUILayout.GetControlRect(true, h);
            GUI.BeginGroup(r);
            EditorGUI.PropertyField(new Rect(0f, -hide, r.width, h + hide), p, true);
            GUI.EndGroup();
        }

        // ------------------------------------------------------------------ colliders

        void DrawColliders()
        {
            var arrays = ColliderFields.Select(serializedObject.FindProperty).ToArray();
            EditorGUILayout.LabelField(new GUIContent("Colliders", "Sphere, Capsule and Box colliders this bounces off. " +
                "Up to 8 of each shape near it are used at a time."), EditorStyles.boldLabel);

            int removeK = -1, removeI = -1, count = 0;
            for (int k = 0; k < arrays.Length; k++)
                for (int i = 0; i < arrays[k].arraySize; i++)
                {
                    count++;
                    var e = arrays[k].GetArrayElementAtIndex(i);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var icon = EditorGUIUtility.ObjectContent(null, ColliderTypes[k]).image;
                        GUILayout.Label(new GUIContent(" " + ShapeNames[k], icon), GUILayout.Width(78f), GUILayout.Height(18f));
                        e.objectReferenceValue = EditorGUILayout.ObjectField(e.objectReferenceValue, ColliderTypes[k], true);
                        if (GUILayout.Button(new GUIContent("✕", "Remove"), EditorStyles.miniButton, GUILayout.Width(24f))) { removeK = k; removeI = i; }
                    }
                }
            if (count == 0) EditorGUILayout.LabelField("None yet: drop colliders or objects below.", EditorStyles.centeredGreyMiniLabel);
            if (removeK >= 0)
            {
                arrays[removeK].GetArrayElementAtIndex(removeI).objectReferenceValue = null;
                arrays[removeK].DeleteArrayElementAtIndex(removeI);
            }

            GUILayout.Space(2);
            Rect drop = GUILayoutUtility.GetRect(0f, 32f, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "＋  Drop Sphere / Capsule / Box colliders or objects here", dropStyle);
            var ev = Event.current;
            if (drop.Contains(ev.mousePosition) && (ev.type == EventType.DragUpdated || ev.type == EventType.DragPerform))
            {
                var found = DragAndDrop.objectReferences.SelectMany(Supported).ToList();
                DragAndDrop.visualMode = found.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (ev.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var c in found) Add(arrays, c);
                }
                ev.Use();
            }
            var picked = EditorGUILayout.ObjectField(new GUIContent("Add", "Pick a Sphere, Capsule or Box collider."), null, typeof(Collider), true);
            if (picked)
            {
                if (Supported(picked).Any()) Add(arrays, (Collider)picked);
                else Debug.LogWarning($"[Blend Property] Only Sphere, Capsule and Box colliders work here, not {picked.GetType().Name}.", picked);
            }
        }

        static IEnumerable<Collider> Supported(Object o)
        {
            var all = o is GameObject go ? go.GetComponents<Collider>() : o is Collider c ? new[] { c } : Array.Empty<Collider>();
            return all.Where(c => Array.IndexOf(ColliderTypes, c.GetType()) >= 0);
        }

        static void Add(SerializedProperty[] arrays, Collider c)
        {
            var a = arrays[Array.IndexOf(ColliderTypes, c.GetType())];
            for (int i = 0; i < a.arraySize; i++) if (a.GetArrayElementAtIndex(i).objectReferenceValue == c) return;
            a.arraySize++;
            a.GetArrayElementAtIndex(a.arraySize - 1).objectReferenceValue = c;
        }

        // ------------------------------------------------------------------ commands

        void DrawCommands()
        {
            var commands = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(m => (m, a: m.GetCustomAttribute<ContextMenu>()))
                .Where(x => x.a != null && !x.a.validate && x.m.GetParameters().Length == 0).ToList();
            if (commands.Count == 0) return;
            GUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
                foreach (var (m, a) in commands)
                {
                    if (!GUILayout.Button(a.menuItem, GUILayout.Height(26f))) continue;
                    foreach (var t in targets)
                    {
                        m.Invoke(t, null);
                        UdonSharpEditorUtility.CopyProxyToUdon((UdonSharpBehaviour)t);
                    }
                    GUIUtility.ExitGUI();
                }
        }
    }

    [CustomEditor(typeof(BlendUdonDeformStack)), CanEditMultipleObjects] class BlendUdonDeformStackEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonExplode)), CanEditMultipleObjects] class BlendUdonExplodeEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonFracture)), CanEditMultipleObjects] class BlendUdonFractureEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonClothPanel)), CanEditMultipleObjects] class BlendUdonClothPanelEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonPanel)), CanEditMultipleObjects] class BlendUdonPanelEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonPlayerColliders)), CanEditMultipleObjects] class BlendUdonPlayerCollidersEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonResetObjects)), CanEditMultipleObjects] class BlendUdonResetObjectsEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonBuoyancy)), CanEditMultipleObjects] class BlendUdonBuoyancyEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonCloth)), CanEditMultipleObjects] class BlendUdonClothEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonDynamicPaint)), CanEditMultipleObjects] class BlendUdonDynamicPaintEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonDynamicWaves)), CanEditMultipleObjects] class BlendUdonDynamicWavesEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonFluid)), CanEditMultipleObjects] class BlendUdonFluidEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonFluidFlow)), CanEditMultipleObjects] class BlendUdonFluidFlowEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonForceField)), CanEditMultipleObjects] class BlendUdonForceFieldEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonHair)), CanEditMultipleObjects] class BlendUdonHairEditor : BlendUdonEditor { }
    [CustomEditor(typeof(BlendUdonPaintBrush)), CanEditMultipleObjects]
    class BlendUdonPaintBrushEditor : BlendUdonEditor
    {
        protected override bool DrawField(SerializedProperty p)
        {
            if (p.name == "strength")
            {
                EditorGUILayout.PropertyField(p, new GUIContent("Flow", "Paint per application; Airbrush applies this at a 60 Hz equivalent rate."));
                return true;
            }
            if (p.name != "softness") return false;
            EditorGUI.showMixedValue = p.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            float hardness = EditorGUILayout.Slider(new GUIContent("Hardness", "1 = hard edge, 0 = soft edge. Pen always uses a hard edge."), 1f - p.floatValue, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) p.floatValue = 1f - hardness;
            EditorGUI.showMixedValue = false;
            return true;
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            EditorGUILayout.HelpBox("Brush connects strokes. Pen has a hard edge and stops building up while still. Airbrush builds up over time. Stamp applies once per contact or new stroke. Enable Erase for any tool.\n\nAssign Tip Texture for a textured nib; Tip local Z projects the image. List this brush on a baked Dynamic Paint Canvas (a flat mesh or UV-mapped object). Use Require Use for a pickup trigger, or call _StartDrawing / _StopDrawing from UI.", MessageType.Info);
        }
    }
    [CustomEditor(typeof(BlendUdonCollision)), CanEditMultipleObjects] class BlendUdonCollisionEditor : BlendUdonEditor { }
}
