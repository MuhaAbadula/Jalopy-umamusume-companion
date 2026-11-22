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
    /// Task 52 (concept 10 rev 1): news bulletins between songs on the game radio and on Uma FM.
    /// A timer of real seconds runs while a radio plays (power on, news toggle of that station on); after the gap
    /// (slider, 8 min) a bulletin is armed. At the end of the current song (UmaFm.NextPrefix, the one shared NextSong
    /// prefix, calls TakeNext first) the bulletin plays on the radio's own AudioSource instead of the next song, piece by
    /// piece; when it is over NextSong runs as usual (the game drops the finished song, Uma FM loads its next track).
    /// Nothing is cut. Power off / station switch / toggle off mid-bulletin = dropped (the items stay unheard, still armed).
    /// Files: Mods\Assets\FukuUncle\radio\news\{game,umafm}\ (task 51), or radio\news_test\ when news\ has no list.tsv.
    /// Pools without repeats (each country, WORLD+SPORT, UMA); heard ids + timer in ModSaves\FukuUncle\news_heard.txt.
    /// </summary>
    public static class News
    {
        public static bool GameOn = true, UmaOn = true;
        public static float GapMinutes = 8f;
        public static string RadioDir;          // Mods\Assets\FukuUncle\radio

        class Item { public string Id, Country; public float Sec; }

        class Piece
        {
            public string File;
            public Item News;                   // null = jingle / intro
            public AudioClip Clip;
            public string Name { get { return News != null ? News.Id : Path.GetFileNameWithoutExtension(File); } }
        }

        class Station
        {
            public string Name, Dir;
            public readonly List<Item> Items = new List<Item>();
        }

        static RadioFreqLogicC owner;           // the radio a bulletin runs on (dies with the scene)
        static AudioSource src;
        static CarLogicC car;
        static bool busy;                       // a bulletin is loading or playing: NextSong does nothing
        static bool playing;                    // one of our pieces is on the source
        static bool busyUma;                    // the station of the running bulletin
        static int runId;
        static readonly List<Piece> pieces = new List<Piece>();
        static bool armed;
        static float timer, saveAt, lastSkipAt = -100f;
        static string lastSkip;
        static bool stateRead, applied;
        static readonly HashSet<string> heard = new HashSet<string>();   // "game/<id>", "umafm/<id>"

        /// <summary>A bulletin is on the air (radio on): she looks at the radio and does not bob.</summary>
        public static bool OnAir { get { return busy && playing && owner != null && car != null && car.radioOn; } }

        static float GapSec { get { return Mathf.Max(1f, GapMinutes) * 60f; } }

        static void Log(string s) { Debug.Log("[UncleSwap] News: " + s); }
        static string St(bool uma) { return uma ? "umafm" : "game"; }
        static bool StationOn(bool uma) { return uma ? UmaOn : GameOn; }

        // ------------------------------------------------------------ reset hooks (new game, return home), as souvenirs
        /// <summary>From SettingsDeclaration (the main menu): the new-game hook must be on before the menu's New Game.</summary>
        public static void Apply()
        {
            if (applied) return;
            applied = true;
            var h = new Harmony("osobl.jalopy.uncleswap.news");
            var erase = AccessTools.Method(typeof(MainMenuClickersC), "EraseSaveData");
            if (erase != null) h.Patch(erase, postfix: new HarmonyMethod(typeof(News), nameof(NewGame)));
            var home = AccessTools.Method(typeof(MainMenuC), "SaveAndReturnHome");
            if (home != null) h.Patch(home, prefix: new HarmonyMethod(typeof(News), nameof(ReturnHome)));
            Log("patches on (new game " + (erase != null) + ", return home " + (home != null) + ")");
        }

        static void NewGame() { try { Reset("new game"); } catch (Exception e) { Debug.LogWarning("[UncleSwap] News: reset failed: " + e.Message); } }
        static void ReturnHome() { try { Reset("return home"); } catch (Exception e) { Debug.LogWarning("[UncleSwap] News: reset failed: " + e.Message); } }

        static void Reset(string why)
        {
            stateRead = true;
            heard.Clear();
            timer = 0f;
            armed = false;
            SaveState();
            Log("heard list cleared (" + why + ")");
        }

        // ------------------------------------------------------------ the radio of this scene
        static void See(RadioFreqLogicC r)
        {
            if (!stateRead) { stateRead = true; ReadState(); }
            if (owner == r && r != null) return;
            if (busy) Log(St(busyUma) + " - - skip scene changed (bulletin dropped)");
            Cleanup();
            owner = r;
            src = r != null ? r.GetComponent<AudioSource>() : null;
            car = r != null && r.carLogic != null ? r.carLogic.GetComponent<CarLogicC>() : null;
        }

        static void Cleanup()
        {
            runId++;
            busy = playing = false;
            foreach (var p in pieces)
            {
                if (p.Clip == null) continue;
                if (src != null && src.clip == p.Clip) { src.Stop(); src.clip = null; }
                UnityEngine.Object.Destroy(p.Clip);
            }
            pieces.Clear();
        }

        /// <summary>Update postfix (from UmaFm.Tick, every frame): the gap timer, aborts.</summary>
        public static void Tick(RadioFreqLogicC r)
        {
            try
            {
                See(r);
                if (src == null || car == null) return;
                bool uma = UmaFm.IsUma;
                if (busy)
                {
                    string why = !car.radioOn ? "radio off" : uma != busyUma ? "station switched" : !StationOn(busyUma) ? "toggle off"
                        : playing && CurrentClip() != null && src.clip != CurrentClip() ? "source taken" : null;
                    if (why != null) Drop(why);
                    return;
                }
                if (!car.radioOn || !StationOn(uma) || !src.isPlaying || Time.deltaTime <= 0f) return;
                timer += Time.deltaTime;
                if (!armed && timer >= GapSec)
                {
                    armed = true;
                    Log(St(uma) + " - - armed after " + (timer / 60f).ToString("F1", CultureInfo.InvariantCulture) + " min of radio, at the end of this song");
                    SaveState();
                }
                if (Time.realtimeSinceStartup >= saveAt) SaveState();
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] News: tick failed: " + e.Message); }
        }

        static int curPiece = -1;
        static AudioClip CurrentClip() { return curPiece >= 0 && curPiece < pieces.Count ? pieces[curPiece].Clip : null; }

        static void Drop(string why)
        {
            Log(St(busyUma) + " - - skip " + why + " (bulletin dropped, the rest stays unheard)");
            Cleanup();   // stops our clip: the next NextSong goes on as usual
        }

        /// <summary>
        /// Called first in the shared NextSong prefix. True = News took this call (the prefix returns false):
        /// a bulletin is loading/playing (her radio trick or a debug skip is ignored then), or an armed bulletin starts now
        /// because the song just ended.
        /// </summary>
        public static bool TakeNext(RadioFreqLogicC r)
        {
            try
            {
                See(r);
                if (busy) return true;
                if (!armed || src == null || car == null) return false;
                bool uma = UmaFm.IsUma;
                // only at a real song end: a skip (trick, debug) while it plays is not one
                bool ended = uma ? UmaFm.TrackEnded(src)
                    : !src.isPlaying && src.pitch > 0.001f && src.clip != null && r.songShuffle.Count > 0 && src.clip == r.songShuffle[0];
                if (!ended) return false;
                if (!StationOn(uma)) { Skip(uma, "toggle off"); return false; }
                if (!car.radioOn) { Skip(uma, "radio off"); return false; }
                var st = Load(uma);
                if (st.Items.Count == 0) { Skip(uma, "no news files in " + st.Dir); armed = false; timer = 0f; return false; }
                if (!Build(st, uma))
                {
                    armed = false; timer = 0f;
                    return false;
                }
                busy = true;
                busyUma = uma;
                playing = false;
                curPiece = -1;
                r.StartCoroutine(Run(st, uma, ++runId));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UncleSwap] News: NextSong hook failed, radio as usual: " + e);
                try { Cleanup(); } catch (Exception) { }
                return false;
            }
        }

        static void Skip(bool uma, string why)
        {
            if (why == lastSkip && Time.realtimeSinceStartup - lastSkipAt < 600f) return;
            lastSkip = why;
            lastSkipAt = Time.realtimeSinceStartup;
            Log(St(uma) + " - - skip " + why + " (stays armed)");
        }

        // ------------------------------------------------------------ files
        static Station Load(bool uma)
        {
            var st = new Station { Name = St(uma) };
            string real = Path.Combine(Path.Combine(RadioDir ?? "", "news"), st.Name);
            string test = Path.Combine(Path.Combine(RadioDir ?? "", "news_test"), st.Name);
            st.Dir = File.Exists(Path.Combine(real, "list.tsv")) ? real : File.Exists(Path.Combine(test, "list.tsv")) ? test : real;
            string list = Path.Combine(st.Dir, "list.tsv");
            try
            {
                if (!File.Exists(list)) return st;
                foreach (var raw in File.ReadAllLines(list, System.Text.Encoding.UTF8))
                {
                    var f = raw.Split('\t');
                    if (f.Length < 2) continue;
                    string id = f[0].Trim(), c = f[1].Trim().ToUpperInvariant();
                    if (id.Length == 0 || id.StartsWith("#") || id == "id") continue;
                    if (!File.Exists(Path.Combine(st.Dir, id + ".ogg"))) continue;
                    float sec = 0f;
                    if (f.Length > 2) float.TryParse(f[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out sec);
                    st.Items.Add(new Item { Id = id, Country = c, Sec = sec });
                }
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] News: can't read " + list + ": " + e.Message); }
            return st;
        }

        /// <summary>A random unheard item of the pool (not already in this bulletin); restart = clear the pool's heard ids when empty.</summary>
        static Item Draw(Station st, Func<Item, bool> inPool, string pool, bool restart, List<Item> taken)
        {
            var all = st.Items.FindAll(i => inPool(i) && !taken.Contains(i));
            if (all.Count == 0) return null;
            var fresh = all.FindAll(i => !heard.Contains(st.Name + "/" + i.Id));
            if (fresh.Count == 0)
            {
                if (!restart) return null;
                foreach (var i in st.Items) if (inPool(i)) heard.Remove(st.Name + "/" + i.Id);
                Log(st.Name + " - " + pool + " pool starts again (" + all.Count + " news)");
                fresh = all;
            }
            return fresh[UnityEngine.Random.Range(0, fresh.Count)];
        }

        static bool Build(Station st, bool uma)
        {
            Cleanup();
            var news = new List<Item>();
            if (!uma)
            {
                string c = CountryNow();
                Func<Item, bool> world = i => i.Country == "WORLD" || i.Country == "SPORT";
                Func<Item, bool> here = i => i.Country == c;
                Item a = c != null ? Draw(st, here, c, false, news) : null;
                if (a == null && c != null && st.Items.Exists(i => here(i)))
                {
                    // every news of this country heard: a second world news now, the country pool starts again next time
                    foreach (var i in st.Items) if (here(i)) heard.Remove(st.Name + "/" + i.Id);
                    Log("game - " + c + " pool heard through: a second world news this time, the pool starts again next time");
                }
                if (a != null) news.Add(a);
                var w = Draw(st, world, "WORLD+SPORT", true, news);
                if (w != null) news.Add(w);
                if (a == null)
                {
                    var w2 = Draw(st, world, "WORLD+SPORT", true, news);
                    if (w2 != null) news.Add(w2);
                }
                if (news.Count == 0 && a == null)
                {
                    // no world news at all: whatever the folder has
                    var any = Draw(st, i => true, "any", true, news);
                    if (any != null) news.Add(any);
                }
                Log("game - " + (c ?? "?") + " bulletin: " + string.Join(", ", news.ConvertAll(i => i.Id + " " + i.Country).ToArray()));
            }
            else
            {
                Func<Item, bool> umaPool = i => i.Country == "UMA";
                if (!st.Items.Exists(i => umaPool(i))) umaPool = i => true;
                var a = Draw(st, umaPool, "UMA", true, news);
                if (a != null) news.Add(a);
                if (a != null && UnityEngine.Random.value < 0.5f)
                {
                    var b = Draw(st, umaPool, "UMA", false, news);
                    if (b != null && (a.Sec <= 0f || b.Sec <= 0f || a.Sec + b.Sec <= 30f)) news.Add(b);
                }
                Log("umafm - UMA bulletin: " + string.Join(", ", news.ConvertAll(i => i.Id).ToArray()));
            }
            if (news.Count == 0) { Log(st.Name + " - - skip no news to play"); return false; }

            string js = Path.Combine(st.Dir, "jingle_start.ogg"), je = Path.Combine(st.Dir, "jingle_end.ogg"), intro = Path.Combine(st.Dir, "intro.ogg");
            if (File.Exists(js)) pieces.Add(new Piece { File = js });
            if (File.Exists(intro)) pieces.Add(new Piece { File = intro });
            foreach (var n in news) pieces.Add(new Piece { File = Path.Combine(st.Dir, n.Id + ".ogg"), News = n });
            if (File.Exists(je)) pieces.Add(new Piece { File = je });
            else if (File.Exists(js)) pieces.Add(new Piece { File = js });
            return true;
        }

        /// <summary>Country code of the road under the car (segment prefab name), else by route level; null = unknown.</summary>
        static string CountryNow()
        {
            var d = GameObject.FindWithTag("Director");
            var rg = d != null ? d.GetComponent<RouteGeneratorC>() : null;
            try
            {
                if (rg != null && car != null && rg.spawnedRouteSegments != null)
                {
                    float z = car.transform.position.z;
                    foreach (var g in rg.spawnedRouteSegments)
                    {
                        var sc = g != null ? g.GetComponent<SpawnContinueC>() : null;
                        if (sc == null) continue;
                        float top = sc.transform.position.z;
                        if (z > top + 5f || z < top - 1005f) continue;   // a segment runs 1000 u towards -z from its root
                        string c = Code(RoadSpots.Country(sc.name));
                        if (c != null) return c;
                    }
                }
            }
            catch (Exception) { }
            if (rg == null) return null;
            string[] east = { "GER", "CSFR", "HUN", "YUGO", "BUL", "TURK", "TURK" };
            string[] home = { "GER", "GER", "GER", "CSFR", "HUN", "YUGO", "BUL" };
            int lv = Mathf.Clamp(rg.routeLevel, 0, 6);
            return rg.drivingTowardsHome ? home[lv] : east[lv];
        }

        static string Code(string country)
        {
            switch (country)
            {
                case "Germany": return "GER";
                case "CSFR": return "CSFR";
                case "Hungary": return "HUN";
                case "Yugoslavia": return "YUGO";
                case "Bulgaria": return "BUL";
                case "Turkey": return "TURK";
            }
            return null;
        }

        // ------------------------------------------------------------ playing
        static IEnumerator Run(Station st, bool uma, int id)
        {
            string s = St(uma);
            float t0 = Time.realtimeSinceStartup;
            // load every piece first (short ogg, compressed in memory like Uma FM tracks)
            for (int k = 0; k < pieces.Count; k++)
            {
                var p = pieces[k];
                var w = new WWW(new Uri(p.File).AbsoluteUri);
                yield return w;
                if (id != runId) { w.Dispose(); yield break; }
                AudioClip c = null;
                if (string.IsNullOrEmpty(w.error))
                {
                    c = w.GetAudioClipCompressed(false, AudioType.OGGVORBIS);
                    while (c != null && c.loadState == AudioDataLoadState.Loading) yield return null;
                }
                string err = !string.IsNullOrEmpty(w.error) ? w.error : c == null ? "no clip" : c.loadState == AudioDataLoadState.Failed ? "decode failed" : c.length < 0.1f ? "empty" : null;
                w.Dispose();
                if (id != runId) { if (c != null) UnityEngine.Object.Destroy(c); yield break; }
                if (err != null)
                {
                    if (c != null) UnityEngine.Object.Destroy(c);
                    Log(s + " " + p.Name + " " + (p.News != null ? p.News.Country : "-") + " skip bad file: " + err);
                    continue;
                }
                c.name = "news_" + p.Name;
                p.Clip = c;
            }
            pieces.RemoveAll(p => p.Clip == null);
            if (!pieces.Exists(p => p.News != null))
            {
                Log(s + " - - skip nothing loaded");
                Cleanup();
                armed = false; timer = 0f;
                yield break;
            }
            Log(s + " - - bulletin of " + pieces.Count + " pieces loaded in " + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("F0") + " ms");

            // a breath after the song, as between songs
            float until = Time.time + 0.8f;
            while (Time.time < until) { if (id != runId) yield break; yield return null; }

            float started = Time.time;
            for (curPiece = 0; curPiece < pieces.Count; curPiece++)
            {
                if (id != runId || src == null) yield break;
                var p = pieces[curPiece];
                src.Stop();
                src.clip = p.Clip;
                src.time = 0f;
                src.Play();
                playing = true;
                // done = stopped at a running pitch (pitch 0 = the pause menu: isPlaying stays true, wait)
                while (true)
                {
                    yield return null;
                    if (id != runId || src == null) yield break;
                    if (src.clip != p.Clip) yield break;   // Tick drops the bulletin
                    if (!src.isPlaying && src.pitch > 0.001f) break;
                }
                if (p.News != null)
                {
                    heard.Add(s + "/" + p.News.Id);
                    SaveState();
                    Log(s + " " + p.News.Id + " " + p.News.Country + " played (" + p.Clip.length.ToString("F1", CultureInfo.InvariantCulture) + " s)");
                    if (owner != null) owner.StartCoroutine(LineLater(s, p.News));
                }
                // short gap between pieces (silence, NextSong still ours)
                if (curPiece < pieces.Count - 1)
                {
                    until = Time.time + (p.News == null ? 0.25f : 0.5f);
                    while (Time.time < until) { if (id != runId) yield break; yield return null; }
                }
            }
            if (id != runId) yield break;
            Log(s + " - - bulletin done (" + (Time.time - started).ToString("F0") + " s), songs go on");
            armed = false;
            timer = 0f;
            SaveState();
            Cleanup();   // source empty and stopped: the next Update calls NextSong, which runs as usual now
        }

        static IEnumerator LineLater(string s, Item n)
        {
            yield return new WaitForSeconds(1f);
            string key = "news_" + n.Id;
            var L = Life.Current;
            string res = L != null && L.Enabled ? L.NewsLine(key) : "skip she is not here";
            Log(s + " " + n.Id + " " + n.Country + " " + res + " (" + key + ")");
        }

        // ------------------------------------------------------------ debug panel
        public static string DebugArm()
        {
            if (owner == null) return "no radio seen yet (get in the car)";
            bool uma = UmaFm.IsUma;
            if (!StationOn(uma)) return "the news toggle of " + St(uma) + " is off";
            if (busy) return "a bulletin is on now";
            armed = true;
            timer = Mathf.Max(timer, GapSec);
            string left = src != null && src.clip != null && src.isPlaying ? ", song " + Clock(src.clip.length - src.time) + " left" : "";
            Log(St(uma) + " - - armed by debug");
            return "bulletin at the end of this song (" + St(uma) + left + (car != null && !car.radioOn ? ", radio is off: switch it on" : "") + ")";
        }

        public static string DebugResetHeard()
        {
            int n = heard.Count;
            heard.Clear();
            SaveState();
            Log("heard list cleared (debug, " + n + " ids)");
            return n + " heard ids cleared";
        }

        public static string Status()
        {
            if (busy) return "news on air (" + St(busyUma) + ")";
            if (armed) return "news armed";
            return "news in " + Clock(Mathf.Max(0f, GapSec - timer));
        }

        // ------------------------------------------------------------ state (ModSaves\FukuUncle\news_heard.txt)
        static string StatePath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "ModSaves"), "FukuUncle"), "news_heard.txt"); }
        }

        static void SaveState()
        {
            saveAt = Time.realtimeSinceStartup + 30f;
            try
            {
                var g = new List<string>();
                var u = new List<string>();
                foreach (var k in heard)
                {
                    if (k.StartsWith("game/")) g.Add(k.Substring(5));
                    else if (k.StartsWith("umafm/")) u.Add(k.Substring(6));
                }
                g.Sort(StringComparer.Ordinal);
                u.Sort(StringComparer.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath,
                    "timer=" + timer.ToString("F1", CultureInfo.InvariantCulture) + "\n"
                    + "game=" + string.Join("|", g.ToArray()) + "\n"
                    + "umafm=" + string.Join("|", u.ToArray()) + "\n");
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] News: state not saved: " + e.Message); }
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
                    if (k == "timer") float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out timer);
                    else if (k == "game" || k == "umafm")
                        foreach (var id in v.Split('|')) if (id.Length > 0) heard.Add(k + "/" + id);
                }
                Log("state: " + heard.Count + " heard, next bulletin in " + Clock(Mathf.Max(0f, GapSec - timer)) + " of radio");
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] News: state not read: " + e.Message); }
        }

        static string Clock(float sec)
        {
            int t = Mathf.Max(0, Mathf.FloorToInt(sec));
            return (t / 60) + ":" + (t % 60).ToString("00");
        }
    }
}
