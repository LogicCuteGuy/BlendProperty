using System.Collections.Generic;
using UnityEngine;

namespace BlendProperty
{
    /// <summary>
    /// Blender 5.2 Array: copies arranged in a Line, Circle, along a Curve, or by a repeated Transform,
    /// with optional alignment, randomization and merging. Count includes the original.
    /// </summary>
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Array.png")]
    [AddComponentMenu("Blend Modifier/Generate/Array")]
    public class ArrayModifier : BlendModifier
    {
        public enum Shape { Line, Circle, Curve, Transform }
        public enum OffsetMethod { Relative, Offset, Endpoint }
        public enum CountMethod { Count, Distance }
        public enum CircleSegment { Full, Arc }
        public enum Axis { X, Y, Z }
        public enum TransformReference { Input, Object }
        public enum ScaleAxes { Uniform, Axes }

        public Shape shape = Shape.Line;

        [Tooltip("Line, Transform: total copies including the original. Circle, Curve: used when Count Method is Count.")]
        [Min(1)] public int count = 2;

        // Line
        public OffsetMethod offsetMethod = OffsetMethod.Relative;
        [Tooltip("Relative: step in multiples of the mesh's bounding-box size.")]
        public Vector3 relativeOffset = new Vector3(1, 0, 0);
        [Tooltip("Offset: step per copy. Endpoint: where the last copy ends up. Transform (Input): step per copy.")]
        public Vector3 translation = new Vector3(1, 0, 0);
        [Tooltip("Line, Transform (Input): rotation added per copy (degrees). Endpoint: rotation of the last copy.")]
        public Vector3 rotation;
        [Tooltip("Line, Transform (Input): scale multiplied per copy. Endpoint: scale of the last copy.")]
        public Vector3 scale = Vector3.one;

        // Circle / Curve
        public CountMethod countMethod = CountMethod.Count;
        [Tooltip("Circle, Distance count: angle between copies (degrees).")]
        [Range(0.1f, 360f)] public float angularDistance = 45f;
        [Tooltip("Curve, Distance count: spacing between copies along the curve.")]
        [Min(0.0001f)] public float distance = 1f;
        [Tooltip("Circle: the axis the circle turns around (Unity is Y-up).")]
        public Axis centralAxis = Axis.Y;
        public CircleSegment circleSegment = CircleSegment.Full;
        [Min(0)] public float radius = 1f;
        [Tooltip("Arc: total angle spanned, first to last copy (degrees).")]
        [Range(0, 360)] public float sweepAngle = 180f;
        [Tooltip("Curve: path through these Transforms (Catmull-Rom), like the Curve modifier.")]
        public Transform[] curvePoints = new Transform[0];
        public bool closedCurve;

        // Transform
        public TransformReference transformReference = TransformReference.Object;
        [Tooltip("Each copy is moved by this object's transform relative to the modified object.")]
        public Transform transformObject;
        [Tooltip("On: the object's offset from this object is the step. Off: the object's own world transform is the step.")]
        public bool relativeSpace = true;

        // Circle / Curve orientation
        [Tooltip("Circle, Curve: turn each copy to follow the circle / curve.")]
        public bool alignRotation = true;
        [Tooltip("Extra rotation of every copy, after alignment (degrees).")]
        public Vector3 localRotation;

        // Randomize
        public Vector3 randomOffset;
        [Tooltip("Maximum random rotation per axis (degrees).")]
        public Vector3 randomRotation;
        public ScaleAxes randomScaleAxes = ScaleAxes.Uniform;
        [Tooltip("Maximum random scale change (Uniform uses X). 0.2 = between 0.8 and 1.2.")]
        public Vector3 randomScale;
        [Tooltip("Randomly mirror copies on these axes.")]
        public bool flipX, flipY, flipZ;
        public bool excludeFirst, excludeLast;
        public int seed;

        public bool merge;
        [Min(0)] public float mergeDistance = 0.001f;

