using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 44 (concept 08): Uma FM, a third position of the Laika radio's power click: off -> game radio -> Uma FM -> off.
    /// Tracks = Mods\Assets\FukuUncle\radio\umafm\*.ogg (none = no third position). They play on the game's own radio
    /// AudioSource (3D, volume knob, ramps, pause menu pitch stay the game's): prefix on NextSong plays the next Uma track
    /// instead of the game's list, prefix on Trigger adds the third click, postfix on Update drives everything else.
    /// One track in memory at a time (WWW + GetAudioClipCompressed, seekable). Radio power off on Uma FM pauses the track,
    /// back on Uma FM resumes it where it stopped. Hiss muted on Uma FM. State in ModSaves\FukuUncle\umafm.txt.
    /// </summary>
    public static class UmaFm
    {
        public static bool Enabled = true;
        public static string Dir;

        static bool applied, stateRead;
        static bool uma;                       // the station: false = game radio, true = Uma FM
        static RadioFreqLogicC radio;
        static AudioSource src, hiss, clickSrc;
        static CarLogicC car;
        static CarPerformanceC perf;

        static readonly List<string> files = new List<string>();   // file names in Dir
        static readonly List<string> bag = new List<string>();     // not played yet in this round
        static string lastPlayed;
        static int lastCount = -1;
        static float scanAt = -100f;

        static AudioClip clip;                 // the Uma track in memory
        static string clipFile;                // its file name
        static float clipTime;                 // its position (kept up to date while it is on the source)
        static int loadId;
        static bool pending;                   // a track is loading or waiting for its gap: NextSong does nothing
        static bool paused;                    // Uma track paused by the power knob
        static float gapUntil, saveAt, restartAt;
        static AudioClip gameClip;             // the game's song we took the source from
        static float gameTime;
        static bool memLogged;

        /// <summary>Uma FM is the station and the radio is on (head bob, her tricks).</summary>
        public static bool Active { get { return uma && radio != null && car != null && car.radioOn; } }

        static bool Available { get { return Enabled && files.Count > 0 && !string.IsNullOrEmpty(Dir); } }

        /// <summary>Task 52: Uma FM is the station (radio on or off).</summary>
        public static bool IsUma { get { return uma; } }

        /// <summary>Task 52: the Uma track on this source has just played to its end (the point where NextSong starts the next one).</summary>
        public static bool TrackEnded(AudioSource s)
        {
            return uma && !pending && !paused && clip != null && s != null && s == src && src.clip == clip && !src.isPlaying
                && src.pitch > 0.001f && clipTime >= clip.length - 2f;
        }

        static void Log(string s) { Debug.Log("[UncleSwap] UmaFM: " + s); }

        public static void Apply()
        {
            if (applied) return;
            applied = true;
            var h = new Harmony("osobl.jalopy.uncleswap.umafm");
            var t = typeof(RadioFreqLogicC);
            h.Patch(AccessTools.Method(t, "Trigger"), prefix: new HarmonyMethod(typeof(UmaFm), nameof(TriggerPrefix)));
            h.Patch(AccessTools.Method(t, "NextSong"), prefix: new HarmonyMethod(typeof(UmaFm), nameof(NextPrefix)));
            h.Patch(AccessTools.Method(t, "TurnRadioOn"), postfix: new HarmonyMethod(typeof(UmaFm), nameof(OnPostfix)));
            h.Patch(AccessTools.Method(t, "Update"), postfix: new HarmonyMethod(typeof(UmaFm), nameof(Tick)));
            Log("patches on, folder " + Dir);
        }

        // ------------------------------------------------------------ attach to the radio of this scene
        static void Attach(RadioFreqLogicC r)
        {
            radio = r;
            src = r.GetComponent<AudioSource>();
            hiss = r.radioStatic != null ? r.radioStatic.GetComponent<AudioSource>() : null;
            clickSrc = r.radioVolume != null ? r.radioVolume.GetComponent<AudioSource>() : null;
            car = r.carLogic != null ? r.carLogic.GetComponent<CarLogicC>() : null;
            perf = r.carLogic != null ? r.carLogic.GetComponent<CarPerformanceC>() : null;
            loadId++;                          // a load started on the old radio died with it
            pending = false;
            paused = false;
            gameClip = null;
            if (!stateRead) { stateRead = true; ReadState(); }
            Scan(true);
            if (uma && !Available) uma = false;
            SetHiss();
            if (!uma) return;
            Take();
            Log("attached, station Uma FM" + (car != null && car.radioOn ? ", radio on" : ", radio off"));
            if (car != null && car.radioOn) NeedleMid(r, 0.01f);
        }

        // ------------------------------------------------------------ tracks
        static void Scan(bool force)
        {
            if (!force && Time.realtimeSinceStartup < scanAt) return;
            scanAt = Time.realtimeSinceStartup + 5f;
            files.Clear();
            try
            {
                if (!string.IsNullOrEmpty(Dir) && Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, "*.ogg")) files.Add(Path.GetFileName(f));
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] UmaFM: can't list " + Dir + ": " + e.Message); }
            files.Sort(StringComparer.OrdinalIgnoreCase);
            bag.RemoveAll(f => !files.Contains(f));
            if (files.Count != lastCount)
            {
                lastCount = files.Count;
                if (files.Count == 0) Log("no tracks (" + Dir + "), the radio works as in the game");
                else Log(files.Count + " tracks in " + Dir);
            }
        }

        /// <summary>Next file of the shuffle bag (no repeats until all played), null = none.</summary>
        static string Draw()
        {
            for (int guard = 0; guard < 3; guard++)
            {
                if (bag.Count == 0)
                {
                    Scan(true);
                    if (files.Count == 0) return null;
                    bag.AddRange(files);
                    for (int i = 0; i < bag.Count; i++)
                    {
                        int j = UnityEngine.Random.Range(i, bag.Count);
                        var x = bag[i]; bag[i] = bag[j]; bag[j] = x;
                    }
                    if (bag.Count > 1 && bag[0] == lastPlayed) { var x = bag[0]; bag[0] = bag[bag.Count - 1]; bag[bag.Count - 1] = x; }
                    Log("new round of " + bag.Count + " tracks");
                }
                while (bag.Count > 0)
                {
                    string f = bag[0];
                    bag.RemoveAt(0);
                    if (File.Exists(Path.Combine(Dir, f))) { lastPlayed = f; return f; }
                }
            }
            return null;
        }

        /// <summary>Start the next track: loads it, waits out the gap, then plays it (if Uma FM is still the station).</summary>
        static void Next(string why, float gap)
        {
            string f = Draw();
            if (f == null) { Log("no tracks left, back to the game radio"); ToGame(); return; }
            Load(f, 0f, why, gap);
        }

        static void Load(string file, float at, string why, float gap)
        {
            if (radio == null) return;
            pending = true;
            gapUntil = Time.time + gap;
            radio.StartCoroutine(LoadCo(file, at, why, ++loadId));
        }

        static IEnumerator LoadCo(string file, float at, string why, int id)
        {
            string path = Path.Combine(Dir, file);
            float t0 = Time.realtimeSinceStartup;
            var w = new WWW(new Uri(path).AbsoluteUri);
            yield return w;
            if (id != loadId) { w.Dispose(); yield break; }
            AudioClip c = null;
            if (string.IsNullOrEmpty(w.error))
            {
                c = w.GetAudioClipCompressed(false, AudioType.OGGVORBIS);
                while (c != null && c.loadState == AudioDataLoadState.Loading) yield return null;
            }
            string err = !string.IsNullOrEmpty(w.error) ? w.error
                : c == null ? "no clip" : c.loadState == AudioDataLoadState.Failed ? "decode failed" : c.length < 1f ? "empty" : null;
            w.Dispose();
            if (id != loadId) { if (c != null) UnityEngine.Object.Destroy(c); yield break; }
            if (err != null)
            {
                Debug.LogWarning("[UncleSwap] UmaFM: " + file + " skipped: " + err);
                if (c != null) UnityEngine.Object.Destroy(c);
                files.Remove(file);
                pending = false;
                if (uma) Next("after a bad file", 0f);
                yield break;
            }
            c.name = Path.GetFileNameWithoutExtension(file);
            while (Time.time < gapUntil && id == loadId) yield return null;
            if (id != loadId) { UnityEngine.Object.Destroy(c); yield break; }

            var old = clip;
            clip = c;
            clipFile = file;
            clipTime = Mathf.Clamp(at, 0f, Mathf.Max(0f, c.length - 1f));
            pending = false;
            if (uma && src != null) PutClip();
            if (old != null && old != c) UnityEngine.Object.Destroy(old);
            Log(c.name + " " + Clock(clipTime) + "/" + Clock(c.length) + " (" + why + ", " + bag.Count + " of " + files.Count
                + " left in the round, loaded in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms)");
            if (!memLogged)
            {
                memLogged = true;
                long kb = 0;
                try { kb = new FileInfo(path).Length / 1024; } catch (Exception) { }
                Log("memory: clip " + (UnityEngine.Profiling.Profiler.GetRuntimeMemorySize(c) / 1024) + " KB (0 = not counted in a release build), file "
                    + kb + " KB, " + c.channels + " ch " + c.frequency + " Hz, " + c.loadType + ", one track in memory at a time");
            }
            SaveState();
        }

        /// <summary>Our clip onto the game's source at clipTime; paused at once if the radio is already silent.</summary>
        static void PutClip()
        {
            if (src == null || clip == null) return;
            src.Stop();
            src.clip = clip;
            src.Play();
            src.time = Mathf.Clamp(clipTime, 0f, Mathf.Max(0f, clip.length - 0.1f));
            paused = false;
            if (car != null && !car.radioOn && src.volume <= 0.001f) { src.Pause(); paused = true; }
        }

        // ------------------------------------------------------------ stations
        /// <summary>Uma FM takes the source: the game's song is kept to come back to.</summary>
        static void Take()
        {
            if (src == null) return;
            if (src.clip != null && src.clip != clip) { gameClip = src.clip; gameTime = src.time; }
            bool on = car != null && car.radioOn;
            if (clip != null) { PutClip(); return; }
            if (on) src.Stop();   // silence while the first track loads
            if (!string.IsNullOrEmpty(clipFile) && files.Contains(clipFile)) Load(clipFile, clipTime, "resume", 0f);
            else Next("start", 0f);
        }

        static void ToUma()
        {
            if (uma) return;
            uma = true;
            SetHiss();
            Take();
            Log("station on" + (clip != null ? " (" + clip.name + " at " + Clock(clipTime) + ")" : ""));
            SaveState();
        }

        static void ToGame()
        {
            if (src != null && clip != null && src.clip == clip) { clipTime = src.time; src.Stop(); }
            bool was = uma;
            uma = false;
            paused = false;
            pending = false;
            loadId++;                          // drop a load in progress
            if (src != null && radio != null && (src.clip == null || src.clip == clip || !src.isPlaying))
            {
                AudioClip g = radio.songShuffle.Count > 0 ? radio.songShuffle[0] : gameClip;
                if (g != null)
                {
                    src.clip = g;
                    src.Play();
                    if (g == gameClip) src.time = Mathf.Clamp(gameTime, 0f, Mathf.Max(0f, g.length - 0.5f));
                }
            }
            SetHiss();
            if (!Enabled && clip != null) { UnityEngine.Object.Destroy(clip); clip = null; clipFile = null; clipTime = 0f; }
            if (was) { Log("station off (game radio)"); SaveState(); }
        }

        static void SetHiss()
        {
            if (hiss != null && hiss.mute != uma) hiss.mute = uma;
        }

        static void NeedleMid(RadioFreqLogicC r, float time)
        {
            if (r == null || r.needle == null) return;
            iTween.Stop(r.needle, "MoveTo");
            var mid = Vector3.Lerp(r.needleMoveFrom, r.needleMoveTo, 0.5f);
            iTween.MoveTo(r.needle, iTween.Hash("position", mid, "time", time, "islocal", true, "easetype", r.dialEaseType));
        }

        /// <summary>The game's battery checks of Trigger: when they fail, the game's own error wiggle runs.</summary>
        static bool BatteryOk()
        {
            if (perf == null || car == null || perf.installedBattery == null) return false;
            var ec = perf.installedBattery.GetComponent<EngineComponentC>();
            if (ec == null) return false;
            return !((ec.charge <= 0f && !car.engineOn) || ec.Condition <= 0f);
        }

        // ------------------------------------------------------------ patches
        static bool TriggerPrefix(RadioFreqLogicC __instance, ref IEnumerator __result)
        {
            try
            {
                if (radio != __instance) Attach(__instance);
                if (car == null || !BatteryOk()) return true;
                if (!car.radioOn)
                {
                    if (uma) ToGame();         // a click from off is always the game radio
                    return true;
                }
                if (uma) return true;          // Uma FM -> off: the game's TurnRadioOff
                Scan(false);
                if (!Available) return true;   // no tracks: game -> off as in the game
                __result = SwitchCo(__instance);
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UncleSwap] UmaFM: Trigger failed, game behaviour: " + e);
                return true;
            }
        }

        static IEnumerator SwitchCo(RadioFreqLogicC r)
        {
            if (clickSrc != null && r.onSFX != null) clickSrc.PlayOneShot(r.onSFX, 0.7f);
            NeedleMid(r, r.tweenTime * 0.7f);
            ToUma();
            yield break;
        }

        static bool NextPrefix(RadioFreqLogicC __instance)
        {
            try
            {
                if (radio != __instance) Attach(__instance);
                if (News.TakeNext(__instance)) return false;   // task 52: a news bulletin between songs (the one shared prefix)
                if (!uma) return true;
                if (!Available) { ToGame(); return true; }
                if (pending || paused || src == null || src.pitch <= 0.001f) return false;   // pitch 0 = pause menu
                if (clip == null && !string.IsNullOrEmpty(clipFile) && files.Contains(clipFile))
                {
                    Load(clipFile, clipTime, "reloaded", 0f);   // the clip was unloaded under us
                    return false;
                }
                if (clip != null && src.clip == clip)
                {
                    if (src.isPlaying)
                    {
                        src.Stop();            // her trick / debug: next track now
                        Next("skipped", 0.3f);
                        return false;
                    }
                    if (clipTime < clip.length - 2f)
                    {
                        // stopped mid-track by something else: go on from there (at most every 2 s)
                        if (Time.realtimeSinceStartup >= restartAt)
                        {
                            restartAt = Time.realtimeSinceStartup + 2f;
                            src.Play();
                            src.time = clipTime;
                            Log("restarted " + clip.name + " at " + Clock(clipTime));
                        }
                        return false;
                    }
                }
                // the track ended (or nothing of ours on the source)
                clipTime = 0f;
                Next("next", UnityEngine.Random.Range(1f, 2f));
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UncleSwap] UmaFM: NextSong failed, back to the game radio: " + e);
                uma = false;
                SetHiss();
                return true;
            }
        }

        static void OnPostfix(RadioFreqLogicC __instance, ref IEnumerator __result)
        {
            __result = OnWrap(__result, __instance);
        }

        /// <summary>TurnRadioOn not from our click (ignition with the radio on): on Uma FM the needle goes mid-dial.</summary>
        static IEnumerator OnWrap(IEnumerator inner, RadioFreqLogicC r)
        {
            bool first = true;
            while (true)
            {
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception e) { Debug.LogWarning("[UncleSwap] UmaFM: TurnRadioOn: " + e.Message); yield break; }
                if (first)
                {
                    first = false;
                    if (uma && r == radio && car != null && car.radioOn) NeedleMid(r, r.tweenTime);
                }
                if (!more) yield break;
                yield return inner.Current;
            }
        }

        static void Tick(RadioFreqLogicC __instance)
        {
            try
            {
                if (radio != __instance) Attach(__instance);
                News.Tick(__instance);                         // task 52: gap timer, drops
                if (!uma) return;
                if (!Available) { Scan(true); if (!Available) { ToGame(); return; } }
                SetHiss();
                if (src == null || clip == null || src.clip != clip) return;
                bool on = car != null && car.radioOn;
                if (!paused)
                {
                    clipTime = src.time;
                    if (!on && src.isPlaying && src.volume <= 0.001f)
                    {
                        src.Pause();
                        paused = true;
                        Log("paused " + clip.name + " at " + Clock(clipTime) + " (radio off)");
                        SaveState();
                    }
                }
                else if (on)
                {
                    src.UnPause();
                    paused = false;
                    Log("resumed " + clip.name + " at " + Clock(src.time));
                }
                if (Time.realtimeSinceStartup >= saveAt) SaveState();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UncleSwap] UmaFM: tick failed, back to the game radio: " + e);
                try { ToGame(); } catch (Exception) { uma = false; }
            }
        }

        // ------------------------------------------------------------ debug panel
        public static string DebugNext()
        {
            if (radio == null) return "no radio seen yet (get in the car)";
            if (!Enabled) return "the Uma FM toggle is off";
            Scan(true);
            if (files.Count == 0) return "no tracks in " + Dir;
            if (car == null || !car.radioOn) return "the radio is off: click it twice";
            if (!uma)
            {
                radio.StartCoroutine(SwitchCo(radio));
                return "switched to Uma FM";
            }
            if (pending) return "a track is loading";
            if (src != null) src.Stop();
            Next("debug", 0.3f);
            return "next track (" + bag.Count + " of " + files.Count + " left in the round)";
        }

        public static string Status()
        {
            if (!uma) return "game radio";
            return "Uma FM " + (clip != null ? clip.name + " " + Clock(clipTime) : pending ? "loading" : "-") + (paused ? " (paused)" : "");
        }

        // ------------------------------------------------------------ state (ModSaves\FukuUncle\umafm.txt)
        static string StatePath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "ModSaves"), "FukuUncle"), "umafm.txt"); }
        }

        static void SaveState()
        {
            saveAt = Time.realtimeSinceStartup + 10f;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath,
                    "station=" + (uma ? "uma" : "game") + "\n"
                    + "track=" + (clipFile ?? "") + "\n"
                    + "time=" + clipTime.ToString("F2", CultureInfo.InvariantCulture) + "\n"
                    + "last=" + (lastPlayed ?? "") + "\n"
                    + "bag=" + string.Join("|", bag.ToArray()) + "\n");
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] UmaFM: state not saved: " + e.Message); }
        }

        static void ReadState()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                foreach (var line in File.ReadAllLines(StatePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    switch (k)
                    {
                        case "station": uma = v == "uma"; break;
                        case "track": clipFile = v.Length > 0 ? v : null; break;
                        case "time": float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out clipTime); break;
                        case "last": lastPlayed = v.Length > 0 ? v : null; break;
                        case "bag":
                            bag.Clear();
                            foreach (var f in v.Split('|')) if (f.Length > 0) bag.Add(f);
                            break;
                    }
                }
                Log("state: station " + (uma ? "Uma FM" : "game") + (clipFile != null ? ", " + clipFile + " at " + Clock(clipTime) : "") + ", " + bag.Count + " in the round");
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] UmaFM: state not read: " + e.Message); }
        }

        static string Clock(float s)
        {
            int t = Mathf.Max(0, Mathf.FloorToInt(s));
            return (t / 60) + ":" + (t % 60).ToString("00");
        }
    }
}
