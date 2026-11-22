using System;
using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    public partial class FukuDriver
    {
        sealed class Purchase
        {
            public int nextRoomPrice;
            public StoreC store;
            public ShopC till;
            public WalletC wallet;
            public CarLogicC car;
            public readonly List<SupplyOffer> items = new List<SupplyOffer>();
            public float total;
        }

        enum Supply { Kit, Oil, Fuel }

        sealed class SupplyOffer
        {
            public Supply kind;
            public ObjectPickupC item;
            public bool owned;
            public int litres;
            public InventoryRelayC slot;
        }

        bool shopPaidToday;
        string shopResult = "No purchase yet.", shopLine;
        internal string ShopStatus => shopResult;

        void ResetShoppingMorning()
        {
            shopPaidToday = false;
            shopLine = null;
            shopResult = "No purchase this morning.";
        }

        static bool Matches(ObjectPickupC p, Supply kind)
        {
            if (kind == Supply.Fuel) return p.GetComponent<PortableFuelC>() != null;
            if (kind == Supply.Oil)
            {
                var bottle = p.GetComponent<OilBottleC>();
                return bottle != null && bottle.oilLevel > 0;
            }
            var kit = p.GetComponent<RepairKitC>();
            return kit != null && !kit.isToolRack && !kit.isTyreRepairKit && kit.spannerDamageLvl < 3f;
        }

        static IEnumerable<Transform> SupplyLocations(CarLogicC car)
        {
            yield return car.bootInventory.transform;
            var upgrades = car.GetComponent<ExtraUpgradesC>();
            if (upgrades != null && upgrades.roofRackInstalled && car.roofInventory != null)
                yield return car.roofInventory.transform;
            var drag = Camera.main != null ? Camera.main.GetComponent<DragRigidbodyC>() : null;
            if (drag != null)
                foreach (var held in new[] { drag.isHolding1, drag.isHolding2, drag.isHolding3 })
                    if (held != null) yield return held.transform;
        }

        static ObjectPickupC OwnedSupply(CarLogicC car, Supply kind)
        {
            ObjectPickupC best = null;
            int lowestLevel = int.MaxValue;
            foreach (var location in SupplyLocations(car))
                foreach (var p in location.GetComponentsInChildren<ObjectPickupC>(true))
                {
                    if (!p.isPurchased || !Matches(p, kind)) continue;
                    if (kind != Supply.Fuel) return p;
                    int level = p.GetComponent<PortableFuelC>().fuelLevel;
                    if (level < lowestLevel) { lowestLevel = level; best = p; }
                }
            return best;
        }

        static ObjectPickupC ShelfItem(StoreC store, Supply kind)
        {
            var shelves = kind == Supply.Oil ? store.oilSpawnLocs :
                          kind == Supply.Kit ? store.repairKitSpawnLocs : store.gasCanSpawnLocs;
            ObjectPickupC best = null;
            if (shelves == null) return null;
            foreach (var shelf in shelves)
            {
                if (shelf == null) continue;
                foreach (Transform child in shelf)
                {
                    var p = child.GetComponent<ObjectPickupC>();
                    if (p == null || !p.gameObject.activeInHierarchy || p.returnPosition != shelf || p.isPurchased ||
                        p.isInShoppingKart || p.isTweenTransition || p.buyValue <= 0 || !Matches(p, kind)) continue;
                    if (best == null || p.buyValue < best.buyValue) best = p;
                }
            }
            return best;
        }

        // Try alternate placements so the oil bottle cannot block the last can-sized slot.
        static bool FitBasket(InventoryLogicC boot, List<SupplyOffer> items, int index, HashSet<GameObject> reserved)
        {
            if (index == items.Count) return true;
            var offer = items[index];
            if (offer.owned) return FitBasket(boot, items, index + 1, reserved);
            var p = offer.item;
            bool column = p.dimensionX == 1 && p.dimensionY == 2 && p.dimensionZ == 1;
            var locations = column ? boot.inventorySlots :
                (p.dimensionX == 2 && p.dimensionY == 2 && p.dimensionZ == 3 ? boot.slot2x2x3 : null);
            if (locations == null) return false;
            foreach (var location in locations)
            {
                if (location == null || location.childCount != 0) continue;
                var slot = location.GetComponent<InventoryRelayC>();
                if (slot == null) continue;
                var cells = column ? new[] { slot.gameObject, slot.spaceAbove } : slot.slots;
                if (cells == null || cells.Length != p.dimensionX * p.dimensionY * p.dimensionZ ||
                    !Array.TrueForAll(cells, c => FreeShopCell(c) && !reserved.Contains(c))) continue;
                foreach (var c in cells) reserved.Add(c);
                if (FitBasket(boot, items, index + 1, reserved)) { offer.slot = slot; return true; }
                foreach (var c in cells) reserved.Remove(c);
            }
            return false;
        }

        static bool FreeShopCell(GameObject cell)
        {
            var relay = cell != null ? cell.GetComponent<InventoryRelayC>() : null;
            return relay != null && !relay.isOccupied;
        }

        static string AddSupply(Purchase plan, Supply kind)
        {
            var upgrades = plan.car.GetComponent<ExtraUpgradesC>();
            if (kind == Supply.Kit && upgrades != null && upgrades.toolRackInstalled) return "already stocked";
            var item = OwnedSupply(plan.car, kind);
            bool owned = item != null;
            if (owned && (kind != Supply.Fuel || item.GetComponent<PortableFuelC>().fuelLevel >= 9))
                return "already stocked";
            var boot = plan.car.bootInventory.GetComponent<InventoryLogicC>();
            if (kind == Supply.Fuel)
            {
                var handle = plan.till.fuelHandle;
                if (handle == null || handle.GetComponent<RefuelLogicC>() == null) return "no pump";
            }
            if (!owned) item = ShelfItem(plan.store, kind);
            if (item == null) return "out of stock";
            float cash = plan.wallet.TotalWealth - plan.total;
            float price = owned ? 0 : item.buyValue;
            int litres = 0;
            if (kind == Supply.Fuel)
            {
                litres = ShoppingRules.FuelLitres(cash, price, plan.nextRoomPrice, item.GetComponent<PortableFuelC>().fuelLevel);
                if (litres == 0) return "no refill in budget";
                price += litres; // RefuelLogicC adds one currency unit per litre to the till.
            }
            if (!ShoppingRules.Affordable(cash, price, plan.nextRoomPrice)) return "over budget";
            var offer = new SupplyOffer { kind = kind, item = item, owned = owned, litres = litres };
            plan.items.Add(offer);
            if (!FitBasket(boot, plan.items, 0, new HashSet<GameObject>()))
            {
                plan.items.RemoveAt(plan.items.Count - 1);
                return "no space";
            }
            plan.total += price;
            return price.ToString("0.##");
        }

        static int NextRoomPrice(RouteGeneratorC route)
        {
            if (route == null || route.routeLevel < 0 || route.routeLevel > 6) return -1;
            int level = route.routeLevel;
            // SpawnContinueC chooses the neighbouring hub. Istanbul only has a return leg.
            int destination = level + (route.drivingTowardsHome || level == 6 ? -1 : 1);
            GameObject[] layouts;
            switch (destination)
            {
                case 0: return 0; // At home the player sleeps for free.
                case 1: layouts = route.destinationObjectsGer; break;
                case 2: layouts = route.destinationObjectsCSFR; break;
                case 3: layouts = route.destinationObjectsHUN; break;
                case 4: layouts = route.destinationObjectsYUGO; break;
                case 5: layouts = route.destinationObjectsBUL; break;
                case 6: layouts = route.destinationObjectsTURK; break;
                default: return -1;
            }
            if (layouts == null || layouts.Length == 0) return -1;
            // The route may still be unselected at wake. Cover every possible layout for that town.
            int basePrice = 0;
            foreach (var layout in layouts)
            {
                var hub = layout != null ? layout.GetComponent<Hub_CitySpawnC>() : null;
                var parent = hub != null && hub.motel != null ? hub.motel.GetComponent<MotelParentC>() : null;
                var room = parent != null && parent.motelLogic != null ? parent.motelLogic.GetComponent<MotelLogicC>() : null;
                if (room == null || room.roomPrice <= 0) return -1;
                basePrice = Math.Max(basePrice, room.roomPrice);
            }
            // BedLogicC already advanced routeLevel before wake. SetMotelPrice uses that same
            // level on arrival, before the player sleeps again.
            return basePrice + 5 * Math.Max(0, level - 1);
        }

        string ShopPlan(MotelLogicC motel, out Purchase plan)
        {
            plan = null;
            if (!CharFeatures.Has("shopping")) return "Select Nice Nature in Model and restart.";
            if (shopPaidToday) return "Already shopped this morning.";
            var u = OutU();
            if (u == null || u.uncleAtHome || u.uncleGoneForever || u.carLogic == null) return "Companion / car unavailable.";
            var car = u.carLogic.GetComponent<CarLogicC>();
            var hub = motel == null ? null : motel.GetComponentInParent<Hub_CitySpawnC>();
            if (hub == null || motel.parkingTrigger == null || car == null || car.bootInventory == null || car.bootStandingLoc == null) return "Park at a town motel.";
            if (Vector3.Distance(car.bootStandingLoc.position, motel.parkingTrigger.transform.position) > 55f * MornMetre)
                return "Car is away from the motel.";
            StoreC store = null;
            foreach (var s in hub.GetComponentsInChildren<StoreC>())
                if (s.shop != null && s.transform.parent != null && s.transform.parent.GetComponent<Hub_BuildingSpawnC>() != null)
                { store = s; break; }
            var till = store != null ? store.shop.GetComponent<ShopC>() : null;
            var wallet = till != null && till.wallet != null ? till.wallet.GetComponent<WalletC>() : null;
            if (wallet == null || wallet.director == null || wallet.director.GetComponent<DirectorC>() == null ||
                car.bootInventory.GetComponent<InventoryLogicC>() == null) return "Market is not ready.";
            int nextRoomPrice = NextRoomPrice(wallet.director.GetComponent<RouteGeneratorC>());
            if (nextRoomPrice < 0) return "Next overnight price unavailable.";
            var e = new Purchase { nextRoomPrice = nextRoomPrice, car = car, store = store, till = till, wallet = wallet };
            if (wallet.incomingFunds != 0 || till.totalPrice != 0 || till.shoppingKart.Count != 0 ||
                store.unPaidCatalogue.Count != 0 || till.inputRestrictDropItem || till.fuelUsed != 0 ||
                till.portableFuelRefilledList.Count != 0) return "Skip: player checkout in progress.";
            var details = new List<string>();
            foreach (var kind in new[] { Supply.Kit, Supply.Oil, Supply.Fuel })
                details.Add(kind + ": " + AddSupply(e, kind));
            if (e.items.Count > 0) plan = e;
            return "cash " + wallet.TotalWealth + ", next room " + nextRoomPrice + " / " +
                string.Join(", ", details.ToArray()) + " / total " + e.total;
        }

        internal string ShopPreview(MotelLogicC motel)
        {
            return ShopPlan(motel, out _);
        }

        internal string BuyMorningSupplies(MotelLogicC motel)
        {
            string plan = ShopPlan(motel, out var e);
            if (e == null) return ReportPurchase(plan);
            var director = e.wallet.director.GetComponent<DirectorC>();
            var inv = e.car.bootInventory.GetComponent<InventoryLogicC>();
            var names = new List<string>();
            int fuelLitres = 0;
            bool refilledOwnCan = false;
            shopPaidToday = true;
            foreach (var offer in e.items)
            {
                if (!offer.owned)
                {
                    offer.item.isPurchased = true;
                    StowPurchase(inv, offer.item, offer.slot);
                    director.eventLogPurchasedObj.Add(offer.item.gameObject);
                }
                if (offer.litres > 0)
                {
                    var can = offer.item.GetComponent<PortableFuelC>();
                    for (int i = 0; i < offer.litres; i++)
                    {
                        can.fuelLevel++;
                        if (--can.fuelTillOilDown <= 0) can.FuelOilDown();
                    }
                    can.FuelUpdate();
                    fuelLitres = offer.litres;
                    refilledOwnCan = offer.owned;
                }
                if (offer.owned) continue; // Mention the refill separately from items placed in the boot.
                var key = offer.kind == Supply.Fuel ? "can" : offer.kind.ToString().ToLowerInvariant();
                names.Add(remarks?.Pick("shop_" + key) ?? key);
            }
            e.till.shopCash += e.total;
            e.wallet.incomingFunds -= e.total;
            e.wallet.ChangeMoneyPart2();
            if (director.eventLogPurchasedObj.Count > 0) director.UpdateEventLogPurchased();
            if (fuelLitres > 0) director.EventLogPurchasedFuel(fuelLitres, fuelLitres);
            string list = names.Count > 0 ? names[0] : "";
            for (int i = 1; i < names.Count; i++)
            {
                string join = i == names.Count - 1 ? " " + (remarks?.Pick("shop_and") ?? "and") + " " : ", ";
                list += join + names[i];
            }
            string template;
            if (!refilledOwnCan) template = remarks?.Pick("shop_boot") ?? "{0} in the boot. Total: {1}.";
            else if (names.Count == 0) template = remarks?.Pick("shop_refill") ?? "I topped up our petrol can. It cost {1}.";
            else template = remarks?.Pick("shop_boot_refill") ?? "I put {0} in the boot and topped up our petrol can. Total: {1}.";
            shopLine = template.Replace("{0}", list).Replace("{1}", e.total.ToString("0.##"));
            return ReportPurchase("delivered to boot / " + plan);
        }

        static void StowPurchase(InventoryLogicC inv, ObjectPickupC p, InventoryRelayC cell)
        {
            var body = p.GetComponent<Rigidbody>();
            p.transform.SetParent(cell.transform, false);
            p.transform.localPosition = p.inventoryAdjustPosition;
            p.transform.localEulerAngles = p.inventoryAdjustRotation;
            p.inventoryPlacedAt = cell.transform;
            if (p.dimensionX == 1)
            {
                cell.isOccupied = true;
                cell.spaceAbove.GetComponent<InventoryRelayC>().isOccupied = true;
            }
            else cell.Occupy();
            if (body != null) Destroy(body);
            foreach (var t in p.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            foreach (var c in p.GetComponents<Collider>()) { c.enabled = true; c.isTrigger = true; }
            inv.SendMessage("ModifyWeight", p.transform);
            p.SendMessage("IntoInventory");
            inv.AvailableSlotCount();
            inv.UpdateInventory();
        }

        string ReportPurchase(string result)
        {
            shopResult = result;
            Debug.Log("[UncleSwap] Shopping: " + result);
            return result;
        }

        void ShopReceipt()
        {
            if (shopLine == null || !InCar() || remarks == null) return;
            var u = OutU();
            if (u == null || !u.isSat || u.player == null || u.carLogic == null) return;
            var car = u.carLogic.GetComponent<CarLogicC>();
            var seat = car != null && car.playerSeat != null ? car.playerSeat.GetComponent<SeatLogicC>() : null;
            // Trigger sets isSat at the start of entry. RestoreControl attaches the player after the tween.
            // Pushing the car also attaches the player, but does not occupy the driver's seat.
            if (seat == null || !seat.isSat || seat.seatLoc == null || u.player.transform.parent != seat.seatLoc) return;
            if (!remarks.CanSay(u)) return;
            StartCoroutine(remarks.Say(u, shopLine));
            if (remarks.Busy) shopLine = null;
        }
    }
}
