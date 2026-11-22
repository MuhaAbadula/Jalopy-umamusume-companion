using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class FukuDriver
    {
        bool StowCrate()
        {
            var drag = Camera.main == null ? null : Camera.main.GetComponent<DragRigidbodyC>();
            if (drag == null || drag.tweenTransition || drag.pickingUp) return false;
            var pickup = fetchBox.GetComponent<ObjectPickupC>();
            var inventory = fetchBoot;
            var slot = FindSlot(inventory, pickup);
            if (slot == null)
            {
                var car = OutU().carLogic.GetComponent<CarLogicC>();
                var upgrades = car.GetComponent<ExtraUpgradesC>();
                if (upgrades != null && upgrades.roofRackInstalled && car.roofInventory != null &&
                    car.roofInventory.activeInHierarchy)
                {
                    inventory = car.roofInventory.GetComponent<InventoryLogicC>();
                    if (inventory != null) slot = FindSlot(inventory, pickup);
                }
            }
            if (slot == null) return UnpackCrate();
            var location = slot.transform;
            if (placingSlot != location)
            {
                placingSlot = location;
                slideStart = fetchBox.transform.position;
                slideRotation = fetchBox.transform.rotation;
                slideTime = Time.time;
            }
            float progress = Mathf.SmoothStep(0f, 1f, (Time.time - slideTime) / 0.65f);
            fetchBox.transform.position = Vector3.Lerp(slideStart, location.TransformPoint(pickup.inventoryAdjustPosition), progress);
            fetchBox.transform.rotation = Quaternion.Slerp(slideRotation, location.rotation * Quaternion.Euler(pickup.inventoryAdjustRotation), progress);
            if (progress < 1f) return false;
            fetchBox.transform.SetParent(slot.transform, true);
            fetchBox.transform.localPosition = pickup.inventoryAdjustPosition;
            fetchBox.transform.localEulerAngles = pickup.inventoryAdjustRotation;
            for (int i = 0; i < carryColliders.Length; i++)
                if (carryColliders[i] != null) carryColliders[i].enabled = colliderStates[i];
            foreach (var collider in fetchBox.GetComponents<Collider>())
            {
                collider.isTrigger = true;
                collider.enabled = true;
            }
            foreach (var part in fetchBox.GetComponentsInChildren<Transform>(true))
                part.gameObject.layer = LayerMask.NameToLayer("Default");
            Destroy(fetchBox.GetComponent<Rigidbody>());
            pickup.inventoryPlacedAt = slot.transform;
            carrying = false; // Ownership has passed to the boot.
            Debug.Log("[UncleSwap] crate stored: " + fetchBox.name + " in " + location.name);
            slot.Occupy();
            inventory.SendMessage("ModifyWeight", fetchBox.transform);
            fetchBox.SendMessage("IntoInventory");
            inventory.AvailableSlotCount();
            inventory.UpdateInventory();
            return true;
        }

        bool UnpackCrate()
        {
            var box = fetchBox.GetComponent<BoxContentsC>();
            EndFetch();
            PutDown(box, this, fetchBoot);
            box.Open();
            if (box.boxOpener != null)
            {
                var opener = box.boxOpener.GetComponent<Collider>();
                if (opener != null) opener.enabled = false;
            }
            foreach (var holder in box.slots)
            {
                if (holder == null || holder.childCount == 0) continue;
                var item = holder.GetChild(0).GetComponent<ObjectPickupC>();
                if (item == null) continue;
                var slot = FindSlot(fetchBoot, item);
                if (slot == null) continue;
                StoreGoods(item, slot, fetchBoot);
                box.RemoveContent();
            }
            bool allStored = box.spawnedItems == 0;
            if (!allStored)
            {
                OutingKey(); // Follow -> Wait beside the open crate.
                CompanionMod.Current?.Notify("Ума: «Подойди к багажнику! Помоги, весь товар не влез».", 9f);
            }
            return allStored;
        }

        static void PutDown(BoxContentsC box, FukuDriver driver, InventoryLogicC boot)
        {
            var actor = driver.GetComponent<CharacterController>().bounds;
            var away = Vector3.ProjectOnPlane(actor.center - boot.transform.position, Vector3.up).normalized;
            var body = box.GetComponent<BoxCollider>();
            box.transform.rotation = Quaternion.Euler(0f, driver.transform.eulerAngles.y, 0f);
            var bounds = body.bounds;
            var offset = away * (actor.extents.magnitude + bounds.extents.magnitude);
            var ground = actor.center + offset;
            RaycastHit hit;
            ground.y = UnityEngine.Physics.Raycast(ground + Vector3.up * actor.size.y, Vector3.down, out hit,
                actor.size.y * 4f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : actor.min.y;
            box.transform.position += ground - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        }

        static InventoryRelayC FindSlot(InventoryLogicC inventory, ObjectPickupC item)
        {
            int x = item.dimensionX, y = item.dimensionY, z = item.dimensionZ;
            bool singleColumn = x == 1 && z == 1 && (y == 1 || y == 2);
            Transform[] slots;
            if (singleColumn) slots = inventory.inventorySlots;
            else switch (x + "x" + y + "x" + z)
            {
                case "2x2x1": slots = inventory.slot2x2x1; break;
                case "3x2x1": slots = inventory.slot3x2x1; break;
                case "2x1x2": slots = inventory.slot2x1x2; break;
                case "2x2x2": slots = inventory.slot2x2x2; break;
                case "2x2x3": slots = inventory.slot2x2x3; break;
                case "3x2x3": slots = inventory.slot3x2x3; break;
                case "4x2x2": slots = inventory.slot4x2x2; break;
                case "4x2x3": slots = inventory.slot4x2x3; break;
                case "4x1x1": slots = inventory.slot4x1x1; break;
                case "4x2x1": slots = inventory.slot4x2x1; break;
                case "2x1x3": slots = inventory.slot2x1x3; break;
                default: return null;
            }
            if (slots == null) return null;
            foreach (var location in slots)
            {
                var slot = location == null ? null : location.GetComponent<InventoryRelayC>();
                if (slot == null || location.childCount != 0) continue;
                if (singleColumn)
                {
                    if (!slot.isOccupied && (y == 1 || Free(slot.spaceAbove))) return slot;
                }
                else if (slot.slots != null && slot.slots.Length == x * y * z &&
                         System.Array.TrueForAll(slot.slots, Free)) return slot;
            }
            return null;
        }

        static bool Free(GameObject cell)
        {
            var relay = cell == null ? null : cell.GetComponent<InventoryRelayC>();
            return relay != null && !relay.isOccupied;
        }

        // InventoryLogicC.InstantTrigger can move objects out of the player's
        // hands. Commit the same slot, weight and pickup state for Uma's item only.
        static void StoreGoods(ObjectPickupC item, InventoryRelayC slot, InventoryLogicC boot)
        {
            item.transform.SetParent(slot.transform, false);
            item.transform.localPosition = item.inventoryAdjustPosition;
            item.transform.localEulerAngles = item.inventoryAdjustRotation;
            item.inventoryPlacedAt = slot.transform;
            if (item.dimensionX == 1 && item.dimensionZ == 1)
            {
                slot.isOccupied = true;
                if (item.dimensionY == 2) slot.spaceAbove.GetComponent<InventoryRelayC>().isOccupied = true;
            }
            else slot.Occupy();

            foreach (var part in item.GetComponentsInChildren<Transform>(true))
                part.gameObject.layer = LayerMask.NameToLayer("Default");
            foreach (var collider in item.GetComponents<Collider>())
            {
                collider.enabled = true;
                collider.isTrigger = true;
            }
            var body = item.GetComponent<Rigidbody>();
            if (body != null) Object.Destroy(body);
            item.gameObject.SetActive(true);
            boot.SendMessage("ModifyWeight", item.transform);
            item.SendMessage("IntoInventory");
            boot.AvailableSlotCount();
            boot.UpdateInventory();
        }
    }
}
