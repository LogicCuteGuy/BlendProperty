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
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BlendProperty.Udon.Editor
{
    /// <summary>
    /// Blend Udon Particles inspector: Preview plays the particles in the Scene view without entering Play mode (like
    /// Unity's Particle Effect panel), Bursts show as a table, and changing a baked setting rebakes by itself.
    /// </summary>
    [CustomEditor(typeof(BlendUdonParticles)), CanEditMultipleObjects]
    class BlendUdonParticlesEditor : BlendUdonEditor
    {
        static BlendUdonParticles previewing;
        static double last;

        public override void OnInspectorGUI()
        {
            var p = (BlendUdonParticles)target;
            if (!Application.isPlaying && targets.Length == 1) PreviewBar(p);
            string before = Baked();
            base.OnInspectorGUI();
            if (Application.isPlaying || Baked() == before) return;
            foreach (BlendUdonParticles t in targets)
                if (t.renderAs == BlendUdonRenderAs.Halo || t.instanceMesh) Bake(t);
            if (previewing && targets.Contains(previewing)) StartPreview(previewing);
        }

        // the settings that need a bake
        string Baked() => string.Join("|", targets.Cast<BlendUdonParticles>().Select(t =>
            $"{t.number} {t.emitFrom} {t.renderAs} {(t.instanceMesh ? t.instanceMesh.GetInstanceID() : 0)} {t.shape == BlendUdonParticleShape.Mesh}"));

        static void Bake(BlendUdonParticles t)
        {
            t.GetType().GetMethod("Bake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(t, null);
            UdonSharpEditorUtility.CopyProxyToUdon(t);
        }

        // ------------------------------------------------------------------ preview

        static void PreviewBar(BlendUdonParticles p)
        {
            bool on = previewing == p;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(on ? "■  Stop Preview" : "▶  Preview", GUILayout.Height(24f)))
                {
                    if (on) StopPreview(); else StartPreview(p);
                    GUIUtility.ExitGUI();
                }
                using (new EditorGUI.DisabledScope(!on))
                {
                    if (GUILayout.Button("Restart", GUILayout.Height(24f), GUILayout.Width(70f))) p.ResetSimulation();
                    if (GUILayout.Button(new GUIContent("Emit 30", "Emit(30): what a Burst does at runtime."), GUILayout.Height(24f), GUILayout.Width(70f))) p.Emit(30);
                }
            }
            if (!p.state || !p.particleRenderer)
                EditorGUILayout.HelpBox("Not baked yet: Preview or ⋮ → Bake Particles makes the particle renderer.", MessageType.Info);
        }

        static void StartPreview(BlendUdonParticles p)
        {
            StopPreview();
            if (!p.state || !p.particleRenderer) Bake(p);
            if (!p.state || !p.particleRenderer) return;
            p.Init(p.particleRenderer.sharedMaterial);
            previewing = p;
            last = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            if (!previewing || !previewing.state || !previewing.isActiveAndEnabled || EditorApplication.isPlayingOrWillChangePlaymode
                || !Selection.gameObjects.Contains(previewing.gameObject))
            {
                StopPreview();
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            previewing.Step((float)(now - last));
            last = now;
            EditorApplication.QueuePlayerLoopUpdate(); // runs the Custom Render Texture update while the editor is idle
            SceneView.RepaintAll();
        }

        static void StopPreview()
        {
            EditorApplication.update -= Tick;
            if (previewing && previewing.state && previewing.particleRenderer)
            {
                previewing.ResetSimulation(); // clear what's in the air
                previewing.Step(0f);
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
            previewing = null;
        }

        // ------------------------------------------------------------------ bursts

        protected override bool DrawField(SerializedProperty p)
        {
            if (p.name != "bursts" || targets.Length != 1) return false;
            EditorGUILayout.LabelField(new GUIContent("Bursts", "Particles emitted at once, at Time seconds into Duration. " +
                "Interval > 0 repeats it Cycles times (0 = until Duration ends)."), EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
                foreach (var h in new[] { "Time", "Count", "Cycles", "Interval" })
                    GUILayout.Label(h, EditorStyles.miniLabel, GUILayout.MinWidth(40f));
            int remove = -1;
            for (int i = 0; i < p.arraySize; i++)
            {
                var e = p.GetArrayElementAtIndex(i);
                Vector4 b = e.vector4Value;
                using (new EditorGUILayout.HorizontalScope())
                {
                    b.x = Mathf.Max(0f, EditorGUILayout.FloatField(b.x, GUILayout.MinWidth(40f)));
                    b.y = Mathf.Max(0, EditorGUILayout.IntField((int)b.y, GUILayout.MinWidth(40f)));
                    b.z = Mathf.Max(0, EditorGUILayout.IntField((int)b.z, GUILayout.MinWidth(40f)));
                    b.w = Mathf.Max(0f, EditorGUILayout.FloatField(b.w, GUILayout.MinWidth(40f)));
                    if (GUILayout.Button(new GUIContent("✕", "Remove"), EditorStyles.miniButton, GUILayout.Width(24f))) remove = i;
                }
                e.vector4Value = b;
            }
            if (remove >= 0) p.DeleteArrayElementAtIndex(remove);
            if (GUILayout.Button("＋  Add Burst", EditorStyles.miniButton))
            {
                p.arraySize++;
                p.GetArrayElementAtIndex(p.arraySize - 1).vector4Value = new Vector4(0f, 30f, 1f, 0.1f);
            }
            return true;
        }

        // ------------------------------------------------------------------ create

        [MenuItem("GameObject/Blend Property/Physics/Particle System", false, 10)]
        static void Create(MenuCommand command)
        {
            var go = new GameObject("Particle System");
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            if (!(command.context is GameObject) && SceneView.lastActiveSceneView) go.transform.position = SceneView.lastActiveSceneView.pivot;
            Undo.RegisterCreatedObjectUndo(go, "Particle System");
            var p = (BlendUdonParticles)UdonSharpUndo.AddComponent(go, typeof(BlendUdonParticles));
            // a small fountain of sparks that fade out
            p.shape = BlendUdonParticleShape.Cone;
            p.shapeRadius = 0.1f;
            p.number = 1000;
            p.lifetime = 2f;
            p.normal = 3f;
            p.speedRandom = 0.3f;
            p.size = 0.08f;
            p.sizeRandom = 0.5f;
            p.gravity = new Vector3(0f, -2f, 0f);
            p.useColorOverLifetime = true;
            p.colorOverLifetime = new Gradient();
            p.colorOverLifetime.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0f, 1f) });
            Bake(p);
            Selection.activeGameObject = go;
            StartPreview(p);
        }
    }
}
