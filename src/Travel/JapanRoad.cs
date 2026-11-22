#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 55 (concept 11 step 2): our own road segment in a leg, test only, spawned by the debug button.
    /// Task 56/58: the template comes from the bundle Mods\Assets\UmamusumeCompanion\road\japan.unity3d, built by
    /// tools\build_road.ps1: prefab JapanLook (task 58, concept 11a: the whole Japanese look, own textures, night glow),
    /// else prefab JapanGrey (task 56, grey geometry only); root scale 2, model 0.5, RouteContinue (dx/2, dy/2, -500)
    /// directly under the root. Bundle missing or broken = the code-made placeholder of task 55 (flat road 20 x 1000 u).
    /// Night (task 58): JapanRoadSeg checks DirectorC.isDay every 2 s and swaps the "*_lit" renderers to the
    /// Self-Illumin materials jp_atlas_lit / jp_signs_lit of the bundle and switches the prefab's point light.
    /// Either one is kept under an inactive holder.
    /// Hook (recon 53): Prefix on RouteGeneratorC.SpawnRoute1..4 puts the template into a 0-0 slot k of routeNSegments[];
    /// the game instantiates it, positions it (east: at the previous continueNode, home: previous position + (0,0,1000)),
    /// adds it to spawnedRouteSegments, and DeleteRoute (motel sleep) destroys it. The slot is restored as soon as the
    /// clone exists. Hard rules: 1000 u along -z, root scale 2, continueNode at local (0,0,-500), seams size 0 (20 u, y 0).
    /// Logs: "[Umamusume companion] JapanRoad: ...".
    /// </summary>
    public static class JapanRoad
    {
        public static bool Armed;
        public static string Dir;         // Mods\Assets\UmamusumeCompanion\road (set by CompanionMod.Start)
        const string BundleFile = "japan.unity3d";
        static readonly string[] PrefabNames = { "JapanLook", "JapanGrey" };   // the look, else the grey geometry

        const float Scale = 2f;           // root scale of every game segment
        const float L = 500f;             // local length (1000 u in the world)
        const float HW = 5f;              // local half width of the road (10 u)
        const float VergeW = 25f;         // local verge width each side (50 u)
        const float Skirt = 21f;          // local skirt depth (42 u), like the game's road colliders
        const int PudCount = 100;         // pud_### every 10 u

        static bool applied;
        static GameObject holder, template;
        static AssetBundle bundle;
        internal static bool FromBundle;   // the template came from the bundle
        internal static bool IsLook;       // ... and it is JapanLook (lit objects, night swap)
        internal static readonly Dictionary<string, Material> LitMats = new Dictionary<string, Material>();   // day material name -> lit material
        static Material matRoad, matVerge, matWall, matLine;

        // the slot we swapped, restored when the clone starts
        static GameObject[] pendArr;
        static int pendK = -1;
        static GameObject pendOrig;
        internal static string PendLeg = "";
        internal static int PendK = -1;

        internal static readonly List<JapanRoadSeg> Live = new List<JapanRoadSeg>();
        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal static void Log(string s) { Debug.Log("[Umamusume companion] JapanRoad: " + s); }
        internal static string F(float v) { return v.ToString("0.00", IC); }
        internal static string P(Vector3 v) { return "(" + v.x.ToString("0.0", IC) + ", " + v.y.ToString("0.0", IC) + ", " + v.z.ToString("0.0", IC) + ")"; }

        // ------------------------------------------------------------ patch
        public static void Apply()
        {
            if (applied) return;
            var h = new Harmony("osobl.jalopy.umamusumecompanion.japanroad");
            int n = 0;
            for (int i = 1; i <= 4; i++)
            {
                var m = AccessTools.Method(typeof(RouteGeneratorC), "SpawnRoute" + i);
                if (m == null) { Debug.LogWarning("[Umamusume companion] JapanRoad: SpawnRoute" + i + " not found"); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(JapanRoad), "Pre" + i));
                n++;
            }
            applied = true;
            Log("patched SpawnRoute x" + n);
        }

        static void Pre1(RouteGeneratorC __instance) { Before(__instance, __instance.route1Segments, 1); }
        static void Pre2(RouteGeneratorC __instance) { Before(__instance, __instance.route2Segments, 2); }
        static void Pre3(RouteGeneratorC __instance) { Before(__instance, __instance.route3Segments, 3); }
        static void Pre4(RouteGeneratorC __instance) { Before(__instance, __instance.route4Segments, 4); }

        static readonly string[] Countries = { "Germany", "CSFR", "Hungary", "Yugoslavia", "Bulgaria", "Turkey" };
        static readonly string[] GatherNames = { "German", "CSFR", "HUN", "YUGO", "BUL", "TURK" };   // RouteGeneratorC.Gather<X>Route<N>

        /// <summary>Index of the road set of a leg. The game (RouteGeneratorC.GetRoute1): east level 0..5 = Germany, CSFR,
        /// Hungary, Yugoslavia, Bulgaria, Turkey; home level 0 and 1 = Germany, 2..6 = CSFR .. Turkey.</summary>
        internal static int CountryIndex(int level, bool home)
        {
            int i = home ? Mathf.Max(level - 1, 0) : level;
            return i >= 0 && i < Countries.Length ? i : -1;
        }

        internal static string Country(int level, bool home)
        {
            int i = CountryIndex(level, home);
            return i >= 0 ? Countries[i] : "level" + level;
        }

        /// <summary>0-0 slots of a leg that are not stop-offs. Segment k is instantiated while routeAmmo == k; the last one
        /// (len-1) builds the town, so k = 1..hi.</summary>
        static void Scan(GameObject[] arr, int hi, List<int> ok, List<string> sizes)
        {
            ok.Clear(); sizes.Clear();
            for (int k = 1; k <= hi; k++)
            {
                var sc = arr[k] != null ? arr[k].GetComponent<SpawnContinueC>() : null;
                if (sc == null) { sizes.Add("?"); continue; }
                sizes.Add(sc.startSize + "-" + sc.continueSize + (sc.stopOffTarget != null ? "s" : ""));
                if (sc.startSize == 0 && sc.continueSize == 0 && sc.stopOffTarget == null) ok.Add(k);
            }
        }

        /// <summary>The leg has no 0-0 slot (short legs are often all 1-1): run the game's own generator for this route again
        /// until it has one. Nothing of the leg is spawned yet (we are in the Prefix of SpawnRouteN); Gather sets the segment
        /// list and the roundabout together, so the leg stays consistent. The destination town is kept.</summary>
        static int Reroll(RouteGeneratorC rg, GameObject[] arr, int route, int hi, List<int> ok, List<string> sizes)
        {
            int ci = CountryIndex(rg.routeLevel, rg.drivingTowardsHome);
            if (ci < 1 || ci > 4) return 0;   // Germany and Turkey have no size-0 roads
            var m = AccessTools.Method(typeof(RouteGeneratorC), "Gather" + GatherNames[ci] + "Route" + route);
            if (m == null) return 0;
            int dest = rg.destinationNumber;
            int n = 0;
            try
            {
                while (ok.Count == 0 && n < 300)
                {
                    m.Invoke(rg, null);
                    n++;
                    Scan(arr, hi, ok, sizes);
                }
            }
            finally { rg.destinationNumber = dest; }
            return n;
        }

        static void Before(RouteGeneratorC rg, GameObject[] arr, int route)
        {
            try
            {
                RestoreSlot();   // a slot left from a leg whose clone never started
                if (!Armed || rg == null) return;
                int len = rg.routeChosenLength;
                string leg = Country(rg.routeLevel, rg.drivingTowardsHome) + " " + (rg.drivingTowardsHome ? "home" : "east") + " route" + route + " len" + len;
                if (arr == null) { Log("fail no segment array (" + leg + "), still armed"); return; }
                // segment k is instantiated while routeAmmo == k; the last one (len-1) builds the town, so k = 1..len-2
                int hi = Mathf.Min(len - 2, arr.Length - 1);
                if (hi < 1) { Log("fail leg too short (" + leg + "), still armed"); return; }
                var ok = new List<int>();
                var sizes = new List<string>();
                Scan(arr, hi, ok, sizes);
                string rolled = "";
                if (ok.Count == 0)
                {
                    string was = string.Join(" ", sizes.ToArray());
                    int n = 0;
                    try { n = Reroll(rg, arr, route, hi, ok, sizes); }
                    catch (Exception e) { Log("fail re-roll " + e.GetType().Name + ": " + e.Message); }
                    if (ok.Count == 0)
                    {
                        Log("fail no 0-0 slot (" + leg + ", slots 1.." + hi + ": " + was + (n > 0 ? "; " + n + " re-rolls did not help" : "; this country has no size-0 road") + "), still armed");
                        return;
                    }
                    rolled = ", leg re-rolled x" + n + " (was " + was + ", now " + string.Join(" ", sizes.ToArray()) + ")";
                }
                if (!Ensure()) { Log("fail template not built, still armed"); return; }
                int pick = ok[UnityEngine.Random.Range(0, ok.Count)];
                pendArr = arr; pendK = pick; pendOrig = arr[pick];
                PendLeg = leg; PendK = pick;
                arr[pick] = template;
                Armed = false;
                Log("slot " + leg + " k=" + pick + " replaces " + (pendOrig != null ? pendOrig.name : "?") + " (0-0 slots: " + ok.Count + ")" + rolled);
            }
            catch (Exception e) { Debug.LogError("[Umamusume companion] JapanRoad: fail prefix " + e); }
        }

        internal static void RestoreSlot()
        {
            if (pendArr != null && pendK >= 0 && pendK < pendArr.Length && pendArr[pendK] == template) pendArr[pendK] = pendOrig;
            pendArr = null; pendK = -1; pendOrig = null;
        }

        // ------------------------------------------------------------ template
        static Material Mat(Color c)
        {
            var sh = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse");
            return new Material(sh) { color = c };
        }

        static bool Ensure()
        {
            if (template != null) return true;
            try { Build(); return template != null; }
            catch (Exception e) { Debug.LogError("[Umamusume companion] JapanRoad: fail template " + e); return false; }
        }

        static void Build()
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = new GameObject("UmamusumeCompanion_JapanRoadHolder");
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
            string why;
            if (BuildFromBundle(out why)) return;
            Log("fail bundle " + why + "; placeholder used");
            BuildPlaceholder();
        }

        // ------------------------------------------------------------ template from the bundle (task 56)
        static bool BuildFromBundle(out string why)
        {
            why = "";
            GameObject root = null;
            try
            {
                if (bundle == null)
                {
                    string path = Path.Combine(Dir ?? "", BundleFile);
                    if (string.IsNullOrEmpty(Dir) || !File.Exists(path)) { why = "missing " + path; return false; }
                    bundle = AssetBundle.LoadFromFile(path);   // once per session; never unloaded (the template uses its assets)
                    if (bundle == null) { why = "LoadFromFile returned null (" + path + ": other Unity version, or loaded twice)"; return false; }
                }
                GameObject pf = null;
                string prefabName = "";
                foreach (var pn in PrefabNames)
                {
                    pf = bundle.LoadAsset<GameObject>(pn);
                    if (pf != null) { prefabName = pn; break; }
                }
                if (pf == null) { why = "no prefab " + string.Join(" / ", PrefabNames) + " in " + BundleFile + " (" + string.Join(", ", bundle.GetAllAssetNames()) + ")"; return false; }
                bool look = prefabName == "JapanLook";

                root = (GameObject)UnityEngine.Object.Instantiate(pf, holder.transform, false);
                root.name = look ? "JapanRoad_look_0-0" : "JapanRoad_grey_0-0";
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one * Scale;
                var model = root.transform.Find("model");
                if (model == null) { why = "prefab has no child 'model'"; Kill(root); return false; }
                model.localScale = Vector3.one * 0.5f;   // the FBX is post-scale (1000 u long), report 54

                // the game's home shift reads continueNode.localPosition x 2: RouteContinue must be a direct child of the root
                var rc = root.transform.Find("RouteContinue");
                if (rc == null) { why = "prefab has no RouteContinue under the root"; Kill(root); return false; }
                rc.localRotation = Quaternion.identity;
                Vector3 end = rc.localPosition;
                if (Mathf.Abs(end.z + L) > 0.01f) { why = "RouteContinue local z " + F(end.z) + " (want -500: 1000 u segment)"; Kill(root); return false; }

                // shaders: re-assign from the player by the name of each material's own shader (fog variants of the game)
                int nMat = 0, nRend = 0, nLit = 0, tris = 0;
                var seen = new List<Material>();
                var shaderNames = new List<string>();
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    nRend++;
                    if (r.name.EndsWith("_lit")) nLit++;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) tris += (int)(mf.sharedMesh.GetIndexCount(0) / 3);
                    foreach (var m in r.sharedMaterials)
                        if (m != null && !seen.Contains(m))
                        {
                            seen.Add(m);
                            if (Reshade(m)) nMat++;
                            if (!shaderNames.Contains(m.shader.name)) shaderNames.Add(m.shader.name);
                        }
                }
                LitMats.Clear();
                if (look)
                    foreach (var dn in new[] { "jp_atlas", "jp_signs" })
                    {
                        var lm = bundle.LoadAsset<Material>(dn + "_lit");
                        if (lm == null) { Log("fail lit material " + dn + "_lit not in the bundle (no glow at night)"); continue; }
                        Reshade(lm);
                        LitMats[dn] = lm;
                    }

                // colliders by layer, for the log
                var cols = new SortedDictionary<int, int>();
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    int n; cols.TryGetValue(c.gameObject.layer, out n); cols[c.gameObject.layer] = n + 1;
                }
                if (!cols.ContainsKey(14)) { why = "no layer-14 road collider in the prefab"; Kill(root); return false; }
                string colStr = "";
                foreach (var kv in cols) colStr += (colStr.Length > 0 ? " " : "") + "L" + kv.Key + " x" + kv.Value;

                // pud_### in name order
                var pl = new List<Transform>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("pud_")) pl.Add(t);
                pl.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

                Fill(root, rc.gameObject, pl.ToArray());
                FromBundle = true;
                IsLook = look;
                long kb = 0;
                try { kb = new FileInfo(Path.Combine(Dir ?? "", BundleFile)).Length / 1024; } catch { }
                Log("template built (bundle " + BundleFile + " " + prefabName + ", scale 2, nodes=" + pl.Count + ", end local " + P(end)
                    + " = dx " + F(end.x * Scale) + " dy " + F(end.y * Scale) + ", colliders " + colStr + ", materials " + nMat + "/" + seen.Count
                    + " re-shaded: " + string.Join(", ", shaderNames.ToArray()) + ")");
                if (look) Log("look (renderers " + nRend + ", tris " + tris + ", lit " + nLit + ", bundle " + kb + " KB)");
                else Log("fail look: prefab JapanLook not in the bundle, grey segment used");
                return true;
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                Kill(root);
                return false;
            }
        }

        static void Kill(GameObject go) { if (go != null) UnityEngine.Object.Destroy(go); }

        /// <summary>The player's own copy of the material's shader (same name). False = not in the game, the bundle's copy stays.</summary>
        static bool Reshade(Material m)
        {
            if (m == null || m.shader == null) return false;
            var s = Shader.Find(m.shader.name);
            if (s == null) return false;
            int q = m.renderQueue;
            m.shader = s;
            m.renderQueue = q;
            return true;
        }

        /// <summary>Game logic: option (b) of recon 53, fields filled from the names.</summary>
        static void Fill(GameObject root, GameObject rc, Transform[] puds)
        {
            var sc = root.AddComponent<SpawnContinueC>();
            sc.continueNode = rc;
            sc.startSize = 0;
            sc.continueSize = 0;
            sc.puddleTargets = puds;
            sc.sceneryTargets = new Transform[0];
            sc.roadConditions = new GameObject[0];
            sc.randomChance = 0;
            sc.stopOffTarget = null;
            sc.DestinationNode = null;
            sc.signForwardDistance = null;
            sc.signBackDistance = null;
            sc.trafficAINodes = new List<Transform>();
            sc.crateNodes = new List<Transform>();
            sc.abandonCarNodes = new List<Transform>();
            root.AddComponent<JapanRoadSeg>();
            template = root;
        }

        // ------------------------------------------------------------ placeholder (task 55)
        static void BuildPlaceholder()
        {
            FromBundle = false;
            IsLook = false;
            if (matRoad == null)
            {
                matRoad = Mat(new Color(0.33f, 0.33f, 0.35f));
                matVerge = Mat(new Color(0.32f, 0.45f, 0.22f));
                matWall = Mat(new Color(0.62f, 0.61f, 0.58f));
                matLine = Mat(new Color(0.85f, 0.7f, 0.15f));
            }

            var root = new GameObject("JapanRoad_0-0");
            root.transform.SetParent(holder.transform, false);
            root.transform.localScale = Vector3.one * Scale;

            // road: visual (layer 0) + the same mesh as the ground collider (layer 14, asphalt grip)
            var road = RoadMesh();
            Visual(root, "road_visual", road, matRoad);
            Collider(root, "road_L14", road, 14);
            // centre line, visual only (solid yellow, as concept 11)
            Visual(root, "line_visual", Strip(-0.075f, 0.075f, 0.005f, 0.005f, 50), matLine);

            // verges: flat, from the road edge out, a hair below the road (layer 17, grass grip)
            var vl = Strip(HW, HW + VergeW, -0.02f, -0.02f, 25);
            var vr = Strip(-HW - VergeW, -HW, -0.02f, -0.02f, 25);
            Visual(root, "verge_L_visual", vl, matVerge); Collider(root, "verge_L_L17", vl, 17);
            Visual(root, "verge_R_visual", vr, matVerge); Collider(root, "verge_R_L17", vr, 17);

            // test wall on the left of the eastbound driver (+x), mid-segment: layer 26 = crash barrier, no damage
            var wallPos = new Vector3(HW + 1.25f, 1f, -L * 0.5f);
            var wallSize = new Vector3(0.75f, 2f, 50f);
            var wv = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.DestroyImmediate(wv.GetComponent<Collider>());
            wv.name = "wall_visual"; wv.layer = 0;
            wv.transform.SetParent(root.transform, false);
            wv.transform.localPosition = wallPos; wv.transform.localScale = wallSize;
            wv.GetComponent<MeshRenderer>().sharedMaterial = matWall;
            var wc = new GameObject("wall_L26");
            wc.layer = 26;
            wc.transform.SetParent(root.transform, false);
            wc.transform.localPosition = wallPos;
            wc.AddComponent<BoxCollider>().size = wallSize;

            // end seam
            var rc = new GameObject("RouteContinue");
            rc.transform.SetParent(root.transform, false);
            rc.transform.localPosition = new Vector3(0f, 0f, -L);

            // pothole / puddle targets: centre line every 10 u, like the game's (y -0.15 u, yaw 0 / 180)
            var puds = new Transform[PudCount];
            var pudRoot = new GameObject("puddles");
            pudRoot.transform.SetParent(root.transform, false);
            for (int i = 0; i < PudCount; i++)
            {
                var p = new GameObject("pud_" + i.ToString("000"));
                p.transform.SetParent(pudRoot.transform, false);
                p.transform.localPosition = new Vector3(0f, -0.075f, -2.5f - 5f * i);
                p.transform.localEulerAngles = new Vector3(0f, i % 2 == 0 ? 0f : 180f, 0f);
                puds[i] = p.transform;
            }

            Fill(root, rc, puds);
            Log("template built (placeholder 20 x 1000 u, scale 2, nodes=" + PudCount + ")");
        }

        static void Visual(GameObject root, string name, Mesh m, Material mat)
        {
            var go = new GameObject(name);
            go.layer = 0;
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
        }

        static void Collider(GameObject root, string name, Mesh m, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshCollider>().sharedMesh = m;
        }

        // ------------------------------------------------------------ meshes (local space, z 0 .. -L)
        static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 want)
        {
            // a-b-c-d around the quad; flip so the front face points along want
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) >= 0f) { t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3); }
            else { t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2); }
        }

        static Mesh Make(string name, List<Vector3> v, List<int> t)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Flat strip x0..x1 at height y (y0 at x0, y1 at x1), cut into n pieces along z.</summary>
        static Mesh Strip(float x0, float x1, float y0, float y1, int n)
        {
            var v = new List<Vector3>(); var t = new List<int>();
            float step = L / n;
            for (int i = 0; i < n; i++)
            {
                float z0 = -step * i, z1 = -step * (i + 1);
                Quad(v, t, new Vector3(x0, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x0, y0, z1), Vector3.up);
            }
            return Make("jr_strip", v, t);
        }

        /// <summary>Road top (y 0, x -HW..HW) cut every 10 u, plus vertical skirts at both edges down to -Skirt.</summary>
        static Mesh RoadMesh()
        {
            var v = new List<Vector3>(); var t = new List<int>();
            int n = 100;
            float step = L / n;
            for (int i = 0; i < n; i++)
            {
                float z0 = -step * i, z1 = -step * (i + 1);
                Quad(v, t, new Vector3(-HW, 0f, z0), new Vector3(HW, 0f, z0), new Vector3(HW, 0f, z1), new Vector3(-HW, 0f, z1), Vector3.up);
            }
            Quad(v, t, new Vector3(HW, 0f, 0f), new Vector3(HW, 0f, -L), new Vector3(HW, -Skirt, -L), new Vector3(HW, -Skirt, 0f), Vector3.right);
            Quad(v, t, new Vector3(-HW, 0f, 0f), new Vector3(-HW, 0f, -L), new Vector3(-HW, -Skirt, -L), new Vector3(-HW, -Skirt, 0f), Vector3.left);
            return Make("jr_road", v, t);
        }

        // ------------------------------------------------------------ debug panel
        public static string DebugArm()
        {
            Apply();
            if (Armed)
            {
                Armed = false;
                Log("disarmed");
                return "disarmed";
            }
            if (!Ensure()) return "failed: template not built (see the log)";
            Armed = true;
            Log("armed");
            return "armed: pick the next town on the map (CSFR / Hungary / Yugoslavia / Bulgaria roads; a leg without a free slot is re-rolled; Germany and Turkey have no size-0 road)";
        }

        public static string DebugShowNodes()
        {
            Live.RemoveAll(s => s == null);
            if (Live.Count == 0) return "no Japanese segment in this leg (" + (Armed ? "armed for the next leg" : "not armed") + ")";
            var cam = Camera.main;
            string r = "";
            foreach (var s in Live)
            {
                int n = s.ShowNodes(60f);
                float dist = cam != null ? Vector3.Distance(cam.transform.position, s.transform.position) : -1f;
                r += (r.Length > 0 ? "; " : "") + "k=" + s.K + " start " + P(s.transform.position) + " " + dist.ToString("0", IC) + " u from you, " + n + " markers";
            }
            return r + " (60 s)";
        }

        public static string Status()
        {
            Live.RemoveAll(s => s == null);
            if (Armed) return "JP road: armed";
            if (Live.Count > 0) return "JP road: in this leg (k=" + Live[0].K + ")";
            return "";
        }
    }

    /// <summary>Marker on the segment: restores the swapped slot, logs the spawn and the seams, logs the destroy.</summary>
    public class JapanRoadSeg : MonoBehaviour
    {
        public int K = -1;
        bool live;
        static readonly Dictionary<Color, Material> markerMats = new Dictionary<Color, Material>();

        void Start()
        {
            if (transform.parent != null) return;   // the template under the holder never starts; this is a clone
            live = true;
            K = JapanRoad.PendK;
            string leg = JapanRoad.PendLeg;
            JapanRoad.RestoreSlot();
            JapanRoad.Live.Add(this);
            if (JapanRoad.IsLook) StartCoroutine(Night());
            StartCoroutine(Report(leg));
        }

        // ---- night (task 58, concept 11a par. 7): like the game's street lights, a material swap by DirectorC.isDay
        struct LitR { public Renderer r; public Material day, night; public float glow; }
        readonly List<LitR> lit = new List<LitR>();
        readonly List<Light> lamps = new List<Light>();
        int nightState = -1;   // -1 unknown, 0 day, 1 night

        static float Glow(string name)
        {
            if (name.StartsWith("reflect")) return 0.5f;    // dim orange dots along the bend
            if (name.StartsWith("window")) return 0.85f;
            return 1f;                                      // vending displays, the street lamp tube
        }

        IEnumerator Night()
        {
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (!r.name.EndsWith("_lit") || r.sharedMaterial == null) continue;
                Material nm;
                if (!JapanRoad.LitMats.TryGetValue(r.sharedMaterial.name, out nm)) continue;
                lit.Add(new LitR { r = r, day = r.sharedMaterial, night = nm, glow = Glow(r.name) });
            }
            foreach (var l in GetComponentsInChildren<Light>(true)) { l.enabled = false; lamps.Add(l); }
            DirectorC dir = null;
            var block = new MaterialPropertyBlock();
            while (true)
            {
                if (dir == null)
                {
                    var d = GameObject.FindWithTag("Director");
                    dir = d != null ? d.GetComponent<DirectorC>() : null;
                }
                int want = dir != null && !dir.isDay ? 1 : 0;
                if (want != nightState)
                {
                    nightState = want;
                    foreach (var x in lit)
                    {
                        if (x.r == null) continue;
                        x.r.sharedMaterial = want == 1 ? x.night : x.day;
                        if (want == 1)
                        {
                            block.Clear();
                            block.SetColor("_Color", new Color(x.glow, x.glow, x.glow, 1f));
                            x.r.SetPropertyBlock(block);
                        }
                        else x.r.SetPropertyBlock(null);
                    }
                    foreach (var l in lamps) if (l != null) l.enabled = want == 1;
                    JapanRoad.Log("night " + (want == 1 ? "on" : "off") + " (lit " + lit.Count + ", light " + lamps.Count + ")");
                }
                yield return new WaitForSeconds(2f);
            }
        }

        IEnumerator Report(string leg)
        {
            yield return new WaitForSeconds(2f);   // whole leg exists ~20 frames after the map click; home shift done in Start
            var sc = GetComponent<SpawnContinueC>();
            int nodes = sc != null && sc.puddleTargets != null ? sc.puddleTargets.Length : 0;
            JapanRoad.Log("spawn " + leg + " " + K + " seam=0 nodes=" + nodes + " pos=" + JapanRoad.P(transform.position));
            try { Seams(sc); }
            catch (Exception e) { JapanRoad.Log("seam check failed: " + e.Message); }
        }

        void Seams(SpawnContinueC me)
        {
            var d = GameObject.FindWithTag("Director");
            var rg = d != null ? d.GetComponent<RouteGeneratorC>() : null;
            if (rg == null || me == null || me.continueNode == null) return;
            bool home = rg.drivingTowardsHome;
            var list = rg.spawnedRouteSegments;
            int i = list.IndexOf(gameObject);
            SpawnContinueC prev = i > 0 && list[i - 1] != null ? list[i - 1].GetComponent<SpawnContinueC>() : null;
            SpawnContinueC next = i >= 0 && i + 1 < list.Count && list[i + 1] != null ? list[i + 1].GetComponent<SpawnContinueC>() : null;
            Vector3 a = transform.position, b = me.continueNode.transform.position;
            string sIn = "-", sOut = "-";
            // east: the previous segment ends at our root, the next starts at our continueNode;
            // home: built towards +z, the previous segment's root = our continueNode, our root = the next one's continueNode
            if (prev != null && prev.continueNode != null)
                sIn = JapanRoad.P(home ? b - prev.transform.position : a - prev.continueNode.transform.position);
            if (next != null && next.continueNode != null)
                sOut = JapanRoad.P(home ? next.continueNode.transform.position - a : next.transform.position - b);
            JapanRoad.Log("seams " + (home ? "home" : "east") + " index " + i + " gap in " + sIn + " out " + sOut
                + " (prev " + (prev != null ? prev.name.Replace("(Clone)", "") : "none") + ", next " + (next != null ? next.name.Replace("(Clone)", "") : "none") + ")"
                + "; step at start " + Step(a) + ", at end " + Step(b) + " (eastbound lane, road height +2 u minus -2 u)");
        }

        /// <summary>Road height difference across a seam on the eastbound lane (x -5): ground (layer 14) at z+2 minus at z-2.</summary>
        static string Step(Vector3 s)
        {
            float h0, h1;
            if (!Ground(s + new Vector3(-5f, 0f, 2f), out h0) || !Ground(s + new Vector3(-5f, 0f, -2f), out h1)) return "n/a";
            return JapanRoad.F(h0 - h1);
        }

        static bool Ground(Vector3 p, out float y)
        {
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 30f, Vector3.down, out hit, 80f, 1 << 14)) { y = hit.point.y; return true; }
            y = 0f; return false;
        }

        public int ShowNodes(float seconds)
        {
            int n = 0;
            var sc = GetComponent<SpawnContinueC>();
            n += Marker(transform.position, new Vector3(1.5f, 12f, 1.5f), Color.red, seconds);
            if (sc != null && sc.continueNode != null) n += Marker(sc.continueNode.transform.position, new Vector3(1.5f, 12f, 1.5f), Color.blue, seconds);
            if (sc != null && sc.puddleTargets != null)
                foreach (var p in sc.puddleTargets)
                    if (p != null) n += Marker(p.position, new Vector3(0.6f, 0.6f, 0.6f), Color.yellow, seconds);
            JapanRoad.Log("nodes shown k=" + K + " (" + n + " markers, red = start seam, blue = end seam, yellow = pud_)");
            return n;
        }

        int Marker(Vector3 pos, Vector3 size, Color c, float seconds)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.name = "jr_marker";
            go.transform.SetParent(transform, true);
            go.transform.position = pos + Vector3.up * size.y * 0.5f;
            go.transform.rotation = Quaternion.identity;
            var ls = transform.lossyScale;
            go.transform.localScale = new Vector3(size.x / ls.x, size.y / ls.y, size.z / ls.z);
            Material m;
            if (!markerMats.TryGetValue(c, out m) || m == null)
            {
                var sh = Shader.Find("Legacy Shaders/Diffuse") ?? Shader.Find("Diffuse");
                m = new Material(sh) { color = c };
                markerMats[c] = m;
            }
            go.GetComponent<MeshRenderer>().sharedMaterial = m;
            Destroy(go, seconds);
            return 1;
        }

        void OnDestroy()
        {
            if (!live) return;
            JapanRoad.Live.Remove(this);
            JapanRoad.Log("destroyed k=" + K + " (leg deleted)");
        }
    }
}
#endif
