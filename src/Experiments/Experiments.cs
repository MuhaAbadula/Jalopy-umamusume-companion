using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 87 (concept 13, part A): Tachyon's potions, features=experiments. Outside the car, about every PotionMinutes
    /// (x0.7..1.3, none in the first 5 min after boarding) she holds out a glowing vial (exp_offer). A click on it within 15 s:
    /// the vial goes to the player's mouth, an effect starts (ExpFx, E1..E15; strong ones only on foot and with the setting on).
    /// No click: exp_refused, and 2-5 min later the effect starts anyway (exp_sneak). The effect's end: exp_log_name, or exp_cut
    /// when the player sat down. Journal: ModSaves\FukuUncle\experiments.txt (samples, fails); in Istanbul exp_summary once.
    /// The vial is a mod-made primitive (no pack prop needed). Log: [UncleSwap] Exp: ...
    /// </summary>
    public partial class FukuDriver
    {
        /// <summary>Settings: "Experiments", "PotionMinutes", "AddonMinutes" (0 = never).</summary>
        public static bool ExpOn = true;
        public static float PotionMinutes = 30f, AddonMinutes = 35f;

        static bool ExpHere { get { return CharFeatures.Has("experiments"); } }

        enum Offer { None, Holding, Drinking }
        Offer offer;
        float offerUntil, offerT, offerW, potClock = -1f, sneakAt = -1f;
        int lastFx, debugFx;
        bool expInit, expWasInCar, expBlockLogged;
        bool offerInCar;   // user 2026-11-19: she offers in the car too (left hand, toward the driver)
        string expBlockLast;

        // the vial: glass, glowing liquid, a small light
        GameObject vial;
        Light vialLight;
        Color vialColor;
        float vialH;
        bool vialTilt;

        static readonly Color[] PotionColors =
        {
            new Color(0.3f, 1f, 0.9f), new Color(0.5f, 1f, 0.3f), new Color(1f, 0.35f, 0.9f), new Color(1f, 0.75f, 0.2f), new Color(0.65f, 0.4f, 1f)
        };

        // ------------------------------------------------------------ lines (wait for a free bubble up to 30 s)
        string expLine;
        float expLineUntil;

        void ExpSay(string key)
        {
            expLine = key;
            expLineUntil = Time.time + 30f;
        }

        void ExpLines(UncleLogicC u)
        {
            if (expLine == null) return;
            if (Time.time > expLineUntil) { ExpFx.Log("line " + expLine + " dropped (bubble busy)"); expLine = null; return; }
            if (remarks == null || remarks.Count == 0) { expLine = null; return; }
            if (!remarks.CanSay(u)) return;
            string text = remarks.Pick(expLine);
            if (text == null) ExpFx.Log("no line for " + expLine + " in this pack");
            else
            {
                text = text.Replace("{n}", expSamples.ToString()).Replace("{fail}", expFails.ToString());
                StartCoroutine(remarks.Say(u, text));
            }
            expLine = null;
        }

        // ------------------------------------------------------------ journal
        int expSamples, expFails;
        bool expSummarySaid;

        static string JournalPath
        {
            get { return Path.Combine(Path.Combine(Path.Combine(Application.persistentDataPath, "ModSaves"), "FukuUncle"), "experiments.txt"); }
        }

        void JournalLoad()
        {
            try
            {
                if (!File.Exists(JournalPath)) return;
                foreach (var line in File.ReadAllLines(JournalPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0 || line.StartsWith("#")) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    int n;
                    if (k == "samples" && int.TryParse(v, out n)) expSamples = n;
                    if (k == "fails" && int.TryParse(v, out n)) expFails = n;
                    if (k == "summary") expSummarySaid = v == "1";
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[UncleSwap] Exp: journal not read: " + e.Message); }
        }

        void JournalSave(string entry)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
                var lines = new List<string> { "# FukuUncle: Tachyon's experiment journal", "samples=" + expSamples, "fails=" + expFails, "summary=" + (expSummarySaid ? "1" : "0") };
                if (File.Exists(JournalPath))
                    foreach (var l in File.ReadAllLines(JournalPath)) if (l.StartsWith("log ")) lines.Add(l);
                if (entry != null) lines.Add("log " + entry);
                File.WriteAllLines(JournalPath, lines.ToArray());
            }
            catch (System.Exception e) { Debug.LogWarning("[UncleSwap] Exp: journal not saved: " + e.Message); }
        }

        void JournalAdd(string what, bool fail)
        {
            expSamples++;
            if (fail) expFails++;
            var route = RouteGeneratorC.Global;
            int level = route != null ? route.routeLevel : 0;
            string country = level >= 1 && level <= 6 ? Souvenirs.Country(level) : "-";
            JournalSave("sample " + expSamples + " " + what + (fail ? " fail" : "") + " " + country);
            ExpFx.Log("journal: sample " + expSamples + " " + what + " in " + country + (fail ? " (fail)" : ""));
        }

        void SummaryStep(UncleLogicC u)
        {
            var route = RouteGeneratorC.Global;
            if (route == null) return;
            if (expSummarySaid && route.routeLevel < 6 && !route.drivingTowardsHome)
            {
                // a new trip east: new journal
                expSummarySaid = false;
                expSamples = expFails = 0;
                JournalSave(null);
                return;
            }
            if (expSummarySaid || route.routeLevel != 6 || expSamples == 0 || !InCar() || expLine != null) return;
            expSummarySaid = true;
            JournalSave(null);
            ExpSay("exp_summary");
        }

        public string ExpSummaryNow() { ExpSay("exp_summary"); return "summary: " + expSamples + " samples, " + expFails + " fails"; }

        // ------------------------------------------------------------ per frame (Update, after UpdateOuting)
        void ExpInit()
        {
            expInit = true;
            ExpFx.OnEnd = OnFxEnd;
            Life.ExtraCursor = VialAimed;
            Life.PatBusy = () => pouring && pourPh != PourPh.Walk;
            JournalLoad();
        }

        static float ExpGap(float minutes) { return Random.Range(0.7f, 1.3f) * Mathf.Max(0.5f, minutes) * 60f; }

        void UpdateExp()
        {
            if (!expInit) ExpInit();
            var u = OutU();
            if (u == null || life == null) return;
            float dt = Time.deltaTime, now = Time.time;
            ExpLines(u);
            AddonStep(u, dt, now);
            if (!ExpHere) { if (offer != Offer.None) EndOffer("not this character"); return; }
            SummaryStep(u);

            bool inCar = InCar();
            if (inCar && !expWasInCar && potClock >= 0f) potClock = Mathf.Max(potClock, 300f);
            expWasInCar = inCar;

            if (offer != Offer.None) { OfferStep(u, now); return; }

            if (sneakAt > 0f && now >= sneakAt && !ExpFx.Active)
            {
                sneakAt = -1f;
                if (StartPotion("sneak")) ExpSay("exp_sneak");
            }

            if (!ExpOn || PotionMinutes < 0.01f) return;
            if (potClock < 0f) potClock = Mathf.Max(300f, ExpGap(PotionMinutes));
            if (u.uncleAtHome || u.uncleGoneForever) return;
            if (!ExpFx.Active) potClock -= dt;
            if (potClock > 0f) return;
            string block = OfferBlock(u);
            if (block != null)
            {
                if (block != expBlockLast) { expBlockLast = block; ExpFx.Log("potion due, waiting: " + block); }
                return;
            }
            expBlockLast = null;
            StartOffer(u, false);
        }

        /// <summary>Why she can't hold out a vial now (null = she can).</summary>
        string OfferBlock(UncleLogicC u)
        {
            bool car = outState == Out.InCar && InCar() && PlayerSat();   // both seated in the car
            if (!car && outState != Out.Follow && outState != Out.Wait) return "not with you (outside or both in the car)";
            if (fetching || pouring) return "busy";
            if (ExpFx.Active) return "an effect runs";
            if (!car && PlayerSat()) return "you sit in the car, she is outside";
            if (car && (Doze.Muted || (animator != null && animator.GetBool("bedTime")))) return "asleep";
            if (u.restrictTalk) return "border";
            if (BubbleShown(u)) return "talking";
            var cam = Camera.main;
            if (cam == null) return "no camera";
            if (!car && Flat(cam.transform.position - transform.position).magnitude > 4f * walkLegLen) return "you are too far";
            return null;
        }

        void StartOffer(UncleLogicC u, bool debug)
        {
            vialColor = PotionColors[Random.Range(0, PotionColors.Length)];
            MakeVial();
            offerInCar = outState == Out.InCar;
            offer = Offer.Holding;
            offerT = 0f;
            offerUntil = Time.time + 15f;
            life.LookAtPlayer(16f);
            life.Mood("smug", 3f);
            ExpSay("exp_offer");
            ExpFx.Log("offer" + (offerInCar ? " in the car" : "") + (debug ? " (debug)" : ""));
        }

        void OfferStep(UncleLogicC u, float now)
        {
            offerT += Time.deltaTime;
            if (offer == Offer.Holding)
            {
                bool still = offerInCar ? (outState == Out.InCar && InCar() && PlayerSat()) : (outState == Out.Follow || outState == Out.Wait);
                if (!still) { EndOffer(offerInCar ? "someone left the car" : "she went back to the car"); potClock = Random.Range(60f, 120f); return; }
                if (Life.Click() && VialAimed())
                {
                    offer = Offer.Drinking;
                    offerT = 0f;
                    ExpSay("exp_drink");
                    ExpFx.Log("drink");
                    return;
                }
                if (now > offerUntil)
                {
                    EndOffer("refused");
                    ExpSay("exp_refused");
                    life.Mood("worried", 3f);
                    sneakAt = now + Random.Range(120f, 300f);
                    potClock = ExpGap(PotionMinutes);
                }
                return;
            }
            // drinking: the vial flies to the mouth, then the effect
            if (offerT >= 1.2f)
            {
                EndOffer("drunk");
                StartPotion("drink");
                potClock = ExpGap(PotionMinutes);
            }
        }

        void EndOffer(string why)
        {
            offer = Offer.None;
            DropVial();
            ExpFx.Log("offer end: " + why);
        }

        bool StartPotion(string why)
        {
            int id = PickFx(PlayerSat());
            string r = ExpFx.Start(id, vialColor == default(Color) ? PotionColors[0] : vialColor, ExpFx.DurationOf(id));
            if (r != null) { ExpFx.Log("potion " + why + " failed: " + r); return false; }
            lastFx = id;
            JournalAdd(ExpFx.Id(id) + " " + ExpFx.Name(id) + " (" + why + ")", id == 5);
            if (id == 5) ExpSay("exp_nothing");
            return true;
        }

        int PickFx(bool atWheel)
        {
            var ids = new List<int>();
            var weights = new List<float>();
            float sum = 0f;
            for (int id = 1; id <= ExpFx.Count; id++)
            {
                if (id == lastFx) continue;
                if (ExpFx.Strong(id) && !ExpFx.StrongOn) continue;   // user 2026-11-19: at the wheel too
                float wgt = id == 14 ? 0.25f : 1f;
                ids.Add(id);
                weights.Add(wgt);
                sum += wgt;
            }
            float r = Random.value * sum;
            for (int i = 0; i < ids.Count; i++) { r -= weights[i]; if (r <= 0f) return ids[i]; }
            return ids.Count > 0 ? ids[ids.Count - 1] : 1;
        }

        void OnFxEnd(int id, bool cut)
        {
            if (cut) ExpSay("exp_cut");
            else ExpSay("exp_log_" + ExpFx.Name(id));
        }

        // ------------------------------------------------------------ the vial
        void MakeVial()
        {
            DropVial();
            float arm = life.ArmLength > 0f ? life.ArmLength : 0.7f;
            vialH = 0.26f * arm;   // ~12 cm for a 45 cm arm
            vial = new GameObject("FukuVial");
            var glass = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(glass.GetComponent<Collider>());
            glass.transform.SetParent(vial.transform, false);
            glass.transform.localScale = new Vector3(0.24f * vialH, 0.5f * vialH, 0.24f * vialH);
            Tint(glass, new Color(0.85f, 0.95f, 1f, 0.35f));
            var liquid = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(liquid.GetComponent<Collider>());
            liquid.transform.SetParent(vial.transform, false);
            liquid.transform.localPosition = new Vector3(0f, -0.15f * vialH, 0f);
            liquid.transform.localScale = new Vector3(0.2f * vialH, 0.33f * vialH, 0.2f * vialH);
            Tint(liquid, vialColor);
            var go = new GameObject("FukuVialLight");
            go.transform.SetParent(vial.transform, false);
            vialLight = go.AddComponent<Light>();
            vialLight.type = LightType.Point;
            vialLight.range = 1.2f * arm;
            vialLight.intensity = 1.2f;
            vialLight.color = vialColor;
            vialLight.shadows = LightShadows.None;
            vialTilt = false;
            offerW = 0f;
        }

        static Shader vialShader;

        static void Tint(GameObject g, Color c)
        {
            if (vialShader == null)
                foreach (var n in new[] { "Sprites/Default", "Unlit/Color", "Legacy Shaders/Diffuse", "Diffuse" })
                {
                    vialShader = Shader.Find(n);
                    if (vialShader != null) break;
                }
            var r = g.GetComponent<Renderer>();
            if (r == null) return;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (vialShader != null) r.material = new Material(vialShader);
            r.material.color = c;
        }

        void DropVial()
        {
            if (vial != null) Destroy(vial);
            vial = null;
            vialLight = null;
            StopStream();
        }

        bool VialAimed()
        {
            if (vial == null || offer != Offer.Holding) return false;
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 to = vial.transform.position - cam.transform.position;
            float along = Vector3.Dot(to, cam.transform.forward);
            if (along <= 0f || along > 4.25f) return false;
            return (to - cam.transform.forward * along).magnitude < 0.9f * vialH;
        }

        /// <summary>From Pose, after PoseBones and the crate hands: her hand and the vial for this frame.</summary>
        void PoseExpHands()
        {
            if (life == null) return;
            float dt = Time.deltaTime;
            float arm = life.ArmLength;
            if (arm <= 0f) return;
            Vector3 sh = life.ShoulderR;
            PinCuffs(0f, 0f);

            if (offer != Offer.None && vial != null)
            {
                Vector3 cb;
                if (offerInCar) PinCuffs(offerW, 0f); else PinCuffs(0f, offerW);
                var cam = Camera.main;
                if (cam == null) return;
                offerW = Mathf.MoveTowards(offerW, offer == Offer.Holding ? 1f : 0f, dt / 0.5f);
                Vector3 inHand;
                if (offerInCar)
                {
                    // seated right of the driver: left hand toward his chest, between the seats, below his eyes
                    Vector3 shL = life.ShoulderL;
                    Vector3 eye = cam.transform.position - Vector3.up * (0.45f * arm);
                    Vector3 to = eye - shL;
                    to = to.sqrMagnitude > 1e-6f ? to.normalized : -transform.right;
                    Vector3 wrist = shL + to * (0.7f * arm) - Vector3.up * (0.15f * arm);
                    life.HandToL(wrist, Vector3.down - transform.right * 0.4f, Flat(to), offerW);
                    inHand = CuffBase(true, arm, out cb) ? cb + Vector3.up * (0.5f * vialH) : life.TipL + Vector3.up * (0.3f * vialH);
                }
                else
                {
                    Vector3 toCam = Flat(cam.transform.position - sh);
                    toCam = toCam.sqrMagnitude > 1e-6f ? toCam.normalized : transform.forward;
                    Vector3 wrist = sh + toCam * (0.75f * arm) - Vector3.up * (0.2f * arm);
                    life.HandTo(wrist, Vector3.down + transform.right * 0.4f, toCam, offerW);
                    inHand = CuffBase(false, arm, out cb) ? cb + Vector3.up * (0.5f * vialH) : life.TipR + Vector3.up * (0.3f * vialH);
                }
                if (offer == Offer.Drinking)
                {
                    float k = Mathf.SmoothStep(0f, 1f, offerT / 0.6f);
                    Vector3 mouth = cam.transform.position + cam.transform.forward * (0.12f * arm) - cam.transform.up * (0.08f * arm);
                    vial.transform.position = Vector3.Lerp(inHand, mouth, k);
                    vial.transform.rotation = Quaternion.FromToRotation(Vector3.up, Vector3.Slerp(Vector3.up, -cam.transform.forward, 0.75f * k));
                    if (offerT > 1.0f && vial.activeSelf) vial.SetActive(false);
                }
                else
                {
                    vial.transform.position = inHand;
                    vial.transform.rotation = Quaternion.identity;
                }
                if (vialLight != null) vialLight.intensity = 1f + 0.3f * Mathf.Sin(Time.time * 3f);
                return;
            }
            PosePourHands(sh, arm, dt);
        }

        // v0.52.5 (user): Tachyon's long cuffs hide her hands (the pack has no hand mesh), so a vial at the fingertips sat
        // deep inside the sleeve. It now stands up out of the cuff's top slit over the hidden fingers, its bottom in the
        // cloth, as if held through the sleeve; that cuff segment is held still, the rest of the cuff hangs.
        // Checked in Blender: work/preview/vial_cuff/sheet_v12_v13.png (v13 = this spot, overhang hanging ~85 deg).
        const string CuffL = "Sp_El_Sleeve0_L_00", CuffR = "Sp_El_Sleeve0_R_00";
        const float CuffAlong = 0.215f, CuffLift = 0.025f;   // arm lengths: at the fold where the overhang drops (user: visible)

        /// <summary>Bottom of a vial held through the cuff (false: this pack has no such cuff, use the fingertips).</summary>
        bool CuffBase(bool left, float arm, out Vector3 b)
        {
            Vector3 dir;
            b = Vector3.zero;
            if (springs == null || !springs.RestAxisPoint(left ? CuffL : CuffR, CuffAlong * arm, out b, out dir)) return false;
            b += Vector3.up * (CuffLift * arm);
            return true;
        }

        void PinCuffs(float left, float right)
        {
            if (springs == null) return;
            springs.PinFirst(CuffL, left);
            springs.PinFirst(CuffR, right);
        }
    }
}
