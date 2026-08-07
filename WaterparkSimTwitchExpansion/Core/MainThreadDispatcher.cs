using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// TwitchLib's chat client raises its events (OnMessageReceived, OnConnected, etc.) on a
    /// background thread. UnityEngine APIs (GameObject, Rigidbody, Instantiate, ...) can only be
    /// touched from Unity's main thread, so any chaos action triggered from a chat event must be
    /// queued here and drained from Plugin.Update().
    /// </summary>
    public sealed class MainThreadDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public void Enqueue(Action action)
        {
            if (action != null)
            {
                _queue.Enqueue(action);
            }
        }

        /// <summary>Call once per frame from Plugin.Update().</summary>
        public void ProcessQueue()
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[WaterparkSimTwitchExpansion] Queued action threw: {e}");
                }
            }
        }
    }
}
