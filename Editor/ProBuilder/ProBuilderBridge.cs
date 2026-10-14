using System.Collections.Generic;
using UnityEditor;
using UnityEditor.ProBuilder;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace BlendProperty.Editor
{
    /// <summary>
    /// Keeps modifier stacks live on ProBuilder meshes. ProBuilder rewrites the MeshFilter whenever the shape
    /// is edited, so the stack's source is ProBuilder's own mesh and every ProBuilder edit re-runs the stack.
    /// In vertex/edge/face mode the stack is hidden so handles match the cage (Blender: "show in edit mode" off).
    /// </summary>
    [InitializeOnLoad]
    static class ProBuilderBridge
    {
        static ProBuilderBridge()
        {
            BlendModifierStack.BeforeRebuild += BeforeRebuild;
            BlendModifierCommands.ApplyToProBuilder = Apply;
            BlendModifierCommands.StripProBuilder = Strip;
            ProBuilderEditor.afterMeshModification += Refresh;
            ProBuilderEditor.selectModeChanged += _ => RefreshAll();
            EditorMeshUtility.meshOptimized += (pb, _) => Refresh(new[] { pb });
            Undo.undoRedoPerformed += RefreshAll;
        }

        static bool InElementMode =>
            ProBuilderEditor.instance != null &&
            (ProBuilderEditor.selectMode & (SelectMode.Vertex | SelectMode.Edge | SelectMode.Face | SelectMode.TextureFace)) != 0;

        static void BeforeRebuild(BlendModifierStack stack)
        {
            if (!stack.TryGetComponent(out ProBuilderMesh pb)) return;
            // ProBuilder reassigns the filter to its own mesh on every edit/Awake: that mesh is the live source.
            var current = stack.Filter.sharedMesh;
            if (current && current != stack.Output && current != stack.source) stack.source = current;
            stack.showResult = !(InElementMode && Selection.Contains(stack.gameObject));
        }

        static void Refresh(IEnumerable<ProBuilderMesh> meshes)
        {
            foreach (var pb in meshes)
                if (pb && pb.TryGetComponent(out BlendModifierStack s))
                    s.Refresh(); // the source mesh changed in place, so the signature can't see it
        }

        static void RefreshAll()
        {
            foreach (var s in Object.FindObjectsByType<BlendModifierStack>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (s.GetComponent<ProBuilderMesh>()) s.Refresh();
        }

        /// <summary>Blender Apply on a ProBuilder object: geometry goes back into the ProBuilderMesh so it stays editable.</summary>
        static bool Apply(BlendModifierStack stack, Mesh result)
        {
            if (!stack.TryGetComponent(out ProBuilderMesh pb)) return false;
            Undo.RegisterCompleteObjectUndo(pb, "Apply Modifier");
            var mats = stack.GetComponent<MeshRenderer>() ? stack.GetComponent<MeshRenderer>().sharedMaterials : null;
            new MeshImporter(result, mats, pb).Import(new MeshImportSettings { quads = true, smoothing = true, smoothingAngle = 1f });
            pb.ToMesh();
            pb.Refresh();
            stack.source = stack.Filter.sharedMesh; // ProBuilder's (now updated) mesh
            return true;
        }

        /// <summary>Build only: remove ProBuilder components (keeping meshes) so ProBuilder's processor can't reset the filter.</summary>
        static void Strip(GameObject go)
        {
            if (!go.TryGetComponent(out ProBuilderMesh pb)) return;
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb && mb != pb && mb.GetType().Namespace != null && mb.GetType().Namespace.StartsWith("UnityEngine.ProBuilder"))
                    Object.DestroyImmediate(mb);
            pb.preserveMeshAssetOnDestroy = true;
            Object.DestroyImmediate(pb);
        }
    }
}
