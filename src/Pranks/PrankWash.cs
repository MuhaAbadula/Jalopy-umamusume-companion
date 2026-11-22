using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    public partial class Life
    {
        /// <summary>
        /// Task 38, N1 "rain from the washer": dry weather, daytime. She reaches for the washer button, the wipers go on and the
        /// washer sprays (WindowWipersLogicC.Trigger first, so the spray's own two sweeps don't fight the running wipers), line
        /// prank_wash. Undone = the player switches the wipers off (car.windowWipersOn true -> false) in the 10 s window.
        /// If he leaves them on, she switches them off herself about 20 s later (her hand to the wiper switch).
        /// Water, tank and battery are checked first: the game shows a red warning without them (recon 36 §1).
        /// </summary>
        class PrankWash : PrankKind
        {
            public override string Id { get { return "N1"; } }
            /// <summary>Bug 3 (v0.41.3): the default key prank_n* has no lines in the pack.</summary>
            public override string Line { get { return "prank_wash"; } }

            WindowWipersLogicC wipers;
            WindowWiperSprayLogicC spray;
            Vector3 sprayAim, wipersAim;
            int waterBefore;
            bool sheStarted, offActed;
            float offAt = -1f, offT = -1f;

            bool Find()
            {
                if (wipers == null) { wipers = L.CabPart<WindowWipersLogicC>(); if (wipers != null) wipersAim = VisibleCentre(wipers.transform); }
                if (spray == null) { spray = L.CabPart<WindowWiperSprayLogicC>(); if (spray != null) sprayAim = VisibleCentre(spray.transform); }
                return wipers != null && spray != null;
            }

            string PowerWhy()
            {
                int w = L.WaterCharges();
                if (w < 0) return "no water tank";
                if (w == 0) return "no water";
                return L.BatteryWhy();
            }

            public override string Can()
            {
                if (L.car == null) return "no car";
                if (!Find()) return "no washer/wipers found";
                if (L.car.rainLvl != 0 || L.raining) return "raining";
                if (!L.IsDay()) return "night";
                if (sheStarted) return "her wipers still on";
                if (L.car.windowWipersOn || wipers.currentPos != 0) return "wipers already on";
                if (spray.currentPos != 0) return "washer busy";
                return PowerWhy();
            }

            public override Beat Start(out string note)
            {
                note = "water " + L.WaterCharges();
                return new Beat { Dur = 2.4f, Look = Look.Reach, LookW = 0.85f, Face = "focused", FaceW = 0.4f, ActAt = 0.5f };
            }

            public override void Arm(Beat b, float env)
            {
                if (spray != null) L.WantReachTo("washer button", spray.transform, sprayAim, false, false, ReachCurve(b));
            }

            public override string Act(float now)
            {
                if (!Find()) return "no washer/wipers found";
                if (L.car.windowWipersOn) return "wipers already on";
                string why = PowerWhy();
                if (why != null) return why;
                waterBefore = L.WaterCharges();
                // wipers first: windowWipersOn goes true at once, so the spray skips its own two sweeps (WaterWipe)
                wipers.StartCoroutine(wipers.Trigger());
                spray.StartCoroutine(spray.Trigger());
                sheStarted = true;
                offAt = now + Random.Range(18f, 22f);
                offT = -1f;
                How = "wipers on + washer";
                return null;
            }

            public override float CheckDelay { get { return 0.6f; } }

            public override string Checked(float now)
            {
                if (!L.car.windowWipersOn) { sheStarted = false; return "wipers did not start"; }
                int w = L.WaterCharges();
                How = "wipers on + washer (water " + waterBefore + " -> " + w + ")";
                return null;
            }

            public override string UndoneBy(float now)
            {
                if (L.car != null && !L.car.windowWipersOn) { sheStarted = false; return "wipers off"; }
                return null;
            }

            /// <summary>Her own wipers, ~20 s after: she switches them off herself if he did not.</summary>
            public override void Tick(float now, bool inCar, bool idle)
            {
                if (!sheStarted) return;
                if (L.car == null || wipers == null || !L.car.windowWipersOn) { sheStarted = false; offT = -1f; return; }   // he (or a flat battery) did it
                if (L.pk == this) return;   // its own 10 s window first
                if (offT < 0f)
                {
                    if (now < offAt) return;
                    // wait for a free moment: no beat, no arm act, no gesture, no other prank
                    if (!idle || L.beat != null || L.PrankBusy || L.act != Life.Act.None || L.GestureWeight > 0.01f || L.patT >= 0f) return;
                    offT = 0f;
                    offActed = false;
                    L.nextBeat = Mathf.Max(L.nextBeat, now + 3f);
                }
                if (!inCar) { offT = -1f; offAt = now + 5f; return; }
                offT += Time.deltaTime;
                const float dur = 2.2f;
                float w = Mathf.Clamp01(Mathf.Min((offT - 0.1f) / 0.6f, (dur - 0.3f - offT) / 0.6f));
                L.WantReachTo("wiper switch", wipers.transform, wipersAim, false, false, w);
                if (idle) { L.curLook = Look.Reach; L.curLookW = 0.8f; }
                if (!offActed && offT >= 0.5f && (L.ReachDone || offT >= 1.5f))
                {
                    offActed = true;
                    if (L.car.windowWipersOn && wipers.currentPos == 1) wipers.StartCoroutine(wipers.Trigger());
                    PLog("N1 wipers off by herself" + (L.ReachDone ? ", hand short " + L.ReachShort.ToString("F3") + " u" : ""));
                }
                if (offT >= dur) { offT = -1f; sheStarted = false; }
            }
        }
    }
}
