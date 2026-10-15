#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    public enum BlendPaintTool { Brush, Pen, Airbrush, Stamp }

    /// <summary>
    /// Blender Dynamic Paint brush (Proximity): paints every BlendUdonDynamicPaint canvas that lists it, within
    /// Radius of the tip. Put it on a pickup to paint by hand, or on anything that moves.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/DynamicPaintBrush.png")]
    [AddComponentMenu("Blend Property/Physics/Simulation/Dynamic Paint Brush")]
    public class BlendUdonPaintBrush : UdonSharpBehaviour
    {
        [Header("Tool")]
        public BlendPaintTool tool;
        [Tooltip("Optional nib position and orientation. Texture projects along its local Z, with local Y pointing up.")]
        public Transform tip;
        public Color color = Color.red;
        [Min(0.001f), Tooltip("Metres from this object's position.")]
        public float radius = 0.1f;
        [Range(0, 1), Tooltip("Paint added per update at the centre (Blender Alpha).")]
        public float strength = 1f;
        [Range(0, 1), Tooltip("0 = hard edge, 1 = fades all the way from the centre (Blender Falloff: Smooth).")]
        public float softness = 0.5f;
        [Tooltip("Remove paint instead of adding it (Blender Erase Paint).")]
        public bool erase;
        [Range(0, 1), Tooltip("Opacity of each application, multiplied by Color alpha and Pressure.")]
        public float opacity = 1f;
        [Range(0, 1), Tooltip("Runtime input for a trigger, tablet or UI. 0 lifts the pen; 1 applies full paint.")]
        public float pressure = 1f;

        [Header("Texture Tip")]
        [Tooltip("Optional brush image. By default white/opaque paints and black/transparent leaves gaps. Read/Write is not required.")]
        public Texture2D tipTexture;
        [Tooltip("Stamp the texture's RGB tinted by Color, using its alpha as the mask. Off uses grayscale times alpha as the mask.")]
        public bool useTextureColor;
        [Range(-180, 180), Tooltip("Rotate the image around the tip's local Z axis.")]
        public float angle;

        [Header("Stroke")]
        public bool drawing = true;
        [Tooltip("Only draw while holding Use on a VRC Pickup on this object.")]
        public bool requireUse;
        [System.NonSerialized] public bool useHeld;
        [System.NonSerialized] public int strokeId;

        public void _StartDrawing() { drawing = true; strokeId++; }
        public void _StopDrawing() { drawing = false; strokeId++; }
        public override void OnPickupUseDown() { useHeld = true; strokeId++; }
        public override void OnPickupUseUp() { useHeld = false; strokeId++; }
        public override void OnDrop() { useHeld = false; strokeId++; }
        void OnDisable() { useHeld = false; strokeId++; }
    }
}
