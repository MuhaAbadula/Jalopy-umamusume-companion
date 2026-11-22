using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 08 + 19: the luck of the day. Level -2..+2 (L1 great luck = +2 ... L5 bad luck = -2), told once per day:
    /// in the morning after the alarm line (Morning.cs), else in the car when you first drive off.
    /// The draw is seeded by the game's DirectorC.daysPassed (part of the save), so a reload gives the same level.
    /// ModSaves\FukuUncle\luck.txt keeps the level with its forDay; another day in the game = pending, drawn again.
    /// Other modules read <see cref="Level"/>; game effects go through LuckFx.
    /// </summary>
    public static class Luck
    {
        /// <summary>Settings toggle AND the character has the "fortune" feature (task 30).</summary>
        public static bool Enabled = true;
        /// <summary>The picked character tells fortunes (info.txt features). False = every fortune path is off.</summary>
        public static bool Mine { get { return CharFeatures.Has("fortune"); } }
        public const string NotMine = "Fortune: not this character";
        /// <summary>Chances of L1..L5 (any scale).</summary>
        public static float[] Chances = { 10f, 25f, 30f, 25f, 10f };

        static int level, forDay = -1, salt, shopRemarkDay = -1;
        static bool pending = true, forced, loaded;

        /// <summary>Luck of the day, -2..+2 (0 also when the toggle is off or nothing is drawn yet).</summary>
        public static int Level { get { Sync(); return Enabled && !pending ? level : 0; } }
        /// <summary>A new day began and the luck is not drawn yet.</summary>
        public static bool Pending { get { Sync(); return pending; } }
        /// <summary>The game's day counter (DirectorC.daysPassed), -1 until the save is loaded.</summary>
        public static int Day { get { Sync(); return GameDay(); } }
        /// <summary>Time.time of the announcement (-1 = not this session).</summary>
        public static float AnnouncedAt = -1f;

        public static bool Happy { get { return Level > 0 && AnnouncedAt >= 0f; } }
        public static bool Bad { get { return Level == -2; } }
        /// <summary>Bad luck, the first two minutes after she said it: nervous.</summary>
        public static bool Nervous { get { return Bad && AnnouncedAt >= 0f && Time.time - AnnouncedAt < 120f; } }

        /// <summary>LuckFx: the shop remark of the day was said (kept in luck.txt, per game day).</summary>
        public static bool ShopRemarkDone
        {
            get { int d = Day; return d >= 0 && shopRemarkDay == d; }
            set { int d = Day; if (d < 0) return; shopRemarkDay = value ? d : -1; Save(); }
        }

        static string StatePath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "ModSaves"), "FukuUncle"), "luck.txt"); }
        }

        // ---- the game's day ----
        static DirectorC director;
        static float findAt;

        static int GameDay()
        {
            // MainMenuC.LateStart loads daysPassed a few frames after the scene starts
            if (Time.timeSinceLevelLoad < 2f) return -1;
            if (director == null)
            {
                if (Time.realtimeSinceStartup < findAt) return -1;
                findAt = Time.realtimeSinceStartup + 1f;
                director = UnityEngine.Object.FindObjectOfType<DirectorC>();
                if (director == null) return -1;
            }
            return director.daysPassed;
        }

        /// <summary>The game is on another day than luck.txt (new sleep, an older or newer save): pending again.</summary>
        static void Sync()
        {
            Load();
            int d = GameDay();
            if (d < 0 || d == forDay) return;
            int was = forDay;
            forDay = d;
            pending = true;
            forced = false;
            salt = 0;
            AnnouncedAt = -1f;
            Save();
            Debug.Log("[UncleSwap] Luck: day " + d + " (luck.txt had day " + was + "): pending, seed " + Seed(d, 0));
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(StatePath)) return;
                bool hasForDay = false;
                foreach (var raw in File.ReadAllLines(StatePath))
                {
                    var kv = raw.Split('=');
                    if (kv.Length != 2) continue;
                    int v;
                    if (!int.TryParse(kv[1].Trim(), out v)) continue;
                    switch (kv[0].Trim())
                    {
                        case "level": level = Mathf.Clamp(v, -2, 2); break;
                        case "forDay": forDay = v; hasForDay = true; break;
                        case "pending": pending = v != 0; break;
                        case "forced": forced = v != 0; break;
                        case "salt": salt = v; break;
                        case "shopRemarkDay": shopRemarkDay = v; break;
                    }
                }
                if (!hasForDay) { forDay = -1; pending = true; }   // pre-0.35 file: no game day, draw again
                if (pending) Debug.Log("[UncleSwap] Luck: day " + forDay + " pending (load)");
                else Debug.Log("[UncleSwap] Luck: day " + forDay + " seed " + Seed(forDay, salt) + " -> L=" + level + " (loaded" + (forced ? ", forced" : "") + ")");
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] Luck: can't read state: " + e.Message); }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllLines(StatePath, new[] { "level=" + level, "forDay=" + forDay, "pending=" + (pending ? 1 : 0),
                    "forced=" + (forced ? 1 : 0), "salt=" + salt, "shopRemarkDay=" + shopRemarkDay });
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] Luck: can't save state: " + e.Message); }
        }

        /// <summary>A real motel sleep ended (daysPassed is already +1): a new day, the luck is drawn again.</summary>
        public static void NewDay()
        {
            Sync();
            pending = true;
            forced = false;
            salt = 0;
            AnnouncedAt = -1f;
            Save();
            Debug.Log("[UncleSwap] Luck: new day " + forDay);
        }

        /// <summary>Seed of a day (+ debug salt): same on every machine and every load.</summary>
        public static int Seed(int day, int s)
        {
            unchecked
            {
                uint h = (uint)day * 0x9E3779B1u ^ (uint)s * 0x85EBCA77u ^ 0x46554B55u;
                h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
                return (int)(h & 0x7FFFFFFFu);
            }
        }

        /// <summary>Seeded pick by the chances, -2..+2; noBad leaves L5 out.</summary>
        static int Roll(int seed, bool noBad)
        {
            int n = Chances.Length;
            float sum = 0f;
            for (int i = 0; i < n; i++) if (!(i == 4 && noBad)) sum += Mathf.Max(0f, Chances[i]);
            int pick = 2;
            if (sum > 0f)
            {
                float r = (float)new System.Random(seed).NextDouble() * sum;
                for (int i = 0; i < n; i++)
                {
                    if (i == 4 && noBad) continue;
                    float c = Mathf.Max(0f, Chances[i]);
                    if (c <= 0f) continue;
                    pick = i;
                    r -= c;
                    if (r < 0f) break;
                }
            }
            return 2 - pick;
        }

        /// <summary>Seeded draw for the game's day; bad luck never two days in a row (by yesterday's raw draw).
        /// source "debug" (Ctrl+F5) adds a salt, so it can give another level.</summary>
        public static int Draw(string source)
        {
            Sync();
            int d = GameDay();
            if (d < 0) d = Mathf.Max(0, forDay);
            if (source == "debug") salt++;
            bool noBad = Roll(Seed(d - 1, 0), false) == -2;
            int seed = Seed(d, salt);
            int lvl = Roll(seed, noBad);
            forDay = d;
            forced = false;
            Apply(lvl);
            Debug.Log("[UncleSwap] Luck: day " + d + " seed " + seed + " -> L=" + lvl + " (seeded" + (salt > 0 ? ", debug salt " + salt : "")
                + (noBad ? ", yesterday was bad" : "") + ", " + source + ")");
            return level;
        }

        /// <summary>Forced level for the current game day (debug), kept until the next day.</summary>
        public static void Set(int lvl, string source)
        {
            Sync();
            int d = GameDay();
            if (d >= 0) forDay = d;
            forced = true;
            Apply(lvl);
            Debug.Log("[UncleSwap] Luck: day " + forDay + " seed " + Seed(forDay, salt) + " -> L=" + level + " (forced, " + source + ")");
        }

        static void Apply(int lvl)
        {
            level = Mathf.Clamp(lvl, -2, 2);
            pending = false;
            AnnouncedAt = Time.time;
            Save();
        }

        /// <summary>Story key of the announcement: fortune_1 (great) .. fortune_5 (bad).</summary>
        public static string Key(int lvl) { return "fortune_" + (3 - lvl); }
    }

    public partial class FukuDriver
    {
        enum Fort { None, Start, Pray, Tell }
        Fort fort = Fort.None;
        float fortAt;
        bool fortTalkSeen, fortPose, fortL5bDone = true, fortDebug;
        string fortSource;
        int fortForce = 99;
        float fortL5bAt = -1f;

        /// <summary>Starts the fortune scene (morning, car or debug). forceLevel 99 = draw.</summary>
        void FortBegin(string source, int forceLevel)
        {
            if (fort != Fort.None || life == null) return;
            if (!Luck.Mine) { Debug.Log("[UncleSwap] Luck: fortune (" + source + ") skipped, not this character"); return; }
            fortSource = source;
            fortForce = forceLevel;
            fort = Fort.Start;
            fortAt = Time.time;
            fortTalkSeen = false;
            fortPose = false;
            Debug.Log("[UncleSwap] Luck: fortune starts (" + source + ")");
        }

        bool FortCanSay() { return life != null && (life.CanSay == null || life.CanSay()); }

        /// <summary>Every frame from Update.</summary>
        void FortUpdate()
        {
            if (life == null) return;
            float now = Time.time;

            // L5b: a minute after bad luck
            if (!fortL5bDone && fortL5bAt >= 0f && now >= fortL5bAt && fort == Fort.None)
            {
                if (Luck.Level != -2) fortL5bDone = true;
                else if (FortCanSay())
                {
                    fortL5bDone = true;
                    bool said = life.StorySay != null && life.StorySay("fortune_5b");
                    Debug.Log("[UncleSwap] Luck: " + (said ? "line fortune_5b" : "no line fortune_5b"));
                }
            }

            if (fort == Fort.None) { FortCarCheck(); return; }
            float t = now - fortAt;
            bool talking = animator != null && animator.GetBool("talking");
            switch (fort)
            {
                case Fort.Start:
                    if (!fortTalkSeen)
                    {
                        if (t > 10f) { Debug.Log("[UncleSwap] Luck: fortune skipped (bubble busy)"); fort = Fort.None; return; }
                        if (!FortCanSay()) return;
                        fortTalkSeen = true;
                        fortAt = now;
                        if (life.StorySay == null || !life.StorySay("fortune_start")) Debug.Log("[UncleSwap] Luck: no line fortune_start");
                        return;
                    }
                    // the line typed out (or 3 s), then the 3 s freeze
                    if ((t > 0.5f && !talking && FortCanSay()) || t > 6f)
                    {
                        fort = Fort.Pray;
                        fortAt = now;
                        fortPose = life.FortPoseBegin();
                    }
                    return;
                case Fort.Pray:
                    if (t < 3f) return;
                    if (fortPose) life.FortPoseEnd();
                    int lvl = fortForce != 99 ? fortForce : Luck.Draw(fortSource);
                    if (fortForce != 99) Luck.Set(lvl, fortSource);
                    fort = Fort.Tell;
                    fortAt = now;
                    fortL5bDone = lvl != -2;
                    fortL5bAt = now + 60f;
                    return;
                case Fort.Tell:
                    if (FortCanSay())
                    {
                        string key = Luck.Key(Luck.Level);
                        bool said = life.StorySay != null && life.StorySay(key);
                        Debug.Log("[UncleSwap] Luck: " + (said ? "line " + key : "no line " + key));
                        if (said) life.FortAnnounced(Luck.Level);
                        fort = Fort.None;
                    }
                    else if (t > 10f) { Debug.Log("[UncleSwap] Luck: announcement skipped (bubble busy)"); fort = Fort.None; }
                    return;
            }
        }

        // fallback: no morning fortune today -> in the car when you first drive off
        float fortCarCheckAt, fortDriveFor;
        void FortCarCheck()
        {
            if (Time.time < fortCarCheckAt) return;
            fortCarCheckAt = Time.time + 0.25f;
            if (!Luck.Enabled || !Luck.Pending || mornPhase != MPhase.Off || Doze.Gate || ReturnTrip.MorningPending) { fortDriveFor = 0f; return; }
            var u = GetComponent<UncleLogicC>();
            if (u == null || u.uncleAtHome || u.uncleGoneForever || !InCar() || Asleep()) { fortDriveFor = 0f; return; }
            var pc = u.player != null ? u.player.GetComponent<RigidbodyControllerC>() : null;
            if (fortCar == null) fortCar = FindObjectOfType<CarControleScriptC>();
            float speed = fortCar != null ? Mathf.Abs(fortCar.currentSpeed) : 0f;
            fortDriveFor = pc != null && pc.isSat && speed > 5f ? fortDriveFor + 0.25f : 0f;
            if (fortDriveFor >= 3f && FortCanSay()) FortBegin("car", 99);
        }
        CarControleScriptC fortCar;

        /// <summary>Called by Morning after the alarm pose (task 07 hook).</summary>
        void FortMorning()
        {
            if (!Luck.Enabled) { Debug.Log("[UncleSwap] Luck: toggle off"); return; }
            if (!Luck.Pending) return;
            if (mornSilent) { Debug.Log("[UncleSwap] Luck: Istanbul morning, after her talk in the car"); return; }
            FortBegin("morning", 99);
        }

        /// <summary>Debug: Ctrl+F5 the whole scene with a new draw.</summary>
        public string FortuneDebugRedraw()
        {
            if (!Luck.Mine) return Luck.NotMine;
            if (fort != Fort.None) return "Fortune: already running";
            FortBegin("debug", 99);
            return "Fortune: drawing now";
        }

        int fortCycle = 2;
        /// <summary>Debug: Ctrl+F6 next level (L1..L5), said at once; L5 also gives L5b in 60 s.</summary>
        public string FortuneDebugCycle()
        {
            if (!Luck.Mine) return Luck.NotMine;
            if (fort != Fort.None) return "Fortune: already running";
            int lvl = fortCycle;
            fortCycle = fortCycle <= -2 ? 2 : fortCycle - 1;
            Luck.Set(lvl, "debug");
            fort = Fort.Tell;
            fortAt = Time.time;
            fortL5bDone = lvl != -2;
            fortL5bAt = Time.time + 60f;
            return "Fortune: L" + (3 - lvl) + " (level " + lvl + ")";
        }
    }

    public partial class Life
    {
        float fortT = -1f, fortOutT = -1f, fortW;

        /// <summary>Praying: palms together at the chest, eyes shut, ears still.</summary>
        public bool FortPoseBegin()
        {
            if (!Enabled || !Luck.Mine || armL == null || armR == null || chest == null) return false;
            fortT = 0f;
            fortOutT = -1f;
            beat = null;
            if (GestureWeight > 0f && StopGesture != null) StopGesture();
            Debug.Log("[UncleSwap] Luck: pose pray");
            return true;
        }

        public void FortPoseEnd()
        {
            if (fortT < 0f || fortOutT >= 0f) return;
            fortOutT = 0f;
        }

        /// <summary>The announcement was said: face for the level.</summary>
        public void FortAnnounced(int lvl)
        {
            if (!Luck.Mine) return;
            if (lvl == 2) Emote("happy", 4f, 1f);
            else if (lvl == 1) Emote("smile", 3.5f, 0.9f);
            else if (lvl == -1) Emote("worried", 3f, 0.6f);
            else if (lvl == -2) Emote("shock", 3.5f, 0.9f);
        }

        /// <summary>From Update, after MornFace.</summary>
        void FortFace(float dt)
        {
            if (fortT < 0f) { fortW = 0f; return; }
            fortT += dt;
            float outK = 1f;
            if (fortOutT >= 0f)
            {
                fortOutT += dt;
                outK = 1f - Mathf.SmoothStep(0f, 1f, fortOutT / 0.5f);
                if (fortOutT >= 0.5f) { fortT = fortOutT = -1f; fortW = 0f; Debug.Log("[UncleSwap] Luck: pose done"); return; }
            }
            fortW = Mathf.SmoothStep(0f, 1f, fortT / 0.5f) * outK;
            Face.Want("asleep", fortW, 0.15f);   // eyes closed
            nextEar = Mathf.Max(nextEar, Time.time + 0.5f);   // ears still
            beat = null;
        }

        /// <summary>From PoseBones, after MornPose: both hands together in front of the chest.</summary>
        void FortPose()
        {
            if (fortW <= 0.001f) return;
            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 right = Vector3.Cross(up, fwd);
            float len = ArmLen(armR);
            Vector3 c = chest.T.position + fwd * (0.42f * len) - up * (0.05f * len);
            foreach (var a in new[] { armL, armR })
            {
                float side = Vector3.Dot(a.Upper.position - chest.T.position, right) >= 0f ? 1f : -1f;
                Vector3 wrist = c + right * (side * 0.05f * len) - up * (0.12f * len);
                SolveArm(a, wrist, -up * 0.6f + right * side, fortW);
                Vector3 cur = Knuckles(a) - a.Hand.position;
                Vector3 axis = (up + fwd * 0.25f).normalized;   // fingers up, palms facing each other
                if (cur.sqrMagnitude > 1e-10f)
                    a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, axis), fortW) * a.Hand.rotation;
                PoseFingers(a, false, fortW);
            }
        }

        /// <summary>Mood of the day: how much busier/calmer the beats are.</summary>
        float LuckActivity { get { return Luck.Happy ? 1.4f : Luck.Nervous ? 1.6f : 1f; } }
    }
}
