using UnityEditor;
using UnityEngine;

namespace BlendProperty.Udon.Editor
{
    /// <summary>Scene-view gizmos for the Blend Physics components, shown while selected (otherwise only the icon). Editor-only.</summary>
    static class BlendPhysicsGizmos
    {
        const GizmoType Always = GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable;
        static readonly Color Field = new Color(0.75f, 0.6f, 1f, 1f);

        // scene icons, one registration per type so each gets its own row in the Gizmos menu (BlendSceneIcons)
        [DrawGizmo(Always)] static void BlendUdonBuoyancyIcon(BlendUdonBuoyancy m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonClothIcon(BlendUdonCloth m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonClothPanelIcon(BlendUdonClothPanel m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonDynamicPaintIcon(BlendUdonDynamicPaint m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonDynamicWavesIcon(BlendUdonDynamicWaves m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonExplodeIcon(BlendUdonExplode m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonFluidIcon(BlendUdonFluid m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonFluidFlowIcon(BlendUdonFluidFlow m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonForceFieldIcon(BlendUdonForceField m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonFractureIcon(BlendUdonFracture m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonHairIcon(BlendUdonHair m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonPaintBrushIcon(BlendUdonPaintBrush m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonPanelIcon(BlendUdonPanel m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonParticlesIcon(BlendUdonParticles m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonPlayerCollidersIcon(BlendUdonPlayerColliders m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);
        [DrawGizmo(Always)] static void BlendUdonResetObjectsIcon(BlendUdonResetObjects m, GizmoType g) => global::BlendProperty.Editor.BlendSceneIcons.Draw(m);

        [DrawGizmo(GizmoType.Selected)]
        static void ForceField(BlendUdonForceField f, GizmoType g)
        {
            var t = f.transform;
            Vector3 c = t.position, up = t.up;
            Handles.color = Gizmos.color = Field;
            Gizmos.DrawWireSphere(c, 0.08f);
            switch (f.type)
            {
                case BlendUdonForceType.Wind:
                    Handles.DrawWireDisc(c, up, 0.3f);
                    Arrow(c, up, 0.6f);
                    break;
                case BlendUdonForceType.Vortex:
                    for (int i = 1; i <= 3; i++) Handles.DrawWireArc(c + up * (0.1f * i), up, t.right, 300f, 0.12f * i);
                    break;
                case BlendUdonForceType.Force:
                    foreach (var d in new[] { t.right, -t.right, up, -up, t.forward, -t.forward }) Arrow(c + d * 0.1f, f.strength >= 0 ? d : -d, 0.2f);
                    break;
                default:
                    Handles.DrawWireDisc(c, up, 0.2f);
                    break;
            }
            if (f.maxDistance > 0f) Gizmos.DrawWireSphere(c, f.maxDistance);
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Brush(BlendUdonPaintBrush b, GizmoType g)
        {
            var col = b.erase ? Color.white : b.color; col.a = 1f;
            Transform tip = b.tip ? b.tip : b.transform;
            Gizmos.color = col;
            Gizmos.DrawWireSphere(tip.position, b.radius);
            if (b.softness > 0f && b.tool != BlendPaintTool.Pen) { Gizmos.color = new Color(col.r, col.g, col.b, 0.4f); Gizmos.DrawWireSphere(tip.position, b.radius * (1f - b.softness)); }
            if (b.tipTexture)
            {
                float a = b.angle * Mathf.Deg2Rad;
                Vector3 up = (tip.up * Mathf.Cos(a) - tip.right * Mathf.Sin(a)) * b.radius;
                Gizmos.DrawLine(tip.position - up, tip.position + up);
            }
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Flow(BlendUdonFluidFlow f, GizmoType g)
        {
            var col = f.flowType == BlendUdonFluidFlowType.Liquid ? new Color(0.3f, 0.6f, 1f) : f.flowType == BlendUdonFluidFlowType.Smoke ? new Color(0.8f, 0.8f, 0.8f) : new Color(1f, 0.55f, 0.15f);
            Handles.color = Gizmos.color = col;
            Gizmos.DrawWireSphere(f.transform.position, f.radius);
            if (f.useInitialVelocity && f.initialVelocity.sqrMagnitude > 1e-6f)
                Arrow(f.transform.position, f.initialVelocity.normalized, Mathf.Min(f.initialVelocity.magnitude * 0.2f, 1f) + f.radius);
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Domain(BlendUdonFluid d, GizmoType g)
        {
            var mf = d.GetComponent<MeshFilter>();
            if (!mf || !mf.sharedMesh) return;
            Gizmos.matrix = d.transform.localToWorldMatrix;
            Gizmos.color = d.domainType == BlendUdonFluidType.Liquid ? new Color(0.3f, 0.6f, 1f) : new Color(1f, 0.6f, 0.2f);
            var b = mf.sharedMesh.bounds;
            Gizmos.DrawWireCube(b.center, b.size);
            if (d.domainType == BlendUdonFluidType.Liquid) // fill level
            {
                float y = b.min.y + b.size.y * d.fillLevel;
                Gizmos.DrawWireCube(new Vector3(b.center.x, y, b.center.z), new Vector3(b.size.x, 0f, b.size.z));
            }
            Gizmos.matrix = Matrix4x4.identity;
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Buoyancy(BlendUdonBuoyancy b, GizmoType g)
        {
            if (b.bodies == null) return;
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.8f);
            foreach (var rb in b.bodies) if (rb) Gizmos.DrawWireSphere(rb.worldCenterOfMass, b.size);
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Explode(BlendUdonExplode e, GizmoType g)
        {
            if (!e.useFloor) return;
            Handles.color = new Color(1f, 0.5f, 0.2f, 0.8f);
            var p = e.transform.position; p.y -= e.floorOffset;
            Handles.DrawWireDisc(p, Vector3.up, 1f); // where the pieces land
        }

        static void Arrow(Vector3 from, Vector3 dir, float length)
        {
            Vector3 to = from + dir * length;
            Gizmos.DrawLine(from, to);
            Handles.ConeHandleCap(0, to - dir * length * 0.1f, Quaternion.LookRotation(dir), length * 0.15f, EventType.Repaint);
        }
    }
}