        public override IEnumerable<Object> Dependencies()
        {
            if (shape == Shape.Transform && transformReference == TransformReference.Object && transformObject) yield return transformObject;
            if (shape == Shape.Curve && curvePoints != null) foreach (var p in curvePoints) if (p) yield return p;
        }

        public override void Apply(MeshData mesh)
        {
            var xf = Transforms(mesh.GetBounds());
            if (xf.Count == 0) return;
            Randomize(xf);
            var src = mesh.Clone();
            mesh.pos.Clear(); mesh.faces.Clear();
            mesh.AppendCopies(src, xf);
            if (merge) mesh.Weld(MeshUnits(mergeDistance));
        }

        // ponytail: copies capped at 10000 so a zero step or tiny distance can't hang the editor.
        const int MaxCopies = 10000;

        List<Matrix4x4> Transforms(Bounds bounds)
        {
            var r = new List<Matrix4x4>();
            var local = Matrix4x4.Rotate(Quaternion.Euler(localRotation));
            switch (shape)
            {
                case Shape.Line:
                {
                    int n = Mathf.Clamp(count, 1, MaxCopies);
                    if (offsetMethod == OffsetMethod.Endpoint)
                    {
                        // evenly between the original and the end point; rotation and scale ramp up to the last copy
                        for (int i = 0; i < n; i++)
                        {
                            float t = n > 1 ? (float)i / (n - 1) : 0f;
                            r.Add(Matrix4x4.TRS(MeshUnits(translation) * t, Quaternion.Euler(rotation * t), Vector3.LerpUnclamped(Vector3.one, scale, t)));
                        }
                        break;
                    }
                    var move = offsetMethod == OffsetMethod.Relative ? Vector3.Scale(relativeOffset, bounds.size) : MeshUnits(translation);
                    Steps(r, Matrix4x4.TRS(move, Quaternion.Euler(rotation), scale), n);
                    break;
                }
                case Shape.Circle:
                {
                    var ax = AxisVector(centralAxis);
                    // the first copy sits at +radius on the axis that comes next after the central one
                    var start = centralAxis == Axis.X ? Vector3.up : Vector3.right;
                    bool full = circleSegment == CircleSegment.Full;
                    float span = full ? 360f : sweepAngle;
                    int n = countMethod == CountMethod.Count ? count
                        : full ? Mathf.FloorToInt(360f / angularDistance + 1e-4f) : Mathf.FloorToInt(sweepAngle / angularDistance + 1e-4f) + 1;
                    n = Mathf.Clamp(n, 1, MaxCopies);
                    float step = countMethod == CountMethod.Distance ? angularDistance
                        : full ? 360f / n : (n > 1 ? sweepAngle / (n - 1) : 0f);
                    for (int i = 0; i < n; i++)
                    {
                        var q = Quaternion.AngleAxis(step * i, ax);
                        var at = Matrix4x4.Translate(q * (start * MeshUnits(radius)));
                        r.Add((alignRotation ? at * Matrix4x4.Rotate(q) : at) * local);
                    }
                    break;
                }
                case Shape.Curve:
                    CurveTransforms(r, local);
                    break;
                case Shape.Transform:
                {
                    Matrix4x4 step;
                    if (transformReference == TransformReference.Input)
                        step = Matrix4x4.TRS(MeshUnits(translation), Quaternion.Euler(rotation), scale);
                    else if (!transformObject) return r;
                    else step = relativeSpace ? ToLocal(transformObject)
                        : transform.worldToLocalMatrix * transformObject.localToWorldMatrix * transform.localToWorldMatrix;
                    Steps(r, step, Mathf.Clamp(count, 1, MaxCopies));
                    break;
                }
            }
            return r;
        }

        static void Steps(List<Matrix4x4> r, Matrix4x4 step, int n)
        {
            var m = Matrix4x4.identity;
            for (int i = 0; i < n; i++) { r.Add(m); m = step * m; }
        }

        static Vector3 AxisVector(Axis a) => a == Axis.X ? Vector3.right : a == Axis.Y ? Vector3.up : Vector3.forward;

