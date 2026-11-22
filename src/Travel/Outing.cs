using System.Collections;
using Pathfinding;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// She gets out of the car at any stop, not only at the motel. The game already has a mid-road exit (the engine
    /// repair tutorial, UncleLogicC.HeadTowardsBonnet) and the walk back to the door (readyToBoard +
    /// WalkTowardsCarDoor -> UncleLogicC.Update opens the door and runs UncleEnterCar); this reuses both.
    /// Key "Outing": in the stopped car = she gets out and walks after you; outside = wait here / come along.
    /// She heads back to the door by herself when you sit down to drive or the car starts moving; if she can't
    /// make it (far away, no path, door gone, car driving off) she is put in her seat the way the game does it
    /// every morning (UncleEnterCar).
    /// </summary>
    public partial class FukuDriver
    {
        internal enum Out { InCar, Exiting, Follow, Wait, Boarding }
        internal Out OutingState => outState;
        Out outState = Out.InCar;
        Transform outT;
        bool outPlayerSat;
        float boardAt, movingFor, stillFor, outLeftSayAt = -1f;
        Vector3 boardLastPos;
        CarControleScriptC outCar;

        public bool Outside { get { return outState != Out.InCar; } }

        UncleLogicC OutU()
        {
            if (uncleLogic == null) uncleLogic = GetComponent<UncleLogicC>();
            return uncleLogic;
        }

        float CarSpeed()
        {
            if (outCar == null) outCar = FindObjectOfType<CarControleScriptC>();
            return outCar != null ? Mathf.Abs(outCar.currentSpeed) : 0f;
        }

        bool PlayerSat()
        {
            var u = OutU();
            var pc = u != null && u.player != null ? u.player.GetComponent<RigidbodyControllerC>() : null;
            return pc != null && pc.isSat;
        }

        void OutSay(string key)
        {
            var u = OutU();
            if (remarks == null || remarks.Count == 0 || u == null || !remarks.CanSay(u)) return;
            string text = remarks.Pick(key);
            if (text != null) StartCoroutine(remarks.Say(u, text));
        }

        static bool BubbleShown(UncleLogicC u)
        {
            var label = u.dialogueHolder != null ? u.dialogueHolder.GetComponent<UILabel>() : null;
            return label != null && !string.IsNullOrEmpty(label.text);
        }

        /// <summary>
        /// Room outside her door. The game's UncleCheckCanExitCar casts along world +X, which on the road often hits
        /// the car itself; this casts from her seat out through her side of the car and skips the car and her.
        /// </summary>
        bool DoorClear(UncleLogicC u)
        {
            if (u.carLogic == null || u.uncleCarTransform == null) return true;
            Transform car = u.carLogic.transform;
            Vector3 seat = u.uncleCarTransform.position;
            Vector3 side = car.right * Mathf.Sign(Vector3.Dot(seat - car.position, car.right));
            Vector3 from = seat + car.up * 0.6f;
            float reach = 1.4f;
            foreach (var h in UnityEngine.Physics.RaycastAll(from, side, reach, ~0, QueryTriggerInteraction.Ignore))
            {
                Transform t = h.collider.transform;
                if (t.IsChildOf(car.root) || t.IsChildOf(transform) || t.root == transform.root) continue;
                if (u.player != null && t.IsChildOf(u.player.transform)) continue;
                Debug.Log("[UncleSwap] outing: door blocked by " + t.name + " (root " + t.root.name + ") at " + h.distance.ToString("0.00"));
                return false;
            }
            return true;
        }

        /// <summary>Why she can't get out right now (null = she can).</summary>
        string ExitBlocked(UncleLogicC u)
        {
            if (u.uncleAtHome || u.uncleGoneForever) return "not on a trip with you";
            if (!u.isSat || !InCar()) return "not in the car";
            if (animator != null && animator.GetBool("bedTime")) return "asleep";
            if (Asleep()) return "asleep";
            if (u.restrictTalk) return "at the border";
            if (u.withinRangeOfMotel || u.hasSaidLetsCheckIn) return "at the motel (she gets out there anyway)";
            if (u.readyToBoard) return "boarding";
            if (!u.hasGivenEngineRepairTutorial && u.engineRepairTutorialState > 0 && u.engineRepairTutorialState != 11) return "repair tutorial";
            var ai = GetComponent<AIPath>();
            if (ai != null && ai.target != null && ai.target == u.bonnetNode) return "repair tutorial";
            if (BubbleShown(u)) return "talking";
            if (u.isTalking)   // the game's flag sticks sometimes (a cut-off line); only the bubble counts
                Debug.Log("[UncleSwap] outing: isTalking stuck (dialogue '" + u.dialogueConvoItem + "', interrupt '" +
                          u.interruptConvoItem + "', queue " + u.textQueue.Count + "), ignored");
            return null;
        }

        /// <summary>The "Outing" key.</summary>
        public string OutingKey()
        {
            if (MornHolding) return "She is waiting in the motel room.";
            var u = OutU();
            if (u == null) return "no uncle";
            var ai = GetComponent<AIPath>();
            if (fetching) return "Fetching a crate.";
            switch (outState)
            {
                case Out.InCar:
                {
                    string why = ExitBlocked(u);
                    if (why != null) return "Outing: not now, " + why + ".";
                    if (CarSpeed() > 2f) { OutSay("out_moving"); return "Outing: the car is moving."; }
                    if (!DoorClear(u))
                    {
                        u.StartCoroutine("DialogueCannotExitCar");
                        return "Outing: her door is blocked.";
                    }
                    StartCoroutine(ExitCar(u));
                    return "Outing: she gets out.";
                }
                case Out.Follow:
                    outState = Out.Wait;
                    if (outT != null) outT.position = transform.position;   // a null target lets her finish the old path
                    if (ai != null) ai.target = outT;
                    OutSay("out_wait");
                    return "Outing: she waits here.";
                case Out.Wait:
                    outState = Out.Follow;
                    OutFollowTarget();
                    if (ai != null) { ai.target = outT; ai.canMove = true; }
                    OutSay("out_follow");
                    return "Outing: she comes along.";
                default:
                    return "Outing: busy (" + outState + ").";
            }
        }

        // same steps as UncleLogicC.HeadTowardsBonnet / DebugExitCar, but she walks after the player
        IEnumerator ExitCar(UncleLogicC u, string line = "out_go")
        {
            outState = Out.Exiting;
            Debug.Log("[UncleSwap] outing: she gets out");
            var an = GetComponent<Animator>();
            var door = u.uncleDoorTrigger;
            transform.parent = null;
            an.SetBool("enterCar", false);
            an.SetBool("exitCar", true);
            u.readyToBoard = false;
            u.isSat = false;
            if (door != null)
            {
                door.GetComponent<DoorLogicC>().isBroken = false;
                door.GetComponent<DoorLogicC>().open = false;
                door.SendMessage("Trigger");
            }
            if (line != null) OutSay(line);   // task 87: the pour has its own line
            yield return new WaitForSeconds(4f);
            if (door != null) door.SendMessage("Trigger");
            an.SetBool("exitCar", false);
            RaycastHit hit;
            if (door != null && UnityEngine.Physics.Raycast(new Ray(door.transform.position, -Vector3.up), out hit) && hit.distance < 1f)
                transform.localPosition += new Vector3(0f, 1f - hit.distance, 0f);
            transform.localPosition += new Vector3(0f, 0.5f, 0f);
            GetComponent<CharacterController>().enabled = true;
            if (u.carNavBlocker != null)
            {
                u.carNavBlocker.SetActive(true);
                AstarPath.active.Scan();
                u.carNavBlocker.SetActive(false);
            }
            var ai = GetComponent<AIPath>();
            OutFollowTarget();
            if (ai != null) { ai.target = outT; ai.canMove = true; }
            outPlayerSat = PlayerSat();
            movingFor = 0f;
            outState = Out.Follow;
        }

        void OutFollowTarget()
        {
            if (outT == null) outT = new GameObject("FukuOutingTarget").transform;
            if (MornHolding) { outT.position = transform.position; return; }   // morning: she waits by the bed until you walk
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 up = transform.up;
            Vector3 me = cam.transform.position;
            RaycastHit hit;
            if (UnityEngine.Physics.Raycast(me, -up, out hit, 5f, ~0, QueryTriggerInteraction.Ignore)) me = hit.point;
            Vector3 toHer = Vector3.ProjectOnPlane(transform.position - me, up);
            float keep = 1.5f * walkLegLen;                                    // stop a little in front of the player
            outT.position = me + (toHer.sqrMagnitude > 1e-4f ? toHer.normalized * keep : Vector3.zero);
        }

        internal void StartBoarding(UncleLogicC u, string why)
        {
            Debug.Log("[UncleSwap] outing: back to the car (" + why + ")");
            outState = Out.Boarding;
            boardAt = Time.time;
            boardLastPos = transform.position;
            stillFor = 0f;
            var ai = GetComponent<AIPath>();
            if (ai != null) ai.canMove = true;
            u.readyToBoard = true;
            u.walkingTowardsDoor = true;    // the game never resets it; Update then steers relay node -> door
            u.WalkTowardsCarDoor();
            if (Vector3.Distance(transform.position, u.uncleCarTransform.position) > 4f * walkLegLen) OutSay("out_call");
        }

        // ------------------------------------------------------------ walking without the path grid
        // The game's A* grid does not cover every stop on the road: there AIPath finds no path and she stands still.
        // Then she is steered straight at the AIPath target (player / relay node / door) with the CharacterController.
        // Task 27: the car carries its own nav nodes (relay node etc., rescanned in ExitCar), so right after she gets out
        // she is "on the graph" even on a motorway. A* then walks her to the node nearest to you (by the car) and she stops.
        // The old per-frame stuck check never fired: every repath (0.5 s) set pathPending and reset it.
        bool direct;
        float navCheckAt, directSince, shortFor, stuckSince, followLogAt = -1f;
        Vector3 stuckPos, followLogPos;
        string followLast;

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>Distance from a point to the nearest A* node, in leg lengths (infinity = no graph / no node).</summary>
        float NodeDist(Vector3 p)
        {
            if (AstarPath.active == null) return float.PositiveInfinity;
            var nn = AstarPath.active.GetNearest(p);
            return nn.node == null ? float.PositiveInfinity : (nn.position - p).magnitude / walkLegLen;
        }

        bool OffGraph() { return NodeDist(transform.position) > 1.5f; }

        /// <summary>What stands in her way straight ahead (direct mode), null = clear.</summary>
        string SteerBlock(Vector3 dir)
        {
            var u = OutU();
            foreach (float h in new[] { 0.4f, 1.1f })
            {
                Vector3 from = transform.position + Vector3.up * h * walkLegLen;
                RaycastHit best = new RaycastHit();
                bool any = false;
                foreach (var hit in UnityEngine.Physics.RaycastAll(from, dir, 1.2f * walkLegLen, ~0, QueryTriggerInteraction.Ignore))
                {
                    Transform t = hit.collider.transform;
                    if (t.IsChildOf(transform) || t.root == transform.root) continue;
                    if (u != null && u.player != null && t.IsChildOf(u.player.transform)) continue;
                    if (!any || hit.distance < best.distance) { best = hit; any = true; }
                }
                if (any)
                    return best.collider.name + " (root " + best.collider.transform.root.name + ", layer " +
                           LayerMask.LayerToName(best.collider.gameObject.layer) + ") at " + (best.distance / walkLegLen).ToString("0.0") +
                           " legs, " + (h < 1f ? "knee" : "chest") + " height";
            }
            return null;
        }

        void FollowLog(UncleLogicC u, AIPath ai, float far, string change)
        {
            if (change == null && Time.time < followLogAt) return;
            float span = followLogAt > 0f ? Time.time - followLogAt + 1f : 0f;
            float spd = span > 0.05f ? Flat(transform.position - followLogPos).magnitude / span / walkLegLen : 0f;
            followLogAt = Time.time + 1f;
            followLogPos = transform.position;
            Transform tgt = ai.target;
            string what = tgt == null ? "none" : tgt == outT ? "you" : tgt == u.relayNodeTransform ? "relay node" :
                          tgt == u.uncleCarTransform ? "her seat" : tgt.name;
            var sb = new System.Text.StringBuilder("[UncleSwap] outing: follow " + outState + ", mode " + (direct ? "direct" : "A*") +
                ", target " + what + " " + (far / walkLegLen).ToString("0.0") + " legs, speed " + spd.ToString("0.00") + " legs/s");
            if (direct)
            {
                Vector3 d = tgt != null ? Flat(tgt.position - transform.position) : Vector3.zero;
                string blk = d.sqrMagnitude > 1e-4f ? SteerBlock(d.normalized) : null;
                sb.Append(", ahead " + (blk ?? "clear"));
            }
            else
            {
                sb.Append(", path " + (ai.hasPath ? "yes" : "no") + (ai.pathPending ? " pending" : "") + (ai.reachedEndOfPath ? " at its end" : "") +
                          ", left " + (ai.hasPath ? (ai.remainingDistance / walkLegLen).ToString("0.0") : "-") + " legs, canMove " + ai.canMove);
            }
            sb.Append(", nearest node: her " + NodeDist(transform.position).ToString("0.0") +
                      (tgt != null ? " / target " + NodeDist(tgt.position).ToString("0.0") : "") + " legs");
            if (change != null) sb.Append(" [" + change + "]");
            Debug.Log(sb.ToString());
        }

        void GoDirect(AIPath ai, string why)
        {
            direct = true;
            directSince = Time.time;
            var sb = new System.Text.StringBuilder("[UncleSwap] outing: walking without the path grid (" + why + "); hasPath " +
                ai.hasPath + ", pending " + ai.pathPending + ", reached " + ai.reachedEndOfPath + "; graphs:");
            if (AstarPath.active != null && AstarPath.active.data != null && AstarPath.active.data.graphs != null)
                foreach (var g in AstarPath.active.data.graphs)
                {
                    if (g == null) continue;
                    sb.Append(" " + g.GetType().Name);
                    var gg = g as GridGraph;
                    if (gg != null) sb.Append(" center " + gg.center + " size " + (gg.width * gg.nodeSize).ToString("0") + "x" + (gg.depth * gg.nodeSize).ToString("0"));
                }
            sb.Append("; she is at " + transform.position);
            Debug.Log(sb.ToString());
        }

        void Steer(UncleLogicC u, float dt)
        {
            var ai = GetComponent<AIPath>();
            var cc = GetComponent<CharacterController>();
            if (ai == null || cc == null || !cc.enabled) return;
            Transform tgt = ai.target;
            Vector3 pos = transform.position;
            float far = tgt != null ? Flat(tgt.position - pos).magnitude : 0f;
            bool following = outState == Out.Follow || outState == Out.Wait;
            string state = outState + (direct ? "/direct" : "/A*");
            if (state != followLast)
            {
                followLast = state;
                stuckPos = pos;
                stuckSince = Time.time;
                shortFor = 0f;
                FollowLog(u, ai, far, "state change");
            }
            else if (outState != Out.Wait) FollowLog(u, ai, far, null);
            if (!direct)
            {
                if (Time.time > navCheckAt)
                {
                    navCheckAt = Time.time + 0.5f;
                    if (OffGraph()) { GoDirect(ai, "no grid node near her"); return; }
                }
                // stuck = she got less than half a leg further in 2.5 s while the target is away (pathPending no longer resets it)
                if (far < 2f * walkLegLen || Flat(pos - stuckPos).magnitude > 0.5f * walkLegLen) { stuckPos = pos; stuckSince = Time.time; }
                else if (Time.time - stuckSince > 2.5f) { GoDirect(ai, "stuck on the path"); return; }
                // the path ends short of you: the graph has no node near you (road stop: only the car's own nodes)
                bool shortEnd = following && ai.hasPath && !ai.pathPending && ai.reachedEndOfPath && far > 2f * walkLegLen;
                shortFor = shortEnd ? shortFor + dt : 0f;
                if (shortFor > 1.5f) GoDirect(ai, "the path ends " + (far / walkLegLen).ToString("0.0") + " legs short of the target");
                return;
            }
            // back on the graph (towns, motel): both she and the target are next to nodes again
            if (following && tgt != null && Time.time - directSince > 5f && Time.time > navCheckAt)
            {
                navCheckAt = Time.time + 1f;
                if (NodeDist(pos) <= 1.5f && NodeDist(tgt.position) <= 1.5f)
                {
                    direct = false;
                    ai.canMove = true;
                    stuckPos = pos;
                    stuckSince = Time.time;
                    shortFor = 0f;
                    Debug.Log("[UncleSwap] outing: back on the path grid (she and the target are next to nodes)");
                    return;
                }
            }
            ai.canMove = false;
            if (tgt == null) return;
            bool toCar = tgt == u.uncleCarTransform || tgt == u.relayNodeTransform;
            if (far < (toCar ? 0.05f : 0.3f * walkLegLen)) { cc.SimpleMove(Vector3.zero); return; }
            Vector3 dir = Flat(tgt.position - pos).normalized;
            float spd = Mathf.Max(0.3f, ai.maxSpeed);
            cc.SimpleMove(dir * Mathf.Min(spd, far / Mathf.Max(dt, 1e-3f)));
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(dir, Vector3.up), 360f * dt);
        }

        // Game bug: RadioTurnedDown (radio turned down or the station changed) runs InterruptCheck, which sets isTalking,
        // and only resets it if the radio had been loud and the line was said. Otherwise isTalking stays true for good
        // and every remark of hers is refused. Reset it once the bubble has been empty for a moment.
        float radioStuck;

        void FixRadioTalk(UncleLogicC u)
        {
            if (u.isTalking && u.interruptConvoItem == "RadioTurnedDown" && !BubbleShown(u))
            {
                radioStuck += Time.deltaTime;
                if (radioStuck < 1.5f) return;
                u.isTalking = false;
                u.interruptConvoItem = string.Empty;
                Debug.Log("[UncleSwap] radio: cleared the game's stuck talking flag (RadioTurnedDown said nothing)");
            }
            radioStuck = 0f;
        }

        void WarpIn(UncleLogicC u, string why)
        {
            if (!MornWarpOk(why)) return;
            if (mornPhase != MPhase.Off) mornWarped = true;
            direct = false;
            Debug.Log("[UncleSwap] outing: put in her seat (" + why + ")");
            var ai = GetComponent<AIPath>();
            if (ai != null) ai.target = null;
            outState = Out.InCar;
            u.StartCoroutine(u.UncleEnterCar());   // as every morning at the motel
            // task 14: the morning warp is silent (she did not chase you, no out_left); the G outing keeps it
            bool morning = mornPhase != MPhase.Off;
            outLeftSayAt = morning ? -1f : Time.time + 6f;
            if (morning) Debug.Log("[UncleSwap] Morning: warp without the out_left line");
        }

        // every motel door the player opens sets her AIPath target to the room (EnvironmentDoorsC), she would walk back
        void FixMotelTarget(UncleLogicC u)
        {
            var ai = GetComponent<AIPath>();
            if (ai == null || outT == null || ai.target == outT || !IsMotelNode(u, ai.target)) return;
            Debug.Log("[UncleSwap] outing: a motel door sent her back to the room, target restored");
            ai.target = outT;
        }

        void UpdateOuting()
        {
            var u = OutU();
            if (u == null) return;
            float dt = Time.deltaTime;

            FixRadioTalk(u);
            if (outLeftSayAt > 0f && Time.time > outLeftSayAt)
            {
                if (remarks != null && remarks.CanSay(u)) { OutSay("out_left"); outLeftSayAt = -1f; }
                else if (Time.time > outLeftSayAt + 60f) outLeftSayAt = -1f;
            }
            if (MornHolding || outState == Out.InCar || outState == Out.Exiting) { followLast = null; return; }

            // the game took her somewhere itself (motel, morning, gone for good)
            if (InCar() || u.uncleGoneForever || u.uncleAtHome || (animator != null && animator.GetBool("bedTime")))
            {
                outState = Out.InCar;
                direct = false;
                EndFetch();
                if (pouring) EndPour(null, "she is in the car again", false);
                Debug.Log("[UncleSwap] outing: in the car again");
                return;
            }

            float speed = CarSpeed();
            movingFor = speed > 3f ? movingFor + dt : 0f;
            bool sat = PlayerSat();
            bool satNow = sat && !outPlayerSat;
            outPlayerSat = sat;
            float toCar = u.uncleCarTransform != null ? Vector3.Distance(transform.position, u.uncleCarTransform.position) : 0f;

            if (outState == Out.Follow || outState == Out.Wait) FixMotelTarget(u);
            if (pouring) { UpdatePour(u, dt); return; }   // task 87
            if (fetching) { UpdateFetch(u, dt); return; }
            if (outState == Out.Follow) OutFollowTarget();
            Steer(u, dt);
            if (outState == Out.Follow || outState == Out.Wait)
            {
                if (satNow) StartBoarding(u, "you sat down");
                else if (movingFor > 0.5f) StartBoarding(u, "the car moves");
                return;
            }

            // boarding: the game walks her to the door; give up if she can't make it
            if (Vector3.Distance(transform.position, boardLastPos) > 0.05f * walkLegLen) { stillFor = 0f; boardLastPos = transform.position; }
            else stillFor += dt;
            if (toCar > 60f * walkLegLen) WarpIn(u, "too far");
            else if (movingFor > 3f) WarpIn(u, "the car drove off");
            else if (stillFor > 5f && !u.carDoorOpen) WarpIn(u, "no way to the door");
            else if (stillFor > 8f) WarpIn(u, "stuck at the door");
            else if (Time.time - boardAt > 30f) WarpIn(u, "took too long");
        }
    }
}
