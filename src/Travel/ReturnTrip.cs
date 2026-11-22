using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// The way home after she stayed in Istanbul (IstanbulPatch). Plays her talks from story_&lt;LANG&gt;.txt through the
    /// game's own dialogue (DialogueCheck + speechStack + DialogueSpeech: bubble, typewriter, click to go on; game lines
    /// queue behind it). Mode "istanbul": "istanbul_morning" once both sit in the car in Istanbul, then per leg N the keys
    /// back_N, back_N_2, back_N_3.. (as long as the key exists). Mode "turn" (task 60, heading home without Istanbul,
    /// level 1..5): "turn_start" once, then home_N, home_N_2.. per leg. The first talk of a leg after 45 s of driving, every
    /// next one after GapMinutes of driving. State lives in ModSaves\FukuUncle\return_trip.txt (the game save has fixed
    /// fields only) and is dropped at home (routeLevel 0) or if she is gone for good.
    /// </summary>
    public class ReturnTrip : MonoBehaviour
    {
        const string Item = "FukuStory";   // our name in the game's dialogue queue
        public static ReturnTrip Current;

        public Remarks Story;
        public Func<bool> RemarkBusy;

        UncleLogicC u;
        RouteGeneratorC route;
        RigidbodyControllerC playerCtl;
        CarControleScriptC carCtl;
        Animator anim;
        bool active;
        string mode = "istanbul";   // "istanbul" | "turn"
        public static float GapMinutes = 4f;   // setting ReturnTalkGap
        int lastLevel = -1, talksThisLeg;
        readonly HashSet<int> missingLogged = new HashSet<int>();
        readonly HashSet<string> said = new HashSet<string>();
        bool playing;
        float bothSatFor, drivenFor, nextCheck;

        static string StatePath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "ModSaves"), "FukuUncle"), "return_trip.txt"); }
        }

        /// <summary>On the way home with her (after Istanbul).</summary>
        public static bool Active { get { return Current != null && Current.active; } }

        /// <summary>A return-trip talk is on screen now (task 34: no pranks then).</summary>
        public static bool Playing { get { return Current != null && Current.playing; } }

        /// <summary>It is the Istanbul morning on the way home and her morning talk is still to come (Morning stays silent).</summary>
        public static bool MorningPending
        {
            get
            {
                var t = Current;
                return t != null && t.active && t.route != null && t.route.routeLevel == 6 && !t.said.Contains("istanbul_morning");
            }
        }

        void Awake()
        {
            Current = this;
            u = GetComponent<UncleLogicC>();
            anim = GetComponent<Animator>();
            try
            {
                if (File.Exists(StatePath))
                    foreach (var raw in File.ReadAllLines(StatePath))
                    {
                        string l = raw.Trim();
                        if (l == "active=1") active = true;
                        else if (l == "mode=turn") mode = "turn";
                        else if (l.StartsWith("said=")) said.Add(l.Substring(5));
                    }
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] return trip: can't read state: " + e.Message); }
            if (active) Debug.Log("[UncleSwap] return trip: on the way home, mode " + mode + " (said: " + string.Join(",", new List<string>(said).ToArray()) + ")");
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                if (!active) { if (File.Exists(StatePath)) File.Delete(StatePath); return; }
                var lines = new List<string> { "active=1", "mode=" + mode };
                foreach (var s in said) lines.Add("said=" + s);
                File.WriteAllLines(StatePath, lines.ToArray());
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] return trip: can't save state: " + e.Message); }
        }

        /// <summary>She stayed in Istanbul just now.</summary>
        public void Begin() { Begin("istanbul"); }

        void Begin(string m)
        {
            active = true;
            mode = m;
            said.Clear();
            talksThisLeg = 0;
            missingLogged.Clear();
            Save();
        }

        void Update()
        {
            if (u == null) return;
            // a talk of ours cut off by the game (crash line etc.) lands in its queue; the game can't start it by
            // name, and it would block the queue: take it out, the talk plays again later
            if (u.textQueue != null && u.textQueue.Remove(Item)) Debug.Log("[UncleSwap] return trip: talk interrupted, later again");
            if (playing || Time.time < nextCheck) return;
            nextCheck = Time.time + 0.25f;

            if (route == null && u.director != null) route = u.director.GetComponent<RouteGeneratorC>();
            if (route == null) return;
            int level = route.routeLevel;
            if (!active && level == 6 && !u.uncleGoneForever && IstanbulPatch.Stay) Begin(); // older save already in Istanbul
            if (!active && TurnAroundReady(level)) { Begin("turn"); Debug.Log("[UncleSwap] return trip: turn-around start (level " + level + ")"); }
            if (!active) return;
            if (level == 0 || u.uncleGoneForever) { active = false; said.Clear(); Save(); Debug.Log("[UncleSwap] return trip: over"); return; }
            if (level != lastLevel) { lastLevel = level; drivenFor = 0f; talksThisLeg = 0; }   // a new leg: its first talk after 45 s again

            bool both = BothSeated();
            bothSatFor = both ? bothSatFor + 0.25f : 0f;
            float speed = carCtl != null ? Mathf.Abs(carCtl.currentSpeed) : 0f;
            if (both && speed > 25f) drivenFor += 0.25f;
            if (!both) return;

            string key = NextKey(level);
            if (key == null) return;
            bool morning = key == "istanbul_morning";
            float need = morning ? 4f : (talksThisLeg == 0 ? 45f : Mathf.Max(1f, GapMinutes) * 60f);
            if ((morning ? bothSatFor : drivenFor) < need) return;
            if (!Free()) return;
            StartTalk(key);
        }

        /// <summary>Heading home without Istanbul, she is in the car (task 60).</summary>
        bool TurnAroundReady(int level)
        {
            return route.drivingTowardsHome && level >= 1 && level <= 5 && !u.uncleGoneForever
                && anim != null && anim.GetBool("enterCar") && u.isSat;
        }

        bool BothSeated()
        {
            if (playerCtl == null && u.player != null) playerCtl = u.player.GetComponent<RigidbodyControllerC>();
            if (carCtl == null) carCtl = FindObjectOfType<CarControleScriptC>();
            bool asleep = u.sleepParticle != null && u.sleepParticle.GetComponent<ParticleSystem>().isPlaying;
            return anim != null && anim.GetBool("enterCar") && u.isSat && !asleep && playerCtl != null && playerCtl.isSat;
        }

        /// <summary>The next talk of this leg that has not been said and has lines, or null (logs once per leg).</summary>
        string NextKey(int level)
        {
            if (mode == "istanbul" && level == 6 && !said.Contains("istanbul_morning"))
                return Story != null && Story.All("istanbul_morning") != null ? "istanbul_morning" : MissingKey("istanbul_morning", level);
            if (!route.drivingTowardsHome || level < 1 || level > 6) return null;
            string head;
            if (mode == "turn")
            {
                if (level > 5) return null;
                if (!said.Contains("turn_start"))
                {
                    if (Story != null && Story.All("turn_start") != null) return "turn_start";
                    said.Add("turn_start");   // no line: skip it for good, the leg talks go on
                    Debug.Log("[UncleSwap] return trip: no lines for turn_start");
                }
                head = "home_" + level;
            }
            else head = "back_" + level;
            for (int i = 1; i < 50; i++)
            {
                string key = i == 1 ? head : head + "_" + i;
                if (said.Contains(key)) continue;
                if (Story != null && Story.All(key) != null) return key;
                return MissingKey(key, level);   // the first missing key ends this leg's talks
            }
            return null;
        }

        string MissingKey(string key, int level)
        {
            if (missingLogged.Add(level)) Debug.Log("[UncleSwap] return trip: no lines for " + key + " (level " + level + ", nothing more this leg)");
            return null;
        }

        void StartTalk(string key)
        {
            var lines = Story != null ? Story.All(key) : null;
            if (lines == null) return;
            StartCoroutine(Play(key, lines));
        }

        /// <summary>Debug panel: the next due talk at once (timers ignored).</summary>
        public static string DebugNow()
        {
            var t = Current;
            if (t == null || t.u == null) return "no return trip component";
            if (t.playing) return "a talk is playing";
            if (t.route == null && t.u.director != null) t.route = t.u.director.GetComponent<RouteGeneratorC>();
            if (t.route == null) return "no route";
            int level = t.route.routeLevel;
            if (!t.BothSeated()) return "both must sit in the car";
            if (!t.active)
            {
                if (!t.TurnAroundReady(level)) return "not heading home at level 1..5 with her aboard (level " + level + ")";
                t.Begin("turn");
                Debug.Log("[UncleSwap] return trip: turn-around start (level " + level + ", debug)");
            }
            string key = t.NextKey(level);
            if (key == null) return "no talk due: no more keys on level " + level + " (mode " + t.mode + ")";
            if (!t.Free()) return "she is busy talking or a bubble is on screen";
            t.StartTalk(key);
            return "talk " + key;
        }

        bool Free()
        {
            if (u.isTalking || u.restrictTalk || u.uncleAtHome || !string.IsNullOrEmpty(u.dialogueConvoItem)) return false;
            if (RemarkBusy != null && RemarkBusy()) return false;
            var label = u.dialogueHolder != null ? u.dialogueHolder.GetComponent<UILabel>() : null;
            return label != null && string.IsNullOrEmpty(label.text);
        }

        IEnumerator Play(string key, List<string> lines)
        {
            playing = true;
            u.dialogueChecker = Item;
            u.StartCoroutine("DialogueCheck");
            yield return new WaitForSeconds(0.1f);
            if (!u.canSpeak || u.dialogueConvoItem != Item)
            {
                u.textQueue.Remove(Item);
                playing = false;
                yield break;
            }
            Debug.Log("[UncleSwap] return trip: talk " + key + " (" + lines.Count + " lines)");
            u.speechStack.Clear();
            u.speechStack.AddRange(lines);
            u.StartCoroutine("DialogueSpeech");
            yield return null;
            while (!u.dialogueSpeechComplete && u.dialogueConvoItem == Item) yield return null;
            if (u.dialogueSpeechComplete)
            {
                said.Add(key);
                Save();
                drivenFor = 0f;
                talksThisLeg++;
            }
            playing = false;
            nextCheck = Time.time + 3f;
        }
    }
}
