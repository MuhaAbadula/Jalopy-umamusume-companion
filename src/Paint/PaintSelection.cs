using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using JaLoader;

namespace UmamusumeCompanion
{
    internal static class PaintSelection
    {
        static bool installed;

        internal static void Install()
        {
            if (installed) return;
            new Harmony("jalopy.umamusumecompanion.paint").Patch(
                AccessTools.Method(typeof(PaintJobManager), nameof(PaintJobManager.ApplyPaintjob)),
                transpiler: new HarmonyMethod(typeof(PaintSelection), nameof(KeepSelection)));
            installed = true;
            PaintStock.Install();
        }

        static IEnumerable<CodeInstruction> KeepSelection(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            // This loader overwrites its sole argument with PaintJobs[0] on entry.
            // Keep the caller's argument and the loader's normal panel application logic.
            if (code.Count >= 5 && code[0].opcode == OpCodes.Ldarg_0 &&
                code[1].LoadsField(AccessTools.Field(typeof(PaintJobManager), nameof(PaintJobManager.PaintJobs))) &&
                code[2].opcode == OpCodes.Ldc_I4_0 &&
                code[3].Calls(AccessTools.PropertyGetter(typeof(List<PaintJob>), "Item")) &&
                code[4].opcode == OpCodes.Starg_S)
            {
                for (int i = 0; i < 5; i++)
                {
                    code[i].opcode = OpCodes.Nop;
                    code[i].operand = null;
                }
            }
            return code;
        }
    }
}
