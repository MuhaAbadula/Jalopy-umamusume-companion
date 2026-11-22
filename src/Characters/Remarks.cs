using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Short remarks she makes on her own (models/&lt;name&gt;/remarks_&lt;LANG&gt;.txt, "key = text", several lines per key).
    /// Shown with the game's own bubble, typewriter and her voice, but without waiting for a click: the bubble closes
    /// itself after a moment. Never over the game's dialogue: skipped if anything is on screen, and if the game starts
    /// talking meanwhile, its text is left alone.
    /// </summary>
    public class Remarks
    {
        readonly Dictionary<string, Dictionary<string, List<string>>> byLang = new Dictionary<string, Dictionary<string, List<string>>>();
        readonly Dictionary<string, string> last = new Dictionary<string, string>();
        readonly Dictionary<string, List<string>> bags = new Dictionary<string, List<string>>();
        public bool Busy { get; private set; }
        public int Count { get; private set; }

        public Remarks(string dir, string prefix = "remarks_")
        {
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.GetFiles(dir, prefix + "*.txt"))
            {
                string lang = Path.GetFileNameWithoutExtension(path).Substring(prefix.Length);
                var map = new Dictionary<string, List<string>>();
                foreach (var raw in File.ReadAllLines(path, System.Text.Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), text = line.Substring(eq + 1).Trim();
                    if (text.Length == 0) continue;
                    List<string> l;
                    if (!map.TryGetValue(key, out l)) map[key] = l = new List<string>();
                    l.Add(text);
                    Count++;
                }
                byLang[lang] = map;
            }
        }

        /// <summary>Every line of a key in file order (a talk), or null.</summary>
        public List<string> All(string key)
        {
            Dictionary<string, List<string>> map;
            if (!byLang.TryGetValue(Language.CurrentLanguage().ToString(), out map) && !byLang.TryGetValue("EN", out map)) return null;
            List<string> l;
            return map.TryGetValue(key, out l) && l.Count > 0 ? new List<string>(l) : null;
        }

        public string Pick(string key)
        {
            Dictionary<string, List<string>> map;
            if (!byLang.TryGetValue(Language.CurrentLanguage().ToString(), out map) && !byLang.TryGetValue("EN", out map)) return null;
            List<string> l;
            if (!map.TryGetValue(key, out l) || l.Count == 0) return null;
            // shuffle bag: every line of a key once before any repeats, never the same line twice in a row
            List<string> bag;
            if (!bags.TryGetValue(key, out bag) || bag.Count == 0)
            {
                bag = new List<string>(l);
                for (int i = bag.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); var t = bag[i]; bag[i] = bag[j]; bag[j] = t; }
                string prev;
                if (last.TryGetValue(key, out prev) && bag.Count > 1 && bag[bag.Count - 1] == prev) { bag[bag.Count - 1] = bag[0]; bag[0] = prev; }
                bags[key] = bag;
            }
            string s = bag[bag.Count - 1];
            bag.RemoveAt(bag.Count - 1);
            last[key] = s;
            return s;
        }

        static void Skip(string why) { Debug.Log("[UncleSwap] remark skipped: " + why); }

        /// <summary>Would a remark show right now (same checks as Say).</summary>
        public bool CanSay(UncleLogicC u)
        {
            if (Doze.Muted || Busy || u == null || u.isTalking || u.restrictTalk || u.uncleAtHome || u.dialogueHolder == null) return false;
            var label = u.dialogueHolder.GetComponent<UILabel>();
            return label != null && string.IsNullOrEmpty(label.text);
        }

        public IEnumerator Say(UncleLogicC u, string text)
        {
            if (Busy || u == null || string.IsNullOrEmpty(text)) { Skip("busy or no text"); yield break; }
            if (Doze.Muted) { Skip("dozing"); yield break; }
            if (u.isTalking || u.restrictTalk || u.uncleAtHome || u.director == null || u.dialogueHolder == null)
            {
                Skip("game state: talking=" + u.isTalking + " restrictTalk=" + u.restrictTalk + " atHome=" + u.uncleAtHome);
                yield break;
            }
            var label = u.dialogueHolder.GetComponent<UILabel>();
            var ds = u.dialogueHolder.GetComponent<DialogueStuffsC>();
            var dir = u.director.GetComponent<DirectorC>();
            var an = u.GetComponent<Animator>();
            if (label == null || ds == null || dir == null || !string.IsNullOrEmpty(label.text))
            {
                Skip("bubble in use: \"" + (label != null ? label.text : "?") + "\"");
                yield break;
            }

            Busy = true;
            dir.BubbleOn(u.mouth, u.characterName);
            ds.word = text;
            u.textFinished = false;
            ds.StartTypeText(u.gameObject);
            u.AudioSpeech();
            if (an != null) an.SetBool("talking", true);
            float t = 0f;
            var drag = Camera.main != null ? Camera.main.GetComponent<DragRigidbodyC>() : null;
            while (!u.textFinished && t < 8f && !u.isTalking)
            {
                if (drag != null) drag.inputRestrict = false;   // the typewriter locks picking things up; ours must not
                t += Time.deltaTime;
                yield return null;
            }
            if (u.isTalking) { Busy = false; yield break; } // the game took the bubble over
            if (an != null) an.SetBool("talking", false);
            u.StartCoroutine("StopInputRestrict");             // the typewriter blocks picking things up while it types
            float hold = 1.8f + text.Length * 0.03f;
            for (t = 0f; t < hold && !u.isTalking; t += Time.deltaTime) yield return null;
            if (!u.isTalking) // the game's own lines set isTalking; ours never do
            {
                label.text = string.Empty;
                if (ds._nameHolder != null)
                {
                    var nameLabel = ds._nameHolder.GetComponent<UILabel>();
                    if (nameLabel != null) nameLabel.text = string.Empty;
                }
                dir.BubbleOff();
            }
            Busy = false;
        }
    }
}
