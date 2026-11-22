using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Feeds BootTalk: the player clicking an item into the car's boot or roof rack (InventoryLogicC.Trigger),
    /// paying at a shop (ShopC.Payed: kart items not purchased before) and ordering from the Laika catalogue
    /// (EngineComponentsCataloguePageC.SendOrderOut, only called after the money is taken).
    /// </summary>
    public static class BootPatch
    {
        static bool applied;

        public static void Apply()
        {
            if (applied) return;
            var h = new Harmony("osobl.jalopy.umamusumecompanion.boot");
            h.Patch(AccessTools.Method(typeof(InventoryLogicC), "Trigger"), prefix: new HarmonyMethod(typeof(BootPatch), nameof(Store)));
            h.Patch(AccessTools.Method(typeof(ShopC), "Payed"), prefix: new HarmonyMethod(typeof(BootPatch), nameof(Paid)));
            var order = AccessTools.Method(typeof(EngineComponentsCataloguePageC), "SendOrderOut");
            if (order != null) h.Patch(order, prefix: new HarmonyMethod(typeof(BootPatch), nameof(Ordered)));
            applied = true;
        }

        static Life L { get { return Life.Current != null && Life.Current.Enabled ? Life.Current : null; } }

        static CarLogicC car;

        static void Store(InventoryLogicC __instance)
        {
            if (L == null || __instance == null) return;
            if (car == null)
            {
                var go = GameObject.FindWithTag("CarLogic");
                if (go != null) car = go.GetComponent<CarLogicC>();
            }
            if (car == null) return;
            bool boot = car.bootInventory != null && __instance.gameObject == car.bootInventory;
            bool roof = car.roofInventory != null && __instance.gameObject == car.roofInventory;
            if (!boot && !roof) return;   // a crate's own inside, the motel shelf...
            var cam = Camera.main;
            var drag = cam != null ? cam.GetComponent<DragRigidbodyC>() : null;
            if (drag == null || drag.isHolding1 == null) return;
            L.OnStoring(drag.isHolding1, __instance.transform);
        }

        static void Paid(ShopC __instance)
        {
            if (L == null || __instance == null || __instance.shoppingKart == null) return;
            foreach (var o in __instance.shoppingKart)
            {
                if (o == null) continue;
                var p = o.GetComponent<ObjectPickupC>();
                if (p != null && !p.isPurchased) L.MarkBought(o);   // purchased already = he is selling it
            }
        }

        static void Ordered(EngineComponentsCataloguePageC __instance)
        {
            if (L == null || __instance == null || __instance.shoppingList == null) return;
            foreach (var o in __instance.shoppingList)
                if (o != null) L.MarkOrdered(o.name);
            Debug.Log("[Umamusume companion] event: Laika order, " + __instance.shoppingList.Count + " parts");
        }
    }
}
