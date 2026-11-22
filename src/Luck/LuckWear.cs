using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 21: normal driving wear of the carburettor, air filter, engine and tyres is multiplied by LuckFx.WearMult()
    /// (0.8 .. 1.1 by the day's luck). The four *DurabilityLossCalc methods set a flag while they run; inside them
    /// EngineComponentC.set_Condition scales the loss (value below the current condition). Crashes, potholes, skidding,
    /// repairs, paint cans, save loading and the Condition = 0 blow-out never run inside the flag or are not a loss
    /// to a positive value, so they stay as in the game. Silent: no remark, only the log.
    /// </summary>
    public static class LuckWear
    {
        static bool applied;
        static bool inWear, inEngine;
        /// <summary>Task 87 (Tachyon's A1 additive): extra multiplier on engine block wear only.</summary>
        public static float EngineExtra = 1f;

        // minute summary (real time), nothing is logged per frame
        static float minuteAt = -1f;
        static float gameLoss, appliedLoss;
        static int writes, ticks;

        public static void Apply()
        {
            if (applied) return;
            applied = true;
            var h = new Harmony("osobl.jalopy.uncleswap.luckwear");
            int ok = 0;
            foreach (string name in new[] { "CarburettorDurabilityLossCalc", "AirFilterDurabilityLossCalc", "EngineDurabilityLossCalc", "TyreDurabilityLossCalc" })
            {
                var m = AccessTools.Method(typeof(EngineComponentC), name);
                if (m == null) { Debug.LogWarning("[UncleSwap] LuckFx: Wear: " + name + " not found, skipped"); continue; }
                try
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(LuckWear), name == "EngineDurabilityLossCalc" ? nameof(EnterEngine) : nameof(Enter)), finalizer: new HarmonyMethod(typeof(LuckWear), nameof(Leave)));
                    ok++;
                }
                catch (System.Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Wear: " + name + " patch failed: " + e.Message); }
            }
            var set = AccessTools.PropertySetter(typeof(EngineComponentC), "Condition");
            if (set == null) Debug.LogWarning("[UncleSwap] LuckFx: Wear: EngineComponentC.set_Condition not found, nothing scaled");
            else
            {
                try { h.Patch(set, prefix: new HarmonyMethod(typeof(LuckWear), nameof(SetCondition))); }
                catch (System.Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Wear: set_Condition patch failed: " + e.Message); set = null; }
            }
            Debug.Log("[UncleSwap] LuckFx: Wear patched " + ok + "/4" + (set != null ? "" : " (set_Condition missing)"));
        }

        static void Enter() { inWear = true; ticks++; }

        static void EnterEngine() { inWear = true; inEngine = true; ticks++; }

        static void Leave() { inWear = false; inEngine = false; }

        static void SetCondition(EngineComponentC __instance, ref float value)
        {
            if (!inWear || __instance == null) return;
            float cur = __instance.condition;
            if (value <= 0f || value >= cur) return;   // blow-out to 0, regain (reverse), no change: not a wear step
            float loss = cur - value;
            float mult = LuckFx.WearMult() * (inEngine ? EngineExtra : 1f);
            float scaled = cur - loss * mult;
            if (mult != 1f && scaled > 0f) value = scaled;   // never push a step to or below 0 myself
            gameLoss += loss;
            appliedLoss += cur - value;
            writes++;
            Summary();
        }

        static void Summary()
        {
            float now = Time.realtimeSinceStartup;
            if (minuteAt < 0f) { minuteAt = now; return; }
            if (now - minuteAt < 60f) return;
            minuteAt = now;
            float pct = gameLoss > 0f ? appliedLoss / gameLoss * 100f : 100f;
            LuckFx.Log("Wear", "game loss " + gameLoss.ToString("0.0000"),
                "applied " + appliedLoss.ToString("0.0000") + " (" + pct.ToString("0") + "%, " + writes + " steps in " + ticks + " ticks, mult " + LuckFx.WearMult().ToString("0.00") + ")");
            gameLoss = appliedLoss = 0f;
            writes = ticks = 0;
        }
    }

    public partial class FukuDriver
    {
        // Unity calls it on AddComponent, before Build: the one place to hook LuckWear without touching FukuUncle.cs
        void OnEnable()
        {
            try { LuckWear.Apply(); }
            catch (System.Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Wear patch failed: " + e.Message); }
        }
    }
}
