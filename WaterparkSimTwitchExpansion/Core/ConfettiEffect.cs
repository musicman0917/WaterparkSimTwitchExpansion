using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>
    /// On-screen confetti burst, meant as a fun/celebratory incentive for Extra Life donations
    /// (see Plugin.Load() wiring `_extraLifeTracker`'s celebrate callback to `Burst`) - not tied to
    /// any specific chaos action. Draws with the exact same technique as OnScreenNotifier's toasts
    /// and ModMenu's panel background - solid-color `Texture2D`-backed `GUIStyle`s drawn via
    /// `GUI.Label(Rect, string, GUIStyle)` - deliberately reusing only IMGUI calls already
    /// confirmed live in this build (see ModMenu's doc comment for the full history of which
    /// legacy IMGUI methods this particular IL2CPP build has actually stripped) rather than a
    /// Unity `ParticleSystem`/`GameObject` effect, which has no such live confirmation here and
    /// would be yet another untested API surface to potentially hit a stripped method on.
    ///
    /// No rotation, no per-particle fade (both would need extra untested API surface - `GUIUtility.
    /// RotateAroundPivot`/`GUI.color` respectively) - particles just fall in a straight line under
    /// a constant "gravity" and disappear outright once their lifetime elapses or they fall past
    /// the bottom of the screen. Still reads as confetti at a glance with enough particles/colors.
    /// </summary>
    public sealed class ConfettiEffect : MonoBehaviour
    {
        private const float GravityPerSecond = 600f;
        private const float MinLifetimeSeconds = 2.5f;
        private const float MaxLifetimeSeconds = 4f;
        private const float ParticleSize = 10f;
        private const int MaxParticles = 400; // Safety cap - a very large donation shouldn't be able to spawn an unbounded number of draw calls.

        private struct Particle
        {
            public float X;
            public float Y;
            public float VelocityX;
            public float VelocityY;
            public float Age;
            public float Lifetime;
            public int ColorIndex;
        }

        private ManualLogSource _log;
        private readonly List<Particle> _particles = new List<Particle>();
        private readonly System.Random _random = new System.Random();

        private GUIStyle[] _colorStyles;
        private bool _loggedDrawError;

        public ConfettiEffect(IntPtr ptr) : base(ptr)
        {
        }

        public void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Spawns a burst of confetti from the top of the screen. Safe to call from
        /// Unity's main thread only (touches Screen.width) - hop through MainThreadDispatcher first
        /// if calling from a background thread, same rule as OnScreenNotifier.Show.</summary>
        public void Burst(int count)
        {
            count = Mathf.Clamp(count, 0, MaxParticles - _particles.Count);
            var width = Screen.width;

            for (var i = 0; i < count; i++)
            {
                _particles.Add(new Particle
                {
                    X = (float)(_random.NextDouble() * width),
                    Y = -ParticleSize - (float)(_random.NextDouble() * 300),
                    VelocityX = (float)(_random.NextDouble() * 160 - 80),
                    VelocityY = 50f + (float)(_random.NextDouble() * 100),
                    Age = 0f,
                    Lifetime = MinLifetimeSeconds + (float)(_random.NextDouble() * (MaxLifetimeSeconds - MinLifetimeSeconds)),
                    ColorIndex = _random.Next(ColorCount),
                });
            }
        }

        private void Update()
        {
            if (_particles.Count == 0)
            {
                return;
            }

            var dt = Time.deltaTime;
            var height = Screen.height;

            for (var i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.VelocityY += GravityPerSecond * dt;
                p.X += p.VelocityX * dt;
                p.Y += p.VelocityY * dt;
                p.Age += dt;

                if (p.Age >= p.Lifetime || p.Y > height + ParticleSize)
                {
                    _particles.RemoveAt(i);
                }
                else
                {
                    _particles[i] = p;
                }
            }
        }

        private const int ColorCount = 7;

        private void EnsureStyles()
        {
            if (_colorStyles != null)
            {
                return;
            }

            Color[] colors =
            {
                new Color(0.95f, 0.2f, 0.25f),
                new Color(1f, 0.65f, 0.1f),
                new Color(1f, 0.9f, 0.15f),
                new Color(0.25f, 0.85f, 0.35f),
                new Color(0.2f, 0.6f, 1f),
                new Color(0.65f, 0.3f, 0.95f),
                new Color(1f, 0.4f, 0.75f),
            };

            _colorStyles = new GUIStyle[colors.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                var texture = new Texture2D(1, 1);
                texture.SetPixel(0, 0, colors[i]);
                texture.Apply();
                _colorStyles[i] = new GUIStyle { normal = { background = texture } };
            }
        }

        private void OnGUI()
        {
            if (_particles.Count == 0)
            {
                return;
            }

            EnsureStyles();

            try
            {
                foreach (var p in _particles)
                {
                    GUI.Label(new Rect(p.X, p.Y, ParticleSize, ParticleSize), string.Empty, _colorStyles[p.ColorIndex]);
                }
            }
            catch (Exception e)
            {
                if (!_loggedDrawError)
                {
                    _loggedDrawError = true;
                    _log?.LogError($"ConfettiEffect: draw threw - confetti will stop rendering: {e}");
                }
            }
        }
    }
}
