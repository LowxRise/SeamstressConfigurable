using System;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using LocalTweaks;
using RoR2;
using RoR2.Projectile;
using UnityEngine;
using UnityEngine.Networking;

namespace SeamstressConfigurable
{
    internal static class SkewerTracking
    {
        private static readonly Harmony harmony = new Harmony(Plugin.Guid + ".tracking");
        private static Type scissorImpact;

        internal static void Install(ManualLogSource logger)
        {
            try
            {
                scissorImpact = AccessTools.TypeByName("SeamstressMod.Seamstress.Components.ScissorImpact");
                var impact = PatchTools.Method("SeamstressMod.Seamstress.Components.ScissorImpact", "TrySticking");
                if (impact.ReturnType != typeof(bool))
                    throw new MissingMethodException("Skewer's sticking method no longer matches.");

                harmony.Patch(AccessTools.Method(typeof(ProjectileController), "Start"),
                    postfix: new HarmonyMethod(typeof(SkewerTracking), nameof(TrackEnemies)));
                harmony.Patch(impact,
                    postfix: new HarmonyMethod(typeof(SkewerTracking), nameof(StopAfterLanding)));
                logger.LogInfo("Skewer projectile tracking loaded.");
            }
            catch (Exception exception)
            {
                harmony.UnpatchSelf();
                logger.LogError($"Skewer tracking was not applied.\n{exception}");
            }
        }

        internal static void Uninstall() => harmony.UnpatchSelf();

        private static void TrackEnemies(ProjectileController __instance)
        {
            if (!NetworkServer.active || __instance.isPrediction || !__instance.GetComponent(scissorImpact))
                return;

            var finder = __instance.GetComponent<ProjectileDirectionalTargetFinder>();
            var steering = __instance.GetComponent<ProjectileSteerTowardTarget>();
            var target = __instance.GetComponent<ProjectileTargetComponent>();
            if (!finder || !steering || !target)
                return;

            finder.lookRange = 60f;
            finder.lookCone = 60f;
            finder.targetSearchInterval = 0.5f;
            finder.onlySearchIfNoTarget = true;
            finder.allowTargetLoss = false;
            finder.testLoS = true;
            finder.ignoreAir = false;
            finder.flierAltitudeTolerance = float.PositiveInfinity;
            finder.enabled = true;
            steering.yAxisOnly = false;
            steering.rotationSpeed = 250f;
            steering.enabled = true;

            var teams = TeamMask.allButNeutral;
            teams.RemoveTeam(TeamComponent.GetObjectTeam(__instance.owner));
            var search = new BullseyeSearch
            {
                searchOrigin = __instance.transform.position,
                searchDirection = __instance.transform.forward,
                maxDistanceFilter = 60f,
                maxAngleFilter = 30f,
                teamMaskFilter = teams,
                filterByLoS = true,
                sortMode = BullseyeSearch.SortMode.Angle
            };
            search.RefreshCandidates();
            var enemy = search.GetResults().FirstOrDefault(hit => hit.healthComponent && hit.healthComponent.alive);
            if (enemy)
                target.target = enemy.transform;
        }

        private static void StopAfterLanding(Component __instance, bool __result)
        {
            if (!NetworkServer.active || !__result)
                return;

            var steering = __instance.GetComponent<ProjectileSteerTowardTarget>();
            if (steering)
                steering.enabled = false;
        }
    }
}
