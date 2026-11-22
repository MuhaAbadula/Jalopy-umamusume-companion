using HarmonyLib;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Debug: every route picked on the map is one segment long. The game takes the trip length from the map label
    /// (MapRelayC.distance -> RouteGeneratorC.routeChosenLength in SpawnRoute), which it copied from the route
    /// generator when the map was set up, so changing the generator later had no effect. The map draws routes only
    /// for 3-6 segments, so the label keeps its number: it is swapped to 1 just for SpawnRoute and put back after.
    /// </summary>
    public static class ShortTripPatch
    {
        public static bool Armed;
        static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var h = new Harmony("osobl.jalopy.uncleswap.shorttrip");
            h.Patch(AccessTools.Method(typeof(MapRelayC), "SpawnRoute"),
                prefix: new HarmonyMethod(typeof(ShortTripPatch), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(ShortTripPatch), nameof(Postfix)));
            applied = true;
        }

        static void Prefix(MapRelayC __instance, out int __state)
        {
            __state = __instance.distance;
            if (Armed) __instance.distance = 1;
        }

        static void Postfix(MapRelayC __instance, int __state)
        {
            __instance.distance = __state;
        }
    }
}
