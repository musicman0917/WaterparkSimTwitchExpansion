using System;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// BepInEx.Unity.IL2CPP plugins derive from BasePlugin, which only gets a one-time Load() -
    /// there is no built-in per-frame callback like Mono's BaseUnityPlugin.Update(). To get one,
    /// inject this MonoBehaviour into the scene (Plugin.Load() calls AddComponent&lt;UpdatePump&gt;())
    /// and wire its OnUpdate action.
    ///
    /// The IntPtr constructor is required by Il2CppInterop for every injected MonoBehaviour -
    /// IL2CPP constructs instances from a native pointer, never via the parameterless .ctor.
    /// </summary>
    public sealed class UpdatePump : MonoBehaviour
    {
        public UpdatePump(IntPtr ptr) : base(ptr)
        {
        }

        public Action OnUpdate;

        private void Update() => OnUpdate?.Invoke();
    }
}
