using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace WaterparkSimTwitchExpansion.Chaos
{
    /// <summary>
    /// Actual gameplay effects. Every public method here touches UnityEngine objects directly,
    /// so it must ONLY ever be called from Unity's main thread - e.g. from Plugin.Update() after
    /// a queued action is dequeued by Core.MainThreadDispatcher. Never call these straight from
    /// a TwitchLib event handler.
    /// </summary>
    public sealed class ChaosController
    {
        // Confirmed via the in-game !scantags diagnostic: the whole scene only has 6 tags in
        // use (CharacterModel, Ground, MainCamera, Player, Trash, Visitor) - "Guest" never
        // existed. Pools and waterslides aren't tagged at all; the game tracks them through its
        // own "Building" system instead, so those two are found by object name instead (see
        // FindByNameContains below), matching real building instance names observed in-game
        // like "0_PoolRectangleSmall(Clone)" and "3_Slide_Modular_Pirate".
        private const string GuestTag = "Visitor";
        private const string PoolNameSubstring = "Pool";
        private const string WaterslideNameSubstring = "Slide";
        private const string PoopPrefabPath = "Prefabs/Interactables/Poop";

        private readonly ManualLogSource _log;
        private readonly System.Random _random = new System.Random();

        public ChaosController(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>Finds a random guest and launches them into the air.</summary>
        public bool YeetGuest(float upForce = 1500f, float sidewaysForce = 300f)
        {
            var guests = GameObject.FindGameObjectsWithTag(GuestTag);
            if (guests.Length == 0)
            {
                _log.LogWarning($"YeetGuest: no GameObjects tagged '{GuestTag}' found.");
                return false;
            }

            var guest = guests[_random.Next(guests.Length)];

            // The Visitor tag sits on sub-components (e.g. "LegsWaterChecker") rather than the
            // character root, so the Rigidbody is more likely to be found on a parent than on
            // the tagged object itself.
            var rb = guest.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = guest.GetComponentInParent<Rigidbody>();
            }

            if (rb == null)
            {
                _log.LogWarning($"YeetGuest: '{guest.name}' has no Rigidbody on itself or any parent, cannot yeet.");
                return false;
            }

            var sideways = new Vector3(
                (float)(_random.NextDouble() * 2 - 1),
                0f,
                (float)(_random.NextDouble() * 2 - 1)).normalized * sidewaysForce;

            rb.AddForce(Vector3.up * upForce + sideways, ForceMode.Impulse);
            _log.LogInfo($"YeetGuest: launched '{rb.gameObject.name}' (found via tagged child '{guest.name}').");
            return true;
        }

        /// <summary>Spawns a poop prefab a little above a random pool.</summary>
        public bool SpawnPoop(float heightOffset = 0.5f)
        {
            var pools = FindByNameContains(PoolNameSubstring, excludeSubstring: "Manager");
            if (pools.Length == 0)
            {
                _log.LogWarning($"SpawnPoop: no GameObjects with '{PoolNameSubstring}' in their name found.");
                return false;
            }

            var prefab = Resources.Load<GameObject>(PoopPrefabPath);
            if (prefab == null)
            {
                _log.LogError($"SpawnPoop: prefab not found at Resources/{PoopPrefabPath}.");
                return false;
            }

            var pool = pools[_random.Next(pools.Length)];
            var spawnPosition = pool.transform.position + Vector3.up * heightOffset;
            UnityEngine.Object.Instantiate(prefab, spawnPosition, Quaternion.identity);

            _log.LogInfo($"SpawnPoop: spawned poop above '{pool.name}'.");
            return true;
        }

        /// <summary>
        /// Breaks a random waterslide. Falls back to a generic visual/functional disable since
        /// there's no way to hook the game's own "break" behavior generically here: Il2CppInterop's
        /// GetComponent&lt;T&gt;() requires T : Il2CppObjectBase, so a plain C# interface (like a
        /// hypothetical IBreakable) can't be looked up this way under IL2CPP. To call a real
        /// game-specific break method, find the actual component type in the generated interop
        /// assembly (BepInEx\interop\Assembly-CSharp.dll, once decompiled/inspected) and call
        /// slide.GetComponent&lt;TheRealType&gt;()?.Break() directly.
        /// </summary>
        public bool SabotageSlide()
        {
            var slides = FindByNameContains(WaterslideNameSubstring, excludeSubstring: "Manager");
            if (slides.Length == 0)
            {
                _log.LogWarning($"SabotageSlide: no GameObjects with '{WaterslideNameSubstring}' in their name found.");
                return false;
            }

            var slide = slides[_random.Next(slides.Length)];

            var disabledSomething = false;

            var renderer = slide.GetComponentInChildren<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = false;
                disabledSomething = true;
            }

            var slideCollider = slide.GetComponentInChildren<Collider>();
            if (slideCollider != null)
            {
                slideCollider.enabled = false;
                disabledSomething = true;
            }

            if (!disabledSomething)
            {
                _log.LogWarning($"SabotageSlide: '{slide.name}' has no MeshRenderer or Collider to sabotage.");
                return false;
            }

            _log.LogInfo($"SabotageSlide: disabled renderer/collider on '{slide.name}'.");
            return true;
        }

        /// <summary>
        /// Finds GameObjects whose name contains <paramref name="substring"/> (case-insensitive),
        /// optionally excluding names that also contain <paramref name="excludeSubstring"/> - used
        /// to skip singleton/manager objects (e.g. "PoolManager") that would otherwise false-match
        /// alongside real instances (e.g. "0_PoolRectangleSmall(Clone)").
        /// </summary>
        private static GameObject[] FindByNameContains(string substring, string excludeSubstring = null)
        {
            var query = UnityEngine.Object.FindObjectsOfType<GameObject>()
                .Where(go => go.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);

            if (excludeSubstring != null)
            {
                query = query.Where(go => go.name.IndexOf(excludeSubstring, StringComparison.OrdinalIgnoreCase) < 0);
            }

            return query.ToArray();
        }

        /// <summary>
        /// One-off diagnostic: dumps every distinct GameObject tag currently in use in the scene,
        /// with a few example object names for each. Kept around (rather than removed now that the
        /// real tags are confirmed) since it's generally useful for finding tags/names for future
        /// chaos actions without guessing.
        /// </summary>
        public bool ScanTags()
        {
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var byTag = new Dictionary<string, List<string>>();

            foreach (var go in allObjects)
            {
                var tag = go.tag;
                if (string.IsNullOrEmpty(tag) || tag == "Untagged")
                {
                    continue;
                }

                if (!byTag.TryGetValue(tag, out var names))
                {
                    names = new List<string>();
                    byTag[tag] = names;
                }

                if (names.Count < 3)
                {
                    names.Add(go.name);
                }
            }

            _log.LogInfo($"ScanTags: {allObjects.Length} GameObjects in scene, {byTag.Count} distinct tag(s) in use:");
            foreach (var tag in byTag.Keys.OrderBy(t => t))
            {
                _log.LogInfo($"  '{tag}': e.g. {string.Join(", ", byTag[tag])}");
            }

            return true;
        }
    }
}
