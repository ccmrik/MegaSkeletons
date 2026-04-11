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
        public const string PluginVersion = "1.0.7";

        public static MegaSkeletonsPlugin Instance { get; private set; }
        internal static ManualLogSource _logger;
        private static Harmony _harmony;
        private static ConfigFile _config;
        private static FileSystemWatcher _configWatcher;

        // Skeleton Buffs
        public static ConfigEntry<bool> EnableSkeletonBuff;
        public static ConfigEntry<float> SkeletonHealthMultiplier;
        public static ConfigEntry<float> SkeletonHealPerSecond;
        public static ConfigEntry<bool> EnableSkeletonSpeedMatch;
        public static ConfigEntry<float> SkeletonSpeedMultiplier;
        public static ConfigEntry<float> SkeletonAttackSpeedMultiplier;

        // Skeleton Persistence
        public static ConfigEntry<bool> EnableSkeletonPersistence;
        public static ConfigEntry<float> SkeletonFollowRadius;

        // Debug
        public static ConfigEntry<bool> DebugMode;

        void Awake()
        {
            Instance = this;
            _logger = Logger;
            _config = Config;

            // 1. Skeleton Buffs
            EnableSkeletonBuff = Config.Bind("1. Skeleton Buffs", "Enable", true,
                "Buffs summoned skeletons from the Dead Raiser (health, speed, attack speed, heal over time)");
            SkeletonHealthMultiplier = Config.Bind("1. Skeleton Buffs", "HealthMultiplier", 10f,
                new ConfigDescription("Health multiplier for summoned skeletons (vanilla ≈ 40 HP)", new AcceptableValueRange<float>(1f, 100f)));
            SkeletonHealPerSecond = Config.Bind("1. Skeleton Buffs", "HealPerSecond", 5f,
                new ConfigDescription("HP healed per second for summoned skeletons (0 = disabled)", new AcceptableValueRange<float>(0f, 100f)));
            EnableSkeletonSpeedMatch = Config.Bind("1. Skeleton Buffs", "SpeedMatch", true,
                "Match summoned skeleton walk/run speed to the player so they keep up");
            SkeletonSpeedMultiplier = Config.Bind("1. Skeleton Buffs", "SpeedMultiplier", 1.5f,
                new ConfigDescription("Speed multiplier on top of player speed matching (1.5 = 50% faster than player, helps them keep up during sprint)", new AcceptableValueRange<float>(1f, 5f)));
            SkeletonAttackSpeedMultiplier = Config.Bind("1. Skeleton Buffs", "AttackSpeedMultiplier", 1f,
                new ConfigDescription("Attack animation speed multiplier (1 = vanilla, 2 = double speed)", new AcceptableValueRange<float>(1f, 5f)));

            // 2. Skeleton Persistence
            EnableSkeletonPersistence = Config.Bind("2. Skeleton Persistence", "Enable", true,
                "Summoned skeletons follow you through portals and dungeon entrances/exits");
            SkeletonFollowRadius = Config.Bind("2. Skeleton Persistence", "FollowRadius", 30f,
                new ConfigDescription("Max distance from player for skeletons to be teleported with you", new AcceptableValueRange<float>(5f, 100f)));

            // 3. Debug
            DebugMode = Config.Bind("3. Debug", "DebugMode", false,
                "Enable verbose debug logging to BepInEx console/log");

            // Config file watcher for live reload
            SetupConfigWatcher();

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            Log($"{PluginName} v{PluginVersion} loaded!");
        }

        void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            _configWatcher?.Dispose();
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

        /// <summary>Always log (not gated by DebugMode) — for critical persistence events.</summary>
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
    /// - Attack animation speed multiplier (continuous during attacks)
    /// </summary>
    public class SkeletonBuff : MonoBehaviour
    {
        private Character _character;
        private MonsterAI _monsterAI;
        private Animator _animator;
        private bool _healthApplied;
        private float _healTimer;

        // ZDO key to prevent HP stacking across respawns
        private static readonly int s_megaHpBuffed = "mega_hp_buffed".GetStableHashCode();

        void Awake()
        {
            _character = GetComponent<Character>();
            _monsterAI = GetComponent<MonsterAI>();
            _animator = GetComponentInChildren<Animator>();
        }

        void OnDestroy()
        {
            // Diagnostic: log when tamed skeletons are destroyed (helps debug dungeon disappearance)
            if (_character != null && _character.IsTamed())
            {
                MegaSkeletonsPlugin.LogAlways($"[SkeletonBuff] Tamed skeleton DESTROYED: '{_character.gameObject?.name}' IsDead={_character.IsDead()} HP={_character.GetHealth()}");
                MegaSkeletonsPlugin.LogAlways($"[SkeletonBuff] Destroy stack: {Environment.StackTrace}");
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

            // Attack speed — only during attacks to avoid twitchy idle/walk animations
            if (_animator != null)
            {
                float attackMult = MegaSkeletonsPlugin.SkeletonAttackSpeedMultiplier.Value;
                if (attackMult > 1f && _character.InAttack())
                    _animator.speed = attackMult;
                else
                    _animator.speed = 1f;
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

            MegaSkeletonsPlugin.LogAlways($"[Persistence] Scanning {allCharacters.Count} characters within {radius}m...");

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
                    MegaSkeletonsPlugin.LogAlways($"[Persistence]   '{objName}' dist={dist:F1} tamed={isTamed} skeleName={isSkele}");
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
                MegaSkeletonsPlugin.LogAlways($"[Persistence] Already have {_savedSkeletons.Count} skeleton(s) pending — skipping duplicate save");
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
                MegaSkeletonsPlugin.LogAlways($"[Persistence] Saved skeleton: {prefabName}, HP={saved.Health}/{saved.MaxHealth}, Lvl={saved.Level}");

                // Destroy via ZNetScene so it's properly cleaned up across the network
                nview.Destroy();
            }

            if (_savedSkeletons.Count > 0)
            {
                _waitingToRespawn = true;
                MegaSkeletonsPlugin.LogAlways($"[Persistence] Saved {_savedSkeletons.Count} skeleton(s) for teleport");
            }
            else
            {
                MegaSkeletonsPlugin.LogAlways($"[Persistence] No tamed skeletons found within {radius}m radius");
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
            MegaSkeletonsPlugin.LogAlways($"[Persistence] Waiting 3s for environment to load before respawning {toRespawn.Count} skeleton(s)...");
            MegaSkeletonsPlugin.Instance.StartCoroutine(RespawnCoroutine(player, toRespawn));
        }

        private static IEnumerator RespawnCoroutine(Player player, List<SavedSkeleton> skeletons)
        {
            yield return new WaitForSeconds(3f);

            if (player == null)
            {
                MegaSkeletonsPlugin.LogAlways("[Persistence] Player gone after delay — aborting respawn");
                yield break;
            }

            MegaSkeletonsPlugin.LogAlways($"[Persistence] Respawning {skeletons.Count} skeleton(s)");

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

                    MegaSkeletonsPlugin.LogAlways($"[Persistence] Respawned {saved.PrefabName} at {spawnPos}, HP={saved.Health}/{saved.MaxHealth}");
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
            MegaSkeletonsPlugin.LogAlways("[Persistence] Player.TeleportTo fired — saving skeletons");
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

            MegaSkeletonsPlugin.LogAlways("[Persistence] Teleport.Interact fired (dungeon door) — saving skeletons");
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
                MegaSkeletonsPlugin.LogAlways("[Persistence] Teleport complete — respawning skeletons");
                SkeletonPersistence.RespawnSkeletons(__instance);
            }

            _wasTeleporting = isTeleporting;
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
}
