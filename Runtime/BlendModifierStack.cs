using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace BlendProperty
{
    /// <summary>
    /// Holds the original mesh and the baked result for the modifier components on this GameObject.
    /// Added automatically by the first modifier. Editor-only: stripped from builds (the baked mesh ships).
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/ModifierStack.png")]
    [AddComponentMenu("Blend Modifier/Modifier Stack")]
    public sealed class BlendModifierStack : MonoBehaviour
#if BLEND_VRC
        , VRC.SDKBase.IEditorOnly
#endif
    {
        public enum ShadingMode { SmoothByAngle, Smooth, Flat, KeepImported }

        [Tooltip("Original mesh the stack starts from. Change it to rebase the stack.")]
        public Mesh source;
        [Tooltip("Shading of the result, like Blender's Shade Smooth / Flat / Auto Smooth. Ignored after a Normals modifier.")]
        public ShadingMode shading = ShadingMode.SmoothByAngle;
        [Range(0, 180)] public float smoothAngle = 30f;
        [Tooltip("Rebuild quads from triangle pairs before modifying (better Subdivision / Bevel / Solidify).")]
        public bool joinTriangles = true;
        [Tooltip("Keep this stack in non-VRChat builds so scripts can change modifiers and call RebuildNow(). " +
                 "The source mesh needs Read/Write enabled. VRChat builds always strip it.")]
        public bool keepAtRuntime;

        [SerializeField, HideInInspector] Mesh output;
        [NonSerialized] Mesh runtimeOutput;
        [SerializeField, HideInInspector] int signature;

        public Mesh Output => output;
        public MeshFilter Filter => GetComponent<MeshFilter>();

        /// <summary>When false the filter shows <see cref="source"/> (ProBuilder edit mode).</summary>
        [NonSerialized] public bool showResult = true;

        public float AutoSmoothAngle => shading switch
        {
            ShadingMode.Smooth => 180f,
            ShadingMode.Flat => 0f,
            _ => smoothAngle,
        };

        public List<BlendModifier> Modifiers()
        {
            var l = new List<BlendModifier>();
            GetComponents(l);
            return l;
        }

        /// <summary>Run source through modifiers [0..upTo] (enabled ones only).</summary>
        public MeshData Evaluate(int upTo = int.MaxValue)
        {
            var data = MeshData.FromMesh(source, joinTriangles);
            if (shading == ShadingMode.KeepImported) data.customNormals = true;
            var mods = Modifiers();
            for (int i = 0; i < mods.Count && i <= upTo; i++)
            {
                if (!mods[i].enabled) continue;
                try { mods[i].Apply(data); }
                catch (Exception e) { Debug.LogException(e, mods[i]); }
            }
            return data;
        }

        public Mesh BuildMesh(MeshData data, string name)
        {
            var m = new Mesh { name = name };
            data.ToMesh(m, AutoSmoothAngle);
            return m;
        }

        void OnEnable()
        {
            if (!source && Filter) source = Filter.sharedMesh;
            MarkDirty();
        }

        void OnValidate() => MarkDirty();

        // Unchecking the component = "don't use": show the original mesh. (Deactivating the GameObject,
        // reloading scripts or closing the scene also call OnDisable, but leave enabled == true.)
        void OnDisable()
        {
            if (!enabled) MarkDirty();
        }

        /// <summary>
        /// Re-evaluate the whole stack now. Works in builds (with <see cref="keepAtRuntime"/>) and play mode:
        /// change modifier fields from a script, then call this. The result is a scene-only mesh, never saved.
        /// ponytail: full re-evaluation every call, no prefix cache; cache like the editor path if this gets hot.
        /// </summary>
        public void RebuildNow()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) { Rebuild(true); return; }
#endif
            if (!source) return;
            var mesh = enabled && Modifiers().Count > 0 ? null : source;
            if (!mesh)
            {
                if (!runtimeOutput) runtimeOutput = new Mesh { name = $"{name}_blend_runtime" };
                Evaluate().ToMesh(runtimeOutput, AutoSmoothAngle);
                mesh = runtimeOutput;
            }
            Filter.sharedMesh = mesh;
            SetCollider(mesh, true);
        }

        void OnDestroy()
        {
            if (runtimeOutput) Destroy(runtimeOutput);
        }

        void SetCollider(Mesh m, bool contentChanged)
        {
            if (!TryGetComponent(out MeshCollider col)) return;
            if (col.sharedMesh == m)
            {
                // Same mesh object rewritten in place: the collider only re-cooks on assignment.
                if (contentChanged) { col.sharedMesh = null; col.sharedMesh = m; }
            }
            else if (col.sharedMesh == source || col.sharedMesh == output || col.sharedMesh == runtimeOutput || !col.sharedMesh)
                col.sharedMesh = m;
        }

