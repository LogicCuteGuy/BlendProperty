# Runtime GPU for Generate modifiers — research

Status: research only, nothing implemented. Date: 2026-10-15. SDK checked: com.vrchat.worlds 3.10.5.

Question: can the Generate modifiers (Array, Bevel, Boolean, Decimate, Mirror, Remesh, Screw, Solidify,
Subdivision Surface, Triangulate, Weld, Wireframe) get the same **Runtime: Off / GPU** option the Deform
modifiers have, keeping the user's rules: GPU only, one Runtime setting on the modifier, nothing extra or
hidden in the scene, everything made at build.

**Short answer:** yes for Array, Mirror, Screw and Solidify, if the build bakes the largest topology and the
GPU only moves vertices. Wireframe, Bevel and Subdivision can follow later with more build-time work.
Boolean, Remesh, Decimate, Weld and Triangulate can't work this way.

---

## 1. How the Deform runtime works today

- `BlendModifier.Runtime` (virtual, `RuntimeMode { Off, GPU }`) — Wave, Simple Deform, Lattice, Hook and
  Warp override it with a serialized `runtime` field.
- `BlendModifierStack.Rebuild` bakes two meshes when any modifier is live: `output` (everything, for
  the editor) and `runtimeBase = Evaluate(skipRuntime: true)` (what ships).
- `RuntimeModifiers.Build` (Udon/Editor, runs at build and when entering Play) adds one
  `BlendUdonDeformStack`, packs up to 8 ops into `opTypes/opA..opD/opT0/opT1`, and bakes a `Rest`
  texture (one texel per vertex of `runtimeBase`) plus two Custom Render Textures (`Deform`, `Bounds`).
- At runtime Udon packs per-frame matrices and settings into shader arrays, updates the CRTs and runs
  `VRCAsyncGPUReadback`. In `OnAsyncGpuReadbackComplete` a **per-vertex Udon loop** copies `Color[]` →
  `Vector3[]`, then calls `mesh.vertices = cur; RecalculateNormals(); RecalculateBounds()`.
- Contract: **output vertex i = f(rest vertex i)**. The vertex count and triangles stay fixed. Live ops run after all
  baked ones (the stack Inspector warns when a live op sits above a baked one).

The main limit is that Udon loop. `TryGetData` only accepts `byte[]`, `float[]`, `Color[]` and `Color32[]`
(checked in `VRC.Udon.VRCWrapperModules.dll`), and `Mesh.SetVertices` needs `Vector3[]`, so Udon has to
copy every vertex itself. Every Generate approach below multiplies the vertex count, so this loop sets the budget.

## 2. Udon / VRChat mesh APIs that matter (checked in the SDK 3.10.5 wrapper DLLs)

| API | Exposed | Use |
|---|---|---|
| `mesh.vertices =`, `SetVertices(Vector3[], start, length)` | yes | write positions (the length overload lets you upload only part of the mesh) |
| `SetTriangles(int[] tris, int start, int length, int submesh, bool calcBounds, int baseVertex)` | yes | **one native call** that sets a prefix of a baked index array, so whole copies can be shown or hidden without a Udon loop |
| `SetIndices(...)`, `SetIndexBufferParams`, `SetSubMesh(int, SubMeshDescriptor, flags)` | yes | `SubMeshDescriptor`'s constructor didn't turn up in the wrapper, so use `SetTriangles` instead |
| `GetTriangles/GetIndices`, `vertexCount`, `CombineMeshes` | yes | — |
| `VRCAsyncGPUReadback.Request` + `TryGetData(Color[]/float[]/byte[]/Color32[])` | yes | no `Vector3[]` or `Vector4[]` overload, so the copy loop stays |
| `new Mesh()`, `AddComponent` | `new Mesh` works; `AddComponent` doesn't (see memory note) | not needed: the build hook does everything |

All the topology code in this package (`BlendUdonExplode`, `BlendUdonHair`, `BlendUdonParticles` mesh
builds) runs in `#if UNITY_EDITOR` bake methods. No Udon script rebuilds topology at runtime today.

## 3. The general technique: a baked largest topology plus a vertex source map

Every modifier recommended below works because each output vertex comes from **one** input vertex plus a
per-vertex transform:

```
out[i] = G_k( D_before( rest[src[i]] ), meta[i] )   then   D_after(...)
```

- **Build:** evaluate the live Generate modifier at its build settings (merge off, open sweep, …) to get
  the largest topology. Record, for every final Unity vertex, `src` (the input-vertex index) and `copy` (which copy, mirror
  side, ring or shell it belongs to). `MeshData.ToMesh` splits vertices by corner attributes, so it needs to
  return a `posOfVertex[]` map. That's the one shared change in `MeshData`.
