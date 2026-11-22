using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Library for putting things by the road (tasks 23-26, the roadside chapel; task 48 took the chapel out of play and
    /// kept the methods). Nothing here is called in normal play; the only caller is the debug row "Road spot probe".
    /// Know-how in LESSONS.md, section "Placing objects by the road".
    /// Hook idea (not installed any more): Postfix on the private SpawnContinueC.Start. The whole leg is spawned at once,
    /// segment k = RouteGeneratorC.routeAmmo - 1, routeAmmo == 1 is a new leg. Things added to rg.spawnedRouteSegments
    /// are removed by DeleteRoute (motel sleep) together with the leg.
    /// </summary>
    public static class RoadSpots
    {
        const int GroundMask = (1 << 14) | (1 << 17);   // Ground (road mesh), Grass (verge plane)
        const float DefaultScale = 2.3f;                // u/m, the recon estimate (lane 8 u, car body ~5.7 x 10 u)

        /// <summary>Her height in metres: info.txt height_m (task 30), default 1.56 (Fukukitaru's mesh).</summary>
        public static float HerMetres = 1.56f;
        /// <summary>Her rest height in game units (FukuDriver.HeightUnits, set once she is built); 0 = unknown.</summary>
        public static float HerHeight;

        static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        static string F(float v) { return v.ToString("0.0", IC); }
        static string P(Vector3 v) { return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")"; }
        static string Clean(string n) { return n != null ? n.Replace("(Clone)", "") : "?"; }

        static RouteGeneratorC Rg()
        {
            var d = GameObject.FindWithTag("Director");
            return d != null ? d.GetComponent<RouteGeneratorC>() : null;
        }

        /// <summary>Country from the segment prefab name prefix (Road_Hills_ = Germany, CSFR_, HUN_, YUGO_, BUL_, TURK_); null = unknown.</summary>
        public static string Country(string name)
        {
            if (name == null) return null;
            if (name.StartsWith("Road_Hills_")) return "Germany";
            if (name.StartsWith("CSFR_")) return "CSFR";
            if (name.StartsWith("HUN_")) return "Hungary";
            if (name.StartsWith("YUGO_")) return "Yugoslavia";
            if (name.StartsWith("BUL_")) return "Bulgaria";
            if (name.StartsWith("TURK_")) return "Turkey";
            return null;
        }

        // ------------------------------------------------------------ straight-stretch search
        /// <summary>A straight stretch of one segment, found by Windows().</summary>
        public struct Window
        {
            public bool Ok;
            public float Z0, Z1;        // world z range (Z0 = nearer to the segment start in travel order)
            public float Cx, Y;         // road centre x, mean road height
            public float Width;         // x spread of the puddle samples
            public float Lz0, Lz1;      // same range relative to the segment root
        }

        /// <summary>World positions of the segment's puddleTargets: the only road centre samples the game gives (x spread = road width).</summary>
        public static List<Vector3> Samples(SpawnContinueC sc)
        {
            var pts = new List<Vector3>();
            if (sc.puddleTargets != null)
                foreach (var t in sc.puddleTargets) if (t != null) pts.Add(t.position);
            return pts;
        }

        /// <summary>
        /// All straight windows in travel order (east drives -z, home +z). A window: 60-100 u of z with >= 4 samples, two
        /// halves whose x centres differ by <= 4, x spread <= 36, height change <= 4. 30 u at each end of the segment
        /// are left out (seams). The longest window size that exists wins.
        /// </summary>
        public static List<Window> Windows(SpawnContinueC sc, bool home)
        {
            var res = new List<Window>();
            var pts = Samples(sc);
            if (pts.Count < 4) return res;
            float rz = sc.transform.position.z;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts) { lo = Mathf.Min(lo, p.z); hi = Mathf.Max(hi, p.z); }
            lo += 30f; hi -= 30f;
            foreach (float w in new[] { 100f, 80f, 60f })
            {
                for (float a = lo; a + w <= hi; a += 10f)
                {
                    float b = a + w, mid = a + w / 2f;
                    float minx = float.MaxValue, maxx = float.MinValue, miny = float.MaxValue, maxy = float.MinValue, sy = 0f;
                    float aminx = float.MaxValue, amaxx = float.MinValue, bminx = float.MaxValue, bmaxx = float.MinValue;
                    int n = 0, na = 0, nb = 0;
                    foreach (var p in pts)
                    {
                        if (p.z < a || p.z > b) continue;
                        n++; sy += p.y;
                        minx = Mathf.Min(minx, p.x); maxx = Mathf.Max(maxx, p.x);
                        miny = Mathf.Min(miny, p.y); maxy = Mathf.Max(maxy, p.y);
                        if (p.z < mid) { na++; aminx = Mathf.Min(aminx, p.x); amaxx = Mathf.Max(amaxx, p.x); }
                        else { nb++; bminx = Mathf.Min(bminx, p.x); bmaxx = Mathf.Max(bmaxx, p.x); }
                    }
                    if (n < 4 || na == 0 || nb == 0) continue;
                    float ca = (aminx + amaxx) / 2f, cb = (bminx + bmaxx) / 2f;
                    if (Mathf.Abs(ca - cb) > 4f || maxx - minx > 36f || maxy - miny > 4f) continue;
                    var win = new Window { Ok = true, Cx = (ca + cb) / 2f, Y = sy / n, Width = maxx - minx };
                    win.Z0 = home ? a : b; win.Z1 = home ? b : a;
                    win.Lz0 = win.Z0 - rz; win.Lz1 = win.Z1 - rz;
                    res.Add(win);
                }
                if (res.Count > 0) break;
            }
            res.Sort((x, y) => home ? x.Z0.CompareTo(y.Z0) : y.Z0.CompareTo(x.Z0));
            return res;
        }

        /// <summary>Distinct x of the traffic lane nodes (trafficAINodes) as text, relative to cx, within dz of z (z = NaN: whole segment, relative to its root); lanes are 8 u apart, outer ones are the road edge.</summary>
        public static string Lanes(SpawnContinueC sc, float z, float dz)
        {
            var xs = new List<float>();
            float cx = float.IsNaN(z) ? sc.transform.position.x : 0f;
            if (sc.trafficAINodes != null)
                foreach (var t in sc.trafficAINodes)
                {
                    if (t == null) continue;
                    if (!float.IsNaN(z) && Mathf.Abs(t.position.z - z) > dz) continue;
                    float x = Mathf.Round(t.position.x - cx);
                    if (!xs.Contains(x)) xs.Add(x);
                }
            if (xs.Count == 0) return "none";
            xs.Sort();
            var parts = new List<string>();
            foreach (var x in xs) parts.Add(x.ToString("0", IC));
            return string.Join("/", parts.ToArray());
        }

        // ------------------------------------------------------------ the verge spot
        /// <summary>Ground under a down ray from topY, layers Ground (14, road mesh) and Grass (17, verge plane) only.</summary>
        public static bool Ground(Vector3 at, float topY, out RaycastHit hit)
        {
            return Physics.Raycast(new Vector3(at.x, topY, at.z), Vector3.down, out hit, 200f, GroundMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>Lateral offset from the centre where the ground under a down ray turns into Grass (layer 17), 2..40 u; -1 = never. Measures the real road edge.</summary>
        public static float GrassAt(float cx, float z, float y, float side)
        {
            for (float o = 2f; o <= 40f; o += 1f)
            {
                RaycastHit h;
                if (Ground(new Vector3(cx + side * o, 0f, z), y + 30f, out h) && h.collider.gameObject.layer == 17) return o;
            }
            return -1f;
        }

        /// <summary>Text of what stands in a footprint besides the ground (non-trigger colliders; small props by renderer bounds, crate/abandoned-car nodes within 8 u), null = free.</summary>
        public static string Blocked(SpawnContinueC sc, Vector3 pos, Quaternion rot, Vector3 half)
        {
            var c = pos + Vector3.up * (half.y + 0.5f);
            foreach (var col in Physics.OverlapBox(c, half, rot, ~GroundMask, QueryTriggerInteraction.Ignore))
                if (col != null) return "collider " + col.name + " (layer " + col.gameObject.layer + ")";
            var bb = new Bounds(c, new Vector3(half.x * 2f, half.y * 2f, half.x * 2f));
            foreach (var r in sc.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                int l = r.gameObject.layer;
                if (l == 14 || l == 17) continue;
                var s = r.bounds.size;
                if (s.x > 60f || s.z > 60f) continue;   // road/terrain pieces, not props
                if (r.bounds.Intersects(bb)) return "prop " + r.name;
            }
            foreach (var list in new[] { sc.crateNodes, sc.abandonCarNodes })
                if (list != null)
                    foreach (var t in list)
                        if (t != null && (t.position - pos).sqrMagnitude < 8f * 8f) return "node " + t.name;
            return null;
        }

        /// <summary>
        /// Rejects spots behind crash barriers and fences (bug 1): walking line from our lane (x = roadX, height roadY)
        /// straight out to the front of the thing at `at` (front = its half depth toward the road), at knee/waist/chest
        /// height (0.3/0.6/1.0 m x s) and at each offset in dz along z. Any non-trigger collider without a Rigidbody
        /// (cars and crates move) on a line = barrier; ground only as a steep face (normal.y <= 0.6). Null = reachable on foot.
        /// </summary>
        public static string Barrier(float roadX, float roadY, Vector3 at, float front, float side, float[] dz, float s)
        {
            const int mask = ~(1 << 17);   // all but the grass plane
            float endX = at.x - side * (front + 0.2f);
            foreach (float z in dz)
                foreach (float hm in new[] { 0.3f, 0.6f, 1.0f })
                {
                    float h = hm * s;
                    var from = new Vector3(roadX, roadY + h, at.z + z);
                    var to = new Vector3(endX, at.y + h, at.z + z);
                    var d = to - from;
                    float len = d.magnitude;
                    if (len < 0.5f || (endX - roadX) * side <= 0f) continue;
                    foreach (var hit in Physics.RaycastAll(from, d / len, len, mask, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider == null || hit.collider.attachedRigidbody != null) continue;
                        if (hit.collider.gameObject.layer == 14 && hit.normal.y > 0.6f) continue;   // road/terrain surface, walkable
                        return "barrier " + hit.collider.name + " (layer " + hit.collider.gameObject.layer + ", " + F((hit.point.x - roadX) * side) + " u out)";
                    }
                }
            return null;
        }

        /// <summary>A verge spot found by FindSpot.</summary>
        public struct Spot
        {
            public Vector3 Pos;       // on the ground (lowest of 3 ground hits)
            public Quaternion Rot;    // front (+z) faces the road
            public float Side;        // +1 / -1: x direction of the right side of travel
            public float DirZ;        // +1 home, -1 east
            public float Scale;       // u/m used
            public float RoadCx, RoadY, Edge, Zc;   // road centre x, road height, road half width (edge), window centre z
            public string Info;       // one line for the log
        }

        /// <summary>
        /// A spot on the right verge of one segment for a thing of w x h x d metres (front = depth toward the road, d/2 for
        /// a box). Straight windows ahead of minAhead (along travel) are tried in order; the road edge = max(puddle spread,
        /// outer lane node + 4.5, measured switch to grass), spot = edge + 7..12 u. Ground by 3 down rays (normal.y > 0.97,
        /// within 0.6 u of each other and 6 u of the road), then Blocked and Barrier. Fills `why` on failure.
        /// </summary>
        public static bool FindSpot(SpawnContinueC sc, bool home, float minAhead, float w, float h, float d, float across, out Spot res, out string why)
        {
            res = default(Spot);
            why = null;
            float dirZ = home ? 1f : -1f;
            float side = home ? 1f : -1f;   // right of travel: east (-z) -> -x, home (+z) -> +x
            var wins = Windows(sc, home);
            if (wins.Count == 0) { why = "no straight window"; return false; }
            string last = "no window ahead";
            int tried = 0;
            string check;
            float s = Scale(out check);
            foreach (var win in wins)
            {
                float zc = (win.Z0 + win.Z1) / 2f;
                if (zc * dirZ < minAhead) continue;
                tried++;
                float half = win.Width / 2f + 2f;
                if (sc.trafficAINodes != null)
                    foreach (var t in sc.trafficAINodes)
                        if (t != null && Mathf.Abs(t.position.z - zc) < 60f) half = Mathf.Max(half, Mathf.Abs(t.position.x - win.Cx) + 4.5f);
                float grass = GrassAt(win.Cx, zc, win.Y, side);
                if (grass > 0f && grass <= 30f) half = Mathf.Max(half, grass);
                float off = UnityEngine.Random.Range(7f, 12f);
                var spot = new Vector3(win.Cx + side * (half + off), 0f, zc);
                RaycastHit h0, h1, h2;
                float top = win.Y + 50f;
                if (!Ground(spot, top, out h0) || !Ground(spot + Vector3.forward * 4f, top, out h1) || !Ground(spot - Vector3.forward * 4f, top, out h2))
                { last = "no ground"; continue; }
                if (h0.normal.y <= 0.97f || h1.normal.y <= 0.97f || h2.normal.y <= 0.97f) { last = "slope " + F(Mathf.Min(h0.normal.y, Mathf.Min(h1.normal.y, h2.normal.y))); continue; }
                if (Mathf.Abs(h1.point.y - h2.point.y) > 0.6f) { last = "rays disagree " + F(h1.point.y - h2.point.y); continue; }
                if (Mathf.Abs(h0.point.y - win.Y) > 6f) { last = "verge " + F(h0.point.y - win.Y) + " u from road height"; continue; }
                spot.y = Mathf.Min(h0.point.y, Mathf.Min(h1.point.y, h2.point.y));
                var rot = Quaternion.LookRotation(new Vector3(-side, 0f, 0f));
                string b = Blocked(sc, spot, rot, new Vector3(w * s / 2f + 1f, h * s / 2f, d * s / 2f + 1f));
                if (b != null) { last = b; continue; }
                b = Barrier(win.Cx + side * 2f, win.Y, spot, d / 2f * s, side, new[] { 0f, -across * s, across * s }, s);
                if (b != null) { last = b; continue; }
                res = new Spot { Pos = spot, Rot = rot, Side = side, DirZ = dirZ, Scale = s, RoadCx = win.Cx, RoadY = win.Y, Edge = half, Zc = zc };
                string country = Country(sc.name) ?? "?";
                res.Info = country + " " + Clean(sc.name) + " " + P(spot) + " local z " + F(zc - sc.transform.position.z) + ", " + F(half + off)
                    + " u from centre (edge " + F(half) + (grass > 0f ? ", grass at " + F(grass) : ", no grass layer") + ", +" + F(off)
                    + "), ground layer " + h0.collider.gameObject.layer + " (" + h0.collider.name + "), lanes x " + Lanes(sc, zc, 60f)
                    + ", scale " + s.ToString("0.00", IC) + " u/m";
                return true;
            }
            why = tried == 0 ? last : last + " (" + tried + " windows tried)";
            return false;
        }

        /// <summary>u/m: 2.3 by default; her height / HerMetres instead only when the two agree within 15 % (a hat in HeightUnits would inflate it).</summary>
        public static float Scale(out string check)
        {
            check = "n/a (she is not built yet)";
            if (HerHeight <= 0f) return DefaultScale;
            float h = HerHeight / HerMetres;
            bool agree = Mathf.Abs(h / DefaultScale - 1f) <= 0.15f;
            check = h.ToString("0.00", IC) + " (her " + F(HerHeight) + " u / " + HerMetres.ToString("0.00", IC) + " m, "
                + (agree ? "agrees, used" : "off by more than 15 %, 2.3 kept") + ")";
            return agree ? h : DefaultScale;
        }

        // ------------------------------------------------------------ placing
        /// <summary>A GameObject with the mesh, material and a non-trigger BoxCollider (colSize/colCentre in the mesh's own metres), layer 0, no Rigidbody, uniform scale s.</summary>
        public static GameObject Prop(string name, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, float s, Vector3 colSize, Vector3 colCentre)
        {
            var go = new GameObject(name);
            go.layer = 0;
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.transform.localScale = Vector3.one * s;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = colSize;
            bc.center = colCentre;
            bc.isTrigger = false;
            return go;
        }

        /// <summary>A pack OBJ (PropMesh) with its atlas on the ground hit; OBJ origin = ground centre of the footprint. Null if the mesh or the png is missing.</summary>
        public static GameObject PlaceModel(string mesh, string png, Vector3 ground, Quaternion rot, float s, Vector3 colSize, Vector3 colCentre)
        {
            var m = PropMesh.Get(mesh);
            var mat = m != null ? PropMesh.Material(png) : null;
            if (m == null || mat == null) return null;
            return Prop("FukuRoad_" + mesh, m, mat, ground, rot, s, colSize, colCentre);
        }

        static Material grey;

        /// <summary>Grey box of size (units) standing on the ground hit, 0.2 u into the ground (no gap on a slight slope), solid. The fallback when a model is missing; the probe uses it.</summary>
        public static GameObject Place(Vector3 ground, Quaternion rot, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "FukuRoad_box";
            go.layer = 0;
            go.transform.position = ground + Vector3.up * (size.y / 2f - 0.2f);
            go.transform.rotation = rot;
            go.transform.localScale = size;
            if (grey == null)
            {
                var sh = Shader.Find("Legacy Shaders/Diffuse");
                grey = sh != null ? new Material(sh) : null;
                if (grey != null) grey.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            }
            var mr = go.GetComponent<MeshRenderer>();
            if (grey != null && mr != null) mr.sharedMaterial = grey;
            var bc = go.GetComponent<BoxCollider>();
            if (bc != null) bc.isTrigger = false;
            return go;
        }

        // ------------------------------------------------------------ tree copy
        /// <summary>
        /// A copy of ONE tree of the same segment, away from the road (bug 2: cloning a tree group put its other trees on
        /// the road). Only an object whose renderers all belong to one tree: no "Group" in the name, no child named like a
        /// tree, combined bounds <= 14 u across. After placing, the clone's whole bounds must stay outside the road
        /// (edge + 3 u) and its trunk spot must pass the ground + Blocked checks, else it is removed. Added to
        /// rg.spawnedRouteSegments. Returns "name (size)" or "none (reason)". Not covered by the debug probe.
        /// </summary>
        public static string Tree(SpawnContinueC sc, RouteGeneratorC rg, Vector3 spot, float side, float dirZ, float roadCx, float edge)
        {
            GameObject go = null;
            try
            {
                Transform src = null;
                Bounds sb = new Bounds();
                foreach (var t in sc.GetComponentsInChildren<Transform>())
                {
                    if (t == null || t == sc.transform) continue;
                    string n = t.name;
                    if (n.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) < 0 && !n.StartsWith("TurkishBush")) continue;
                    if (n.IndexOf("Group", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    bool group = false;
                    foreach (var c in t.GetComponentsInChildren<Transform>())
                        if (c != t && (c.name.IndexOf("Tree", StringComparison.OrdinalIgnoreCase) >= 0 || c.name.StartsWith("TurkishBush"))) { group = true; break; }
                    if (group) continue;
                    Bounds b;
                    if (!AllBounds(t.gameObject, out b)) continue;
                    if (b.size.x > 14f || b.size.z > 14f) continue;
                    src = t; sb = b; break;
                }
                if (src == null) return "none (no single tree in the segment)";
                float outBy = Mathf.Max(6f, sb.extents.x + 3f);
                var at = spot + new Vector3(side * outBy, 0f, -dirZ * 5f);
                RaycastHit h;
                if (!Ground(at, spot.y + 50f, out h) || h.normal.y <= 0.9f || Mathf.Abs(h.point.y - spot.y) > 2f) return "none (ground)";
                at.y = h.point.y - 0.1f;
                var c2 = Blocked(sc, at, Quaternion.identity, new Vector3(1.5f, 3f, 1.5f));
                if (c2 != null) return "none (" + c2 + ")";
                go = (GameObject)UnityEngine.Object.Instantiate(src.gameObject);
                go.transform.SetParent(null, false);
                go.transform.position = at + (src.position - Base(src));
                go.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f) * src.rotation;
                go.transform.localScale = src.lossyScale;
                go.name = "FukuRoad_tree";
                Bounds cb;
                if (!AllBounds(go, out cb)) { UnityEngine.Object.Destroy(go); return "none (no renderers after copy)"; }
                float near = side > 0f ? cb.min.x - roadCx : roadCx - cb.max.x;   // nearest point of the copy to the road centre
                if (near < edge + 3f)
                {
                    UnityEngine.Object.Destroy(go);
                    return "none (" + Clean(src.name) + " would reach " + F(near) + " u from the centre, road edge " + F(edge) + ")";
                }
                rg.spawnedRouteSegments.Add(go);
                return Clean(src.name) + " (" + F(cb.size.x) + " x " + F(cb.size.y) + " x " + F(cb.size.z) + " u, " + F(near) + " u from the centre)";
            }
            catch (Exception e)
            {
                if (go != null) UnityEngine.Object.Destroy(go);
                Debug.LogWarning("[UncleSwap] RoadSpots: tree copy failed: " + e.Message);
                return "none (error)";
            }
        }

        /// <summary>Combined bounds of the enabled renderers of an object; false if none.</summary>
        public static bool AllBounds(GameObject g, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            foreach (var r in g.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>Bottom centre of a tree's first renderer (where it meets the ground); the tree's pivot is not at its foot.</summary>
        public static Vector3 Base(Transform t)
        {
            var r = t.GetComponentInChildren<Renderer>();
            if (r == null) return t.position;
            var b = r.bounds;
            return new Vector3(t.position.x, b.min.y, t.position.z);
        }

        // ------------------------------------------------------------ debug probe
        static GameObject probeBox;

        /// <summary>
        /// Debug panel "Road spot probe": a verge spot on the nearest live segment ahead of the car (far end >= 120 u ahead,
        /// window centre >= 80 u ahead), a grey 1 m box there for 60 s (gone on the next press). Not Turkey, no stop-off segments.
        /// </summary>
        public static string DebugProbe()
        {
            if (probeBox != null) { UnityEngine.Object.Destroy(probeBox); probeBox = null; }
            var rg = Rg();
            if (rg == null) return Log("none (no route generator)");
            var car = UnityEngine.Object.FindObjectOfType<CarControleScriptC>();
            if (car == null) return Log("none (no car)");
            bool home = rg.drivingTowardsHome;
            float dirZ = home ? 1f : -1f;
            float carAlong = car.transform.position.z * dirZ;
            var cands = new List<SpawnContinueC>();
            foreach (var g in rg.spawnedRouteSegments)
            {
                var sc = g != null ? g.GetComponent<SpawnContinueC>() : null;
                if (sc == null) continue;
                float a = sc.transform.position.z * dirZ, b = (sc.transform.position.z - 1000f) * dirZ;
                if (Mathf.Max(a, b) < carAlong + 120f) continue;
                cands.Add(sc);
            }
            if (cands.Count == 0) return Log("none (no road segment ahead: in a town or at the end of the leg)");
            cands.Sort((x, y) => (x.transform.position.z * dirZ).CompareTo(y.transform.position.z * dirZ));
            var reasons = new List<string>();
            foreach (var sc in cands)
            {
                if (Country(sc.name) == "Turkey") { reasons.Add(Clean(sc.name) + ": Turkey"); continue; }
                if (sc.stopOffTarget != null) { reasons.Add(Clean(sc.name) + ": stop-off"); continue; }
                Spot sp;
                string why;
                if (FindSpot(sc, home, carAlong + 80f, 1f, 1f, 1f, 0.5f, out sp, out why))
                {
                    probeBox = Place(sp.Pos, sp.Rot, Vector3.one * sp.Scale);   // 1 m cube
                    UnityEngine.Object.Destroy(probeBox, 60f);
                    float dist = Vector3.Distance(sp.Pos, car.transform.position);
                    Debug.Log("[UncleSwap] RoadSpots: probe spot " + P(sp.Pos) + " | " + sp.Info + ", " + dist.ToString("0", IC) + " u from the car");
                    return "spot " + P(sp.Pos) + " in " + (Country(sc.name) ?? "?") + ", " + dist.ToString("0", IC) + " u from the car, grey box for 60 s";
                }
                reasons.Add(Clean(sc.name) + ": " + why);
                if (reasons.Count >= 3) break;
            }
            return Log("none (" + string.Join("; ", reasons.ToArray()) + ")");
        }

        static string Log(string text)
        {
            Debug.Log("[UncleSwap] RoadSpots: probe " + text);
            return text;
        }
    }
}
