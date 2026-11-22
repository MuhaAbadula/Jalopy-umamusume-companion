using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Game bug: DialogueStuffsC.StartTypeText starts the typewriter with StartCoroutine(TypeText()) but
    /// StopTypeText uses StopCoroutine("TypeText"), which cannot stop it. When a new line starts while the old
    /// one is still typing (picking up a part right after "go fetch one"), both append letters to the same label
    /// and the text comes out scrambled.
    /// Fix: when a new line starts over a running one, stop the old typewriter. If the old one belonged to a
    /// different speaker object, send that object "TextFinished" right away, as the old typewriter would have,
    /// so whatever script waits for it carries on.
    /// </summary>
    public static class TypeTextPatch
    {
        static readonly Dictionary<DialogueStuffsC, GameObject> running = new Dictionary<DialogueStuffsC, GameObject>();
        static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var harmony = new Harmony("osobl.jalopy.uncleswap.typetext");
            harmony.Patch(AccessTools.Method(typeof(DialogueStuffsC), "StartTypeText"),
                prefix: new HarmonyMethod(typeof(TypeTextPatch), nameof(StartPrefix)),
                postfix: new HarmonyMethod(typeof(TypeTextPatch), nameof(StartPostfix)));
            var iter = AccessTools.FirstInner(typeof(DialogueStuffsC), t => t.Name.Contains("TypeText"));
            if (iter != null)
                harmony.Patch(AccessTools.Method(iter, "MoveNext"),
                    postfix: new HarmonyMethod(typeof(TypeTextPatch), nameof(MoveNextPostfix)));
            applied = true;
        }

        static void StartPrefix(DialogueStuffsC __instance, GameObject target1)
        {
            GameObject oldTarget;
            if (__instance == null || !running.TryGetValue(__instance, out oldTarget)) return;
            __instance.StopAllCoroutines(); // only the typewriter runs as a coroutine here; icons use Invoke
            running.Remove(__instance);
            __instance.skipLevel = 0;
            if (oldTarget != null && oldTarget != target1)
                oldTarget.SendMessage("TextFinished", SendMessageOptions.DontRequireReceiver);
        }

        static void StartPostfix(DialogueStuffsC __instance, GameObject target1)
        {
            if (__instance != null) running[__instance] = target1;
        }

        static void MoveNextPostfix(object __instance, bool __result)
        {
            if (__result) return;
            var owner = Traverse.Create(__instance).Field("$this").GetValue<DialogueStuffsC>();
            if (owner != null) running.Remove(owner);
        }
    }
}
