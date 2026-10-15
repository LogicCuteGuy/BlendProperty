using UnityEditor;
using UnityEngine;
using Shape = BlendProperty.ArrayModifier.Shape;

namespace BlendProperty.Editor
{
    /// <summary>Blender-style Array panel: only the options of the chosen Shape are shown.</summary>
    [CustomEditor(typeof(ArrayModifier)), CanEditMultipleObjects]
    class ArrayModifierEditor : UnityEditor.Editor
    {
        static bool showRandom;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var shapeProp = P("shape");
            EditorGUILayout.PropertyField(shapeProp);
            var shape = (Shape)shapeProp.enumValueIndex;
            bool mixed = shapeProp.hasMultipleDifferentValues;

            if (mixed || shape == Shape.Line)
            {
                var method = P("offsetMethod");
                EditorGUILayout.PropertyField(method, new GUIContent("Offset Method"));
                EditorGUILayout.PropertyField(P("count"));
                var m = (ArrayModifier.OffsetMethod)method.enumValueIndex;
                if (m == ArrayModifier.OffsetMethod.Relative) EditorGUILayout.PropertyField(P("relativeOffset"), new GUIContent("Offset"));
                else EditorGUILayout.PropertyField(P("translation"), new GUIContent(m == ArrayModifier.OffsetMethod.Endpoint ? "End Point" : "Offset"));
                EditorGUILayout.PropertyField(P("rotation"));
                EditorGUILayout.PropertyField(P("scale"));
            }
            if (mixed || shape == Shape.Circle || shape == Shape.Curve)
            {
                if (shape == Shape.Curve)
                {
#if BLEND_SPLINES
                    EditorGUILayout.PropertyField(P("curveSpline"), new GUIContent("Spline"));
#endif
                    EditorGUILayout.PropertyField(P("curvePoints"), new GUIContent("Curve Points"), true);
                    EditorGUILayout.PropertyField(P("closedCurve"), new GUIContent("Closed"));
                }
                var cm = P("countMethod");
                EditorGUILayout.PropertyField(cm, new GUIContent("Count Method"));
                if (cm.enumValueIndex == (int)ArrayModifier.CountMethod.Count) EditorGUILayout.PropertyField(P("count"));
                else EditorGUILayout.PropertyField(shape == Shape.Circle ? P("angularDistance") : P("distance"));
                if (shape == Shape.Circle)
                {
                    EditorGUILayout.PropertyField(P("centralAxis"));
                    var seg = P("circleSegment");
                    EditorGUILayout.PropertyField(seg);
                    EditorGUILayout.PropertyField(P("radius"));
                    if (seg.enumValueIndex == (int)ArrayModifier.CircleSegment.Arc) EditorGUILayout.PropertyField(P("sweepAngle"));
                }
                EditorGUILayout.PropertyField(P("alignRotation"));
                EditorGUILayout.PropertyField(P("localRotation"));
            }
            if (mixed || shape == Shape.Transform)
            {
                var reference = P("transformReference");
                EditorGUILayout.PropertyField(reference, new GUIContent("Transform Reference"));
                EditorGUILayout.PropertyField(P("count"));
                if (reference.enumValueIndex == (int)ArrayModifier.TransformReference.Input)
                {
                    EditorGUILayout.PropertyField(P("translation"));
                    EditorGUILayout.PropertyField(P("rotation"));
                    EditorGUILayout.PropertyField(P("scale"));
                }
                else
                {
                    EditorGUILayout.PropertyField(P("transformObject"), new GUIContent("Transform Object"));
                    EditorGUILayout.PropertyField(P("relativeSpace"));
                }
            }

            showRandom = EditorGUILayout.Foldout(showRandom, "Randomize", true);
            if (showRandom)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(P("randomOffset"), new GUIContent("Offset"));
                    EditorGUILayout.PropertyField(P("randomRotation"), new GUIContent("Rotation"));
                    EditorGUILayout.PropertyField(P("randomScaleAxes"), new GUIContent("Scale Axes"));
                    var scale = P("randomScale");
                    if (P("randomScaleAxes").enumValueIndex == (int)ArrayModifier.ScaleAxes.Uniform)
                    {
                        EditorGUI.BeginChangeCheck();
                        float s = EditorGUILayout.FloatField("Scale", scale.vector3Value.x);
                        if (EditorGUI.EndChangeCheck()) scale.vector3Value = new Vector3(s, s, s);
                    }
                    else EditorGUILayout.PropertyField(scale, new GUIContent("Scale"));
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel("Flipping");
                        Toggle("flipX", "X"); Toggle("flipY", "Y"); Toggle("flipZ", "Z");
                    }
                    EditorGUILayout.PropertyField(P("excludeFirst"));
                    EditorGUILayout.PropertyField(P("excludeLast"));
                    EditorGUILayout.PropertyField(P("seed"));
                }
            }

            EditorGUILayout.PropertyField(P("splitCopies"));
            var merge = P("merge");
            EditorGUILayout.PropertyField(merge);
            if (merge.boolValue) EditorGUILayout.PropertyField(P("mergeDistance"), new GUIContent("Distance"));
            var cpc = P("colliderPerCopy");
            EditorGUILayout.PropertyField(cpc);
            if (cpc.boolValue) EditorGUILayout.PropertyField(P("colliderMaterial"), new GUIContent("Material"));
            serializedObject.ApplyModifiedProperties();
        }

        SerializedProperty P(string name) => serializedObject.FindProperty(name);

        void Toggle(string prop, string label)
        {
            var p = P(prop);
            p.boolValue = GUILayout.Toggle(p.boolValue, label, "Button", GUILayout.Width(28));
        }
    }
}
