using UnityEditor;
using UnityEngine;

namespace BlendProperty.Editor
{
    /// <summary>Scene-view gizmos for modifiers that use another object or a radius (Hook, Warp, Cast, Mirror, UV Project, Simple Deform), shown while selected.</summary>
    static class BlendModifierGizmos
    {
        static readonly Color Orange = new Color(1f, 0.6f, 0.2f, 0.9f);
        static readonly Color Faint = new Color(1f, 0.6f, 0.2f, 0.3f);

        static bool Live(BlendModifier m) => m.enabled;

        [DrawGizmo(GizmoType.Selected)]
        static void Hook(HookModifier m, GizmoType g)
        {
            if (!Live(m) || !m.target) return;
            Gizmos.color = Orange;
            Gizmos.DrawLine(m.transform.position, m.target.position);
            Gizmos.DrawWireCube(m.target.position, Vector3.one * 0.05f);
            if (m.radius > 0f) Gizmos.DrawWireSphere(m.target.position, m.radius);
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Warp(WarpModifier m, GizmoType g)
        {
            if (!Live(m) || !m.from || !m.to) return;
            Gizmos.color = Orange;
            Gizmos.DrawLine(m.from.position, m.to.position);
            Gizmos.DrawWireSphere(m.from.position, m.radius);
            Gizmos.color = Faint;
            Gizmos.DrawWireSphere(m.to.position, m.radius);
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Cast(CastModifier m, GizmoType g)
        {
            if (!Live(m)) return;
            var c = m.control ? m.control : m.transform;
            Gizmos.color = Orange;
            float r = m.radius > 0f ? m.radius : m.sizeFromRadius ? 0f : m.size;
            if (r <= 0f) return;
            if (m.shape == CastModifier.Shape.Cuboid) Gizmos.DrawWireCube(c.position, Vector3.one * 2f * r);
            else if (m.shape == CastModifier.Shape.Cylinder) { Handles.color = Orange; Handles.DrawWireDisc(c.position, c.up, r); }
            else Gizmos.DrawWireSphere(c.position, r);
        }

        [DrawGizmo(GizmoType.Selected)]
        static void Mirror(MirrorModifier m, GizmoType g)
        {
            if (!Live(m)) return;
            var t = m.mirrorObject ? m.mirrorObject : m.transform;
            var r = m.GetComponent<Renderer>();
            float s = r ? r.bounds.extents.magnitude : 1f;
            Gizmos.matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            Gizmos.color = Faint;
            if (m.x) Gizmos.DrawWireCube(Vector3.zero, new Vector3(0f, s, s) * 2f);
            if (m.y) Gizmos.DrawWireCube(Vector3.zero, new Vector3(s, 0f, s) * 2f);
            if (m.z) Gizmos.DrawWireCube(Vector3.zero, new Vector3(s, s, 0f) * 2f);
            Gizmos.matrix = Matrix4x4.identity;
        }

        [DrawGizmo(GizmoType.Selected)]
        static void UVProject(UVProjectModifier m, GizmoType g)
        {
            if (!Live(m) || !m.projector) return;
            Gizmos.matrix = m.projector.localToWorldMatrix;
            Gizmos.color = Orange;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1f / Mathf.Max(Mathf.Abs(m.scale.x), 1e-4f), 1f / Mathf.Max(Mathf.Abs(m.scale.y), 1e-4f), 0f));
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.5f); // projection direction
            Gizmos.matrix = Matrix4x4.identity;
        }

        [DrawGizmo(GizmoType.Selected)]
        static void SimpleDeform(SimpleDeformModifier m, GizmoType g)
        {
            if (!Live(m)) return;
            var t = m.origin ? m.origin : m.transform;
            var axis = m.axis == SimpleDeformModifier.Axis.X ? t.right : m.axis == SimpleDeformModifier.Axis.Y ? t.up : t.forward;
            var r = m.GetComponent<Renderer>();
            float s = r ? r.bounds.extents.magnitude : 1f;
            Gizmos.color = Orange;
            Gizmos.DrawLine(t.position - axis * s, t.position + axis * s);
        }
    }
}
