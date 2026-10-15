#if !BLEND_UDON
using System;
using UnityEditor;
using UnityEngine;

namespace BlendProperty.Standalone.Editor
{
    static class NativeUndo
    {
        public static BlendBehaviour AddComponent(GameObject go, Type type)
        {
            return (BlendBehaviour)Undo.AddComponent(go, type);
        }
        public static void DestroyImmediate(BlendBehaviour component) { Undo.DestroyObjectImmediate(component); }
    }

    static class NativeEditorUtility
    {
        public static void CopyProxyToUdon(BlendBehaviour component) { EditorUtility.SetDirty(component); }
    }

    static class NativeGUI
    {
        public static bool DrawDefaultUdonSharpBehaviourHeader(UnityEngine.Object[] targets) { return false; }
    }
}
#endif