- The current single `Rest` texture becomes **`Rest` (input positions, n texels) + `Meta` (per output vertex:
  src index, copy code, op flags)**. The `Deform` CRT grows to the output vertex count.
- **Shader:** for each output texel, read `Rest[src]`, run the live ops in stack order. When it reaches a
  Generate op, apply that copy's transform. Deform ops before and after both still work, because each
  op still touches one vertex at a time.
- **Udon (cheap, per change, not per vertex):** compute per-copy matrices for Array/Mirror/Screw into a
  `Vector4[]` (3 rows per copy) → shader array. Shader arrays max out at 1023 float4, so **cap at ~256 copies
  per stack**.
- **Live copy count** (Array count, Screw iterations): faces are already appended copy by copy, so each
  submesh's index buffer is ordered by copy. At build, store `trisPerCopy[submesh]`. At runtime call
  `SetTriangles(baked, 0, k * trisPerCopy * 3, s, false, 0)`, one native call per submesh. Hidden copies
  get no triangles, so they cost nothing to draw and nothing degenerate gets drawn. Their vertices are still read back. The shader can
  skip computing them, but the loop still copies them.
- **Merge / Weld inside a live modifier is off at runtime** (log a warning, like Wave's Along Normals).
  Welded seam vertices would have to sit in two places once the copies move. Without merge, seams show
  as a hard normal split after `RecalculateNormals`.
  Ceiling: a smooth seam would need a baked seam-pair list and a normal-averaging pass in Udon.

## 4. Per-modifier classification

(a) = per-vertex motion on a baked largest-topology mesh (GPU). (b) = needs topology rebuilt in Udon.
(c) = not feasible or not worth it.

| Modifier | Class | Live at runtime | Fixed at build (topology) | Notes |
|---|---|---|---|---|
| **Array** | **(a)** | Line offsets, rotation and scale per copy; Circle radius and sweep; Transform object moving; Curve points moving; count ≤ build count (prefix `SetTriangles`); Distance count method, clamped to build count | shape, build count = max, random seed (bake random per copy), flips | Udon ports `Transforms()` (small loops over ≤256 copies, Catmull-Rom for Curve). `splitCopies`, `colliderPerCopy` and merge are off when live. |
| **Mirror** | **(a)** | mirror object moving or rotating | axes on/off, bisect, UV flips | 2–8 copies (X×Y×Z), so one matrix per copy. Bisect: either keep the build-time cut (a gap or overlap appears when the mirror moves) or clamp vertices onto the plane in the shader, which is an approximation because triangles get squashed rather than cut. Recommend baked cut + warning. |
| **Screw** | **(a)** | angle, screw offset, axis object, object screw, iterations ≤ build | steps, profile edges, keep original, flip, stretch U/V | Always bake the **open** sweep (`rings = total + 1`), even at 360°. Otherwise changing the angle can't open it. At 360° the last ring sits on the first, which leaves a normal seam. Ring r = copy r. |
| **Solidify** | **(a)** | thickness, offset | fill rim, only rim, flip, material offsets, even thickness and clamp factors (bake per vertex) | Shell vertex = src + n·(outer or inner). `n` = input vertex normal **baked from the build-time input**. That's exact when no live Deform runs before Solidify, and approximate otherwise. |
| **Wireframe** | (a), later | thickness, offset | relative, even, replace original | Inner corners = v + dir·d(thickness). Bake dir and angle factors per corner, then Solidify on top. Its min() clamps make it non-linear, so it's more bake data than Solidify. |
| **Bevel** | (a), later, width only | width (Offset type, while Clamp Overlap doesn't trigger) | segments, profile, angle limit, affect, harden normals | Positions are affine in width at a fixed profile, so bake P(w) and P(2w), check the topology matches, then shader = P0 + w·d. Clamp overlap and angle limit change topology, so they stay at build values. |
| **Subdivision Surface** | (a), later, fixed levels | **positions follow live deforms above it** (Lattice/Hook → smooth surface) | levels, mode | At a fixed level, Catmull-Clark is a fixed linear map, so output = Σ wⱼ·input[j]. Needs a 2nd CRT pass: deform the control cage, then apply baked stencils. That breaks the one-source rule, so it needs its own pass. Weights grow fast with level (practical: 1–2). Changing levels at runtime is (c). |
| **Decimate** | (c) | — | — | Collapse could become a baked progressive mesh with a live ratio, but the reason to decimate is fewer triangles, and that would only work by snapping vertices into degenerate triangles. Not worth it. Planar and Un-Subdivide are (c). |
| **Weld** | (c) | — | — | Its only effect is removing vertices. With a fixed vertex count the best you get is snapping positions together, and that's what the baked result already is. |
| **Triangulate** | (c) | — | — | No continuous parameter. Changing the method only changes which diagonals are drawn. Leave it baked. |
| **Boolean** | (c) | — | — | A moving cutter changes topology freely. A Udon BSP port would be orders of magnitude too slow, and shader CSG tricks need the material's cooperation, but materials are the user's (lilToon). |
| **Remesh** | (c) | — | — | Voxel → marching cubes up to 192³ cells. Even a GPU max-topology marching cubes would need a huge vertex budget and readback. |

**(b) list: empty.** Udon can call `SetTriangles`/`SetVertices`, but generating topology means per-element
Udon loops (rough estimate: 1 µs or more per element) and breaks the GPU-only preference. The only "topology in
Udon" kept is the prefix `SetTriangles` trick, which is one native call with no loop.

## 5. Costs and limits to put in the Inspector help

- Readback loop: Udon per vertex on every frame where something changed. A live Array of 20 copies of
  a 2k-vertex mesh = 40k vertices copied in Udon per change frame. That's the main cost, at roughly 1 µs or more per vertex
  (an estimate, not profiled). Recommend a build warning above ~20k live output vertices.
- Shader arrays: ≤1023 float4 per array, so ~256 copies (3 rows each) across all live Generate ops.
- One-source rule: Subdivision (and anything else that averages neighbours) needs an extra CRT pass.
- Normals: `RecalculateNormals` after readback (native, already called). Seams split wherever merge was skipped.
- Order: live ops still run after all baked ones. With Generate this matters more (e.g. Array(GPU) →
  Bevel(Off) can't be represented). The existing "move them to the bottom" warning covers it.

## 6. Recommended phase 2

1. **Shared plumbing:** `MeshData.ToMesh` returns `posOfVertex[]`. `BlendModifier` gets a build-only virtual
   `BakeRuntime(MeshData input) → (MeshData expanded, int[] src, int[] copy)`, or the generate op
   documents its pos layout: Array/Mirror copy k = range `[k·n, (k+1)·n)`, Screw ring r, Solidify `[0,n)` outer /
   `[n,2n)` inner. `runtimeBase` = the expanded mesh. The `Meta` texture and per-copy matrix array go in the shader.
2. **Array** (Line / Circle / Transform / Curve). This is the most-requested case: rotating rings, growing
   rows, chains following moving points.
3. **Mirror** (moving mirror object).
4. **Screw** (live angle, offset, iterations; open sweep baked).
5. **Solidify** (live thickness and offset).

All four reuse `BlendUdonDeformStack`: new op types `Array=5, Mirror=6, Screw=7, Solidify=8`, with
`Slot()` field names taken from the modifier (`count`, `radius`, `angle`, `thickness`...). They also reuse
the same CRT and readback path and the same `RuntimeModifiers.Build` switch. Nothing new is added to the scene,
and each modifier gets the same `public RuntimeMode runtime` field the Deform modifiers have.

Phase 3 candidates: Wireframe thickness, Bevel width, Subdivision with fixed levels after live deforms.

### Design sketch (phase 2)

```
Build (RuntimeModifiers.Build):
  live = enabled mods with Runtime GPU (≤ 8)
  input = stack.Evaluate(skipRuntime)            // n verts, before any live op
  data  = input; codes = identity
  for op in live:
      if op is Generate: (data, src, copy) = op.BakeRuntime(data)   // merge off, open sweep, max count
                         codes = compose(codes, src, copy)           // mixed-radix copy code per vertex
      else: pass-through (positions untouched; deform runs on GPU)
  runtimeBase = data.ToMesh(out posOfVertex)
  Rest  = input positions (n)          Meta = per final vertex (src, copyCode)
  Deform CRT = final vertex count; trisPerCopy[submesh] for live count

Udon (BlendUdonDeformStack.PostLateUpdate, on change):
  Pack(): existing deform ops + per-copy matrices for Array/Mirror/Screw → _Copies[]
  if count changed: mesh.SetTriangles(baked[s], 0, k*trisPerCopy[s]*3, s, false, 0) per submesh
  output.Update(1) → readback → existing loop → mesh.vertices

Shader (Deform pass):
  p = Rest(src); code = copyCode
  for o in ops:
      Deform types: p = Op(o, p)                        // unchanged
      Array/Mirror/Screw: c = digit(code, o); p = mul(_Copies[base_o + c], p)
      Solidify: p += BakedNormal(src) * (c == 0 ? outer : inner)
```
