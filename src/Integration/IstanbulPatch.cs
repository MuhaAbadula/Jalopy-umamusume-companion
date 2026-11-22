using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// In the game the uncle leaves for good on the night in Istanbul: UncleLogicC.SuitCaseOutOfBed sets
    /// uncleGoneForever when routeLevel == 5 and the car still heads east, and in the morning
    /// BedLogicC / UncleReadyForNextDay move him off the map and leave his suitcase with the letter.
    /// A prefix/postfix on that coroutine's MoveNext undoes the flag in the same step, so she wakes up in Istanbul,
    /// gets into the car and rides home with you (the game already handles a passenger on the way back, the same as
    /// turning round halfway). A save where she has already gone is left as it is.
    /// </summary>
    public static class IstanbulPatch
    {
        static bool applied;
        public static bool Stay = true;
        /// <summary>She stayed this session (Time.time), -1 if not.</summary>
        public static float KeptAt = -1f;

        public static void Apply()
        {
            if (applied) return;
            var iter = AccessTools.FirstInner(typeof(UncleLogicC), t => t.Name.Contains("SuitCaseOutOfBed"));
            if (iter == null)
            {
                Debug.LogWarning("[Umamusume companion] SuitCaseOutOfBed iterator not found: she will leave in Istanbul");
                return;
            }
            var h = new Harmony("osobl.jalopy.umamusumecompanion.istanbul");
            h.Patch(AccessTools.Method(iter, "MoveNext"),
                prefix: new HarmonyMethod(typeof(IstanbulPatch), nameof(Before)),
                postfix: new HarmonyMethod(typeof(IstanbulPatch), nameof(After)));
            applied = true;
        }

        static void Before(object __instance, out bool __state)
        {
            var u = Traverse.Create(__instance).Field("$this").GetValue<UncleLogicC>();
            __state = u != null && u.uncleGoneForever;
        }

        static void After(object __instance, bool __state)
        {
            if (!Stay || __state) return;
            var u = Traverse.Create(__instance).Field("$this").GetValue<UncleLogicC>();
            if (u == null || !u.uncleGoneForever) return;
            u.uncleGoneForever = false;
            KeptAt = Time.time;
            if (ReturnTrip.Current != null) ReturnTrip.Current.Begin();
            Debug.Log("[Umamusume companion] istanbul: the game sent her away for good, she stays and rides back with you");
            JaLoader.Console.Log(CompanionStorage.ModId, "Istanbul: she stays with you for the way back.");
        }
    }
}
