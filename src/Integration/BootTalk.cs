using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// v0.22: she comments on what the player puts into the boot or on the roof rack (goods, crates by contents,
    /// things just bought) and is glad about new parts for the car (the Laika).
    /// Not tiresome: items placed close together are one batch with one remark at most, 60 s between boot remarks,
    /// a cooldown per kind, and every time a kind is said its chance drops (x0.65, down to a floor).
    /// Faces happen every time, words only now and then.
    /// </summary>
    public partial class Life
    {
        class Kind { public string Key, Face; public float Chance, Floor, Cool, FaceDur; public int Rank, Said; }

        // Rank: the best item of a batch wins
        static readonly Dictionary<string, Kind> Kinds = new Dictionary<string, Kind>
        {
            { "boot_laika",         new Kind { Key = "boot_laika",         Rank = 9, Chance = 1f,   Floor = 0.5f,  Cool = 120f, Face = "happy",   FaceDur = 2.5f } },
            // "munitions" is only the code name: the game's crates of that kind hold meat and sausages (Meat(Municians), Sausage)
            { "boot_box_meat",      new Kind { Key = "boot_box_meat",      Rank = 6, Chance = 0.7f, Floor = 0.15f, Cool = 300f, Face = "happy",   FaceDur = 1.8f } },
            { "boot_box_alcohol",   new Kind { Key = "boot_box_alcohol",   Rank = 6, Chance = 0.7f, Floor = 0.15f, Cool = 300f, Face = "smug",    FaceDur = 1.8f } },
            { "boot_box_tobacco",   new Kind { Key = "boot_box_tobacco",   Rank = 6, Chance = 0.7f, Floor = 0.15f, Cool = 300f, Face = "annoyed", FaceDur = 1.5f } },
            { "boot_box_pharma",    new Kind { Key = "boot_box_pharma",    Rank = 6, Chance = 0.7f, Floor = 0.15f, Cool = 300f, Face = "focused", FaceDur = 1.5f } },
            { "boot_box_textiles",  new Kind { Key = "boot_box_textiles",  Rank = 6, Chance = 0.7f, Floor = 0.15f, Cool = 300f, Face = "smile",   FaceDur = 1.5f } },
            { "boot_box_parts",     new Kind { Key = "boot_box_parts",     Rank = 6, Chance = 0.7f, Floor = 0.15f, Cool = 300f, Face = "focused", FaceDur = 1.5f } },
            { "boot_box",           new Kind { Key = "boot_box",           Rank = 5, Chance = 0.6f, Floor = 0.15f, Cool = 300f, Face = "smile",   FaceDur = 1.5f } },
            { "boot_goods",         new Kind { Key = "boot_goods",         Rank = 4, Chance = 0.6f, Floor = 0.15f, Cool = 300f, Face = "smile",   FaceDur = 1.5f } },
            { "boot_bought",        new Kind { Key = "boot_bought",        Rank = 3, Chance = 0.35f, Floor = 0.1f, Cool = 400f, Face = "smile",   FaceDur = 1.5f } },
        };

        class Stored { public GameObject Obj; public Transform Storage; public float CheckAt; }
        readonly List<Stored> storedPending = new List<Stored>();
        readonly Dictionary<int, float> boughtAt = new Dictionary<int, float>();    // instance id -> time paid
        readonly Dictionary<string, float> orderedAt = new Dictionary<string, float>(); // Laika catalogue prefab name -> time
        Kind batchBest;
        int batchCount;
        float batchUntil = -1f, nextBootTalk, batchFarSince = -1f;
        const float NearArms = 7f;   // boot to her seat is a few arm lengths; tune from the log

        /// <summary>Player camera to her head, in her arm lengths (the game's units are not metres).</summary>
        float PlayerDistanceArms()
        {
            var cam = Camera.main;
            if (cam == null || head == null || armL == null) return float.MaxValue;
            float arm = Mathf.Max(1e-4f, ArmLen(armL));
            return Vector3.Distance(cam.transform.position, head.T.position) / arm;
        }

        /// <summary>The player paid at a shop for these (not yet purchased) things.</summary>
        public void MarkBought(GameObject obj)
        {
            if (obj != null) boughtAt[obj.GetInstanceID()] = Time.time;
        }

        /// <summary>Parts ordered from the Laika catalogue (they appear outside as new objects, "(Clone)").</summary>
        public void MarkOrdered(string prefabName)
        {
            if (!string.IsNullOrEmpty(prefabName)) orderedAt[prefabName] = Time.time;
        }

        /// <summary>The player clicked an item into the boot or the roof rack; checked a moment later (it may not fit).</summary>
        public void OnStoring(GameObject obj, Transform storage)
        {
            if (obj == null || storage == null) return;
            storedPending.Add(new Stored { Obj = obj, Storage = storage, CheckAt = Time.time + 0.7f });
        }

        static string BaseName(string n)
        {
            int i = n.IndexOf("(Clone)");
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }

        bool WasBought(GameObject o, float now)
        {
            float t;
            if (boughtAt.TryGetValue(o.GetInstanceID(), out t) && now - t < 1800f) return true;
            return orderedAt.TryGetValue(BaseName(o.name), out t) && now - t < 1800f;
        }

        string BootKind(GameObject o, float now)
        {
            bool part = o.GetComponent<EngineComponentC>() != null || o.GetComponent<ExtraComponentC>() != null;
            bool bought = WasBought(o, now);
            if (part && bought) return "boot_laika";
            var box = o.GetComponent<BoxContentsC>();
            if (box != null)
            {
                if (box.munitions) return "boot_box_meat";
                if (box.alchol) return "boot_box_alcohol";
                if (box.tobacco) return "boot_box_tobacco";
                if (box.pharma) return "boot_box_pharma";
                if (box.textiles) return "boot_box_textiles";
                if (box.machineParts) return "boot_box_parts";
                return "boot_box";
            }
            var pick = o.GetComponent<ObjectPickupC>();
            if (pick != null && pick.isTradeGood) return "boot_goods";
            if (bought) return "boot_bought";
            return null; // moving her own stuff around, scrap finds...: nothing to say
        }

        /// <summary>From Update: settle pending items, then talk once per batch.</summary>
        void BootLayer(float now)
        {
            for (int i = storedPending.Count - 1; i >= 0; i--)
            {
                var s = storedPending[i];
                if (now < s.CheckAt) continue;
                storedPending.RemoveAt(i);
                if (s.Obj == null || !s.Obj.transform.IsChildOf(s.Storage))
                {
                    Debug.Log("[Umamusume companion] boot: " + (s.Obj != null ? s.Obj.name + " is not inside (did not fit?)" : "item gone"));
                    continue;
                }
                string k = BootKind(s.Obj, now);
                Debug.Log("[Umamusume companion] boot: " + s.Obj.name + " -> " + (k ?? "nothing to say"));
                if (k == null) continue;
                var kind = Kinds[k];
                if (batchBest == null || kind.Rank > batchBest.Rank) batchBest = kind;
                batchCount++;
                batchUntil = now + 2.5f;   // wait until he stops loading
            }
            if (batchBest == null || now < batchUntil || storedPending.Count > 0) return;
            // only with the player beside her (user): a far batch waits for him to come back, up to 3 minutes
            float near = PlayerDistanceArms();
            if (near > NearArms)
            {
                if (batchFarSince < 0f)
                {
                    batchFarSince = now;
                    Debug.Log("[Umamusume companion] boot: player " + near.ToString("F1") + " arm lengths away, waiting for him (" + batchBest.Key + ")");
                }
                if (now - batchFarSince > 180f) { batchBest = null; batchCount = 0; batchFarSince = -1f; }
                return;
            }
            batchFarSince = -1f;
            var best = batchBest;
            batchBest = null;
            batchCount = 0;
            if (!Enabled || asleepNow) return;
            Debug.Log("[Umamusume companion] boot: player " + near.ToString("F1") + " arm lengths away, " + best.Key);

            if (!talkingNow) Emote(best.Face, best.FaceDur, 0.85f);
            if (best.Key == "boot_laika" && inCarNow) Attend(Look.Player, 2f, 0.9f);
            if (talkingNow || now < nextBootTalk) { Debug.Log("[Umamusume companion] boot remark held back (" + best.Key + ")"); return; }
            float chance = Mathf.Max(best.Floor, best.Chance * Mathf.Pow(0.65f, best.Said));
            float until;
            if (sayCool.TryGetValue(best.Key, out until) && now < until) return;
            if (Random.value > chance) { Debug.Log("[Umamusume companion] boot remark rolled away (" + best.Key + ", " + chance.ToString("F2") + ")"); return; }
            best.Said++;
            nextBootTalk = now + 60f;
            sayCool[best.Key] = now + best.Cool;
            Debug.Log("[Umamusume companion] remark: " + best.Key);
            if (Say != null) Say(best.Key);
        }
    }
}
