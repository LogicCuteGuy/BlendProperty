using UnityEngine;

namespace BlendProperty.Example
{
    /// <summary>
    /// Standalone runtime demo. Needs Keep at Runtime on the Modifier Stack.
    /// With an Array modifier: cycles its count every <see cref="interval"/> seconds.
    /// With a Simple Deform modifier: swings its angle every frame.
    /// Each change is followed by RebuildNow(). Compiled only in projects without the VRChat SDK.
    /// </summary>
    [RequireComponent(typeof(BlendModifierStack))]
    public class RuntimeRebuildDemo : MonoBehaviour
    {
        public int minCount = 1, maxCount = 5;
        public float interval = 1f;
        public float maxAngle = 120f;

        BlendModifierStack stack;
        ArrayModifier array;
        SimpleDeformModifier deform;
        float next;

        void Start()
        {
            stack = GetComponent<BlendModifierStack>();
            array = GetComponent<ArrayModifier>();
            deform = GetComponent<SimpleDeformModifier>();
        }

        void Update()
        {
            bool changed = false;
            if (array && Time.time >= next)
            {
                next = Time.time + interval;
                array.count = array.count >= maxCount ? minCount : array.count + 1;
                changed = true;
            }
            if (deform)
            {
                deform.angle = Mathf.Sin(Time.time) * maxAngle;
                changed = true;
            }
            if (changed) stack.RebuildNow();
        }
    }
}
