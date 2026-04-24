using System.Reflection;
using System.Collections.Generic;
using BepInEx;
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
        public const string ModVersion = "0.1.1";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
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
            if (!TryGetWaterSurfacePoint(__instance, out Vector3 waterPoint, out string reason))
            {
                if (__result)
                    TrackInjectedGroundState(__instance, isInjected: false);
                else
                    MaybeLogDecision(__instance, $"synthetic ground skipped: {reason}");
                TrackInjectedGroundState(__instance, isInjected: false);
                return;
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
