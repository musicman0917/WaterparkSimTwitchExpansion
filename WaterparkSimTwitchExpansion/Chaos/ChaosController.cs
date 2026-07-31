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
        private const string GuestTag = "Guest";
        private const string PoolTag = "Pool";
        private const string WaterslideTag = "Waterslide";
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
            var rb = guest.GetComponent<Rigidbody>();
            if (rb == null)
            {
                _log.LogWarning($"YeetGuest: '{guest.name}' has no Rigidbody, cannot yeet.");
                return false;
            }

            var sideways = new Vector3(
                (float)(_random.NextDouble() * 2 - 1),
                0f,
                (float)(_random.NextDouble() * 2 - 1)).normalized * sidewaysForce;

            rb.AddForce(Vector3.up * upForce + sideways, ForceMode.Impulse);
            _log.LogInfo($"YeetGuest: launched '{guest.name}'.");
            return true;
        }

        /// <summary>Spawns a poop prefab a little above a random pool.</summary>
        public bool SpawnPoop(float heightOffset = 0.5f)
        {
            var pools = GameObject.FindGameObjectsWithTag(PoolTag);
            if (pools.Length == 0)
            {
                _log.LogWarning($"SpawnPoop: no GameObjects tagged '{PoolTag}' found.");
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
            Object.Instantiate(prefab, spawnPosition, Quaternion.identity);

            _log.LogInfo($"SpawnPoop: spawned poop above '{pool.name}'.");
            return true;
        }

        /// <summary>Breaks a random waterslide - via IBreakable.Break() if present, otherwise a generic visual/functional disable.</summary>
        public bool SabotageSlide()
        {
            var slides = GameObject.FindGameObjectsWithTag(WaterslideTag);
            if (slides.Length == 0)
            {
                _log.LogWarning($"SabotageSlide: no GameObjects tagged '{WaterslideTag}' found.");
                return false;
            }

            var slide = slides[_random.Next(slides.Length)];

            var breakable = slide.GetComponentInChildren<IBreakable>();
            if (breakable != null)
            {
                breakable.Break();
                _log.LogInfo($"SabotageSlide: called Break() on '{slide.name}'.");
                return true;
            }

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
                _log.LogWarning($"SabotageSlide: '{slide.name}' has no IBreakable, MeshRenderer, or Collider to sabotage.");
                return false;
            }

            _log.LogInfo($"SabotageSlide: disabled renderer/collider on '{slide.name}' (no IBreakable found).");
            return true;
        }
    }
}