        void CurveTransforms(List<Matrix4x4> r, Matrix4x4 local)
        {
            var ctrl = new List<Vector3>();
            if (curvePoints != null) foreach (var t in curvePoints) if (t) ctrl.Add(transform.InverseTransformPoint(t.position));
            if (ctrl.Count < 2) return;

            // dense polyline + arc length, then copies placed by distance along it
            const int perSegment = 24;
            int segs = closedCurve ? ctrl.Count : ctrl.Count - 1;
            var pts = new List<Vector3>();
            for (int s = 0; s < segs; s++)
                for (int k = 0; k < perSegment; k++)
                    pts.Add(CurveModifier.CatmullRom(ctrl, s, (float)k / perSegment, closedCurve));
            pts.Add(closedCurve ? pts[0] : ctrl[ctrl.Count - 1]);
            var len = new float[pts.Count];
            for (int i = 1; i < pts.Count; i++) len[i] = len[i - 1] + (pts[i] - pts[i - 1]).magnitude;
            float total = len[len.Length - 1];
            if (total <= 0) return;

            float dist = MeshUnits(distance);
            int n = countMethod == CountMethod.Count ? count : Mathf.FloorToInt(total / dist + 1e-4f) + (closedCurve ? 0 : 1);
            n = Mathf.Clamp(n, 1, MaxCopies);
            // closed: no copy on top of the first; open: first and last sit on the ends
            float spacing = countMethod == CountMethod.Distance ? dist : closedCurve ? total / n : (n > 1 ? total / (n - 1) : 0f);

            // rotation-minimizing frame, started from the up vector, so copies don't spin around the curve
            Vector3 up = Vector3.up, prevTan = Vector3.zero;
            int j = 1;
            for (int i = 0; i < n; i++)
            {
                float d = Mathf.Min(spacing * i, total);
                while (j < len.Length - 1 && len[j] < d) j++;
                float f = (d - len[j - 1]) / Mathf.Max(len[j] - len[j - 1], 1e-12f);
                var p = Vector3.Lerp(pts[j - 1], pts[j], f);
                var tan = MeshData.Unit(pts[j] - pts[j - 1]);
                if (i == 0) { up = MeshData.Unit(Vector3.ProjectOnPlane(Vector3.up, tan)); if (up == Vector3.zero) up = MeshData.Unit(Vector3.ProjectOnPlane(Vector3.forward, tan)); }
                else up = MeshData.Unit(Vector3.ProjectOnPlane(Quaternion.FromToRotation(prevTan, tan) * up, tan));
                prevTan = tan;
                var rot = alignRotation ? Quaternion.LookRotation(Vector3.Cross(tan, up), up) : Quaternion.identity; // copy's +X along the curve
                r.Add(Matrix4x4.TRS(p, rot, Vector3.one) * local);
            }
        }

        void Randomize(List<Matrix4x4> xf)
        {
            bool any = randomOffset != Vector3.zero || randomRotation != Vector3.zero || randomScale != Vector3.zero || flipX || flipY || flipZ;
            if (!any) return;
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble() * 2f - 1f;
            for (int i = 0; i < xf.Count; i++)
            {
                // draw for every copy, so excluding first/last doesn't reshuffle the others
                var off = Vector3.Scale(MeshUnits(randomOffset), new Vector3(R(), R(), R()));
                var rot = Quaternion.Euler(Vector3.Scale(randomRotation, new Vector3(R(), R(), R())));
                var sc = randomScaleAxes == ScaleAxes.Uniform ? Vector3.one * (1f + randomScale.x * R())
                    : Vector3.one + Vector3.Scale(randomScale, new Vector3(R(), R(), R()));
                var flip = new Vector3(flipX && rng.Next(2) == 1 ? -1 : 1, flipY && rng.Next(2) == 1 ? -1 : 1, flipZ && rng.Next(2) == 1 ? -1 : 1);
                if ((excludeFirst && i == 0) || (excludeLast && i == xf.Count - 1)) continue;
                xf[i] = xf[i] * Matrix4x4.TRS(off, rot, Vector3.Scale(sc, flip));
            }
        }
    }
}
