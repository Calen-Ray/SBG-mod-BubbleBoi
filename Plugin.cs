using System.Reflection;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BubbleBoi
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "sbg.bubbleboi";
        public const string ModName = "BubbleBoi";
        public const string ModVersion = "0.2.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        internal ConfigEntry<bool> verboseLoggingConfig;
        internal ConfigEntry<float> expiryWarningSecondsConfig;
        internal ConfigEntry<bool> expiryWarningEnabledConfig;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            verboseLoggingConfig = Config.Bind(
                "Diagnostics",
                "VerboseLogging",
                false,
                "Emit per-frame water-walk decision logs. Off by default — flip on when reporting issues so the log shows why synthetic ground was/wasn't injected.");
            expiryWarningEnabledConfig = Config.Bind(
                "Visuals",
                "ExpiryWarningEnabled",
                true,
                "Tint the local player's electromagnet shield orange→red in the final seconds before it expires.");
            expiryWarningSecondsConfig = Config.Bind(
                "Visuals",
                "ExpiryWarningSeconds",
                3.5f,
                "Window before shield expiry over which the warning hue ramps in. The first half is orange-tinted, the last half ramps to red.");

            WaterSurfaceProxy.EnsureCreated();
            new Harmony(ModGuid).PatchAll();
            Log.LogInfo($"{ModName} v{ModVersion} loaded.");
        }
    }

    internal static class WaterSurfaceProxy
    {
        private static BoxCollider collider;

        internal static Collider Collider
        {
            get
            {
                EnsureCreated();
                return collider;
            }
        }

        internal static void EnsureCreated()
        {
            if (collider != null)
                return;

            GameObject proxy = new GameObject("BubbleBoiWaterSurfaceProxy");
            proxy.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(proxy);

            collider = proxy.AddComponent<BoxCollider>();
            collider.enabled = false;
            collider.isTrigger = false;
            collider.size = new Vector3(1f, 0.1f, 1f);
        }
    }

    // Local-player-only expiry hue warning. Ramps the shield particle renderers' tint from
    // their base color through orange and into red over the configured trailing window so the
    // wearer has a visual cue that their shield is about to drop. Other clients can't compute
    // remaining time (the activation timestamp is local-only on PlayerInfo) so we don't tint
    // for them — they wouldn't know what to do with the cue anyway.
    internal sealed class ShieldExpiryTinter : MonoBehaviour
    {
        private static readonly Color OrangeWarning = new Color(1f, 0.55f, 0.1f, 1f);
        private static readonly int[] ColorPropertyIds =
        {
            Shader.PropertyToID("_TintColor"),
            Shader.PropertyToID("_Color"),
            Shader.PropertyToID("_BaseColor"),
            Shader.PropertyToID("_EmissionColor"),
        };

        private static ShieldExpiryTinter instance;
        private static MaterialPropertyBlock block;

        private PlayerInfo target;
        private double activationTimestamp;
        private Renderer[] cachedRenderers;
        // ParticleSystemRenderer ignores MaterialPropertyBlock for tint — particle color
        // comes from ParticleSystem.MainModule.startColor. Track each particle system we
        // touch so we can restore the original color when the warning ends.
        private readonly System.Collections.Generic.Dictionary<ParticleSystem, ParticleSystem.MinMaxGradient> originalStartColors
            = new System.Collections.Generic.Dictionary<ParticleSystem, ParticleSystem.MinMaxGradient>();
        private bool tintApplied;

        public static void OnLocalShieldActivated(PlayerInfo player)
        {
            EnsureInstance();
            instance.target = player;
            instance.activationTimestamp = Time.timeAsDouble;
            instance.cachedRenderers = null;
            instance.originalStartColors.Clear();
            instance.tintApplied = false;
        }

        private static void EnsureInstance()
        {
            if (instance != null)
                return;
            GameObject go = new GameObject("BubbleBoiShieldExpiryTinter");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            instance = go.AddComponent<ShieldExpiryTinter>();
            block = new MaterialPropertyBlock();
        }

        private void Update()
        {
            if (target == null)
                return;
            if (!target.IsElectromagnetShieldActive)
            {
                if (tintApplied)
                    ResetTint();
                target = null;
                cachedRenderers = null;
                return;
            }

            Plugin plugin = Plugin.Instance;
            if (plugin == null || !plugin.expiryWarningEnabledConfig.Value)
            {
                if (tintApplied)
                    ResetTint();
                return;
            }

            float duration = GameManager.ItemSettings != null
                ? GameManager.ItemSettings.ElectromagnetShieldDuration
                : 5f;
            float remaining = duration - (float)(Time.timeAsDouble - activationTimestamp);
            float window = plugin.expiryWarningSecondsConfig.Value;
            if (remaining > window || remaining <= 0f)
            {
                if (tintApplied)
                    ResetTint();
                return;
            }

            // 0..1 across the warning window. First half goes white→orange, second half
            // continues orange→red so the visual cue accelerates as time runs out.
            float t = 1f - (remaining / window);
            Color tint = t < 0.5f
                ? Color.Lerp(Color.white, OrangeWarning, t * 2f)
                : Color.Lerp(OrangeWarning, Color.red, (t - 0.5f) * 2f);

            EnsureRenderers();
            ApplyTint(tint);
            tintApplied = true;
        }

        private void EnsureRenderers()
        {
            if (cachedRenderers != null && cachedRenderers.Length > 0)
                return;
            if (target == null || target.ElectromagnetShieldCollider == null)
                return;
            cachedRenderers = target.ElectromagnetShieldCollider
                .GetComponentsInChildren<Renderer>(includeInactive: true);
        }

        private void ApplyTint(Color tint)
        {
            if (cachedRenderers == null)
                return;
            foreach (Renderer renderer in cachedRenderers)
            {
                if (renderer == null)
                    continue;

                // ParticleSystemRenderer ignores MaterialPropertyBlock color overrides; new
                // particles take their color from the ParticleSystem.MainModule.startColor.
                if (renderer is ParticleSystemRenderer && renderer.TryGetComponent(out ParticleSystem ps))
                {
                    if (!originalStartColors.ContainsKey(ps))
                        originalStartColors[ps] = ps.main.startColor;
                    ParticleSystem.MainModule main = ps.main;
                    main.startColor = new ParticleSystem.MinMaxGradient(tint);
                    continue;
                }

                renderer.GetPropertyBlock(block);
                foreach (int id in ColorPropertyIds)
                    block.SetColor(id, tint);
                renderer.SetPropertyBlock(block);
            }
        }

        private void ResetTint()
        {
            if (cachedRenderers != null)
            {
                foreach (Renderer renderer in cachedRenderers)
                {
                    if (renderer == null)
                        continue;
                    if (renderer is ParticleSystemRenderer)
                        continue; // restored below from originalStartColors
                    // Passing an empty block clears overrides; renderer falls back to whatever
                    // color the underlying material defines.
                    renderer.SetPropertyBlock(null);
                }
            }
            foreach (var pair in originalStartColors)
            {
                if (pair.Key == null)
                    continue;
                ParticleSystem.MainModule main = pair.Key.main;
                main.startColor = pair.Value;
            }
            originalStartColors.Clear();
            tintApplied = false;
        }
    }

    [HarmonyPatch]
    internal static class WaterWalkPatches
    {
        private const float DecisionLogIntervalSeconds = 1.0f;
        private static readonly FieldInfo UprightColliderField = AccessTools.Field(typeof(PlayerMovement), "uprightCollider");
        private static readonly FieldInfo GroundDataField = AccessTools.Field(typeof(PlayerMovement), "<GroundData>k__BackingField");
        private static readonly MethodInfo NoClipEnabledGetter = AccessTools.PropertyGetter(typeof(PlayerMovement), "NoClipEnabled");
        private static readonly Dictionary<int, double> LastDecisionLogTimes = new Dictionary<int, double>();
        private static readonly Dictionary<int, bool> LastInjectedGroundState = new Dictionary<int, bool>();

        [HarmonyPatch(typeof(PlayerMovement), "PerformGroundCheck")]
        [HarmonyPostfix]
        private static void PerformGroundCheckPostfix(PlayerMovement __instance, ref bool __result)
        {
            // Two-state fix:
            //  (a) If vanilla finds real ground AT OR ABOVE the water surface (true sand bank /
            //      fairway / etc.), leave it alone — overriding it would wedge the player into
            //      our NotTerrain proxy.
            //  (b) If vanilla finds ground BELOW the water surface (shallow sand floor under
            //      water), the player is supposed to be water-walking, not standing on the
            //      submerged sand — proceed with synthetic injection as if no ground was found.
            // Earlier 0.2.0 returned on any vanilla-found ground, which broke (b) and stranded
            // the shielded player wading from sand into shallow water.
            if (!TryGetWaterSurfacePoint(__instance, out Vector3 waterPoint, out string reason))
            {
                if (__result)
                    TrackInjectedGroundState(__instance, isInjected: false);
                else
                    MaybeLogDecision(__instance, $"synthetic ground skipped: {reason}");
                TrackInjectedGroundState(__instance, isInjected: false);
                return;
            }

            if (__result)
            {
                Vector3 vanillaGroundPoint = __instance.GroundData.point;
                const float SubmergedTolerance = 0.05f;
                if (vanillaGroundPoint.y >= waterPoint.y - SubmergedTolerance)
                {
                    MaybeLogDecision(__instance, $"keeping vanilla ground at y={vanillaGroundPoint.y:F3} (water y={waterPoint.y:F3})");
                    TrackInjectedGroundState(__instance, isInjected: false);
                    return;
                }
                MaybeLogDecision(__instance, $"vanilla ground submerged y={vanillaGroundPoint.y:F3} < water y={waterPoint.y:F3} — overriding with synthetic surface");
            }

            CapsuleCollider uprightCollider = UprightColliderField?.GetValue(__instance) as CapsuleCollider;
            if (uprightCollider == null)
            {
                MaybeLogDecision(__instance, "synthetic ground skipped: uprightCollider missing");
                TrackInjectedGroundState(__instance, isInjected: false);
                return;
            }

            Vector3 rayOrigin = uprightCollider.transform.TransformPoint(uprightCollider.center);
            float maxGroundingDistance = uprightCollider.center.y + 0.35f;
            if (rayOrigin.y - waterPoint.y > maxGroundingDistance)
            {
                MaybeLogDecision(__instance, $"synthetic ground skipped: water surface too far below ray origin ({rayOrigin.y - waterPoint.y:F3}m > {maxGroundingDistance:F3}m)");
                TrackInjectedGroundState(__instance, isInjected: false);
                return;
            }

            __instance.NetworkgroundTerrainType = GroundTerrainType.NotTerrain;
            __instance.NetworkgroundTerrainDominantGlobalLayer = TerrainLayer.Fairway;
            GroundDataField?.SetValue(__instance, new PlayerGroundData
            {
                point = waterPoint,
                contactPoint = waterPoint,
                normal = Vector3.up,
                collider = WaterSurfaceProxy.Collider,
                hasRigidbody = false,
                rigidbody = null
            });

            bool replacedVanillaGround = __result;
            __result = true;
            MaybeLogDecision(__instance, $"synthetic ground injected at waterY={waterPoint.y:F3}, playerY={__instance.Position.y:F3}, replacedVanillaGround={replacedVanillaGround}, bounds={__instance.PlayerInfo?.LevelBoundsTracker?.AuthoritativeBoundsState}");
            TrackInjectedGroundState(__instance, isInjected: true);
        }

        [HarmonyPatch(typeof(PlayerMovement), "FixedUpdate")]
        [HarmonyPostfix]
        private static void FixedUpdatePostfix(PlayerMovement __instance)
        {
            if (!TryGetWaterSurfacePoint(__instance, out Vector3 waterPoint, out _))
                return;

            LevelBoundsTracker tracker = __instance.PlayerInfo?.LevelBoundsTracker;
            if (tracker == null)
                return;

            Rigidbody rigidbody = __instance.PlayerInfo?.Rigidbody;
            if (rigidbody == null || rigidbody.isKinematic)
                return;

            Vector3 position = rigidbody.position;
            if (position.y >= waterPoint.y)
                return;

            float raiseDistance = waterPoint.y - position.y;
            position.y = waterPoint.y;
            rigidbody.position = position;

            Vector3 velocity = rigidbody.linearVelocity;
            if (velocity.y < 0f)
            {
                velocity.y = 0f;
                rigidbody.linearVelocity = velocity;
            }

            MaybeLogDecision(__instance, $"surface correction applied: raised by {raiseDistance:F3}m to waterY={waterPoint.y:F3}", force: true);
        }

        [HarmonyPatch(typeof(PlayerMovement), "OnServerBoundsStateChanged")]
        [HarmonyPrefix]
        private static bool OnServerBoundsStateChangedPrefix(PlayerMovement __instance, BoundsState currentState)
        {
            if (!currentState.IsInOutOfBoundsHazard())
                return true;

            if (!ShouldWaterWalk(__instance))
            {
                MaybeLogDecision(__instance, $"server hazard passthrough: currentState={currentState}, reason=water-walk conditions not met");
                return true;
            }

            bool isWaterHazard = IsWaterHazard(__instance.PlayerInfo?.LevelBoundsTracker, currentState);
            if (isWaterHazard)
            {
                MaybeLogDecision(__instance, $"server water elimination suppressed: currentState={currentState}");
                return false;
            }

            MaybeLogDecision(__instance, $"server hazard passthrough: currentState={currentState}, reason=hazard is not water");
            return true;
        }

        private static bool TryGetWaterSurfacePoint(PlayerMovement movement, out Vector3 waterPoint, out string reason)
        {
            waterPoint = default;
            reason = string.Empty;
            if (!ShouldWaterWalkLocally(movement))
            {
                reason = "local water-walk conditions not met";
                return false;
            }

            LevelBoundsTracker tracker = movement.PlayerInfo?.LevelBoundsTracker;
            if (tracker == null)
            {
                reason = "level bounds tracker missing";
                return false;
            }

            if (!IsWaterHazard(tracker, tracker.AuthoritativeBoundsState) && !tracker.IsInOrOverOutOfBoundsHazard())
            {
                reason = $"not over a water hazard (bounds={tracker.AuthoritativeBoundsState})";
                return false;
            }

            waterPoint = movement.Position;
            waterPoint.y = tracker.CurrentOutOfBoundsHazardWorldHeightLocalOnly;
            return true;
        }

        private static bool ShouldWaterWalk(PlayerMovement movement)
        {
            if (movement == null)
                return false;

            if (movement.PlayerInfo == null || !movement.PlayerInfo.IsElectromagnetShieldActive)
                return false;

            if (movement.PlayerInfo.ActiveGolfCartSeat.IsValid())
                return false;

            if ((bool)(NoClipEnabledGetter?.Invoke(null, null) ?? false))
                return false;

            return true;
        }

        private static bool ShouldWaterWalkLocally(PlayerMovement movement)
        {
            return movement != null && movement.isLocalPlayer && ShouldWaterWalk(movement);
        }

        private static void TrackInjectedGroundState(PlayerMovement movement, bool isInjected)
        {
            if (movement == null)
                return;

            int key = movement.GetInstanceID();
            bool hadPrevious = LastInjectedGroundState.TryGetValue(key, out bool previous);
            if (!hadPrevious || previous != isInjected)
            {
                LastInjectedGroundState[key] = isInjected;
                MaybeLogDecision(movement, isInjected ? "synthetic ground state changed: active" : "synthetic ground state changed: inactive", force: true);
            }
        }

        private static void MaybeLogDecision(PlayerMovement movement, string message, bool force = false)
        {
            if (movement == null || Plugin.Log == null)
                return;
            if (Plugin.Instance == null || !Plugin.Instance.verboseLoggingConfig.Value)
                return;

            int key = movement.GetInstanceID();
            double now = Time.timeAsDouble;
            if (!force && LastDecisionLogTimes.TryGetValue(key, out double lastLog) && now - lastLog < DecisionLogIntervalSeconds)
                return;

            LastDecisionLogTimes[key] = now;

            PlayerInfo playerInfo = movement.PlayerInfo;
            LevelBoundsTracker tracker = playerInfo?.LevelBoundsTracker;
            Plugin.Log.LogInfo(
                $"[BubbleBoi] player={playerInfo?.name ?? movement.name} " +
                $"local={movement.isLocalPlayer} shield={playerInfo?.IsElectromagnetShieldActive ?? false} " +
                $"grounded={movement.IsGrounded} visible={movement.IsVisible} " +
                $"inCart={playerInfo?.ActiveGolfCartSeat.IsValid() ?? false} " +
                $"bounds={(tracker != null ? tracker.AuthoritativeBoundsState.ToString() : "null")} " +
                $"waterY={(tracker != null ? tracker.CurrentOutOfBoundsHazardWorldHeightLocalOnly.ToString("F3") : "n/a")} " +
                $"posY={movement.Position.y:F3} :: {message}");
        }

        // Capture the local-player activation timestamp so the tinter knows when to start
        // ramping. The game's own copy of this is a private field on PlayerInfo and is local-
        // only; mirroring it here avoids reflection on the hot path.
        [HarmonyPatch(typeof(PlayerInfo), nameof(PlayerInfo.LocalPlayerActivateElectromagnetShield))]
        [HarmonyPostfix]
        private static void LocalPlayerActivateElectromagnetShieldPostfix(PlayerInfo __instance)
        {
            ShieldExpiryTinter.OnLocalShieldActivated(__instance);
        }

        private static bool IsWaterHazard(LevelBoundsTracker tracker, BoundsState boundsState)
        {
            if (tracker == null)
                return false;

            if (boundsState.HasState(BoundsState.InMainOutOfBoundsHazard))
                return MainOutOfBoundsHazard.Type == OutOfBoundsHazard.Water;

            if (tracker.CurrentSecondaryHazardLocalOnly != null)
                return tracker.CurrentSecondaryHazardLocalOnly.Type == OutOfBoundsHazard.Water;

            return MainOutOfBoundsHazard.Type == OutOfBoundsHazard.Water;
        }
    }
}
