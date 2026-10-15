#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    public enum BlendUdonForceType { Force, Wind, Vortex, Turbulence, Drag, Harmonic }

    /// <summary>
    /// Blender Force Field: Force (push away from / pull toward this object), Wind (along its up axis, Blender's Z),
    /// Vortex (spin around its up axis), Turbulence (swirling gusts), Drag (slows things down) or Harmonic (springs
    /// things back to this object). Strength is an acceleration in m/s², so it doesn't depend on mass.
    /// Falloff like Blender's: (1 + distance)^-power, nothing past Max Distance.
    /// Affects the cloths and fluids that list it in Force Fields and the Rigidbodies listed here.
    /// Move, rotate or animate the object to move the field. Turn the component off to stop it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/ForceField.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Force Field")]
    public class BlendUdonForceField : UdonSharpBehaviour
    {
        public BlendUdonForceType type;
        [Tooltip("m/s². Force: positive pushes away, negative pulls in. Drag: per m/s of speed. Harmonic: per metre from the centre.")]
        public float strength = 5f;
        [Min(0f), Tooltip("Falloff power (Blender Power): 0 = same everywhere, 2 = inverse square.")]
        public float power;
        [Min(0f), Tooltip("Metres. No effect beyond this. 0 = unlimited.")]
        public float maxDistance;
        [Min(0.01f), Tooltip("Turbulence: metres between gusts (Blender Size).")]
        public float size = 1f;
        [Min(0f), Tooltip("Drag: extra drag growing with speed (Blender Quadratic).")]
        public float quadratic;
        [Min(0f), Tooltip("Harmonic: how quickly the bouncing dies out (Blender Damping).")]
        public float damping = 1f;
        [Tooltip("Rigidbodies pushed by this field (in FixedUpdate). One with several colliders (e.g. an Array modifier's " +
                 "Collider Per Copy) is pushed at each collider, so it can also spin.")]
        public Rigidbody[] rigidbodies;

        Collider[][] parts; // per rigidbody: its solid colliders when it has more than one, else null

        // read by the cloths and fluids: (centre, type), (up axis, strength), (power, max distance, on, size / quadratic / damping)
        [HideInInspector] public Vector4 packA, packB, packC;

        void OnDisable() { packC = Vector4.zero; }

        void Update() { Pack(); }

        void Pack()
        {
            Vector3 c = transform.position, u = transform.up;
            float extra = type == BlendUdonForceType.Turbulence ? size : (type == BlendUdonForceType.Drag ? quadratic : damping);
            packA = new Vector4(c.x, c.y, c.z, (int)type);
            packB = new Vector4(u.x, u.y, u.z, strength);
            packC = new Vector4(power, maxDistance, 1f, extra);
        }

        void FixedUpdate()
        {
            if (rigidbodies == null) return;
            Pack();
            float t = Time.time;
            if (parts == null || parts.Length != rigidbodies.Length) FindParts();
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                Rigidbody rb = rigidbodies[i];
                if (rb == null || rb.isKinematic) continue;
                Collider[] cs = parts[i];
                if (cs == null) { rb.AddForce(Accel(rb.position, rb.velocity, t, packA, packB, packC), ForceMode.Acceleration); continue; }
                // compound body: same total acceleration, but applied where each part is
                float m = rb.mass / cs.Length;
                for (int k = 0; k < cs.Length; k++)
                {
                    if (cs[k] == null) continue;
                    Vector3 p = cs[k].bounds.center;
                    rb.AddForceAtPosition(Accel(p, rb.GetPointVelocity(p), t, packA, packB, packC) * m, p, ForceMode.Force);
                }
            }
        }

        void FindParts()
        {
            parts = new Collider[rigidbodies.Length][];
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                if (rigidbodies[i] == null) continue;
                Collider[] all = rigidbodies[i].GetComponentsInChildren<Collider>();
                int n = 0;
                for (int k = 0; k < all.Length; k++) if (!all[k].isTrigger) n++;
                if (n < 2) continue;
                Collider[] solid = new Collider[n];
                n = 0;
                for (int k = 0; k < all.Length; k++) if (!all[k].isTrigger) solid[n++] = all[k];
                parts[i] = solid;
            }
        }

        /// <summary>
        /// Acceleration at world point p moving at velocity v (m/s), at time t (for Turbulence), from one packed field.
        /// Same maths as BlendForceField.cginc.
        /// </summary>
        public static Vector3 Accel(Vector3 p, Vector3 v, float t, Vector4 a, Vector4 b, Vector4 c)
        {
            if (c.z < 0.5f) return Vector3.zero; // off
            Vector3 d = p - new Vector3(a.x, a.y, a.z);
            float dist = d.magnitude;
            if (c.y > 0f && dist > c.y) return Vector3.zero;
            float fall = c.x > 0f ? Mathf.Pow(1f + dist, -c.x) : 1f;
            float s = b.w * fall;
            if (a.w > 4.5f) return -(d * b.w + v * c.w) * fall;              // Harmonic
            if (a.w > 3.5f) return -v * ((b.w + c.w * v.magnitude) * fall);  // Drag
            if (a.w > 2.5f)                                                  // Turbulence
            {
                Vector3 q = d / Mathf.Max(c.w, 0.01f);
                return new Vector3(Mathf.Sin(q.y * 1.7f + t * 1.3f + Mathf.Sin(q.z * 2.3f + t)),
                                   Mathf.Sin(q.z * 1.9f + t * 1.1f + Mathf.Sin(q.x * 2.1f - t * 0.7f)),
                                   Mathf.Sin(q.x * 1.5f + t * 1.7f + Mathf.Sin(q.y * 2.5f + t * 0.9f))) * s;
            }
            Vector3 axis = new Vector3(b.x, b.y, b.z);
            if (a.w < 0.5f) return dist > 1e-6f ? d * (s / dist) : Vector3.zero; // Force
            if (a.w < 1.5f) return axis * s;                                     // Wind
            Vector3 tan = Vector3.Cross(axis, d);                                // Vortex
            float len = tan.magnitude;
            return len > 1e-6f ? tan * (s / len) : Vector3.zero;
        }
    }
}
