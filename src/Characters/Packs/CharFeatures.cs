using System;
using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 30: what the picked character has of her own, from info.txt `features=` (comma list).
    /// Absent key = "fortune" (packs made before v0.38); empty or "none" = nothing.
    /// Known: fortune (Luck, LuckFx, the prayer pose), pranks (task 34, Pranks.cs).
    /// Until a pack is read the default list applies, so nothing changes for old packs.
    /// </summary>
    public static class CharFeatures
    {
        public const string Default = "fortune";
        static readonly HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Default };
        static readonly List<string> order = new List<string> { Default };

        public static bool Has(string name) { return set.Contains(name); }

        /// <summary>"fortune", "fortune,pranks" or "none".</summary>
        public static string List { get { return order.Count > 0 ? string.Join(",", order.ToArray()) : "none"; } }

        public static void Load(CharInfo info)
        {
            set.Clear();
            order.Clear();
            string raw = info != null ? info.Raw("features") : null;
            if (raw == null) raw = Default;
            foreach (var part in raw.Split(','))
            {
                string f = part.Trim().ToLowerInvariant();
                if (f.Length == 0 || f == "none" || set.Contains(f)) continue;
                set.Add(f);
                order.Add(f);
            }
        }
    }
}
