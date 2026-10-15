#if BLEND_UDON
using UdonSharp;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace BlendProperty.Udon
{
    /// <summary>
    /// In-world controls for any Blend Udon component, or for a modifier whose Runtime is on (set Modifier; field names
    /// are the modifier's, e.g. height, angle, strength): each Toggle drives a bool field,
    /// each Slider a float field (an int or enum field when the Slider has Whole Numbers on). Field names go in the
    /// matching slot of toggleFields / sliderFields. Leave Target empty and set Target Renderer instead to drive a
    /// shader (e.g. Blend Property/Wave): the names are then material properties and Toggles set 1 / 0.
    /// Hook Toggle / Slider On Value Changed → SendCustomEvent "Apply".
    /// A Text inside a Slider shows the Slider's name and value. Local only (not synced).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/ControlPanel.png")]
    [AddComponentMenu("Blend Property/Physics/Interaction/Control Panel")]
    public class BlendUdonPanel : UdonSharpBehaviour
    {
        public UdonSharpBehaviour target;
        [Tooltip("A Wave / Simple Deform / Lattice / Hook / Warp modifier with Runtime on. Used instead of Target.")]
        public Component modifier;
        [HideInInspector] public BlendUdonDeformStack deform; // set at build from Modifier
        [HideInInspector] public int deformOp;
        [Tooltip("Used when Target is empty: its material's properties are driven instead (this renderer only).")]
        public Renderer targetRenderer;
        public Toggle[] toggles;
        public string[] toggleFields;
        public Slider[] sliders;
        public string[] sliderFields;
        [Tooltip("Optional, per Slider: comma-separated names shown instead of the number (e.g. an enum: Twist,Bend,Taper,Stretch).")]
        public string[] sliderChoices;
        [Tooltip("Event sent to the target after every change, e.g. Deform. Empty: none.")]
        public string afterChange = "Deform";

        bool ready;
        Material mat;

        void Start()
        {
            if (deform != null)
            {
                for (int i = 0; i < toggles.Length; i++) toggles[i].isOn = deform.GetBool(deformOp, toggleFields[i]);
                for (int i = 0; i < sliders.Length; i++) sliders[i].value = deform.GetFloat(deformOp, sliderFields[i]);
                ready = true;
                Apply();
                return;
            }
            if (target == null)
            {
                if (targetRenderer == null) { enabled = false; return; }
                mat = targetRenderer.material;
            }
            // start the controls at the target's own settings
            for (int i = 0; i < toggles.Length; i++)
                toggles[i].isOn = mat != null ? mat.GetFloat(toggleFields[i]) > 0.5f : (bool)target.GetProgramVariable(toggleFields[i]);
            for (int i = 0; i < sliders.Length; i++)
            {
                Slider s = sliders[i];
                if (mat != null) s.value = mat.GetFloat(sliderFields[i]);
                else s.value = s.wholeNumbers ? (int)target.GetProgramVariable(sliderFields[i]) : (float)target.GetProgramVariable(sliderFields[i]);
            }
            ready = true;
            Apply();
        }

        /// <summary>Copy every control into the target.</summary>
        public void Apply()
        {
            if (!ready) return; // setting the controls in Start fires their events
            if (deform != null)
            {
                for (int i = 0; i < toggles.Length; i++) deform.SetBool(deformOp, toggleFields[i], toggles[i].isOn);
                for (int i = 0; i < sliders.Length; i++)
                {
                    Slider s = sliders[i];
                    deform.SetFloat(deformOp, sliderFields[i], s.wholeNumbers ? Mathf.Round(s.value) : s.value);
                    Text t = s.GetComponentInChildren<Text>();
                    if (t != null) t.text = s.name + "  " + Show(i, s);
                }
                deform.Deform();
                return;
            }
            for (int i = 0; i < toggles.Length; i++)
            {
                if (mat != null) mat.SetFloat(toggleFields[i], toggles[i].isOn ? 1f : 0f);
                else target.SetProgramVariable(toggleFields[i], toggles[i].isOn);
            }
            for (int i = 0; i < sliders.Length; i++)
            {
                Slider s = sliders[i];
                if (mat != null) mat.SetFloat(sliderFields[i], s.value);
                else if (s.wholeNumbers) target.SetProgramVariable(sliderFields[i], Mathf.RoundToInt(s.value));
                else target.SetProgramVariable(sliderFields[i], s.value);
                Text t = s.GetComponentInChildren<Text>();
                if (t != null) t.text = s.name + "  " + Show(i, s);
            }
            if (target != null && afterChange != "") target.SendCustomEvent(afterChange);
        }

        string Show(int i, Slider s)
        {
            if (sliderChoices != null && i < sliderChoices.Length && sliderChoices[i] != "")
            {
                string[] names = sliderChoices[i].Split(',');
                int k = Mathf.RoundToInt(s.value);
                if (k >= 0 && k < names.Length) return names[k];
            }
            return s.value.ToString(s.wholeNumbers ? "0" : "0.00");
        }
    }
}
