using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// Sabotages the streamer's own movement/jump input. Originally this patched
    /// UnityEngine.Input (the legacy Input Manager), but a live test of '!buy nojump' doing
    /// nothing led to inspecting the real Assembly-CSharp.dll: the game's PlayerMovementController
    /// doesn't call UnityEngine.Input at all - it reads a `move`/`jump` state off a custom
    /// MonoBehaviour (global, unnamespaced class called "InputSystem", wired to a
    /// UnityEngine.InputSystem.PlayerInput component) that the new Input System pushes into via
    /// OnJump/OnMove callbacks.
    ///
    /// A first attempt patched the small internal setter methods those callbacks call
    /// (JumpInput(bool)/MoveInput(Vector2)) directly, matching Unity's "StarterAssetsInputs"
    /// template convention - still confirmed live to do nothing. Most likely cause: IL2Cpp's AOT
    /// compiler frequently inlines short one-line internal calls like `jump = newJumpState;` at
    /// the native level, so a call from OnJump straight into JumpInput never actually passes
    /// through the managed interop shim Harmony patches - a known IL2Cpp modding gotcha, not
    /// something that shows up for methods invoked as real delegate/event targets.
    ///
    /// This now patches OnJump/OnMove themselves instead, which ARE guaranteed real call
    /// boundaries - Unity's Input System invokes them through an actual C# event subscription no
    /// matter which device (keyboard or gamepad) fired the action - and forces the resulting
    /// `jump`/`move` state back to what we want immediately afterward via the confirmed-public
    /// `jump`/`move` properties. That also makes this keyboard/controller-agnostic: it operates on
    /// the merged input state both device types feed into, not a specific physical key, so
    /// there's no need for (and no benefit to) something like an OS-level spacebar block, which
    /// would only ever affect keyboard players anyway. Still unverified against a real build until
    /// confirmed via a live log, same as everything else first guessed at in this mod.
    /// </summary>
    public static class PlayerInputSabotage
    {
        public static bool InvertControlsActive;
        public static bool JumpDisabledActive;

        public static void Apply(ManualLogSource log)
        {
            try
            {
                new Harmony("com.musicman0917.waterparksimtwitchexpansion.inputsabotage")
                    .PatchAll(typeof(PlayerInputSabotage));
                log.LogInfo("PlayerInputSabotage: Harmony patches applied.");
            }
            catch (Exception e)
            {
                log.LogError($"PlayerInputSabotage: failed to apply Harmony patches - invert/nojump won't do anything: {e}");
            }
        }

        [HarmonyPatch(typeof(global::InputSystem), nameof(global::InputSystem.OnJump))]
        [HarmonyPostfix]
        private static void OnJumpPostfix(global::InputSystem __instance)
        {
            if (JumpDisabledActive)
            {
                __instance.jump = false;
            }
        }

        [HarmonyPatch(typeof(global::InputSystem), nameof(global::InputSystem.OnMove))]
        [HarmonyPostfix]
        private static void OnMovePostfix(global::InputSystem __instance)
        {
            if (InvertControlsActive)
            {
                __instance.move = -__instance.move;
            }
        }
    }
}
