using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// EXPERIMENTAL. Sabotages the streamer's own input by Harmony-patching UnityEngine.Input,
    /// so it doesn't need to know anything about the game's actual player-controller script. This
    /// only works if Waterpark Simulator still reads movement/jump through Unity's legacy Input
    /// Manager (Input.GetAxis/GetButton/GetKey) rather than the newer com.unity.inputsystem
    /// package - unverified until tested live, same as everything else that was first guessed at
    /// in this mod (e.g. the "Guest" tag turning out to actually be "Visitor"). If '!buy
    /// invert'/'!buy nojump'/'!buy drop' load without errors but visibly do nothing in-game,
    /// that's the most likely reason. The axis/button/key names below are just Unity's common
    /// defaults, not confirmed for this game - override them in the config's [PlayerSabotage]
    /// section if they turn out to be wrong.
    /// </summary>
    public static class PlayerInputSabotage
    {
        public static bool InvertControlsActive;
        public static bool JumpDisabledActive;
        private static bool _dropKeyPending;

        public static string HorizontalAxisName = "Horizontal";
        public static string VerticalAxisName = "Vertical";
        public static string JumpButtonName = "Jump";
        public static KeyCode JumpKeyCode = KeyCode.Space;
        public static KeyCode DropKeyCode = KeyCode.Q;

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
                log.LogError($"PlayerInputSabotage: failed to apply Harmony patches - invert/nojump/drop won't do anything: {e}");
            }
        }

        public static void TriggerDrop() => _dropKeyPending = true;

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis))]
        [HarmonyPostfix]
        private static void GetAxisPostfix(string axisName, ref float __result)
        {
            if (InvertControlsActive && (axisName == HorizontalAxisName || axisName == VerticalAxisName))
            {
                __result = -__result;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw))]
        [HarmonyPostfix]
        private static void GetAxisRawPostfix(string axisName, ref float __result)
        {
            if (InvertControlsActive && (axisName == HorizontalAxisName || axisName == VerticalAxisName))
            {
                __result = -__result;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetButton))]
        [HarmonyPostfix]
        private static void GetButtonPostfix(string buttonName, ref bool __result)
        {
            if (JumpDisabledActive && buttonName == JumpButtonName)
            {
                __result = false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetButtonDown))]
        [HarmonyPostfix]
        private static void GetButtonDownPostfix(string buttonName, ref bool __result)
        {
            if (JumpDisabledActive && buttonName == JumpButtonName)
            {
                __result = false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(KeyCode))]
        [HarmonyPostfix]
        private static void GetKeyPostfix(KeyCode key, ref bool __result)
        {
            if (JumpDisabledActive && key == JumpKeyCode)
            {
                __result = false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
        [HarmonyPostfix]
        private static void GetKeyDownPostfix(KeyCode key, ref bool __result)
        {
            if (JumpDisabledActive && key == JumpKeyCode)
            {
                __result = false;
                return;
            }

            // One-shot: simulate exactly one frame of the drop key being pressed, then clear the
            // flag so it doesn't look like the key is being held down forever.
            if (_dropKeyPending && key == DropKeyCode)
            {
                __result = true;
                _dropKeyPending = false;
            }
        }
    }
}
