using System;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 19: shared layer of the luck effects (concept 04). Settings, multipliers by the day's luck, the log line
    /// and the remark gate. Tasks 20-22 (LuckShop, LuckWear, LuckBorder) call it; this file changes nothing in the game.
    /// Every value is the game's own when the effect is off, at L = 0 or at strength 0.
    /// </summary>
    public static class LuckFx
    {
        public static bool Enabled = true, Shop = true, Crates = true, Wear = true, Border = true;
        /// <summary>0..2: scales the difference from the game (0 = clean game, 1 = concept numbers).</summary>
        public static float Strength = 1f;

        /// <summary>The game's border search chance (BorderLogicC.ReadThroughPapers: 24 of 100 rolls).</summary>
        public const float BorderGameChance = 0.24f;
        public const float BorderMaxChance = 0.90f;
        /// <summary>Real seconds between two LuckFx remarks.</summary>
        public const float SayGap = 300f;

        // tables by level +2, +1, 0, -1, -2
        static readonly float[] shop = { 0.85f, 0.92f, 1f, 1.05f, 1.10f };
        static readonly float[] crateSell = { 1.10f, 1.05f, 1f, 0.97f, 0.95f };
        static readonly float[] wear = { 0.8f, 0.9f, 1f, 1.05f, 1.1f };
        static readonly float[] border = { 0.12f, 0.18f, 0.24f, 0.28f, 0.31f };

        /// <summary>Luck of the day as the effects see it (-2..+2, 0 while pending or with the fortune off).</summary>
        public static int L { get { return Luck.Level; } }

        /// <summary>fx = "Shop", "Crates", "Wear" or "Border": master toggle AND the effect toggle (and the fortune on).</summary>
        public static bool On(string fx)
        {
            if (!Enabled || !Luck.Enabled) return false;
            switch (fx)
            {
                case "Shop": return Shop;
                case "Crates": return Crates;
                case "Wear": return Wear;
                case "Border": return Border;
            }
            return false;
        }

        static float Mult(string fx, float[] table)
        {
            int l = L;
            if (!On(fx) || l == 0 || Strength <= 0f) return 1f;
            float b = table[2 - Mathf.Clamp(l, -2, 2)];
            return Mathf.Max(0.05f, 1f + (b - 1f) * Strength);
        }

        /// <summary>Shelf price multiplier: 0.85 / 0.92 / 1 / 1.05 / 1.10 for L +2..-2.</summary>
        public static float ShopMult() { return Mult("Shop", shop); }
        /// <summary>Crate sell price multiplier: 1.10 / 1.05 / 1 / 0.97 / 0.95.</summary>
        public static float CrateSellMult() { return Mult("Crates", crateSell); }
        /// <summary>Running wear multiplier: 0.8 / 0.9 / 1 / 1.05 / 1.1.</summary>
        public static float WearMult() { return Mult("Wear", wear); }

        /// <summary>Border search probability 0..1: 0.12 / 0.18 / 0.24 / 0.28 / 0.31 (game 0.24), strength scales the
        /// difference from 0.24, clamped to 0..0.90.</summary>
        public static float BorderSearchChance()
        {
            int l = L;
            if (!On("Border") || l == 0 || Strength <= 0f) return BorderGameChance;
            float b = border[2 - Mathf.Clamp(l, -2, 2)];
            return Mathf.Clamp(BorderGameChance + (b - BorderGameChance) * Strength, 0f, BorderMaxChance);
        }

        /// <summary>A random generator seeded by the game day and a key (same after reload), for per-visit rolls.</summary>
        public static System.Random Rng(string key)
        {
            int h = 0;
            if (key != null) unchecked { foreach (char c in key) h = h * 31 + c; }
            return new System.Random(Luck.Seed(Mathf.Max(0, Luck.Day), h));
        }

        /// <summary>[UncleSwap] LuckFx: &lt;fx&gt; L=&lt;l&gt; &lt;was&gt; -&gt; &lt;now&gt;</summary>
        public static void Log(string fx, string was, string now)
        {
            Debug.Log("[UncleSwap] LuckFx: " + fx + " L=" + L + " " + was + " -> " + now);
        }

        /// <summary>The shop remark is said once per game day (in the first shop).</summary>
        public static bool ShopRemarkDoneToday
        {
            get { return Luck.ShopRemarkDone; }
            set { Luck.ShopRemarkDone = value; }
        }

        static float saidAt = -1000f;

        /// <summary>Remark gate: a story_&lt;LANG&gt; line key (luckfx_*), at most one LuckFx remark per 5 real minutes,
        /// not while she dozes, talks, tells the fortune or wherever her remarks are refused. True = she says it.</summary>
        public static bool TrySay(string key)
        {
            if (!Luck.Mine) return false;   // task 30: another character has no luck lines
            float now = Time.realtimeSinceStartup;
            if (now - saidAt < SayGap) { Debug.Log("[UncleSwap] LuckFx: line " + key + " skipped (5 min gap)"); return false; }
            FukuDriver d = null;
            try { d = UnityEngine.Object.FindObjectOfType<FukuDriver>(); } catch (Exception) { }
            string why = d != null ? d.LuckFxSay(key) : "no driver";
            if (why != null) { Debug.Log("[UncleSwap] LuckFx: line " + key + " skipped (" + why + ")"); return false; }
            saidAt = now;
            Debug.Log("[UncleSwap] LuckFx: line " + key);
            return true;
        }
    }

    public partial class FukuDriver
    {
        /// <summary>LuckFx remark: null = said, else why not.</summary>
        internal string LuckFxSay(string key)
        {
            if (life == null || life.StorySay == null) return "no story lines";
            if (Doze.Gate) return "dozing";
            if (fort != Fort.None) return "fortune";
            if (life.CanSay != null && !life.CanSay()) return "busy";
            return life.StorySay(key) ? null : "no line " + key;
        }
    }
}
