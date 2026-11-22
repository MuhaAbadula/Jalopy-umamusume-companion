using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class CompanionDriver
    {
        const float SnackDriveSeconds = 60f;
        float snackDriveTime;
        float nextSnackCheck;
        const float BiteAt = 1.1f;
        const float SnackDuration = 3.2f;
        const float FullnessDuration = 45f;
        int fullBellyShape = -1;
        float fullSince = -1f;
        float snackStarted = -1f;
        GameObject snackProp;

        void UpdateBootSnack()
        {
            if (!CharFeatures.Has("boot_snack") || Time.timeScale == 0f) return;
            if (fullSince >= 0f)
            {
                float age = Time.time - fullSince;
                float blend = Mathf.Min(age / 1.2f, (FullnessDuration - age) / 3f);
                girlSmr.SetBlendShapeWeight(fullBellyShape, Mathf.SmoothStep(0f, 100f, blend));
                if (age >= FullnessDuration) fullSince = -1f;
            }
            var actor = OutU();
            if (snackStarted >= 0f)
            {
                if (actor == null || !actor.isSat || !InCar() || actor.isTalking ||
                    actor.uncleAtHome || actor.uncleGoneForever || Asleep() || Doze.Muted || outState != Out.InCar)
                {
                    EndSnack();
                    return;
                }
                float elapsed = Time.time - snackStarted;
                if (elapsed >= BiteAt && snackProp != null) RemoveSnackProp();
                if (elapsed >= SnackDuration)
                {
                    EndSnack();
                    if (fullBellyShape >= 0) fullSince = Time.time;
                    life.React("happy", 1.5f, "smile", 1.5f);
                    OutSay("boot_snack");
                }
                return;
            }
            if (actor == null || !actor.isSat || actor.uncleAtHome || actor.uncleGoneForever) return;
            if (CarSpeed() > 2f)
            {
                snackDriveTime = Mathf.Min(SnackDriveSeconds, snackDriveTime + Time.deltaTime);
                return;
            }
            if (snackDriveTime < SnackDriveSeconds || Time.time < nextSnackCheck) return;
            nextSnackCheck = Time.time + 3f;
            StartBootSnack(actor, false);
        }

#if DEBUG
        internal string DebugBootSnack(bool preview)
        {
            if (!CharFeatures.Has("boot_snack")) return "Select Oguri Cap first.";
            if (Time.timeScale == 0f) return "Resume the game first.";
            if (snackStarted >= 0f) return "She is already eating.";
            if (CarSpeed() > 2f) return "Stop the car first.";
            return StartBootSnack(OutU(), preview);
        }

#endif

        // A null result means the animation started. A preview supplies only the prop in her hand.
        string StartBootSnack(UncleLogicC actor, bool preview)
        {
            if (actor == null || actor.uncleAtHome || actor.uncleGoneForever) return "Take Oguri along on the trip first.";
            if (!actor.isSat || !InCar() || outState != Out.InCar || actor.readyToBoard)
                return "Use 'Put her in her seat' first.";
            if (actor.carLogic == null || life == null || girlSmr == null || remarks == null) return "Companion is still loading.";
            if (Asleep() || Doze.Muted) return "Wait until she wakes up.";
            if (Fetching || !remarks.CanSay(actor)) return "Wait until she is free and the dialogue ends.";
            var player = Camera.main != null ? Camera.main.GetComponent<DragRigidbodyC>() : null;
            if (player == null || player.pickingUp || player.droppingOff || player.tweenTransition)
                return "Finish the current interaction first.";
            if (!preview)
            {
                var car = actor.carLogic.GetComponent<CarLogicC>();
                if (car == null || car.bootInventory == null) return "Boot inventory is unavailable.";
                var food = FindSnackInBoot(car);
                if (food == null) return "Put an unpacked sausage packet in the boot.";
                food.BorderRemoveFromInventory();
                snackDriveTime = 0f;
            }

            snackStarted = Time.time;
            snackProp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            snackProp.name = "Oguri snack";
            snackProp.SetActive(false);
            var collider = snackProp.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            var renderer = snackProp.GetComponent<Renderer>();
            renderer.sharedMaterial = girlSmr.sharedMaterial;
            var tint = new MaterialPropertyBlock();
            tint.SetTexture("_MainTex", Texture2D.whiteTexture);
            tint.SetColor("_Color", new Color32(168, 81, 52, 255));
            renderer.SetPropertyBlock(tint);
            return null;
        }

        static ObjectPickupC FindSnackInBoot(CarLogicC car)
        {
            // The inventory itself can be hidden with the lid shut. Only its directly stowed goods belong here.
            foreach (var food in car.bootInventory.GetComponentsInChildren<ObjectPickupC>(true))
            {
                if (food.componentHeader != "ui_obj_meat_01" || !food.isPurchased ||
                    food.isTweenTransition || food.inventoryPlacedAt == null ||
                    food.transform.parent != food.inventoryPlacedAt) continue;
                var slot = food.inventoryPlacedAt.GetComponent<InventoryRelayC>();
                if (slot == null || slot.inventoryBase != car.bootInventory || !slot.isOccupied) continue;

                return food;
            }
            return null;
        }

        void SnackFace()
        {
            float t = Time.time - snackStarted;
            float bite = Mathf.Clamp01(1f - Mathf.Abs(t - (BiteAt - 0.15f)) / 0.34f);
            float chew = Mathf.InverseLerp(BiteAt, 2.95f, t);
            float wave = Mathf.Sin(chew * 3f * Mathf.PI);
            life.Face.Pulse("snack_chew", Mathf.Max(bite, 0.72f * wave * wave));
        }

        void RemoveSnackProp()
        {
            snackProp.SetActive(false);
            Destroy(snackProp);
            snackProp = null;
        }

        void EndSnack()
        {
            if (snackProp != null) RemoveSnackProp();
            snackStarted = -1f;
        }

        void OnDisable()
        {
            EndSnack();
            if (fullSince >= 0f && girlSmr != null) girlSmr.SetBlendShapeWeight(fullBellyShape, 0f);
            fullSince = -1f;
        }
    }

    public partial class Life
    {
        Transform snackThumb, snackIndex;
        Vector3 thumbPadLocal, indexPadLocal;
        Quaternion snackWristRest;

        void BindSnackGrip(Dictionary<string, Transform> bones)
        {
            if (armL != null) snackWristRest = armL.Hand.localRotation;
            bones.TryGetValue("Thumb_03_L", out snackThumb);
            bones.TryGetValue("Index_03_L", out snackIndex);
            if (snackThumb != null) thumbPadLocal = FingerPad(snackThumb);
            if (snackIndex != null) indexPadLocal = FingerPad(snackIndex);
        }

        static Vector3 FingerPad(Transform distal)
        {
            // The PMX end handles coincide with the last joint. The pad lies beyond it.
            return -distal.InverseTransformPoint(distal.parent.position) * 0.65f;
        }

        internal void PoseSnack(float elapsed, GameObject food)
        {
            Vector3 center;
            float radius;
            if (armL == null || snackThumb == null || snackIndex == null || !HeadSphere(out center, out radius)) return;
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.Min(elapsed / 0.82f, (1.85f - elapsed) / 0.68f));
            if (weight <= 0f && food == null) return;

            Quaternion face = head.Deform();
            Vector3 up = face * Vector3.up, forward = face * Vector3.forward;
            Vector3 lips = center + (forward * 0.73f - up * 0.40f) * radius;
            // Her seated fingers are already bent. Leave room for the thickness of the sausage.
            CurlFingers(armL, 0.18f * (food != null ? 1f : weight));
            var hand = armL.Hand;
            hand.localRotation = Quaternion.Slerp(hand.localRotation, snackWristRest, weight);
            Vector3 thumb = snackThumb.TransformPoint(thumbPadLocal);
            Vector3 index = snackIndex.TransformPoint(indexPadLocal);
            Vector3 grip = Vector3.Lerp(thumb, index, 0.52f);
            Vector3 fingers = Knuckles(armL) - hand.position;
            Vector3 pinch = (thumb - index).normalized;
            // Lay the sausage across the fingertips so the free end approaches the lips from the front.
            Vector3 along = Quaternion.AngleAxis(120f, pinch) * Vector3.ProjectOnPlane(fingers, pinch).normalized;
            Vector3 middle = grip + along * (radius * 0.10f);
            Vector3 tip = middle + along * (radius * 0.31f);
            Vector3 localMiddle = hand.InverseTransformPoint(middle);
            Vector3 localAxis = hand.InverseTransformDirection(along);

            // Shoulder and elbow carry the bite to her lips. The wrist stays aligned with the forearm.
            Vector3 bodyForward, bodyUp;
            Facing(out bodyForward, out bodyUp);
            Vector3 elbowPole = -bodyUp + Vector3.Cross(bodyForward, bodyUp) * 0.45f + bodyForward * 0.65f;
            SolveArm(armL, lips, elbowPole, weight, hand.InverseTransformPoint(tip));

            if (food == null) return;
            food.transform.position = hand.TransformPoint(localMiddle);
            food.transform.rotation = Quaternion.FromToRotation(Vector3.up, hand.TransformDirection(localAxis));
            food.transform.localScale = new Vector3(0.13f, 0.31f, 0.13f) * radius;
            if (!food.activeSelf) food.SetActive(true);
        }
    }
}
