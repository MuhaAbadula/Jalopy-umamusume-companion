using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 22 (concept 04, E5): the border search chance follows the day's luck. In BorderLogicC.ReadThroughPapers the
    /// game rolls Random.Range(0,100) + 5*guestAnnoyance: 0..75 pass, 76..95 boot search, 96..100 crate search (24 %).
    /// A transpiler swaps the iterator's Random.Range(int,int) calls for Roll; Roll changes only the roll of the
    /// penaltyFare == 0 branch: with chance 1-p it returns 0..75, else 76..99 (boot:crate stays 20:4). With fines the game
    /// rolls as before. After the border she says a line: pass with L>0, search with L<0 (LuckFx 5 min gate).
    /// </summary>
    public static class LuckBorder
    {
        static bool applied;
        static BorderLogicC cur;   // set while the ReadThroughPapers iterator runs one step
        static MethodInfo range = AccessTools.Method(typeof(UnityEngine.Random), "Range", new[] { typeof(int), typeof(int) });

        public static void Apply()
        {
            if (applied) return;
            applied = true;
            var iter = AccessTools.FirstInner(typeof(BorderLogicC), t => t.Name.Contains("ReadThroughPapers"));
            var move = iter != null ? AccessTools.Method(iter, "MoveNext") : null;
            if (move == null || range == null) { Debug.LogWarning("[UncleSwap] LuckFx: Border transpiler not found (no iterator)"); return; }
            var h = new Harmony("osobl.jalopy.uncleswap.luckborder");
            h.Patch(move,
                prefix: new HarmonyMethod(typeof(LuckBorder), nameof(Before)),
                postfix: new HarmonyMethod(typeof(LuckBorder), nameof(After)),
                transpiler: new HarmonyMethod(typeof(LuckBorder), nameof(Swap)));
            Debug.Log(swapped > 0 ? "[UncleSwap] LuckFx: Border transpiler ok (" + swapped + " calls)"
                                  : "[UncleSwap] LuckFx: Border transpiler not found");
        }

        static int swapped;

        static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> code)
        {
            var roll = AccessTools.Method(typeof(LuckBorder), nameof(Roll));
            swapped = 0;
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand as MethodInfo == range)
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = roll;
                    swapped++;
                }
                yield return ci;
            }
        }

        static void Before(object __instance)
        {
            try { cur = Traverse.Create(__instance).Field("$this").GetValue<BorderLogicC>(); }
            catch (Exception) { cur = null; }
        }

        static void After()
        {
            cur = null;
        }

        static int Penalty(BorderLogicC b)
        {
            var cl = b != null && b.carLogic != null ? b.carLogic.GetComponent<CarLogicC>() : null;
            if (cl == null) { var go = GameObject.FindWithTag("CarLogic"); cl = go != null ? go.GetComponent<CarLogicC>() : null; }
            return cl != null ? cl.penaltyFare : -1;
        }

        static int Annoyance(BorderLogicC b)
        {
            var d = b != null && b.director != null ? b.director.GetComponent<DirectorC>() : null;
            if (d == null) { var go = GameObject.FindWithTag("Director"); d = go != null ? go.GetComponent<DirectorC>() : null; }
            return d != null ? d.guestAnnoyance : 0;
        }

        /// <summary>One roll in 0..99: with chance p a search value (76..99), else 0..75.</summary>
        static int Draw(float p)
        {
            return UnityEngine.Random.value < p ? UnityEngine.Random.Range(76, 100) : UnityEngine.Random.Range(0, 76);
        }

        static string Pct(float p) { return Mathf.RoundToInt(p * 100f) + "%"; }

        /// <summary>Replaces Random.Range(int,int) inside ReadThroughPapers.MoveNext.</summary>
        public static int Roll(int min, int max)
        {
            var b = cur;
            if (b == null || min != 0 || max != 100 || !LuckFx.On("Border") || Penalty(b) != 0)
                return UnityEngine.Random.Range(min, max);
            float p = LuckFx.BorderSearchChance();
            int raw = Draw(p);
            int annoy = Annoyance(b);
            int fin = annoy > 0 ? Mathf.Min(100, raw + annoy * 5) : raw;
            bool search = fin > 75;
            LuckFx.Log("Border", "p=" + Pct(LuckFx.BorderGameChance),
                "p=" + Pct(p) + " -> " + (search ? "search" : "pass") + " (roll " + raw + (annoy > 0 ? ", annoyance +" + annoy * 5 : "") + ")");
            int l = LuckFx.L;
            string key = !search && l > 0 ? "luckfx_border_pass" : search && l < 0 ? "luckfx_border_search" : null;
            if (key != null) Runner.Get().Later(key);
            return raw;
        }

        /// <summary>Debug panel: p for the current L and one simulated roll, no game state change.</summary>
        public static string DebugRoll()
        {
            int l = LuckFx.L;
            bool on = LuckFx.On("Border");
            float p = LuckFx.BorderSearchChance();
            int raw = Draw(p);
            int annoy = Annoyance(null);
            int fin = annoy > 0 ? Mathf.Min(100, raw + annoy * 5) : raw;
            string what = fin <= 75 ? "pass" : fin <= 95 ? "boot search" : "crate search";
            return "L=" + (l > 0 ? "+" : "") + l + " p=" + Pct(p) + " (game 24%" + (on ? "" : ", effect off") + ")"
                + (applied && swapped > 0 ? "" : ", PATCH NOT ACTIVE")
                + "; roll " + raw + (annoy > 0 ? " +" + annoy * 5 + " annoyance" : "") + " -> " + what;
        }

        /// <summary>Waits until the border lets her talk again (restrictTalk off: gate opened or fine paid), then says the line.</summary>
        class Runner : MonoBehaviour
        {
            static Runner inst;
            public static Runner Get()
            {
                if (inst == null)
                {
                    var go = new GameObject("FukuLuckBorder");
                    DontDestroyOnLoad(go);
                    inst = go.AddComponent<Runner>();
                }
                return inst;
            }

            public void Later(string key)
            {
                StopAllCoroutines();
                StartCoroutine(SayAfterBorder(key));
            }

            IEnumerator SayAfterBorder(string key)
            {
                float until = Time.time + 240f;
                yield return new WaitForSeconds(1f);
                while (Time.time < until)
                {
                    var u = UnityEngine.Object.FindObjectOfType<UncleLogicC>();
                    if (u != null && !u.restrictTalk) break;
                    yield return new WaitForSeconds(0.5f);
                }
                if (Time.time >= until) { Debug.Log("[UncleSwap] LuckFx: line " + key + " skipped (border still holds her talk)"); yield break; }
                yield return new WaitForSeconds(2.5f);
                for (int i = 0; i < 10; i++)   // the guard's last bubble may still be up: retry for ~10 s
                {
                    var u = UnityEngine.Object.FindObjectOfType<UncleLogicC>();
                    var rem = u != null ? u.GetComponent<FukuDriver>() : null;
                    if (u != null && !u.isTalking && rem != null && rem.LuckFxReady()) break;
                    yield return new WaitForSeconds(1f);
                }
                LuckFx.TrySay(key);
            }
        }
    }

    public partial class FukuDriver
    {
        /// <summary>Her remark bubble is free (used to wait out the guard's last line before a LuckFx remark).</summary>
        internal bool LuckFxReady()
        {
            return life != null && (life.CanSay == null || life.CanSay());
        }
    }
}
