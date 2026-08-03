using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// Injected MonoBehaviour that draws a short-lived line of text on screen for every chaos
    /// purchase, so viewers (and the streamer) can see what a redemption actually did without
    /// needing the BepInEx console open. Show() touches UnityEngine.Time, so only call it from
    /// Unity's main thread - i.e. after a Core.MainThreadDispatcher hop, same as ChaosController.
    /// </summary>
    public sealed class OnScreenNotifier : MonoBehaviour
    {
        private const float DisplaySeconds = 4f;
        private const int MaxLines = 5;

        private readonly List<(string Text, float ExpiresAt)> _messages = new List<(string, float)>();
        private GUIStyle _style;

        public OnScreenNotifier(IntPtr ptr) : base(ptr)
        {
        }

        public void Show(string message)
        {
            _messages.Add((message, Time.time + DisplaySeconds));
            if (_messages.Count > MaxLines)
            {
                _messages.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            if (_messages.Count == 0)
            {
                return;
            }

            _messages.RemoveAll(m => m.ExpiresAt <= Time.time);
            if (_messages.Count == 0)
            {
                return;
            }

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
                _style.normal.textColor = Color.white;
            }

            var y = 20f;
            foreach (var message in _messages)
            {
                GUI.Label(new Rect(20, y, 900, 30), message.Text, _style);
                y += 28f;
            }
        }
    }
}
