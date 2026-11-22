using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;

namespace UmamusumeCompanion
{
    /// <summary>
    /// The character's own dialogue: models\&lt;name&gt;\speech_&lt;LANG&gt;.txt ("key=text" lines, UTF-8, '#' comments)
    /// replaces the game's text for those keys of the "Speech" sheet in that language (LANG = RU, EN, ...).
    /// Keys without an override, and languages without a file, keep the game's text.
    /// </summary>
    public static class SpeechPatch
    {
        const string Sheet = "Speech";
        static readonly Dictionary<string, Dictionary<string, string>> byLang =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        static bool applied;

        public static int Load(string dir)
        {
            byLang.Clear();
            int total = 0;
            foreach (var path in Directory.GetFiles(dir, "speech_*.txt"))
            {
                string lang = Path.GetFileNameWithoutExtension(path).Substring("speech_".Length);
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.TrimEnd('\r');
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                }
                byLang[lang] = map;
                total += map.Count;
            }
            return total;
        }

        public static void Apply()
        {
            if (applied) return;
            var harmony = new Harmony("osobl.jalopy.umamusumecompanion.speech");
            harmony.Patch(AccessTools.Method(typeof(Language), "Get", new[] { typeof(string), typeof(string) }),
                postfix: new HarmonyMethod(typeof(SpeechPatch), nameof(Postfix)));
            applied = true;
        }

        static void Postfix(string key, string sheetTitle, ref string __result)
        {
            if (sheetTitle != Sheet || key == null || byLang.Count == 0) return;
            Dictionary<string, string> map;
            string text;
            if (byLang.TryGetValue(Language.CurrentLanguage().ToString(), out map) && map.TryGetValue(key, out text))
                __result = text;
        }
    }
}
