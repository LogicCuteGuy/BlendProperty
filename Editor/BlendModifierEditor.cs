using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BlendProperty.Editor
{
    /// <summary>Editor glue: asset persistence, change tracking, Blender-style Apply commands, build stripping.</summary>
    [InitializeOnLoad]
    public static class BlendModifierCommands
    {
        public const string BakeFolder = "Assets/BlendProperty/Baked";

        /// <summary>Set by the ProBuilder bridge: writes <paramref name="result"/> into a ProBuilderMesh. Returns true if handled.</summary>
        public static Func<BlendModifierStack, Mesh, bool> ApplyToProBuilder;
        /// <summary>Set by the ProBuilder bridge: removes ProBuilder components before the build processor runs.</summary>
        public static Action<GameObject> StripProBuilder;

        static BlendModifierCommands()
        {
            BlendModifierStack.PersistOutput = Persist;
            ObjectChangeEvents.changesPublished += OnChanges;
            Undo.undoRedoPerformed += () => { foreach (var s in AllStacks()) s.MarkDirty(); };
            EditorSceneManager.sceneSaved += _ => AssetDatabase.SaveAssets();
        }

        static IEnumerable<BlendModifierStack> AllStacks() =>
            Object.FindObjectsByType<BlendModifierStack>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        // ------------------------------------------------------------------ persistence

        static Mesh Persist(BlendModifierStack stack, Mesh mesh)
        {
            if (EditorUtility.IsPersistent(mesh)) { EditorUtility.SetDirty(mesh); return mesh; }
            return SaveNew(mesh, mesh.name);
        }

        public static Mesh SaveNew(Mesh mesh, string baseName)
        {
            Directory.CreateDirectory(BakeFolder);
            string safe = string.Join("_", baseName.Split(Path.GetInvalidFileNameChars()));
            string path = AssetDatabase.GenerateUniqueAssetPath($"{BakeFolder}/{safe}.asset");
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // ------------------------------------------------------------------ change tracking

        static void OnChanges(ref ObjectChangeEventStream stream)
        {
            var changed = new HashSet<GameObject>();
            for (int i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var s);
                        Add(changed, s.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var p);
                        Add(changed, p.instanceId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var pa);
                        Add(changed, pa.instanceId);
                        break;
                }
            }
            if (changed.Count == 0) return;

            foreach (var stack in AllStacks())
            {
                if (changed.Contains(stack.gameObject)) { stack.MarkDirty(); continue; } // reorder / add / remove
                foreach (var m in stack.Modifiers())
                    if (m.Dependencies().Any(d => d && changed.Contains(GameObjectOf(d)))) { stack.MarkDirty(); break; }
            }
        }

        static void Add(HashSet<GameObject> set, int id)
        {
            var go = GameObjectOf(EditorUtility.InstanceIDToObject(id));
            if (go) set.Add(go);
        }

        static GameObject GameObjectOf(Object o) => o switch
        {
            GameObject g => g,
            Component c => c ? c.gameObject : null,
            _ => null,
        };

        // ------------------------------------------------------------------ Apply (Blender Ctrl+A)

        /// <summary>
        /// Bake the source through every enabled modifier up to and including <paramref name="target"/>,
        /// make that the new source, and remove those modifiers. Disabled modifiers above it stay.
        /// </summary>
        public static void Apply(BlendModifier target)
        {
            var stack = target.GetComponent<BlendModifierStack>();
            var mods = stack.Modifiers();
            int idx = mods.IndexOf(target);
            var data = stack.Evaluate(idx);
            var result = stack.BuildMesh(data, $"{stack.name}_applied");

            Undo.RegisterCompleteObjectUndo(stack, "Apply Modifier");
            bool proBuilder = ApplyToProBuilder != null && ApplyToProBuilder(stack, result);
            if (!proBuilder) stack.source = SaveNew(result, result.name);
            else Object.DestroyImmediate(result);

            for (int i = 0; i <= idx; i++)
                if (mods[i].enabled || mods[i] == target) Undo.DestroyObjectImmediate(mods[i]);

            if (stack.Modifiers().Count == 0) Finish(stack);
            else stack.Rebuild(true);
        }

        public static void ApplyAll(BlendModifierStack stack)
        {
            var mods = stack.Modifiers();
            if (mods.Count == 0) { Finish(stack); return; }
            Apply(mods[mods.Count - 1]);
            // Disabled leftovers (Blender drops nothing, but an all-applied object has no stack): remove them too.
            foreach (var m in stack ? stack.Modifiers() : new List<BlendModifier>()) Undo.DestroyObjectImmediate(m);
            if (stack) Finish(stack);
        }

        /// <summary>Drop every modifier without applying: the object goes back to its original mesh. One undo step.</summary>
        public static void RemoveAll(BlendModifierStack stack)
        {
            Undo.SetCurrentGroupName("Remove All Modifiers");
            int group = Undo.GetCurrentGroup();
            foreach (var m in stack.Modifiers()) Undo.DestroyObjectImmediate(m);
            Finish(stack);
            Undo.CollapseUndoOperations(group);
        }

        /// <summary>Remove the (empty) stack, leaving the source mesh on the filter.</summary>
        static void Finish(BlendModifierStack stack)
        {
            var filter = stack.Filter;
            Undo.RecordObject(filter, "Apply Modifier");
            filter.sharedMesh = stack.source;
            if (stack.TryGetComponent(out MeshCollider col) && (col.sharedMesh == stack.Output || !col.sharedMesh))
            {
                Undo.RecordObject(col, "Apply Modifier");
                col.sharedMesh = stack.source;
            }
            Undo.DestroyObjectImmediate(stack);
        }

        [MenuItem("CONTEXT/BlendModifier/Apply", priority = 0)]
        static void ApplyMenu(MenuCommand c) => Apply((BlendModifier)c.context);

        [MenuItem("CONTEXT/BlendModifier/Apply All", priority = 1)]
        static void ApplyAllMenu(MenuCommand c) => ApplyAll(((BlendModifier)c.context).GetComponent<BlendModifierStack>());

        [MenuItem("CONTEXT/BlendModifierStack/Apply All", priority = 0)]
        static void ApplyAllStackMenu(MenuCommand c) => ApplyAll((BlendModifierStack)c.context);

        [MenuItem("CONTEXT/BlendModifier/Remove All Modifiers", priority = 2)]
        static void RemoveAllMenu(MenuCommand c) => RemoveAll(((BlendModifier)c.context).GetComponent<BlendModifierStack>());

        [MenuItem("CONTEXT/BlendModifierStack/Remove All Modifiers", priority = 1)]
        static void RemoveAllStackMenu(MenuCommand c) => RemoveAll((BlendModifierStack)c.context);

        [MenuItem("CONTEXT/BlendModifier/Duplicate", priority = 20)]
        static void Duplicate(MenuCommand c)
        {
            var m = (BlendModifier)c.context;
            ComponentUtility.CopyComponent(m);
            ComponentUtility.PasteComponentAsNew(m.gameObject);
            var mods = m.GetComponent<BlendModifierStack>().Modifiers();
            var copy = mods[mods.Count - 1];
            for (int i = mods.Count - 1; i > mods.IndexOf(m) + 1; i--) ComponentUtility.MoveComponentUp(copy);
        }

        [MenuItem("CONTEXT/BlendModifier/Copy to Selected", priority = 21)]
        static void CopyToSelected(MenuCommand c)
        {
            var m = (BlendModifier)c.context;
            ComponentUtility.CopyComponent(m);
            foreach (var go in Selection.gameObjects)
                if (go != m.gameObject && go.GetComponent<MeshFilter>())
                    ComponentUtility.PasteComponentAsNew(go);
        }

        [MenuItem("CONTEXT/BlendModifier/Move to First", priority = 40)]
        static void MoveFirst(MenuCommand c)
        {
            var m = (BlendModifier)c.context;
            while (m.GetComponent<BlendModifierStack>().Modifiers()[0] != m && ComponentUtility.MoveComponentUp(m)) { }
        }

        [MenuItem("CONTEXT/BlendModifier/Move to Last", priority = 41)]
        static void MoveLast(MenuCommand c)
        {
            var m = (BlendModifier)c.context;
            while (ComponentUtility.MoveComponentDown(m)) { }
        }

        [MenuItem("GameObject/Blend Modifier/Apply All in Children", priority = 0)]
        static void ApplyAllInChildren()
        {
            foreach (var root in Selection.gameObjects)
                foreach (var s in root.GetComponentsInChildren<BlendModifierStack>(true))
                    ApplyAll(s);
        }

        [MenuItem("GameObject/Blend Modifier/Apply All in Children", true)]
        static bool ApplyAllInChildrenValidate() => Selection.gameObjects.Length > 0;

        [MenuItem("GameObject/Blend Modifier/Remove All in Children", priority = 1)]
        static void RemoveAllInChildren()
        {
            Undo.SetCurrentGroupName("Remove All Modifiers");
            int group = Undo.GetCurrentGroup();
            foreach (var root in Selection.gameObjects)
                foreach (var s in root.GetComponentsInChildren<BlendModifierStack>(true))
                    RemoveAll(s);
            Undo.CollapseUndoOperations(group);
        }

        [MenuItem("GameObject/Blend Modifier/Remove All in Children", true)]
        static bool RemoveAllInChildrenValidate() => Selection.gameObjects.Length > 0;

        // ------------------------------------------------------------------ cleanup

        /// <summary>
        /// Delete meshes in the bake folder that no saved scene or prefab, and nothing in the open scenes, uses.
        /// They pile up when stacks are removed or objects deleted.
        /// </summary>
        [MenuItem("Tools/Blend Modifier/Delete Unused Baked Meshes")]
        static void DeleteUnusedBaked()
        {
            if (!AssetDatabase.IsValidFolder(BakeFolder)) { EditorUtility.DisplayDialog("Blend Modifier", "No baked meshes yet.", "OK"); return; }
            var unused = UnusedBaked();
            if (unused.Count == 0) { EditorUtility.DisplayDialog("Blend Modifier", "Every baked mesh is in use.", "OK"); return; }
            var list = string.Join("\n", unused.Take(15).Select(Path.GetFileName)) + (unused.Count > 15 ? $"\n… and {unused.Count - 15} more" : "");
            if (!EditorUtility.DisplayDialog("Delete unused baked meshes?",
                    $"{unused.Count} mesh(es) in {BakeFolder} aren't used by any scene, prefab or open object:\n\n{list}\n\nThis can't be undone.",
                    "Delete", "Cancel")) return;
            var failed = new List<string>();
            AssetDatabase.DeleteAssets(unused.ToArray(), failed);
            if (failed.Count > 0) Debug.LogWarning("Blend Modifier: couldn't delete " + string.Join(", ", failed));
        }

        public static List<string> UnusedBaked()
        {
            var used = new HashSet<string>();
            var roots = AssetDatabase.FindAssets("t:Scene t:Prefab").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            used.UnionWith(AssetDatabase.GetDependencies(roots, false));
            // open scenes may hold unsaved references
            void Use(Object o) { if (o) used.Add(AssetDatabase.GetAssetPath(o)); }
            foreach (var f in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Use(f.sharedMesh);
            foreach (var c in Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Use(c.sharedMesh);
            foreach (var s in AllStacks()) { Use(s.source); Use(s.Output); }
            return AssetDatabase.FindAssets("t:Mesh", new[] { BakeFolder }).Select(AssetDatabase.GUIDToAssetPath)
                .Distinct().Where(p => !used.Contains(p)).ToList();
        }

        // ------------------------------------------------------------------ build: ship baked mesh, strip components

        class StripOnBuild : UnityEditor.Build.IProcessSceneWithReport
        {
            // Run before ProBuilder / VRChat processors so they never see our components.
            public int callbackOrder => -10000;

            public void OnProcessScene(UnityEngine.SceneManagement.Scene scene, UnityEditor.Build.Reporting.BuildReport report)
            {
                var keptLattices = new HashSet<BlendLattice>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var stack in root.GetComponentsInChildren<BlendModifierStack>(true))
                    {
                        var mesh = stack.Modifiers().Count > 0 && stack.Output ? stack.Output : stack.source;
                        StripProBuilder?.Invoke(stack.gameObject);
                        var filter = stack.GetComponent<MeshFilter>();
                        if (filter) filter.sharedMesh = mesh;
                        if (stack.TryGetComponent(out MeshCollider col) && (!col.sharedMesh || col.sharedMesh == stack.source || col.sharedMesh == stack.Output))
                            col.sharedMesh = mesh;
#if !BLEND_VRC // VRChat can't run these components: always strip there
                        if (stack.keepAtRuntime)
                        {
                            if (stack.source && !stack.source.isReadable)
                                Debug.LogWarning($"Blend Property: '{stack.name}' keeps its stack at runtime, but source mesh '{stack.source.name}' " +
                                                 "has Read/Write disabled. RebuildNow() will fail in the build.", stack);
                            foreach (var lm in stack.GetComponents<LatticeModifier>()) if (lm.lattice) keptLattices.Add(lm.lattice);
                            continue;
                        }
#endif
                        foreach (var m in stack.Modifiers()) Object.DestroyImmediate(m);
                        Object.DestroyImmediate(stack);
                    }
                }
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var l in root.GetComponentsInChildren<BlendLattice>(true))
                        if (!keptLattices.Contains(l)) Object.DestroyImmediate(l);
            }
        }
    }

    [CustomEditor(typeof(BlendModifierStack))]
    class BlendModifierStackInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var stack = (BlendModifierStack)target;
