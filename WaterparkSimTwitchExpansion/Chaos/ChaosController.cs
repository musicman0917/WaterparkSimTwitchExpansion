using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Logging;
using CayplayAI;
using Unity.Netcode;
using UnityEngine;
using WaterparkSimTwitchExpansion.Core;

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
        // FindByNameContains below). A live "!scan pool" dump found 196 GameObjects containing
        // "Pool", of which only 4 were real placed buildings - all 4 ending in "(Clone)" (what
        // Unity appends to anything Instantiate()'d from a prefab at runtime), e.g.
        // "0_PoolRectangleSmall(Clone)" - so real instances are now identified by that suffix
        // rather than blacklisting the other 192 false-positives one at a time.
        private const string GuestTag = "Visitor";
        private const string PlayerTag = "Player";
        private const string PoolNameSubstring = "Pool";
        private const string WaterslideNameSubstring = "Slide";
        private const string PoopObjectNameSubstring = "Poop";

        // Keeps growing as new false-matches turn up live - "Manager" skips singletons (e.g.
        // "PoolManager"); "FX"/"Decal" skip visual-effect/decal objects (e.g. "CleanPoolDirtFX",
        // "FX_Pigeons_PoopAppear", "PoolDirtDecal"); "Spawner"/"Plug"/"Convex" skip spawn markers
        // and raw collision meshes (e.g. "_DecorOldAttraction_Pool_1_Spawner", "Convex_PoolPlug",
        // "Convex_Pool") - none of these are real placed building instances, just things that
        // happen to share the name. "Convex_Pool" is also suspected of having caused a hard
        // engine crash right after SpawnPoop targeted it live (no C# exception was logged, just
        // the process going silent - consistent with spawning geometry at a degenerate collision
        // mesh's transform) - see HasSanePosition below for the general-purpose backstop for
        // whatever the next one of these turns out to be. "Decor" skips decorative scenery that
        // still happens to end in "(Clone)" - e.g. "_DecorOldAttraction_Pool_1/2/3(Clone)", which
        // passed the "(Clone)" check but the streamer confirmed sits in an inactive/unused area of
        // the map, not a real player-built pool. The game's own "[Building] Client: Building
        // built: 'X(Clone)'" log line is the actual source of truth for real placed buildings (it
        // never mentions these) but isn't something this mod currently hooks into.
        private static readonly string[] NonInstanceNameHints = { "Manager", "FX", "Decal", "Spawner", "Plug", "Convex", "Decor" };

        // Used by ScanMoney - see its doc comment for why this exists instead of a real
        // add/removemoney implementation.
        private static readonly string[] MoneyNameHints = { "Money", "Cash", "Bank", "Economy", "Finance", "Currency", "Wallet" };

        // Used by ScanPoop - see its doc comment. Not "Poo" alone: that substring also matches
        // "Pool", which would flood the results with every pool in the park.
        private static readonly string[] PoopNameHints = { "Poop", "Feces", "Turd" };

        // Used only when picking a poop template to clone (not the shared NonInstanceNameHints,
        // since "(Clone)" is exactly how real pool/slide *instances* are identified elsewhere).
        // "(Clone)" excludes our own previously-spawned poops, so a long stream doesn't end up
        // cloning clones of clones; "_LOD" collapses "sm2_poop_LOD0/1/2" (three near-duplicate
        // objects for the same model at different detail levels) down to the one base "sm2_poop"
        // object, so they don't dilute real variety between genuinely different-looking props.
        private static readonly string[] PoopTemplateExcludeHints = { "FX", "(Clone)", "_LOD" };

        private readonly ManualLogSource _log;
        private readonly MainThreadDispatcher _dispatcher;
        private readonly System.Random _random = new System.Random();
        private readonly float _invertDurationSeconds;
        private readonly float _noJumpDurationSeconds;
        private readonly float _poopLifetimeSeconds;
        private readonly float _yeetUpForce;
        private readonly float _yeetSidewaysForce;
        private readonly float _ragdollUpForce;
        private readonly float _ragdollSidewaysForce;
        private readonly float _addMoneyAmount;
        private readonly float _removeMoneyAmount;

        private float? _invertControlsUntil;
        private float? _jumpDisabledUntil;

        // Captured right before InvertControls flips GamePersistentSettings.InvertMouseY, so
        // TickSabotageTimers can restore whatever the streamer actually had it set to (rather than
        // assuming it was always off) once the effect expires.
        private bool? _originalInvertMouseY;

        /// <param name="dispatcher">Used only by TrySpawnRealPoop to hop back onto Unity's main
        /// thread after Task.Delay(PoopLifetimeSeconds) to call NetworkObject.Despawn() - a real
        /// networked spawn can't be torn down with a plain delayed Object.Destroy() the way the
        /// name-matched fallback clones are.</param>
        public ChaosController(
            ManualLogSource log,
            MainThreadDispatcher dispatcher,
            float invertDurationSeconds = 15f,
            float noJumpDurationSeconds = 15f,
            float poopLifetimeSeconds = 90f,
            float yeetUpForce = 500f,
            float yeetSidewaysForce = 150f,
            float ragdollUpForce = 250f,
            float ragdollSidewaysForce = 150f,
            float addMoneyAmount = 5000f,
            float removeMoneyAmount = 5000f)
        {
            _log = log;
            _dispatcher = dispatcher;
            _invertDurationSeconds = invertDurationSeconds;
            _noJumpDurationSeconds = noJumpDurationSeconds;
            _poopLifetimeSeconds = poopLifetimeSeconds;
            _yeetUpForce = yeetUpForce;
            _yeetSidewaysForce = yeetSidewaysForce;
            _ragdollUpForce = ragdollUpForce;
            _ragdollSidewaysForce = ragdollSidewaysForce;
            _addMoneyAmount = addMoneyAmount;
            _removeMoneyAmount = removeMoneyAmount;
        }

        /// <summary>
        /// Finds a random guest currently in view of the main camera and launches them into the
        /// air. Forces are configurable (Config's Chaos.YeetUpForce/YeetSidewaysForce) since the
        /// original defaults (1500/300) sent guests flying far enough to land off the
        /// NavMesh, which the game then silently despawns ("Failed to create agent because it is
        /// not close enough to the NavMesh" in the log right after a yeet) - the guest should fly,
        /// not vanish.
        /// </summary>
        /// <param name="npcName">Set to the yeeted guest's in-game name (e.g. for the overlay/chat
        /// reply to say who got yeeted) whenever this returns true; null on failure.</param>
        public bool YeetGuest(out string npcName)
        {
            npcName = null;

            var guest = FindRandomVisibleGuestInPark("YeetGuest");
            if (guest == null)
            {
                return false;
            }

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
                (float)(_random.NextDouble() * 2 - 1)).normalized * _yeetSidewaysForce;

            rb.AddForce(Vector3.up * _yeetUpForce + sideways, ForceMode.Impulse);
            npcName = GetVisitorDisplayName(guest.GetComponentInParent<AIBrain>(), rb.gameObject);
            _log.LogInfo($"YeetGuest: launched '{rb.gameObject.name}' (found via tagged child '{guest.name}'), display name '{npcName}'.");
            return true;
        }

        /// <summary>
        /// Triggers a random visible in-park guest's own vomiting behavior via
        /// AIBrain.TryToPuke(bool ignoreCooldown) - found the same way as the poop/yeet fixes, by
        /// decoding Assembly-CSharp.dll's metadata. Passes ignoreCooldown=true so a paid chaos
        /// action always visibly does something instead of sometimes silently no-opping because
        /// the AI's own internal cooldown hasn't elapsed. Unlike SpawnPoop's saga, this doesn't
        /// spawn/clone anything ourselves - it calls a method on an already-alive, already-spawned
        /// guest, the same way the game itself triggers this when an NPC naturally gets sick.
        /// </summary>
        public bool MakeGuestVomit(out string npcName) => TryAIBrainAction("MakeGuestVomit", brain => brain.TryToPuke(true), out npcName);

        /// <summary>
        /// Triggers a random visible in-park guest's own peeing behavior via
        /// AIBrain.StartPeeing(). See MakeGuestVomit's doc comment for the general approach/safety
        /// reasoning - same deal here, just a different AIBrain method.
        /// </summary>
        public bool MakeGuestPee(out string npcName) => TryAIBrainAction("MakeGuestPee", brain => brain.StartPeeing(), out npcName);

        /// <summary>
        /// Makes a random visible in-park guest litter via AIBrain.TrySpawnTrash() - the same
        /// method the game itself calls when a guest naturally drops trash. See MakeGuestVomit's
        /// doc comment for the general approach/safety reasoning.
        /// </summary>
        public bool MakeGuestLitter(out string npcName) => TryAIBrainAction("MakeGuestLitter", brain => brain.TrySpawnTrash(), out npcName);

        /// <summary>
        /// Shared by MakeGuestVomit/MakeGuestPee/MakeGuestLitter: finds a random visible in-park
        /// guest, resolves its AIBrain, and invokes <paramref name="action"/> on it inside a
        /// try/catch (a native IL2CPP call misbehaving shouldn't be able to take down the plugin).
        /// </summary>
        private bool TryAIBrainAction(string actionLabel, Action<AIBrain> action, out string npcName)
        {
            npcName = null;

            var guest = FindRandomVisibleGuestInPark(actionLabel);
            if (guest == null)
            {
                return false;
            }

            var brain = guest.GetComponentInParent<AIBrain>();
            if (brain == null)
            {
                _log.LogWarning($"{actionLabel}: '{guest.name}' has no AIBrain on itself or any parent.");
                return false;
            }

            try
            {
                action(brain);
            }
            catch (Exception e)
            {
                _log.LogWarning($"{actionLabel}: AIBrain call threw: {e.Message}");
                return false;
            }

            npcName = GetVisitorDisplayName(brain, guest);
            _log.LogInfo($"{actionLabel}: triggered on '{npcName}'.");
            return true;
        }

        /// <summary>
        /// Shared by YeetGuest/MakeGuestVomit/MakeGuestPee/MakeGuestLitter: finds every GameObject
        /// tagged Visitor, excludes background city pedestrians (see IsInPark), and filters down to
        /// ones currently visible to the camera (see FilterVisibleToCamera) - so chat sees the
        /// chaos happen on stream instead of it landing on someone off in an unwatched corner of
        /// the park. Returns null (after logging why) if no candidate survives any of these steps.
        /// </summary>
        private GameObject FindRandomVisibleGuestInPark(string actionLabel)
        {
            var allGuests = GameObject.FindGameObjectsWithTag(GuestTag);
            if (allGuests.Length == 0)
            {
                _log.LogWarning($"{actionLabel}: no GameObjects tagged '{GuestTag}' found.");
                return null;
            }

            // Background city pedestrians on the sidewalk outside the park apparently also carry
            // the Visitor tag - exclude anything nested under "StaticCityLayout" (confirmed as the
            // background city's root object name via other objects' scene hierarchy paths in the
            // log, e.g. "StaticCityLayout/City/Near City/..."), leaving only real park guests
            // (presumably under something like "DynamicParkLayout"). Best-effort and permissive by
            // default (an object with no "StaticCityLayout" ancestor counts as in-park) so a wrong
            // guess just risks occasionally still targeting a pedestrian, not breaking the action
            // entirely if the assumption turns out wrong - run "!scan visitor" (now logs each
            // match's full hierarchy path) to check the real ancestry if pedestrians keep getting
            // hit.
            var parkGuests = allGuests.Where(IsInPark).ToArray();
            if (parkGuests.Length == 0)
            {
                _log.LogWarning($"{actionLabel}: {allGuests.Length} guest(s) found, but all are outside the park (under StaticCityLayout) - this assumption may be wrong, run \"!scan visitor\" to check.");
                return null;
            }

            var guests = FilterVisibleToCamera(parkGuests);
            if (guests.Length == 0)
            {
                _log.LogWarning($"{actionLabel}: {parkGuests.Length} in-park guest(s) found, but none are in view of the camera.");
                return null;
            }

            return guests[_random.Next(guests.Length)];
        }

        /// <summary>
        /// Best-effort "name" for a targeted guest, for the overlay/chat reply to say who got
        /// yeeted/sick/etc. AIBrain.OnNameChanged(FixedString64Bytes oldName, FixedString64Bytes
        /// newName) - found by decoding Assembly-CSharp.dll's metadata - confirms visitors have a
        /// real networked name, and AIBrain.ToString() is overridden (presumably to include it), so
        /// try that first. A live log showed ToString()'s actual format is a debug dump, e.g.
        /// `[AIBrain Visitor Teen Male///ID770 "Braylen" visitor state=InteractWithAttraction
        /// target=0_DivingBoard(Clone) netId=770]` - the real name is the quoted token, so pull
        /// that out with a regex instead of announcing the whole debug string to chat. Falls back
        /// to the quoted match's absence (uses the full ToString(), better than nothing) or to the
        /// fallback object's own name (minus the "(Clone)" suffix) if no AIBrain is found or its
        /// ToString() comes back empty - this is a display-only fallback, not something that
        /// should ever block a chaos action succeeding.
        /// </summary>
        private static readonly Regex VisitorNameFromToString = new Regex("\"([^\"]+)\"");

        private static string GetVisitorDisplayName(AIBrain brain, GameObject fallbackObject)
        {
            try
            {
                var brainName = brain?.ToString();
                if (!string.IsNullOrWhiteSpace(brainName))
                {
                    var match = VisitorNameFromToString.Match(brainName);
                    return match.Success ? match.Groups[1].Value : brainName;
                }
            }
            catch (Exception)
            {
                // Read-only display lookup - never let this take down a successful chaos action.
            }

            return fallbackObject.name.Replace("(Clone)", string.Empty).Trim();
        }

        /// <summary>
        /// Drops a poop object above a random pool. Tries the game's own spawn machinery first
        /// (TrySpawnRealPoop) and falls back to cloning a static Poop/sm2_poop prop (found live via
        /// !scanpoop) if that isn't available or fails for any reason.
        ///
        /// IMPORTANT: an earlier version of this raw-cloned the game's own
        /// ToiletInteraction.PoopPrefab via Object.Instantiate (found by decompiling BepInEx's
        /// interop-generated Assembly-CSharp.dll with ILSpy). It compiled and cloned successfully
        /// by name in a live test, but its interactive script(s) (PoopInteractable et al.) expect
        /// the game's own spawn lifecycle to initialize them, and a raw Instantiate() caused the
        /// exact same infinite per-frame NullReferenceException freeze as the 'Trash' incident
        /// below - except TryCloneSafely's NetworkObject check didn't catch it that time. Parsing
        /// Assembly-CSharp.dll's actual metadata (type hierarchy + method signatures, decoded
        /// directly from the ECMA-335 tables) explained why: PoopInteractable extends
        /// TrashInteractable extends ... extends NetworkBehaviour, and has a `wasTakenFromPool`
        /// field - it's designed to come from the game's own PooledSpawnSystem
        /// (SpawnObject/GetPooledObject), never a bare Instantiate(). TrySpawnRealPoop now goes
        /// through that same system instead of cloning the prefab ourselves.
        ///
        /// IMPORTANT (older lesson, same root cause): an earlier version of this cloned a
        /// 'Trash'-tagged object instead, which also caused an infinite NullReferenceException
        /// spam in-game every frame. This game runs on Unity Netcode, and Trash items are
        /// spawned/tracked through it (see the constant "[Spawner] ... to SpawnerManager" log
        /// lines) - cloning a networked object with a plain Instantiate() (instead of properly
        /// spawning it through Netcode) leaves the clone in a broken half-initialized state that
        /// errors every frame forever. TryCloneSafely (used by the fallback path below) checks for
        /// a NetworkObject component before committing to a clone as defense in depth, but as the
        /// PoopPrefab incident showed, that only catches the Netcode-specific case, not every way a
        /// real game script can misbehave when cloned outside its intended spawn path - which is
        /// exactly why the fallback only ever clones plain decorative props, never real scripted
        /// objects.
        /// </summary>
        public bool SpawnPoop(float heightOffset = 0.5f)
        {
            var pools = FindByNameContains(PoolNameSubstring, NonInstanceNameHints, requireCloneSuffix: true)
                .Where(HasSanePosition)
                .ToArray();
            if (pools.Length == 0)
            {
                _log.LogWarning($"SpawnPoop: no GameObjects with '{PoolNameSubstring}' in their name found.");
                return false;
            }

            var pool = pools[_random.Next(pools.Length)];
            var spawnPosition = pool.transform.position + Vector3.up * heightOffset;

            if (TrySpawnRealPoop(spawnPosition, out var realFailureReason))
            {
                _log.LogInfo($"SpawnPoop: spawned the real PoopPrefab above '{pool.name}' via PooledSpawnSystem (despawns in {_poopLifetimeSeconds:0}s).");
                return true;
            }

            _log.LogInfo($"SpawnPoop: real spawn unavailable ({realFailureReason}), falling back to a static prop.");

            var template = FindFallbackPoopTemplate();
            if (template == null)
            {
                _log.LogWarning("SpawnPoop: no poop props found by name (run !scanpoop).");
                return false;
            }

            if (!TryCloneSafely(template, spawnPosition, out var clone, out var reason))
            {
                _log.LogWarning($"SpawnPoop: couldn't clone '{template.name}' - {reason}");
                return false;
            }

            UnityEngine.Object.Destroy(clone, _poopLifetimeSeconds);
            _log.LogInfo($"SpawnPoop: cloned '{template.name}' above '{pool.name}' (despawns in {_poopLifetimeSeconds:0}s).");
            return true;
        }

        /// <summary>
        /// Spawns the game's own PoopPrefab through PooledSpawnSystem.SpawnObject - the same
        /// pooled Netcode-spawn path the game itself uses (found by decoding Assembly-CSharp.dll's
        /// method signatures: PooledSpawnSystem.SpawnObject(GameObject prefab, Vector3 position,
        /// Quaternion rotation) returns a Unity.Netcode.NetworkObject). Requires: a toilet placed
        /// in the park (so ToiletInteraction.PoopPrefab has something to read), a live
        /// PooledSpawnSystem, and that prefab already being registered with it - if any of those
        /// aren't true, or the call throws for any reason, this returns false and SpawnPoop falls
        /// back to a static prop instead of risking a repeat of the raw-Instantiate freeze.
        /// </summary>
        private bool TrySpawnRealPoop(Vector3 position, out string failureReason)
        {
            var prefab = GetRealPoopPrefab();
            if (prefab == null)
            {
                failureReason = "no ToiletInteraction.PoopPrefab found (build a toilet?)";
                return false;
            }

            var spawnSystem = UnityEngine.Object.FindObjectOfType<PooledSpawnSystem>();
            if (spawnSystem == null)
            {
                failureReason = "no PooledSpawnSystem found in the scene";
                return false;
            }

            if (!spawnSystem.IsPrefabRegistered(prefab))
            {
                failureReason = $"'{prefab.name}' is not registered with PooledSpawnSystem";
                return false;
            }

            NetworkObject spawned;
            try
            {
                spawned = spawnSystem.SpawnObject(prefab, position, Quaternion.identity);
            }
            catch (Exception e)
            {
                failureReason = $"PooledSpawnSystem.SpawnObject threw: {e.Message}";
                return false;
            }

            if (spawned == null)
            {
                failureReason = "PooledSpawnSystem.SpawnObject returned null";
                return false;
            }

            // A plain delayed Object.Destroy() isn't safe for a properly-spawned networked object
            // (Netcode expects Despawn() first) - schedule the real teardown on a background timer
            // and hop back onto Unity's main thread through the same dispatcher chat commands use.
            Task.Delay(TimeSpan.FromSeconds(_poopLifetimeSeconds)).ContinueWith(_ =>
            {
                _dispatcher.Enqueue(() =>
                {
                    if (spawned != null && spawned.IsSpawned)
                    {
                        spawned.Despawn(true);
                    }
                });
            });

            failureReason = null;
            return true;
        }

        /// <summary>
        /// Reads PoopPrefab off any live ToiletInteraction in the park - the same prefab the
        /// game's own toilet-accident mechanic uses. Iterates rather than just taking the first
        /// instance in case a particular toilet's field is somehow unset.
        /// </summary>
        private static GameObject GetRealPoopPrefab()
        {
            foreach (var toilet in UnityEngine.Object.FindObjectsOfType<ToiletInteraction>())
            {
                var prefab = toilet.PoopPrefab;
                if (prefab != null)
                {
                    return prefab;
                }
            }

            return null;
        }

        /// <summary>
        /// Clone one of the static Poop/sm2_poop props found live via !scanpoop. Distinct by name
        /// - "Poop" exists as two separate instances in the park (same model, so they shouldn't
        /// double its odds of being picked), while "sm2_poop" is a genuinely different-looking
        /// model and should get an equal shot. Also requires an actual Renderer: a live "!scan
        /// poop" dump found one 'Poop' object sitting at the origin with nothing but a Transform -
        /// cloning that one would spawn something completely invisible.
        /// </summary>
        private GameObject FindFallbackPoopTemplate()
        {
            var candidates = FindByNameContains(PoopObjectNameSubstring, PoopTemplateExcludeHints)
                .Where(go => go.GetComponentInChildren<Renderer>() != null)
                .GroupBy(go => go.name)
                .Select(group => group.First())
                .ToArray();

            return candidates.Length == 0 ? null : candidates[_random.Next(candidates.Length)];
        }

        /// <summary>
        /// Clones <paramref name="template"/> via Object.Instantiate, then immediately destroys
        /// the clone (returning false) if it turns out to carry a NetworkObject component - see
        /// SpawnPoop's doc comment for why. Detected by component type name rather than a real
        /// Unity.Netcode.NetworkObject type check, so this doesn't need a new assembly reference
        /// just for a safety net.
        /// </summary>
        private static bool TryCloneSafely(GameObject template, Vector3 position, out GameObject clone, out string failureReason)
        {
            clone = UnityEngine.Object.Instantiate(template, position, Quaternion.identity);

            var isNetworked = clone.GetComponentsInChildren<Component>()
                .Any(c => c != null && c.GetType().Name == "NetworkObject");

            if (isNetworked)
            {
                UnityEngine.Object.Destroy(clone);
                clone = null;
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
        ///
        /// Also requires the "(Clone)" suffix, same reasoning as SpawnPoop's pool matching - a
        /// live scan proved this conclusively for pools, but there's no equivalent "!scan slide"
        /// dump confirming it for slides yet (this mostly just disables an existing renderer/
        /// collider rather than spawning new geometry, so the worst case if this assumption is
        /// wrong is "no slides found" rather than anything dangerous - but run "!scan slide" to
        /// confirm if this stops finding any).
        /// </summary>
        public bool SabotageSlide()
        {
            var slides = FindByNameContains(WaterslideNameSubstring, NonInstanceNameHints, requireCloneSuffix: true);
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
        /// Flings the streamer's own character around with a random impulse + torque. A raw
        /// Rigidbody.AddForce/AddTorque was confirmed live to do nothing - the player moves via
        /// CharacterController, which AddForce can't touch - so this instead calls
        /// `PlayerRagdollSystem.EnableRagdollTemp(Vector3 forceVector, Vector3 torqueVector)`
        /// directly (found by decoding Assembly-CSharp.dll's metadata), almost certainly the same
        /// method the game's own double-tap-jump ragdoll control calls. Confirmed working live,
        /// but the original 800/600 defaults flung the streamer high enough to clear map
        /// barriers and get stuck outside the playable area - RagdollUpForce/RagdollSidewaysForce
        /// default lower now; tune further in the config if it's still too much (or not enough).
        /// </summary>
        public bool RagdollPlayer()
        {
            var players = GameObject.FindGameObjectsWithTag(PlayerTag);
            if (players.Length == 0)
            {
                _log.LogWarning($"RagdollPlayer: no GameObjects tagged '{PlayerTag}' found.");
                return false;
            }

            var player = players[0];
            var ragdoll = player.GetComponent<global::PlayerRagdollSystem>()
                ?? player.GetComponentInParent<global::PlayerRagdollSystem>()
                ?? player.GetComponentInChildren<global::PlayerRagdollSystem>();

            if (ragdoll == null)
            {
                _log.LogWarning($"RagdollPlayer: '{player.name}' has no PlayerRagdollSystem on itself, its parents, or its children.");
                return false;
            }

            var randomDirection = new Vector3(
                (float)(_random.NextDouble() * 2 - 1),
                0f,
                (float)(_random.NextDouble() * 2 - 1)).normalized;

            try
            {
                ragdoll.EnableRagdollTemp(Vector3.up * _ragdollUpForce + randomDirection * _ragdollSidewaysForce, randomDirection * _ragdollSidewaysForce);
                _log.LogInfo($"RagdollPlayer: called PlayerRagdollSystem.EnableRagdollTemp() on '{player.name}'.");
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning($"RagdollPlayer: EnableRagdollTemp() threw: {e}");
                return false;
            }
        }

        /// <summary>
        /// Flips the game's own "Invert Y Axis (Player)" accessibility setting
        /// (SettingsManager.Data.Game.InvertMouseY) rather than patching anything - the streamer
        /// pointed out this exists as a real toggle already in the game's Settings menu, and given
        /// nojump needed two attempts to find a Harmony patch point IL2Cpp wouldn't inline away,
        /// reusing a setting the game already applies correctly itself is far more reliable than a
        /// third patch guess. Flips relative to whatever the streamer's own preference already was
        /// (captured in _originalInvertMouseY) rather than assuming it starts off, and
        /// TickSabotageTimers restores that exact original value when the effect expires -
        /// ApplyCameraSystemSettings() is called both times to push the change live immediately,
        /// the same method the in-game Settings UI itself uses, without ever calling
        /// CommitSettings()/SaveSettings() so this never gets written to the streamer's real save
        /// file. Unverified until tested live, same as everything else in this mod.
        /// </summary>
        public bool InvertControls()
        {
            var settingsManager = global::SettingsManager.Instance;
            if (settingsManager == null)
            {
                _log.LogWarning("InvertControls: SettingsManager.Instance is null.");
                return false;
            }

            // SettingsManager.Data is static (not an instance member, despite Instance existing
            // too) - confirmed live via CS0176 after first writing this as settingsManager.Data.
            var settingsData = global::SettingsManager.Data;
            var gameSettings = settingsData == null ? null : settingsData.Game;
            if (gameSettings == null)
            {
                _log.LogWarning("InvertControls: SettingsManager.Data.Game is null.");
                return false;
            }

            if (!_originalInvertMouseY.HasValue)
            {
                _originalInvertMouseY = gameSettings.InvertMouseY;
            }

            gameSettings.InvertMouseY = !_originalInvertMouseY.Value;
            settingsManager.ApplyCameraSystemSettings();

            _invertControlsUntil = Time.time + _invertDurationSeconds;
            _log.LogInfo($"InvertControls: set InvertMouseY to {gameSettings.InvertMouseY} for {_invertDurationSeconds:0}s (was {_originalInvertMouseY.Value}).");
            return true;
        }

        /// <summary>
        /// See PlayerInputSabotage.cs. First patched UnityEngine.Input (confirmed live to do
        /// nothing - wrong API family entirely), then InputSystem.JumpInput (also confirmed live
        /// to do nothing - likely inlined away by IL2Cpp's AOT compiler), now patches
        /// InputSystem.OnJump instead, the actual call boundary the new Input System invokes
        /// through a real delegate. Confirmed working live.
        /// </summary>
        public bool DisableJump()
        {
            _jumpDisabledUntil = Time.time + _noJumpDurationSeconds;
            PlayerInputSabotage.JumpDisabledActive = true;
            _log.LogInfo($"DisableJump: active for {_noJumpDurationSeconds:0}s.");
            return true;
        }

        /// <summary>
        /// Calls the player's own InventorySystem.DropItem() directly - found the same way the
        /// vomit/pee/trash AIBrain methods were (decoding the real DLL metadata rather than
        /// guessing). Replaces the old approach of simulating a keypress via
        /// PlayerInputSabotage, which relied on UnityEngine.Input and turned out to be the wrong
        /// input layer entirely for this game (see PlayerInputSabotage.cs) - calling the real
        /// method directly sidesteps that whole class of bug, the same way SpawnPoop's real-spawn
        /// path is safer than simulating input ever was.
        /// </summary>
        public bool DropItem()
        {
            var players = GameObject.FindGameObjectsWithTag(PlayerTag);
            if (players.Length == 0)
            {
                _log.LogWarning($"DropItem: no GameObjects tagged '{PlayerTag}' found.");
                return false;
            }

            var player = players[0];
            var inventory = player.GetComponent<global::InventorySystem>()
                ?? player.GetComponentInParent<global::InventorySystem>()
                ?? player.GetComponentInChildren<global::InventorySystem>();

            if (inventory == null)
            {
                _log.LogWarning($"DropItem: '{player.name}' has no InventorySystem on itself, its parents, or its children.");
                return false;
            }

            try
            {
                inventory.DropItem();
                _log.LogInfo("DropItem: called InventorySystem.DropItem() on the player.");
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning($"DropItem: InventorySystem.DropItem() threw: {e}");
                return false;
            }
        }

        /// <summary>Call every frame (from Plugin.Tick) to auto-revert timed sabotage effects.</summary>
        public void TickSabotageTimers()
        {
            if (_invertControlsUntil.HasValue && Time.time >= _invertControlsUntil.Value)
            {
                var settingsManager = global::SettingsManager.Instance;
                var settingsData = global::SettingsManager.Data;
                var gameSettings = settingsData == null ? null : settingsData.Game;
                if (settingsManager != null && gameSettings != null && _originalInvertMouseY.HasValue)
                {
                    gameSettings.InvertMouseY = _originalInvertMouseY.Value;
                    settingsManager.ApplyCameraSystemSettings();
                }

                _originalInvertMouseY = null;
                _invertControlsUntil = null;
            }

            if (_jumpDisabledUntil.HasValue && Time.time >= _jumpDisabledUntil.Value)
            {
                PlayerInputSabotage.JumpDisabledActive = false;
                _jumpDisabledUntil = null;
            }
        }

        /// <summary>
        /// Diagnostic, kept for reference: this is how the real money tracker (below) was
        /// actually found - not by this scan (which never got a live run before the real answer
        /// turned up by decoding Assembly-CSharp.dll's metadata directly, same technique as every
        /// other real-method call in this file), but the same name-hint approach it uses is worth
        /// keeping around for the next unknown system that comes up.
        /// </summary>
        public bool ScanMoney()
        {
            return ScanByNameHints("ScanMoney", MoneyNameHints, "money/cash/bank/economy/finance/currency/wallet");
        }

        /// <summary>
        /// Adds/removes the game's own in-park money (not this mod's separate Twitch-points
        /// economy) via `FinanceSystem.ForceChangeMoney`. Found by decoding
        /// `Assembly-CSharp.dll`'s metadata: `GameManager` (a `NetworkBehaviour` with a static
        /// `Instance`) exposes `FinanceSystem` (itself a `NetworkBehaviour`, no static `Instance`
        /// of its own), which has `ChangeMoney(float, MoneyChangeReason)` (returns false and
        /// no-ops if it would take the park negative and spending isn't allowed) and
        /// `ForceChangeMoney(float, MoneyChangeReason)` (always applies, no return value). Uses
        /// the latter - a paid `!buy removemoney` should always visibly do something instead of
        /// silently failing once the park's already broke, same "always do something" reasoning as
        /// `MakeGuestVomit`'s `ignoreCooldown: true`. Passes `MoneyChangeReason.Cheats`, the enum
        /// value the game itself reserves for exactly this kind of external/debug change, so it's
        /// correctly categorized in the in-game finance report rather than miscounted as real
        /// ticket/attraction income.
        /// </summary>
        private bool ChangeParkMoney(float amount, string actionLabel)
        {
            var gameManager = global::GameManager.Instance;
            var financeSystem = gameManager == null ? null : gameManager.FinanceSystem;
            if (financeSystem == null)
            {
                _log.LogWarning($"{actionLabel}: GameManager.Instance.FinanceSystem is null.");
                return false;
            }

            try
            {
                financeSystem.ForceChangeMoney(amount, global::MoneyChangeReason.Cheats);
                _log.LogInfo($"{actionLabel}: changed park money by {amount}.");
                return true;
            }
            catch (Exception e)
            {
                _log.LogWarning($"{actionLabel}: ForceChangeMoney threw: {e}");
                return false;
            }
        }

        public bool AddMoney() => ChangeParkMoney(_addMoneyAmount, "AddMoney");

        public bool RemoveMoney() => ChangeParkMoney(-_removeMoneyAmount, "RemoveMoney");

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
        /// General-purpose diagnostic: logs EVERY GameObject whose name contains
        /// <paramref name="substring"/> (case-insensitive), with its tag, position, and full
        /// component list - not just a curated set of hints like ScanMoney/ScanPoop. Built after
        /// repeatedly discovering "Pool"/"Slide" name-match false-positives one at a time, live,
        /// often only after something broke (CleanPoolDirtFX, PoolDirtDecal, a Spawner marker, a
        /// PoolPlug collider, and finally Convex_Pool, which is suspected of freezing the game
        /// outright). Run "!scan pool" (or "!scan slide", "!scan <anything>") to see every match
        /// up front in one pass instead of finding the next bad one the hard way.
        /// </summary>
        public bool Scan(string substring)
        {
            if (string.IsNullOrWhiteSpace(substring))
            {
                _log.LogInfo("Scan: usage '!scan <term>', e.g. '!scan pool'.");
                return false;
            }

            var matches = UnityEngine.Object.FindObjectsOfType<GameObject>()
                .Where(go => go.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            if (matches.Length == 0)
            {
                _log.LogInfo($"Scan: nothing matching '{substring}' found by name.");
                return false;
            }

            _log.LogInfo($"Scan: {matches.Length} GameObject(s) matching '{substring}':");
            foreach (var go in matches)
            {
                var componentNames = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name);

                var suspectFlag = HasSanePosition(go) ? "" : " [SUSPECT POSITION]";
                _log.LogInfo($"  '{go.name}' tag={go.tag} pos={go.transform.position}{suspectFlag} path={GetHierarchyPath(go)} components: {string.Join(", ", componentNames)}");
            }

            return true;
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
                    // Compare whole-character roots, not just the tagged object itself: the
                    // Visitor tag often sits on a small sub-part (e.g. "LegsWaterChecker") rather
                    // than the character root (see YeetGuest's own Rigidbody lookup), so a ray
                    // aimed at that sub-part's position almost always hits some other collider on
                    // the same character's body first - which used to get miscounted as "blocked
                    // by something else" even in plain view. A live "!buy yeet" dump found exactly
                    // this: 53/116 candidates rejected as occluded, 0 ever visible.
                    if (hit.transform.root == go.transform.root)
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
        /// optionally excluding names that also contain any of <paramref name="excludeSubstrings"/>,
        /// optionally requiring the name to end with "(Clone)" - what Unity automatically appends
        /// to anything Instantiate()'d from a prefab at runtime, which is exactly how this game
        /// places buildings. A live "!scan pool" dump found 196 GameObjects containing "Pool", of
        /// which only 4 were real placed pool buildings (0_PoolRectangleSmall(Clone) x2,
        /// _DecorOldAttraction_Pool_1/2/3(Clone)) - every one of them ending in "(Clone)", and
        /// none of the other 192 (ladders, LOD meshes, outlines, decals, FX, spawners, even an
        /// unrelated object-pooling system called "PooledObjects") did. Requiring the suffix is
        /// far more reliable than continuing to blacklist individual false-positives one at a time
        /// as they turn up live (which is still kept as defense in depth via
        /// <paramref name="excludeSubstrings"/> - see NonInstanceNameHints).
        /// </summary>
        private static GameObject[] FindByNameContains(string substring, string[] excludeSubstrings = null, bool requireCloneSuffix = false)
        {
            var query = UnityEngine.Object.FindObjectsOfType<GameObject>()
                .Where(go => go.name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);

            if (requireCloneSuffix)
            {
                query = query.Where(go => go.name.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase));
            }

            if (excludeSubstrings != null)
            {
                query = query.Where(go => !excludeSubstrings.Any(exclude => go.name.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return query.ToArray();
        }

        /// <summary>
        /// General-purpose backstop against spawning geometry at a broken transform - rejects
        /// NaN/Infinity components and anything absurdly far from a sane play area (way beyond
        /// any real park size). Added after a live crash immediately following SpawnPoop
        /// targeting 'Convex_Pool' (a raw collision mesh, now also excluded by name via
        /// NonInstanceNameHints) - no C# exception was logged, just the whole process going
        /// silent, consistent with an engine-level crash from a degenerate transform. This can't
        /// prove that was the cause, but costs nothing and guards against whatever the next
        /// unexpected name match turns out to be.
        /// </summary>
        private static bool HasSanePosition(GameObject go)
        {
            const float maxCoordinate = 100_000f;
            var position = go.transform.position;

            return !float.IsNaN(position.x) && !float.IsNaN(position.y) && !float.IsNaN(position.z)
                && !float.IsInfinity(position.x) && !float.IsInfinity(position.y) && !float.IsInfinity(position.z)
                && Mathf.Abs(position.x) < maxCoordinate && Mathf.Abs(position.y) < maxCoordinate && Mathf.Abs(position.z) < maxCoordinate;
        }

        /// <summary>
        /// Used by YeetGuest to exclude background city pedestrians (sidewalk NPCs outside the
        /// park, which apparently also carry the Visitor tag) - see YeetGuest's doc comment for
        /// the reasoning and caveats. Walks the full ancestor chain rather than just checking the
        /// immediate parent, since we don't know how deep the real guest hierarchy nests.
        /// </summary>
        private static bool IsInPark(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (t.name.IndexOf("StaticCityLayout", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds "Root/Child/.../go" from the scene root down to <paramref name="go"/> - lets
        /// !scan reveal a GameObject's actual ancestry (e.g. to confirm/refute IsInPark's
        /// "StaticCityLayout" assumption above) instead of guessing at hierarchy from object names
        /// alone.
        /// </summary>
        private static string GetHierarchyPath(GameObject go)
        {
            var names = new List<string>();
            for (var t = go.transform; t != null; t = t.parent)
            {
                names.Add(t.name);
            }

            names.Reverse();
            return string.Join("/", names);
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
