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
    /// In-world controls for one BlendUdonCloth: Simulate toggle, Reset button and sliders.
    /// Hook the UI events to this behaviour: Toggle / Slider On Value Changed → SendCustomEvent "Apply",
    /// Button On Click → SendCustomEvent "ResetCloth". Any control left empty is skipped. A Text inside a slider
    /// shows its name and value. Local only (not synced).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/ClothPanel.png")]
    [AddComponentMenu("Blend Property/Physics/Interaction/Cloth Panel")]
    public class BlendUdonClothPanel : UdonSharpBehaviour
    {
        [Tooltip("The cloth simulation controlled by this panel.")]
        public UdonSharpBehaviour cloth;
        public Toggle simulate;
        [Tooltip("Downward gravity, m/s².")]
        public Slider gravity;
        [Tooltip("Wind along the cloth's forward axis, m/s².")]
        public Slider wind;
        public Slider airDamping, structural, bending, goalStiffness, collisionDistance;
        public Slider substeps;

        bool ready;

        void Start()
        {
            if (cloth == null) { enabled = false; return; }
            // start the controls at the cloth's own settings
            if (simulate != null) simulate.isOn = (bool)cloth.GetProgramVariable("simulate");
            if (gravity != null) gravity.value = -((Vector3)cloth.GetProgramVariable("gravity")).y;
            if (wind != null) wind.value = Vector3.Dot((Vector3)cloth.GetProgramVariable("wind"), cloth.transform.forward);
            Load(airDamping, "airDamping");
            Load(structural, "structural");
            Load(bending, "bending");
            Load(goalStiffness, "goalStiffness");
            Load(collisionDistance, "collisionDistance");
            LoadInt(substeps, "substeps");
            ready = true;
            Apply();
        }

        void Load(Slider s, string field) { if (s != null) s.value = (float)cloth.GetProgramVariable(field); }
        void Store(Slider s, string field) { if (s != null) cloth.SetProgramVariable(field, s.value); }
        void LoadInt(Slider s, string field) { if (s != null) s.value = (int)cloth.GetProgramVariable(field); }
        void StoreInt(Slider s, string field) { if (s != null) cloth.SetProgramVariable(field, Mathf.Max(1, Mathf.RoundToInt(s.value))); }

        /// <summary>Copy every control into the cloth.</summary>
        public void Apply()
        {
            if (!ready) return; // setting the controls in Start fires their events
            if (simulate != null) cloth.SetProgramVariable("simulate", simulate.isOn);
            if (gravity != null) cloth.SetProgramVariable("gravity", new Vector3(0f, -gravity.value, 0f));
            if (wind != null) cloth.SetProgramVariable("wind", cloth.transform.forward * wind.value);
            Store(airDamping, "airDamping");
            Store(structural, "structural");
            Store(bending, "bending");
            Store(goalStiffness, "goalStiffness");
            Store(collisionDistance, "collisionDistance");
            StoreInt(substeps, "substeps");
            Label(gravity, "Gravity");
            Label(wind, "Wind");
            Label(airDamping, "Air Damping");
            Label(structural, "Structural");
            Label(bending, "Bending");
            Label(goalStiffness, "Goal Stiffness");
            Label(collisionDistance, "Collision Distance");
            Label(substeps, "Substeps");
        }

        void Label(Slider s, string name)
        {
            if (s == null) return;
            Text t = s.GetComponentInChildren<Text>();
            if (t != null) t.text = name + "  " + s.value.ToString(s.wholeNumbers ? "0" : (s.maxValue < 0.5f ? "0.000" : "0.00"));
        }

        public void ResetCloth() { cloth.SendCustomEvent("ResetSimulation"); }
    }
}
