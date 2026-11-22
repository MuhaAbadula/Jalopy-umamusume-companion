using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 40, N6 "hat on you" (concept 07 rev 2): by day, dry. Her driver-side hand to her hat brim, she lifts it off, a flick
    /// of the arm towards him, the hat flies (0.4 s arc, one spin) onto his head: a static copy of the hat prop as a child of
    /// Camera.main, brim front HatFront u ahead of the eye and HatUp u above the eye line (recon 36 §5), her own hat renderer
    /// hidden. Line prank_hat, giggle, 10 s caught window (pat), no undone. After 20 s she reaches for it, it flies back onto her
    /// head (prank_hat_back) and her hat shows again. The copy keeps its own clock (HatCopy, a component on it): when he leaves the
    /// car, or nobody took it back in time, or the scene goes, her hat is back at once.
    /// Also here: her hand on the brim (WantBrim / PoseBrim), used by N5 (she straightens her hat in the mirror) and N6.
    /// Log: Prank: N6 start|acted|done|caught|skip ...; Prank: N6 hat back (her hand | at once, why).
    /// </summary>
    public partial class Life
    {
        const float HatUp = 0.13f;      // brim front, height above his eye (game units); bug 1 (v0.42.1): top 40 % of his view, nearest brim 0.16 u (near clip 0.1)
        const float HatFront = 0.34f;   // brim front, ahead of his eye
        const float HatOnHimFor = 20f;
        const float HatFly = 0.4f;

        /// <summary>The hat is not on her head (in her hand, flying or on him): no hat hold, no N5.</summary>
        public static bool HatAway { get { return HatCopy.Live != null; } }

        // ------------------------------------------------------------ her hand on the brim
        float brimWant, brimW, brimLift, brimWiggle;
        bool brimOuter;

        /// <summary>This frame her hand goes to her hat brim (outer = window-side arm). lift = hand raised (head radii), wiggle = a
        /// small side-to-side straightening.</summary>
        void WantBrim(bool outer, float w, float lift, float wiggle)
        {
            if (w <= brimWant) return;
            brimWant = w;
            brimOuter = outer;
            brimLift = lift;
            brimWiggle = wiggle;
        }

        /// <summary>From PosePranks (after the reach): palm on the brim towards his side, like the window hat hold. Then the copy
        /// in her hand follows the hand.</summary>
        void PoseBrim(float dt)
        {
            brimW = Mathf.Clamp01(brimWant);
            float lift = brimLift, wig = brimWiggle;
            bool outerArm = brimOuter;
            brimWant = 0f; brimLift = 0f; brimWiggle = 0f;
            if (hatProp != null && armL != null && armR != null && head != null && brimW > 0.001f && hatRing != null)
            {
                Vector3 fwd, up, winDir, c;
                Arm outer, inner;
                float r;
                ArmSides(out outer, out inner, out fwd, out up, out winDir);
                Arm a = outerArm ? outer : inner;
                if (HeadSphere(out c, out r))
                {
                    Quaternion H = head.Deform();
                    Vector3 hf = H * Vector3.forward, hu = H * Vector3.up;
                    Vector3 side = outerArm ? winDir : -winDir;
                    Vector3 dirW = (hf + side * 0.8f).normalized;
                    Vector3 grip = c;
                    float bestD = float.MinValue;
                    foreach (var p in hatRing)
                    {
                        Vector3 wp = head.T.TransformPoint(p);
                        float d = Vector3.Dot(wp - c, dirW);
                        if (d > bestD) { bestD = d; grip = wp; }
                    }
                    Vector3 outward = Vector3.ProjectOnPlane(grip - c, hu);
                    outward = outward.sqrMagnitude > 1e-8f ? outward.normalized : dirW;
                    Vector3 across = Vector3.Cross(hu, outward).normalized;
                    Vector3 knuck = Knuckles(a);
                    float k = (knuck - a.Hand.position).magnitude;
                    if (k < 1e-5f) k = ArmLen(a) * 0.12f;
                    Vector3 wrist = grip - outward * (k * 1.2f) + hu * (r * (0.12f + lift))
                        + across * (r * 0.1f * wig * Mathf.Sin(Time.time * Mathf.PI * 2f * 2.2f));
                    float sw = Mathf.SmoothStep(0f, 1f, brimW);
                    ReachLog("hat brim (" + (outerArm ? "window" : "driver") + " hand)", a, wrist);
                    SolveArm(a, wrist, -up + side * 0.6f, sw);
                    Vector3 cur = Knuckles(a) - a.Hand.position;
                    Vector3 axis = (outward - hu * 0.3f).normalized;
                    if (cur.sqrMagnitude > 1e-10f)
                        a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(cur, axis), sw) * a.Hand.rotation;
                    PoseFingers(a, false, sw * 0.6f);
                }
            }
            HatCopy.FollowHand();
        }

        /// <summary>The driver-side hand (towards him).</summary>
        Transform InnerHand()
        {
            if (armL == null || armR == null) return null;
            Vector3 fwd, up, winDir;
            Arm outer, inner;
            ArmSides(out outer, out inner, out fwd, out up, out winDir);
            return inner.Hand;
        }

        // ------------------------------------------------------------ the copy of her hat
        /// <summary>
        /// The flying / worn copy of her hat. Lives on its own GameObject with its own clock (Time.time), so it gives the hat back
        /// even when her Life does not run: he leaves his seat (SeatLogicC.isSat), the timer runs out (BackBy + 12 s), the camera
        /// or her rig goes away (OnDestroy). States: Hand (follows her hand), ToHim (flight), OnHim, ToHer (flight), then gone.
        /// </summary>
        class HatCopy : MonoBehaviour
        {
            public static HatCopy Live;
            enum St { Hand, ToHim, OnHim, ToHer }
            St st;
            SkinnedMeshRenderer worn;
            Transform head, hand, rootT, cam;
            SeatLogicC seat;
            Vector3 handOff, slotPos, upL, fromPos;
            Quaternion rootRot, slotRot, fromRot;
            Vector3 fromScale, toScale;
            float t0, arc;
            public float BackBy;   // Time.time after which the copy gives the hat back by itself
            public string Note;

            static Mesh mesh;
            static Prop meshOf;
            static Vector3 fL, uL;
            static float xC, yFront, zFront;   // in the "hat frame" (head-local turned so fL = +z, uL = +y), head-local units
            static Vector3[] frameVerts;

            /// <summary>Static mesh in her head bone's local space (as the hat sits at rest), plus its size in the hat frame.</summary>
            static bool Build(Prop p, Transform headT, Quaternion headRest, out string why)
            {
                why = null;
                if (mesh != null && meshOf == p) return true;
                var bones = p.Renderer.bones;
                var bp = p.Renderer.sharedMesh.bindposes;
                int hi = Array.IndexOf(bones, headT);
                if (hi < 0 || hi >= bp.Length) { why = "head bone not in the hat renderer"; return false; }
                var pos = p.RestPositions;
                var nrm = p.RestNormals;
                int nv = pos.Length;
                var v = new Vector3[nv];
                var n = new Vector3[nv];
                for (int i = 0; i < nv; i++)
                {
                    v[i] = bp[hi].MultiplyPoint3x4(pos[i]);
                    n[i] = bp[hi].MultiplyVector(nrm[i]).normalized;
                }
                if (mesh != null) Destroy(mesh);
                mesh = new Mesh { name = "HatOnHim" };
                mesh.vertices = v;
                mesh.normals = n;
                mesh.uv = p.UVs;
                mesh.subMeshCount = p.Triangles.Count;
                for (int k = 0; k < p.Triangles.Count; k++) mesh.SetTriangles(p.Triangles[k], k);
                mesh.RecalculateBounds();
                meshOf = p;
                Quaternion inv = Quaternion.Inverse(headRest);
                fL = inv * Vector3.forward;
                uL = inv * Vector3.up;
                Quaternion toFrame = Quaternion.Inverse(Quaternion.LookRotation(fL, uL));
                frameVerts = new Vector3[nv];
                float xmin = float.MaxValue, xmax = float.MinValue;
                zFront = float.MinValue;
                for (int i = 0; i < nv; i++)
                {
                    Vector3 f = toFrame * v[i];
                    frameVerts[i] = f;
                    xmin = Mathf.Min(xmin, f.x); xmax = Mathf.Max(xmax, f.x);
                    if (f.z > zFront) { zFront = f.z; yFront = f.y; }
                }
                xC = (xmin + xmax) * 0.5f;
                return true;
            }

            /// <summary>Her hand takes the hat off her head: the copy appears where her hat is, her hat hides.</summary>
            public static string Grab(Prop p, Transform headT, Quaternion headRest, Transform handT, Transform root, SeatLogicC seat)
            {
                if (Live != null) return "hat already away";
                if (p == null || p.Renderer == null || headT == null || handT == null || root == null) return "no hat";
                string why;
                if (!Build(p, headT, headRest, out why)) return why;
                var go = new GameObject("FukuHatCopy");
                go.layer = p.Renderer.gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = p.Materials;
                go.transform.SetParent(headT, false);   // exactly where the worn hat is
                go.transform.SetParent(root, true);
                var h = go.AddComponent<HatCopy>();
                h.worn = p.Renderer;
                h.head = headT;
                h.hand = handT;
                h.rootT = root;
                h.seat = seat;
                h.handOff = root.InverseTransformVector(go.transform.position - handT.position);
                h.rootRot = go.transform.localRotation;
                h.st = St.Hand;
                h.BackBy = Time.time + 30f;
                Props.SetHatless(true);   // bug 2: hat and body hair off, head_nohat on
                Live = h;
                return null;
            }

            /// <summary>From PoseBrim (after her pose): in her hand it follows the hand.</summary>
            public static void FollowHand()
            {
                var h = Live;
                if (h == null || h.st != St.Hand || h.hand == null || h.rootT == null) return;
                h.transform.position = h.hand.position + h.rootT.TransformVector(h.handOff);
                h.transform.localRotation = h.rootRot;
            }

            /// <summary>Let go towards his head. Returns a note for the log (near clip, what of the brim is in view).</summary>
            public string Throw(Camera c, float backAfter)
            {
                cam = c.transform;
                transform.SetParent(cam, true);
                fromPos = transform.localPosition;
                fromRot = transform.localRotation;
                fromScale = transform.localScale;
                toScale = fromScale;
                float s = Mathf.Abs(toScale.x);
                const float roll = 4f;   // lands a little crooked
                slotRot = Quaternion.AngleAxis(roll, Vector3.forward) * Quaternion.Inverse(Quaternion.LookRotation(fL, uL));
                slotPos = new Vector3(-s * xC, HatUp - s * yFront, HatFront - s * zFront);
                upL = Vector3.up;
                arc = 0.25f;
                t0 = Time.time;
                st = St.ToHim;
                BackBy = Time.time + backAfter + 12f;
                // what of it is in his view, and how close (the brim must stay past the near clip plane)
                float tanV = Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * c.aspect;
                float nearest = float.MaxValue, top = float.MaxValue;
                Quaternion roll0 = Quaternion.AngleAxis(roll, Vector3.forward);
                foreach (var f in frameVerts)
                {
                    Vector3 q = slotPos + roll0 * (f * s);
                    if (q.z <= 0f || Mathf.Abs(q.x) > q.z * tanH || Mathf.Abs(q.y) > q.z * tanV) continue;
                    nearest = Mathf.Min(nearest, q.z);
                    top = Mathf.Min(top, q.y / q.z);
                }
                return "near clip " + c.nearClipPlane.ToString("F2") + ", fov " + c.fieldOfView.ToString("F0") + ", scale " + s.ToString("F3")
                    + (nearest < float.MaxValue
                        ? ", nearest brim in view " + nearest.ToString("F2") + " u" + (nearest < c.nearClipPlane * 1.2f ? " (CLIPS)" : "")
                          + ", brim covers the top " + (100f * (tanV - top) / (2f * tanV)).ToString("F0") + " % of the view"
                        : ", brim not in view");
            }

            public bool OnHim { get { return st == St.OnHim; } }
            public bool InHand { get { return st == St.Hand; } }

            /// <summary>Back onto her head by a flight (her hand reached for it).</summary>
            public void FlyBack()
            {
                if (head == null) { Back("her head is gone"); return; }
                transform.SetParent(head, true);
                fromPos = transform.localPosition;
                fromRot = transform.localRotation;
                fromScale = transform.localScale;
                toScale = Vector3.one;
                slotPos = Vector3.zero;
                slotRot = Quaternion.identity;
                upL = uL;
                arc = 0.2f / Mathf.Max(1e-4f, Mathf.Abs(head.lossyScale.x));
                t0 = Time.time;
                st = St.ToHer;
            }

            /// <summary>Hat back on her at once (no flight).</summary>
            public void Back(string why)
            {
                if (Live == this) Live = null;
                if (worn != null) Props.SetHatless(false);
                Note = why;
                PLog("N6 hat back " + why);
                Destroy(gameObject);
            }

            void LateUpdate()
            {
                if (st == St.ToHim || st == St.ToHer)
                {
                    float t = Mathf.Clamp01((Time.time - t0) / HatFly);
                    float e = Mathf.SmoothStep(0f, 1f, t);
                    transform.localPosition = Vector3.Lerp(fromPos, slotPos, e) + upL * (arc * 4f * t * (1f - t));
                    transform.localRotation = Quaternion.Slerp(fromRot, slotRot, e) * Quaternion.AngleAxis(-360f * (1f - e), uL);
                    transform.localScale = Vector3.Lerp(fromScale, toScale, e);
                    if (t >= 1f)
                    {
                        if (st == St.ToHim) st = St.OnHim;
                        else { Back("(her hand)"); return; }
                    }
                }
                if (st == St.ToHer) return;
                if (seat != null && !seat.isSat && st != St.Hand) { Back("at once (he left his seat)"); return; }
                if (Time.time > BackBy) Back("at once (timer)");
                else if (worn == null) { if (Live == this) Live = null; Destroy(gameObject); }
            }

            void OnDestroy()
            {
                if (Live == this) Live = null;
                if (worn != null) Props.SetHatless(false);
            }
        }

        // ------------------------------------------------------------ N6
        float hatBackAt = -1f, hatBackT = -1f;
        bool hatBackActed;

        class PrankHat : PrankKind
        {
            public override string Id { get { return "N6"; } }
            public override string Line { get { return "prank_hat"; } }
            bool released;

            public override string Can()
            {
                if (L.hatProp == null) return "no hat prop";
                if (HatAway) return "hat already away";
                if (L.car == null) return "no car";
                if (L.car.rainLvl != 0 || L.raining) return "raining";
                if (!L.IsDay()) return "night";
                if (!L.PlayerSeated()) return "player out";
                if (Camera.main == null) return "no camera";
                if (L.armL == null || L.armR == null || L.head == null) return "no arms";
                return null;
            }

            public override Beat Start(out string note)
            {
                released = false;
                note = "hat off";
                // brim 0.05-0.95 s (off and up from 0.5 s), flick at him 0.65-1.0 s, let go at 1.0 s
                return new Beat { Dur = 2.4f, Look = Look.Player, LookW = 0.8f, Face = "smug", FaceW = 0.6f, ActAt = 1.0f };
            }

            public override void Arm(Beat b, float env)
            {
                float T = b.T;
                if (!released)
                {
                    float wB = Mathf.Clamp01(Mathf.Min((T - 0.05f) / 0.35f, (0.95f - T) / 0.25f));
                    float lift = Mathf.SmoothStep(0f, 0.9f, Mathf.Clamp01((T - 0.5f) / 0.3f));
                    L.WantBrim(false, wB, lift, 0f);
                    if (T >= 0.5f && HatCopy.Live == null)
                    {
                        string why = HatCopy.Grab(L.hatProp, L.head.T, L.head.Rest, L.InnerHand(), L.uncle, L.playerSeatLogic);
                        if (why != null) { PLog("N6 grab failed: " + why); released = true; }
                    }
                }
                var cam = Camera.main;
                if (cam != null)
                    L.WantReachTo("his head", cam.transform, false, false, Mathf.Clamp01(Mathf.Min((T - 0.65f) / 0.35f, (b.Dur - 0.3f - T) / 0.6f)));
            }

            public override string Act(float now)
            {
                var h = HatCopy.Live;
                var cam = Camera.main;
                if (h == null || !h.InHand) return "hat not in her hand";
                if (cam == null) { h.Back("at once (no camera)"); return "no camera"; }
                if (!L.PlayerSeated()) { h.Back("at once (he is out)"); return "player out"; }
                string note = h.Throw(cam, HatOnHimFor);
                released = true;
                L.hatBackAt = now + HatOnHimFor;
                L.hatBackT = -1f;
                How = "hat thrown on his head (" + note + ")";
                return null;
            }

            public override float CheckDelay { get { return HatFly + 0.2f; } }

            public override string Checked(float now)
            {
                var h = HatCopy.Live;
                return h != null && h.OnHim ? null : "hat did not land";
            }

            public override void End()
            {
                // cut before she let go (or it never landed): her hat back on her
                var h = HatCopy.Live;
                if (h != null && h.InHand) h.Back("at once (prank cut)");
            }

            /// <summary>20 s after the throw she reaches for it and it flies back (her hand, prank_hat_back). Busy for 8 s more,
            /// out of her seat or asleep: back at once.</summary>
            public override void Tick(float now, bool inCar, bool idle)
            {
                var h = HatCopy.Live;
                if (h == null && L.hatBackT < 0f) { L.hatBackAt = -1f; return; }
                if (h != null && L.hatBackT < 0f)
                {
                    if (L.pk == this && L.pState != PState.Open) return;   // still throwing
                    if (h.InHand) return;
                    if (!inCar || L.asleepNow) { h.Back("at once (" + (!inCar ? "she left her seat" : "asleep") + ")"); return; }
                    if (L.hatBackAt < 0f || now < L.hatBackAt || !h.OnHim) return;
                    // a glance beat may be cut; her own moves, gestures, pats and other pranks are waited for
                    var b = L.beat;
                    bool beatBusy = b != null && (b.Name.StartsWith("prank_") || (b.ActAt >= 0f && !b.Acted));
                    if (!idle || beatBusy || (L.PrankBusy && L.pk != this) || L.act != Life.Act.None || L.GestureWeight > 0.01f || L.patT >= 0f)
                    {
                        if (now > L.hatBackAt + 8f) h.Back("at once (she was busy)");
                        return;
                    }
                    if (L.pk == this) L.PrankEnd("end, hat back");
                    L.beat = null;
                    L.hatBackT = 0f;
                    L.hatBackActed = false;
                    L.nextBeat = Mathf.Max(L.nextBeat, now + 4f);
                    L.QueueLine("prank_hat_back", 0.1f, 5f);
                }
                if (!inCar || L.asleepNow) { L.hatBackT = -1f; L.hatBackAt = -1f; return; }
                L.hatBackT += Time.deltaTime;
                float T = L.hatBackT;
                const float dur = 2.6f;
                var cam = Camera.main;
                if (cam != null)
                    L.WantReachTo("his head", cam.transform, false, false, Mathf.Clamp01(Mathf.Min((T - 0.1f) / 0.5f, (1.5f - T) / 0.5f)));
                // and the brim set straight once it lands
                L.WantBrim(false, Mathf.Clamp01(Mathf.Min((T - 1.2f) / 0.3f, (dur - 0.2f - T) / 0.4f)), 0f, 0.6f);
                if (idle) { L.curLook = Look.Player; L.curLookW = 0.8f; }
                if (!L.hatBackActed && T >= 0.45f && (L.ReachDone || T >= 0.9f))
                {
                    L.hatBackActed = true;
                    if (h != null) h.FlyBack();
                }
                if (T >= dur) { L.hatBackT = -1f; L.hatBackAt = -1f; }
            }
        }
    }
}
