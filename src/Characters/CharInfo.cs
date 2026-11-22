using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UmamusumeCompanion
{
    /// <summary>
    /// models\&lt;name&gt;\info.txt: plain "key=value" lines (UTF-8), '#' starts a comment.
    ///   name=Фукукитару        shown in dialogue when the game text is Cyrillic
    ///   name_en=Fukukitaru     shown otherwise
    ///   voice_pitch=1.45       used when there is no voice\ folder: the uncle's mumbles, pitched up
    ///   features=fortune       comma list of character-only features (absent key = fortune; empty or "none" = nothing)
    ///   height_m=1.56          her height in metres (her scale in RoadSpots.Scale)
    ///   body_scale=1           uniform size of her whole rig against the export (task 31; the export puts every Uma's
    ///                          hips at the uncle's height). 0.5..1.5, absent = 1
    /// A voice\ folder with .wav/.ogg files replaces the uncle's mumbles entirely.
    /// </summary>
    public class CharInfo
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static CharInfo Load(string dir)
        {
            var info = new CharInfo();
            string path = Path.Combine(dir, "info.txt");
            if (!File.Exists(path)) return info;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                info.values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return info;
        }

        /// <summary>The value as written: null when the key is absent, "" when it is empty.</summary>
        public string Raw(string key)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : null;
        }

        public string Get(string key, string fallback = null)
        {
            string v;
            return values.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
        }

        public float GetFloat(string key, float fallback)
        {
            float f;
            string v = Get(key);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : fallback;
        }
    }
}
