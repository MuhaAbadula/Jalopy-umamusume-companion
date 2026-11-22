using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// UncleLogicC.AudioSpeech picks Random.Range(0, speech.Length - 1) per line: repeats are common and the last
    /// clip never plays. With a character voice loaded, play from a shuffle bag instead: every clip once per round,
    /// and never the same clip twice in a row (also across rounds).
    /// </summary>
    public static class VoicePatch
    {
        static AudioClip[] clips;
        static int[] bag;
        static int pos, last = -1;
        static bool applied;

        /// <summary>A line sleeping: only these clips (the lowest-sounding ones), at low volume.</summary>
        public static bool Quiet;
        static readonly string[] QuietNames = { "mumble_05", "mumble_12", "mumble_34" };
        const float QuietVolume = 0.35f;

        public static void SetClips(AudioClip[] c)
        {
            clips = c != null && c.Length > 0 ? c : null;
            bag = null;
            pos = 0;
            last = -1;
        }

        public static void Apply()
        {
            if (applied) return;
            var harmony = new Harmony("osobl.jalopy.uncleswap.voice");
            harmony.Patch(AccessTools.Method(typeof(UncleLogicC), "AudioSpeech"),
                prefix: new HarmonyMethod(typeof(VoicePatch), nameof(Prefix)));
            applied = true;
        }

        static bool Prefix(UncleLogicC __instance)
        {
            if (clips == null || __instance == null || __instance.sleepParticle == null) return true;
            var src = __instance.sleepParticle.GetComponent<AudioSource>();
            if (src == null) return true;
            if (Quiet)
            {
                var pool = new System.Collections.Generic.List<AudioClip>();
                foreach (var c in clips) if (System.Array.IndexOf(QuietNames, c.name) >= 0) pool.Add(c);
                if (pool.Count > 0)
                {
                    src.PlayOneShot(pool[Random.Range(0, pool.Count)], QuietVolume);
                    return false;
                }
                src.PlayOneShot(clips[Next()], QuietVolume);
                return false;
            }
            src.PlayOneShot(clips[Next()], 1f);
            return false;
        }

        static int Next()
        {
            int n = clips.Length;
            if (n == 1) return 0;
            if (bag == null || pos >= n)
            {
                if (bag == null || bag.Length != n)
                {
                    bag = new int[n];
                    for (int i = 0; i < n; i++) bag[i] = i;
                }
                for (int i = n - 1; i > 0; i--)
                {
                    int j = Random.Range(0, i + 1);
                    int t = bag[i]; bag[i] = bag[j]; bag[j] = t;
                }
                if (bag[0] == last)
                {
                    int j = Random.Range(1, n);
                    int t = bag[0]; bag[0] = bag[j]; bag[j] = t;
                }
                pos = 0;
            }
            last = bag[pos++];
            return last;
        }
    }
}
