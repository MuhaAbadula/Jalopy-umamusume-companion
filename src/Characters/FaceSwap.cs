using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Expressions by mesh swap (concept 14, Konata from a GMod model: bodygroups instead of face shape keys).
    /// The pack has submeshes "face:&lt;part&gt;_&lt;k&gt;/&lt;material&gt;" (blender/export_char.py, profile face_variants);
    /// one variant per part is drawn, the other submeshes get a material that discards every pixel
    /// (_Cutoff above 1, so SetBrightness, which only touches _Color, cannot bring them back).
    /// Default per part = variant 0 when the pack has it (eyes_0, mouth_0, eyebrows_0); parts without a 0
    /// (blush, sad, sweatdrops) are hidden until an expression asks for them.
    /// What to show comes from face.txt "Swap(part_k) 100" terms (FaceRig), blink and talk from its blink/talk lines.
    /// </summary>
    public class FaceSwap
    {
        readonly SkinnedMeshRenderer smr;
        readonly Material[] orig, cur;
        readonly Material hidden;
        readonly Dictionary<string, Dictionary<string, List<int>>> parts = new Dictionary<string, Dictionary<string, List<int>>>();
        readonly Dictionary<string, string> defaults = new Dictionary<string, string>();
        readonly Dictionary<string, string> shown = new Dictionary<string, string>();
        public int Variants { get; private set; }
        public int PartCount { get { return parts.Count; } }

        /// <summary>Every original material (shown or not), for SetBrightness.</summary>
        public IEnumerable<Material> Originals { get { return orig; } }

        public static FaceSwap Build(CharPack d, SkinnedMeshRenderer smr, Material[] mats)
        {
            bool any = false;
            foreach (var sm in d.SubMeshes) if (sm.Material != null && sm.Material.StartsWith("face:")) { any = true; break; }
            return any ? new FaceSwap(d, smr, mats) : null;
        }

        /// <summary>"eyes_3" -> "eyes".</summary>
        public static string PartOf(string variant)
        {
            int u = variant.LastIndexOf('_');
            return u > 0 ? variant.Substring(0, u) : variant;
        }

        FaceSwap(CharPack d, SkinnedMeshRenderer smr, Material[] mats)
        {
            this.smr = smr;
            orig = (Material[])mats.Clone();
            cur = (Material[])mats.Clone();
            hidden = new Material(Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse") ?? Shader.Find("Legacy Shaders/Diffuse")) { name = "face_hidden" };
            if (hidden.HasProperty("_Cutoff")) hidden.SetFloat("_Cutoff", 1.5f);
            if (hidden.HasProperty("_Color")) hidden.SetColor("_Color", new Color(0f, 0f, 0f, 0f));
            var vars = new HashSet<string>();
            for (int s = 0; s < d.SubMeshes.Count; s++)
            {
                string n = d.SubMeshes[s].Material;
                if (n == null || !n.StartsWith("face:")) continue;
                int slash = n.IndexOf('/');
                string v = slash > 5 ? n.Substring(5, slash - 5) : n.Substring(5);
                string p = PartOf(v);
                Dictionary<string, List<int>> pv;
                if (!parts.TryGetValue(p, out pv)) parts[p] = pv = new Dictionary<string, List<int>>();
                List<int> subs;
                if (!pv.TryGetValue(v, out subs)) pv[v] = subs = new List<int>();
                subs.Add(s);
                vars.Add(v);
            }
            Variants = vars.Count;
            foreach (var kv in parts)
            {
                string def = kv.Key + "_0";
                defaults[kv.Key] = kv.Value.ContainsKey(def) ? def : null;
                shown[kv.Key] = "?";   // forces the first Apply to set every part
            }
            Apply(null);
        }

        public bool Has(string variant) { Dictionary<string, List<int>> pv; return variant != null && parts.TryGetValue(PartOf(variant), out pv) && pv.ContainsKey(variant); }

        /// <summary>Shows want[part] for each part it names (unknown variants fall back to the default), the default elsewhere.</summary>
        public void Apply(Dictionary<string, string> want)
        {
            bool dirty = false;
            foreach (var kv in parts)
            {
                string v = null;
                if (want == null || !want.TryGetValue(kv.Key, out v) || v == null || !kv.Value.ContainsKey(v)) v = defaults[kv.Key];
                if (shown[kv.Key] == v) continue;
                shown[kv.Key] = v;
                foreach (var pv in kv.Value)
                    foreach (int s in pv.Value) cur[s] = pv.Key == v ? orig[s] : hidden;
                dirty = true;
            }
            if (dirty && smr != null) smr.sharedMaterials = cur;
        }

        /// <summary>Debug: "eyes_0 mouth_6 eyebrows_0 blush -".</summary>
        public string State
        {
            get
            {
                var l = new List<string>();
                foreach (var kv in shown) l.Add(kv.Value ?? (kv.Key + " -"));
                return string.Join(" ", l.ToArray());
            }
        }
    }
}
