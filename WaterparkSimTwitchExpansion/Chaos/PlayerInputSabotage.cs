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
    /// UnityEngine.InputSystem.PlayerInput component) that gets pushed to by the new Input System's
    /// generated callbacks (OnJump/OnMove -> JumpInput(bool)/MoveInput(Vector2)). So the legacy
    /// patches were never being consulted. This now patches those two setter methods directly,
    /// which is the same choke point Unity's own "StarterAssetsInputs" template uses upstream -
    /// still unverified against a real build until the streamer confirms via a live log, same as
    /// everything else first guessed at in this mod.
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

        [HarmonyPatch(typeof(global::InputSystem), nameof(global::InputSystem.JumpInput))]
        [HarmonyPrefix]
        private static void JumpInputPrefix(ref bool newJumpState)
        {
            if (JumpDisabledActive)
            {
                newJumpState = false;
            }
        }

        [HarmonyPatch(typeof(global::InputSystem), nameof(global::InputSystem.MoveInput))]
        [HarmonyPrefix]
        private static void MoveInputPrefix(ref Vector2 newMoveDirection)
        {
            if (InvertControlsActive)
            {
                newMoveDirection = -newMoveDirection;
            }
        }
    }
}
