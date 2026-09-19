using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LocalTweaks;
using RoR2.Projectile;

namespace SeamstressConfigurable
{
    internal static class DamageSettings
    {
        private const string ConfigType = "SeamstressMod.Seamstress.Content.SeamstressConfig";
        private const string States = "SeamstressMod.Seamstress.SkillStates.";
        private const string Components = "SeamstressMod.Seamstress.Components.";
        private static readonly Harmony harmony = new Harmony(Plugin.Guid + ".damage");
        private static readonly ConfigEntry<int>[] values = new ConfigEntry<int>[10];
        private static readonly Dictionary<MethodBase, int> expectedReads = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<string, int> fields = new Dictionary<string, int>
        {
            ["trimDamageCoefficient"] = 0,
            ["trimThirdDamageCoefficient"] = 1,
            ["scissorSlashDamageCoefficient"] = 2,
            ["flurryDamageCoefficient"] = 4,
            ["telekinesisDamageCoefficient"] = 7,
            ["scissorDamageCoefficient"] = 8
        };
        private static Type trim;
        private static Type flurry;
        private static Type scissorImpact;
        private static FieldInfo swingIndex;

        internal static void Install(ConfigFile config, ManualLogSource logger)
        {
            try
            {
                trim = AccessTools.TypeByName(States + "Trim");
                flurry = AccessTools.TypeByName(States + "Flurry");
                scissorImpact = AccessTools.TypeByName(Components + "ScissorImpact");
                swingIndex = AccessTools.Field(trim, "swingIndex");
                if (trim == null || flurry == null || scissorImpact == null || swingIndex == null)
                    throw new TypeLoadException("The Seamstress attack types no longer match.");

                Bind(config, 0, "Trim", "Claw damage percent", "trimDamageCoefficient", "Damage of each normal bare-hand hit.");
                Bind(config, 1, "Trim", "Third claw hit damage percent", "trimThirdDamageCoefficient", "Damage of the third bare-hand hit in the combo.");
                Bind(config, 2, "Trim", "Scissor damage percent", "scissorSlashDamageCoefficient", "Extra scissor hit damage when that hand has a scissor.");
                Bind(config, 3, "Trim", "Third scissor hit damage percent", "scissorSlashDamageCoefficient", "Extra scissor hit damage on the third attack, when both scissors are present.");
                Bind(config, 4, "Flurry", "Claw damage percent", "flurryDamageCoefficient", "Damage of each bare-hand hit.");
                Bind(config, 5, "Flurry", "Scissor damage percent", "scissorSlashDamageCoefficient", "Extra scissor hit damage when that hand has a scissor.");
                Bind(config, 6, "Clip", "Damage per snip percent", "clipDamageCoefficient", "Damage per snip. The two starting snips and extra snips from needles are unchanged.");
                Bind(config, 7, "Planar Manipulation", "Base damage percent", "telekinesisDamageCoefficient", "Base coefficient for held and thrown impacts. Needle, velocity and victim-health terms and caps are unchanged; zero does not remove separate health-based damage.");
                Bind(config, 8, "Skewer", "Initial hit damage percent", "scissorDamageCoefficient", "Damage of the impact blast when the fired scissor first lands.");
                Bind(config, 9, "Skewer", "Symbiotic pickup damage percent", "scissorPickupDamageCoefficient", "Damage of the slash when the scissor is picked up or detonates. Independent of the initial hit.");

                PatchRead(States + "Trim", "OnEnter", 2);
                PatchRead(States + "Flurry", "OnEnter", 1);
                PatchRead("SeamstressMod.Modules.BaseStates.BaseMeleeAttack", "FixedUpdate", 1);
                PatchRead(States + "Telekinesis", "FixedUpdate", 4);
                PatchRead(Components + "DetonateOnImpactThrownTelekinesis", "FixedUpdate", 2);
                PatchRead(Components + "ScissorImpact", "OnProjectileImpact", 1);
                harmony.Patch(PatchTools.Method(States + "Clip", "OnEnter"),
                    prefix: new HarmonyMethod(typeof(DamageSettings), nameof(ChangeClipDamage)));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(ProjectileExplosion), "DetonateServer"),
                    transpiler: new HarmonyMethod(typeof(DamageSettings), nameof(ChangePickupDamage)));
                logger.LogInfo("Primary, secondary and Skewer damage settings loaded.");
            }
            catch (Exception exception)
            {
                Uninstall();
                logger.LogError($"Damage settings were not applied. Existing health and Retaliate settings are unaffected.\n{exception}");
            }
        }

        internal static void Uninstall()
        {
            harmony.UnpatchSelf();
            expectedReads.Clear();
        }

        private static void Bind(ConfigFile config, int index, string section, string name, string fieldName, string description)
        {
            var field = AccessTools.Field(AccessTools.TypeByName(ConfigType), fieldName);
            if (!(field?.GetValue(null) is ConfigEntry<float> original))
                throw new MissingFieldException(ConfigType, fieldName);
            int vanilla = (int)Math.Round((float)original.DefaultValue * 100f);
            values[index] = TweakSettings.Slider(config, section, name, vanilla, 0, Math.Max(1000, vanilla),
                description + " Uses percent of base damage, not a bonus percent. Applies to the next attack or damage calculation.", Plugin.Guid, Plugin.Name);
        }

        private static void PatchRead(string type, string method, int count)
        {
            var target = PatchTools.Method(type, method);
            expectedReads.Add(target, count);
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(DamageSettings), nameof(ChangeDamageReads)));
        }

        private static void ChangeClipDamage(ref float ___damageCoefficient) => ___damageCoefficient = values[6].Value / 100f;

        private static float DamageValue(float original, int index, object state)
        {
            if (index == 2)
            {
                if (trim.IsInstanceOfType(state))
                    index = (int)swingIndex.GetValue(state) == 2 ? 3 : 2;
                else if (flurry.IsInstanceOfType(state))
                    index = 5;
                else
                    return original;
            }
            return values[index].Value / 100f;
        }

        private static float PickupValue(float original, ProjectileExplosion explosion) =>
            explosion.GetComponent(scissorImpact) ? values[9].Value / 100f : original;

        private static IEnumerable<CodeInstruction> ChangeDamageReads(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            int count = 0;
            for (int i = 0; i < code.Count - 1; i++)
            {
                if (code[i].opcode != OpCodes.Ldsfld || !(code[i].operand is FieldInfo field) ||
                    field.DeclaringType?.FullName != ConfigType || !fields.TryGetValue(field.Name, out int index) ||
                    !(code[i + 1].operand is MethodInfo getter) || getter.Name != "get_Value" || getter.ReturnType != typeof(float))
                    continue;
                code.InsertRange(i + 2, new[]
                {
                    new CodeInstruction(OpCodes.Ldc_I4, index),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DamageSettings), nameof(DamageValue)))
                });
                i += 4;
                count++;
            }
            if (!expectedReads.TryGetValue(__originalMethod, out int expected) || count != expected)
                throw new InvalidOperationException($"Unexpected damage reads in {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}: {count}.");
            return code;
        }

        private static IEnumerable<CodeInstruction> ChangePickupDamage(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int count = 0;
            for (int i = 0; i < code.Count - 2; i++)
            {
                if (code[i].opcode != OpCodes.Ldfld || !(code[i].operand is FieldInfo field) ||
                    field.DeclaringType != typeof(ProjectileExplosion) || field.Name != "blastDamageCoefficient" ||
                    code[i + 1].opcode != OpCodes.Mul || code[i + 2].opcode != OpCodes.Stfld ||
                    !(code[i + 2].operand is FieldInfo destination) || destination.Name != "baseDamage")
                    continue;
                code.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DamageSettings), nameof(PickupValue)))
                });
                i += 2;
                count++;
            }
            if (count != 1)
                throw new InvalidOperationException($"Expected one pickup damage calculation, found {count}.");
            return code;
        }
    }
}
