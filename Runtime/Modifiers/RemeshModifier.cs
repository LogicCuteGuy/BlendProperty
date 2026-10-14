using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender Remesh: rebuild the shape as an all-quad surface from a voxel grid.
    /// Blocks = cubes, Smooth = relaxed surface, Sharp = snapped back onto the input (keeps edges),
    /// Voxel = sized by Voxel Size, snapped onto the input so it keeps its volume.
    /// The input should be closed (watertight); UVs are not kept, as in Blender.
    /// </summary>
    // ponytail: inside/outside from ray parity + surface nets (not Blender's dual contouring / OpenVDB),
    // so parts thinner than a voxel vanish. Grid capped at MaxCells per axis.
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Remesh.png")]
    [AddComponentMenu("Blend Modifier/Generate/Remesh")]
    public class RemeshModifier : BlendModifier
    {
        public enum Mode { Voxel, Blocks, Smooth, Sharp }

        public const int MaxCells = 192;

        public Mode mode = Mode.Voxel;
        [Min(0.00001f), Tooltip("Voxel: size of one voxel in metres. Grows automatically to stay under 192 voxels per axis.")]
        public float voxelSize = 0.05f;
        [Range(1, 10), Tooltip("Blocks / Smooth / Sharp: resolution is 2^depth voxels across the largest side.")]
        public int octreeDepth = 4;
        [Range(0.01f, 1f), Tooltip("Blocks / Smooth / Sharp: lower values lower the resolution.")]
        public float scale = 0.9f;
        [Range(0, 1), Tooltip("Sharp: how closely the result is pulled back onto the input's edges and corners.")]
        public float sharpness = 1f;
        [Range(0, 20), Tooltip("Relaxation passes that round off the voxel steps (Smooth, Voxel).")]
        public int smooth = 4;
        [Tooltip("Drop small loose pieces left by thin parts.")]
        public bool removeDisconnected;
        [Range(0, 1), Tooltip("Pieces with fewer faces than this share of the biggest piece are removed.")]
        public float threshold = 0.1f;

        public override void Apply(MeshData mesh)
        {
            var b = mesh.GetBounds();
            float size = mode == Mode.Voxel ? MeshUnits(voxelSize)
                : Mathf.Max(b.size.x, b.size.y, b.size.z) / (Mathf.Pow(2, octreeDepth) * scale);
            Remesh(mesh, size, mode, smooth, sharpness);
            if (removeDisconnected) RemoveSmallParts(mesh, threshold);
        }

        public static void Remesh(MeshData m, float voxelSize, Mode mode, int smooth, float sharpness = 1f)
        {
            if (m.faces.Count == 0) return;
            var tri = m.Clone();
            tri.TriangulateAll();
            var b = tri.GetBounds();
            float s = Mathf.Max(voxelSize, b.size.x / MaxCells, b.size.y / MaxCells, b.size.z / MaxCells, 1e-30f);
            // one empty layer of lattice points on every side keeps all inside points interior
            Vector3 origin = b.min - Vector3.one * s;
            int nx = Mathf.CeilToInt(b.size.x / s) + 3, ny = Mathf.CeilToInt(b.size.y / s) + 3, nz = Mathf.CeilToInt(b.size.z / s) + 3;
            var inside = Occupancy(tri, origin, s, nx, ny, nz);
            int Id(int x, int y, int z) => (z * ny + y) * nx + x;
            bool In(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < nx && y < ny && z < nz && inside[Id(x, y, z)];

            m.pos.Clear(); m.faces.Clear();
            m.subMeshCount = 1; m.hasUV1 = m.hasColor = false; m.customNormals = false;
            var attr = new[] { new Attr { color = Color.white }, new Attr { color = Color.white }, new Attr { color = Color.white }, new Attr { color = Color.white } };
            var vert = new Dictionary<int, int>();
            var dirs = new[] { Vector3Int.right, Vector3Int.up, new Vector3Int(0, 0, 1) };

            if (mode == Mode.Blocks)
            {
                // a voxel is the cube around a lattice point; its corners sit on the dual lattice (offset −½)
                int Corner(int x, int y, int z)
                {
                    int key = (z * (ny + 1) + y) * (nx + 1) + x;
                    if (!vert.TryGetValue(key, out int i)) { i = m.pos.Count; vert[key] = i; m.pos.Add(origin + new Vector3(x - 0.5f, y - 0.5f, z - 0.5f) * s); }
                    return i;
                }
                for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
                {
                    if (!inside[Id(x, y, z)]) continue;
                    for (int a = 0; a < 3; a++)
                        for (int sign = -1; sign <= 1; sign += 2)
                        {
                            var d = dirs[a] * sign;
                            if (In(x + d.x, y + d.y, z + d.z)) continue;
                            // the face on side `sign` of axis a: corners around it, counter-clockwise seen from outside
                            int u = (a + 1) % 3, v = (a + 2) % 3;
                            var q = new int[4];
                            var c = new Vector3Int(x, y, z); c[a] += sign > 0 ? 1 : 0;
                            var offs = new[] { (0, 0), (1, 0), (1, 1), (0, 1) };
                            for (int k = 0; k < 4; k++)
                            {
                                var p = c; p[u] += offs[k].Item1; p[v] += offs[k].Item2;
                                q[k] = Corner(p.x, p.y, p.z);
                            }
                            var face = new Face(q, (Attr[])attr.Clone(), 0);
                            if (sign < 0) face.Reverse();
                            m.faces.Add(face);
                        }
                }
                return;
            }

            // Surface nets: one vertex per cell that straddles the surface, one quad per crossing lattice edge.
            int Cell(int x, int y, int z)
            {
                int key = (z * ny + y) * nx + x;
                if (vert.TryGetValue(key, out int i)) return i;
                Vector3 sum = Vector3.zero; int n = 0;
                for (int a = 0; a < 3; a++)
                {
                    int u = (a + 1) % 3, v = (a + 2) % 3;
                    for (int k = 0; k < 4; k++)
                    {
                        var p0 = new Vector3Int(x, y, z); p0[u] += k & 1; p0[v] += k >> 1;
                        var p1 = p0; p1[a] += 1;
                        if (In(p0.x, p0.y, p0.z) == In(p1.x, p1.y, p1.z)) continue;
                        sum += ((Vector3)p0 + (Vector3)p1) * 0.5f; n++;
                    }
                }
                i = m.pos.Count; vert[key] = i;
                m.pos.Add(origin + (n > 0 ? sum / n : new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) * s);
                return i;
            }
            // every layer: index 1 lies exactly on the mesh bounds, so it can be inside (a sphere's pole)
            for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            {
                bool here = inside[Id(x, y, z)];
                for (int a = 0; a < 3; a++)
                {
                    var d = dirs[a];
                    if (here == In(x + d.x, y + d.y, z + d.z)) continue;
                    int u = (a + 1) % 3, v = (a + 2) % 3;
                    var q = new int[4];
                    var offs = new[] { (-1, -1), (0, -1), (0, 0), (-1, 0) };
                    for (int k = 0; k < 4; k++)
                    {
                        var c = new Vector3Int(x, y, z); c[u] += offs[k].Item1; c[v] += offs[k].Item2;
                        q[k] = Cell(c.x, c.y, c.z);
                    }
                    var face = new Face(q, (Attr[])attr.Clone(), 0);
                    if (!here) face.Reverse(); // normal points from inside to outside
                    m.faces.Add(face);
                }
            }

            switch (mode)
            {
                case Mode.Smooth:
                    LaplacianSmoothModifier.Relax(m, 0.5f, smooth, true, 1f);
                    break;
                case Mode.Sharp:
                {
                    var snap = new Snapper(tri, s);
                    LaplacianSmoothModifier.Relax(m, 0.5f, 1, true, 1f);
                    for (int i = 0; i < m.pos.Count; i++) m.pos[i] = Vector3.LerpUnclamped(m.pos[i], snap.Nearest(m.pos[i]), sharpness);
                    break;
                }
                default: // Voxel: put the surface back where the input was, then soften the steps
                {
                    var snap = new Snapper(tri, s);
                    for (int i = 0; i < m.pos.Count; i++) m.pos[i] = snap.Nearest(m.pos[i]);
                    if (smooth > 0)
                    {
                        LaplacianSmoothModifier.Relax(m, 0.5f, smooth, true, 1f);
                        for (int i = 0; i < m.pos.Count; i++) m.pos[i] = Vector3.Lerp(m.pos[i], snap.Nearest(m.pos[i]), 0.5f);
                    }
                    break;
                }
            }
        }

        /// <summary>Remove connected pieces with fewer faces than <paramref name="threshold"/> × the biggest piece.</summary>
        public static void RemoveSmallParts(MeshData m, float threshold)
        {
            var parent = new int[m.pos.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            foreach (var f in m.faces) for (int c = 1; c < f.Count; c++) { int a = Find(f.v[0]), b = Find(f.v[c]); if (a != b) parent[a] = b; }
            var count = new Dictionary<int, int>();
            foreach (var f in m.faces) { int r = Find(f.v[0]); count[r] = count.TryGetValue(r, out int n) ? n + 1 : 1; }
            int max = 0; foreach (var n in count.Values) max = Mathf.Max(max, n);
            m.faces = m.faces.FindAll(f => count[Find(f.v[0])] >= threshold * max);
            m.Compact();
        }

        /// <summary>Nearest point on the input surface; triangles bucketed in a grid of the voxel size.</summary>
        class Snapper
        {
            readonly MeshData tri;
            readonly float cell;
            readonly Dictionary<Vector3Int, List<int>> grid = new Dictionary<Vector3Int, List<int>>();
            readonly List<int> big = new List<int>(); // spans too many cells to bucket: always checked

            public Snapper(MeshData tri, float cell)
            {
                this.tri = tri; this.cell = cell;
                for (int fi = 0; fi < tri.faces.Count; fi++)
                {
                    var f = tri.faces[fi];
                    Vector3 a = tri.pos[f.v[0]], b = tri.pos[f.v[1]], c = tri.pos[f.v[2]];
                    var lo = Vector3Int.FloorToInt(Vector3.Min(a, Vector3.Min(b, c)) / cell);
                    var hi = Vector3Int.FloorToInt(Vector3.Max(a, Vector3.Max(b, c)) / cell);
                    var span = hi - lo + Vector3Int.one;
                    if ((long)span.x * span.y * span.z > 64) { big.Add(fi); continue; }
                    for (int z = lo.z; z <= hi.z; z++) for (int y = lo.y; y <= hi.y; y++) for (int x = lo.x; x <= hi.x; x++)
                    {
                        var k = new Vector3Int(x, y, z);
                        if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                        l.Add(fi);
                    }
                }
            }

            public Vector3 Nearest(Vector3 p)
            {
                // surface-net vertices sit within about a voxel of the surface: search the 5×5×5 cells around
                var k0 = Vector3Int.FloorToInt(p / cell);
                Vector3 best = p; float bd = float.MaxValue;
                foreach (var fi in big) Try(fi);
                for (int z = -2; z <= 2; z++) for (int y = -2; y <= 2; y++) for (int x = -2; x <= 2; x++)
                {
                    if (!grid.TryGetValue(k0 + new Vector3Int(x, y, z), out var l)) continue;
                    foreach (var fi in l) Try(fi);
                }
                return best;

                void Try(int fi)
                {
                    var f = tri.faces[fi];
                    var q = ShrinkwrapModifier.Closest(p, tri.pos[f.v[0]], tri.pos[f.v[1]], tri.pos[f.v[2]]);
                    float d = (q - p).sqrMagnitude;
                    if (d < bd) { bd = d; best = q; }
                }
            }
        }

        /// <summary>Lattice point inside/outside by counting ray crossings along +Z per (x, y) column.</summary>
        static bool[] Occupancy(MeshData tri, Vector3 origin, float s, int nx, int ny, int nz)
        {
            var hits = new List<float>[nx * ny];
            // a tiny irrational offset keeps rays off shared edges and vertices (no double counting)
            const float jx = 0.000123f, jy = 0.000277f;
            foreach (var f in tri.faces)
            {
                Vector3 a = tri.pos[f.v[0]], b = tri.pos[f.v[1]], c = tri.pos[f.v[2]];
                int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) - origin.x) / s)), x1 = Mathf.Min(nx - 1, Mathf.CeilToInt((Mathf.Max(a.x, b.x, c.x) - origin.x) / s));
                int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, b.y, c.y) - origin.y) / s)), y1 = Mathf.Min(ny - 1, Mathf.CeilToInt((Mathf.Max(a.y, b.y, c.y) - origin.y) / s));
                float det = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                if (det == 0) continue; // edge-on to the ray
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = origin.x + (x + jx) * s, py = origin.y + (y + jy) * s;
                        float l1 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / det;
                        float l2 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / det;
                        float l3 = 1f - l1 - l2;
                        if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                        (hits[y * nx + x] ??= new List<float>()).Add(l1 * a.z + l2 * b.z + l3 * c.z);
                    }
            }
            var inside = new bool[nx * ny * nz];
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    var h = hits[y * nx + x];
                    if (h == null) continue;
                    h.Sort();
                    int next = 0;
                    for (int z = 0; z < nz; z++)
                    {
                        float pz = origin.z + z * s;
                        while (next < h.Count && h[next] < pz) next++;
                        inside[(z * ny + y) * nx + x] = (next & 1) == 1;
                    }
                }
            return inside;
        }
    }
}
