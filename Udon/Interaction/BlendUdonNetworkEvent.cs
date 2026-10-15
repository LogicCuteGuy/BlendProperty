#if BLEND_UDON
using UdonSharp;
using VRC.Udon.Common.Interfaces;
#else
using BlendProperty.Standalone;
using UdonSharpBehaviour = BlendProperty.Standalone.BlendBehaviour;
#endif
using UnityEngine;

namespace BlendProperty.Udon
{
    /// <summary>
    /// Sends an event to Target on every player's machine, e.g. ClearPaint, ResetWaves or ResetSimulation, so each
    /// player's local simulation resets together. Hook a Button's On Click → SendCustomEvent "Send" on this.
    /// Players who join later don't get it (they start fresh anyway).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    [Icon("Packages/com.logiccuteguy.blendproperty/Editor/Icons/Physics/ControlPanel.png")]
    [AddComponentMenu("Blend Property/Physics/Interaction/Network Event")]
    public class BlendUdonNetworkEvent : UdonSharpBehaviour
    {
        public UdonSharpBehaviour target;
        public string eventName = "ResetSimulation";

        public void Send()
        {
#if BLEND_UDON
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Run));
#else
            Run();
#endif
        }

        public void Run() { if (target != null) target.SendCustomEvent(eventName); }
    }
}
