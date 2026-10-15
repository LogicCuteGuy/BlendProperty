#if BLEND_UDON
using UdonSharp;
using VRC.SDKBase;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;
using UnityEngine.UI;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Makes a Control Panel or Cloth Panel shared: whoever moves a control takes ownership and its values reach every
    /// player, players who join later included. Put it next to the panel and hook each Toggle / Slider
    /// On Value Changed → SendCustomEvent "Changed" on this (instead of "Apply" on the panel).
    /// Toggles / Sliders empty: the Control Panel's own are used.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/ControlPanel.png")]
    [AddComponentMenu("Blend Property/Physics/Interaction/Synced Control Panel")]
    public class BlendUdonSyncedPanel : UdonSharpBehaviour
    {
        [Tooltip("A Control Panel or Cloth Panel.")]
        public UdonSharpBehaviour panel;
        public Toggle[] toggles;
        public Slider[] sliders;

        [UdonSynced] bool[] toggleValues = new bool[0];
        [UdonSynced] float[] sliderValues = new float[0];
        bool pulling;

        void Start()
        {
            if (panel == null) { enabled = false; return; }
            if ((toggles == null || toggles.Length == 0) && (sliders == null || sliders.Length == 0))
            {
                toggles = (Toggle[])panel.GetProgramVariable("toggles");
                sliders = (Slider[])panel.GetProgramVariable("sliders");
            }
            if (toggles == null) toggles = new Toggle[0];
            if (sliders == null) sliders = new Slider[0];
        }

        bool Ready() { return panel != null && toggles != null && sliders != null && (bool)panel.GetProgramVariable("ready"); }

        /// <summary>A control was moved: apply it here and send it to everyone.</summary>
        public void Changed()
        {
            if (pulling || !Ready()) return; // the panel setting itself up / us applying synced values
#if BLEND_UDON
            if (!Utilities.IsValid(Networking.LocalPlayer)) return;
            if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
            if (!Networking.IsOwner(gameObject)) return;
#endif
            panel.SendCustomEvent("Apply");
            if (toggleValues.Length != toggles.Length) toggleValues = new bool[toggles.Length];
            if (sliderValues.Length != sliders.Length) sliderValues = new float[sliders.Length];
            for (int i = 0; i < toggles.Length; i++) if (toggles[i] != null) toggleValues[i] = toggles[i].isOn;
            for (int i = 0; i < sliders.Length; i++) if (sliders[i] != null) sliderValues[i] = sliders[i].value;
#if BLEND_UDON
            RequestSerialization();
#endif
        }

        public override void OnDeserialization() { Pull(); }

        public void Pull()
        {
            if (panel == null) return;
            if (!Ready()) { SendCustomEventDelayedFrames(nameof(Pull), 1); return; } // late joiner: wait for the panel's Start
            if (toggleValues.Length != toggles.Length || sliderValues.Length != sliders.Length) return; // nothing sent yet
            pulling = true;
            for (int i = 0; i < toggleValues.Length; i++) if (toggles[i] != null) toggles[i].isOn = toggleValues[i];
            for (int i = 0; i < sliderValues.Length; i++) if (sliders[i] != null) sliders[i].value = sliderValues[i];
            pulling = false;
            panel.SendCustomEvent("Apply");
        }
    }
}
