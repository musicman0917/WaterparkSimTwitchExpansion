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
        private const string PlayerTag = "Player";
        private const string PoolNameSubstring = "Pool";
        private const string WaterslideNameSubstring = "Slide";
        private const string PoopObjectNameSubstring = "Poop";

        // "Manager" skips singletons (e.g. "PoolManager"); "FX"/"Decal" skip visual-effect and
        // decal objects (e.g. "CleanPoolDirtFX", "FX_Pigeons_PoopAppear", "PoolDirtDecal") that
        // would otherwise false-match alongside real building/prop instances.
        private static readonly string[] NonInstanceNameHints = { "Manager", "FX", "Decal" };

        // Used by ScanMoney - see its doc comment for why this exists instead of a real
        // add/removemoney implementation.
        private static readonly string[] MoneyNameHints = { "Money", "Cash", "Bank", "Economy", "Finance", "Currency", "Wallet" };

        // Used by ScanPoop - see its doc comment. Not "Poo" alone: that substring also matches
        // "Pool", which would flood the results with every pool in the park.
        private static readonly string[] PoopNameHints = { "Poop", "Feces", "Turd" };

        private readonly ManualLogSource _log;
        private readonly System.Random _random = new System.Random();
        private readonly float _invertDurationSeconds;
        private readonly float _noJumpDurationSeconds;

        private float? _invertControlsUntil;
        private float? _jumpDisabledUntil;

        public ChaosController(ManualLogSource log, float invertDurationSeconds = 15f, float noJumpDurationSeconds = 15f)
        {
            _log = log;
            _invertDurationSeconds = invertDurationSeconds;
            _noJumpDurationSeconds = noJumpDurationSeconds;
        }

        /// <summary>Finds a random guest currently in view of the main camera and launches them into the air.</summary>
        public bool YeetGuest(float upForce = 1500f, float sidewaysForce = 300f)
        {
            var allGuests = GameObject.FindGameObjectsWithTag(GuestTag);
            if (allGuests.Length == 0)
            {
                _log.LogWarning($"YeetGuest: no GameObjects tagged '{GuestTag}' found.");
                return false;
            }

            var guests = FilterVisibleToCamera(allGuests);
            if (guests.Length == 0)
            {
                _log.LogWarning($"YeetGuest: {allGuests.Length} guest(s) found, but none are in view of the camera.");
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

        /// <summary>
        /// Drops a real poop object above a random pool. Confirmed live via !scanpoop: the game
        /// has real 'Poop'/'sm2_poop' objects (not our own invention). Resources.Load-by-path
        /// never worked regardless of path given, since this game preloads assets via
        /// Addressables labels, not a Resources folder - so this clones an existing live instance
        /// with Object.Instantiate instead, which needs no asset path at all.
        ///
        /// IMPORTANT: an earlier version of this cloned a 'Trash'-tagged object instead, which
        /// caused an infinite NullReferenceException spam in-game every frame. This game runs on
        /// Unity Netcode, and Trash items are spawned/tracked through it (see the constant
        /// "[Spawner] ... to SpawnerManager" log lines) - cloning a networked object with a plain
        /// Instantiate() (instead of properly spawning it through Netcode) leaves the clone in a
        /// broken half-initialized state that errors every frame forever. The 'Poop' objects
        /// found by !scanpoop are static props, not part of that spawner system, but
        /// TryCloneSafely still checks for a NetworkObject component before committing to a
        /// clone, so this can't repeat that failure regardless of what ends up matching.
        /// </summary>
        public bool SpawnPoop(float heightOffset = 0.5f)
        {
            var pools = FindByNameContains(PoolNameSubstring, NonInstanceNameHints);
            if (pools.Length == 0)
            {
                _log.LogWarning($"SpawnPoop: no GameObjects with '{PoolNameSubstring}' in their name found.");
                return false;
            }

            var poopTemplates = FindByNameContains(PoopObjectNameSubstring, NonInstanceNameHints);
            if (poopTemplates.Length == 0)
            {
                _log.LogWarning($"SpawnPoop: no GameObjects with '{PoopObjectNameSubstring}' in their name found to clone.");
                return false;
            }

            var template = poopTemplates[_random.Next(poopTemplates.Length)];
            var pool = pools[_random.Next(pools.Length)];
            var spawnPosition = pool.transform.position + Vector3.up * heightOffset;

            if (!TryCloneSafely(template, spawnPosition, out var reason))
            {
                _log.LogWarning($"SpawnPoop: couldn't clone '{template.name}' - {reason}");
                return false;
            }

            _log.LogInfo($"SpawnPoop: cloned '{template.name}' above '{pool.name}'.");
            return true;
        }

        /// <summary>
        /// Clones <paramref name="template"/> via Object.Instantiate, then immediately destroys
        /// the clone (returning false) if it turns out to carry a NetworkObject component - see
        /// SpawnPoop's doc comment for why. Detected by component type name rather than a real
        /// Unity.Netcode.NetworkObject type check, so this doesn't need a new assembly reference
        /// just for a safety net.
        /// </summary>
        private static bool TryCloneSafely(GameObject template, Vector3 position, out string failureReason)
        {
            var clone = UnityEngine.Object.Instantiate(template, position, Quaternion.identity);

            var isNetworked = clone.GetComponentsInChildren<Component>()
                .Any(c => c != null && c.GetType().Name == "NetworkObject");

            if (isNetworked)
            {
                UnityEngine.Object.Destroy(clone);
                failureReason = "it's a networked object (has a NetworkObject component) and can't be safely cloned this way.";
                return false;
            }

            failureReason = null;
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
            var slides = FindByNameContains(WaterslideNameSubstring, NonInstanceNameHints);
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

        /// <summary>Flings the streamer's own character around with a random impulse + torque.</summary>
        public bool RagdollPlayer(float upForce = 800f, float sidewaysForce = 600f)
        {
            var players = GameObject.FindGameObjectsWithTag(PlayerTag);
            if (players.Length == 0)
            {
                _log.LogWarning($"RagdollPlayer: no GameObjects tagged '{PlayerTag}' found.");
                return false;
            }

            var player = players[0];
            var rb = player.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = player.GetComponentInParent<Rigidbody>();
            }
            if (rb == null)
            {
                rb = player.GetComponentInChildren<Rigidbody>();
            }

            if (rb == null)
            {
                _log.LogWarning($"RagdollPlayer: '{player.name}' has no Rigidbody on itself, its parents, or its children - the player likely moves via CharacterController instead, which AddForce can't touch. Needs live investigation, same as the guest tag did.");
                return false;
            }

            var randomDirection = new Vector3(
                (float)(_random.NextDouble() * 2 - 1),
                0f,
                (float)(_random.NextDouble() * 2 - 1)).normalized;

            rb.AddForce(Vector3.up * upForce + randomDirection * sidewaysForce, ForceMode.Impulse);
            rb.AddTorque(randomDirection * sidewaysForce, ForceMode.Impulse);

            _log.LogInfo($"RagdollPlayer: sent '{rb.gameObject.name}' flying.");
            return true;
        }

        /// <summary>
        /// EXPERIMENTAL - see PlayerInputSabotage.cs. Reverses the streamer's movement input for a
        /// configured duration via a Harmony patch on UnityEngine.Input. Only works if the game
        /// still reads movement through the legacy Input Manager; unverified until tested live.
        /// </summary>
        public bool InvertControls()
        {
            _invertControlsUntil = Time.time + _invertDurationSeconds;
            PlayerInputSabotage.InvertControlsActive = true;
            _log.LogInfo($"InvertControls: active for {_invertDurationSeconds:0}s.");
            return true;
        }

        /// <summary>EXPERIMENTAL - see PlayerInputSabotage.cs. Same caveats as InvertControls.</summary>
        public bool DisableJump()
        {
            _jumpDisabledUntil = Time.time + _noJumpDurationSeconds;
            PlayerInputSabotage.JumpDisabledActive = true;
            _log.LogInfo($"DisableJump: active for {_noJumpDurationSeconds:0}s.");
            return true;
        }

        /// <summary>
        /// EXPERIMENTAL - see PlayerInputSabotage.cs. Simulates a single press of the configured
        /// "drop" key, hoping the game binds dropping a held item to a plain key. Unverified -
        /// adjust Config's PlayerSabotage.DropKeyCode to match the game's real binding if it does
        /// nothing.
        /// </summary>
        public bool DropItem()
        {
            PlayerInputSabotage.TriggerDrop();
            _log.LogInfo($"DropItem: simulated a '{PlayerInputSabotage.DropKeyCode}' press.");
            return true;
        }

        /// <summary>Call every frame (from Plugin.Tick) to auto-revert timed sabotage effects.</summary>
        public void TickSabotageTimers()
        {
            if (_invertControlsUntil.HasValue && Time.time >= _invertControlsUntil.Value)
            {
                PlayerInputSabotage.InvertControlsActive = false;
                _invertControlsUntil = null;
            }

            if (_jumpDisabledUntil.HasValue && Time.time >= _jumpDisabledUntil.Value)
            {
                PlayerInputSabotage.JumpDisabledActive = false;
                _jumpDisabledUntil = null;
            }
        }

        /// <summary>
        /// Diagnostic, not a real chaos action: we don't yet know what tracks the game's own
        /// in-park money (as opposed to this mod's separate Twitch-points economy), so
        /// "!buy addmoney"/"!buy removemoney" aren't implemented yet - guessing at an unknown
        /// internal field/class would just repeat the mistake "Guest" (vs. the real tag,
        /// "Visitor") already taught us to avoid. This walks the scene the same way ScanTags does,
        /// but flags any GameObject name OR component type name that looks money-related, so the
        /// real target can be identified from a live run before writing the actual mutation code.
        /// </summary>
        public bool ScanMoney()
        {
            return ScanByNameHints("ScanMoney", MoneyNameHints, "money/cash/bank/economy/finance/currency/wallet");
        }

        /// <summary>
        /// Diagnostic: there's apparently a real poop object/mechanic already in this game (per
        /// the streamer, not something we invented) - SpawnPoop currently works around not
        /// knowing what it's called by cloning a piece of litter instead (see its doc comment).
        /// This scans for the real thing by name so SpawnPoop can be pointed at it directly.
        /// </summary>
        public bool ScanPoop()
        {
            return ScanByNameHints("ScanPoop", PoopNameHints, "poop/feces/turd");
        }

        /// <summary>
        /// Shared by ScanMoney/ScanPoop: walks every GameObject in the scene (same as ScanTags)
        /// and flags any whose own name, or any attached component's type name, contains one of
        /// <paramref name="hints"/> - finding real names/types empirically instead of guessing at
        /// them, the same way the real "Visitor" tag was found instead of the guessed "Guest".
        /// </summary>
        private bool ScanByNameHints(string label, string[] hints, string hintsDescription)
        {
            var found = new List<string>();

            foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                var nameMatches = hints.Any(hint => go.name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
                var matchingComponents = go.GetComponents<Component>()
                    .Where(c => c != null && hints.Any(hint => c.GetType().Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(c => c.GetType().Name)
                    .ToArray();

                if (nameMatches || matchingComponents.Length > 0)
                {
                    found.Add(matchingComponents.Length > 0
                        ? $"'{go.name}' - components: {string.Join(", ", matchingComponents)}"
                        : $"'{go.name}' (name match only, no matching component type)");
                }
            }

            if (found.Count == 0)
            {
                _log.LogInfo($"{label}: nothing matching {hintsDescription} found by name.");
                return false;
            }

            _log.LogInfo($"{label}: {found.Count} candidate(s):");
            foreach (var line in found)
            {
                _log.LogInfo($"  {line}");
            }

            return true;
        }

        /// <summary>
        /// Filters to only the GameObjects currently within the main camera's view frustum (and
        /// not blocked by a wall/floor/etc. in between) - so "!buy yeet" launches someone the
        /// streamer can actually see happen, instead of a random guest off in an unwatched corner
        /// of the park.
        /// </summary>
        private GameObject[] FilterVisibleToCamera(GameObject[] candidates)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                // No camera found (shouldn't happen in normal play) - fall back to "everyone counts".
                _log.LogWarning("FilterVisibleToCamera: Camera.main is null - treating everyone as visible.");
                return candidates;
            }

            var cameraPosition = camera.transform.position;
            var visible = new List<GameObject>();
            var rejectedByFrustum = 0;
            var rejectedByOcclusion = 0;

            foreach (var go in candidates)
            {
                var viewportPoint = camera.WorldToViewportPoint(go.transform.position);
                var inFrustum = viewportPoint.z > 0f
                    && viewportPoint.x >= 0f && viewportPoint.x <= 1f
                    && viewportPoint.y >= 0f && viewportPoint.y <= 1f;

                if (!inFrustum)
                {
                    rejectedByFrustum++;
                    continue;
                }

                var toGuest = go.transform.position - cameraPosition;
                var distance = toGuest.magnitude;
                var direction = toGuest.normalized;

                // Start the ray a little ahead of the camera, not exactly at it - starting right
                // at the camera position risks immediately self-hitting the player's own body
                // collider (common for first-person cameras nested inside a capsule), which would
                // make every single guest look "occluded" regardless of the camera's real view.
                const float rayStartOffset = 0.3f;
                var rayStart = cameraPosition + direction * rayStartOffset;
                var rayDistance = Mathf.Max(distance - rayStartOffset, 0f);

                if (Physics.Raycast(rayStart, direction, out var hit, rayDistance))
                {
                    if (hit.transform == go.transform || hit.transform.IsChildOf(go.transform))
                    {
                        visible.Add(go);
                    }
                    else
                    {
                        rejectedByOcclusion++;
                    }
                }
                else
                {
                    visible.Add(go);
                }
            }

            if (visible.Count == 0 && candidates.Length > 0)
            {
                _log.LogWarning($"FilterVisibleToCamera: camera '{camera.name}' at {cameraPosition} - {candidates.Length} candidate(s): {rejectedByFrustum} outside the frustum, {rejectedByOcclusion} blocked by something else. If this keeps happening even outdoors, Camera.main is probably resolving to the wrong camera in this scene.");
            }

            return visible.ToArray();
        }

        /// <summary>
        /// Finds GameObjects whose name contains <paramref name="substring"/> (case-insensitive),
        /// optionally excluding names that also contain any of <paramref name="excludeSubstrings"/>
        /// - used to skip singleton/manager objects (e.g. "PoolManager") and visual-effect objects
        /// (e.g. "CleanPoolDirtFX") that would otherwise false-match alongside real instances
        /// (e.g. "0_PoolRectangleSmall(Clone)").
        /// </summary>
        private static GameObject[] FindByNameContains(string substring, string[] excludeSubstrings = null)
        {
            var query = UnityEngine.Object.FindObjectsOfType<GameObject>()
                .Where(go => go.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);

            if (excludeSubstrings != null)
            {
                query = query.Where(go => !excludeSubstrings.Any(exclude => go.name.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0));
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
