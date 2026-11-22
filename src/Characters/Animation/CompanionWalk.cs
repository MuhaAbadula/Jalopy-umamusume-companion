using Pathfinding;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// v0.24 walk for v5 clips (her own Uma walk, recorded from UmaViewer):
    /// - the step phase follows the ground the uncle's root covers (clip ground per loop x her leg length),
    ///   so her feet don't slide; turning on the spot steps too;
    /// - starts on a fresh step, stops by finishing the step until both feet are down;
    /// - hips at her own standing height plus the clip's bob;
    /// - the standing foot is pinned where it landed, feet follow the ground height (steps, slopes), the hips drop
    ///   if a leg can't reach;
    /// - debug: "follow me" makes the uncle (and so her) walk after the player.
    /// Older clips (no ground-per-loop) keep the v0.18 ankle-crossing sync in UpdateWalk.
    /// </summary>
    public partial class CompanionDriver
    {
        class LegIK
        {
            public Transform Thigh, Knee, Ankle;
            public bool Locked;
            public Vector3 LockPos;
            public float LockW;    // 1 while pinned, eases out after lift-off
            public float Bias;     // height correction (along up) that puts the planted ankle on the floor
            public float Drift;    // pinned foot vs where the clip has it, in leg units (debug log)
        }

        LegIK legL, legR;
        Vector3 lastRootPos, lastRootFwd;
        bool haveRoot, walkStopping;
        float rootSpeed, yawRate, stopRate, stopFor, hipAbove = -1f, walkLegLen = 1f, walkRate;

        /// <summary>Per-frame walk log and the twitch detector (setting "PhysicsDebug"); off: walk confirmed 2026-11-01.</summary>
        public bool WalkDebug;
        bool movingState, lastWant;
        float walkStartAt = -10f;
        float slowFor;

        bool NewWalk { get { return gestures != null && gestures.WalkCycle > 0.01f; } }

        LegIK MakeLeg(string side)
        {
            Transform a, b, c;
            if (boneByName == null || !boneByName.TryGetValue("Thigh_" + side, out a) || !boneByName.TryGetValue("Knee_" + side, out b)
                || !boneByName.TryGetValue("Ankle_" + side, out c)) return null;
            return new LegIK { Thigh = a, Knee = b, Ankle = c };
        }

        float LegLength()
        {
            if (legL == null) return 1f;
            return (legL.Knee.position - legL.Thigh.position).magnitude + (legL.Ankle.position - legL.Knee.position).magnitude;
        }

        /// <summary>Phase and weight of the v5 walk; called from UpdateWalk.</summary>
        void UpdateWalk2(float dt, bool inCar)
        {
            if (legL == null) { legL = MakeLeg("L"); legR = MakeLeg("R"); }
            if (legL == null || legR == null || dt <= 0f) { walkW = 0f; return; }
            Vector3 up = transform.up;
            Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, up).normalized;
            Vector3 p = transform.position;
            walkLegLen = Mathf.Max(1e-3f, LegLength());
            if (!haveRoot) { lastRootPos = p; lastRootFwd = fwd; haveRoot = true; }
            float v = Vector3.ProjectOnPlane(p - lastRootPos, up).magnitude / dt;
            if (v > 30f * walkLegLen) v = 0f;                                  // teleported (scene change, car exit snap)
            // the path is recomputed every second or so and the speed dips for a moment: smooth it more
            // (user: a hitch every few steps)
            rootSpeed = Mathf.Lerp(rootSpeed, v, 1f - Mathf.Exp(-dt / 0.35f));
            ApplyPace();
            SmoothWalkFrame(dt);
            if (gestures.HasFastWalk)
            {
                // calm walk at its own speed, brisk walk at its own: in between, a mix (both in step, see Gestures)
                float vs = gestures.WalkNaturalSpeed(false), vf = gestures.WalkNaturalSpeed(true);
                float vlu = rootSpeed / Mathf.Max(1e-3f, LegUnit());
                float bt = vf > vs + 0.01f ? Mathf.Clamp01((vlu - vs) / (vf - vs)) : 0f;
                gestures.WalkBlend = Mathf.Lerp(gestures.WalkBlend, bt, 1f - Mathf.Exp(-dt / 1.0f));
            }
            float yaw = Vector3.SignedAngle(lastRootFwd, fwd, up) / dt;
            if (Mathf.Abs(yaw) > 720f) yaw = 0f;
            yawRate = Mathf.Lerp(yawRate, yaw, 1f - Mathf.Exp(-dt / 0.15f));
            lastRootPos = p;
            lastRootFwd = fwd;

            float ws = animator != null ? animator.GetFloat("walkSpeed") : 0f;
            bool allowed = OwnWalk && !inCar && seatBlend <= 0f && (life == null || life.Enabled) && bedW <= 0f && !InBed();
            // hysteresis: start above 0.15 legs/s, stop only after 0.35 s below 0.08 (a speed dip used to start the
            // stop, fade towards the stance and pick the walk up again: a hitch every few steps)
            bool fastNow = rootSpeed > 0.15f * walkLegLen && (ws > 0.1f || rootSpeed > 0.4f * walkLegLen);
            bool slowNow = rootSpeed < 0.08f * walkLegLen;
            if (fastNow) { movingState = true; slowFor = 0f; }
            else if (slowNow) { slowFor += dt; if (slowFor > 0.35f) movingState = false; }
            else slowFor = 0f;
            bool moving = movingState;
            bool turning = !moving && Mathf.Abs(yawRate) > 45f;
            bool want = allowed && (moving || turning);
            if (want != lastWant)
            {
                lastWant = want;
                if (want) walkStartAt = Time.time;
                Debug.Log("[Umamusume companion] walk: " + (want ? "start" : "stop") + " at " + Time.time.ToString("F2") + " s, speed "
                    + (rootSpeed / walkLegLen).ToString("F2") + " legs/s" + (turning ? " (turning)" : ""));
            }
            float cycle = gestures.WalkCycle * LegUnit();                      // ground per loop at her size

            float targetW = 0f;
            if (want)
            {
                float rate = moving ? rootSpeed / cycle
                    : Mathf.Abs(yawRate) * Mathf.Deg2Rad * walkLegLen * 0.35f / cycle;   // small steps while turning
                if (walkW <= 0.01f) walkPhase = gestures.WalkStartPhase;        // set off from a fresh step
                walkRate = rate;
                walkPhase = Mathf.Repeat(walkPhase + rate * dt, 1f);
                walkStopping = false;
                stopFor = 0f;
                targetW = moving ? 1f : 0.7f;
            }
            else if (walkW > 0f)
            {
                // stopping: finish the step until both feet are on the ground, then settle into the stance
                if (!walkStopping) { walkStopping = true; stopFor = 0f; stopRate = Mathf.Max(walkRate, 0.6f); }
                stopFor += dt;
                float cl, cr, dy;
                gestures.WalkContacts(walkPhase, out cl, out cr, out dy);
                bool bothDown = cl > 0.6f && cr > 0.6f;
                if (!bothDown && stopFor < 1.2f && allowed)
                {
                    walkPhase = Mathf.Repeat(walkPhase + stopRate * dt, 1f);
                    targetW = walkW;
                }
            }
            walkW = Mathf.MoveTowards(walkW, targetW, dt / (want ? 0.2f : 0.3f));
            if (walkW <= 0f) { walkStopping = false; ReleaseLegs(); }
        }

        // The Uma clips move the pelvis only about a centimetre (anime style); in Jalopy's camera that read as a
        // body sliding on a rail (user: wooden). Bob x2, sideways/forward sway x2.5.
        const float BobGain = 2f, SwayGain = 2.5f;

        // her facing while walking: the uncle's root turns in jumps (path corners); her body eases after it (0.15 s),
        // the pinned feet stay where they are
        Vector3 walkFwd;
        bool walkFwdSet;

        Quaternion WalkFrame()
        {
            Vector3 up = transform.up;
            Vector3 f = Vector3.ProjectOnPlane(transform.forward, up);
            if (f.sqrMagnitude < 1e-6f) return GroundFrame();
            f.Normalize();
            if (!walkFwdSet || walkW <= 0.01f) { walkFwd = f; walkFwdSet = true; }
            return Quaternion.LookRotation(walkFwd, up);
        }

        void SmoothWalkFrame(float dt)
        {
            Vector3 up = transform.up;
            Vector3 f = Vector3.ProjectOnPlane(transform.forward, up);
            if (f.sqrMagnitude < 1e-6f) return;
            f.Normalize();
            if (!walkFwdSet) { walkFwd = f; walkFwdSet = true; return; }
            walkFwd = Vector3.Slerp(walkFwd, f, 1f - Mathf.Exp(-dt / 0.15f));
            walkFwd = Vector3.ProjectOnPlane(walkFwd, up).normalized;
        }

        Vector3 lastSpringRoot;
        bool haveSpringRoot;

        /// <summary>After Life set the tail sway: while walking the tail swings side to side once per stride, and the
        /// hair/tail particles are carried along with her travel (outside the car only).</summary>
        void WalkSprings()
        {
            if (springs == null) return;
            Vector3 p = transform.position;
            Vector3 d = haveSpringRoot ? p - lastSpringRoot : Vector3.zero;
            lastSpringRoot = p;
            haveSpringRoot = true;
            bool outside = !InCar();
            if (!outside || d.magnitude > 2f * walkLegLen) d = Vector3.zero;   // car frame handles riding; teleports reset
            float w = NewWalk ? Mathf.SmoothStep(0f, 1f, walkW) : 0f;
            springs.RootDelta = d;
            // walking tail (user: "like a stick, twitching"): less carry so it trails and catches up, softer, a
            // wave from root to tip locked to her steps (+ a bounce per step and a slow wander, no pendulum)
            springs.CarryTail = outside ? Mathf.Lerp(0.4f, 0.6f, w) : 0f;
            springs.CarryOther = outside ? Mathf.Lerp(0.3f, 0.7f, w) : 0f;
            springs.TailStiffScale = Mathf.Lerp(1f, 0.5f, w);
            springs.TailWave = Mathf.Lerp(0.7f, 1.1f, w);
            if (w > 0.01f)
            {
                float wander = 0.75f + 0.5f * Mathf.PerlinNoise(Time.time * 0.4f, 3.1f);
                springs.TailAmp = Mathf.Lerp(springs.TailAmp, 6f * wander, w);
                springs.TailLift = Mathf.Lerp(springs.TailLift, 1.5f, w);
                springs.TailBounce = 4f * w;
                // the tail swings away from the standing leg: the step phase (0 = left foot lands) drives it
                springs.TailPhase = walkPhase * Mathf.PI * 2f + 0.4f * Mathf.Sin(Time.time * 0.7f);
            }
            else { springs.TailPhase = -1f; springs.TailBounce = 0f; }
        }

        void ReleaseLegs()
        {
            if (legL != null) { legL.Locked = false; legL.LockW = 0f; }
            if (legR != null) { legR.Locked = false; legR.LockW = 0f; }
        }

        // Heights are measured from the floor under the uncle (a ray), not from his transform: his pivot is not on
        // the floor, and v0.24.0 read every floor as far below the root -> clamped -0.35 leg -> sunken feet (user video).
        float rootGround, ankleRest = -1f;
        int rootGroundFrame = -1;
        float nextWalkLog, lastDrop;

        /// <summary>Floor height (along up) under the uncle this frame; his own position if no floor is found.</summary>
        float RootGround()
        {
            if (rootGroundFrame == Time.frameCount) return rootGround;
            rootGroundFrame = Time.frameCount;
            Vector3 up = transform.up;
            float h;
            rootGround = FloorAt(transform.position + up * walkLegLen, up, 3f * walkLegLen, out h) ? h : Vector3.Dot(transform.position, up);
            return rootGround;
        }

        /// <summary>First floor hit straight down from a point (skipping the uncle's own colliders); its height along up.</summary>
        bool FloorAt(Vector3 from, Vector3 up, float dist, out float height)
        {
            height = 0f;
            var hits = UnityEngine.Physics.RaycastAll(from, -up, dist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(transform)) continue;   // his capsule, the suitcase
                if (h.distance < best) { best = h.distance; height = Vector3.Dot(h.point, up); }
            }
            return best < float.MaxValue;
        }

        /// <summary>Her standing hip height above the floor: learned, else Uma proportions of thigh + shin.</summary>
        float StandHip() { return hipAbove > 0f ? hipAbove : 1.2f * walkLegLen; }

        /// <summary>The clip's length unit (its hip above its lowest ankle) at her size.</summary>
        float LegUnit()
        {
            float r = gestures != null ? gestures.WalkLegRatio : 0f;
            return r > 0f ? StandHip() * r : walkLegLen;
        }

        /// <summary>Learn her standing hip and ankle heights above the floor whenever she stands still outside.
        /// v0.24.1 waited for uncleStanding, which only the (disabled) standing clips update: nothing was learned.</summary>
        void LearnHipHeight(bool inCar)
        {
            if (hipLink == null || inCar || walkW > 0f || bedW > 0f || seatBlend > 0f || legL == null || InBed()) return;
            if (rootSpeed > 0.05f * walkLegLen || (animator != null && animator.GetFloat("walkSpeed") > 0.05f)) return;
            // task 31 bug 2: the G outing exit clip runs 4 s with the uncle already out of the car (no parent, enterCar
            // off): her stance was learned from that half-seated pose in the air (hip 2.55, ankles 0.92) and the next
            // walk floated. Learn only from a settled stance with the feet near the floor.
            if (animator != null && (animator.GetBool("exitCar") || animator.GetBool("enterCar") || animator.GetBool("giveItem")
                || animator.GetBool("bedTime"))) return;
            if (BodyScale != 1f && standW < 0.98f) return;
            Vector3 up = transform.up;
            float g = RootGround();
            float h = Vector3.Dot(hipLink.Girl.position, up) - g;
            float a = Mathf.Min(Vector3.Dot(legL.Ankle.position, up), Vector3.Dot(legR.Ankle.position, up)) - g;
            if (h <= 0f || a > 0.4f * walkLegLen) return;
            hipAbove = hipAbove < 0f ? h : Mathf.Lerp(hipAbove, h, 0.05f);
            ankleRest = ankleRest < 0f ? a : Mathf.Lerp(ankleRest, a, 0.05f);
        }

        /// <summary>After the walk clip is applied: hip height, pinned feet, ground height, IK.</summary>
        void WalkIK(float dt)
        {
            if (!NewWalk || walkW <= 0.001f || legL == null || legR == null || hipLink == null) return;
            float w = Mathf.SmoothStep(0f, 1f, walkW);
            Vector3 up = transform.up;
            float cl, cr, hipDy;
            gestures.WalkContacts(walkPhase, out cl, out cr, out hipDy);
            float leg = walkLegLen;
            float g = RootGround();

            // hips: her standing height above the floor + the clip's bob (the uncle's own hip bob is a man's gait)
            // her standing hip x the clip's walking/standing ratio (the clip's own lower walking hip), + its bob
            float H = StandHip();
            leg = LegUnit();
            float hipH = H * (gestures.WalkHipRatio > 0f ? gestures.WalkHipRatio : 0.97f);
            {
                // sideways/forward the hips came from the uncle's own walk (his gait sway and the hitch at his clip's
                // loop: user, "a twitch for a split second"): keep only its slow average offset from his root
                Vector3 off = Vector3.ProjectOnPlane(hipLink.Girl.position - transform.position, up);
                if (!hipOffSet || walkW <= 0.01f) { hipOffAvg = off; hipOffSet = true; }
                hipOffAvg = Vector3.Lerp(hipOffAvg, off, 1f - Mathf.Exp(-dt / 1.5f));
                hipLink.Girl.position += (hipOffAvg - off) * w;
            }
            {
                float cur = Vector3.Dot(hipLink.Girl.position, up) - g;
                hipLink.Girl.position += up * ((hipH + hipDy * BobGain * leg - cur) * w);
            }

            // her weight shifts onto the standing foot: the clip's sideways and forward hip sway (feet stay pinned)
            float sr, sf;
            gestures.WalkHipSway(walkPhase, out sr, out sf);
            if (sr != 0f || sf != 0f)
            {
                Quaternion gf = WalkFrame();
                hipLink.Girl.position += (gf * Vector3.right * sr * SwayGain + gf * Vector3.forward * sf * SwayGain) * leg * w;
            }

            UpdateSteps(cl, cr, w);
            Vector3 tL = FootTarget(legL, cl, dt, up, leg, g), tR = FootTarget(legR, cr, dt, up, leg, g);
            // a leg that can't reach its target pulls the hips down (stairs down, a dip)
            float drop = Mathf.Max(Overreach(legL, tL), Overreach(legR, tR));
            // no hip drop any more: the right stance is longer than the left in these clips, the leg overreached at
            // its end and pulled the hips down once per cycle (user: a jerk every two steps). An unreachable target
            // now just leaves the foot a little short (heel/toe roll), the IK clamps the leg straight.
            lastDrop = drop;
            Vector3 kneeFwd = WalkFrame() * Vector3.forward;
            SolveLeg(legL, tL, w, kneeFwd);
            SolveLeg(legR, tR, w, kneeFwd);
            if (WalkDebug && Time.time - walkStartAt < 4f)
                Debug.Log("[Umamusume companion] walkf " + Time.time.ToString("F3") + " ph " + walkPhase.ToString("F3") + " c " + cl.ToString("F2") + "/" + cr.ToString("F2")
                    + " pin " + legL.LockW.ToString("F2") + "/" + legR.LockW.ToString("F2") + " drift " + legL.Drift.ToString("F3") + "/" + legR.Drift.ToString("F3")
                    + " reachShort " + (drop / leg).ToString("F3") + " v " + (rootSpeed / walkLegLen).ToString("F2") + " mix " + gestures.WalkBlend.ToString("F2")
                    + " pace " + autoPace.ToString("F2") + " hipOff " + (hipOffAvg.magnitude / leg).ToString("F3"));

            if (Time.time >= nextWalkLog)
            {
                nextWalkLog = Time.time + 2f;
                Debug.Log("[Umamusume companion] walk: speed " + (rootSpeed / leg).ToString("F2") + " legs/s, phase rate " + walkRate.ToString("F2")
                    + "/s, root above floor " + (Vector3.Dot(transform.position, up) - g).ToString("F2") + ", hip above floor "
                    + (Vector3.Dot(hipLink.Girl.position, up) - g).ToString("F2") + " (learned " + hipAbove.ToString("F2") + "), ankles "
                    + (Vector3.Dot(legL.Ankle.position, up) - g).ToString("F2") + "/" + (Vector3.Dot(legR.Ankle.position, up) - g).ToString("F2")
                    + " (rest " + ankleRest.ToString("F2") + "), leg " + leg.ToString("F2") + ", hip drop " + lastDrop.ToString("F2")
                    + ", contacts " + cl.ToString("F2") + "/" + cr.ToString("F2") + ", brisk mix " + gestures.WalkBlend.ToString("F2")
                    + (BodyScale != 1f ? ", body_scale " + BodyScale.ToString("F3") + " hip/leg " + ((Vector3.Dot(hipLink.Girl.position, up) - g) / Mathf.Max(1e-3f, walkLegLen)).ToString("F2")
                       + " leg " + (walkLegLen / Mathf.Max(1e-6f, girlRoot.lossyScale.x)).ToString("F3") + " her u" : ""));
            }
        }

        static float Overreach(LegIK L, Vector3 target)
        {
            float reach = ((L.Knee.position - L.Thigh.position).magnitude + (L.Ankle.position - L.Knee.position).magnitude) * 0.995f;
            return Mathf.Max(0f, (target - L.Thigh.position).magnitude - reach);
        }

        /// <summary>
        /// Where this ankle should be. On the ground the ankle sits at her standing ankle height above the floor under
        /// it; that correction (Bias) is kept through the swing so the lifted foot moves on without a jump. Pinned where
        /// it landed while the clip says it stands.
        /// </summary>
        Vector3 FootTarget(LegIK L, float contact, float dt, Vector3 up, float leg, float g)
        {
            Vector3 ankle = L.Ankle.position;
            float floor;
            float off = FloorAt(ankle + up * (0.6f * leg), up, 1.6f * leg, out floor) ? Mathf.Clamp(floor - g, -0.35f * leg, 0.35f * leg) : 0f;
            float hFk = Vector3.Dot(ankle, up) - g;
            // only the flat-footed part of the stance (contact 1 = ankle within 3 cm of its lowest in the clip):
            // v0.24.2 pinned and pressed the ankle to the floor through the heel strike and toe roll too, where the
            // clip lifts it; the leg came up short, the hips dropped and both knees bent (user: knees bend too much)
            bool down = contact > 0.98f;
            float rest = ankleRest >= 0f ? ankleRest : Mathf.Max(0.05f * leg, StandHip() - leg);
            if (down)
                L.Bias = Mathf.Lerp(L.Bias, rest + off - hFk, 1f - Mathf.Exp(-dt / 0.15f));
            Vector3 free = ankle + up * L.Bias;
            // the pin starts at the heel strike and its weight follows the contact through the roll in and out
            // (v0.25.1 pinned only the flat part and let go in one go: a jerk once per cycle, user)
            // pin from the moment the foot is really down (contact 0.98: in 0.25.2-0.25.3 it started at 0.5, while the
            // foot was still swinging forward before the heel strike, and the drift shot up to the limit), right where
            // it is (no jump); it lets go gradually through the toe roll (weight follows contact 1 -> 0.5)
            if (contact >= 0.98f && !L.Locked) { L.Locked = true; L.LockPos = free; }
            if (contact <= 0.5f) L.Locked = false;
            if (L.Locked)
            {
                L.LockPos = L.LockPos - up * Vector3.Dot(L.LockPos - free, up);  // pinned in the floor plane only
                // soft pin: the two Uma feet slide back at slightly different speeds (asymmetric gait), so one phase
                // rate can't hold both; a hard pin built up to the 0.2-leg limit on the right foot and let go with a
                // jerk (user). The pin follows the clip's foot with a 0.25 s lag: a hint of slide, no snap.
                L.LockPos = Vector3.Lerp(L.LockPos, free, 1f - Mathf.Exp(-dt / 0.25f));
                // drifting too far from where the clip has the foot (turns, speed changes): let the pin slide
                // along instead of letting go at once (that let-go was a visible jerk)
                Vector3 drift = L.LockPos - free;
                float max = 0.2f * leg;
                if (drift.magnitude > max) L.LockPos = free + drift.normalized * max;
            }
            L.LockW = L.Locked ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1f, contact)) : 0f;
            L.Drift = L.Locked ? (L.LockPos - free).magnitude / leg : 0f;
            return Vector3.Lerp(free, L.LockPos, L.LockW);
        }

        /// <summary>Two-bone IK on thigh/knee towards the target; the knee keeps bending where it bends now; the ankle
        /// keeps the clip's world rotation.</summary>
        static void SolveLeg(LegIK L, Vector3 target, float w, Vector3 fwd)
        {
            if (w <= 0.001f) return;
            Quaternion ankleRot = L.Ankle.rotation;
            Vector3 A = L.Thigh.position, B = L.Knee.position, C = L.Ankle.position;
            float l1 = (B - A).magnitude, l2 = (C - B).magnitude;
            if (l1 < 1e-5f || l2 < 1e-5f) return;
            Vector3 d = target - A;
            float dist = Mathf.Clamp(d.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
            Vector3 dn = d.sqrMagnitude > 1e-10f ? d.normalized : (C - A).normalized;
            // which way the knee bends: the clip's own bend plus a small constant pull forward. With the leg almost
            // straight (the heel strike) the clip's bend is tiny and its direction flickered for a frame (twitch log:
            // knee and ankle jolts right at each landing)
            Vector3 pole = Vector3.ProjectOnPlane(B - (A + C) * 0.5f, dn)
                + Vector3.ProjectOnPlane(fwd, dn).normalized * (0.08f * l1);
            if (pole.sqrMagnitude < 1e-8f) pole = Vector3.ProjectOnPlane(L.Thigh.forward, dn);
            pole.Normalize();
            float cosA = Mathf.Clamp((l1 * l1 + dist * dist - l2 * l2) / (2f * l1 * dist), -1f, 1f);
            Vector3 E = A + dn * (l1 * cosA) + pole * (l1 * Mathf.Sqrt(1f - cosA * cosA));
            Quaternion thighOrig = L.Thigh.rotation, kneeOrig = L.Knee.rotation;
            L.Thigh.rotation = Quaternion.FromToRotation(B - A, E - A) * thighOrig;
            Vector3 B2 = L.Knee.position, C2 = L.Ankle.position;
            Quaternion kneeSolved = Quaternion.FromToRotation(C2 - B2, A + dn * dist - B2) * L.Knee.rotation;
            Quaternion thighSolved = L.Thigh.rotation;
            L.Thigh.rotation = Quaternion.Slerp(thighOrig, thighSolved, w);
            L.Knee.rotation = Quaternion.Slerp(kneeOrig, kneeSolved, w);
            L.Ankle.rotation = ankleRot;
        }

        // ------------------------------------------------------------ her pace: the uncle's path speed x WalkPace
        // The uncle strides like a grown man: at his speed her clip ran 1.75x its own tempo and her legs minced
        // (user). Slower path speed (default 0.8) + the brisk clip blended in keep her steps near their natural tempo.
        public float WalkPace = 0f;   // 0 = her own Uma pace (auto)
        float baseAiSpeed = -1f;

        // WalkPace <= 0: her own Uma pace. The path speed is steered so she walks at the clip's natural speed
        // (ground per loop / loop length, in her leg units), learned while she walks steadily.
        float autoPace = 0.35f;
        Vector3 hipOffAvg;
        bool hipOffSet;

        void ApplyPace()
        {
            var ai = GetComponent<AIPath>();
            if (ai == null) return;
            if (baseAiSpeed < 0f) baseAiSpeed = ai.speed;
            float pace;
            if (WalkPace > 0.01f) pace = Mathf.Clamp(WalkPace, 0.2f, 1f);
            else
            {
                float nat = gestures != null ? gestures.WalkNaturalSpeed(false) : 0f;
                if (nat > 0f && movingState && walkW > 0.5f && rootSpeed > 0.05f * walkLegLen)
                {
                    float target = nat * LegUnit();
                    autoPace *= Mathf.Pow(target / rootSpeed, Mathf.Min(Time.deltaTime, 0.1f) / 1.5f);
                    autoPace = Mathf.Clamp(autoPace, 0.15f, 1f);
                }
                pace = autoPace;
            }
            float want = baseAiSpeed * pace;
            if (Mathf.Abs(ai.speed - want) > 1e-3f) ai.speed = want;
        }

        // ------------------------------------------------------------ debug: the uncle walks after the player
        Transform followT;
        bool following;
        Transform prevTarget;
        bool prevCanMove;

        public string ToggleFollow()
        {
            var ai = GetComponent<AIPath>();
            if (ai == null) return "no AIPath on the uncle";
            if (following)
            {
                ai.target = prevTarget;
                ai.canMove = prevCanMove;
                following = false;
                return "Follow OFF: she goes back to what the game wanted.";
            }
            if (InCar()) return "Follow: she is in the car, get her out first.";
            prevTarget = ai.target;
            prevCanMove = ai.canMove;
            if (followT == null) followT = new GameObject("FukuFollowTarget").transform;
            UpdateFollow();
            ai.target = followT;
            ai.canMove = true;
            following = true;
            return "Follow ON: she walks after you (same key to stop).";
        }

        void UpdateFollow()
        {
            if (!following && followT == null) return;
            if (following && InCar())
            {
                var ai0 = GetComponent<AIPath>();
                if (ai0 != null) { ai0.target = prevTarget; ai0.canMove = prevCanMove; }
                following = false;
                return;
            }
            var cam = Camera.main;
            if (cam == null || followT == null) return;
            Vector3 up = transform.up;
            Vector3 me = cam.transform.position;
            RaycastHit hit;
            if (UnityEngine.Physics.Raycast(me, -up, out hit, 5f, ~0, QueryTriggerInteraction.Ignore)) me = hit.point;
            Vector3 toHer = Vector3.ProjectOnPlane(transform.position - me, up);
            float keep = 1.5f * walkLegLen;                                    // stop a little in front of the player
            followT.position = me + (toHer.sqrMagnitude > 1e-4f ? toHer.normalized * keep : Vector3.zero);
        }
    }
}