#if BLEND_VRC
            if (stack.keepAtRuntime)
                EditorGUILayout.HelpBox("VRChat can't run this stack: it is still stripped from world builds. " +
                                        "For runtime motion use the Blend Udon deformers (Wave, Simple Deform, Lattice).", MessageType.Warning);
#endif
            var o = stack.Output;
            int count = stack.Modifiers().Count;
            if (count == 0)
            {
                EditorGUILayout.HelpBox("No modifiers left. Remove the stack to go back to the plain mesh.", MessageType.Info);
                if (GUILayout.Button("Remove Stack")) { BlendModifierCommands.RemoveAll(stack); GUIUtility.ExitGUI(); }
                return;
            }
            if (!stack.enabled)
                EditorGUILayout.HelpBox("Stack is off: showing the original mesh.", MessageType.None);
            else if (o)
                EditorGUILayout.HelpBox($"Result: {o.vertexCount} verts, {Tris(o)} tris  (source: {(stack.source ? stack.source.vertexCount : 0)} verts)", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Apply All", "Bake every modifier into the mesh and remove them."))) { BlendModifierCommands.ApplyAll(stack); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(new GUIContent("Remove All", "Drop every modifier without applying: back to the original mesh."))) { BlendModifierCommands.RemoveAll(stack); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("Rebuild")) stack.Rebuild(true);
            }
        }

        static long Tris(Mesh m)
        {
            long n = 0;
            for (int i = 0; i < m.subMeshCount; i++) n += m.GetIndexCount(i) / 3;
            return n;
        }
    }
}