#if UNITY_EDITOR
        [NonSerialized] double readyAt, lastCost;
        [NonSerialized] bool forceNext;
        [NonSerialized] MeshData prefixCache;
        [NonSerialized] int prefixKey;

        /// <summary>Hook the ProBuilder bridge (or others) can set to supply live source meshes.</summary>
        public static Action<BlendModifierStack> BeforeRebuild;
        /// <summary>Editor hook: saves the output mesh as an asset. Set by the editor assembly.</summary>
        public static Func<BlendModifierStack, Mesh, Mesh> PersistOutput;

        // One update pump for every stack. delayCall is not used: Unity drops the rest of the delayCall chain when
        // any subscriber (from any package) throws, which used to leave a stack waiting forever.
        static readonly HashSet<BlendModifierStack> pending = new HashSet<BlendModifierStack>();
        static bool pumping;

        public void MarkDirty()
        {
            pending.Add(this);
            if (pumping) return;
            pumping = true;
            EditorApplication.update += Pump;
        }

        /// <summary>Like <see cref="MarkDirty"/>, but rebuilds even if nothing it can see changed (source edited in place).</summary>
        public void Refresh()
        {
            forceNext = true;
            MarkDirty();
        }

        /// <summary>Rebuilds slower than this wait for the mouse to be released instead of stalling a drag.</summary>
        const double LiveBudget = 0.08;

        // ponytail: throttle so rebuilds take at most ~half the editor's time, and heavy stacks update on release.
        // Upgrade path: evaluate on a worker thread so heavy stacks can stay live too.
        static void Pump()
        {
            if (pending.Count == 0) return;
            bool dragging = GUIUtility.hotControl != 0;
            foreach (var s in new List<BlendModifierStack>(pending))
            {
                if (!s) { pending.Remove(s); continue; }
                double now = EditorApplication.timeSinceStartup;
                if (now < s.readyAt || (dragging && s.lastCost > LiveBudget)) continue;
                pending.Remove(s);
                bool force = s.forceNext; s.forceNext = false;
                try { s.Rebuild(force); }
                catch (Exception e) { Debug.LogException(e, s); }
                double end = EditorApplication.timeSinceStartup;
                if (end - now > 0.001) s.lastCost = end - now; // skipped (unchanged) rebuilds don't count
                s.readyAt = end + (end - now);
            }
        }

        /// <summary>Run every pending rebuild now (tests, tools).</summary>
        public static void FlushPending()
        {
            foreach (var s in new List<BlendModifierStack>(pending)) { pending.Remove(s); if (s) { s.Rebuild(s.forceNext); s.forceNext = false; } }
        }

        /// <summary>Re-evaluate the stack. Skips work when nothing changed since the last bake.</summary>
        public void Rebuild(bool force)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            BeforeRebuild?.Invoke(this);
            var filter = Filter;
            var mods = Modifiers();

            if (!showResult || !enabled || mods.Count == 0)
            {
                if (filter.sharedMesh != source) filter.sharedMesh = source;
                SetCollider(source, false);
                return;
            }

            int sig = Signature(mods, mods.Count);
            if (!force && sig == signature && output)
            {
                // unchanged: just show it again (e.g. after the component was re-checked)
                if (filter.sharedMesh != output) { filter.sharedMesh = output; SetCollider(output, false); }
                return;
            }

            var data = EvaluateCached(mods, force);
            if (!output || SharedWithAnotherStack())
                output = new Mesh { name = $"{name}_blend" };
            data.ToMesh(output, AutoSmoothAngle);
            output.name = $"{name}_blend";
            if (PersistOutput != null) output = PersistOutput(this, output);

            signature = sig;
            filter.sharedMesh = output;
            SetCollider(output, true);
            EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Like <see cref="Evaluate"/>, but reuses the result of the modifiers above the first one that follows
        /// another object (or above the last modifier), so dragging a cutter or tweaking the last modifier
        /// only re-runs the tail of the stack.
        /// </summary>
        MeshData EvaluateCached(List<BlendModifier> mods, bool force)
        {
            int split = mods.Count - 1;
            for (int i = 0; i < mods.Count; i++)
                if (mods[i].enabled && HasDependencies(mods[i])) { split = i; break; }

            int key = Signature(mods, split);
            if (force || prefixCache == null || key != prefixKey) { prefixCache = Evaluate(split - 1); prefixKey = key; }
            var data = prefixCache.Clone();
            for (int i = split; i < mods.Count; i++)
            {
                if (!mods[i].enabled) continue;
                try { mods[i].Apply(data); }
                catch (Exception e) { Debug.LogException(e, mods[i]); }
            }
            return data;
        }

        static bool HasDependencies(BlendModifier m)
        {
            foreach (var d in m.Dependencies()) if (d) return true;
            return false;
        }

        bool SharedWithAnotherStack()
        {
            foreach (var s in FindObjectsByType<BlendModifierStack>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (s != this && s.output == output) return true;
            return false;
        }

        /// <summary>Hash of the source, settings and the first <paramref name="count"/> modifiers.</summary>
        int Signature(List<BlendModifier> mods, int count)
        {
            var h = new HashCode();
            h.Add(source && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long fid) ? guid + fid : (object)(source ? source.GetInstanceID() : 0));
            h.Add(shading); h.Add(smoothAngle); h.Add(joinTriangles); h.Add(count);
            h.Add(transform.lossyScale); // modifier distances are in metres, so scaling the object changes the result
            for (int i = 0; i < count; i++)
            {
                var m = mods[i];
                h.Add(m.GetType());
                h.Add(m.enabled);
                h.Add(EditorJsonUtility.ToJson(m));
                foreach (var d in m.Dependencies())
                {
                    if (d is Transform t) h.Add(transform.worldToLocalMatrix * t.localToWorldMatrix);
                    else if (d is Component c) { h.Add(EditorJsonUtility.ToJson(c)); h.Add(transform.worldToLocalMatrix * c.transform.localToWorldMatrix); }
                    else if (d) h.Add(d.GetInstanceID());
                }
            }
            return h.ToHashCode();
        }
#else
        public void MarkDirty() { }
#endif
    }
}
