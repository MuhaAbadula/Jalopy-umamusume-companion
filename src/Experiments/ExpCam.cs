using System;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 87 (concept 13, part A): what her potion does to the player. One effect at a time (E1..E15), fade in 1.5 s, out 2 s.
    /// Camera effects (FOV, roll, height, hiccup kick) are applied in OnPreCull and undone in OnPostRender of a component on the
    /// player camera: MouseLook / RigidbodyControllerC rewrite FOV and height every frame, raycasts and game logic keep the real
    /// transform (recon 87). Overlays (tint, rainbow, tunnel) in OnGUI; double vision copies the frame in OnRenderImage (ExpGhost).
    /// Strong effects end within 2 s when the player sits down to drive (OnCut). Log: [UncleSwap] Exp: E? start|end|cut.
    /// </summary>
    public static class ExpFx
    {
        public const int Count = 15;
        static readonly string[] names = { "", "glow", "fast", "color", "hiccup", "nothing", "fisheye", "zoom", "sway", "rainbow", "tunnel", "small", "giant", "slow", "flip", "double" };
        // strong = only on foot, short; light = also at the wheel, long
        static readonly bool[] strong = { false, false, false, false, false, false, true, false, true, false, true, true, true, true, true, false };

        public static string Name(int id) { return id >= 1 && id <= Count ? names[id] : "?"; }
        public static string Id(int id) { return "E" + id; }
        public static bool Strong(int id) { return id >= 1 && id <= Count && strong[id]; }

        /// <summary>Settings: strong effects allowed (off = only light ones are drawn).</summary>
        public static bool StrongOn = true;

        /// <summary>Called when an effect is over: id, cut (the player sat down / debug stop).</summary>
        public static Action<int, bool> OnEnd;

        public static int Current { get; private set; }
        public static bool Active { get { return Current != 0; } }
        public static Color Tint { get; private set; }

        internal static float start, dur, cutAt = -1f;
        internal static float w;   // 0..1, eased in and out

        /// <summary>Walking speed factor for the RigidbodyControllerC patch (E2).</summary>
        public static float SpeedMult { get { return Current == 2 ? 1f + 0.5f * w : 1f; } }

        static ExpCam cam;

        public static float DurationOf(int id)
        {
            if (id == 5) return 0f;
            if (id == 14) return 20f;
            return Strong(id) ? Random.Range(40f, 90f) : Random.Range(180f, 300f);
        }

        /// <summary>Starts effect id (any running one is ended first, without its line).</summary>
        public static string Start(int id, Color c, float seconds)
        {
            if (id < 1 || id > Count) return "no such effect";
            if (Active) Stop(false, "replaced");
            if (id == 5) { Log(Id(id) + " start (nothing)"); return null; }
            if (!Attach()) return "no player camera";
            Current = id;
            Tint = c;
            start = Time.time;
            dur = seconds;
            cutAt = -1f;
            w = 0f;
            Patches.Apply();
            cam.Begin();
            Log(Id(id) + " start " + Name(id) + " for " + seconds.ToString("0") + " s");
            return null;
        }

        /// <summary>Fade out now (2 s). cut: the line for a cut, not the result line.</summary>
        public static void Cut(string why)
        {
            if (!Active || cutAt >= 0f) return;
            cutAt = Time.time;
            Log(Id(Current) + " cut: " + why);
        }

        /// <summary>Ends at once, puts the camera back.</summary>
        public static void Stop(bool notify, string why)
        {
            if (!Active) return;
            int id = Current;
            bool wasCut = cutAt >= 0f;
            Current = 0;
            w = 0f;
            if (cam != null) cam.End();
            Log(Id(id) + " end (" + why + ")");
            if (notify && OnEnd != null)
            {
                try { OnEnd(id, wasCut); }
                catch (Exception e) { Debug.LogWarning("[UncleSwap] Exp: OnEnd: " + e.Message); }
            }
        }

        static bool Attach()
        {
            var c = Camera.main;
            if (c == null) return false;
            if (cam != null && cam.gameObject == c.gameObject) return true;
            if (cam != null) UnityEngine.Object.Destroy(cam);
            cam = c.gameObject.GetComponent<ExpCam>() ?? c.gameObject.AddComponent<ExpCam>();
            return true;
        }

        internal static void Log(string s) { Debug.Log("[UncleSwap] Exp: " + s); }

        // ------------------------------------------------------------ E2: walking speed
        static class Patches
        {
            static bool done;

            public static void Apply()
            {
                if (done) return;
                done = true;
                try
                {
                    var h = new Harmony("osobl.jalopy.uncleswap.expfx");
                    var m = AccessTools.Method(typeof(RigidbodyControllerC), "FixedUpdate");
                    if (m == null) { Debug.LogWarning("[UncleSwap] Exp: RigidbodyControllerC.FixedUpdate not found, E2 off"); return; }
                    h.Patch(m, prefix: new HarmonyMethod(typeof(Patches), nameof(Pre)), postfix: new HarmonyMethod(typeof(Patches), nameof(Post)));
                    Log("walk speed patch on");
                }
                catch (Exception e) { Debug.LogWarning("[UncleSwap] Exp: walk speed patch failed: " + e.Message); }
            }

            // Bug (user 2026-11-19, v0.52.0): the postfix multiplied the CURRENT velocity every step, also when the game had not
            // written it (no key pressed, sitting in the moving car): x1.5 per step = launched. Now only a step where the game
            // set velocity = targetVelocity from a pressed key, and from targetVelocity itself (never compounding); never seated.
            static void Pre(RigidbodyControllerC __instance, bool ___grounded, out bool __state)
            {
                __state = ___grounded && !__instance.driving && !__instance.isSat && SpeedMult > 1.001f;
            }

            static void Post(RigidbodyControllerC __instance, bool __state)
            {
                if (!__state) return;
                Vector3 tv = __instance.targetVelocity;
                if (tv.sqrMagnitude < 0.01f) return;   // no key: the game did not touch the velocity this step
                var seat = __instance.seatObj != null ? __instance.seatObj.GetComponent<SeatLogicC>() : null;
                if (seat != null && seat.isSat) return;
                var rb = __instance.GetComponent<Rigidbody>();
                if (rb == null) return;
                float k = SpeedMult;
                rb.velocity = new Vector3(tv.x * k, rb.velocity.y, tv.z * k);
            }
        }
    }

    /// <summary>Lives on the player camera while an effect runs (stays, idle, afterwards).</summary>
    public class ExpCam : MonoBehaviour
    {
        // game units per metre (recon 45: 2.61 u per metre of her at the dash; the player is scaled about the same): rounded down
        const float U = 2.0f;

        Camera cam;
        Light glow;
        AudioSource hicSrc;
        AudioClip hicClip;
        Texture2D white, vignette;
        ExpGhost ghost;
        float nextHic, hicAt = -1f;
        bool timeOwned;

        // saved in OnPreCull, put back in OnPostRender
        bool applied;
        float savedFov;
        Vector3 savedPos;
        Quaternion savedRot;

        public void Begin()
        {
            cam = GetComponent<Camera>();
            nextHic = Time.time + Random.Range(2f, 4f);
            hicAt = -1f;
            if (ExpFx.Current == 15)
            {
                ghost = gameObject.GetComponent<ExpGhost>() ?? gameObject.AddComponent<ExpGhost>();
                ghost.enabled = true;
            }
        }

        public void End()
        {
            if (glow != null) glow.enabled = false;
            if (ghost != null) { Destroy(ghost); ghost = null; }
            if (timeOwned)
            {
                if (Time.timeScale > 0.05f) Time.timeScale = 1f;
                timeOwned = false;
            }
        }

        bool PlayerAtWheel()
        {
            var pc = transform.parent != null ? transform.parent.GetComponent<RigidbodyControllerC>() : null;
            return pc != null && (pc.driving || pc.isSat);
        }

        void Update()
        {
            int id = ExpFx.Current;
            if (id == 0) return;
            float now = Time.time;
            // user 2026-11-19: strong effects stay at the wheel too ("chaos"); PlayerAtWheel kept for the log
            float fadeIn = id == 14 ? 1.5f : 1.5f, fadeOut = 2f;
            float wIn = Mathf.Clamp01((now - ExpFx.start) / fadeIn);
            float end = ExpFx.cutAt >= 0f ? ExpFx.cutAt + fadeOut : ExpFx.start + ExpFx.dur;
            float wOut = Mathf.Clamp01((end - now) / fadeOut);
            ExpFx.w = Mathf.SmoothStep(0f, 1f, Mathf.Min(wIn, wOut));
            if (now >= end) { ExpFx.Stop(true, ExpFx.cutAt >= 0f ? "cut" : "time"); return; }

            float t = now - ExpFx.start;
            // E1, E9: the player glows
            if (id == 1 || id == 9)
            {
                if (glow == null)
                {
                    var go = new GameObject("FukuExpGlow");
                    go.transform.SetParent(transform, false);
                    go.transform.localPosition = new Vector3(0f, -0.3f, 0.2f);
                    glow = go.AddComponent<Light>();
                    glow.type = LightType.Point;
                    glow.shadows = LightShadows.None;
                }
                glow.enabled = true;
                glow.range = 10f;
                glow.intensity = 1.8f * ExpFx.w;
                glow.color = id == 9 ? Rainbow(t) : ExpFx.Tint;
            }
            // E13: slow motion (not over the pause menu's 0)
            if (id == 13 && Time.timeScale > 0.05f)
            {
                Time.timeScale = 1f - 0.4f * ExpFx.w;
                timeOwned = true;
            }
            // E4: hiccup every 4-8 s
            if (id == 4 && now >= nextHic && ExpFx.w > 0.5f)
            {
                nextHic = now + Random.Range(4f, 8f);
                hicAt = now;
                Hic();
            }
        }

        static Color Rainbow(float t)
        {
            Color c = Color.HSVToRGB(Mathf.Repeat(t / 6f, 1f), 0.75f, 1f);
            return c;
        }

        void Hic()
        {
            if (hicSrc == null)
            {
                hicSrc = gameObject.AddComponent<AudioSource>();
                hicSrc.playOnAwake = false;
                hicSrc.spatialBlend = 0f;
            }
            if (hicClip == null) hicClip = MakeHic();
            hicSrc.pitch = Random.Range(0.9f, 1.15f);
            hicSrc.PlayOneShot(hicClip, 0.7f);
        }

        // a short "hic": a falling chirp with a breathy edge
        static AudioClip MakeHic()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.17f);
            var data = new float[n];
            float phase = 0f;
            var rnd = new System.Random(87);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float f = Mathf.Lerp(560f, 260f, t / 0.17f);
                phase += 2f * Mathf.PI * f / rate;
                float env = (1f - Mathf.Exp(-t * 250f)) * Mathf.Exp(-t * 26f);
                float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
                data[i] = env * (0.75f * Mathf.Sin(phase) + 0.25f * Mathf.Sin(phase * 2.03f) + 0.2f * noise);
            }
            var clip = AudioClip.Create("FukuHic", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        void OnPreCull()
        {
            applied = false;
            int id = ExpFx.Current;
            if (id == 0 || cam == null) return;
            float w = ExpFx.w;
            if (w <= 0.0001f && id != 4) return;
            float t = Time.time - ExpFx.start;
            savedFov = cam.fieldOfView;
            savedPos = transform.position;
            savedRot = transform.rotation;
            applied = true;

            float fov = savedFov, roll = 0f, pitch = 0f, lift = 0f;
            switch (id)
            {
                case 6: fov = Mathf.Lerp(savedFov, 100f + 8f * Mathf.Sin(2f * Mathf.PI * t / 3f), w); break;
                case 7: fov = Mathf.Lerp(savedFov, 30f, w); break;
                case 8:
                    roll = 6f * Mathf.Sin(2f * Mathf.PI * t / 4f) * w;
                    pitch = 1.5f * Mathf.Sin(2f * Mathf.PI * t / 5.3f) * w;
                    break;
                case 11: lift = -0.35f * U * w; break;
                case 12: lift = 0.5f * U * w; break;
                case 14: roll = 180f * w; break;
                case 4:
                    if (hicAt >= 0f)
                    {
                        float k = (Time.time - hicAt) / 0.22f;
                        if (k < 1f) pitch = 3f * Mathf.Sin(k * Mathf.PI) * (1f - k * 0.5f);
                    }
                    break;
            }
            cam.fieldOfView = fov;
            if (lift != 0f) transform.position = savedPos + Vector3.up * lift;
            if (roll != 0f || pitch != 0f) transform.rotation = savedRot * Quaternion.Euler(pitch, 0f, roll);
        }

        void OnPostRender()
        {
            if (!applied) return;
            applied = false;
            cam.fieldOfView = savedFov;
            transform.position = savedPos;
            transform.rotation = savedRot;
        }

        void OnGUI()
        {
            int id = ExpFx.Current;
            if (id == 0 || Event.current.type != EventType.Repaint) return;
            float w = ExpFx.w;
            if (w <= 0.001f) return;
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            GUI.depth = 50;
            var full = new Rect(0f, 0f, Screen.width, Screen.height);
            Color keep = GUI.color;
            float t = Time.time - ExpFx.start;
            if (id == 3) { Color c = ExpFx.Tint; c.a = 0.2f * w; GUI.color = c; GUI.DrawTexture(full, white); }
            if (id == 9) { Color c = Rainbow(t); c.a = 0.22f * w; GUI.color = c; GUI.DrawTexture(full, white); }
            if (id == 10)
            {
                if (vignette == null) vignette = MakeVignette();
                GUI.color = new Color(1f, 1f, 1f, w);
                GUI.DrawTexture(full, vignette, ScaleMode.StretchToFill);
            }
            if (id == 15 && ghost != null && ghost.Frame != null)
            {
                float off = (10f + 6f * Mathf.Sin(t * 1.3f)) * w * Screen.height / 900f;
                GUI.color = new Color(1f, 1f, 1f, 0.35f * w);
                GUI.DrawTexture(new Rect(off, off * 0.3f, Screen.width, Screen.height), ghost.Frame, ScaleMode.StretchToFill);
            }
            GUI.color = keep;
        }

        static Texture2D MakeVignette()
        {
            const int s = 256;
            var tex = new Texture2D(s, s, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f, dy = (y + 0.5f) / s * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.95f, r)) * 0.95f;
                    px[y * s + x] = new Color(0f, 0f, 0f, a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        void OnDisable()
        {
            if (applied) OnPostRender();
        }
    }

    /// <summary>E15: keeps a copy of the last frame for the shifted second image.</summary>
    public class ExpGhost : MonoBehaviour
    {
        public RenderTexture Frame;

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (Frame == null || Frame.width != src.width || Frame.height != src.height)
            {
                if (Frame != null) Frame.Release();
                Frame = new RenderTexture(src.width, src.height, 0);
            }
            Graphics.Blit(src, Frame);
            Graphics.Blit(src, dst);
        }

        void OnDestroy()
        {
            if (Frame != null) { Frame.Release(); Frame = null; }
        }
    }
}
