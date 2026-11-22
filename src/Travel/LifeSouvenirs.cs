using System;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 47: her side of the souvenirs (state and the row: Souvenirs.cs). Beat souvenir_place: her hand to the spot (ReachTo, no
    /// lean; spots 1-2 driver-side arm, 3-6 window-side), the item rides on her fingertips, at ReachDone it hops to the spot (0.25 s),
    /// then line souvenir_&lt;code&gt;, giggle (pranks packs) or a happy face, a look at him. Click on an item (ray against its box +1 cm;
    /// a nearer game collider or an interactive one within 0.3 u keeps the click for the game): she says its line again, at most
    /// once per 10 s; the interact cursor shows over an item (PatCursor).
    /// </summary>
    public partial class Life
    {
        const string SouvBeat = "souvenir_place";
        int souvK;                       // country of the running beat
        float souvFindAt, souvNextTry, souvClickOk, souvAttnAt = -1f;
        string souvWhy, souvLine;
        float souvLineAt, souvLineUntil;
        bool souvCamLogged;

        Transform SouvCarRoot()
        {
            if (carCtl == null) SpeedKmh();
            if (carCtl != null) return carCtl.transform.root;
            return car != null ? car.transform.root : null;
        }

        static readonly Vector3 SouvAimLocal = new Vector3(0f, 0.03f, 0f);   // 3 cm above the spot (spot space = metres)

        void SouvenirStep(float now, bool inCar, bool slp, bool talking, bool reacting)
        {
            if (!Souvenirs.Here) return;
            var row = Souvenirs.Row.Live;
            if (row == null)
            {
                if (now < souvFindAt) return;
                souvFindAt = now + 2f;
                row = Souvenirs.Row.Build(SouvCarRoot());
                if (row == null) return;
            }
            Souvenirs.CatchUp();
            row.Sync();

            var cam = Camera.main;
            if (!souvCamLogged && inCar && cam != null && PlayerSeated() && Time.timeSinceLevelLoad > 5f)
            {
                souvCamLogged = true;
                Souvenirs.Log(Souvenirs.Pack + " - - camera in car space " + Souvenirs.Row.V(row.WorldToCar(cam.transform.position))
                    + " (recon eye (-0.918, -0.389, 1.160))");
            }
            if (playerCtl != null && !playerCtl.isSat) Souvenirs.SawOut = true;
            if (souvAttnAt >= 0f && now >= souvAttnAt) { souvAttnAt = -1f; Attend(Look.Player, 2.2f, 0.9f); }
            SouvenirLine(now);
            SouvenirClick(now, inCar, slp, talking);

            if (beat != null && beat.Name == SouvBeat) return;
            if (souvK != 0)
            {
                row.Drop(souvK);
                Souvenirs.Log(souvK, "skip cut before the hop (tries again)");
                souvK = 0;
                souvNextTry = now + 5f;
            }
            int k = Souvenirs.NextPending();
            if (k == 0 || now < souvNextTry) return;
            string why = SouvBlock(now, inCar, slp, talking, reacting);
            if (why != null)
            {
                if (why != souvWhy) { souvWhy = why; Souvenirs.Log(k, "waiting: " + why); }
                return;
            }
            souvWhy = null;
            souvK = k;
            beat = new Beat { Name = SouvBeat, Dur = 2.6f, Look = Look.Reach, LookW = 0.95f, Face = "smile", FaceW = 0.6f, ActAt = 0.8f, WaitReach = true, T = 0f };
            Souvenirs.Log(k, "start" + (Souvenirs.Forced ? " (debug)" : ""));
        }

        /// <summary>Why she can't place one now (null = free).</summary>
        string SouvBlock(float now, bool inCar, bool slp, bool talking, bool reacting)
        {
            if (!inCar) return "she is not in her seat";
            if (slp || Doze.Gate) return "asleep";
            if (talking || talkingNow) return "talking";
            if (reacting) return "reacting";
            if (!Souvenirs.Forced)
            {
                if (Souvenirs.NeedBoard && !Souvenirs.SawOut) return "waits for him to get out and back in";
                if (!PlayerSeated()) return "he is not in his seat";
                float v = SpeedKmh();
                if (v >= 10f) return "moving " + v.ToString("F0") + " km/h";
            }
            var u = logic;
            if (u != null && u.restrictTalk) return "border";
            if (ReturnTrip.Playing) return "return trip talk";
            if (CanSay != null && !CanSay()) return "bubble busy";
            if (GestureWeight > 0.01f || act != Act.None || patT >= 0f) return "busy";
            if (PrankBusy || now < sulkUntil || giggleAt >= 0f || now < caughtUntil) return "prank";
            if (HatAway) return "her hat is away";
            if (beat != null && ((beat.ActAt >= 0f && !beat.Acted) || beat.Name.StartsWith("prank_"))) return "busy";
            return null;
        }

        /// <summary>From ArmIntent: the hand to 3 cm above the spot.</summary>
        void SouvenirArm(Beat b)
        {
            var row = Souvenirs.Row.Live;
            if (souvK == 0 || row == null) return;
            WantReachTo("souvenir " + souvK, row.Spots[souvK - 1], SouvAimLocal, souvK >= 3, false, ReachCurve(b));
        }

        Arm SouvArm()
        {
            if (armL == null || armR == null) return null;
            Arm outer, inner;
            Vector3 fwd, up, winDir;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            return souvK >= 3 ? outer : inner;
        }

        /// <summary>Fingertip point a little under the item's middle (the item base, in world space).</summary>
        Vector3 SouvTip(Arm a, Transform spot)
        {
            Vector3 kn = Knuckles(a);
            Vector3 tip = kn + (kn - a.Hand.position) * 0.9f;
            return tip - spot.up * (0.03f * Souvenirs.UPerM);
        }

        /// <summary>From the pose pass (after the arms): the item rides on her fingertips until the hop.</summary>
        void SouvenirPose()
        {
            var row = Souvenirs.Row.Live;
            if (souvK == 0 || row == null || beat == null || beat.Name != SouvBeat || beat.Acted || beat.T < 0.25f) return;
            var a = SouvArm();
            if (a == null) return;
            row.Hold(souvK, SouvTip(a, row.Spots[souvK - 1]));
        }

        /// <summary>From DoAction at ActAt (hand there or 1 s late): hop, save, line.</summary>
        void SouvenirAct(float now)
        {
            int k = souvK;
            var row = Souvenirs.Row.Live;
            souvK = 0;
            if (k == 0 || row == null) return;
            var held = row.ItemT(k);
            Vector3 from;
            var a = SouvArm();
            if (held != null) from = held.position;
            else from = a != null ? SouvTip(a, row.Spots[k - 1]) : row.Spots[k - 1].position;
            row.Hop(k, from);
            Souvenirs.Placed(k);
            Souvenirs.Log(k, "hop from " + (from - row.Spots[k - 1].position).magnitude.ToString("F3") + " u"
                + (ReachDone ? ", hand short " + ReachShort.ToString("F3") + " u" : ", hand still on its way"));
            souvLine = "souvenir_" + Souvenirs.Codes[k - 1];
            souvLineAt = now + 0.35f;
            souvLineUntil = souvLineAt + 8f;
            if (PranksHere) giggleAt = now + 0.5f;   // Sweep: her "fufu"
            else Emote("happy", 2.6f, 0.9f);
            souvAttnAt = now + 0.35f;
            souvNextTry = now + 3f;   // the next one (catch-up) after a breath
        }

        void SouvenirLine(float now)
        {
            if (souvLine == null || now < souvLineAt) return;
            if (Say == null) { souvLine = null; return; }
            if (CanSay == null || CanSay())
            {
                Debug.Log("[Umamusume companion] remark: " + souvLine);
                Say(souvLine);
                souvLine = null;
            }
            else if (now > souvLineUntil) { Souvenirs.Log(Souvenirs.Pack + " - - line " + souvLine + " skipped, bubble busy"); souvLine = null; }
        }

        // ------------------------------------------------------------ click on an item
        string souvBlockedLog;

        /// <summary>The item under the crosshair (1..6), 0 = none or the game's click. why = what kept it (for the log).</summary>
        int SouvAim(out string why)
        {
            why = null;
            var row = Souvenirs.Row.Live;
            var cam = Camera.main;
            if (row == null || cam == null || !Souvenirs.Here || Time.timeScale <= 0f || DebugPanel.IsOpen) return 0;
            var drag = cam.GetComponent<DragRigidbodyC>();
            if (drag != null && (drag.isHolding1 != null || (drag.inputRestrict && !drag.inputRestrictDropOnly))) return 0;
            float reach = drag != null && drag.maxRayDistance > 0f ? drag.maxRayDistance : 4.25f;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            int best = 0;
            float bestD = float.MaxValue;
            for (int k = 1; k <= 6; k++)
            {
                var t = row.ItemT(k);
                if (t == null || !t.gameObject.activeInHierarchy || !Souvenirs.Has(k)) continue;
                Vector3 lo = t.InverseTransformPoint(ray.origin), ld = t.InverseTransformDirection(ray.direction);
                if (ld.sqrMagnitude < 1e-12f) continue;
                float dl;
                if (!row.BoundsOf(k).IntersectRay(new Ray(lo, ld.normalized), out dl)) continue;
                float d = (t.TransformPoint(lo + ld.normalized * dl) - ray.origin).magnitude;
                if (d < bestD) { bestD = d; best = k; }
            }
            if (best == 0 || bestD > reach) return 0;
            RaycastHit hit;
            int mask = drag != null ? drag.myLayerMask.value : Physics.DefaultRaycastLayers;
            if (Physics.Raycast(ray, out hit, reach, mask, QueryTriggerInteraction.Collide))
            {
                string tag = hit.collider.tag;
                bool interactive = tag == "CarInteractor" || tag == "Interactor" || tag == "Pickup";
                if (hit.distance < bestD - 0.02f) { why = "game's " + hit.collider.name + " nearer (" + tag + ")"; return 0; }
                if (interactive && (hit.point - row.ItemT(best).position).magnitude < 0.3f) { why = "game's " + hit.collider.name + " next to it (" + tag + ")"; return 0; }
            }
            return best;
        }

        /// <summary>For PatCursor: an item is under the crosshair.</summary>
        bool SouvenirCursor()
        {
            string why;
            return Souvenirs.Row.Live != null && SouvAim(out why) > 0;
        }

        void SouvenirClick(float now, bool inCar, bool slp, bool talking)
        {
            if (!GameClick()) return;
            string blocked;
            int k = SouvAim(out blocked);
            if (k == 0)
            {
                if (blocked != null && blocked != souvBlockedLog) { souvBlockedLog = blocked; Souvenirs.Log(Souvenirs.Pack + " - - click left to the game: " + blocked); }
                return;
            }
            string why = null;
            if (!inCar) why = "she is not in her seat";
            else if (slp || Doze.Gate) why = "asleep";
            else if (now < souvClickOk) why = "10 s";
            else if (talking || talkingNow || (CanSay != null && !CanSay())) why = "bubble busy";
            else if (Say == null) why = "no remarks";
            if (why != null) { Souvenirs.Log(k, "click skip " + why); return; }
            souvClickOk = now + 10f;
            Souvenirs.Log(k, "click");
            Debug.Log("[Umamusume companion] remark: souvenir_" + Souvenirs.Codes[k - 1]);
            Say("souvenir_" + Souvenirs.Codes[k - 1]);
            // a glance at the item, then at him
            var spot = Souvenirs.Row.Live.Spots[k - 1];
            if (beat == null || beat.ActAt < 0f || beat.Acted)
            {
                reachT = spot;
                reachLocal = SouvAimLocal;
                Attend(Look.Reach, 1.2f, 0.85f);
                souvAttnAt = now + 1.2f;
            }
            Emote("smile", 3f, 0.8f);
        }
    }
}
