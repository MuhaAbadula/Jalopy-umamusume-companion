using System;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 87 (concept 13 rev 2, part B): Tachyon's additive for the Laika. About every AddonMinutes of driving she silently
    /// decides on an experiment (armed, no line). At the next stop (car still, engine off, 2.5 s; the player may stay seated, user 2026-11-19)
    /// she gets out by herself wherever it is (night, rain, town; not at the border post or the motel), walks round to the
    /// bonnet (UncleLogicC.bonnetNode), pops and opens it (BonnetLogicC.PopBonnet / Trigger) unless it is open, pours the vial
    /// into the fuel lid (installedFuelTank.fuelLid, recon 87) with a glowing stream, closes what she opened and boards.
    /// The player starting the engine before she is done = addon_undone, nothing poured.
    /// The engine started after a pour starts the variant until the motel or 15 min of driving:
    /// A1 top speed +15 %, engine wear x2; A2 fuel use -30 %, top speed -10 %; A3 smoke 30-60 s, top speed -20 %.
    /// Top speed / fuel via postfixes on CarPerformanceC.UpdatePerformanceTopSpeed / UpdateFuelEconomy.
    /// Log: [Umamusume companion] Exp: A? armed|go|walk|pour|undone|start|end.
    /// </summary>
    public partial class CompanionDriver
    {
        enum PourPh { Walk, Pop, Open, Pour, Back, Close }

        bool addonArmed, pouring, engineWas, bonnetByHer, bonnetWasOpen;
        PourPh pourPh;
        float addClock = -1f, addWait, phaseT, pourStalled, addonDriveT, smokeUntil = -1f, recordBase;
        int addonPending, addonActive, pourLeg, debugAddon;
        float pourSide, pourDetours, pourW;
        Vector3 pourParked, pourLastPos;
        bool addonPushSaid, addonRecordSaid;
        GameObject smoke;
        LineRenderer stream;
        BonnetLogicC bonnetLogic;

        static readonly string[] AddonNames = { "", "boost", "economy", "fail" };

        /// <summary>Read by the CarPerformanceC postfixes.</summary>
        public static float AddonSpeedMult = 1f, AddonFuelMult = 1f;

        CarLogicC CarL(UncleLogicC u) { return u != null && u.carLogic != null ? u.carLogic.GetComponent<CarLogicC>() : null; }

        BonnetLogicC Bonnet(UncleLogicC u)
        {
            if (bonnetLogic == null && u != null && u.carLogic != null) bonnetLogic = u.carLogic.transform.root.GetComponentInChildren<BonnetLogicC>(true);
            return bonnetLogic;
        }

        // ------------------------------------------------------------ per frame
        void AddonStep(UncleLogicC u, float dt, float now)
        {
            var car = CarL(u);
            if (car == null) return;
            bool here = ExpHere && ExpOn;

            // silent timer, driving only
            if (here && AddonMinutes > 0.01f && !addonArmed && addonPending == 0 && addonActive == 0 && !pouring)
            {
                if (addClock < 0f) addClock = ExpGap(AddonMinutes);
                if (InCar() && PlayerSat() && CarSpeed() > 15f) addClock -= dt;
                if (addClock <= 0f) { addonArmed = true; addClock = -1f; ExpFx.Log("additive armed (silent)"); }
            }

            // the next stop
            if (addonArmed && !pouring && outState == Out.InCar && offer == Offer.None)
            {
                string block = AddonBlock(u, car);
                // v0.52.6 (user: armed, stopped, she did not get out, no trace): say why, once per reason, while the car stands
                if (block != null && block != "moving" && block != addBlockSaid && now - addBlockAt > 3f)
                {
                    addBlockSaid = block;
                    addBlockAt = now;
                    ExpFx.Log("additive armed, waiting: " + block + " (speed " + CarSpeed().ToString("F1") + ")");
                }
                if (block != null) addWait = 0f;
                else if ((addWait += dt) >= 2.5f) StartPour(u, car, false);
            }

            // the effect: starts with the engine
            bool eng = car.engineOn;
            if (addonPending > 0 && eng && !engineWas) ActivateAddon(u, car);
            engineWas = eng;
            RestoreCcRadius(u);
            if (addonActive > 0) AddonActiveStep(u, car, dt, now);
        }

        /// <summary>Why she does not go and pour now (null = go).</summary>
        string AddonBlock(UncleLogicC u, CarLogicC car)
        {
            if (!InCar()) return "not in her seat";
            if (fetching) return "fetching";
            if (CarSpeed() > 2f) return "moving";   // 0.5 caught the wheel-rpm jitter of a parked car with him in it
            if (car.engineOn) return "engine on";
            // user 2026-11-19: she goes even if you stay in the car (stopped, engine off is enough)
            if (u.uncleAtHome || u.uncleGoneForever) return "not on a trip";
            if (u.restrictTalk) return "border";
            if (u.withinRangeOfMotel || u.hasSaidLetsCheckIn) return "motel";
            if (animator != null && animator.GetBool("bedTime")) return "bed";
            if (Doze.Gate) { Doze.OnG(); return "waking up"; }   // "in any case": the stop wakes her
            if (u.readyToBoard) return "boarding";
            if (Bonnet(u) == null) return "no bonnet";
            if (!DoorClear(u)) return "door blocked";
            return null;
        }

        void StartPour(UncleLogicC u, CarLogicC car, bool debug)
        {
            vialColor = PotionColors[Random.Range(0, PotionColors.Length)];
            MakeVial();
            vial.SetActive(false);   // out of her pocket at the bonnet
            pouring = true;
            pourPh = PourPh.Walk;
            phaseT = pourStalled = 0f;
            pourLeg = 0;
            pourSide = pourDetours = 0f;
            pourW = 0f;
            pourStepT = pourFastT = 0f;
            pourPoseLogged = pourPlanned = false;
            addBlockSaid = null;
            bonnetByHer = false;
            pourParked = u.uncleCarTransform.position;
            pourLastPos = transform.position;
            ExpSay("addon_go");
            ExpFx.Log("additive: she gets out to pour" + (debug ? " (debug)" : ""));
            StartCoroutine(ExitCar(u, null));
        }

        Vector3 StandPoint(UncleLogicC u, CarLogicC car, out Vector3 front)
        {
            Vector3 passenger = u.uncleCarTransform.position;
            Vector3 driver = car.playerSeat != null ? car.playerSeat.transform.position : passenger;
            Vector3 middle = (driver + passenger) * 0.5f;
            var bl = Bonnet(u);
            Vector3 bon = bl != null ? bl.transform.parent.position : middle + Flat(car.transform.forward) * 2f * walkLegLen;
            front = Flat(bon - middle);
            front = front.sqrMagnitude > 1e-6f ? front.normalized : Flat(car.transform.forward).normalized;
            if (u.bonnetNode != null) return u.bonnetNode.position;
            return bon + front * (1.2f * walkLegLen);
        }

        // v0.52.6 (user: too far from the engine, too much turning on the way): she stands straight out in front of the fuel
        // lid, right at the car's nose (found by rays against the car's own colliders), not at the game's bonnetNode, which
        // stood 6 arms from the lid. The way there is two straight legs walked without the A* grid: along her side of the
        // body to the front corner, then across to the spot.
        bool pourPlanned;
        Vector3 pourStand, pourCorner, pourFront;

        void PlanPour(UncleLogicC u, CarLogicC car)
        {
            pourPlanned = true;
            StandPoint(u, car, out pourFront);
            Vector3 lid = LidPoint(u, car);
            var cc = GetComponent<CharacterController>();
            float feet = cc != null ? transform.position.y + cc.center.y - cc.height * 0.5f : transform.position.y;
            // v0.52.8, checked in Blender (blender/pour_preview.py, work/preview/pour87): the lid sits at the back of the engine
            // bay, 0.9 legs behind the nose, and her CharacterController is 0.53 legs wide, so even at the bumper the vial
            // stayed 0.4 m short. For the pour her collision radius shrinks to 0.2 legs (restored once she is clear of the car).
            if (cc != null && pourCcRad < 0f && cc.radius > 0.2f * walkLegLen)
            {
                pourCcRad = cc.radius;
                cc.radius = 0.2f * walkLegLen;
                if (cc.height < 2f * cc.radius) cc.height = 2f * cc.radius;
            }
            float rad = cc != null ? cc.radius : 0.3f * walkLegLen;
            Transform root = car.transform.root;
            float nose = BodyOut(lid, pourFront, feet, root);
            Vector3 lidFlat = new Vector3(lid.x, transform.position.y, lid.z);
            Vector3 across = Vector3.Cross(Vector3.up, pourFront);
            Vector3 passenger = u.uncleCarTransform.position;
            Vector3 driver = car.playerSeat != null ? car.playerSeat.transform.position : passenger;
            Vector3 middle = (driver + passenger) * 0.5f;
            float lat = Vector3.Dot(transform.position - middle, across);
            float side = lat >= 0f ? 1f : -1f;
            // v0.52.7 (user: still too far): the lid sits 1.2 legs behind the nose, so her own side of the body may be nearer
            float flank = BodyOut(lid, across * side, feet, root);
            bool fromSide = flank >= 0f && (nose < 0f || flank < nose - 0.1f * walkLegLen);
            if (nose < 0f && !fromSide)
            {
                Vector3 f;
                pourStand = u.bonnetNode != null ? u.bonnetNode.position : StandPoint(u, car, out f);
                ExpFx.Log("additive: no car body found round the lid, the game's bonnet spot");
            }
            else if (fromSide) pourStand = lidFlat + across * side * (flank + rad + 0.08f * walkLegLen);
            else pourStand = lidFlat + pourFront * (nose + rad + 0.08f * walkLegLen);
            // the corner: level with the spot, out at her side (at least where she is now, so the first leg runs along the body)
            float wide = Mathf.Max(Mathf.Abs(lat), Flat(passenger - driver).magnitude + 0.5f * walkLegLen) + 0.15f * walkLegLen;
            float standLat = Vector3.Dot(pourStand - middle, across);
            pourCorner = pourStand + across * (side * wide - standLat);
            // from the side, or already in front: one straight leg along the body
            pourLeg = fromSide || Vector3.Dot(transform.position - pourStand, pourFront) > -0.2f * walkLegLen ? 1 : 0;
            ExpFx.Log("additive: plan " + (fromSide ? "from her side" : "from the front") + ", flank " + (flank / walkLegLen).ToString("F2") + " legs");
            float arm = life != null ? life.ArmLength : 0f;
            ExpFx.Log("additive: plan, nose " + (nose / walkLegLen).ToString("F2") + " legs in front of the lid, stand "
                + (arm > 0f ? (Flat(pourStand - lid).magnitude / arm).ToString("F2") + " arm" : "?") + " from it, corner "
                + (Flat(pourCorner - transform.position).magnitude / walkLegLen).ToString("F1") + " legs away");
        }

        /// <summary>How far the car body reaches out from the lid along dir (world units, rays at knee to bonnet height); -1 none.</summary>
        float BodyOut(Vector3 lid, Vector3 dir, float feet, Transform root)
        {
            float reach = 6f * walkLegLen, best = -1f;
            foreach (float h in new[] { 0.45f, 0.7f, 0.95f, 1.2f })
            {
                Vector3 o = new Vector3(lid.x, feet + h * walkLegLen, lid.z) + dir * reach;
                foreach (var hit in UnityEngine.Physics.RaycastAll(o, -dir, reach, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.transform.root != root || hit.collider.transform.IsChildOf(transform)) continue;
                    best = Mathf.Max(best, Vector3.Dot(hit.point - lid, dir));
                }
            }
            return best;
        }

        float pourCcRad = -1f;

        /// <summary>Her own collision radius back once she is not pouring and clear of the car (or seated): restoring it
        /// while she still touches the bonnet would shove her.</summary>
        void RestoreCcRadius(UncleLogicC u)
        {
            if (pourCcRad < 0f || pouring) return;
            var cc = GetComponent<CharacterController>();
            if (cc == null) { pourCcRad = -1f; return; }
            if (!InCar() && Flat(transform.position - pourStand).magnitude < pourCcRad + 0.3f * walkLegLen) return;
            cc.radius = pourCcRad;
            ExpFx.Log("additive: her collision radius back to " + (pourCcRad / walkLegLen).ToString("F2") + " legs");
            pourCcRad = -1f;
        }

        Vector3 FrontWaypoint(Vector3 actor, out bool at)
        {
            at = false;
            if (pourLeg == 0 && Flat(pourCorner - actor).magnitude <= 0.4f * walkLegLen) { pourLeg = 1; pourStalled = 0f; }
            if (pourLeg == 0) return pourCorner;
            at = Flat(pourStand - actor).magnitude <= 0.25f * walkLegLen;
            return pourStand;
        }

        Vector3 LidPoint(UncleLogicC u, CarLogicC car)
        {
            var cp = car.GetComponent<CarPerformanceC>();
            var tank = cp != null && cp.installedFuelTank != null ? cp.installedFuelTank.GetComponent<EngineComponentC>() : null;
            if (tank != null && tank.fuelLid != null) return tank.fuelLid.transform.position;
            var bl = Bonnet(u);
            return bl != null ? bl.transform.parent.position : transform.position + transform.forward * walkLegLen;
        }

        void UpdatePour(UncleLogicC u, float dt)
        {
            var car = CarL(u);
            var bl = Bonnet(u);
            if (car == null || bl == null) { EndPour(u, "no car", false); return; }
            // v0.52.4: the wheel-rpm speed jumps over 1.5 when she brushes the car or the bonnet swings (user: "experiment
            // ruined" every time, nobody drove). Only a real drive-off counts: engine on with him at the wheel, the car well away
            // from where it stood, or a steady speed for a second.
            float moved = Flat(u.uncleCarTransform.position - pourParked).magnitude;
            float spd = CarSpeed();
            pourFastT = spd > 6f ? pourFastT + dt : 0f;
            string off = moved > 2.5f * walkLegLen ? "car moved " + (moved / walkLegLen).ToString("F1") + " legs"
                : pourFastT > 1f ? "speed " + spd.ToString("F1") + " for 1 s"
                : PlayerSat() && car.engineOn ? "engine started" : null;
            if (off != null)
            {
                EndPour(u, "the player drove on (" + off + ", " + pourPh + ")", true);
                return;
            }
            phaseT += dt;
            switch (pourPh)
            {
                case PourPh.Walk:
                {
                    bool at;
                    if (!pourPlanned) PlanPour(u, car);
                    direct = true;   // straight legs, no A* grid (it swung her round the car)
                    outT.position = FrontWaypoint(transform.position, out at);
                    // blocked on the last leg near the spot: she is as close as the car lets her
                    if (!at && pourLeg == 1 && pourStalled > 1.2f && Flat(pourStand - transform.position).magnitude < 1.2f * walkLegLen) at = true;
                    if (at)
                    {
                        outT.position = transform.position;
                        bonnetWasOpen = car.bonnetOpen;
                        pourPh = bonnetWasOpen ? PourPh.Pour : PourPh.Pop;
                        phaseT = 0f;
                        if (bonnetWasOpen) ExpSay("addon_engine");
                        float armL = life != null ? life.ArmLength : 0f;
                        ExpFx.Log("additive: at the bonnet (" + (bonnetWasOpen ? "open" : "closed") + "), lid "
                            + (armL > 0f ? (Flat(LidPoint(u, car) - transform.position).magnitude / armL).ToString("F2") + " arm" : "?") + " away");
                        break;
                    }
                    if (Flat(transform.position - pourLastPos).sqrMagnitude > 0.04f) { pourLastPos = transform.position; pourStalled = 0f; }
                    else if ((pourStalled += dt) > 2.5f && pourLeg == 0) { pourLeg = 1; pourStalled = 0f; ExpFx.Log("additive: stuck on the way to the corner, straight to the spot"); }
                    if (phaseT > 25f) { EndPour(u, "cannot get to the bonnet", false); return; }
                    break;
                }
                case PourPh.Pop:
                    FacePoint(LidPoint(u, car), dt);
                    if (phaseT < dt * 1.5f && !car.bonnetPopped) bl.PopBonnet();
                    if (phaseT > 2.0f) { pourPh = PourPh.Open; phaseT = 0f; }
                    break;
                case PourPh.Open:
                    FacePoint(LidPoint(u, car), dt);
                    if (phaseT >= 0.5f && !bonnetByHer && !car.bonnetOpen) { bl.StartCoroutine("Trigger"); bonnetByHer = true; }
                    if (phaseT > 3.0f) { pourPh = PourPh.Pour; phaseT = 0f; }
                    break;
                case PourPh.Pour:
                    FacePoint(LidPoint(u, car), dt);
                    if (vial != null && !vial.activeSelf) vial.SetActive(true);
                    if (phaseT >= 1.2f && phaseT < 7.2f) { if (stream == null || !stream.enabled) { ExpSay("addon_pour"); ExpFx.Log("additive: pouring"); } StreamOn(LidPoint(u, car)); }
                    if (phaseT >= 7.2f) { StopStream(); pourPh = PourPh.Back; phaseT = 0f; }
                    break;
                case PourPh.Back:
                    if (phaseT > 1.5f) { DropVial(); pourPh = PourPh.Close; phaseT = 0f; }
                    break;
                case PourPh.Close:
                    if (phaseT < dt * 1.5f && bonnetByHer && car.bonnetOpen) bl.StartCoroutine("Trigger");
                    if (phaseT > 2.0f) { DonePour(u); return; }
                    break;
            }
            if (pourPh != PourPh.Walk) { outT.position = transform.position; PourStepIn(u, car, dt); outT.position = transform.position; }
            Steer(u, dt);
        }

        void FacePoint(Vector3 p, float dt)
        {
            Vector3 d = Flat(p - transform.position);
            if (d.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), dt * 240f);
        }

        void DonePour(UncleLogicC u)
        {
            addonArmed = false;
            addonPending = debugAddon > 0 ? debugAddon : Random.Range(1, 4);
            debugAddon = 0;
            JournalAdd("A" + addonPending + " " + AddonNames[addonPending] + " (poured)", false);
            ExpFx.Log("additive A" + addonPending + " poured, starts with the engine");
            pouring = false;
            direct = false;   // the game walks her to the door on the grid again
            StartBoarding(u, "additive poured");
        }

        /// <summary>undone: the player cut it short (line, disarmed); else a quiet stop, still armed for the next stop.</summary>
        void EndPour(UncleLogicC u, string why, bool undone)
        {
            StopStream();
            DropVial();
            var car = CarL(u);
            var bl = Bonnet(u);
            if (bonnetByHer && car != null && car.bonnetOpen && bl != null) bl.StartCoroutine("Trigger");
            bonnetByHer = false;
            pouring = false;
            direct = false;
            if (undone) { addonArmed = false; ExpSay("addon_undone"); }
            ExpFx.Log("additive: " + (undone ? "undone, " : "stopped, ") + why);
            if (u != null && outState != Out.InCar) StartBoarding(u, "additive " + (undone ? "undone" : "stopped"));
        }

        float bendW, pourStepT, pourFastT, addBlockAt = -10f;
        string addBlockSaid;
        bool pourPoseLogged;

        /// <summary>
        /// Before the stream (Pop/Open/first second of Pour): if the lid is beyond what a bend and an arm reach, a short step in
        /// towards it (CharacterController, so the car body stops her). v0.52.3, user: she stood off to the side and only the
        /// stream reached the engine.
        /// </summary>
        void PourStepIn(UncleLogicC u, CarLogicC car, float dt)
        {
            if (life == null || pourStepT > 2.5f) return;
            bool early = pourPh == PourPh.Pop || pourPh == PourPh.Open || (pourPh == PourPh.Pour && phaseT < 1.0f);
            if (!early) return;
            var cc = GetComponent<CharacterController>();
            float arm = life.ArmLength;
            if (cc == null || !cc.enabled || arm <= 0f) return;
            Vector3 d = Flat(LidPoint(u, car) - transform.position);
            float far = d.magnitude, want = 1.15f * arm;
            if (far <= want) return;
            pourStepT += dt;
            Vector3 dirN = d / far;
            Vector3 before = transform.position;
            float step = Mathf.Min(far - want, 0.6f * walkLegLen * dt);
            cc.Move(dirN * step);
            // blocked = less than half the step went towards the lid (sliding along the body does not count)
            float gained = Vector3.Dot(Flat(transform.position - before), dirN);
            if (pourStepT >= 2.5f || (cc.collisionFlags & CollisionFlags.Sides) != 0 || gained < 0.5f * step)
            {
                pourStepT = 3f;   // blocked by the car or out of time: she bends further instead
                ExpFx.Log("additive: step in done, lid " + (Flat(LidPoint(u, car) - transform.position).magnitude / arm).ToString("F2") + " arm away");
            }
        }

        void PosePourHands(Vector3 sh, float arm, float dt)
        {
            bool want = pouring && (pourPh == PourPh.Open || pourPh == PourPh.Pour);
            pourW = Mathf.MoveTowards(pourW, want ? 1f : 0f, dt / 0.5f);
            // the bend: a little at the bonnet handle, all the way over the engine while pouring, up again on the way back
            float bendWant = !pouring ? 0f : pourPh == PourPh.Pour ? 1f : pourPh == PourPh.Open ? 0.35f : pourPh == PourPh.Pop ? 0.15f : 0f;
            bendW = Mathf.MoveTowards(bendW, bendWant, dt / 0.9f);
            if (!pouring && pourW <= 0f && bendW <= 0f) { pourPoseLogged = false; return; }
            var u = OutU();
            var car = CarL(u);
            if (car == null) return;
            Vector3 lid = LidPoint(u, car);
            float bend = life.BendTo(lid + Vector3.up * (0.3f * arm), 70f, Mathf.SmoothStep(0f, 1f, bendW));
            sh = life.ShoulderR;   // moved with the bend
            Vector3 fwd = Flat(lid - sh);
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : transform.forward;
            Vector3 target;
            if (pourPh == PourPh.Open)
            {
                var bl = Bonnet(u);
                target = bl != null && bl.handle != null ? bl.handle.transform.position : lid;
            }
            else target = lid + Vector3.up * (0.3f * arm) - fwd * (0.15f * arm);   // wrist just short of the lid: the vial is past the fingers
            Vector3 dir = target - sh;
            bool shortArm = dir.magnitude > 0.95f * arm;
            if (shortArm) target = sh + dir.normalized * 0.95f * arm;   // as close as it goes
            if (pourW > 0f) life.HandTo(target, Vector3.down + transform.right * 0.4f, fwd, pourW);
            PinCuffs(0f, pourW);
            if (!pourPoseLogged && pourPh == PourPh.Pour && bendW >= 0.99f)
            {
                pourPoseLogged = true;
                ExpFx.Log("additive pose: bend " + bend.ToString("0") + " deg, shoulder to lid " + ((lid - sh).magnitude / arm).ToString("F2")
                    + " arm, lid " + (Flat(lid - transform.position).magnitude / arm).ToString("F2") + " arm from her feet" + (shortArm ? ", arm short" : ""));
            }
            if (vial != null && vial.activeSelf)
            {
                // tilt: up over 0.6 s before the stream, held, back up over the first 0.6 s of Back
                float tiltK = 0f;
                if (pourPh == PourPh.Pour) tiltK = Mathf.SmoothStep(0f, 1f, (phaseT - 0.5f) / 0.6f);
                else if (pourPh == PourPh.Back) tiltK = 1f - Mathf.SmoothStep(0f, 1f, phaseT / 0.6f);
                // the open end (+Y of the cylinder) turns over the lid, past horizontal; held in the fingers, not the palm
                Vector3 pourDir = (fwd * 0.55f - Vector3.up * 0.85f).normalized;
                Vector3 vup = Vector3.Slerp(Vector3.up, pourDir, tiltK);
                vial.transform.rotation = Quaternion.FromToRotation(Vector3.up, vup);
                Vector3 cb;   // v0.52.5: turned about its bottom, held through the cuff
                vial.transform.position = CuffBase(false, arm, out cb) ? cb + vup * (0.5f * vialH) : life.TipR + vup * (0.15f * vialH);
            }
        }

        void StreamOn(Vector3 lid)
        {
            if (vial == null) return;
            if (stream == null)
            {
                var go = new GameObject("FukuVialStream");
                stream = go.AddComponent<LineRenderer>();
                stream.positionCount = 2;
                stream.useWorldSpace = true;
                stream.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var sh = Shader.Find("Sprites/Default");
                if (sh != null) stream.material = new Material(sh);
            }
            stream.enabled = true;
            stream.startWidth = 0.05f * vialH;
            stream.endWidth = 0.03f * vialH;
            Color c = vialColor;
            stream.startColor = c;
            c.a = 0.7f;
            stream.endColor = c;
            Vector3 tip = vial.transform.position + vial.transform.up * (0.5f * vialH);
            stream.SetPosition(0, tip);
            stream.SetPosition(1, lid);
        }

        void StopStream()
        {
            if (stream != null) stream.enabled = false;
        }

        // ------------------------------------------------------------ the effect on the car
        void ActivateAddon(UncleLogicC u, CarLogicC car)
        {
            addonActive = addonPending;
            addonPending = 0;
            addonDriveT = 0f;
            addonPushSaid = addonRecordSaid = false;
            var dir = u.director != null ? u.director.GetComponent<DirectorC>() : null;
            recordBase = dir != null ? dir.topSpeedStats : 0f;
            switch (addonActive)
            {
                case 1: AddonSpeedMult = 1.15f; AddonFuelMult = 1f; LuckWear.EngineExtra = 2f; break;
                case 2: AddonSpeedMult = 0.9f; AddonFuelMult = 1f / 0.7f; LuckWear.EngineExtra = 1f; break;
                case 3:
                    AddonSpeedMult = 0.8f; AddonFuelMult = 1f; LuckWear.EngineExtra = 1f;
                    smokeUntil = Time.time + Random.Range(30f, 60f);
                    Smoke(car, true);
                    ExpSay("addon_fail");
                    break;
            }
            CarPatches.Apply();
            Recompute(car);
            ExpFx.Log("A" + addonActive + " start " + AddonNames[addonActive] + ": speed x" + AddonSpeedMult.ToString("0.00") +
                      ", fuel economy x" + AddonFuelMult.ToString("0.00") + ", engine wear x" + LuckWear.EngineExtra.ToString("0"));
        }

        void AddonActiveStep(UncleLogicC u, CarLogicC car, float dt, float now)
        {
            if (CarSpeed() > 5f) addonDriveT += dt;
            if (addonActive == 1)
            {
                if (!addonPushSaid && addonDriveT > 20f) { addonPushSaid = true; ExpSay("addon_push"); }
                var dir = u.director != null ? u.director.GetComponent<DirectorC>() : null;
                if (!addonRecordSaid && dir != null && recordBase > 1f && dir.topSpeedStats > recordBase + 0.5f)
                {
                    addonRecordSaid = true;
                    ExpSay("addon_record");
                    ExpFx.Log("A1 record " + recordBase.ToString("0.0") + " -> " + dir.topSpeedStats.ToString("0.0"));
                }
            }
            string end = null;
            if (addonDriveT > 900f) end = "15 min of driving";
            else if (u.withinRangeOfMotel) end = "town";
            else if (addonActive == 3 && now > smokeUntil) end = "smoke over";
            if (end != null) EndAddon(car, end);
        }

        void EndAddon(CarLogicC car, string why)
        {
            if (addonActive == 0) return;
            ExpFx.Log("A" + addonActive + " end (" + why + ")");
            addonActive = 0;
            AddonSpeedMult = AddonFuelMult = 1f;
            LuckWear.EngineExtra = 1f;
            Smoke(car, false);
            if (car != null) Recompute(car);
        }

        static void Recompute(CarLogicC car)
        {
            var cp = car.GetComponent<CarPerformanceC>();
            if (cp == null) return;
            try { cp.UpdatePerformanceTopSpeed(); } catch (Exception e) { Debug.LogWarning("[Umamusume companion] Exp: top speed recompute: " + e.Message); }
            try { cp.UpdateFuelEconomy(); } catch (Exception e) { Debug.LogWarning("[Umamusume companion] Exp: fuel recompute: " + e.Message); }
        }

        void Smoke(CarLogicC car, bool on)
        {
            if (!on)
            {
                if (smoke != null) Destroy(smoke);
                smoke = null;
                return;
            }
            if (car == null || car.engineDamageParticles == null || car.engineDamageParticles.Length == 0 || car.engineDamageParticles[0] == null)
            {
                ExpFx.Log("A3: no engine smoke to copy");
                return;
            }
            var src = car.engineDamageParticles[0];
            smoke = Instantiate(src, src.transform.parent);
            smoke.name = "FukuAddonSmoke";
            smoke.transform.localPosition = src.transform.localPosition;
            smoke.transform.localRotation = src.transform.localRotation;
            smoke.SetActive(true);
            foreach (var ps in smoke.GetComponentsInChildren<ParticleSystem>(true)) ps.Play();
        }

        // ------------------------------------------------------------ debug
        public string DebugAddonArm()
        {
            debugAddon = debugAddon % 3 + 1;
            addonArmed = true;
            return "additive armed, A" + debugAddon + " " + AddonNames[debugAddon] + " after the pour; stop, engine off, get out";
        }

        public string DebugAddonNow()
        {
            var u = OutU();
            var car = CarL(u);
            if (car == null) return "no car";
            if (pouring) return "already pouring";
            if (debugAddon == 0) debugAddon = 1;
            string block = AddonBlock(u, car);
            if (block != null && block != "you sit") return "not now: " + block;
            StartPour(u, car, true);
            return "she goes to pour A" + debugAddon;
        }

        public string DebugAddonStop()
        {
            var u = OutU();
            if (pouring) EndPour(u, "debug", false);
            addonArmed = false;
            addonPending = 0;
            EndAddon(CarL(u), "debug");
            return "additive off";
        }

        public string DebugPotion()
        {
            debugFx = debugFx % ExpFx.Count + 1;
            if (vialColor == default(Color)) vialColor = PotionColors[0];
            string r = ExpFx.Start(debugFx, vialColor, ExpFx.DurationOf(debugFx));
            return r ?? ("effect " + ExpFx.Id(debugFx) + " " + ExpFx.Name(debugFx) + (ExpFx.Strong(debugFx) ? " (strong)" : ""));
        }

        public string DebugOffer()
        {
            var u = OutU();
            if (u == null) return "no uncle";
            if (offer != Offer.None) return "already offering";
            string block = OfferBlock(u);
            if (block != null) return "not now: " + block;
            StartOffer(u, true);
            return "she holds out a vial (click it within 15 s)";
        }

        public static string DebugStopFx()
        {
            if (!ExpFx.Active) return "no effect running";
            ExpFx.Stop(false, "debug");
            return "effect stopped";
        }

        public string ExpStatus()
        {
            return "Exp: " + (ExpHere ? "on" : "not this pack") + ", fx " + (ExpFx.Active ? ExpFx.Id(ExpFx.Current) : "-") +
                   ", potion in " + (potClock < 0f ? "-" : (potClock / 60f).ToString("0.0") + " min") +
                   ", additive " + (pouring ? "pouring " + pourPh : addonArmed ? "armed" : addonPending > 0 ? "A" + addonPending + " waits for the engine" :
                                    addonActive > 0 ? "A" + addonActive + " on" : addClock < 0f ? "-" : "in " + (addClock / 60f).ToString("0.0") + " min");
        }

        static class CarPatches
        {
            static bool done;

            public static void Apply()
            {
                if (done) return;
                done = true;
                try
                {
                    var h = new Harmony("osobl.jalopy.umamusumecompanion.expcar");
                    h.Patch(AccessTools.Method(typeof(CarPerformanceC), "UpdatePerformanceTopSpeed"), postfix: new HarmonyMethod(typeof(CarPatches), nameof(TopSpeedPost)));
                    h.Patch(AccessTools.Method(typeof(CarPerformanceC), "UpdateFuelEconomy"), postfix: new HarmonyMethod(typeof(CarPatches), nameof(FuelPost)));
                    ExpFx.Log("car patches on");
                }
                catch (Exception e) { Debug.LogWarning("[Umamusume companion] Exp: car patches failed: " + e.Message); }
            }

            static void TopSpeedPost(CarPerformanceC __instance)
            {
                if (Mathf.Approximately(AddonSpeedMult, 1f)) return;
                var l = __instance.GetComponent<CarLogicC>();
                if (l != null && l.isPushingCar) return;
                var t = __instance.transform.parent != null ? __instance.transform.parent.parent : null;
                var ccs = t != null ? t.GetComponent<CarControleScriptC>() : null;
                if (ccs != null) ccs.TopSpeed *= AddonSpeedMult;
            }

            static void FuelPost(CarPerformanceC __instance)
            {
                if (!Mathf.Approximately(AddonFuelMult, 1f)) __instance.actualFuelConsumptionRate *= AddonFuelMult;
            }
        }
    }
}
