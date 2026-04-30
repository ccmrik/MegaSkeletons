using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MegaSkeletons
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MegaSkeletonsPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.rik.megaskeletons";
        public const string PluginName = "Mega Skeletons";
        public const string PluginVersion = "1.4.0";

        public static MegaSkeletonsPlugin Instance { get; private set; }
        internal static ManualLogSource _logger;
        private static Harmony _harmony;
        private static ConfigFile _config;
        private static FileSystemWatcher _configWatcher;

        // Skeleton Buffs
        public static ConfigEntry<bool> EnableSkeletonBuff;
        public static ConfigEntry<int> SkeletonHealthMultiplier;
        public static ConfigEntry<int> SkeletonHealPerSecond;
        public static ConfigEntry<bool> EnableSkeletonSpeedMatch;
        public static ConfigEntry<int> SkeletonSpeedMultiplier;

        // Skeleton Persistence
        public static ConfigEntry<bool> EnableSkeletonPersistence;
        public static ConfigEntry<int> SkeletonFollowRadius;

        // Skeleton Sight (line-of-sight range)
        public static ConfigEntry<bool> EnableSkeletonSight;
        public static ConfigEntry<int> SkeletonSightMultiplier;

        // Skeleton Exploder
        public static ConfigEntry<bool> EnableSkeletonExploder;
        public static ConfigEntry<KeyboardShortcut> ExploderHotkey;
        public static ConfigEntry<int> ExploderDamageMultiplier;
        public static ConfigEntry<int> ExploderAoeRadius;
        public static ConfigEntry<SkeletonExploder.ExplosionFx> ExploderExplosionType;

        // Debug
        public static ConfigEntry<bool> DebugMode;

        void Awake()
        {
            Instance = this;
            _logger = Logger;
            _config = Config;

            MigrateDebugSection(Config.ConfigFilePath);
            // Only reload if the file already exists; on a fresh profile the file is
            // created by the first Bind() call below, and calling Reload() on a missing
            // file throws FileNotFoundException and aborts Awake (no patches applied).
            if (File.Exists(Config.ConfigFilePath))
                Config.Reload();

            // 1. Skeleton Buffs
            EnableSkeletonBuff = Config.Bind("1. Skeleton Buffs", "Enable", true,
                "Buffs summoned skeletons from the Dead Raiser (health, speed, heal over time)");
            SkeletonHealthMultiplier = Config.Bind("1. Skeleton Buffs", "HealthMultiplier", 10,
                new ConfigDescription("Health multiplier for summoned skeletons (vanilla ≈ 40 HP)", new AcceptableValueRange<int>(1, 10)));
            SkeletonHealPerSecond = Config.Bind("1. Skeleton Buffs", "HealPerSecond", 5,
                new ConfigDescription("HP healed per second for summoned skeletons (0 = disabled)", new AcceptableValueRange<int>(0, 10)));
            EnableSkeletonSpeedMatch = Config.Bind("1. Skeleton Buffs", "SpeedMatch", true,
                "Match summoned skeleton walk/run speed to the player so they keep up");
            SkeletonSpeedMultiplier = Config.Bind("1. Skeleton Buffs", "SpeedMultiplier", 2,
                new ConfigDescription("Speed multiplier on top of player speed matching (2 = double player speed so they keep up during sprint)", new AcceptableValueRange<int>(1, 2)));

            // 2. Skeleton Persistence
            EnableSkeletonPersistence = Config.Bind("2. Skeleton Persistence", "Enable", true,
                "Summoned skeletons follow you through portals and dungeon entrances/exits");
            SkeletonFollowRadius = Config.Bind("2. Skeleton Persistence", "FollowRadius", 30,
                new ConfigDescription("Max distance from player (metres) for skeletons to be teleported with you", new AcceptableValueRange<int>(5, 100)));

            // 3. Skeleton Exploder — remote-detonate every controlled skeleton in follow radius
            EnableSkeletonExploder = Config.Bind("3. Skeleton Exploder", "Enable", true,
                "Press the hotkey to detonate every tamed skeleton in follow radius (each leaves an AOE explosion + lingering housefire)");
            ExploderHotkey = Config.Bind("3. Skeleton Exploder", "Hotkey", new KeyboardShortcut(KeyCode.KeypadEnter),
                "Hotkey to trigger detonation of all controlled skeletons");
            ExploderDamageMultiplier = Config.Bind("3. Skeleton Exploder", "DamageMultiplier", 1,
                new ConfigDescription("Damage multiplier on top of the 200-base elemental split (40 fire/poison/spirit/lightning/frost + 200 chop + 200 pickaxe). 1x = baseline, 10x = ten times.", new AcceptableValueRange<int>(1, 10)));
            ExploderAoeRadius = Config.Bind("3. Skeleton Exploder", "AoeRadius", 10,
                new ConfigDescription("AOE damage radius around each detonating skeleton (metres)", new AcceptableValueRange<int>(1, 10)));
            ExploderExplosionType = Config.Bind("3. Skeleton Exploder", "ExplosionType", SkeletonExploder.ExplosionFx.StaffEmbers,
                "Visual explosion effect. StaffEmbers mimics the Staff of Embers; Meteor/Bonemass/Lava/Lightning use other vanilla effects. Enable DebugMode to log every fx_/explosion/aoe prefab discovered at runtime so we can refine the list.");

            // 4. Skeleton Sight — extend tamed skeleton view + alert range so they engage further away
            EnableSkeletonSight = Config.Bind("4. Skeleton Sight", "Enable", true,
                "Extend summoned skeleton line of sight + alert range so they spot and attack enemies further away");
            SkeletonSightMultiplier = Config.Bind("4. Skeleton Sight", "SightMultiplier", 3,
                new ConfigDescription("Multiplier on view range and alert range (vanilla view ≈ 30m, 3x = 90m)", new AcceptableValueRange<int>(1, 10)));

            // 99. Debug — standardised section across all Mega mods (v1.1.0+)
            DebugMode = Config.Bind("99. Debug", "DebugMode", false,
                "Enable verbose debug logging to BepInEx console/log");

            // Sweep orphan keys/sections left behind by previous versions
            // (e.g. AttackSpeedMultiplier removed in v1.4.0). Runs AFTER every Bind
            // so the snapshot is complete, BEFORE the watcher attaches so we don't
            // trigger a phantom Reload.
            ConfigPruner.Prune(Config, _logger);

            // Config file watcher for live reload
            SetupConfigWatcher();

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            Log($"{PluginName} v{PluginVersion} loaded!");
        }

        void Update()
        {
            if (EnableSkeletonExploder == null || !EnableSkeletonExploder.Value) return;
            if (Player.m_localPlayer == null) return;
            if (ExploderHotkey.Value.IsDown())
                SkeletonExploder.TryDetonate(Player.m_localPlayer);
        }

        void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _configWatcher?.Dispose();
        }

        /// <summary>One-shot rename of "[3. Debug]" → "[99. Debug]" so user configs carry over.</summary>
        private static void MigrateDebugSection(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                var text = File.ReadAllText(configPath);
                if (text.Contains("[99. Debug]")) return;
                if (!text.Contains("[3. Debug]")) return;
                File.WriteAllText(configPath, text.Replace("[3. Debug]", "[99. Debug]"));
            }
            catch { /* non-fatal */ }
        }

        private void SetupConfigWatcher()
        {
            var configDir = Path.GetDirectoryName(Config.ConfigFilePath);
            var configFile = Path.GetFileName(Config.ConfigFilePath);
            _configWatcher = new FileSystemWatcher(configDir, configFile);
            _configWatcher.Changed += (s, e) =>
            {
                Config.Reload();
                Log("Config reloaded via file watcher");
            };
            _configWatcher.EnableRaisingEvents = true;
        }

        internal static void Log(string message)
        {
            if (DebugMode != null && DebugMode.Value)
                _logger.LogInfo(message);
        }

        /// <summary>Always log (not gated by DebugMode) — reserve for startup/critical events only.</summary>
        internal static void LogAlways(string message)
        {
            _logger.LogInfo(message);
        }
    }

    // ==================== SKELETON DETECTION ====================

    [HarmonyPatch(typeof(Character), "Awake")]
    public static class Character_Awake_SkeletonBuff_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Character __instance)
        {
            if (!MegaSkeletonsPlugin.EnableSkeletonBuff.Value) return;

            // Match both prefab names (Skeleton_Friendly) and display names (Skelett)
            string objName = __instance.gameObject.name.ToLower();
            if (!objName.Contains("skeleton") && !objName.Contains("skelett")) return;

            MegaSkeletonsPlugin.Log($"[SkeletonBuff] Attaching to '{__instance.gameObject.name}'");
            if (__instance.gameObject.GetComponent<SkeletonBuff>() == null)
                __instance.gameObject.AddComponent<SkeletonBuff>();
        }
    }

    // ==================== SKELETON BUFFS ====================

    /// <summary>
    /// Buffs summoned (tamed) skeletons from the Dead Raiser staff:
    /// - Health multiplier (applied once on first tamed detection, via ZDO)
    /// - Heal over time (continuous HP regen)
    /// - Speed matching to player walk/run speed (continuous)
    /// </summary>
    public class SkeletonBuff : MonoBehaviour
    {
        private Character _character;
        private MonsterAI _monsterAI;
        private bool _healthApplied;
        private float _healTimer;

        // Cached vanilla view/alert ranges so the multiplier always applies to a clean baseline
        // (capturing on Awake; -1 means "not yet captured")
        private float _baseViewRange = -1f;
        private float _baseAlertRange = -1f;

        // ZDO key to prevent HP stacking across respawns
        private static readonly int s_megaHpBuffed = "mega_hp_buffed".GetStableHashCode();

        void Awake()
        {
            _character = GetComponent<Character>();
            _monsterAI = GetComponent<MonsterAI>();
        }

        void OnDestroy()
        {
            // Diagnostic: log when tamed skeletons are destroyed (helps debug dungeon disappearance)
            if (_character != null && _character.IsTamed())
            {
                MegaSkeletonsPlugin.Log($"[SkeletonBuff] Tamed skeleton DESTROYED: '{_character.gameObject?.name}' IsDead={_character.IsDead()} HP={_character.GetHealth()}");
                MegaSkeletonsPlugin.Log($"[SkeletonBuff] Destroy stack: {Environment.StackTrace}");
            }
        }

        void Update()
        {
            if (_character == null) return;

            // Only buff tamed skeletons (player-summoned via Dead Raiser)
            if (!_character.IsTamed()) return;

            if (!MegaSkeletonsPlugin.EnableSkeletonBuff.Value) return;

            Player player = Player.m_localPlayer;
            if (player == null) return;

            // Health multiplier — apply once via ZDO (m_health field is dead after Awake)
            // Uses ZDO flag to prevent stacking across respawns (each respawn would re-multiply)
            if (!_healthApplied)
            {
                _healthApplied = true;
                float mult = MegaSkeletonsPlugin.SkeletonHealthMultiplier.Value;
                if (mult > 1f)
                {
                    var nview = _character.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid())
                    {
                        var zdo = nview.GetZDO();
                        if (zdo.GetBool(s_megaHpBuffed))
                        {
                            MegaSkeletonsPlugin.Log($"[SkeletonBuff] Already HP-buffed (ZDO flag), skipping");
                        }
                        else
                        {
                            float newMax = _character.GetMaxHealth() * mult;
                            zdo.Set(ZDOVars.s_maxHealth, newMax);
                            zdo.Set(ZDOVars.s_health, newMax);
                            _character.m_health = newMax;
                            zdo.Set(s_megaHpBuffed, true);
                            MegaSkeletonsPlugin.Log($"[SkeletonBuff] Health buffed to {newMax} ({mult}x)");
                        }
                    }
                }
            }

            // Heal over time — continuous regen (skip if dead to prevent resurrection)
            float healRate = MegaSkeletonsPlugin.SkeletonHealPerSecond.Value;
            if (healRate > 0f && !_character.IsDead())
            {
                _healTimer += Time.deltaTime;
                if (_healTimer >= 1f)
                {
                    _healTimer = 0f;
                    var nview = _character.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid())
                    {
                        float maxHp = _character.GetMaxHealth();
                        float curHp = _character.GetHealth();
                        if (curHp > 0f && curHp < maxHp)
                        {
                            float newHp = Mathf.Min(curHp + healRate, maxHp);
                            _character.SetHealth(newHp);
                        }
                    }
                }
            }

            // Sight + alert range — extend so tamed skeletons engage further away.
            // Baseline captured once from the prefab values, then re-applied each tick
            // so live config edits and pheromone-tame transitions both stay in sync.
            if (_monsterAI != null)
            {
                if (_baseViewRange < 0f) _baseViewRange = _monsterAI.m_viewRange;
                if (_baseAlertRange < 0f) _baseAlertRange = _monsterAI.m_alertRange;

                if (MegaSkeletonsPlugin.EnableSkeletonSight.Value)
                {
                    float sightMult = MegaSkeletonsPlugin.SkeletonSightMultiplier.Value;
                    _monsterAI.m_viewRange = _baseViewRange * sightMult;
                    _monsterAI.m_alertRange = _baseAlertRange * sightMult;
                }
                else
                {
                    _monsterAI.m_viewRange = _baseViewRange;
                    _monsterAI.m_alertRange = _baseAlertRange;
                }
            }

            // Speed matching — continuous, with multiplier so they keep up during sprint
            if (MegaSkeletonsPlugin.EnableSkeletonSpeedMatch.Value)
            {
                float speedMult = MegaSkeletonsPlugin.SkeletonSpeedMultiplier.Value;
                _character.m_walkSpeed = player.m_walkSpeed * speedMult;
                _character.m_runSpeed = player.m_runSpeed * speedMult;
                _character.m_acceleration = 20f; // snappy acceleration (vanilla ~6)
                _character.m_turnSpeed = 600f;    // fast turning to follow player

                // Water speed — match swim speed so skeletons keep up in water
                _character.m_swimSpeed = player.m_swimSpeed * speedMult;
                _character.m_swimAcceleration = player.m_swimAcceleration;
                _character.m_swimTurnSpeed = player.m_swimTurnSpeed;
            }
        }
    }

    // ==================== SKELETON PERSISTENCE ====================

    /// <summary>
    /// Saves skeleton state before teleport, respawns after arrival.
    /// Hooks Player.TeleportTo (start) and Player.UpdateTeleport (complete).
    /// </summary>
    public static class SkeletonPersistence
    {
        private struct SavedSkeleton
        {
            public string PrefabName;
            public float Health;
            public float MaxHealth;
            public int Level;
        }

        private static readonly List<SavedSkeleton> _savedSkeletons = new List<SavedSkeleton>();
        private static bool _waitingToRespawn;

        public static bool IsSkeletonPrefab(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLower();
            return lower.Contains("skeleton") || lower.Contains("skelett");
        }

        /// <summary>
        /// Find all tamed skeletons near the local player within radius.
        /// Does NOT require follow target — Dead Raiser skeletons may follow
        /// via pheromone/tame system rather than explicit SetFollowTarget.
        /// </summary>
        public static List<Character> GetNearbyTamedSkeletons(Player player, float radius)
        {
            var results = new List<Character>();
            if (player == null) return results;

            var allCharacters = Character.GetAllCharacters();
            Vector3 playerPos = player.transform.position;

            MegaSkeletonsPlugin.Log($"[Persistence] Scanning {allCharacters.Count} characters within {radius}m...");

            foreach (var character in allCharacters)
            {
                if (character == null || character == player) continue;

                string objName = character.gameObject.name;
                float dist = Vector3.Distance(playerPos, character.transform.position);
                bool isTamed = character.IsTamed();
                bool isSkele = IsSkeletonPrefab(objName);

                // Log all nearby creatures for diagnostics
                if (dist <= radius && (isTamed || isSkele))
                {
                    MegaSkeletonsPlugin.Log($"[Persistence]   '{objName}' dist={dist:F1} tamed={isTamed} skeleName={isSkele}");
                }

                if (!isTamed) continue;
                if (!isSkele) continue;
                if (dist > radius) continue;

                results.Add(character);
            }

            return results;
        }

        /// <summary>
        /// Save skeleton state and destroy them before teleport.
        /// </summary>
        public static void SaveAndDestroySkeletons(Player player)
        {
            if (!MegaSkeletonsPlugin.EnableSkeletonPersistence.Value) return;

            // If we already have saved skeletons pending respawn, don't overwrite them.
            // This prevents Teleport.Interact → Player.TeleportTo double-fire from clearing
            // the batch that Interact already captured.
            if (_waitingToRespawn && _savedSkeletons.Count > 0)
            {
                MegaSkeletonsPlugin.Log($"[Persistence] Already have {_savedSkeletons.Count} skeleton(s) pending — skipping duplicate save");
                return;
            }

            _savedSkeletons.Clear();
            _waitingToRespawn = false;

            float radius = MegaSkeletonsPlugin.SkeletonFollowRadius.Value;
            var skeletons = GetNearbyTamedSkeletons(player, radius);

            foreach (var skeleton in skeletons)
            {
                var nview = skeleton.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                // Get the clean prefab name (strip "(Clone)" and instance numbers)
                string prefabName = Utils.GetPrefabName(skeleton.gameObject);

                var saved = new SavedSkeleton
                {
                    PrefabName = prefabName,
                    Health = skeleton.GetHealth(),
                    MaxHealth = skeleton.GetMaxHealth(),
                    Level = skeleton.GetLevel()
                };

                _savedSkeletons.Add(saved);
                MegaSkeletonsPlugin.Log($"[Persistence] Saved skeleton: {prefabName}, HP={saved.Health}/{saved.MaxHealth}, Lvl={saved.Level}");

                // Destroy via ZNetScene so it's properly cleaned up across the network
                nview.Destroy();
            }

            if (_savedSkeletons.Count > 0)
            {
                _waitingToRespawn = true;
                MegaSkeletonsPlugin.Log($"[Persistence] Saved {_savedSkeletons.Count} skeleton(s) for teleport");
            }
            else
            {
                MegaSkeletonsPlugin.Log($"[Persistence] No tamed skeletons found within {radius}m radius");
            }
        }

        /// <summary>
        /// Respawn saved skeletons near the player after teleport completes.
        /// Uses a delayed coroutine to let dungeons fully load before spawning.
        /// </summary>
        public static void RespawnSkeletons(Player player)
        {
            if (!_waitingToRespawn || _savedSkeletons.Count == 0) return;

            _waitingToRespawn = false;

            // Copy the list and clear immediately — coroutine uses its own copy
            var toRespawn = new List<SavedSkeleton>(_savedSkeletons);
            _savedSkeletons.Clear();

            // Delay spawn by 3 seconds to let dungeon environments fully load.
            // Without this, skeletons spawned inside dungeons can fall through
            // floors or get cleaned up by zone management before rooms settle.
            MegaSkeletonsPlugin.Log($"[Persistence] Waiting 3s for environment to load before respawning {toRespawn.Count} skeleton(s)...");
            MegaSkeletonsPlugin.Instance.StartCoroutine(RespawnCoroutine(player, toRespawn));
        }

        private static IEnumerator RespawnCoroutine(Player player, List<SavedSkeleton> skeletons)
        {
            yield return new WaitForSeconds(3f);

            if (player == null)
            {
                MegaSkeletonsPlugin.Log("[Persistence] Player gone after delay — aborting respawn");
                yield break;
            }

            MegaSkeletonsPlugin.Log($"[Persistence] Respawning {skeletons.Count} skeleton(s)");

            // ZDO hash for the HP-buff flag (same as SkeletonBuff uses)
            int megaHpBuffed = "mega_hp_buffed".GetStableHashCode();

            int index = 0;
            foreach (var saved in skeletons)
            {
                var prefab = ZNetScene.instance.GetPrefab(saved.PrefabName);
                if (prefab == null)
                {
                    MegaSkeletonsPlugin._logger.LogWarning($"[Persistence] Prefab '{saved.PrefabName}' not found, skipping");
                    continue;
                }

                // Spawn near player with offset to avoid stacking.
                // +1.5 Y offset ensures skeletons spawn above the floor in dungeons
                // (gravity will settle them down; prevents clipping through dungeon geometry).
                float angle = index * (360f / skeletons.Count) * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(angle) * 2f, 1.5f, Mathf.Sin(angle) * 2f);
                Vector3 spawnPos = player.transform.position + offset;

                var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity);
                var nview = go.GetComponent<ZNetView>();
                var character = go.GetComponent<Character>();
                var tameable = go.GetComponent<Tameable>();
                var monsterAI = go.GetComponent<MonsterAI>();

                if (nview != null && nview.IsValid())
                {
                    var zdo = nview.GetZDO();

                    // Restore level (before health, as SetupMaxHealth uses level)
                    if (saved.Level > 1)
                    {
                        zdo.Set(ZDOVars.s_level, saved.Level);
                        if (character != null)
                            character.SetLevel(saved.Level);
                    }

                    // Tame the skeleton (Tame() is private, use reflection)
                    if (tameable != null)
                    {
                        var tameMethod = typeof(Tameable).GetMethod("Tame",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        tameMethod?.Invoke(tameable, null);
                    }

                    // Restore health — mark as already buffed to prevent SkeletonBuff
                    // from applying the multiplier again (HP stacking bug)
                    zdo.Set(ZDOVars.s_maxHealth, saved.MaxHealth);
                    zdo.Set(ZDOVars.s_health, Mathf.Min(saved.Health, saved.MaxHealth));
                    zdo.Set(megaHpBuffed, true);
                    if (character != null)
                        character.m_health = saved.MaxHealth;

                    // Set follow target to player
                    if (monsterAI != null)
                        monsterAI.SetFollowTarget(player.gameObject);

                    MegaSkeletonsPlugin.Log($"[Persistence] Respawned {saved.PrefabName} at {spawnPos}, HP={saved.Health}/{saved.MaxHealth}");
                }

                index++;
            }
        }

        /// <summary>
        /// Check if we're waiting to respawn skeletons.
        /// </summary>
        public static bool IsWaitingToRespawn => _waitingToRespawn;
    }

    // ==================== TELEPORT HOOKS ====================

    /// <summary>
    /// Capture nearby tamed skeletons before portal teleport.
    /// Player.TeleportTo fires for portals and map teleport.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
    public static class Player_TeleportTo_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            MegaSkeletonsPlugin.Log("[Persistence] Player.TeleportTo fired — saving skeletons");
            SkeletonPersistence.SaveAndDestroySkeletons(__instance);
        }
    }

    /// <summary>
    /// Capture nearby tamed skeletons before dungeon entrance/exit.
    /// Teleport.Interact fires when player uses a dungeon door — separate path from Player.TeleportTo.
    /// </summary>
    [HarmonyPatch(typeof(Teleport), nameof(Teleport.Interact))]
    public static class Teleport_Interact_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Teleport __instance, Humanoid character)
        {
            var player = character as Player;
            if (player == null || player != Player.m_localPlayer) return;

            // Only save if we have a valid target point (actual dungeon door, not a broken one)
            if (__instance.m_targetPoint == null) return;

            MegaSkeletonsPlugin.Log("[Persistence] Teleport.Interact fired (dungeon door) — saving skeletons");
            SkeletonPersistence.SaveAndDestroySkeletons(player);
        }
    }

    /// <summary>
    /// Detect when teleport completes and respawn saved skeletons.
    /// Player.UpdateTeleport runs every frame during teleport.
    /// When m_teleporting goes false, teleport is complete.
    /// </summary>
    [HarmonyPatch(typeof(Player), "UpdateTeleport")]
    public static class Player_UpdateTeleport_Patch
    {
        private static bool _wasTeleporting;

        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;

            // Access m_teleporting via Traverse (private field)
            bool isTeleporting = Traverse.Create(__instance).Field("m_teleporting").GetValue<bool>();

            // Detect transition from teleporting → not teleporting
            if (_wasTeleporting && !isTeleporting)
            {
                MegaSkeletonsPlugin.Log("[Persistence] Teleport complete — respawning skeletons");
                SkeletonPersistence.RespawnSkeletons(__instance);
            }

            _wasTeleporting = isTeleporting;
        }
    }

    // ==================== SKELETON EXPLODER ====================

    /// <summary>
    /// Remote detonation of every tamed skeleton in follow radius.
    /// Each skeleton vanishes and leaves an AOE explosion at its position dealing
    /// configurable elemental damage (40-base split across fire/poison/spirit/
    /// lightning/frost + chop + pickaxe, scaled by DamageMultiplier 1-10x). Hits
    /// hostile creatures, trees, rocks, mining nodes and world structures —
    /// player-built pieces and pickables (berries etc) are skipped. A vanilla
    /// HouseFire is spawned at each detonation point for ongoing burn damage
    /// and the visual cue. Vanilla DoT status effects (Burning, Poisoned, etc)
    /// trigger naturally from the elemental values via Character.Damage().
    /// </summary>
    public static class SkeletonExploder
    {
        public enum ExplosionFx
        {
            StaffEmbers,
            Meteor,
            Bonemass,
            Lava,
            Lightning,
        }

        private static bool _prefabsDiscovered;

        // Cached HouseFire prefab — vanilla `fire_house` (or fallback via Cinder.m_houseFirePrefab).
        private static GameObject _houseFirePrefab;
        private static bool _houseFireSearchDone;

        // Verified prefab names from runtime ZNetScene discovery (v1.2.0 debug dump).
        // Each list is ordered: primary visual → fallbacks if user's install differs.
        private static readonly Dictionary<ExplosionFx, string[]> FxFallbacks = new Dictionary<ExplosionFx, string[]>
        {
            [ExplosionFx.StaffEmbers] = new[] { "fx_fireball_staff_explosion", "fx_shaman_fireball_expl", "charred_fireball_aoe" },
            [ExplosionFx.Meteor]      = new[] { "fx_goblinking_meteor_hit", "fx_fader_meteor_hit", "Fader_MeteorSmash_AOE", "fx_fader_meteorsmash" },
            [ExplosionFx.Bonemass]    = new[] { "bonemass_aoe", "fx_Bonemass_aoe_start" },
            [ExplosionFx.Lava]        = new[] { "fx_lavabomb_explosion", "lavabomb_explosion", "fx_unstablelavarock_explosion", "fx_blobLava_explosion" },
            [ExplosionFx.Lightning]   = new[] { "fx_chainlightning_spread", "lightningAOE", "fx_Lightning", "fx_DvergerMage_Nova_ring", "aoe_nova", "fx_redlightning_burst" },
        };

        public static void TryDetonate(Player player)
        {
            if (player == null) return;

            float followRadius = MegaSkeletonsPlugin.SkeletonFollowRadius.Value;
            var skeletons = SkeletonPersistence.GetNearbyTamedSkeletons(player, followRadius);

            if (skeletons.Count == 0)
            {
                MegaSkeletonsPlugin.Log("[Exploder] No tamed skeletons in follow radius — nothing to detonate");
                return;
            }

            // First-detonation prefab discovery (debug only) — logs every fx_/explosion/aoe
            // prefab loaded by ZNetScene so we can refine the FxFallbacks list against
            // what's actually present in this Valheim install.
            if (MegaSkeletonsPlugin.DebugMode.Value && !_prefabsDiscovered)
            {
                _prefabsDiscovered = true;
                LogDiscoveredFxPrefabs();
            }

            float aoeRadius = MegaSkeletonsPlugin.ExploderAoeRadius.Value;
            float mult = MegaSkeletonsPlugin.ExploderDamageMultiplier.Value;
            var fxType = MegaSkeletonsPlugin.ExploderExplosionType.Value;

            MegaSkeletonsPlugin.LogAlways($"[Exploder] Detonating {skeletons.Count} skeleton(s) — radius={aoeRadius}m, mult={mult}x, fx={fxType}");

            ZDOID attackerId = player.GetZDOID();
            int idx = 0;
            foreach (var sk in skeletons)
            {
                if (sk == null) continue;
                Vector3 pos = sk.transform.position;

                // Spawn the visual + housefire + apply outward AOE first;
                // ApplyAoeDamage skips tamed creatures so the skeleton itself
                // is safe from its own blast — we then send a lethal HitData
                // so Valheim's OnDeath path runs the proper skeleton death
                // animation, ragdoll, and audio (rather than yanking the
                // netview which just makes the skeleton vanish silently).
                SpawnExplosionFx(pos, fxType);
                SpawnHouseFire(pos);
                ApplyAoeDamage(pos, aoeRadius, mult, player);

                var nv = sk.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && !nv.IsOwner())
                    nv.ClaimOwnership();

                try
                {
                    var lethal = new HitData
                    {
                        m_point = pos,
                        m_dir = Vector3.up,
                        m_pushForce = 20f,
                        m_attacker = attackerId,
                    };
                    lethal.m_damage.m_damage   = 999999f;
                    lethal.m_damage.m_blunt    = 999999f;
                    lethal.m_damage.m_slash    = 999999f;
                    lethal.m_damage.m_pierce   = 999999f;
                    lethal.m_damage.m_fire     = 999999f;
                    lethal.m_damage.m_spirit   = 999999f;
                    sk.Damage(lethal);
                }
                catch (Exception ex)
                {
                    MegaSkeletonsPlugin.Log($"[Exploder] Lethal damage failed ({ex.Message}) — falling back to nview.Destroy()");
                    if (nv != null && nv.IsValid()) nv.Destroy();
                }

                idx++;
            }

            MegaSkeletonsPlugin.Log($"[Exploder] Detonation complete — {idx} skeleton(s) blown");
        }

        private static void SpawnExplosionFx(Vector3 pos, ExplosionFx fxType)
        {
            var zns = ZNetScene.instance;
            if (zns == null) return;

            string[] candidates;
            if (!FxFallbacks.TryGetValue(fxType, out candidates)) return;

            foreach (var name in candidates)
            {
                var prefab = zns.GetPrefab(name);
                if (prefab == null) continue;

                try
                {
                    UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
                    MegaSkeletonsPlugin.Log($"[Exploder] Spawned fx '{name}' at {pos}");
                    return;
                }
                catch (Exception ex)
                {
                    MegaSkeletonsPlugin.Log($"[Exploder] Failed to spawn fx '{name}': {ex.Message}");
                }
            }

            MegaSkeletonsPlugin.Log($"[Exploder] No fx prefab found for {fxType} — tried: {string.Join(", ", candidates)}");
        }

        private static void ApplyAoeDamage(Vector3 center, float radius, float mult, Player attacker)
        {
            // 200 base damage split evenly across 5 elements = 40 each, then multiplier.
            // Plus 200x chop and pickaxe so trees and rock nodes actually take damage —
            // elemental alone bounces off them via vanilla DamageModifiers.
            float perElement = 40f * mult;
            float perPhys    = 200f * mult;
            ZDOID attackerId = attacker != null ? attacker.GetZDOID() : ZDOID.None;

            var hits = Physics.OverlapSphere(center, radius);
            var damagedCharacters = new HashSet<int>();
            var damagedWnt = new HashSet<int>();
            var damagedTree = new HashSet<int>();
            var damagedTreeLog = new HashSet<int>();
            var damagedMineRock5 = new HashSet<int>();
            var damagedMineRock = new HashSet<int>();
            var damagedDestructible = new HashSet<int>();

            foreach (var col in hits)
            {
                if (col == null) continue;

                // Hard skip: anything pickable (cloudberries, blueberries, raspberries,
                // vineberries, mushrooms, dandelions, thistle, etc). Per Milord's spec
                // explosions must never strip pickable resources.
                if (col.GetComponentInParent<Pickable>() != null) continue;

                // Damage characters (skip player + tamed allies + already-dead)
                var character = col.GetComponentInParent<Character>();
                if (character != null && !character.IsDead() && character != attacker && !character.IsTamed())
                {
                    int id = character.GetInstanceID();
                    if (damagedCharacters.Add(id))
                    {
                        var hit = BuildElementalHit(perElement, perPhys, center, character.transform.position, attackerId);
                        try { character.Damage(hit); }
                        catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] Character.Damage failed: {ex.Message}"); }
                    }
                    continue;
                }

                // Damage world structures (WearNTear) — skip player-built pieces
                var wnt = col.GetComponentInParent<WearNTear>();
                if (wnt != null)
                {
                    int id = wnt.GetInstanceID();
                    if (damagedWnt.Add(id))
                    {
                        var piece = wnt.GetComponent<Piece>();
                        if (piece != null && piece.IsPlacedByPlayer())
                        {
                            // Player-built — leave it alone
                        }
                        else
                        {
                            var hit = BuildElementalHit(perElement, perPhys, center, wnt.transform.position, attackerId);
                            try { wnt.Damage(hit); }
                            catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] WearNTear.Damage failed: {ex.Message}"); }
                        }
                    }
                    continue;
                }

                // Standing trees
                var tree = col.GetComponentInParent<TreeBase>();
                if (tree != null)
                {
                    int id = tree.GetInstanceID();
                    if (damagedTree.Add(id))
                    {
                        var hit = BuildElementalHit(perElement, perPhys, center, tree.transform.position, attackerId);
                        try { tree.Damage(hit); }
                        catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] TreeBase.Damage failed: {ex.Message}"); }
                    }
                    continue;
                }

                // Felled tree logs (need a second chop to break into wood)
                var tlog = col.GetComponentInParent<TreeLog>();
                if (tlog != null)
                {
                    int id = tlog.GetInstanceID();
                    if (damagedTreeLog.Add(id))
                    {
                        var hit = BuildElementalHit(perElement, perPhys, center, tlog.transform.position, attackerId);
                        try { tlog.Damage(hit); }
                        catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] TreeLog.Damage failed: {ex.Message}"); }
                    }
                    continue;
                }

                // Multi-area mining nodes (copper, silver, iron deposits etc)
                var rock5 = col.GetComponentInParent<MineRock5>();
                if (rock5 != null)
                {
                    int id = rock5.GetInstanceID();
                    if (damagedMineRock5.Add(id))
                    {
                        var hit = BuildElementalHit(perElement, perPhys, center, rock5.transform.position, attackerId);
                        try { rock5.Damage(hit); }
                        catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] MineRock5.Damage failed: {ex.Message}"); }
                    }
                    continue;
                }

                // Single-piece mining nodes (tin, obsidian, small rocks)
                var rock = col.GetComponentInParent<MineRock>();
                if (rock != null)
                {
                    int id = rock.GetInstanceID();
                    if (damagedMineRock.Add(id))
                    {
                        var hit = BuildElementalHit(perElement, perPhys, center, rock.transform.position, attackerId);
                        try { rock.Damage(hit); }
                        catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] MineRock.Damage failed: {ex.Message}"); }
                    }
                    continue;
                }

                // Generic Destructibles (small rocks, branches, scattered debris)
                var dest = col.GetComponentInParent<Destructible>();
                if (dest != null)
                {
                    int id = dest.GetInstanceID();
                    if (damagedDestructible.Add(id))
                    {
                        var hit = BuildElementalHit(perElement, perPhys, center, dest.transform.position, attackerId);
                        try { dest.Damage(hit); }
                        catch (Exception ex) { MegaSkeletonsPlugin.Log($"[Exploder] Destructible.Damage failed: {ex.Message}"); }
                    }
                }
            }

            MegaSkeletonsPlugin.Log($"[Exploder] AOE @ {center} r={radius}m: chars={damagedCharacters.Count} wnt={damagedWnt.Count} tree={damagedTree.Count} log={damagedTreeLog.Count} rock5={damagedMineRock5.Count} rock={damagedMineRock.Count} dest={damagedDestructible.Count}");
        }

        private static HitData BuildElementalHit(float perElement, float perPhys, Vector3 center, Vector3 target, ZDOID attackerId)
        {
            var hit = new HitData();
            hit.m_point = target;
            hit.m_dir = (target - center).normalized;
            if (hit.m_dir.sqrMagnitude < 0.01f) hit.m_dir = Vector3.up;
            hit.m_pushForce = 50f;
            hit.m_attacker = attackerId;
            hit.m_damage.m_fire = perElement;
            hit.m_damage.m_poison = perElement;
            hit.m_damage.m_spirit = perElement;
            hit.m_damage.m_lightning = perElement;
            hit.m_damage.m_frost = perElement;
            // Chop/Pickaxe so trees and rock nodes actually take damage —
            // elemental alone is filtered to zero by their DamageModifiers.
            hit.m_damage.m_chop = perPhys;
            hit.m_damage.m_pickaxe = perPhys;
            return hit;
        }

        // ---- HouseFire visual + lingering burn ---------------------------------

        private static void SpawnHouseFire(Vector3 pos)
        {
            try
            {
                if (!_houseFireSearchDone) FindHouseFirePrefab();
                if (_houseFirePrefab == null)
                {
                    MegaSkeletonsPlugin.Log("[Exploder] HouseFire prefab not found — skipping fire spawn");
                    return;
                }

                UnityEngine.Object.Instantiate(_houseFirePrefab, pos + Vector3.up * 0.1f, Quaternion.identity);
                MegaSkeletonsPlugin.Log($"[Exploder] HouseFire spawned at {pos}");
            }
            catch (Exception ex)
            {
                MegaSkeletonsPlugin.Log($"[Exploder] SpawnHouseFire failed: {ex.Message}");
            }
        }

        private static void FindHouseFirePrefab()
        {
            _houseFireSearchDone = true;
            try
            {
                var zns = ZNetScene.instance;
                if (zns == null) return;

                // Try canonical names first
                string[] knownNames = { "fire_house", "HouseFire", "houseFire", "fx_fire_house" };
                foreach (var name in knownNames)
                {
                    var prefab = zns.GetPrefab(name);
                    if (prefab != null)
                    {
                        _houseFirePrefab = prefab;
                        MegaSkeletonsPlugin.Log($"[Exploder] HouseFire prefab cached via name '{name}'");
                        return;
                    }
                }

                // Fallback: find any Cinder prefab and grab its m_houseFirePrefab field
                var cinderField = typeof(Cinder).GetField("m_houseFirePrefab",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (cinderField != null)
                {
                    foreach (var prefab in zns.m_prefabs)
                    {
                        if (prefab == null) continue;
                        var cinder = prefab.GetComponent<Cinder>();
                        if (cinder == null) continue;
                        var hf = cinderField.GetValue(cinder) as GameObject;
                        if (hf != null)
                        {
                            _houseFirePrefab = hf;
                            MegaSkeletonsPlugin.Log($"[Exploder] HouseFire prefab cached via Cinder fallback ({prefab.name})");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MegaSkeletonsPlugin.Log($"[Exploder] FindHouseFirePrefab failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks ZNetScene.m_prefabs and logs every prefab name matching fx_/explosion/aoe
        /// so we can refine the hardcoded FxFallbacks list. Only runs once per session
        /// when DebugMode is on and the user first detonates.
        /// </summary>
        private static void LogDiscoveredFxPrefabs()
        {
            var zns = ZNetScene.instance;
            if (zns == null || zns.m_prefabs == null)
            {
                MegaSkeletonsPlugin.Log("[Exploder] ZNetScene unavailable — skipping prefab discovery");
                return;
            }

            var matches = new List<string>();
            foreach (var p in zns.m_prefabs)
            {
                if (p == null) continue;
                var name = p.name;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf("explosion", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("aoe", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.StartsWith("fx_", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(name);
                }
            }
            matches.Sort(StringComparer.OrdinalIgnoreCase);

            MegaSkeletonsPlugin.Log($"[Exploder] Discovered {matches.Count} fx/explosion/aoe prefabs:");
            foreach (var m in matches)
                MegaSkeletonsPlugin.Log("  " + m);
        }
    }

    // ==================== VANILLA BUG FIX ====================

    /// <summary>
    /// Guard against NRE in MonsterAI.PheromoneFleeCheck when skeleton references go stale.
    /// (Moved from MegaQoL since it primarily affects summoned skeletons)
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), "PheromoneFleeCheck")]
    public static class PheromoneFleeCheckNullGuard
    {
        static bool Prefix(MonsterAI __instance, Character target)
        {
            return __instance != null && target != null;
        }

        static Exception Finalizer(Exception __exception)
        {
            if (__exception is NullReferenceException)
                return null;
            return __exception;
        }
    }

    // ==================== CONFIG PRUNER ====================

    /// <summary>
    /// Inline INI sweeper. Walks the bound config snapshot and rewrites the
    /// raw cfg without any orphan sections or keys (BepInEx never deletes them
    /// itself — left over content snowballs across versions). Run AFTER every
    /// Bind() and BEFORE the file-watcher attaches so we don't trigger a
    /// phantom reload mid-sweep.
    /// </summary>
    public static class ConfigPruner
    {
        public static int Prune(ConfigFile cfg, ManualLogSource log = null)
        {
            if (cfg == null) return 0;
            string path = cfg.ConfigFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            var bound = new HashSet<string>(StringComparer.Ordinal);
            var boundSections = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var def in cfg.Keys)
                {
                    bound.Add(def.Section + "\0" + def.Key);
                    boundSections.Add(def.Section);
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[ConfigPruner] Couldn't enumerate bound entries: {ex.Message}");
                return 0;
            }
            if (boundSections.Count == 0) return 0;

            string[] inLines;
            try { inLines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                log?.LogWarning($"[ConfigPruner] Read failed: {ex.Message}");
                return 0;
            }

            var outLines = new List<string>(inLines.Length);
            string currentSection = null;
            bool currentSectionKeep = true;
            int droppedSections = 0, droppedKeys = 0;

            for (int i = 0; i < inLines.Length; i++)
            {
                var line = inLines[i];
                var trimmed = line.Trim();

                if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                {
                    currentSection = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    currentSectionKeep = boundSections.Contains(currentSection);
                    if (!currentSectionKeep)
                    {
                        droppedSections++;
                        TrimTrailingBlanksAndComments(outLines);
                        continue;
                    }
                    outLines.Add(line);
                    continue;
                }

                if (!currentSectionKeep) continue;

                int eq = trimmed.IndexOf('=');
                bool isKeyLine = eq > 0 && trimmed[0] != '#' && trimmed[0] != ';';
                if (isKeyLine && currentSection != null)
                {
                    string key = trimmed.Substring(0, eq).Trim();
                    if (!bound.Contains(currentSection + "\0" + key))
                    {
                        droppedKeys++;
                        TrimTrailingBlanksAndComments(outLines);
                        continue;
                    }
                }
                outLines.Add(line);
            }

            if (droppedSections == 0 && droppedKeys == 0) return 0;

            while (outLines.Count > 0 && outLines[outLines.Count - 1].Trim().Length == 0)
                outLines.RemoveAt(outLines.Count - 1);
            outLines.Add(string.Empty);

            try
            {
                File.WriteAllLines(path, outLines);
                log?.LogInfo($"[ConfigPruner] {Path.GetFileName(path)}: dropped {droppedSections} orphan section(s), {droppedKeys} orphan key(s)");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"[ConfigPruner] Write failed: {ex.Message}");
                return 0;
            }
            return droppedSections + droppedKeys;
        }

        private static void TrimTrailingBlanksAndComments(List<string> lines)
        {
            while (lines.Count > 0)
            {
                var last = lines[lines.Count - 1].TrimStart();
                if (last.Length == 0 || last[0] == '#' || last[0] == ';')
                    lines.RemoveAt(lines.Count - 1);
                else
                    break;
            }
        }
    }
}
