using System.Reflection;
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
        public const string ModVersion = "0.1.0";

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
        private static readonly FieldInfo UprightColliderField = AccessTools.Field(typeof(PlayerMovement), "uprightCollider");
        private static readonly FieldInfo GroundDataField = AccessTools.Field(typeof(PlayerMovement), "<GroundData>k__BackingField");
        private static readonly MethodInfo NoClipEnabledGetter = AccessTools.PropertyGetter(typeof(PlayerMovement), "NoClipEnabled");

        [HarmonyPatch(typeof(PlayerMovement), "PerformGroundCheck")]
        [HarmonyPostfix]
        private static void PerformGroundCheckPostfix(PlayerMovement __instance, ref bool __result)
        {
            if (__result)
                return;

            if (!TryGetWaterSurfacePoint(__instance, out Vector3 waterPoint))
                return;

            CapsuleCollider uprightCollider = UprightColliderField?.GetValue(__instance) as CapsuleCollider;
            if (uprightCollider == null)
                return;

            Vector3 rayOrigin = uprightCollider.transform.TransformPoint(uprightCollider.center);
            float maxGroundingDistance = uprightCollider.center.y + 0.35f;
            if (rayOrigin.y - waterPoint.y > maxGroundingDistance)
                return;

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

            __result = true;
        }

        [HarmonyPatch(typeof(PlayerMovement), "OnServerBoundsStateChanged")]
        [HarmonyPrefix]
        private static bool OnServerBoundsStateChangedPrefix(PlayerMovement __instance, BoundsState currentState)
        {
            if (!currentState.IsInOutOfBoundsHazard())
                return true;

            if (!ShouldWaterWalk(__instance))
                return true;

            return !IsWaterHazard(__instance.PlayerInfo?.LevelBoundsTracker, currentState);
        }

        private static bool TryGetWaterSurfacePoint(PlayerMovement movement, out Vector3 waterPoint)
        {
            waterPoint = default;
            if (!ShouldWaterWalkLocally(movement))
                return false;

            LevelBoundsTracker tracker = movement.PlayerInfo?.LevelBoundsTracker;
            if (tracker == null)
                return false;

            if (!IsWaterHazard(tracker, tracker.AuthoritativeBoundsState) && !tracker.IsInOrOverOutOfBoundsHazard())
                return false;

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
