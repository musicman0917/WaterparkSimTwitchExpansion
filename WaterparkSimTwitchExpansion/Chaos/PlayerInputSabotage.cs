using System;
using BepInEx.Logging;
using HarmonyLib;

namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// Sabotages the streamer's own jump input. Originally this patched UnityEngine.Input (the
    /// legacy Input Manager), but a live test of '!buy nojump' doing nothing led to inspecting the
    /// real Assembly-CSharp.dll: the game's PlayerMovementController doesn't call
    /// UnityEngine.Input at all - it reads a `jump` state off a custom MonoBehaviour (global,
    /// unnamespaced class called "InputSystem", wired to a UnityEngine.InputSystem.PlayerInput
    /// component) that the new Input System pushes into via an OnJump callback.
    ///
    /// A first attempt patched the small internal setter method that callback calls
    /// (JumpInput(bool)), matching Unity's "StarterAssetsInputs" template convention - still
    /// confirmed live to do nothing. Most likely cause: IL2Cpp's AOT compiler frequently inlines
    /// short one-line internal calls like `jump = newJumpState;` at the native level, so a call
    /// from OnJump straight into JumpInput never actually passes through the managed interop shim
    /// Harmony patches - a known IL2Cpp modding gotcha, not something that shows up for methods
    /// invoked as real delegate/event targets.
    ///
    /// This now patches OnJump itself instead, which IS a guaranteed real call boundary - Unity's
    /// Input System invokes it through an actual C# event subscription no matter which device
    /// (keyboard or gamepad) fired the action - and forces the resulting `jump` state back to
    /// false immediately afterward via the confirmed-public `jump` property. That also makes this
    /// keyboard/controller-agnostic: it operates on the merged input state both device types feed
    /// into, not a specific physical key. Still unverified against a real build until confirmed
    /// via a live log, same as everything else first guessed at in this mod.
    ///
    /// '!buy invert' doesn't live here - the streamer pointed out the game already ships a real
    /// "Invert Y Axis (Player)" toggle in its own Settings menu, so ChaosController.InvertControls
    /// just flips that setting directly (SettingsManager.Data.Game.InvertMouseY) instead of
    /// patching anything, which sidesteps this whole class of IL2Cpp-inlining risk entirely.
    /// </summary>
    public static class PlayerInputSabotage
    {
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
                log.LogError($"PlayerInputSabotage: failed to apply Harmony patches - nojump won't do anything: {e}");
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
    }
}
