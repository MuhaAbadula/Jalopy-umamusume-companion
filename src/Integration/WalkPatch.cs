using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Game bug, visible at high frame rates: UncleLogicC.Update feeds the Animator walkSpeed = (displacement this
    /// frame) * 8, i.e. it depends on the frame rate. At ~120 fps the value is half of what the animator was tuned
    /// for (60 fps) and hovers around the idle/walk threshold, so the walk keeps restarting and a leg twitches.
    /// After the game sets it, rescale the value to 60 fps and smooth it a little (~0.1 s).
    /// </summary>
    public static class WalkPatch
    {
        const float TuneFps = 60f;
        const float Smooth = 0.1f;
        static float smoothed;
        static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var harmony = new Harmony("osobl.jalopy.uncleswap.walk");
            harmony.Patch(AccessTools.Method(typeof(UncleLogicC), "Update"),
                postfix: new HarmonyMethod(typeof(WalkPatch), nameof(Postfix)));
            applied = true;
        }

        static void Postfix(UncleLogicC __instance)
        {
            if (__instance == null) return;
            var an = __instance.GetComponent<Animator>();
            float dt = Time.deltaTime;
            if (an == null || dt <= 0f) return;
            float v = an.GetFloat("walkSpeed");
            float target = v / (TuneFps * dt);              // per-frame value as if the game ran at 60 fps
            smoothed = Mathf.Lerp(smoothed, target, 1f - Mathf.Exp(-dt / Smooth));
            if (v == 0f && smoothed < 0.05f) smoothed = 0f;  // the game's explicit stops still stop
            an.SetFloat("walkSpeed", smoothed);
        }
    }
}
