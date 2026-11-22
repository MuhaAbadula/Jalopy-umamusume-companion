using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using JaLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UmamusumeCompanion
{
    internal static class PaintStock
    {
        static readonly HashSet<string> Names = new HashSet<string> {
            "Fukukitaru 07", "Daikichi After Dark", "Tachyon Spectrum",
            "Taishin Last Spurt", "Tosho Witching Hour"
        };
        static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

        internal static void Install()
        {
            var patch = new Harmony("jalopy.umacommands.paintstock");
            var page = typeof(EngineComponentsCataloguePageC);
            patch.Patch(AccessTools.Method(typeof(ModHelper), "CreatePaintJobBox"),
                postfix: Hook(nameof(UseRegularCatalogue)));
            patch.Patch(AccessTools.Method(page, "SelectPaintDecals"), prefix: Hook(nameof(AddCandidates)));
            patch.Patch(AccessTools.Method(page, "PaintDecalsGo"), postfix: Hook(nameof(ShowOriginalColours)));
            patch.Patch(AccessTools.Method(page, "SpawnGo"), transpiler: Hook(nameof(RouteBoxDelivery)));
        }

        static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(PaintStock), name);

        static void UseRegularCatalogue(string name, GameObject __result)
        {
            if (__result == null || !Names.Contains(name)) return;
            __result.GetComponent<ObjectIdentification>().CanBuyInDealership = false;
            __result.GetComponent<CustomObjectInfo>().CanBuyInDealership = false;
            var engine = __result.GetComponent<EngineComponentC>();
            engine.flavourTextDetailed = engine.flavourText;
        }

        static void AddCandidates(EngineComponentsCataloguePageC __instance)
        {
            var boxes = __instance.prefabPaintDecals;
            var sprites = __instance.prefabPaintDecalsSpritesLibrary;
            var jobs = PaintJobManager.Instance;
            var registry = CustomObjectsManager.Instance;
            if (jobs == null || registry == null || sprites.Count == 0 || sprites.Count != boxes.Count) return;
            var sample = sprites.Find(s => s != null);
            if (sample == null) return;
            float width = sample.rect.width / sample.pixelsPerUnit;
            foreach (var job in jobs.PaintJobs)
            {
                if (!Names.Contains(job.Name) || job.PreviewIcon == null) continue;
                var box = registry.GetObject(job.Name);
                if (box == null || boxes.Contains(box) || __instance.prefabPaintDecalsSelected.Contains(box)) continue;
                Sprite sprite;
                var tex = job.PreviewIcon;
                if (!Icons.TryGetValue(job.Name, out sprite) || sprite == null || sprite.texture != tex)
                {
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                        new Vector2(.5f, .5f), tex.width / width);
                    Icons[job.Name] = sprite;
                }
                boxes.Add(box);
                sprites.Add(sprite);
            }
        }

        static void ShowOriginalColours(EngineComponentsCataloguePageC __instance)
        {
            var targets = new[] { __instance.componentTarget1, __instance.componentTarget2,
                                  __instance.componentTarget3, __instance.componentTarget4 };
            for (int i = 0; i < targets.Length; i++)
            {
                var info = __instance.prefabPaintDecalsSelected[i].GetComponent<CustomObjectInfo>();
                if (info != null && Names.Contains(info.objName))
                    targets[i].GetComponent<SpriteRenderer>().color = Color.white;
            }
        }

        static IEnumerable<CodeInstruction> RouteBoxDelivery(IEnumerable<CodeInstruction> instructions)
        {
            // Vanilla clones active prefabs. JaLoader keeps its registered templates inactive.
            Func<GameObject, Vector3, Quaternion, GameObject> instantiate = Object.Instantiate<GameObject>;
            foreach (var op in instructions)
            {
                if (op.Calls(instantiate.Method))
                    op.operand = AccessTools.Method(typeof(PaintStock), nameof(Deliver));
                yield return op;
            }
        }

        static GameObject Deliver(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            var info = prefab.GetComponent<CustomObjectInfo>();
            if (info != null && Names.Contains(info.objName) &&
                CustomObjectsManager.Instance.GetObject(info.objName) == prefab)
                return CustomObjectsManager.Instance.SpawnObject(info.objName, position, rotation);
            return Object.Instantiate(prefab, position, rotation);
        }
    }
}
