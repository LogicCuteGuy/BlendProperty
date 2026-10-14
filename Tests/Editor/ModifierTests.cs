using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BlendProperty.Tests
{
    public class ModifierTests
    {
        static Mesh cubeMesh;
        GameObject go;

        static MeshData Cube()
        {
            if (!cubeMesh)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Object.DestroyImmediate(tmp);
            }
            return MeshData.FromMesh(cubeMesh);
        }

        static int Tris(MeshData m) { var c = m.Clone(); c.TriangulateAll(); return c.faces.Count; }

        static Mesh Export(MeshData m, float angle = 30f) { var mesh = new Mesh(); m.ToMesh(mesh, angle); return mesh; }

        [TearDown] public void TearDown() { if (go) Object.DestroyImmediate(go); }

        [Test] public void Import_WeldsAndJoinsQuads()
        {
            var m = Cube();
            Assert.AreEqual(8, m.pos.Count);
            Assert.AreEqual(6, m.faces.Count);
            Assert.IsTrue(m.faces.All(f => f.Count == 4));
            Assert.AreEqual(24, Export(m).vertexCount); // 30° auto smooth keeps cube hard edges
        }

        [Test] public void Weld_MergesNearDuplicates()
        {
            var m = Cube();
            var copy = m.Clone();
            m.Append(copy, Matrix4x4.Translate(new Vector3(1.0005f, 0, 0)));
            Assert.AreEqual(16, m.pos.Count);
            m.Weld(0.001f);
            Assert.AreEqual(12, m.pos.Count);
        }

        [Test] public void Array_CopiesCount()
        {
            var m = Cube();
            go = new GameObject();
            var a = go.AddComponent<MeshFilter>().gameObject.AddComponent<ArrayModifier>();
            a.count = 3;
            a.Apply(m);
            Assert.AreEqual(24, m.pos.Count);
            Assert.AreEqual(18, m.faces.Count);
            Assert.AreEqual(3f, m.GetBounds().size.x, 1e-4f);
        }

        [Test] public void Array_CircleFullAndArc()
        {
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var a = go.AddComponent<ArrayModifier>();
            a.shape = ArrayModifier.Shape.Circle; a.count = 8; a.radius = 3f;
            var m = Cube(); a.Apply(m);
            Assert.AreEqual(8 * 6, m.faces.Count);
            var b = m.GetBounds();
            Assert.AreEqual(0f, b.center.x, 1e-3f); Assert.AreEqual(0f, b.center.z, 1e-3f); // centred ring
            Assert.AreEqual(1f, b.size.y, 1e-4f);                                            // flat around Y
            Assert.Greater(b.size.x, 6f);

            a.circleSegment = ArrayModifier.CircleSegment.Arc; a.sweepAngle = 180f; a.count = 3;
            m = Cube(); a.Apply(m);
            // 0°, 90°, 180°: copies at +X, -Z and -X
            Assert.AreEqual(7f, m.GetBounds().size.x, 1e-3f);
        }

        [Test] public void Array_LineEndpointAndDistanceCount()
        {
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var a = go.AddComponent<ArrayModifier>();
            a.offsetMethod = ArrayModifier.OffsetMethod.Endpoint; a.translation = new Vector3(0, 0, 10); a.count = 5;
            var m = Cube(); a.Apply(m);
            Assert.AreEqual(11f, m.GetBounds().size.z, 1e-4f);
            Assert.AreEqual(5 * 6, m.faces.Count);

            a.shape = ArrayModifier.Shape.Circle; a.countMethod = ArrayModifier.CountMethod.Distance; a.angularDistance = 30f;
            m = Cube(); a.Apply(m);
            Assert.AreEqual(12 * 6, m.faces.Count);
        }

        [Test] public void Array_RandomIsSeeded()
        {
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var a = go.AddComponent<ArrayModifier>();
            a.count = 4; a.randomOffset = Vector3.one; a.seed = 7;
            var m1 = Cube(); a.Apply(m1);
            var m2 = Cube(); a.Apply(m2);
            Assert.AreEqual(m1.pos[30], m2.pos[30]);
            a.seed = 8;
            var m3 = Cube(); a.Apply(m3);
            Assert.AreNotEqual(m1.pos[30], m3.pos[30]);
        }

        [Test] public void Weld_ConnectedKeepsLooseParts()
        {
            var m = Cube();
            var other = Cube(); // a touching duplicate: All merges it in, Connected leaves it
            m.Append(other, Matrix4x4.identity);
            var all = m.Clone(); all.Weld(0.01f);
            Assert.AreEqual(8, all.pos.Count);
            WeldModifier.WeldConnected(m, 0.01f);
            Assert.AreEqual(16, m.pos.Count);
        }

        [Test] public void Triangulate_QuadMethodsPickDiagonals()
        {
            var m = MeshData.FromMesh(QuadMesh());
            Assert.AreEqual(1, m.faces.Count); // joined into one quad
            var fixedA = m.Clone(); TriangulateModifier.Triangulate(fixedA, TriangulateModifier.QuadMethod.Fixed, TriangulateModifier.NgonMethod.Clip, 4);
            var fixedB = m.Clone(); TriangulateModifier.Triangulate(fixedB, TriangulateModifier.QuadMethod.FixedAlternate, TriangulateModifier.NgonMethod.Clip, 4);
            Assert.AreEqual(2, fixedA.faces.Count);
            bool Has(MeshData d, int a, int b) => d.EdgeFaces().ContainsKey(MeshData.EdgeKey(a, b));
            var q = m.faces[0].v;
            Assert.IsTrue(Has(fixedA, q[0], q[2]) && !Has(fixedA, q[1], q[3]));
            Assert.IsTrue(Has(fixedB, q[1], q[3]) && !Has(fixedB, q[0], q[2]));
        }

        [Test] public void Solidify_OnlyRimAndMaterialOffset()
        {
            var quad = MeshData.FromMesh(QuadMesh());
            SolidifyModifier.Solidify(quad, 0.1f, -1f, false, true, false, onlyRim: true, materialOffset: 1, rimMaterialOffset: 2);
            Assert.AreEqual(1 + 4, quad.faces.Count);             // new shell + 4 rim quads, original dropped
            Assert.AreEqual(3, quad.subMeshCount);
            Assert.AreEqual(1, quad.faces.Count(f => f.sub == 1));
            Assert.AreEqual(4, quad.faces.Count(f => f.sub == 2));
        }

        [Test] public void SimpleDeform_LockKeepsAxis()
        {
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var d = go.AddComponent<SimpleDeformModifier>();
            d.mode = SimpleDeformModifier.Mode.Taper; d.axis = SimpleDeformModifier.Axis.Y; d.factor = 1f; d.lockX = true;
            var m = Cube(); d.Apply(m);
            Assert.AreEqual(1f, m.GetBounds().size.x, 1e-4f);   // locked
            Assert.Greater(m.GetBounds().size.z, 1.01f);         // tapered
        }

        [Test] public void Wave_DiesAfterLifeAndDamping()
        {
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var w = go.AddComponent<WaveModifier>();
            w.life = 5; w.damping = 5; w.time = 20;
            var m = Cube(); var before = m.pos.ToArray(); w.Apply(m);
            CollectionAssert.AreEqual(before, m.pos);
        }

        [Test] public void Bevel_WidthTypesOnCube()
        {
            // cube edges are 90°: Width w → offset w/√2, Depth d → offset d·√2
            float Inset(BevelModifier.WidthType t, float w)
            {
                var m = Cube();
                BevelModifier.Bevel(m, w, 1, 0.5f, 30f, true, t);
                float top = 0; // the top face shrinks by 2 × offset
                foreach (var f in m.faces)
                    if (MeshData.Unit(m.FaceNormal(f)).y > 0.99f) { var b = new Bounds(m.pos[f.v[0]], Vector3.zero); foreach (var v in f.v) b.Encapsulate(m.pos[v]); top = b.size.x; }
                return (1f - top) * 0.5f;
            }
            Assert.AreEqual(0.1f, Inset(BevelModifier.WidthType.Offset, 0.1f), 1e-4f);
            Assert.AreEqual(0.1f / Mathf.Sqrt(2), Inset(BevelModifier.WidthType.Width, 0.1f), 1e-4f);
            Assert.AreEqual(0.1f * Mathf.Sqrt(2), Inset(BevelModifier.WidthType.Depth, 0.1f), 1e-4f);
            Assert.AreEqual(0.1f, Inset(BevelModifier.WidthType.Percent, 10f), 1e-4f); // 10% of 1-unit edges
        }

        [Test] public void Bevel_VerticesCutsCorners()
        {
            var m = Cube();
            BevelModifier.BevelVertices(m, 0.2f, BevelModifier.WidthType.Offset, 0f, true);
            Assert.AreEqual(6 + 8, m.faces.Count);             // 6 octagons + 8 corner triangles
            Assert.AreEqual(24, m.pos.Count);
            Assert.AreEqual(0, OpenEdges(m));
        }

        [Test] public void Bevel_HardenKeepsFlatFaces()
        {
            var m = Cube();
            BevelModifier.Bevel(m, 0.1f, 3, 0.5f, 30f, true, hardenNormals: true);
            Assert.IsTrue(m.customNormals);
            var top = m.faces.First(f => MeshData.Unit(m.FaceNormal(f)).y > 0.999f && f.Count == 4);
            foreach (var a in top.a) Assert.AreEqual(1f, a.normal.y, 1e-4f);
        }

        [Test] public void Decimate_UnSubdivideRestoresCube()
        {
            var m = Cube();
            SubdivisionModifier.Subdivide(m, false, true);
            Assert.AreEqual(24, m.faces.Count);
            Assert.IsTrue(DecimateModifier.UnSubdivide(m));
            Assert.AreEqual(6, m.faces.Count);
            Assert.AreEqual(8, m.pos.Count);
        }

        [Test] public void Remesh_SharpKeepsCubeCorners()
        {
            var m = Cube();
            RemeshModifier.Remesh(m, 0.05f, RemeshModifier.Mode.Sharp, 0);
            Assert.AreEqual(0, OpenEdges(m));
            Assert.AreEqual(1f, Volume(m), 0.03f);
            Assert.AreEqual(1f, m.GetBounds().size.x, 0.02f);
        }

        [Test] public void Boolean_CollectionCutsEveryChild()
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var holder = new GameObject("cutters"); holder.transform.SetParent(go.transform, false);
            for (int i = 0; i < 2; i++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                c.transform.SetParent(holder.transform, false);
                c.transform.localPosition = new Vector3(i == 0 ? 0.5f : -0.5f, 0, 0);
                c.transform.localScale = new Vector3(0.5f, 2f, 2f);
            }
            var b = go.AddComponent<BooleanModifier>();
            b.operandType = BooleanModifier.OperandType.Collection; b.collection = holder.transform;
            var m = Cube(); b.Apply(m);
            Assert.AreEqual(0.5f, Volume(m), 1e-3f); // both ends cut off: x from -0.25 to 0.25
        }

        [Test] public void Units_AreMetresAtAnyObjectScale()
        {
            // a centimetre FBX: mesh 0.01 units big on an object at scale 100 — same world size as a 1 m cube
            go = new GameObject(); go.AddComponent<MeshFilter>();
            go.transform.localScale = Vector3.one * 100f;
            var bevel = go.AddComponent<BevelModifier>(); bevel.width = 0.1f; // 10 cm
            var m = TinyCube(); bevel.Apply(m);
            float top = 0;
            foreach (var f in m.faces)
                if (MeshData.Unit(m.FaceNormal(f)).y > 0.99f) { var b = new Bounds(m.pos[f.v[0]], Vector3.zero); foreach (var v in f.v) b.Encapsulate(m.pos[v]); top = b.size.x; }
            Assert.AreEqual(0.001f, (0.01f - top) * 0.5f, 1e-6f); // 10 cm in the scene = 0.001 mesh units

            Object.DestroyImmediate(bevel);
            var array = go.AddComponent<ArrayModifier>();
            array.offsetMethod = ArrayModifier.OffsetMethod.Offset; array.translation = new Vector3(2f, 0, 0); array.count = 2; // 2 m apart
            m = TinyCube(); array.Apply(m);
            Assert.AreEqual(0.02f + 0.01f, m.GetBounds().size.x, 1e-6f);
        }

        [Test] public void Mirror_DoublesWithoutMerge()
        {
            var m = Cube();
            for (int i = 0; i < m.pos.Count; i++) m.pos[i] += Vector3.right; // move off the plane
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var mi = go.AddComponent<MirrorModifier>();
            mi.Apply(m);
            Assert.AreEqual(16, m.pos.Count);
            Assert.AreEqual(12, m.faces.Count);
            Assert.AreEqual(-1.5f, m.GetBounds().min.x, 1e-4f);
        }

        [Test] public void Mirror_MergesOnPlane()
        {
            var m = Cube();
            for (int i = 0; i < m.pos.Count; i++) m.pos[i] += Vector3.right * 0.5f; // left face on x=0
            go = new GameObject(); go.AddComponent<MeshFilter>();
            go.AddComponent<MirrorModifier>().Apply(m);
            Assert.AreEqual(12, m.pos.Count);
        }

        [Test] public void Subdivision_SimpleQuadruples()
        {
            var m = Cube();
            SubdivisionModifier.Subdivide(m, simple: true, smoothBoundary: true);
            Assert.AreEqual(24, m.faces.Count);
            Assert.AreEqual(48, Tris(m));
            Assert.AreEqual(1f, m.GetBounds().size.x, 1e-4f);
        }

        [Test] public void Subdivision_CatmullClarkShrinksCube()
        {
            var m = Cube();
            SubdivisionModifier.Subdivide(m, simple: false, smoothBoundary: true);
            Assert.AreEqual(26, m.pos.Count);
            // cube corner (±.5,±.5,±.5) -> (Q + 2R) / 3 = ±5/18 on each axis
            var corner = m.pos[0];
            Assert.AreEqual(5f / 18f, Mathf.Abs(corner.x), 1e-4f);
            Assert.AreEqual(5f / 18f, Mathf.Abs(corner.y), 1e-4f);
        }

        [Test] public void Solidify_ShellAndRim()
        {
            var quad = MeshData.FromMesh(QuadMesh());
            int faces = quad.faces.Count, verts = quad.pos.Count;
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var s = go.AddComponent<SolidifyModifier>();
            s.thickness = 0.1f;
            s.Apply(quad);
            Assert.AreEqual(verts * 2, quad.pos.Count);
            Assert.AreEqual(faces * 2 + 4, quad.faces.Count); // 4 rim quads
        }

        [Test] public void Decimate_HalvesTriangles()
        {
            var m = Cube();
            SubdivisionModifier.Subdivide(m, false, true);
            SubdivisionModifier.Subdivide(m, false, true);
            int before = Tris(m);
            DecimateModifier.Collapse(m, 0.5f);
            Assert.LessOrEqual(m.faces.Count, before * 0.6f);
            Assert.Greater(m.faces.Count, 0);
        }

        [Test] public void Decimate_PlanarRemergesSubdividedFaces()
        {
            var m = Cube();
            SubdivisionModifier.Subdivide(m, simple: true, smoothBoundary: true);
            DecimateModifier.Planar(m, 5f);
            Assert.AreEqual(6, m.faces.Count);
        }

        [Test] public void Bevel_CubeAddsStripsAndCorners()
        {
            var m = Cube();
            BevelModifier.Bevel(m, 0.1f, 1, 0.5f, 30f, true);
            Assert.AreEqual(6 + 12 + 8, m.faces.Count); // faces + edge strips + corner tris
            Assert.AreEqual(24, m.pos.Count);
            Assert.AreEqual(1f, m.GetBounds().size.x, 1e-4f);
        }

        [Test] public void Bevel_SegmentsRound()
        {
            var m = Cube();
            BevelModifier.Bevel(m, 0.1f, 3, 0.5f, 30f, true);
            Assert.AreEqual(6 + 12 * 3 + 8 * 9, m.faces.Count); // corner caps: 9-point ring fanned into tris
            // Non-planar caps shade differently once applied (baked to triangles): every face must be flat.
            foreach (var f in m.faces)
            {
                var n = m.FaceNormal(f).normalized; var c = m.FaceCenter(f);
                foreach (var v in f.v) Assert.AreEqual(0f, Vector3.Dot(m.pos[v] - c, n), 1e-4f);
            }
        }

        [Test] public void SmoothByAngle_Thresholds()
        {
            var m = Cube();
            // UV seams keep 24 verts either way; count distinct normals instead
            Assert.AreEqual(6, Export(m, 30f).normals.Distinct().Count());
            Assert.AreEqual(8, Export(m, 180f).normals.Distinct().Count());
        }

        [Test] public void Displace_ZeroStrengthNoop()
        {
            var m = Cube();
            var before = m.pos.ToArray();
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var d = go.AddComponent<DisplaceModifier>();
            d.strength = 0;
            d.Apply(m);
            CollectionAssert.AreEqual(before, m.pos);
        }

        [Test] public void Stack_OrderMattersAndApply()
        {
            // revert our own undo ops, so the runner's undo replay can't resurrect objects into the user's scene
            int undoGroup = Undo.GetCurrentGroup();
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var array = go.AddComponent<ArrayModifier>();
            array.count = 2;
            var mirror = go.AddComponent<MirrorModifier>();
            mirror.merge = false;
            var stack = go.GetComponent<BlendModifierStack>();
            Assert.IsNotNull(stack);
            var ab = stack.Evaluate();
            Assert.AreEqual(3f, ab.GetBounds().size.x, 1e-4f); // array -0.5..1.5, mirrored -> -1.5..1.5

            UnityEditorInternal.ComponentUtility.MoveComponentUp(mirror);
            Assert.AreEqual(mirror, stack.Modifiers()[0]);
            var ba = stack.Evaluate();
            Assert.AreEqual(2f, ba.GetBounds().size.x, 1e-4f); // mirror onto itself, then array

            var created = new System.Collections.Generic.List<string>();
            try
            {
                Editor.BlendModifierCommands.Apply(mirror);
                Assert.AreEqual(1, stack.Modifiers().Count);
                Assert.IsTrue(EditorUtility.IsPersistent(stack.source));
                created.Add(AssetDatabase.GetAssetPath(stack.source));
                if (stack.Output) created.Add(AssetDatabase.GetAssetPath(stack.Output));

                Editor.BlendModifierCommands.ApplyAll(stack);
                Assert.IsFalse(go.GetComponent<BlendModifierStack>());
                Assert.IsFalse(go.GetComponent<BlendModifier>());
                var final = go.GetComponent<MeshFilter>().sharedMesh;
                Assert.IsTrue(EditorUtility.IsPersistent(final));
                created.Add(AssetDatabase.GetAssetPath(final));
                Assert.AreEqual(2f, final.bounds.size.x, 1e-4f);
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
                Object.DestroyImmediate(go);
                foreach (var path in created.Distinct()) if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
            }
        }

        static float Volume(MeshData m)
        {
            var c = m.Clone(); c.TriangulateAll();
            float v = 0;
            foreach (var f in c.faces) v += Vector3.Dot(c.pos[f.v[0]], Vector3.Cross(c.pos[f.v[1]], c.pos[f.v[2]])) / 6f;
            return v;
        }

        static int OpenEdges(MeshData m) => m.EdgeFaces().Count(kv => kv.Value.Count != 2);

        [TestCase(BooleanModifier.Operation.Union, 1.5f)]
        [TestCase(BooleanModifier.Operation.Difference, 0.5f)]
        [TestCase(BooleanModifier.Operation.Intersect, 0.5f)]
        public void Boolean_CubeVolumes(BooleanModifier.Operation op, float volume)
        {
            var a = Cube();
            var b = Cube();
            for (int i = 0; i < b.pos.Count; i++) b.pos[i] += new Vector3(0.5f, 0, 0);
            BooleanModifier.Csg(a, b, op);
            Assert.AreEqual(volume, Volume(a), 1e-3f);
        }

        [Test] public void Boolean_SphereWithBentQuads()
        {
            // joinTriangles leaves quads up to 1° off-plane; they used to loop forever in the BSP.
            var sphere = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            var a = MeshData.FromMesh(sphere);
            var b = MeshData.FromMesh(sphere);
            for (int i = 0; i < b.pos.Count; i++) b.pos[i] = b.pos[i] * 0.8f + new Vector3(0.35f, 0.3f, -0.3f);
            float full = Volume(a);
            BooleanModifier.Csg(a, b, BooleanModifier.Operation.Difference);
            Assert.Greater(Volume(a), 0.3f * full);
            Assert.Less(Volume(a), 0.95f * full);
        }

        static GameObject ArrayCube(out BlendModifierStack stack)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(g.GetComponent<BoxCollider>());
            g.AddComponent<ArrayModifier>().count = 3;
            stack = g.GetComponent<BlendModifierStack>();
            stack.Rebuild(true);
            return g;
        }

        [Test] public void Stack_UncheckedShowsOriginal()
        {
            var keep = BlendModifierStack.PersistOutput; BlendModifierStack.PersistOutput = null;
            try
            {
                go = ArrayCube(out var stack);
                var filter = go.GetComponent<MeshFilter>();
                Assert.AreEqual(stack.Output, filter.sharedMesh);
                stack.enabled = false; BlendModifierStack.FlushPending();
                Assert.AreEqual(stack.source, filter.sharedMesh);
                stack.enabled = true; BlendModifierStack.FlushPending();
                Assert.AreEqual(stack.Output, filter.sharedMesh);
                Assert.AreEqual(3f, filter.sharedMesh.bounds.size.x, 1e-4f);
            }
            finally { BlendModifierStack.PersistOutput = keep; }
        }

        [Test] public void RemoveAll_BackToOriginal_OneUndo()
        {
            var keep = BlendModifierStack.PersistOutput; BlendModifierStack.PersistOutput = null;
            try
            {
                go = ArrayCube(out var stack);
                go.AddComponent<WaveModifier>();
                var source = stack.source;
                Undo.IncrementCurrentGroup();
                Editor.BlendModifierCommands.RemoveAll(stack);
                Assert.IsFalse(go.GetComponent<BlendModifierStack>());
                Assert.IsEmpty(go.GetComponents<BlendModifier>());
                Assert.AreEqual(source, go.GetComponent<MeshFilter>().sharedMesh);
                Undo.PerformUndo();
                Assert.IsTrue(go.GetComponent<BlendModifierStack>());
                Assert.AreEqual(2, go.GetComponents<BlendModifier>().Length);
            }
            finally { BlendModifierStack.PersistOutput = keep; }
        }

        /// <summary>A centimetre-scale import: the whole mesh is 0.01 units (FBX with object scale 100).</summary>
        static MeshData TinyCube()
        {
            var m = Cube();
            for (int i = 0; i < m.pos.Count; i++) m.pos[i] *= 0.01f;
            return m;
        }

        [Test] public void TinyMesh_NormalsStayUnitLength()
        {
            // Vector3.normalized returns zero under 1e-5 length, which made every normal zero (black mesh).
            var m = TinyCube();
            BevelModifier.Bevel(m, 0.0005f, 3, 0.5f, 30f, true);
            SubdivisionModifier.Subdivide(m, false, true);
            var mesh = Export(m);
            foreach (var n in mesh.normals) Assert.AreEqual(1f, n.magnitude, 1e-3f);
        }

        [Test] public void TinyMesh_BooleanKeepsVolume()
        {
            var a = TinyCube();
            var b = TinyCube();
            for (int i = 0; i < b.pos.Count; i++) b.pos[i] += new Vector3(0.005f, 0, 0);
            BooleanModifier.Csg(a, b, BooleanModifier.Operation.Union);
            Assert.AreEqual(1.5e-6f, Volume(a), 1e-8f);
        }

        [Test] public void Remesh_VoxelSphereKeepsShape()
        {
            var m = MeshData.FromMesh(Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx"));
            float v0 = Volume(m);
            RemeshModifier.Remesh(m, 0.05f, RemeshModifier.Mode.Voxel, 4);
            Assert.AreEqual(0, OpenEdges(m));
            Assert.AreEqual(v0, Volume(m), v0 * 0.08f);
            Assert.AreEqual(1f, m.GetBounds().size.x, 0.08f);
        }

        [Test] public void Remesh_BlocksCubeIsExact()
        {
            var m = Cube();
            RemeshModifier.Remesh(m, 0.25f, RemeshModifier.Mode.Blocks, 0);
            Assert.AreEqual(0, OpenEdges(m));
            Assert.Greater(Volume(m), 0.9f); // voxels centred on lattice points: within one voxel of the cube
            Assert.Less(Volume(m), 1.6f);
        }

        [Test] public void LaplacianSmooth_KeepsVolumeBetterThanSmooth()
        {
            var sphere = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            var plain = MeshData.FromMesh(sphere);
            var taubin = MeshData.FromMesh(sphere);
            float v0 = Volume(plain);
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var sm = go.AddComponent<SmoothModifier>(); sm.factor = 0.5f; sm.repeat = 10;
            sm.Apply(plain);
            LaplacianSmoothModifier.Relax(taubin, 0.5f, 10, true, 0f);
            Assert.Less(Mathf.Abs(Volume(taubin) - v0), Mathf.Abs(Volume(plain) - v0) * 0.5f);
        }

        [Test] public void Hook_FollowsTargetFromBindPose()
        {
            go = new GameObject(); go.AddComponent<MeshFilter>();
            var target = new GameObject("hook");
            target.transform.SetParent(go.transform, false);
            try
            {
                var hook = go.AddComponent<HookModifier>();
                hook.target = target.transform; hook.radius = 0;
                var m = Cube();
                var before = m.pos.ToArray();
                hook.Apply(m); // binds: no change
                Assert.AreEqual(before[0], m.pos[0]);
                target.transform.localPosition = new Vector3(0, 2, 0);
                m = Cube(); hook.Apply(m);
                Assert.AreEqual(before[0] + new Vector3(0, 2, 0), m.pos[0]);
            }
            finally { Object.DestroyImmediate(target); }
        }

        [Test] public void Wireframe_ClosedFrames()
        {
            var m = Cube();
            WireframeModifier.Wireframe(m, 0.05f, -1f, true);
            Assert.AreEqual(0, OpenEdges(m));
            Assert.Less(Volume(m), 1f * 0.5f); // a hollow frame, far less than the solid cube
            Assert.Greater(Volume(m), 0f);
        }

        [Test] public void Shrinkwrap_ClosestPointOnTriangle()
        {
            Vector3 a = Vector3.zero, b = Vector3.right, c = Vector3.forward;
            Assert.AreEqual(new Vector3(0.25f, 0, 0.25f), ShrinkwrapModifier.Closest(new Vector3(0.25f, 3, 0.25f), a, b, c));
            Assert.AreEqual(b, ShrinkwrapModifier.Closest(new Vector3(2, 0, -1), a, b, c));
        }

        static Mesh QuadMesh()
        {
            var m = new Mesh();
            m.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) };
            m.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateNormals();
            return m;
        }
    }
}
