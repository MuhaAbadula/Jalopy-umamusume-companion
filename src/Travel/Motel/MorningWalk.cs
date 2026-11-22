using System.Collections.Generic;
using Pathfinding;
using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class CompanionDriver
    {
        internal sealed class MorningWalk
        {
            // Siblings under the motel prefab's Nodes transform, in walking order.
            // The three inside each room run down the aisle between the beds.
            static readonly int[] Room1A = { 36, 35, 34, 8 };
            static readonly int[] Room1B = { 37, 38, 39, 11, 10, 9, 8 };
            static readonly int[] Room2A = { 33, 32, 31, 20 };
            static readonly int[] Room2B = { 28, 29, 30, 22, 21, 20 };
            static readonly int[] Room2C = { 27, 26, 25, 24, 23, 22, 21, 20 };
            static readonly int[] Downstairs = { 19, 18, 17, 16, 15, 14, 13 };
            static readonly int[] ToParking = { 12, 7, 6, 40, 41, 3, 2 };

            // Local positions from the game's Motel/Nodes prefab. Its local Z is up.
            // The game can remove navigation nodes under the parked car between nights.
            // Store the turns in motel space so their positions survive that change.
            static readonly Vector3[] FloorPlan = {
                // Parking and entrance
                Vector3.zero, Vector3.zero,
                new Vector3(7.340f, -10.505f, 0.168f), new Vector3(6.950f, -6.710f, 0.460f),
                new Vector3(7.290f, -2.696f, 0.168f),
                // Lobby
                new Vector3(3.689f, -2.691f, 0.168f), new Vector3(2.867f, 1.809f, 0.168f),
                new Vector3(2.963f, 6.232f, 0.168f),
                // Ground-floor corridor
                new Vector3(-1.804f, 10.937f, 0.168f), new Vector3(-4.162f, 10.809f, 0.168f),
                new Vector3(-6.662f, 10.481f, 0.168f), new Vector3(-8.837f, 10.696f, 0.168f),
                new Vector3(2.338f, 11.302f, 0.168f),
                // Stairs and landing
                new Vector3(5.754f, 11.302f, 2.286f), new Vector3(6.838f, 11.302f, 3.668f),
                new Vector3(8.838f, 11.302f, 4.668f), new Vector3(10.838f, 11.302f, 5.668f),
                new Vector3(12.338f, 11.302f, 5.668f), new Vector3(12.338f, 9.512f, 5.668f),
                new Vector3(9.838f, 9.105f, 5.668f),
                // Upper corridor
                new Vector3(7.112f, 9.064f, 5.668f), new Vector3(3.107f, 9.064f, 5.668f),
                new Vector3(-1.128f, 9.431f, 5.668f), new Vector3(-4.518f, 10.242f, 5.668f),
                new Vector3(-8.103f, 10.975f, 5.668f),
                // Room 2C
                new Vector3(-8.077f, 7.064f, 5.668f), new Vector3(-8.077f, 4.483f, 5.668f),
                new Vector3(-8.077f, 1.484f, 5.668f),
                // Room 2B
                new Vector3(-0.656f, 1.484f, 5.668f), new Vector3(-0.656f, 4.483f, 5.668f),
                new Vector3(-0.656f, 7.064f, 5.668f),
                // Room 2A
                new Vector3(7.214f, 7.064f, 5.668f), new Vector3(7.423f, 4.483f, 5.668f),
                new Vector3(7.423f, 1.484f, 5.668f),
                // Room 1A
                new Vector3(-1.492f, 7.064f, 0.668f), new Vector3(-1.492f, 4.483f, 0.668f),
                new Vector3(-1.492f, 1.966f, 0.668f),
                // Room 1B
                new Vector3(-8.683f, 1.966f, 0.668f), new Vector3(-8.683f, 4.483f, 0.668f),
                new Vector3(-8.683f, 7.064f, 0.668f),
                // Cross the clear aisle between the counter and the sofa, then face the exit.
                new Vector3(3.689f, -1.750f, 0.168f), new Vector3(7.025f, -1.750f, 0.168f),
            };

            static readonly int EnterCar = Animator.StringToHash("Base Layer.Uncle_Enter_Car");
            static MorningWalk current;
            internal static bool IsWalking(CompanionDriver d) => current != null && current.driver == d;

            readonly CompanionDriver driver;
            readonly UncleLogicC actor;
            readonly AIPath ai;
            readonly AIDestinationSetter destination;
            readonly CharacterController body;
            readonly Transform hips;
            readonly List<Vector3> stops = new List<Vector3>();
            readonly Transform frame;
            readonly EnvironmentDoorsC roomDoor, exitDoor;
            readonly bool authoredRoute;
            int next, lastDirectMoveFrame = -1;
            bool onGraph, atRelay, parkingScanned;
            float waitUntil, progressAt, nextStallLog;
            Vector3 lastPosition;

            internal static bool UpdateDeparture(CompanionDriver d)
            {
                if (current == null || current.driver != d)
                {
                    var wheel = CompanionMod.Current;
                    if (wheel == null || !wheel.isActiveAndEnabled) return false;
                    if (d.mornFreeAt < 0f || d.mornPose != MPose.None) return true;
                    var motel = d.mornMotel;
                    if (motel == null) return false;
                    current = new MorningWalk(d, motel);
                    d.outState = Out.Boarding;
                    var life = d.life;
                    if (life != null) life.MornStay = false;
                }
                current.Step();
                return true;
            }

            internal static void EndMorning(CompanionDriver driver)
            {
                if (current != null && current.driver == driver) current = null;
            }

            // Pose() copies the seated uncle's pelvis before every render. MornFeet only
            // centres it while standing still. Keep it over the controller during the walk
            // too, before WalkIK adds the gait's sway and plants the feet.
            internal static void CenterWalkingPose(CompanionDriver driver, bool inCar)
            {
                if (!IsWalking(driver) || inCar || current.hips == null || current.body == null || !current.body.enabled) return;
                var root = driver.transform;
                current.hips.position -= Vector3.ProjectOnPlane(current.hips.position - root.position, root.up);
                driver.hipOffSet = false;
            }

            MorningWalk(CompanionDriver d, MotelLogicC motel)
            {
                driver = d;
                actor = d.GetComponent<UncleLogicC>();
                body = d.GetComponent<CharacterController>();
                hips = d.hipLink == null ? null : d.hipLink.Girl;
                ai = d.GetComponent<AIPath>();
                destination = d.GetComponent<AIDestinationSetter>(); // created by UmamusumeCompanion when it places her in the room
                int r = motel.roomNumber;
                var door = At(motel.doorNodes, r);
                var handle = At(motel.roomDoors, r);
                roomDoor = handle == null ? null : handle.GetComponent<EnvironmentDoorsC>();
                var nodes = motel.lobbyNode == null ? null : motel.lobbyNode.parent;
                int[] room = door == null ? null : RoomPath(door.name);
                authoredRoute = nodes != null && room != null && door.parent == nodes &&
                    (door.localPosition - FloorPlan[room[3]]).sqrMagnitude < 0.01f &&
                    (motel.lobbyNode.localPosition - FloorPlan[5]).sqrMagnitude < 0.01f;
                frame = authoredRoute ? nodes : motel.transform;
                if (authoredRoute)
                {
                    Vector3 inside = frame.InverseTransformPoint(d.transform.position);
                    int start = 0;
                    for (int i = 1; i < 3; i++)
                        if ((FloorPlan[room[i]] - inside).sqrMagnitude < (FloorPlan[room[start]] - inside).sqrMagnitude)
                            start = i;
                    for (int i = start; i < room.Length; i++)
                    {
                        Vector3 stop = FloorPlan[room[i]];
                        // Line up inside the room before crossing the jambs straight on.
                        if (i == 2) stop.x = FloorPlan[room[3]].x;
                        stops.Add(stop);
                    }
                    if (room == Room2A || room == Room2B || room == Room2C)
                        foreach (int i in Downstairs) stops.Add(FloorPlan[i]);
                    foreach (int i in ToParking) stops.Add(FloorPlan[i]);
                }
                else
                {
                    // Other motel layouts can use their own A* network and named targets.
                    if (door != null) stops.Add(frame.InverseTransformPoint(door.position));
                    if (motel.lobbyNode != null) stops.Add(frame.InverseTransformPoint(motel.lobbyNode.position));
                    onGraph = true;
                }
                if (motel.transform.parent != null && motel.lobbyNode != null)
                {
                    Vector3 entry = onGraph ? motel.lobbyNode.position :
                        frame.TransformPoint((FloorPlan[4] + FloorPlan[3]) * 0.5f);
                    float nearest = float.PositiveInfinity;
                    foreach (var candidate in motel.transform.parent.GetComponentsInChildren<EnvironmentDoorsC>())
                    {
                        if (candidate.isLocked || candidate == roomDoor) continue;
                        float distance = (candidate.transform.position - entry).sqrMagnitude;
                        if (distance < nearest) { nearest = distance; exitDoor = candidate; }
                    }
                }
                ResetProgress();
                Debug.Log("[Umamusume companion] Morning walk from " + (door == null ? motel.name : door.name) +
                          " via " + stops.Count + " stops");
            }

            static T At<T>(T[] array, int i) where T : class =>
                array != null && i >= 0 && i < array.Length ? array[i] : null;

            static int[] RoomPath(string name)
            {
                switch (name)
                {
                    case "Door1ANode": return Room1A;
                    case "Door1BNode": return Room1B;
                    case "Door2ANode": return Room2A;
                    case "Door2BNode": return Room2B;
                    case "Door2CNode": return Room2C;
                    default: return null;
                }
            }

            float Distance(Transform target) => (target.position - driver.transform.position).sqrMagnitude;

            void ResetProgress()
            {
                lastPosition = driver.transform.position;
                progressAt = Time.time;
            }

            bool OpenNearby(EnvironmentDoorsC door)
            {
                if (door == null || door.open || door.isLocked || !door.isActiveAndEnabled) return false;
                Vector3 delta = door.transform.position - body.bounds.center;
                if (Mathf.Abs(delta.y) > 2.5f || delta.x * delta.x + delta.z * delta.z > 16f) return false;
                door.StartCoroutine(door.Trigger());
                waitUntil = Time.time + 1.45f;
                return true;
            }

            void Step()
            {
                if (actor.isSat && driver.transform.parent == actor.uncleCarTransform)
                {
                    // Wake-up keeps the native animator in seated idle throughout the walk.
                    // Start entry once the game has attached the actor to the car anchor.
                    var animator = actor.GetComponent<Animator>();
                    if (animator != null && animator.HasState(0, EnterCar))
                        animator.Play(EnterCar, 0, 0f);
                    driver.outState = Out.InCar;
                    Debug.Log("[Umamusume companion] Morning walk finished at the passenger seat");
                    return;
                }
                if (body == null || !body.enabled || ai == null || Time.deltaTime <= 0f) return;
                if (Time.time < waitUntil || OpenNearby(roomDoor) || OpenNearby(exitDoor))
                {
                    ai.canMove = false;
                    ResetProgress();
                    return;
                }
                // Consume reached points in the same update, including the last point
                // outside, so the parking path can start while the final stride finishes.
                while (next < stops.Count && frame != null)
                {
                    if (!WalkTo(frame.TransformPoint(stops[next]), 0.4f, 2f)) return;
                    next++;
                    onGraph = !authoredRoute;
                    ResetProgress();
                }
                if (actor.uncleCarTransform == null || actor.relayNodeTransform == null ||
                    actor.uncleDoorTrigger == null || actor.carNavBlocker == null || AstarPath.active == null) return;
                if (!parkingScanned)
                {
                    // Night moves the town back to the world origin. Scan once here with the
                    // car blocker, exactly as the game's normal boarding routine does.
                    actor.WalkTowardsCarDoor();
                    parkingScanned = true;
                    onGraph = true;
                    ResetProgress();
                }
                if (!atRelay)
                {
                    if (!WalkTo(actor.relayNodeTransform.position, 0.45f, 1f)) return;
                    atRelay = true;
                    onGraph = false;
                    ResetProgress();
                }
                // If the player moved the car during the last few steps, approach its side again.
                if (Distance(actor.relayNodeTransform) > 36f)
                {
                    atRelay = false;
                    onGraph = true;
                    return;
                }
                if (Distance(actor.uncleCarTransform) > 0.25f)
                {
                    WalkTo(actor.uncleCarTransform.position, 0.05f, 0.49f);
                    return;
                }
                ai.canMove = false;
                // UncleLogicC opens the door and plays its normal boarding sequence here.
                actor.walkingTowardsDoor = true;
                actor.readyToBoard = true;
            }

            bool WalkTo(Vector3 target, float radius, float height)
            {
                Vector3 delta = target - driver.transform.position;
                float dy = delta.y;
                delta.y = 0f;
                float distance = delta.magnitude;
                if (distance <= radius && Mathf.Abs(dy) <= height)
                {
                    ai.canMove = false;
                    return true;
                }
                if ((driver.transform.position - lastPosition).sqrMagnitude > 0.12f) ResetProgress();
                else if (Time.time - progressAt > 2f)
                {
                    // Retry a blocked leg around a prop, or finish the gap between a sparse
                    // graph endpoint and the actual car. Both modes use physical movement.
                    onGraph = !onGraph;
                    if (Time.time >= nextStallLog)
                    {
                        Debug.Log("[Umamusume companion] Morning walk: retry at " + driver.transform.position +
                                  ", stop " + next + " at " + target);
                        nextStallLog = Time.time + 10f;
                    }
                    ResetProgress();
                }
                if (onGraph && AstarPath.active != null && destination != null)
                {
                    if (destination.target != null || !ai.canMove || (ai.destination - target).sqrMagnitude > 0.04f)
                    {
                        destination.target = null;
                        ai.destination = target;
                        ai.SearchPath();
                    }
                    ai.canMove = true;
                }
                else
                {
                    ai.canMove = false;
                    if (lastDirectMoveFrame == Time.frameCount) return false;
                    lastDirectMoveFrame = Time.frameCount;
                    Vector3 dir = delta.normalized;
                    body.SimpleMove(dir * Mathf.Min(ai.maxSpeed, distance / Time.deltaTime));
                    if (distance > 0.01f)
                        driver.transform.rotation = Quaternion.RotateTowards(driver.transform.rotation,
                            Quaternion.LookRotation(dir), 270f * Time.deltaTime);
                    Vector3 remaining = target - driver.transform.position;
                    return remaining.x * remaining.x + remaining.z * remaining.z <= radius * radius &&
                           Mathf.Abs(remaining.y) <= height;
                }
                return false;
            }
        }
    }
}
