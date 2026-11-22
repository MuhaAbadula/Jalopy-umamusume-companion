using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    public partial class Life
    {
        /// <summary>
        /// Task 38, N4 "standing spell": the Laika's handbrake is the drive switch (recon 36 §3: currentPos 1 = released and
        /// driving, 0 = up). He sat, released it at a standstill and presses the throttle: she reaches for the HandbrakeButton and
        /// pulls it (HandbrakeLogicC.Trigger, the player's own click: DrivingStop), so the car stops or never moves; prank_handbrake
        /// and the giggle. Undone = he releases it again (currentPos 0 -> 1) in the 10 s window, which starts when she pulls it.
        /// Not on her own schedule: a release at a standstill arms it when a prank is due soon (the core clock under half a gap)
        /// or by the debug button. Blockers: slope over 6 deg, an AI car within 120 u, motel, the fuel/engine tutorials
        /// (UncleLogicC.HandBrakeCarStopped would start them), border, engine off.
        /// </summary>
        class PrankHandbrake : PrankKind
        {
            public override string Id { get { return "N4"; } }
            /// <summary>Bug 3 (v0.41.3): the default key prank_n* has no lines in the pack.</summary>
            public override string Line { get { return "prank_handbrake"; } }

            HandbrakeLogicC hb;
            Transform grip;
            Vector3 gripAim;
            int lastPos = -1;
            float armedUntil = -1f, debugUntil = -1f;
            bool throttle;
            string lastSkip, lastWait;
            const float MaxKmh = 8f;
            float skipLogAt;

            bool Find()
            {
                if (hb == null && L.car != null && L.car.handBrakeObj != null)
                {
                    hb = L.car.handBrakeObj.GetComponent<HandbrakeLogicC>();
                    if (hb == null) hb = L.car.handBrakeObj.GetComponentInChildren<HandbrakeLogicC>(true);
                }
                if (hb == null) hb = L.CabPart<HandbrakeLogicC>();
                if (hb != null && grip == null)
                {
                    grip = FindNear(hb.transform, "HandbrakeButton") ?? hb.transform;   // the lever's top (task 37 bug 3)
                    gripAim = VisibleCentre(grip);
                }
                return hb != null;
            }

            /// <summary>Why she can't pull it now (null = she can): the game's own Trigger refusals plus ours.</summary>
            string Blocked()
            {
                if (L.car == null) return "no car";
                if (!Find()) return "no handbrake found";
                if (hb.currentPos != 1) return "handbrake is up";
                if (!L.PlayerSeated()) return "player out";
                if (!L.car.engineOn) return "engine off";
                if (L.car.preventHandBrake) return "handbrake locked by the game";
                var u = L.logic;
                if (L.directorC == null) L.IsDay();
                if (L.directorC != null && L.directorC.isSatAtBorder) return "border";
                if (u != null && (u.withinRangeOfMotel || u.hasSaidLetsCheckIn)) return "motel";
                if (u != null && (u.engineRepairTutorialState == 3 || u.engineRepairTutorialState == 8 || u.fuelTutorialState == 1)) return "tutorial";
                float slope = Slope();
                if (slope > 6f) return "slope " + slope.ToString("F1") + " deg";
                Vector3 p = L.car.transform.position;
                foreach (var c in L.aiCars)
                    if (c != null && c.gameObject.activeInHierarchy && (c.transform.position - p).sqrMagnitude < 120f * 120f) return "traffic near";
                return null;
            }

            /// <summary>Bug 2 (v0.41.2): car.transform.up gave "slope" on flat ground (that object's axes are not the car's).
            /// Now: the car body's axis closest to world up (any of up/forward/right, either sign), its angle to world up.</summary>
            float Slope()
            {
                Transform t = L.carCtl != null ? L.carCtl.transform : L.car.transform;
                float best = 0f;
                foreach (var a in new[] { t.up, t.forward, t.right }) best = Mathf.Max(best, Mathf.Abs(Vector3.Dot(a, Vector3.up)));
                return Mathf.Acos(Mathf.Clamp01(best)) * Mathf.Rad2Deg;
            }

            public override string Can()
            {
                if (!throttle) return "waits for a stop and the throttle";
                if (L.SpeedKmh() >= MaxKmh) return "moving";
                return Blocked();
            }

            public override string DebugStart()
            {
                if (L.car == null || !Find()) { PLog("N4 skip no handbrake (debug)"); return "N4 skip: no handbrake found"; }
                debugUntil = Time.time + 180f;
                PLog("N4 armed (debug): waits for a stop, the handbrake released and the throttle");
                return "N4 armed: stop, release the handbrake, press gas (3 min)";
            }

            /// <summary>Watches his release and the throttle; starts the prank itself (the scheduler only runs while driving).</summary>
            public override void Tick(float now, bool inCar, bool idle)
            {
                if (L.car == null || !Find()) return;
                int pos = hb.currentPos;
                bool stopped = L.SpeedKmh() < 1f;
                bool seated = inCar && L.PlayerSeated();
                // he released it at a standstill: arm if a prank is due soon (not in the first minutes after she boarded)
                if (lastPos == 0 && pos == 1 && seated && stopped && L.pk == null)
                {
                    bool due = PranksOn && PrankMinutes > 0.01f && L.pFirstLeft <= 0f && L.lastPrank != Id
                        && L.pClock >= 0f && L.pClock < 0.5f * PrankMinutes * 60f;
                    if (due && Random.value < 0.6f) { armedUntil = now + 20f; PLog("N4 armed: handbrake released at a standstill"); }
                }
                lastPos = pos;
                bool debug = now < debugUntil;
                if (!debug && now >= armedUntil) { lastWait = null; return; }
                // bug 1 (v0.41.0, user): armed in debug, but nothing more in the log. Every wait reason is logged now (once per
                // change), the throttle no longer ignores an open debug panel, and she may still pull it while it rolls < 8 km/h
                float kmh = L.SpeedKmh();
                string wait = pos != 1 ? "handbrake up" : !seated ? "player out" : kmh >= MaxKmh ? "moving" : !ThrottleDown() ? "no throttle yet"
                    : !idle ? "she is busy (talking, reacting or asleep)" : L.pState != PState.None ? "another prank under way" : null;
                if (wait != null)
                {
                    if (wait != lastWait)
                        PLog("N4 waiting: " + wait + " (handbrake pos " + pos + ", " + kmh.ToString("F0") + " km/h, seated " + seated + (debug ? ", debug" : "") + ")");
                    lastWait = wait;
                    if (!debug && wait == "moving") { armedUntil = -1f; lastWait = null; PLog("N4 disarmed: the car moved off before the throttle"); }
                    return;
                }
                lastWait = null;
                string block = L.PrankBlock(now, inCar, false, false, false);
                if (block == null) block = Blocked();
                if (block != null)
                {
                    // the debug arming stays (he may fix it and press again); the log says each new reason once
                    // bug 2: each blocked press is logged (v0.41.2 said a repeated reason only once)
                    if (block != lastSkip || now >= skipLogAt) PLog("N4 skip " + block + (debug ? " (debug, still armed)" : ""));
                    lastSkip = block;
                    skipLogAt = now + 3f;
                    if (!debug) armedUntil = -1f;
                    return;
                }
                lastSkip = null;
                throttle = true;
                string r = L.StartPrank(this, debug);
                throttle = false;
                armedUntil = debugUntil = -1f;
                if (r == null) L.pClock = L.NextPrankGap();
            }

            public override Beat Start(out string note)
            {
                note = "throttle at " + L.SpeedKmh().ToString("F1") + " km/h";
                return new Beat { Dur = 2.2f, Look = Look.Reach, LookW = 0.75f, Face = "focused", FaceW = 0.35f, ActAt = 0.25f };
            }

            public override void Arm(Beat b, float env)
            {
                if (grip != null) L.WantReachTo("handbrake", grip, gripAim, false, false, ReachCurve(b));
            }

            public override string Act(float now)
            {
                if (!Find()) return "no handbrake found";
                if (hb.currentPos != 1) return "already up";
                if (!L.car.engineOn) return "engine off";
                hb.Trigger();
                How = "handbrake pulled at " + L.SpeedKmh().ToString("F1") + " km/h";
                return null;
            }

            public override string Checked(float now)
            {
                return hb != null && hb.currentPos == 0 ? null : "handbrake did not go up";
            }

            public override string UndoneBy(float now)
            {
                return hb != null && hb.currentPos == 1 ? "handbrake released" : null;
            }
        }
    }
}
