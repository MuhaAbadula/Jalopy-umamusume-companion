using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Prank N7 "Бу!" (task 39, concept 07 rev 2). By day, while he drives, she pretends to doze: Doze.Fake goes straight into
    /// the sleep pose (head to her window, eyes shut), no drowse, no mumbles. When he looks at her head for 1-1.8 s she sits
    /// up at once (the doze pose's 0.35 s release), surprise face, "Бу!" (prank_boo), then the core's giggle and 10 s caught
    /// window. No look for 40 s: she sits up silently. A pat while she pretends = caught (prank_caught, shy face). No undone.
    /// Every other wake (brake, pothole, dialogue, border, thunder, G, Ctrl+F8...) ends it silently.
    /// Log: Prank: N7 start | acted: fake doze | boo ... | done | timeout ... | caught (pat) | skip ...
    /// </summary>
    public partial class Life
    {
        const float BooTimeout = 40f;
        const float BooCone = 25f;          // degrees between his view and her head centre that count as "looking at her"
        float booStart = -1f, booAt = -1f, booGaze, booNeed, booMinAng, booFaceUntil = -1f;

        class PrankBoo : PrankKind
        {
            public override string Id { get { return "N7"; } }

            public override string Can()
            {
                if (L.night || (L.hourNow >= 0f && Doze.InNight(L.hourNow))) return "night";
                if (Doze.Phase != DozePhase.Awake) return "dozing";
                if (!L.PlayerSeated()) return "player out";
                if (L.SpeedKmh() < 15f) return "not driving";
                var u = L.logic;
                if (u != null && u.restrictTalk) return "border";
                if (u != null && (u.withinRangeOfMotel || u.hasSaidLetsCheckIn)) return "motel";
                if (L.DozeDialog(u)) return "conversation";
                if (Camera.main == null) return "no camera";
                return null;
            }

            public override Beat Start(out string note)
            {
                note = "she settles against the window";
                // a short look out of her window, then the fake doze starts (Act); no hand target: Act fires 1 s after ActAt
                return new Beat { Dur = 1.5f, Look = Look.Window, LookW = 0.7f, Face = "sleepy", FaceW = 0.5f, ActAt = 0.3f };
            }

            public override string Act(float now)
            {
                if (Doze.Phase != DozePhase.Awake) return "dozing";
                L.booStart = now;
                L.booAt = -1f;
                L.booGaze = 0f;
                L.booMinAng = 180f;
                L.booNeed = Random.Range(1f, 1.8f);
                L.DozeFake();
                How = "fake doze";
                return null;
            }

            // BooStep fires Checked itself (pCheckAt = now) on his look; this is only a safety net
            public override float CheckDelay { get { return BooTimeout + 20f; } }

            public override string Checked(float now) { return L.booAt >= 0f ? null : "lost (no look, no timeout)"; }

            public override string Line { get { return "prank_boo"; } }

            public override void End()
            {
                if (Doze.Fake) L.DozeFakeEnd("prank over", 0.4f);
                L.booStart = -1f;
            }
        }

        bool BooOurs { get { return pk is PrankBoo && pState == PState.Check && booAt < 0f; } }

        /// <summary>His view angle to her head centre (degrees), 180 if unknown.</summary>
        float BooGazeAngle()
        {
            var cam = Camera.main;
            Vector3 c;
            float r;
            if (cam == null || !HeadSphere(out c, out r)) return 180f;
            Vector3 to = c - cam.transform.position;
            if (to.sqrMagnitude < 1e-8f) return 180f;
            return Vector3.Angle(cam.transform.forward, to);
        }

        /// <summary>From DozeStep while Doze.Fake (Yawn/Drowse/Sleep, after the wake checks). True = the fake doze is over.</summary>
        bool BooStep(float now, float dt, bool seated)
        {
            if (!BooOurs) { DozeFakeEnd("no prank", 0.4f); return true; }
            if (!seated) { DozeFakeEnd("player out", 0.4f); PrankEnd("skip player out"); return true; }
            float ang = BooGazeAngle();
            booMinAng = Mathf.Min(booMinAng, ang);
            // inside the cone the look adds up; outside it drains twice as fast (a glance past her does not count)
            booGaze = ang < BooCone ? booGaze + dt : Mathf.Max(0f, booGaze - 2f * dt);
            if (booGaze >= booNeed)
            {
                booAt = now;
                PLog("N7 boo after " + (now - booStart).ToString("F1") + " s (he looked " + booGaze.ToString("F1") + " s, "
                    + ang.ToString("F0") + " deg off her head)");
                DozeFakeEnd("boo", 0.08f);
                booFaceUntil = now + 0.6f;
                earMoves.Add(new EarMove { Name = "fwd_l", Start = now, In = 0.06f, Hold = 0.8f, Out = 0.5f, W = 1f });
                earMoves.Add(new EarMove { Name = "fwd_r", Start = now, In = 0.06f, Hold = 0.8f, Out = 0.5f, W = 1f });
                pk.How = "boo";
                pCheckAt = now;   // PrankStep (later this frame) runs Checked: her line, the giggle, Attend(Player), 10 s window
                return true;
            }
            if (now - booStart >= BooTimeout)
            {
                DozeFakeEnd("timeout", 0.5f);
                PrankEnd("timeout (he did not look; closest " + booMinAng.ToString("F0") + " deg)");
                return true;
            }
            return false;
        }

        /// <summary>From DozeWake while Doze.Fake: any other wake ends the prank silently.</summary>
        void BooWoken(string reason)
        {
            bool ours = BooOurs;
            DozeFakeEnd(reason, 0.3f);
            if (ours) PrankEnd("skip woken (" + reason + ")");
        }

        /// <summary>From DozePat while Doze.Fake: caught. She opens her eyes under his hand and owns up (like PrankCatch).</summary>
        void BooCaught(float now)
        {
            bool ours = BooOurs;
            DozeFakeEnd("caught", 0.15f);
            if (!ours) return;
            patAsleep = false;                       // the pat plays as an awake one (happy, ears flat), not the sleepy smile
            Attend(Look.Player, patDur + 1.5f, 1f);
            PrankEnd("caught (pat)");
            QueueLine("prank_caught", 0.4f, 6f);
            caughtUntil = now + 4.5f;
            giggleAt = now + 2.6f;                   // after the pat (2.4 s)
        }

        /// <summary>From DozeApply every frame: "Бу!" face for 0.6 s after the sit-up, her line at once, the giggle after the face.</summary>
        void BooFace(float now)
        {
            if (now >= booFaceUntil) return;
            Face.Want("surprise", 1f, 0.06f);
            Face.Solo("surprise", 1f);
            if (giggleAt >= 0f && giggleAt < booFaceUntil) giggleAt = booFaceUntil;
            if (pLine == "prank_boo" && pLineAt > now + 0.1f) pLineAt = now + 0.1f;
        }
    }
}
