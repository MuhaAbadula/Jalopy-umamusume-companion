using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 20 (concept 04, E1 + E2): shop prices follow the day's luck.
    /// Shelf items (not purchased, not trade goods): ObjectPickupC.SetPriceTag prefix sets buyValue = game price x
    /// LuckFx.ShopMult() before the game writes the tag, so tag, inspector, cart total and charge all use it. The game
    /// price is kept per instance (never compounds); the price never drops below the item's full-condition sell value.
    /// Crates (trade goods the player owns): SetPriceTag postfix scales sellValue by LuckFx.CrateSellMult(), capped at
    /// this shop's buy price of the same goods. Crate buy prices, fuel and the Laika catalogue are the game's.
    /// When the multipliers change (new day, Ctrl+F6) the tags are refreshed once no shop cart holds anything.
    /// </summary>
    public static class LuckShop
    {
        static bool applied;

        class Shelf { public float game; public float set; public int store; }
        static readonly Dictionary<ObjectPickupC, Shelf> shelf = new Dictionary<ObjectPickupC, Shelf>();
        static readonly Dictionary<ObjectPickupC, float> crateGame = new Dictionary<ObjectPickupC, float>();
        static readonly HashSet<string> logged = new HashSet<string>();
        static float appliedShop = 1f, appliedCrate = 1f, nextTick;
        static FieldInfo curObj = AccessTools.Field(typeof(ShopC), "currentlyProcessedObject");

        public static void Apply()
        {
            if (applied) return;
            applied = true;
            var h = new Harmony("osobl.jalopy.uncleswap.luckshop");
            h.Patch(AccessTools.Method(typeof(ObjectPickupC), "SetPriceTag"),
                prefix: new HarmonyMethod(typeof(LuckShop), nameof(TagPrefix)),
                postfix: new HarmonyMethod(typeof(LuckShop), nameof(TagPostfix)));
            h.Patch(AccessTools.Method(typeof(ShopC), "PlayerTriggerEnter"), postfix: new HarmonyMethod(typeof(LuckShop), nameof(Enter)));
            h.Patch(AccessTools.Method(typeof(ShopC), "Trigger"), prefix: new HarmonyMethod(typeof(LuckShop), nameof(Counter)));
            h.Patch(AccessTools.Method(typeof(ShopC), "PlaceObjectIntoShoppingBagConvertToMoney"),
                prefix: new HarmonyMethod(typeof(LuckShop), nameof(SoldPrefix)));
            h.Patch(AccessTools.Method(typeof(ShopC), "Update"), postfix: new HarmonyMethod(typeof(LuckShop), nameof(Tick)));
            Debug.Log("[UncleSwap] LuckFx: Shop patches on");
        }

        // ------------------------------------------------------------ shelf items

        static void TagPrefix(ObjectPickupC __instance)
        {
            try
            {
                var o = __instance;
                if (o == null || o.isPurchased || o.isTradeGood) return;
                Shelf s;
                if (!shelf.TryGetValue(o, out s))
                {
                    var st = o.GetComponentInParent<StoreC>();
                    s = new Shelf { game = o.buyValue, set = o.buyValue, store = st != null ? st.GetInstanceID() : 0 };
                    shelf[o] = s;
                    if (shelf.Count > 400) Trim();
                }
                else if (o.buyValue != s.set) s.game = o.buyValue;   // the game set a new price itself
                if (o.isInShoppingKart) return;                       // already counted in the cart total: keep it
                float now = ShelfPrice(o, s.game);
                o.buyValue = now;
                s.set = now;
                if (now != s.game && logged.Add(s.store + "|" + Name(o) + "|" + s.game + "|" + now))
                    LuckFx.Log("Shop", Name(o) + " " + s.game, now.ToString());
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Shop tag failed: " + e.Message); }
        }

        static float ShelfPrice(ObjectPickupC o, float game)
        {
            float m = LuckFx.ShopMult();
            if (m == 1f || game <= 0f) return game;
            float v = game * m;
            v = game >= 10f ? Mathf.Round(v) : Mathf.Round(v * 10f) / 10f;
            if (m < 1f)
            {
                // no free money: never below what the shop pays back for it in full condition
                float floor = Mathf.Max(o.sellValue, o.originalSellValue);
                if (v < floor) v = Mathf.Min(game, floor);
            }
            return v;
        }

        // ------------------------------------------------------------ crates

        static void TagPostfix(ObjectPickupC __instance)
        {
            try
            {
                var o = __instance;
                if (o == null || !o.isTradeGood || !o.isPurchased) return;
                float game = o.sellValue;
                crateGame[o] = game;
                if (crateGame.Count > 200) Trim();
                float m = LuckFx.CrateSellMult();
                if (m == 1f || game <= 0f) return;
                float now = Mathf.Round(game * m * 10f) / 10f;
                // within one shop the sell price stays at or below its buy price of the same goods
                if (m > 1f && o.buyValue > 0f && now > o.buyValue) now = Mathf.Max(game, o.buyValue);
                if (now == game) return;
                o.sellValue = now;
                SellTag(o);
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Crates tag failed: " + e.Message); }
        }

        /// <summary>The game's sell tag format (ObjectPickupC.SetPriceTag).</summary>
        static void SellTag(ObjectPickupC o)
        {
            if (o.sellPriceObj == null) return;
            // TextMesh lives in UnityEngine.TextRenderingModule, which the project does not reference
            var t = o.sellPriceObj.GetComponent("TextMesh");
            if (t == null) return;
            if (textProp == null) textProp = t.GetType().GetProperty("text");
            if (textProp == null) return;
            float s = o.sellValue;
            string txt;
            if (s < 10f)
            {
                if (s < 0.1f && s > 0f) txt = "0" + s;
                else if (s % 1f != 0f) txt = "0" + s + "0";
                else txt = "0" + s + ".00";
            }
            else if (s % 1f != 0f) txt = s + "0";
            else txt = s + ".00";
            textProp.SetValue(t, txt, null);
        }

        static PropertyInfo textProp;

        /// <summary>Before the counter checks the held thing: crates in hand (or in a carried box) get today's price.</summary>
        static void Counter()
        {
            try
            {
                if (!EconomyReady() || Camera.main == null) return;
                var drag = Camera.main.GetComponent<DragRigidbodyC>();
                var held = drag != null ? drag.isHolding1 : null;
                if (held == null) return;
                foreach (var o in held.GetComponentsInChildren<ObjectPickupC>(true))
                    if (o.isTradeGood && o.isPurchased && !o.isInShoppingKart) o.SetPriceTag();
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Crates counter failed: " + e.Message); }
        }

        static void SoldPrefix(ShopC __instance)
        {
            try
            {
                var go = curObj != null ? curObj.GetValue(__instance) as GameObject : null;
                var o = go != null ? go.GetComponent<ObjectPickupC>() : null;
                if (o == null || !o.isTradeGood || !o.isPurchased) return;
                if (!LuckFx.On("Crates") || LuckFx.L == 0) return;
                float game;
                if (!crateGame.TryGetValue(o, out game)) game = o.sellValue;
                LuckFx.Log("Crates", Name(o) + " " + game, o.sellValue.ToString());
                if (LuckFx.L > 0 && o.sellValue > game) LuckFx.TrySay("luckfx_crate_good");
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Crates sale failed: " + e.Message); }
        }

        // ------------------------------------------------------------ remark, refresh

        static void Enter()
        {
            try
            {
                if (LuckFx.ShopRemarkDoneToday) return;
                int l = LuckFx.L;
                if (l == 0 || LuckFx.ShopMult() == 1f) return;
                if (LuckFx.TrySay(l > 0 ? "luckfx_shop_good" : "luckfx_shop_bad")) LuckFx.ShopRemarkDoneToday = true;
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Shop remark failed: " + e.Message); }
        }

        /// <summary>ShopC.Update (any loaded shop): twice a second, re-tag everything when a multiplier changed.</summary>
        static void Tick()
        {
            float t = Time.realtimeSinceStartup;
            if (t < nextTick) return;
            nextTick = t + 0.5f;
            try
            {
                float s = LuckFx.ShopMult(), c = LuckFx.CrateSellMult();
                if (s == appliedShop && c == appliedCrate) return;
                if (!EconomyReady()) return;
                foreach (var shop in UnityEngine.Object.FindObjectsOfType<ShopC>())
                    if (shop.shoppingKart != null && shop.shoppingKart.Count > 0) return;   // wait for an empty cart
                int ns = 0, nc = 0;
                foreach (var o in UnityEngine.Object.FindObjectsOfType<ObjectPickupC>())
                {
                    if (o.isInShoppingKart) continue;
                    if (s != appliedShop && !o.isPurchased && !o.isTradeGood && shelf.ContainsKey(o)) { o.SetPriceTag(); ns++; }
                    else if (c != appliedCrate && o.isTradeGood && o.isPurchased) { o.SetPriceTag(); nc++; }
                }
                Debug.Log("[UncleSwap] LuckFx: Shop L=" + LuckFx.L + " re-tagged " + ns + " shelf items (x" + s + "), " + nc + " crates (x" + c + ")");
                appliedShop = s;
                appliedCrate = c;
            }
            catch (Exception e) { nextTick = t + 5f; Debug.LogWarning("[UncleSwap] LuckFx: Shop refresh failed: " + e.Message); }
        }

        static bool EconomyReady()
        {
            if (MainMenuC.Global == null || MainMenuC.Global.director == null) return false;
            var d = MainMenuC.Global.director.GetComponent<DirectorC>();
            return d != null && d.economyReady;
        }

        static string Name(ObjectPickupC o)
        {
            return o.gameObject.name.Replace("(Clone)", "").Trim();
        }

        static void Trim()
        {
            var dead = new List<ObjectPickupC>();
            foreach (var k in shelf.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) shelf.Remove(k);
            dead.Clear();
            foreach (var k in crateGame.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) crateGame.Remove(k);
            if (logged.Count > 2000) logged.Clear();
        }
    }

    public partial class FukuDriver
    {
        // Unity calls it once after Build (OnEnable is LuckWear's hook): applies the shop patches without touching FukuUncle.cs
        void Start()
        {
            try { LuckShop.Apply(); }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] LuckFx: Shop patch failed: " + e.Message); }
        }
    }
}
