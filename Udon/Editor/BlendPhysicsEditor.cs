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
    /// <summary>Blender's Physics tab: one button per physics type, each adding / removing the Blend Udon component behind it.</summary>
    [CustomEditor(typeof(BlendPhysics))]
    class BlendPhysicsEditor : UnityEditor.Editor
    {
        enum PaintType { Canvas, Waves, Brush }
        enum FluidType { Gas, Liquid, Flow }
        enum BodyType { Active, Passive }
        enum ConstraintType { Fixed, Hinge, Point, Slider, Spring, Generic }

        static PaintType nextPaint;
        static FluidType nextFluid;
        static BodyType nextBody;
        static ConstraintType nextConstraint;

        BlendPhysics Phys => (BlendPhysics)target;
        GameObject Go => Phys.gameObject;
        T Get<T>() where T : Component => Go.GetComponent<T>();
        int VertexCount { get { var mf = Get<MeshFilter>(); return mf && mf.sharedMesh ? mf.sharedMesh.vertexCount : 0; } }

        public override void OnInspectorGUI()
        {
            Action pending = null;
            EditorGUILayout.HelpBox("Click a physics type to add or remove it, like Blender's Physics tab. Its settings are on the component it adds below.", MessageType.None);
            var scope = (BlendPhysics.LinkScope)EditorGUILayout.EnumPopup(new GUIContent("Scope", "What this object links with (Blender Effector Collection). Scene: everything. Parent: only objects under this object's parent."), Phys.scope);
            if (scope != Phys.scope) pending = () => { Undo.RecordObject(Phys, "Scope"); Phys.scope = scope; Relink(); };

            var ff = Get<BlendUdonForceField>();
            if (Row("Force Field", ff, true, out bool ffClick, ff ? (Enum)ff.type : null, out Enum ffType)) pending = () =>
            { Undo.RecordObject(ff, "Force Field Type"); ff.type = (BlendUdonForceType)ffType; Commit(ff); };
            if (ffClick) pending = () => { if (ff) RemoveLinked(ff); else { Add(typeof(BlendUdonForceField)); Relink(); } };

            bool col = Phys.collision;
            Row("Collision", col, true, out bool colClick);
            if (colClick) pending = () => { ToggleCollision(!col); };

            // mesh writers: one per mesh
            var cloth = Get<BlendUdonCloth>();
            var waves = Get<BlendUdonDynamicWaves>(); var explode = Get<BlendUdonExplode>();
            bool hasCloth = cloth;
            float shape = cloth ? cloth.shape : 0f;
            var stack = Get<BlendModifierStack>();
            bool liveModifiers = stack && stack.Modifiers().Exists(m => m.enabled && m.Runtime != BlendModifier.RuntimeMode.Off);
            bool writerFree = !(waves || explode || liveModifiers);

            Row("Cloth", hasCloth && shape <= 0f, writerFree, out bool clothClick);
            if (clothClick) pending = () => { ToggleCloth(hasCloth && shape <= 0f, 0f); };
            Row("Soft Body", hasCloth && shape > 0f, writerFree, out bool softClick);
            if (softClick) pending = () => { ToggleCloth(hasCloth && shape > 0f, 0.6f); };

            var paint = Get<BlendUdonDynamicPaint>(); var brush = Get<BlendUdonPaintBrush>();
            Component dp = paint ? paint : waves ? waves : (Component)brush;
            PaintType paintType = paint ? PaintType.Canvas : waves ? PaintType.Waves : brush ? PaintType.Brush : nextPaint;
            if (Row("Dynamic Paint", dp, writerFree || waves, out bool dpClick, paintType, out Enum dpType)) pending = () =>
            {
                nextPaint = (PaintType)dpType;
                if (dp) { RemoveLinked(dp); AddPaint(nextPaint); }
            };
            if (dpClick) pending = () => { if (dp) RemoveLinked(dp); else AddPaint(nextPaint); };

            var domain = Get<BlendUdonFluid>(); var flow = Get<BlendUdonFluidFlow>();
            FluidType fluidType = domain ? (domain.domainType == BlendUdonFluidType.Liquid ? FluidType.Liquid : FluidType.Gas) : flow ? FluidType.Flow : nextFluid;
            if (Row("Fluid", domain || flow, true, out bool fluidClick, fluidType, out Enum flType)) pending = () =>
            {
                nextFluid = (FluidType)flType;
                if (domain && nextFluid != FluidType.Flow) { Undo.RecordObject(domain, "Fluid Type"); domain.domainType = nextFluid == FluidType.Liquid ? BlendUdonFluidType.Liquid : BlendUdonFluidType.Gas; Bake(domain); }
                else if (domain || flow) { RemoveFluid(domain, flow); AddFluid(nextFluid); }
            };
            if (fluidClick) pending = () => { if (domain || flow) RemoveFluid(domain, flow); else AddFluid(nextFluid); };

            var rb = Get<Rigidbody>();
            BodyType bodyType = rb ? (rb.isKinematic ? BodyType.Passive : BodyType.Active) : nextBody;
            if (Row("Rigid Body", rb, true, out bool rbClick, bodyType, out Enum rbType)) pending = () =>
            {
                nextBody = (BodyType)rbType;
                Undo.RecordObject(rb, "Rigid Body Type"); rb.isKinematic = nextBody == BodyType.Passive; Relink(new HashSet<Object> { rb });
            };
            if (rbClick) pending = () => { ToggleRigidBody(rb); };

            var joint = Get<Joint>();
            ConstraintType cType = joint ? TypeOf(joint) : nextConstraint;
            if (Row("Rigid Body Constraint", joint, true, out bool jClick, cType, out Enum jType)) pending = () =>
            {
                nextConstraint = (ConstraintType)jType;
                var body = joint.connectedBody;
                Undo.DestroyObjectImmediate(joint);
                AddJoint(nextConstraint).connectedBody = body;
            };
            if (jClick) pending = () => { if (joint) Undo.DestroyObjectImmediate(joint); else AddJoint(nextConstraint); };

            var parts = Get<BlendUdonParticles>();
            if (Row("Particles", parts, true, out bool pClick, parts ? (Enum)parts.renderAs : null, out Enum pType)) pending = () =>
            {
                Undo.RecordObject(parts, "Render As"); parts.renderAs = (BlendUdonRenderAs)pType;
                if (parts.renderAs == BlendUdonRenderAs.Halo || parts.instanceMesh) Bake(parts); else Commit(parts);
            };
            if (parts && parts.renderAs == BlendUdonRenderAs.Object && !parts.instanceMesh)
                EditorGUILayout.HelpBox("Render As Object: set Instance Mesh on Blend Udon Particles, then ⋮ → Bake Particles.", MessageType.Info);
            if (pClick) pending = () => { if (parts) RemoveWithChild(parts, parts.particleRenderer); else { Bake(Add(typeof(BlendUdonParticles))); Relink(); } };

            var hair = Get<BlendUdonHair>();
            Row("Hair", hair, true, out bool hClick);
            if (hClick) pending = () => { if (hair) RemoveWithChild(hair, hair.hairRenderer); else { Bake(Add(typeof(BlendUdonHair))); Relink(); } };

            var buoy = Get<BlendUdonBuoyancy>();
            Row("Ocean", buoy, true, out bool oClick);
            if (buoy && VertexCount < 2000) EditorGUILayout.HelpBox("The Ocean needs a finely subdivided plane (thousands of vertices) to show its waves.", MessageType.Info);
            if (oClick) pending = () => { ToggleOcean(buoy); };

            Row("Explode", explode, writerFree || explode, out bool eClick);
            if (eClick) pending = () => { if (explode) { Invoke(explode, "Unbake"); UdonSharpUndo.DestroyImmediate(explode); } else Bake(Add(typeof(BlendUdonExplode))); };

            var fracture = Get<BlendUdonFracture>();
            Row("Cell Fracture", fracture, true, out bool cfClick);
            if (cfClick) pending = () =>
            {
                if (fracture) { Invoke(fracture, "Unbake"); UdonSharpUndo.DestroyImmediate(fracture); }
                else
                {
                    if (!Get<Collider>()) { var mc = Undo.AddComponent<MeshCollider>(Go); mc.convex = true; } // so hits can break it
                    Bake(Add(typeof(BlendUdonFracture)));
                }
            };

            if (!writerFree && !hasCloth && !waves && !explode)
                EditorGUILayout.HelpBox("A modifier with Runtime on (or another Blend Udon deformer) already moves this mesh, so Cloth, Soft Body, Waves and Explode are off.", MessageType.None);
            BlendUdonEditor.Footer();
            if (pending != null)
            {
                pending();
                GUIUtility.ExitGUI(); // the components changed under this layout
            }
        }

        /// <summary>Toggle button, plus a type popup when <paramref name="type"/> is given. Returns true when the popup changed.</summary>
        bool Row(string label, bool on, bool enabled, out bool clicked, Enum type, out Enum newType)
        {
            newType = type;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!enabled && !on))
                    clicked = GUILayout.Toggle(on, label, "Button", GUILayout.Width(EditorGUIUtility.currentViewWidth * 0.5f)) != on;
                if (type == null) return false;
                newType = EditorGUILayout.EnumPopup(type);
                return on && !Equals(newType, type);
            }
        }

        void Row(string label, bool on, bool enabled, out bool clicked) => Row(label, on, enabled, out clicked, null, out _);

        // ------------------------------------------------------------------ adding and removing

        UdonSharpBehaviour Add(Type t)
        {
            var u = UdonSharpUndo.AddComponent(Go, t);
            Commit(u);
            return u;
        }

        static void Commit(UdonSharpBehaviour u)
        {
            EditorUtility.SetDirty(u);
            UdonSharpEditorUtility.CopyProxyToUdon(u);
        }

        static void Invoke(UdonSharpBehaviour u, string method)
        {
            u.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(u, null);
        }

        static void Bake(UdonSharpBehaviour u)
        {
            Invoke(u, "Bake");
            Commit(u);
        }

        /// <summary>Remove a component, first taking it out of every list in the scene.</summary>
        void RemoveLinked(Component c)
        {
            Relink(new HashSet<Object> { c });
            if (c is UdonSharpBehaviour u) UdonSharpUndo.DestroyImmediate(u); else Undo.DestroyObjectImmediate(c);
        }

        void RemoveWithChild(UdonSharpBehaviour u, Renderer child)
        {
            if (child && child.transform.IsChildOf(Go.transform) && child.gameObject != Go) Undo.DestroyObjectImmediate(child.gameObject);
            RemoveLinked(u);
        }

        void ToggleCollision(bool on)
        {
            Undo.RecordObject(Phys, "Collision");
            if (!on)
            {
                Phys.collision = false;
                Relink(new HashSet<Object>(Go.GetComponents<Collider>()));
                return;
            }
            if (!Get<Collider>())
            {
                // the GPU simulations only collide with Sphere, Capsule and Box colliders: guess one from the name
                string n = (Get<MeshFilter>() && Get<MeshFilter>().sharedMesh ? Get<MeshFilter>().sharedMesh.name : Go.name).ToLowerInvariant();
                if (n.Contains("sphere") || n.Contains("ball")) Undo.AddComponent<SphereCollider>(Go);
                else if (n.Contains("capsule") || n.Contains("cylinder")) Undo.AddComponent<CapsuleCollider>(Go);
                else Undo.AddComponent<BoxCollider>(Go);
            }
            Phys.collision = true;
            Relink();
        }

        void ToggleCloth(bool remove, float shape)
        {
            var cloth = Get<BlendUdonCloth>();
            if (remove) { RemoveLinked(cloth); return; }
            if (cloth) { Undo.RecordObject(cloth, "Soft Body"); cloth.shape = shape; Bake(cloth); return; }
            var c = (BlendUdonCloth)Add(typeof(BlendUdonCloth));
            c.simulate = true; c.shape = shape;
            Bake(c);
            Relink();
        }

        void AddPaint(PaintType t)
        {
            if (t == PaintType.Brush) Add(typeof(BlendUdonPaintBrush));
            else Bake(Add(t == PaintType.Canvas ? typeof(BlendUdonDynamicPaint) : typeof(BlendUdonDynamicWaves)));
            Relink();
        }

        void AddFluid(FluidType t)
        {
            if (t == FluidType.Flow)
            {
                var f = (BlendUdonFluidFlow)Add(typeof(BlendUdonFluidFlow));
                var domains = Find<BlendUdonFluid>();
                if (domains.Count > 0 && domains.All(d => d.domainType == BlendUdonFluidType.Liquid)) { f.flowType = BlendUdonFluidFlowType.Liquid; Commit(f); }
            }
            else
            {
                SaveMaterials();
                var d = (BlendUdonFluid)Add(typeof(BlendUdonFluid));
                d.simulate = true;
                d.domainType = t == FluidType.Liquid ? BlendUdonFluidType.Liquid : BlendUdonFluidType.Gas;
                Bake(d);
            }
            Relink();
        }

        void RemoveFluid(BlendUdonFluid domain, BlendUdonFluidFlow flow)
        {
            if (domain) { RemoveLinked(domain); RestoreMaterials(); }
            if (flow) RemoveLinked(flow);
        }

        void ToggleRigidBody(Rigidbody rb)
        {
            if (rb)
            {
                Relink(new HashSet<Object> { rb });
                if (Get<Joint>()) Undo.DestroyObjectImmediate(Get<Joint>()); // a joint needs its Rigidbody
                Undo.DestroyObjectImmediate(rb);
                return;
            }
            var col = Get<Collider>();
            if (!col && Get<MeshFilter>()) col = Undo.AddComponent<MeshCollider>(Go);
            if (col is MeshCollider mc && nextBody == BodyType.Active) { Undo.RecordObject(mc, "Rigid Body"); mc.convex = true; }
            rb = Undo.AddComponent<Rigidbody>(Go);
            rb.isKinematic = nextBody == BodyType.Passive;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            Relink();
        }

        static ConstraintType TypeOf(Joint j)
        {
            switch (j)
            {
                case FixedJoint _: return ConstraintType.Fixed;
                case HingeJoint _: return ConstraintType.Hinge;
                case SpringJoint _: return ConstraintType.Spring;
                case ConfigurableJoint c:
                    bool linLocked = c.xMotion == ConfigurableJointMotion.Locked && c.yMotion == ConfigurableJointMotion.Locked && c.zMotion == ConfigurableJointMotion.Locked;
                    bool angFree = c.angularXMotion == ConfigurableJointMotion.Free && c.angularYMotion == ConfigurableJointMotion.Free && c.angularZMotion == ConfigurableJointMotion.Free;
                    bool angLocked = c.angularXMotion == ConfigurableJointMotion.Locked && c.angularYMotion == ConfigurableJointMotion.Locked && c.angularZMotion == ConfigurableJointMotion.Locked;
                    if (linLocked && angFree) return ConstraintType.Point;
                    if (c.xMotion == ConfigurableJointMotion.Free && c.yMotion == ConfigurableJointMotion.Locked && c.zMotion == ConfigurableJointMotion.Locked && angLocked) return ConstraintType.Slider;
                    return ConstraintType.Generic;
                default: return ConstraintType.Generic;
            }
        }

        Joint AddJoint(ConstraintType t)
        {
            switch (t)
            {
                case ConstraintType.Fixed: return Undo.AddComponent<FixedJoint>(Go);
                case ConstraintType.Hinge: return Undo.AddComponent<HingeJoint>(Go);
                case ConstraintType.Spring: return Undo.AddComponent<SpringJoint>(Go);
            }
            var c = Undo.AddComponent<ConfigurableJoint>(Go);
            var L = ConfigurableJointMotion.Locked; var F = ConfigurableJointMotion.Free;
            if (t == ConstraintType.Point) { c.xMotion = c.yMotion = c.zMotion = L; c.angularXMotion = c.angularYMotion = c.angularZMotion = F; }
            else if (t == ConstraintType.Slider) { c.xMotion = F; c.yMotion = c.zMotion = L; c.angularXMotion = c.angularYMotion = c.angularZMotion = L; }
            return c;
        }

        void ToggleOcean(BlendUdonBuoyancy buoy)
        {
            if (buoy) { RemoveLinked(buoy); RestoreMaterials(); return; }
            var r = Get<Renderer>();
            if (r)
            {
                SaveMaterials();
                const string folder = "Assets/BlendProperty/Baked";
                if (!AssetDatabase.IsValidFolder("Assets/BlendProperty")) AssetDatabase.CreateFolder("Assets", "BlendProperty");
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/BlendProperty", "Baked");
                var mat = new Material(Shader.Find("Blend Property/Ocean")) { name = Go.name + "_ocean" };
                AssetDatabase.CreateAsset(mat, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Go.name}_ocean.mat"));
                Undo.RecordObject(r, "Ocean");
                r.sharedMaterial = mat;
            }
            Add(typeof(BlendUdonBuoyancy));
            Relink();
        }

        void SaveMaterials()
        {
            var r = Get<Renderer>();
            if (!r) return;
            Undo.RecordObject(Phys, "Physics");
            Phys.previousMaterials = r.sharedMaterials;
        }

        void RestoreMaterials()
        {
            var r = Get<Renderer>();
            if (!r || Phys.previousMaterials == null || Phys.previousMaterials.Length == 0) return;
            Undo.RecordObject(r, "Physics");
            r.sharedMaterials = Phys.previousMaterials;
            Undo.RecordObject(Phys, "Physics");
            Phys.previousMaterials = null;
        }

        // ------------------------------------------------------------------ linking, like Blender's scene-wide effectors

        static readonly Type[] Roles = { typeof(BlendUdonForceField), typeof(Collider), typeof(Rigidbody), typeof(BlendUdonPaintBrush), typeof(BlendUdonFluidFlow) };
        static readonly Type[] NotLinked = { typeof(BlendUdonResetObjects), typeof(BlendUdonPanel), typeof(BlendUdonClothPanel), typeof(BlendUdonPlayerColliders) };

        static bool InScope(Component a, Component b) => Allows(a, b) && Allows(b, a);

        static bool Allows(Component a, Component b)
        {
            var p = a.GetComponent<BlendPhysics>();
            if (!p || p.scope == BlendPhysics.LinkScope.Scene) return true;
            var parent = a.transform.parent;
            return !parent || b.transform.IsChildOf(parent);
        }

        static List<T> Find<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).ToList();

        /// <summary>
        /// Between objects with a Physics component, like Blender's scene-wide effectors: every simulation's list of force
        /// fields, colliders (objects with Collision on), rigid bodies (Active), brushes or flows gets every one of that
        /// kind in its Scope, keeping what was set by hand. Lists on objects without a Physics component only lose
        /// <paramref name="removed"/>.
        /// </summary>
        static void Relink(HashSet<Object> removed = null)
        {
            var sources = new List<Object>();
            foreach (var p in Find<BlendPhysics>())
            {
                sources.AddRange(p.GetComponents<BlendUdonForceField>());
                sources.AddRange(p.GetComponents<BlendUdonPaintBrush>());
                sources.AddRange(p.GetComponents<BlendUdonFluidFlow>());
                if (p.collision) sources.AddRange(p.GetComponents<Collider>().Where(c => !c.isTrigger));
                var rb = p.GetComponent<Rigidbody>();
                if (rb && !rb.isKinematic) sources.Add(rb);
            }
            if (removed != null) sources.RemoveAll(removed.Contains);

            foreach (var u in Find<UdonSharpBehaviour>())
            {
                var type = u.GetType();
                if (type.Namespace != typeof(BlendUdonCloth).Namespace || NotLinked.Contains(type)) continue;
                bool linked = u.GetComponent<BlendPhysics>(); // set up by hand: only lose what was removed
                bool isCloth = u is BlendUdonCloth; // a cloth can't collide with its own collider
                bool changed = false;
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var et = f.FieldType.IsArray ? f.FieldType.GetElementType() : null;
                    if (et == null || !Roles.Any(r => r.IsAssignableFrom(et)) || (u is BlendUdonFracture && f.Name == nameof(BlendUdonFracture.pieces))) continue; // baked pieces aren't links
                    var old = ((Array)f.GetValue(u))?.Cast<Object>().ToList() ?? new List<Object>();
                    var list = old.Where(o => o && (removed == null || !removed.Contains(o)) && (!(o is Component oc) || InScope(u, oc))).ToList();
                    foreach (var s in linked ? sources : new List<Object>())
                    {
                        if (!et.IsInstanceOfType(s) || list.Contains(s) || !InScope(u, (Component)s)) continue;
                        if (s is Component c && c.gameObject == u.gameObject && (isCloth || s is BlendUdonForceField || s is Rigidbody)) continue;
                        list.Add(s);
                    }
                    if (list.SequenceEqual(old)) continue;
                    if (!changed) Undo.RecordObject(u, "Link Physics");
                    var arr = Array.CreateInstance(et, list.Count);
                    for (int i = 0; i < list.Count; i++) arr.SetValue(list[i], i);
                    f.SetValue(u, arr);
                    changed = true;
                }
                if (changed) Commit(u);
            }
        }
    }
}
