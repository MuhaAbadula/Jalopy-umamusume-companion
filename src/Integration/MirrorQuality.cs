using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 40 bug 3: sharper rear-view mirror. The game's MirrorReflection renders into a square texture (128-1024 px by the
    /// game's mirror setting, MirrorReflection.qualityValue 1-4) that is projected over the whole screen (_ProjMatrix = the main
    /// camera's projection), so the small glass shows only a few dozen texels across, and it redraws every 2-6 frames
    /// (frameCount). Setting "Mirror quality" = High, for RV_Mirror_* only (door mirrors untouched): a texture the size of the
    /// screen, 24-bit depth, bilinear, MSAA x4 when the camera renders forward, redrawn every frame (qualityValue is 4 for the
    /// length of that one call, then back). "Game default" puts a game-style texture back. Log: [UncleSwap] mirror: ...
    /// </summary>
    public static class MirrorQuality
    {
        /// <summary>Setting "Mirror quality": 0 = as in the game, 1 = high.</summary>
        public static int Level = 1;

        static bool applied;
        static AccessTools.FieldRef<MirrorReflection, RenderTexture> texRef;
        const string Mark = "__FukuMirrorHigh";

        public static void Apply()
        {
            if (applied) return;
            texRef = AccessTools.FieldRefAccess<MirrorReflection, RenderTexture>("m_ReflectionTexture");
            var h = new Harmony("osobl.jalopy.uncleswap.mirror");
            h.Patch(AccessTools.Method(typeof(MirrorReflection), "OnWillRenderObject"),
                prefix: new HarmonyMethod(typeof(MirrorQuality), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(MirrorQuality), nameof(Finalizer)),
                transpiler: new HarmonyMethod(typeof(MirrorQuality), nameof(Transpiler)));
            applied = true;
            Debug.Log("[UncleSwap] mirror: patch on, quality " + (Level > 0 ? "high" : "game default") + " (game mirror setting " + MirrorReflection.qualityValue + ")");
        }

        static bool IsRearView(MirrorReflection m)
        {
            var p = m.transform.parent;
            return p != null && p.name.StartsWith("RV_Mirror");
        }

        // ---- bug 6 (v0.42.8): the game builds an oblique projection for its reflection camera and then overwrites it with the
        // plain one, so everything between the reflected eye and the glass (the mirror's own housing, the car key collider...)
        // is drawn over the reflection. The transpiler sends that second projectionMatrix set through SetProjection, which puts
        // an oblique near plane on the glass (2 cm towards the viewer) for the rear-view mirror.
        /// <summary>Oblique clip at the glass. Off since v0.42.9: the user says nothing covers the picture (the brown in the glass
        /// was the tables behind her: the aim was off, not an occluder). The transpiler stays as a plain pass-through.</summary>
        public static bool ClipAtGlass = false;
        static bool clipNow;
        static Vector3 clipPoint, clipNormal;
        static int transpiled;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var setProj = AccessTools.PropertySetter(typeof(Camera), "projectionMatrix");
            var mine = AccessTools.Method(typeof(MirrorQuality), nameof(SetProjection));
            int seen = 0;
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && Equals(ci.operand, setProj))
                {
                    seen++;
                    if (seen == 2) { transpiled++; yield return new CodeInstruction(OpCodes.Call, mine); continue; }
                }
                yield return ci;
            }
        }

        /// <summary>The game's second (plain) projection set; for the RV mirror with clipping on, an oblique near plane at the glass.</summary>
        static void SetProjection(Camera rc, Matrix4x4 m)
        {
            rc.projectionMatrix = m;
            if (!clipNow || rc == null) return;
            try
            {
                Matrix4x4 w2c = rc.worldToCameraMatrix;
                Vector3 p = w2c.MultiplyPoint(clipPoint);
                Vector3 nc = w2c.MultiplyVector(clipNormal).normalized;
                rc.projectionMatrix = rc.CalculateObliqueMatrix(new Vector4(nc.x, nc.y, nc.z, -Vector3.Dot(p, nc)));
            }
            catch (Exception) { rc.projectionMatrix = m; }
        }

        static bool clipLogged;

        // ---- bug 8 (v0.42.11) "a strip along the top": the housing RV_Mirror_001 has a face 0.002 u from the glass quad (cab dump,
        // tools/mirror_dump.py), so they z-fight and the housing shows through in dotted strips along the glass edges. The glass
        // quad (the Mirror object) is moved 0.01 u (4 mm) towards the viewer, once per mirror; back when the setting goes to
        // game default.
        const float Lift = 0.01f;
        static readonly System.Collections.Generic.Dictionary<int, Vector3> lifted = new System.Collections.Generic.Dictionary<int, Vector3>();

        static void LiftGlass(MirrorReflection m, Camera cur, Renderer rr)
        {
            int id = m.GetInstanceID();
            Vector3 home;
            bool done = lifted.TryGetValue(id, out home);
            if (Level <= 0)
            {
                if (done) { m.transform.localPosition = home; lifted.Remove(id); }
                return;
            }
            if (done || cur == null || rr == null || cur != Camera.main) return;   // not from another mirror's camera
            Vector3 up = m.transform.up;
            Vector3 toViewer = Vector3.Dot(up, cur.transform.position - rr.bounds.center) >= 0f ? up : -up;
            lifted[id] = m.transform.localPosition;
            m.transform.position += toViewer * Lift;
            Debug.Log("[UncleSwap] mirror: " + m.transform.parent.name + " glass moved " + Lift + " u towards the viewer (z-fight with the housing)");
        }

        static void Prefix(MirrorReflection __instance, out int __state)
        {
            __state = -1;
            clipNow = false;
            try
            {
                if (texRef == null || !IsRearView(__instance)) return;
                var cur = Camera.current;
                var rr = __instance.GetComponent<Renderer>();
                LiftGlass(__instance, cur, rr);
                if (ClipAtGlass && Level > 0 && cur != null && rr != null)
                {
                    Vector3 g = rr.bounds.center, up = __instance.transform.up;
                    clipNormal = Vector3.Dot(up, cur.transform.position - g) >= 0f ? up : -up;   // towards the viewer
                    clipPoint = g + clipNormal * 0.05f;   // 2 cm in front of the glass
                    clipNow = true;
                    if (!clipLogged)
                    {
                        clipLogged = true;
                        Debug.Log("[UncleSwap] mirror: " + __instance.transform.parent.name + " oblique clip at the glass on (transpiled " + transpiled
                            + ", mirror up " + (clipNormal == up ? "faces" : "faces away from") + " the viewer)");
                    }
                }
                var rt = texRef(__instance);
                if (rt == null) return;   // the game makes it on its first call
                bool ours = rt.name.StartsWith(Mark);
                if (Level <= 0)
                {
                    if (ours) Swap(__instance, rt, GameStyle(__instance), "game default");
                    return;
                }
                int w = Mathf.Max(256, Screen.width), hh = Mathf.Max(256, Screen.height);
                if (!ours || rt.width != w || rt.height != hh) Swap(__instance, rt, High(w, hh), "high");
                __state = MirrorReflection.qualityValue;
                MirrorReflection.qualityValue = 4;   // the game's top level: redraw every frame
            }
            catch (Exception e) { Debug.LogWarning("[UncleSwap] mirror: " + e.Message); }
        }

        static Exception Finalizer(Exception __exception, int __state)
        {
            clipNow = false;
            if (__state >= 0) MirrorReflection.qualityValue = __state;
            return __exception;
        }

        static RenderTexture High(int w, int h)
        {
            var rt = new RenderTexture(w, h, 24);
            var cam = Camera.main;
            bool forward = cam != null && cam.actualRenderingPath == RenderingPath.Forward;
            if (forward) rt.antiAliasing = 4;   // MSAA only works with forward rendering
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = Mark + " " + w + "x" + h + (forward ? " AA4" : " noAA");
            rt.hideFlags = HideFlags.DontSave;
            return rt;
        }

        static RenderTexture GameStyle(MirrorReflection m)
        {
            int s = m.m_TextureSize > 0 ? m.m_TextureSize : 1024;
            var rt = new RenderTexture(s, s, 16);
            rt.isPowerOfTwo = true;
            rt.name = "__MirrorReflection" + m.GetInstanceID();
            rt.hideFlags = HideFlags.DontSave;
            return rt;
        }

        static void Swap(MirrorReflection m, RenderTexture old, RenderTexture now, string what)
        {
            texRef(m) = now;
            Debug.Log("[UncleSwap] mirror: " + m.transform.parent.name + " reflection " + old.width + "x" + old.height
                + " (game mirror setting " + MirrorReflection.qualityValue + ") -> " + now.width + "x" + now.height
                + (now.antiAliasing > 1 ? " MSAA x" + now.antiAliasing : "") + ", " + what
                + (what == "high" ? ", every frame" : ""));
            if (old != null) UnityEngine.Object.Destroy(old);
        }
    }
}
