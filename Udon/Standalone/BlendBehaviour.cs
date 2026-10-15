#if !BLEND_UDON
using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlendProperty.Standalone
{
    // The shared components keep their script GUIDs and public event/field API in both project types.
    [UnityEngine.Scripting.Preserve]
    public abstract class BlendBehaviour : MonoBehaviour
    {
        protected void LateUpdate() { PostLateUpdate(); }
        public virtual void PostLateUpdate() { }
        public virtual void OnDeserialization() { }
        public virtual void OnPickupUseDown() { }
        public virtual void OnPickupUseUp() { }
        public virtual void OnDrop() { }
        public virtual void OnPlayerJoined(BlendPlayer player) { }
        public virtual void OnPlayerLeft(BlendPlayer player) { }
        public virtual void OnAsyncGpuReadbackComplete(BlendReadbackRequest request) { }

        public void SendCustomEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            var method = GetType().GetMethod(eventName, BindingFlags.Instance | BindingFlags.Public,
                null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void))
                throw new MissingMethodException(GetType().FullName, eventName);
            method.Invoke(this, null);
        }

        public object GetProgramVariable(string variableName) { return Field(variableName).GetValue(this); }
        public void SetProgramVariable(string variableName, object value) { Field(variableName).SetValue(this, value); }

        FieldInfo Field(string name)
        {
            var field = GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) throw new MissingFieldException(GetType().FullName, name);
            return field;
        }

        public void SendCustomEventDelayedFrames(string eventName, int frames)
        {
            StartCoroutine(Delayed(eventName, Mathf.Max(1, frames)));
        }

        IEnumerator Delayed(string eventName, int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
            SendCustomEvent(eventName);
        }
    }

    public readonly struct BlendReadbackRequest
    {
        readonly AsyncGPUReadbackRequest request;
        public BlendReadbackRequest(AsyncGPUReadbackRequest request) { this.request = request; }
        public bool hasError => request.hasError;

        public bool TryGetData(Color[] destination, int layer)
        {
            if (request.hasError || !request.done || destination == null || layer < 0 || layer >= request.layerCount) return false;
            var data = request.GetData<Color>(layer);
            if (destination.Length != data.Length) return false;
            data.CopyTo(destination);
            return true;
        }
    }

    public static class BlendReadback
    {
        public static void Request(Texture texture, int mip, TextureFormat format, BlendBehaviour receiver)
        {
            AsyncGPUReadback.Request(texture, mip, format, request =>
            {
                if (receiver) receiver.OnAsyncGpuReadbackComplete(new BlendReadbackRequest(request));
            });
        }
    }

    public sealed class BlendPlayer
    {
        public readonly Animator avatar;
        readonly Transform viewpoint;
        public readonly bool isLocal;
        public string displayName => avatar ? avatar.name : "Local Player";

        public BlendPlayer(Animator avatar, Transform viewpoint, bool isLocal)
        {
            this.avatar = avatar;
            this.viewpoint = viewpoint;
            this.isLocal = isLocal;
        }

        public bool IsValid() { return (avatar && avatar.gameObject.activeInHierarchy) || viewpoint; }

        public Vector3 GetBonePosition(HumanBodyBones bone)
        {
            var transform = avatar && avatar.isHuman ? avatar.GetBoneTransform(bone) : null;
            return transform ? transform.position : Vector3.zero;
        }

        public enum TrackingDataType { Head }
        public struct TrackingData { public Vector3 position; }
        public TrackingData GetTrackingData(TrackingDataType type)
        {
            return new TrackingData { position = viewpoint ? viewpoint.position : GetBonePosition(HumanBodyBones.Head) };
        }

        public float GetAvatarEyeHeightAsMeters()
        {
            var head = GetTrackingData(TrackingDataType.Head).position;
            return avatar && head != Vector3.zero ? Mathf.Abs(head.y - avatar.transform.position.y) : 1.6f;
        }
    }

    // Sync metadata is ignored by the native, local components.
    public enum BehaviourSyncMode { None, NoVariableSync, Manual, Continuous }
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class UdonBehaviourSyncModeAttribute : Attribute
    {
        public UdonBehaviourSyncModeAttribute(BehaviourSyncMode mode) { }
    }
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class UdonSyncedAttribute : Attribute { }
}
#endif
