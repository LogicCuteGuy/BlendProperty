using UnityEditor;
using UnityEngine;

namespace BlendProperty.Editor
{
    [CustomEditor(typeof(BlendLattice))]
    class BlendLatticeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (GUILayout.Button("Reset Points"))
            {
                var l = (BlendLattice)target;
                Undo.RecordObject(l, "Reset Lattice");
                l.EnsurePoints();
                l.ResetPoints();
                EditorUtility.SetDirty(l);
            }
            EditorGUILayout.HelpBox("Drag the points in the Scene view. Scale/rotate this object to fit the mesh.", MessageType.None);
        }

        void OnSceneGUI()
        {
            var l = (BlendLattice)target;
            l.EnsurePoints();
            var m = l.transform.localToWorldMatrix;
            for (int i = 0; i < l.points.Length; i++)
            {
                var world = m.MultiplyPoint3x4(l.points[i]);
                float size = HandleUtility.GetHandleSize(world) * 0.06f;
                EditorGUI.BeginChangeCheck();
                var moved = Handles.FreeMoveHandle(world, size, Vector3.zero, Handles.DotHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(l, "Move Lattice Point");
                    l.points[i] = l.transform.InverseTransformPoint(moved);
                    EditorUtility.SetDirty(l);
                }
            }
        }
    }
}
