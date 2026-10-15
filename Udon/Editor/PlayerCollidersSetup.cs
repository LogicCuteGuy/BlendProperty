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
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BlendProperty.Udon.Editor
{
    /// <summary>Keeps every Player Colliders' Body Template and Simulations list filled in, whenever the scene changes.</summary>
    static class PlayerCollidersSetup
    {
        static readonly string[] SphereNames = { "Head", "Chest", "Hips", "Hand L", "Hand R", "Foot L", "Foot R" };
        static readonly string[] CapsuleNames = { "Upper Arm L", "Upper Arm R", "Lower Arm L", "Lower Arm R", "Upper Leg L", "Upper Leg R", "Lower Leg L", "Lower Leg R" };
        static readonly string[] ColliderLists = { "sphereColliders", "capsuleColliders", "colliders", "effectors", "brushes" };
        static readonly string[] BrushParts = { "Hand L", "Hand R", "Foot L", "Foot R" };

        [InitializeOnLoadMethod]
        static void Hook() => EditorApplication.hierarchyChanged += Update;

        [MenuItem("GameObject/Blend Property/Physics/Player Colliders", false, 10)]
        static void Create()
        {
            var go = new GameObject("Player Colliders");
            Undo.RegisterCreatedObjectUndo(go, "Player Colliders");
            UdonSharpUndo.AddComponent(go, typeof(BlendUdonPlayerColliders));
            Update();
            Selection.activeGameObject = go;
        }

        static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var all = Object.FindObjectsByType<BlendUdonPlayerColliders>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length == 0) return;
            // every Blend Udon simulation with a collider list
            var sims = Object.FindObjectsByType<UdonSharpBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(u => !(u is BlendUdonPlayerColliders) && ColliderLists.Any(n => u.GetType().GetField(n) != null))
                .OrderBy(u => GlobalObjectId.GetGlobalObjectIdSlow(u).ToString()).ToArray();
            foreach (var p in all)
            {
                bool changed = false;
                if (!p.template)
                {
                    p.template = Template(p.transform);
                    changed = true;
                }
                AddBrushes(p.template.transform);
                var fractures = Object.FindObjectsByType<BlendUdonFracture>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .OrderBy(u => GlobalObjectId.GetGlobalObjectIdSlow(u).ToString()).ToArray();
                if (p.fractures == null || !p.fractures.SequenceEqual(fractures))
                {
                    p.fractures = fractures;
                    changed = true;
                }
                var lists = sims.Select(Lists).ToArray();
                if (p.simulations == null || p.simulationLists == null || !p.simulations.SequenceEqual(sims) || !p.simulationLists.SequenceEqual(lists))
                {
                    p.simulations = sims;
                    p.simulationLists = lists;
                    changed = true;
                }
                if (!changed) continue;
                EditorUtility.SetDirty(p);
                UdonSharpEditorUtility.CopyProxyToUdon(p);
            }
        }

        // bit per collider list the simulation has, in ColliderLists order
        static int Lists(UdonSharpBehaviour u)
        {
            int bits = 0;
            for (int i = 0; i < ColliderLists.Length; i++)
                if (u.GetType().GetField(ColliderLists[i]) != null) bits |= 1 << i;
            return bits;
        }

                static GameObject Template(Transform parent)
        {
            var body = new GameObject("Body Template") { layer = 2 };
            Undo.RegisterCreatedObjectUndo(body, "Player Colliders");
            body.transform.SetParent(parent, false);
            body.SetActive(false); // copied per player at runtime
            foreach (var n in SphereNames) Part<SphereCollider>(body.transform, n);
            foreach (var n in CapsuleNames) Part<CapsuleCollider>(body.transform, n).direction = 1;
            return body;
        }

        // hands and feet paint Dynamic Paint canvases and push Dynamic Waves
        static void AddBrushes(Transform template)
        {
            foreach (var n in BrushParts)
            {
                var part = template.Find(n);
                if (!part || part.GetComponent<BlendUdonPaintBrush>()) continue;
                var b = (BlendUdonPaintBrush)UdonSharpUndo.AddComponent(part.gameObject, typeof(BlendUdonPaintBrush));
                b.radius = n.StartsWith("Hand") ? 0.06f : 0.08f;
                b.color = new Color(0.2f, 0.5f, 1f);
                EditorUtility.SetDirty(b);
                UdonSharpEditorUtility.CopyProxyToUdon(b);
            }
        }

        static T Part<T>(Transform parent, string name) where T : Collider
        {
            var go = new GameObject(name) { layer = 2 }; // Ignore Raycast: hands don't block the laser
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<T>();
            c.isTrigger = true; // never pushes players or rigid bodies
            c.enabled = false; // until a player's bones place it
            return c;
        }
    }
}
