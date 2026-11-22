using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class CompanionDriver
    {
        const float FetchRadius = 15f;
        GameObject fetchBox;
        internal bool Fetching => fetching;
        bool fetching, carrying;
        Vector3 lastFetchPosition, parkedSeat;
        float stalled, checkSpaceAt;
        InventoryLogicC fetchBoot;
        Collider[] carryColliders;
        bool[] colliderStates;
        bool wasKinematic;
        int bootLeg, detours;
        float bootSide;

        // Walk out past the open door, along the body, then across behind the bumper.
        Vector3 BootWaypoint(UncleLogicC u, Vector3 actor, out bool atBoot)
        {
            Vector3 passenger = u.uncleCarTransform.position;
            Vector3 driver = u.carLogic.GetComponent<CarLogicC>().playerSeat.transform.position;
            var car = u.carLogic.GetComponent<CarLogicC>();
            Vector3 middle = (driver + passenger) * 0.5f;
            Vector3 rear = Flat(fetchBoot.transform.position - middle).normalized;
            Vector3 bumper = fetchBoot.transform.position + rear * (2f * walkLegLen);
            if (car.bootStandingLoc != null)
            {
                bumper = car.bootStandingLoc.position;
                rear = Flat(bumper - middle).normalized;
            }
            Vector3 across = Vector3.Cross(Vector3.up, rear);
            float longitudinal = Vector3.Dot(actor - bumper, rear);
            float lateral = Vector3.Dot(actor - middle, across);
            float width = Flat(passenger - driver).magnitude * 1.5f + (1.4f + detours) * walkLegLen;
            if (bootSide == 0f)
            {
                bootSide = lateral >= 0f ? 1f : -1f;
                if (longitudinal >= 0f) bootLeg = 2;
            }
            if (bootLeg == 0 && lateral * bootSide >= width - 0.35f * walkLegLen) bootLeg++;
            if (bootLeg == 1 && longitudinal >= 0.65f * walkLegLen) bootLeg++;
            atBoot = bootLeg == 2 && Flat(bumper - actor).magnitude <= 1.4f * walkLegLen &&
                     Mathf.Abs(actor.y - bumper.y) <= GetComponent<CharacterController>().bounds.size.y;
            if (bootLeg == 2) return bumper;
            Vector3 outside = actor + across * (width * bootSide - lateral);
            return bootLeg == 0 ? outside : outside + rear * (walkLegLen - longitudinal);
        }

        public string FetchKey()
        {
            if (fetching) return "Already fetching a crate.";
            var u = OutU();
            var route = RouteGeneratorC.Global;
            if (Time.timeScale == 0 || u == null || route == null || u.carLogic == null ||
                CarSpeed() > 1f || (outState == Out.InCar ? ExitBlocked(u) != null || !DoorClear(u) :
                (outState != Out.Follow && outState != Out.Wait) || MornHolding))
                return "Stop with the passenger door clear first.";
            var car = u.carLogic.GetComponent<CarLogicC>();
            fetchBoot = car.bootInventory.GetComponent<InventoryLogicC>();
            float best = FetchRadius * FetchRadius;
            foreach (var box in route.cratesToBeCleanedUp)
            {
                if (box == null || !box.activeInHierarchy || box.transform.parent != null ||
                    box.GetComponent<BoxContentsC>() == null || box.GetComponent<Rigidbody>() == null) continue;
                var pickup = box.GetComponent<ObjectPickupC>();
                if (pickup == null || pickup.inventoryPlacedAt != null) continue;
                float distance = (box.transform.position - car.playerSeat.transform.position).sqrMagnitude;
                if (distance > best) continue;
                fetchBox = box;
                best = distance;
            }
            if (fetchBox == null) return "No roadside crate nearby.";
            fetchLid = car.bootLid != null ? car.bootLid.GetComponent<DoorLogicC>() : null;
            if (fetchLid == null) { fetchBox = null; return "Boot lid unavailable."; }
            parkedSeat = u.uncleCarTransform.position;
            fetching = true;
            stalled = 0f;
            checkSpaceAt = 0f;
            lastFetchPosition = transform.position;
            if (outState == Out.InCar) StartCoroutine(ExitCar(u));
            else
            {
                outState = Out.Follow;
                var ai = GetComponent<Pathfinding.AIPath>();
                if (ai != null) ai.target = outT;
            }
            return "Fetching the nearest roadside crate.";
        }

        DoorLogicC fetchLid;
        bool openedLid, reaching;
        float reachTime, liftTime, lidReadyAt, handleWeight, gripWidth;
        Vector3 crateBottomLocal;
        Vector3 liftStart;
        Transform placingSlot;
        float slideTime;
        Vector3 slideStart;
        Quaternion slideRotation;

        void PoseCrateHands()
        {
            if (life == null || fetchBox == null || (!carrying && !reaching)) return;
            Vector3 side = transform.right;
            Vector3 center = fetchBox.transform.TransformPoint(crateBottomLocal);
            Vector3 right = center + side * gripWidth;
            if (fetchLid != null && handleWeight > 0f)
            {
                Transform handle = fetchLid.handle != null ? fetchLid.handle.transform : fetchLid.transform;
                right = Vector3.Lerp(right, handle.position, handleWeight);
            }
            life.CrateHands(center - side * gripWidth, right, side, transform.forward, carrying ? 1f : Mathf.Clamp01(reachTime / 0.4f));
        }

        void FaceCrateTarget(Vector3 position, float dt)
        {
            Vector3 direction = Flat(position - transform.position);
            if (direction.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), dt * 240f);
        }

        bool OpenForLoading(float dt)
        {
            if (fetchLid == null) return false;
            if (!fetchLid.open)
            {
                if (fetchLid.isLocked || Time.time < lidReadyAt) return false;
                handleWeight = Mathf.MoveTowards(handleWeight, 1f, dt * 3f);
                if (handleWeight < 1f) return false;
                fetchLid.StartCoroutine(fetchLid.Trigger());
                openedLid = fetchLid.open;
                lidReadyAt = Time.time + 1.45f;
                return false;
            }
            handleWeight = Mathf.MoveTowards(handleWeight, 0f, dt * 2f);
            return Time.time >= lidReadyAt;
        }

        internal void EndFetch()
        {
            if (carrying && fetchBox != null)
            {
                fetchBox.transform.SetParent(null, true);
                fetchBox.GetComponent<Rigidbody>().isKinematic = wasKinematic;
                var route = RouteGeneratorC.Global;
                if (route != null && !route.cratesToBeCleanedUp.Contains(fetchBox))
                {
                    fetchBox.GetComponent<ObjectPickupC>().isInGarbageCollection = route.cratesToBeCleanedUp.Count;
                    route.cratesToBeCleanedUp.Add(fetchBox);
                }
                for (int i = 0; i < carryColliders.Length; i++)
                    if (carryColliders[i] != null) carryColliders[i].enabled = colliderStates[i];
            }
            if (openedLid && fetchLid != null && fetchLid.open) fetchLid.StartCoroutine(fetchLid.Trigger());
            openedLid = reaching = false;
            reachTime = handleWeight = lidReadyAt = 0f;
            placingSlot = null;
            fetchLid = null;
            fetching = carrying = false;
            fetchBox = null;
            carryColliders = null;
            colliderStates = null;
        }

        void CarryCrate()
        {
            var body = fetchBox.GetComponent<Rigidbody>();
            var route = RouteGeneratorC.Global;
            int index = route.cratesToBeCleanedUp.IndexOf(fetchBox);
            if (index >= 0)
            {
                route.cratesToBeCleanedUp.RemoveAt(index);
                route.UpdateGarbageCollection(index);
            }
            fetchBox.GetComponent<ObjectPickupC>().isInGarbageCollection = -1;
            wasKinematic = body.isKinematic;
            body.isKinematic = true;
            carryColliders = fetchBox.GetComponentsInChildren<Collider>();
            colliderStates = new bool[carryColliders.Length];
            for (int i = 0; i < carryColliders.Length; i++)
            {
                colliderStates[i] = carryColliders[i].enabled;
                carryColliders[i].enabled = false;
            }
            fetchBox.transform.SetParent(transform, true);
            liftStart = fetchBox.transform.localPosition;
            liftTime = 0f;
            fetchBox.transform.rotation = transform.rotation;
            Debug.Log("[Umamusume companion] crate picked up: " + fetchBox.name);
            bootSide = 0f;
            bootLeg = detours = 0;
            carrying = true;
            stalled = 0f;
        }

        void UpdateFetch(UncleLogicC u, float dt)
        {
            if (fetchBox == null || fetchBoot == null || Flat(u.uncleCarTransform.position - parkedSeat).magnitude > 4f * walkLegLen ||
                (!carrying && (fetchBox.transform.parent != null || fetchBox.GetComponent<Rigidbody>() == null ||
                 fetchBox.GetComponent<ObjectPickupC>().isInGarbageCollection < 0)))
            {
                EndFetch();
                StartBoarding(u, "crate request interrupted");
                return;
            }
            var controller = GetComponent<CharacterController>();
            Bounds actor = controller.bounds;
            Vector3 target;
            bool withinReach;
            if (carrying)
            {
                if (placingSlot == null)
                {
                    liftTime += dt;
                    Vector3 held = actor.center + transform.forward * (0.65f * walkLegLen);
                    fetchBox.transform.localPosition = Vector3.Lerp(liftStart, transform.InverseTransformPoint(held),
                        Mathf.SmoothStep(0f, 1f, liftTime / 0.55f));
                }
                target = BootWaypoint(u, actor.center, out withinReach);
            }
            else
            {
                var collider = fetchBox.GetComponent<Collider>();
                if (collider == null) { EndFetch(); StartBoarding(u, "crate removed"); return; }
                target = collider.ClosestPoint(actor.center);
                Vector3 nearest = actor.ClosestPoint(target);
                withinReach = Vector3.Distance(target, nearest) <= Mathf.Max(0.45f, 0.8f * walkLegLen);
            }
            outT.position = target;
            if (withinReach)
            {
                outT.position = transform.position;
                FaceCrateTarget(carrying ? fetchBoot.transform.position : fetchBox.transform.position, dt);
                if (!carrying)
                {
                    reaching = true;
                    reachTime += dt;
                    var bounds = fetchBox.GetComponent<Collider>().bounds;
                    crateBottomLocal = fetchBox.transform.InverseTransformPoint(bounds.center - Vector3.up * bounds.extents.y);
                    gripWidth = Mathf.Clamp(Mathf.Min(bounds.extents.x, bounds.extents.z), walkLegLen * 0.18f, walkLegLen * 0.55f);
                    if (reachTime >= 0.45f) CarryCrate();
                }
                else if (OpenForLoading(dt) && (placingSlot != null || Time.time >= checkSpaceAt))
                {
                    checkSpaceAt = Time.time + 0.6f;
                    if (StowCrate())
                    {
                        EndFetch();
                        StartBoarding(u, "crate stowed");
                        return;
                    }
                }
            }
            else if (Flat(transform.position - lastFetchPosition).sqrMagnitude > 0.04f)
            {
                lastFetchPosition = transform.position;
                stalled = 0f;
            }
            else if (carrying && (stalled += dt) > 3f)
            {
                // Give an open door extra clearance if its tip still blocks this lane.
                detours = Mathf.Min(detours + 1, 3);
                bootLeg = 0;
                stalled = 0f;
            }
            else if (!carrying && (stalled += dt) > 10f)
            {
                EndFetch();
                StartBoarding(u, "crate unreachable");
                return;
            }
            Steer(u, dt);
        }
    }
}
