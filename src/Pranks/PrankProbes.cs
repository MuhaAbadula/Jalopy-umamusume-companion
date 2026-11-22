using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 34: measurements for the later pranks, log only (features=pranks).
    /// reach: poke = how many of her arm lengths his shoulder is from hers (P3), once per drive.
    /// traffic: first seen / visible = how far an AI car is when it first shows up in the AICar list and when the player
    /// can first see it (renderer in the main camera's frustum + clear line of sight; fog not counted) (P4).
    /// Also the AI car list itself: tag AICar (FindObjectsOfType costs 65-72 ms, LESSONS).
    /// </summary>
    public partial class Life
    {
        // ------------------------------------------------------------ AI cars by tag
        bool aiTagBroken, aiTagLogged, aiTagEmptyLogged;

        /// <summary>AI cars by tag AICar. The tag sits on a child or the root of the car: the SmartAICar is looked up on the
        /// object, below it, then above it. No FindObjectsOfType fallback (task 37: it hitches 65-72 ms and was hit every 2 s).</summary>
        SmartAICar[] ScanAICars()
        {
            if (aiTagBroken) return new SmartAICar[0];
            GameObject[] gos;
            try { gos = GameObject.FindGameObjectsWithTag("AICar"); }
            catch (UnityException) { aiTagBroken = true; Debug.Log("[UncleSwap] traffic: no AICar tag in this game, no traffic list"); return new SmartAICar[0]; }
            var l = new List<SmartAICar>();
            foreach (var g in gos)
            {
                if (g == null) continue;
                var c = g.GetComponent<SmartAICar>();
                if (c == null) c = g.GetComponentInChildren<SmartAICar>();
                if (c == null) c = g.GetComponentInParent<SmartAICar>();
                if (c != null && !l.Contains(c)) l.Add(c);
            }
            if (!aiTagLogged && l.Count > 0) { aiTagLogged = true; Debug.Log("[UncleSwap] traffic: AICar tag gives " + l.Count + " cars (" + gos.Length + " tagged)"); }
            if (!aiTagEmptyLogged && gos.Length > 0 && l.Count == 0)
            {
                aiTagEmptyLogged = true;
                // the player's own car frame (FrameHolder) carries the tag too: in town with no traffic that is all there is
                Debug.Log("[UncleSwap] traffic: no AI cars yet (" + gos.Length + " tagged: " + gos[0].name + ", our own car frame), tag lookup stays on");
            }
            return l.ToArray();
        }

        // ------------------------------------------------------------ debug: reach test (task 37)
        static readonly string[] ReachTestNames = { "radio", "volume knob", "wiper switch", "washer button", "horn", "mirror", "handbrake", "his head", "window crank" };
        int reachTestIdx;
        Transform reachTestT;
        string reachTestName;
        bool reachTestOuter;   // the crank: outer arm and a grip (wrist on the handle)
        Vector3 reachTestLocal;

        /// <summary>Middle of what is drawn for t (its renderer, else a child's, else its parent's), in t's local space.
        /// Bug 3: the pivots sit at the mounts (mirror at the roof, handbrake at the floor), not where a hand would go.</summary>
        static Vector3 VisibleCentre(Transform t)
        {
            Renderer r = t.GetComponent<Renderer>();
            if (r == null) r = t.GetComponentInChildren<Renderer>();
            if (r == null && t.parent != null) r = t.parent.GetComponent<Renderer>();
            return r != null ? t.InverseTransformPoint(r.bounds.center) : Vector3.zero;
        }

        /// <summary>A child (any depth) of t or of its parent with this name.</summary>
        static Transform FindNear(Transform t, string name)
        {
            for (var root = t; root != null; root = root == t ? t.parent : null)
                foreach (var c in root.GetComponentsInChildren<Transform>(true))
                    if (c.name == name) return c;
            return null;
        }

        Transform NearestOf<T>(string prefer = null) where T : Component
        {
            Transform best = null;
            float bd = float.MaxValue;
            Vector3 from = head != null ? head.T.position : uncle.position;
            foreach (var c in Object.FindObjectsOfType<T>())
            {
                // bug 2: MirrorLogicC is on the door mirrors too (L_Door_002); the rear-view one is RV_Mirror_001
                if (prefer != null && !c.name.StartsWith(prefer) && (c.transform.parent == null || !c.transform.parent.name.StartsWith(prefer))) continue;
                float d = (c.transform.position - from).sqrMagnitude;
                if (d < bd) { bd = d; best = c.transform; }
            }
            return best != null && bd < 25f ? best : null;
        }

        /// <summary>Debug "Reach test": her hand to the next cab target for 3 s (no action), log reach: name short u.</summary>
        public string DebugReach()
        {
            if (!inCarNow || asleepNow || Doze.Gate) return "she is not awake in her seat";
            string n = ReachTestNames[reachTestIdx++ % ReachTestNames.Length];
            Transform t = null;
            bool outer = false;
            switch (n)
            {
                case "radio": t = radio != null && radio.handle != null ? radio.handle.transform : null; break;
                case "volume knob": t = Vol() != null && Vol().handle != null ? Vol().handle.transform : null; break;
                case "wiper switch": t = NearestOf<WindowWipersLogicC>(); break;
                case "washer button": t = NearestOf<WindowWiperSprayLogicC>(); break;
                case "horn": t = NearestOf<CarHornC>(); break;
                case "mirror": t = NearestOf<MirrorLogicC>("RV_Mirror"); break;
                case "handbrake":
                    t = NearestOf<HandbrakeLogicC>();
                    if (t != null) t = FindNear(t, "HandbrakeButton") ?? t;   // bug 3: the grip at the lever's top, not its base
                    break;
                case "his head": t = Camera.main != null ? Camera.main.transform : null; break;
                case "window crank": t = herWindow != null ? herWindow.transform : null; outer = true; break;
            }
            if (t == null) return "reach test " + n + ": not found";
            reachTestT = t;
            reachTestName = n;
            reachTestOuter = outer;
            reachTestLocal = n == "his head" || n == "window crank" ? Vector3.zero : VisibleCentre(t);
            reachShortLogged = false;
            nextBeat = Time.time + 30f;
            beat = new Beat { Name = "reach_test", Dur = 3.2f, Look = Look.Reach, LookW = 0.9f };
            Debug.Log("[UncleSwap] Debug: reach test " + n + " (" + t.name + ")");
            return "reach test: " + n + " (" + t.name + (reachTestLocal != Vector3.zero ? ", aim " + t.TransformPoint(reachTestLocal).ToString("F2") + " pivot " + t.position.ToString("F2") : "") + ")";
        }

        // ------------------------------------------------------------ per frame
        float pokeDriveFor, nextTrafficProbe;
        bool pokeLogged, fogLogged, probeWasInCar;

        void ProbeStep(float dt, float now, bool inCar, bool slp)
        {
            if (inCar && !probeWasInCar) { pokeLogged = false; pokeDriveFor = 0f; reachLogged.Remove("poke"); }
            probeWasInCar = inCar;
            bool seated = inCar && !slp && PlayerSeated();
            bool driving = seated && SpeedKmh() > 20f;
            if (!pokeLogged && driving)
            {
                pokeDriveFor += dt;
                if (pokeDriveFor > 5f) { pokeLogged = true; PokeProbe(); }
            }
            else if (!driving) pokeDriveFor = 0f;
            if (seated) TrafficProbe(now);
        }

        /// <summary>His shoulder (estimate: 0.45 u towards her and 0.45 u below his eyes, recon 32 §4) against her inner arm.</summary>
        void PokeProbe()
        {
            var cam = Camera.main;
            if (cam == null || head == null || armL == null || armR == null) return;
            Vector3 fwd, up, winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            Vector3 eye = cam.transform.position;
            Vector3 flat = Vector3.ProjectOnPlane(head.T.position - eye, up);
            if (flat.sqrMagnitude < 1e-6f) return;
            Vector3 shoulder = eye + flat.normalized * 0.45f - up * 0.45f;
            ReachLog("poke", inner, shoulder);
            Debug.Log("[UncleSwap] reach: poke detail: her shoulder to his " + (shoulder - inner.Upper.position).magnitude.ToString("F2")
                + " u, his eyes to her head " + (head.T.position - eye).magnitude.ToString("F2") + " u, her arm " + ArmLen(inner).ToString("F3") + " u");
        }

        class SeenCar
        {
            public SmartAICar C;
            public Renderer[] R;
            public float First, FirstT, InView = -1f;
            public bool Done;
        }

        readonly Dictionary<int, SeenCar> seenCars = new Dictionary<int, SeenCar>();
        int trafficLogs;
        const int TrafficLogCap = 60;

        void TrafficProbe(float now)
        {
            if (now < nextTrafficProbe || trafficLogs >= TrafficLogCap) return;
            nextTrafficProbe = now + 0.25f;
            var cam = Camera.main;
            if (cam == null || car == null) return;
            if (!fogLogged)
            {
                fogLogged = true;
                Debug.Log("[UncleSwap] traffic: camera far clip " + cam.farClipPlane.ToString("F0") + " u, fog " + (RenderSettings.fog
                    ? RenderSettings.fogMode + " density " + RenderSettings.fogDensity.ToString("F4") + " start " + RenderSettings.fogStartDistance.ToString("F0")
                      + " end " + RenderSettings.fogEndDistance.ToString("F0") : "off"));
            }
            Vector3 eye = cam.transform.position;
            Plane[] planes = null;
            var alive = new HashSet<int>();
            foreach (var c in aiCars)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                int id = c.GetInstanceID();
                alive.Add(id);
                float d = Vector3.Distance(eye, c.transform.position);
                SeenCar s;
                if (!seenCars.TryGetValue(id, out s))
                {
                    s = new SeenCar { C = c, R = c.GetComponentsInChildren<Renderer>(), First = d, FirstT = now };
                    seenCars[id] = s;
                }
                if (s.Done) continue;
                if (planes == null) planes = GeometryUtility.CalculateFrustumPlanes(cam);
                Renderer seen = null;
                foreach (var r in s.R)
                    if (r != null && r.enabled && r.isVisible && GeometryUtility.TestPlanesAABB(planes, r.bounds)) { seen = r; break; }
                if (seen == null) continue;
                if (s.InView < 0f) s.InView = d;
                if (!LineClear(eye, seen.bounds.center, c.transform, cam.transform)) continue;
                s.Done = true;
                Vector3 cf = car.transform.forward;
                float ahead = Vector3.Dot(c.transform.position - eye, cf);
                float way = Vector3.Dot(c.transform.forward, cf);
                trafficLogs++;
                Debug.Log("[UncleSwap] traffic: first seen " + s.First.ToString("F0") + " u / visible " + d.ToString("F0") + " u (in view from "
                    + s.InView.ToString("F0") + " u, " + (now - s.FirstT).ToString("F1") + " s after first seen, " + (ahead >= 0f ? "ahead" : "behind")
                    + ", " + (way < -0.5f ? "oncoming" : way > 0.5f ? "same way" : "across") + ", us " + SpeedKmh().ToString("F0") + " km/h)");
            }
            // cars gone before the player could see them
            List<int> gone = null;
            foreach (var kv in seenCars)
                if (!alive.Contains(kv.Key)) { if (gone == null) gone = new List<int>(); gone.Add(kv.Key); }
            if (gone == null) return;
            foreach (int id in gone)
            {
                var s = seenCars[id];
                seenCars.Remove(id);
                if (s.Done || trafficLogs >= TrafficLogCap) continue;
                trafficLogs++;
                Debug.Log("[UncleSwap] traffic: first seen " + s.First.ToString("F0") + " u / never visible (gone after " + (now - s.FirstT).ToString("F0")
                    + " s" + (s.InView >= 0f ? ", in view from " + s.InView.ToString("F0") + " u but blocked" : "") + ")");
            }
            if (trafficLogs >= TrafficLogCap) Debug.Log("[UncleSwap] traffic: " + TrafficLogCap + " lines, probe stops for this session");
        }

        /// <summary>Nothing solid between the player's eyes and the car but the car itself, our car, the player and her.</summary>
        bool LineClear(Vector3 from, Vector3 to, Transform aiCar, Transform camT)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.01f) return true;
            Transform ours = car != null ? car.transform.root : null, player = camT.root;
            foreach (var h in Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Ignore))
            {
                Transform t = h.transform;
                if (t == null) continue;
                if (t.IsChildOf(aiCar) || (ours != null && t.IsChildOf(ours)) || t.IsChildOf(player) || t.IsChildOf(uncle)) continue;
                return false;
            }
            return true;
        }
    }
}
