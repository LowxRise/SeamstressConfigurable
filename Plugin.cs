using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LocalTweaks;
using RoR2;

namespace SeamstressConfigurable
{
    [BepInPlugin(Guid, Name, "1.2.0")]
    [BepInDependency("com.kenko.Seamstress")]
    [BepInDependency("com.rune580.riskofoptions")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.youssef.SeamstressConfigurable";
        public const string Name = "Seamstress Configurable";
        private static ConfigEntry<int> lifesteal;
        private static ConfigEntry<int> skewerCost;
        private static ConfigEntry<bool> cleanRetaliate;
        private static FieldInfo insatiable;
        private static FieldInfo needles;
        private static FieldInfo selfBleed;
        private readonly Harmony harmony = new Harmony(Guid);

        private void Awake()
        {
            lifesteal = TweakSettings.Slider(Config, "It Hungers", "Lifesteal percent", 15, 1, 25,
                "Maximum lifesteal coefficient. Still scales with missing health and the hit's proc coefficient. Applies immediately.", Guid, Name);
            cleanRetaliate = TweakSettings.Checkbox(Config, "Retaliate", "No self bleed or needle gain", true,
                "While Retaliate's Insatiable is active, prevent its self-bleed damage and needle gains. Keep barrier conversion, duration and activation unchanged. Applies immediately.", Guid, Name);
            skewerCost = TweakSettings.Slider(Config, "Skewer", "Health cost percent", 15, 0, 25,
                "Percent of full combined health spent when firing Skewer. Applies on the next shot.", Guid, Name);
            try
            {
                var buffs = AccessTools.TypeByName("SeamstressMod.Seamstress.Content.SeamstressBuffs");
                insatiable = AccessTools.Field(buffs, "SeamstressInsatiableBuff");
                needles = AccessTools.Field(buffs, "Needles");
                selfBleed = AccessTools.Field(AccessTools.TypeByName("SeamstressMod.Seamstress.Content.SeamstressDots"), "SeamstressSelfBleed");
                if (insatiable == null || needles == null || selfBleed == null)
                    throw new MissingFieldException("Seamstress buff or self-bleed fields were not found.");
                harmony.Patch(PatchTools.Method("SeamstressMod.Seamstress.Components.SeamstressBaseDamageController", "ApplyLifesteal"),
                    transpiler: new HarmonyMethod(typeof(Plugin), nameof(ChangeLifesteal)));
                harmony.Patch(PatchTools.Method("SeamstressMod.Seamstress.SkillStates.FireScissor", "OnEnter"),
                    transpiler: new HarmonyMethod(typeof(Plugin), nameof(ChangeSkewerCost)));
                harmony.Patch(AccessTools.Method(typeof(HealthComponent), "TakeDamage", new[] { typeof(DamageInfo) }),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowDamage)));
                harmony.Patch(PatchTools.Method("SeamstressMod.Seamstress.SkillStates.HeartStandBy", "HandleBleed"),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowHeartBleed)));
                harmony.Patch(AccessTools.Method(typeof(CharacterBody), "AddBuff", new[] { typeof(BuffDef) }),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowBuff)));
                Logger.LogInfo("Seamstress tweaks loaded.");
            }
            catch (Exception exception)
            {
                harmony.UnpatchSelf();
                Logger.LogError($"Seamstress tweaks were not applied. The installed Seamstress version may have changed.\n{exception}");
            }
            DamageSettings.Install(Config, Logger);
            SkewerTracking.Install(Logger);
        }

        private void OnDestroy()
        {
            SkewerTracking.Uninstall();
            DamageSettings.Uninstall();
            harmony.UnpatchSelf();
        }

        private static float LifestealFraction() => lifesteal.Value / 100f;
        private static float SkewerFraction() => skewerCost.Value / 100f;

        private static IEnumerable<CodeInstruction> ChangeLifesteal(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var matches = new List<int>();
            for (int i = 0; i < code.Count - 1; i++)
            {
                if (code[i].opcode == OpCodes.Ldsfld && code[i].operand is FieldInfo field &&
                    field.Name == "passiveLifeSteal" && code[i + 1].operand is MethodInfo getter &&
                    getter.Name == "get_Value" && getter.ReturnType == typeof(float))
                    matches.Add(i);
            }
            if (matches.Count != 1)
                throw new InvalidOperationException("The It Hungers lifesteal read no longer matches.");
            int index = matches[0];
            code[index].opcode = OpCodes.Nop;
            code[index].operand = null;
            code[index + 1].opcode = OpCodes.Call;
            code[index + 1].operand = AccessTools.Method(typeof(Plugin), nameof(LifestealFraction));
            return code;
        }

        private static IEnumerable<CodeInstruction> ChangeSkewerCost(IEnumerable<CodeInstruction> instructions) =>
            PatchTools.ReplaceFloat(instructions, 0.15f, AccessTools.Method(typeof(Plugin), nameof(SkewerFraction)));

        private static bool IsRetaliateInsatiable(CharacterBody body)
        {
            if (!cleanRetaliate.Value || !body)
                return false;
            var utility = body.skillLocator ? body.skillLocator.utility : null;
            var buff = insatiable.GetValue(null) as BuffDef;
            return utility && utility.skillDef &&
                utility.skillDef.skillNameToken == "KENKO_SEAMSTRESS_UTILITY_PARRY_NAME" &&
                buff && body.HasBuff(buff);
        }

        private static bool AllowDamage(HealthComponent __instance, DamageInfo damageInfo) =>
            damageInfo.dotIndex != (DotController.DotIndex)selfBleed.GetValue(null) ||
            !IsRetaliateInsatiable(__instance.body);

        private static bool AllowHeartBleed(CharacterBody ___ownerBody) => !IsRetaliateInsatiable(___ownerBody);

        private static bool AllowBuff(CharacterBody __instance, BuffDef buffDef) =>
            buffDef != needles.GetValue(null) as BuffDef || !IsRetaliateInsatiable(__instance);
    }
}
