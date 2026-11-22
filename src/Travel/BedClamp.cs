using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Tasks 10 + 12: in the motel bed her body, head and arms stay on the mattress.
    /// Task 10 took the nearest BedLogicC, but that is the PLAYER's bed (user log: "nearest 2.00 legs").
    /// The uncle's bed is wherever UncleBedTriggerC sat him. Task 12 picks the bed from every mesh renderer and collider
    /// under the lying uncle's hips (logged once), and falls back to the edge he sat on (one-sided clamp) if none fits.
    /// The mattress is a rectangle on two horizontal world axes; every frame her key points are measured and her root
    /// is shifted to keep them inside.
    /// </summary>
    public partial class FukuDriver
    {
        bool bedHave;                       // a rectangle (or at least the sitting edge) is known
        string bedSrc;                      // "mesh <name>" / "edge <trigger>" for the log
        Vector3 bedO, bedE1, bedE2;          // origin + two horizontal unit axes (world)
        float bedLo1, bedHi1, bedLo2, bedHi2; // limits along them (world units, inset; may be infinite for the edge)
        Vector3 bedLongDir;                  // world, horizontal, along the bed's long side
        readonly List<Transform> bedPoints = new List<Transform>();
        Vector2 bedClampS, bedClampV;
        Vector2 bedOver;                     // last needed shift, for the log
        float bedLogT;
        bool bedShiftLogged, bedFitLogged;
        float bedSettleT;                    // how long the uncle has been lying flat (axis wait)

        void BedClampReset()
        {
            bedHave = false;
            bedSrc = null;
            bedClampS = bedClampV = Vector2.zero;
            bedLogT = 0f;
            bedShiftLogged = bedFitLogged = false;
            bedSettleT = 0f;
        }

        struct BedCand
        {
            public string Name, Path, Kind, Comps;
            public int Rank;                // lower is better: 0 mesh, 1 box collider, 2 other collider (AABB), 3 trigger
            public Vector3 O, E1, E2;
            public float Lo1, Hi1, Lo2, Hi2, Top;
            public bool Contains, Plausible;
        }

        static string BedPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static string BedComps(GameObject g)
        {
            var sb = new StringBuilder();
            foreach (var c in g.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(c.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>Box -> horizontal rectangle: from 8 world corners and the box's own axes (the two least vertical).</summary>
        static void BedRect(ref BedCand c, Vector3[] corners, Vector3 ax, Vector3 ay, Vector3 az)
        {
            Vector3[] axes = { ax, ay, az };
            int up = 0;
            float bu = -1f;
            for (int k = 0; k < 3; k++)
            {
                float dp = Mathf.Abs(Vector3.Dot(axes[k].normalized, Vector3.up));
                if (dp > bu) { bu = dp; up = k; }
            }
            c.E1 = Vector3.ProjectOnPlane(axes[(up + 1) % 3], Vector3.up).normalized;
            c.E2 = Vector3.ProjectOnPlane(axes[(up + 2) % 3], Vector3.up).normalized;
            Vector3 o = Vector3.zero;
            foreach (var p in corners) o += p;
            c.O = o / corners.Length;
            c.Lo1 = c.Lo2 = float.MaxValue; c.Hi1 = c.Hi2 = float.MinValue; c.Top = float.MinValue;
            foreach (var p in corners)
            {
                float a = Vector3.Dot(p - c.O, c.E1), b = Vector3.Dot(p - c.O, c.E2);
                c.Lo1 = Mathf.Min(c.Lo1, a); c.Hi1 = Mathf.Max(c.Hi1, a);
                c.Lo2 = Mathf.Min(c.Lo2, b); c.Hi2 = Mathf.Max(c.Hi2, b);
                c.Top = Mathf.Max(c.Top, p.y);
            }
        }

        static Vector3[] BedCorners(Bounds b, Matrix4x4 m)
        {
            var r = new Vector3[8];
            for (int i = 0; i < 8; i++)
                r[i] = m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z));
            return r;
        }

        bool BedSkip(Transform t)
        {
            if (t == null) return true;
            if (t.IsChildOf(transform)) return true;                                   // the uncle
            if (girlRoot != null && t.IsChildOf(girlRoot)) return true;                 // her
            if (suitcaseAnchor != null && t.IsChildOf(suitcaseAnchor)) return true;     // the suitcase on the floor
            var cam = Camera.main;
            if (cam != null && t.IsChildOf(cam.transform.root)) return true;            // the player
            return false;
        }

        /// <summary>
        /// The uncle's bed: the box (mesh or collider) whose rectangle holds his lying hips, with a top between the floor
        /// and his hips and a bed-like size. Called once per night, after he lies flat.
        /// </summary>
        bool BedFind(Vector3 hip)
        {
            BedClampReset();
            float leg = uAnkleL != null && uHip != null ? Mathf.Max(0.05f, (uHip.position - uAnkleL.position).magnitude) : 1f;
            float floorY = bedSurveyed ? bedFloorY : hip.y - 1.2f * leg;

            // the trigger that sat him down: nearest to his hips
            UncleBedTriggerC trig = null;
            float td = float.MaxValue;
            foreach (var t in Object.FindObjectsOfType<UncleBedTriggerC>())
            {
                float d = Vector3.Distance(t.transform.position, hip);
                if (d < td) { td = d; trig = t; }
            }
            if (trig != null && td > 4f * leg) trig = null;

            var cands = new List<BedCand>();
            float r = 3f * leg;
            foreach (var mr in Object.FindObjectsOfType<MeshRenderer>())
            {
                if (mr == null || !mr.enabled || BedSkip(mr.transform)) continue;
                Bounds wb = mr.bounds;
                if (wb.SqrDistance(hip) > r * r) continue;
                var c = new BedCand { Name = mr.name, Path = BedPath(mr.transform), Comps = BedComps(mr.gameObject) };
                var mf = mr.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh != null && !mesh.name.StartsWith("Combined Mesh"))
                {
                    var tr = mr.transform;
                    BedRect(ref c, BedCorners(mesh.bounds, tr.localToWorldMatrix), tr.right, tr.up, tr.forward);
                    c.Kind = "mesh"; c.Rank = 0;
                }
                else
                {
                    BedRect(ref c, BedCorners(wb, Matrix4x4.identity), Vector3.right, Vector3.up, Vector3.forward);
                    c.Kind = "mesh-aabb"; c.Rank = 0;
                }
                cands.Add(c);
            }
            foreach (var col in UnityEngine.Physics.OverlapSphere(hip, r, ~0, QueryTriggerInteraction.Collide))
            {
                if (col == null || BedSkip(col.transform)) continue;
                var c = new BedCand { Name = col.name, Path = BedPath(col.transform), Comps = BedComps(col.gameObject) };
                var box = col as BoxCollider;
                if (box != null)
                {
                    var tr = col.transform;
                    BedRect(ref c, BedCorners(new Bounds(box.center, box.size), tr.localToWorldMatrix), tr.right, tr.up, tr.forward);
                    c.Kind = "box"; c.Rank = 1;
                }
                else
                {
                    BedRect(ref c, BedCorners(col.bounds, Matrix4x4.identity), Vector3.right, Vector3.up, Vector3.forward);
                    c.Kind = col.GetType().Name; c.Rank = 2;
                }
                if (col.isTrigger) { c.Kind += "-trigger"; c.Rank = 3; }
                cands.Add(c);
            }

            for (int i = 0; i < cands.Count; i++)
            {
                var c = cands[i];
                float a = Vector3.Dot(hip - c.O, c.E1), b = Vector3.Dot(hip - c.O, c.E2), tol = 0.05f * leg;
                float w1 = c.Hi1 - c.Lo1, w2 = c.Hi2 - c.Lo2;
                c.Contains = a >= c.Lo1 - tol && a <= c.Hi1 + tol && b >= c.Lo2 - tol && b <= c.Hi2 + tol;
                c.Plausible = c.Contains
                    && Mathf.Min(w1, w2) >= 0.4f * leg && Mathf.Max(w1, w2) >= 0.9f * leg && Mathf.Max(w1, w2) <= 4f * leg
                    && c.Top >= floorY + 0.12f * leg && c.Top <= hip.y + 0.5f * leg;
                cands[i] = c;
            }

            // one-time survey for the next bug report: what is around his hips
            cands.Sort((x, y) => x.Contains != y.Contains ? (x.Contains ? -1 : 1) : x.Rank.CompareTo(y.Rank));
            Debug.Log("[UncleSwap] bed: " + cands.Count + " candidates within 3 legs of his hips, leg " + leg.ToString("F2")
                + ", floor " + (bedSurveyed ? "surveyed" : "guessed") + " " + (hip.y - floorY).ToString("F2") + " below hips, trigger "
                + (trig != null ? BedPath(trig.transform) + " at " + trig.transform.position.ToString("F2") + " fwd " + trig.transform.forward.ToString("F2") : "none"));
            for (int i = 0; i < cands.Count && i < 20; i++)
            {
                var c = cands[i];
                Debug.Log("[UncleSwap] bed:   cand " + c.Kind + " " + c.Path + " [" + c.Comps + "] "
                    + ((c.Hi1 - c.Lo1) / leg).ToString("F2") + " x " + ((c.Hi2 - c.Lo2) / leg).ToString("F2") + " legs, top "
                    + ((c.Top - floorY) / leg).ToString("F2") + " legs above floor" + (c.Contains ? ", holds hips" : "") + (c.Plausible ? ", PLAUSIBLE" : ""));
            }

            BedCand pick = new BedCand();
            bool found = false;
            foreach (var c in cands)
                if (c.Plausible && (!found || c.Rank < pick.Rank || (c.Rank == pick.Rank && (c.Hi1 - c.Lo1) * (c.Hi2 - c.Lo2) > (pick.Hi1 - pick.Lo1) * (pick.Hi2 - pick.Lo2))))
                { pick = c; found = true; }

            if (found)
            {
                float in1 = 0.03f * (pick.Hi1 - pick.Lo1), in2 = 0.03f * (pick.Hi2 - pick.Lo2);
                bedO = pick.O; bedE1 = pick.E1; bedE2 = pick.E2;
                bedLo1 = pick.Lo1 + in1; bedHi1 = pick.Hi1 - in1;
                bedLo2 = pick.Lo2 + in2; bedHi2 = pick.Hi2 - in2;
                bedLongDir = pick.Hi1 - pick.Lo1 >= pick.Hi2 - pick.Lo2 ? bedE1 : bedE2;
                bedSrc = "mesh " + pick.Name;
                Debug.Log("[UncleSwap] bed: mesh " + pick.Kind + " " + pick.Path + " found, "
                    + ((pick.Hi1 - pick.Lo1) / leg).ToString("F2") + " x " + ((pick.Hi2 - pick.Lo2) / leg).ToString("F2")
                    + " legs, long side " + bedLongDir.ToString("F2"));
            }
            else if (trig != null)
            {
                // no bed box: at least the long edge he sat on. Out of the bed = the way he faced while sitting
                Vector3 outDir = Vector3.ProjectOnPlane(trig.transform.forward, Vector3.up).normalized;
                if (outDir.sqrMagnitude < 1e-6f) return false;
                bool flipped = Vector3.Dot(hip - trig.transform.position, outDir) > 0f;   // his lying hips must be behind the edge
                if (flipped) outDir = -outDir;
                bedO = trig.transform.position;
                bedE2 = outDir;
                bedE1 = Vector3.Cross(Vector3.up, outDir).normalized;
                bedLo1 = float.NegativeInfinity; bedHi1 = float.PositiveInfinity;
                bedLo2 = float.NegativeInfinity; bedHi2 = 0f;
                bedLongDir = bedE1;
                bedSrc = "edge " + trig.name;
                Debug.Log("[UncleSwap] bed: no bed mesh fits, clamping to the edge he sat on at " + bedO.ToString("F2")
                    + ", out " + outDir.ToString("F2") + (flipped ? " (trigger faced into the bed, flipped)" : ""));
            }
            else
            {
                Debug.Log("[UncleSwap] bed: no bed mesh and no UncleBedTriggerC near his hips");
                return false;
            }
            bedHave = true;

            bedPoints.Clear();
            foreach (var n in new[] { "Head", "Chest", "Elbow_L", "Elbow_R", "Wrist_L", "Wrist_R", "Knee_L", "Knee_R", "Ankle_L", "Ankle_R" })
            {
                Transform t;
                if (boneByName != null && boneByName.TryGetValue(n, out t)) bedPoints.Add(t);
            }
            return true;
        }

        /// <summary>Her lying axis along the bed's long side (the closer of the two directions) when within 60 deg.</summary>
        Vector3 BedSnapAxis(Vector3 ua)
        {
            if (!bedHave || ua.sqrMagnitude < 1e-6f || bedLongDir.sqrMagnitude < 1e-6f) return ua;
            float ang = Vector3.Angle(ua, bedLongDir);
            if (ang > 90f) ang = 180f - ang;
            Debug.Log("[UncleSwap] bed: uncle axis is " + ang.ToString("F0") + " deg off the long side" + (ang <= 60f ? ", snapped" : ", kept"));
            if (ang > 60f) return ua;
            return (Vector3.Dot(ua, bedLongDir) >= 0f ? bedLongDir : -bedLongDir) * ua.magnitude;
        }

        static float BedFit(float mn, float mx, float lo, float hi)
        {
            if (mx - mn >= hi - lo) return 0.5f * (lo + hi) - 0.5f * (mn + mx);   // wider than the mattress: centre
            if (mn < lo) return lo - mn;
            if (mx > hi) return hi - mx;
            return 0f;
        }

        /// <summary>After the clip is applied: the world shift that brings every key point inside the mattress.</summary>
        Vector3 BedClampShift(float w)
        {
            if (!bedHave || bedPoints.Count == 0) return Vector3.zero;
            float mn1 = float.MaxValue, mx1 = float.MinValue, mn2 = float.MaxValue, mx2 = float.MinValue;
            foreach (var t in bedPoints)
            {
                Vector3 d = t.position - bedO;
                float a = Vector3.Dot(d, bedE1), b = Vector3.Dot(d, bedE2);
                if (a < mn1) mn1 = a; if (a > mx1) mx1 = a;
                if (b < mn2) mn2 = b; if (b > mx2) mx2 = b;
            }
            // her root is reset from the uncle every frame, so this is the unshifted pose: the shift is absolute
            float want1 = BedFit(mn1, mx1, bedLo1, bedHi1), want2 = BedFit(mn2, mx2, bedLo2, bedHi2);
            bedOver = new Vector2(want1, want2);
            if (!bedFitLogged && w >= 0.99f)
            {
                bedFitLogged = true;
                float leg = uAnkleL != null && uHip != null ? Mathf.Max(0.05f, (uHip.position - uAnkleL.position).magnitude) : 1f;
                bool wide = mx2 - mn2 >= bedHi2 - bedLo2 || mx1 - mn1 >= bedHi1 - bedLo1;
                Debug.Log("[UncleSwap] bed: clip " + gestures.BedName(bedClip) + " spans " + ((mx1 - mn1) / leg).ToString("F2") + " x "
                    + ((mx2 - mn2) / leg).ToString("F2") + " legs, bed " + ((bedHi1 - bedLo1) / leg).ToString("F2") + " x "
                    + ((bedHi2 - bedLo2) / leg).ToString("F2") + (wide ? ", WIDER than the bed: centred, still sticks out" : ", fits"));
            }
            float dt = Time.deltaTime;
            bedClampS.x = Mathf.SmoothDamp(bedClampS.x, want1, ref bedClampV.x, 0.3f, float.MaxValue, dt);
            bedClampS.y = Mathf.SmoothDamp(bedClampS.y, want2, ref bedClampV.y, 0.3f, float.MaxValue, dt);
            if (!bedShiftLogged && (Mathf.Abs(want1) > 1e-3f || Mathf.Abs(want2) > 1e-3f))
            {
                bedShiftLogged = true;
                Debug.Log("[UncleSwap] bed: shift applied, needed " + bedOver.ToString("F2") + " (" + bedSrc + ")");
            }
            return (bedE1 * bedClampS.x + bedE2 * bedClampS.y) * w;
        }

        void BedClampLog()
        {
            bedLogT += Time.deltaTime;
            if (bedLogT < 6f || !bedHave) return;
            bedLogT = 0f;
            Debug.Log("[UncleSwap] bed: pose " + gestures.BedName(bedClip) + " t " + bedT.ToString("F0") + " root "
                + hipLink.Girl.position.ToString("F2") + " " + bedSrc + " needed shift " + bedOver.ToString("F2")
                + " shift applied " + bedClampS.ToString("F2"));
        }
    }
}
