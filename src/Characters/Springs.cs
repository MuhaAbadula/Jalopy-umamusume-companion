using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Real-time spring bones (skirt, hair, tail, ears, ribbons...), a port of the Blender
    /// blender-spring-physics bake: verlet particles pulled toward the chain's animated rest shape,
    /// body capsules, segment-vs-capsule tests, inelastic contacts, a bend cone per segment and
    /// skirt goals that partly follow the thighs. Lengths are in bind units; world = bind * root scale.
    /// </summary>
    public class Springs
    {
        class Col { public Transform Bone; public Vector3 A, B; public Vector3 WA, WB; }
        class Leg { public Transform Bone; public Vector3 Seg; public Quaternion RelRest; }

        class Chain
        {
            public Transform[] Bones;
            public Vector3[] Seg;          // segment to the next joint, in bone local space (bind units)
            public Quaternion[] RestLocal;
            public float Stiff, Drag, Grav, CosBend, Bend;
            public int[] Cols;
            public float[] ColR;
            public float[] LegW;
            public bool Kinematic;
            public bool Tail;              // gets the idle sway (TailAmp)
            public float WindResp = 1f;    // how much window wind moves it (chest 0, tail a little)
            public Vector3[] Pos, Prev, T, TPrev, Goal, GoalDir, Show;
            public Vector3[] Seat;         // task 31 bug 4: seated drape of a kinematic coat chain
            public float SeatL = 0.5f;     // share of the left leg (from the leg-follow weights)
            public float SeatDx, SeatShelf, SeatFront, SeatKnee;  // bug 5: seated lap offsets (bind units), 0 back .. 1 front
            public float[] Len;
            public bool Ready;
            // debug stats
            public string Name;
            public Vector3[] LastShow, LastT, LastPos;
            public float JitShow, JitT, JitPos;
            public int[] Hits;
        }

        public bool DebugLog;
        float dbgNext;
        int dbgFrames, dbgSteps, dbgResetFrame, dbgResetTele, dbgResetInit; float dbgRootMove;
        float dbgDtMin = 1f, dbgDtMax;

        readonly List<Chain> chains = new List<Chain>();
        readonly List<Col> cols = new List<Col>();
        readonly List<Leg> legs = new List<Leg>();
        readonly Transform root;
        readonly float particleR, h;
        readonly int iters;
        float lastSub = -1f;   // previous substep length, for verlet velocity rescaling
        Transform frame;          // particles are stored relative to this (the car while riding), null = world
        Matrix4x4 frameToWorld = Matrix4x4.identity;

        public bool Enabled = true;
        /// <summary>Asleep / lying still: no gravity, no colliders, stiff pull to the rest shape (no contact buzz).</summary>
        public bool Calm;

        /// <summary>v0.52.5 (Tachyon's long cuffs, no hand mesh inside): the first segment of a chain held on its rest shape
        /// (0 free .. 1 held), the rest of the chain still hangs. Keyed by the chain's first bone name.</summary>
        readonly Dictionary<string, float> pinFirst = new Dictionary<string, float>();
        public void PinFirst(string chain, float w) { pinFirst[chain] = Mathf.Clamp01(w); }

        /// <summary>A point on a chain's rest axis: its root plus <paramref name="along"/> (world units) along the first
        /// segment's rest direction (carried by the parent bone, so valid right after the arm IK of this frame).</summary>
        public bool RestAxisPoint(string chain, float along, out Vector3 p, out Vector3 dir)
        {
            p = dir = Vector3.zero;
            foreach (var ch in chains)
            {
                if (ch.Name != chain || ch.Bones[0].parent == null) continue;
                dir = ch.Bones[0].parent.rotation * ch.RestLocal[0] * ch.Seg[0];
                if (dir.sqrMagnitude < 1e-12f) return false;
                dir.Normalize();
                p = ch.Bones[0].position + dir * along;
                return true;
            }
            return false;
        }
        /// <summary>Task 31 bug 4: 0 standing .. 1 seated in the car (FukuDriver seatBlend). Kinematic coat chains then
        /// lie on the thighs up to the knees and hang along the shins (SeatDrape), never below her ankles: Sweep's long
        /// coat hung straight down through the cab floor. No collision, so nothing can shake.</summary>
        public float Seated;
        public bool TuckSeatedTail;
        Transform kneeL, kneeR, ankleL, ankleR, hipBone;
        Vector3 latRest;   // her rest side axis in the hip's local frame
        bool drapeOk;
        public int ChainCount { get { return chains.Count; } }

        /// <summary>Idle tail sway, set by Life every frame: side swing (degrees per joint), speed, slow lift.</summary>
        public float TailAmp, TailHz, TailLift;
        /// <summary>v0.26: walking tail. TailPhase >= 0 drives the sway from her step phase (radians) instead of the free
        /// clock; TailWave = lag per joint (radians, more = an S-wave down to the tip); TailStiffScale softens the tail.</summary>
        public float TailPhase = -1f, TailWave = 0.7f, TailStiffScale = 1f, TailBounce;
        /// <summary>Wind through an open window: world direction * strength (0..~1.4), set by Life every frame.</summary>
        public Vector3 Wind;
        /// <summary>v0.24.6: how far the body travelled this frame outside the car (world). Loose chains are carried
        /// along by CarryTail / CarryOther of it, so walking forward doesn't stream the tail back like a flag.</summary>
        public Vector3 RootDelta;
        public float CarryTail, CarryOther;
        // The goal pull is strong (hair hardly sags under gravity), so a force alone barely moves it: the wind bends
        // the rest shape towards the wind (up to WindBendMax degrees per segment) and a smaller force adds flutter.
        const float WindGain = 60f;
        const float WindBendMax = 45f;
        float tailPhase;

        public Springs(CharPack d, Transform root, Dictionary<string, Transform> bones)
        {
            this.root = root;
            particleR = d.ParticleRadius;
            h = 1f / Mathf.Max(30f, d.SubstepHz);
            iters = Mathf.Max(1, d.Iterations);
            var restRot = new Dictionary<string, Quaternion>();
            foreach (var b in d.Bones) restRot[b.Name] = FukuDriver.Rot(b.Rest);

            foreach (var c in d.Colliders)
            {
                Transform t;
                bones.TryGetValue(c.Bone, out t);
                cols.Add(new Col { Bone = t, A = c.A, B = c.B });
            }
            foreach (var l in d.Legs)
            {
                Transform t;
                if (!bones.TryGetValue(l.Bone, out t) || t.parent == null) { legs.Add(null); continue; }
                var parentRest = restRot.ContainsKey(t.parent.name) ? restRot[t.parent.name] : Quaternion.identity;
                legs.Add(new Leg { Bone = t, Seg = l.Seg, RelRest = Quaternion.Inverse(parentRest) * restRot[l.Bone] });
            }
            foreach (var c in d.Chains)
            {
                var ch = new Chain
                {
                    Stiff = c.Stiff, Drag = c.Drag, Grav = c.Grav, Bend = c.Bend, CosBend = Mathf.Cos(c.Bend * Mathf.Deg2Rad),
                    LegW = c.LegWeights, Kinematic = c.Kinematic,
                };
                int n = c.Bones.Count;
                ch.Bones = new Transform[n];
                ch.Seg = new Vector3[n];
                ch.RestLocal = new Quaternion[n];
                bool ok = true;
                for (int i = 0; i < n; i++)
                {
                    if (!bones.TryGetValue(c.Bones[i].Name, out ch.Bones[i])) { ok = false; break; }
                    ch.Seg[i] = c.Bones[i].Seg;
                    ch.RestLocal[i] = ch.Bones[i].localRotation;
                }
                if (!ok) continue;
                ch.Cols = new int[c.Colliders.Count];
                ch.ColR = new float[c.Colliders.Count];
                for (int i = 0; i < c.Colliders.Count; i++) { ch.Cols[i] = c.Colliders[i].Key; ch.ColR[i] = c.Colliders[i].Value; }
                ch.Pos = new Vector3[n + 1]; ch.Prev = new Vector3[n + 1]; ch.T = new Vector3[n + 1]; ch.TPrev = new Vector3[n + 1];
                ch.Show = new Vector3[n + 1];
                ch.Name = c.Bones[0].Name;
                ch.Tail = ch.Name.IndexOf("Tail", System.StringComparison.OrdinalIgnoreCase) >= 0;
                ch.WindResp = ch.Name.IndexOf("Bust", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 0f : ch.Tail ? 0.3f : 1f;
                ch.LastShow = new Vector3[n]; ch.LastT = new Vector3[n]; ch.LastPos = new Vector3[n];
                ch.Hits = new int[c.Colliders.Count];
                ch.Goal = new Vector3[n + 1]; ch.GoalDir = new Vector3[n + 1]; ch.Len = new float[n];
                chains.Add(ch);
            }
            LogChains(d);
            SetupDrape(d, bones);
        }

        void SetupDrape(CharPack d, Dictionary<string, Transform> bones)
        {
            var rest = new Dictionary<string, Matrix4x4>();
            foreach (var b in d.Bones) rest[b.Name] = b.Rest;
            // only a long coat (some kinematic chain ends below the knee at rest: Sweep's CSkirt) drapes; a short skirt
            // (Fukukitaru's MSkirt ends 0.25 above the knee) stays exactly as it was
            bool any = false;
            Matrix4x4 kr;
            float kneeY = rest.TryGetValue("Knee_L", out kr) ? kr.m13 : float.MinValue;
            foreach (var c in d.Chains)
            {
                Matrix4x4 lm;
                if (!c.Kinematic || c.Bones.Count == 0 || !rest.TryGetValue(c.Bones[c.Bones.Count - 1].Name, out lm)) continue;
                if (lm.MultiplyPoint3x4(c.Bones[c.Bones.Count - 1].Seg).y < kneeY) any = true;
            }
            if (!any || legs.Count < 2 || legs[0] == null || legs[1] == null) return;
            if (!bones.TryGetValue("Knee_L", out kneeL) || !bones.TryGetValue("Knee_R", out kneeR)
                || !bones.TryGetValue("Ankle_L", out ankleL) || !bones.TryGetValue("Ankle_R", out ankleR)) return;
            hipBone = legs[0].Bone.parent;
            Matrix4x4 h;
            if (hipBone == null || !rest.TryGetValue(hipBone.name, out h) || !rest.ContainsKey("Knee_L") || !rest.ContainsKey("Knee_R")) return;
            Quaternion hinv = Quaternion.Inverse(FukuDriver.Rot(h));
            latRest = hinv * Vector3.right;
            Vector3 kL = rest["Knee_L"].GetColumn(3), kR = rest["Knee_R"].GetColumn(3);
            var defs = new Dictionary<string, CharPack.Chain>();
            foreach (var c in d.Chains) if (c.Bones.Count > 0) defs[c.Bones[0].Name] = c;
            foreach (var ch in chains)
            {
                CharPack.Chain c;
                if (!ch.Kinematic || !defs.TryGetValue(ch.Name, out c)) continue;
                float l = ch.LegW != null && ch.LegW.Length > 0 ? ch.LegW[0] : 0f, r = ch.LegW != null && ch.LegW.Length > 1 ? ch.LegW[1] : 0f;
                ch.SeatL = l + r > 0.05f ? l / (l + r) : 0.5f;
                // thigh and knee radii from the chain's colliders (bind units)
                float rT = 0.12f, rK = 0.09f;
                foreach (var kv in c.Colliders)
                {
                    var col = kv.Key >= 0 && kv.Key < d.Colliders.Count ? d.Colliders[kv.Key] : null;
                    if (col == null) continue;
                    if (col.Bone == "Thigh_L") rT = kv.Value;
                    else if (col.Bone == "Knee_L") rK = kv.Value;
                }
                ch.SeatKnee = rK;
                // where the coat is at her knee height at rest: beside / in front of / behind the legs
                int n = c.Bones.Count;
                var p = new Vector3[n + 1];
                for (int i = 0; i < n; i++) p[i] = rest[c.Bones[i].Name].GetColumn(3);
                p[n] = rest[c.Bones[n - 1].Name].MultiplyPoint3x4(c.Bones[n - 1].Seg);
                Vector3 q = p[n];
                for (int i = 0; i < n; i++)
                    if (p[i].y >= kneeY && p[i + 1].y < kneeY) { q = Vector3.Lerp(p[i], p[i + 1], (p[i].y - kneeY) / (p[i].y - p[i + 1].y)); break; }
                Vector3 axis = Vector3.Lerp(kR, kL, ch.SeatL);
                Vector3 off = hinv * (q - axis);
                // seated, gravity pulls the A-line flare in toward the legs
                ch.SeatDx = 0.6f * off.x;
                float shelf = float.MinValue;
                foreach (Vector3 kk in new[] { kL, kR })
                {
                    float dd = Mathf.Abs(ch.SeatDx - (hinv * (kk - axis)).x);
                    // on top of the thigh (circle); past its edge the cloth slides down beside it to the seat
                    float f = dd <= rT ? Mathf.Sqrt(rT * rT - dd * dd) : -rT * Mathf.SmoothStep(0f, 1f, (dd - rT) / rT);
                    shelf = Mathf.Max(shelf, f);
                }
                ch.SeatShelf = shelf;
                // panels behind the legs (the back of the coat) are sat on: they keep the hip-carried shape
                ch.SeatFront = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.15f, 0f, off.z));
                ch.Seat = new Vector3[n + 1];
            }
            drapeOk = true;
            var sb = new System.Text.StringBuilder("[UncleSwap] springs: long coat, seated it lies on the thighs and hangs over the knees:");
            foreach (var ch in chains)
                if (ch.Seat != null)
                    sb.Append(' ').Append(ch.Name.Replace("_00", "")).Append(ch.SeatFront > 0.5f ? (ch.SeatShelf > 0f ? " lap" : " side") : " back");
            Debug.Log(sb.ToString());
        }

        /// <summary>Task 31 bugs 4-5: the seated coat as a curtain over her legs. From the chain root it drops onto the
        /// thigh top (or beside the thigh, down to the seat), runs along the thigh to the knee, over the knee and down along
        /// the shin (never back under the seat). Chain points sit at their rest arc lengths along that path, so bone lengths
        /// hold; panels behind her keep the hip-carried shape; points below her ankles are lifted to them. No collision.
        /// v0.38.4 turned the rest (flared) shape with the thigh instead: the flaps stood up from the lap like a board.</summary>
        void SeatDrape(Chain ch, int n, float k, Vector3 up)
        {
            float wl = ch.SeatL;
            Transform tL = legs[0].Bone, tR = legs[1].Bone;
            Vector3 hp = Vector3.Lerp(tR.position, tL.position, wl);
            Vector3 kp = Vector3.Lerp(kneeR.position, kneeL.position, wl);
            Vector3 ap = Vector3.Lerp(ankleR.position, ankleL.position, wl);
            Vector3 a = kp - hp;
            float L = a.magnitude;
            a = L > 1e-5f ? a / L : -up;
            Vector3 u = up - a * Vector3.Dot(up, a);
            u = u.sqrMagnitude > 1e-6f ? u.normalized : hipBone.rotation * Vector3.forward;
            Vector3 lat = hipBone.rotation * latRest;
            lat -= a * Vector3.Dot(lat, a) + u * Vector3.Dot(lat, u);
            lat = lat.sqrMagnitude > 1e-6f ? lat.normalized : Vector3.Cross(u, a);
            Vector3 side = lat * (ch.SeatDx * k);
            float shelf = ch.SeatShelf * k;
            Vector3 v0 = ch.T[0];
            float tE = Mathf.Clamp(Vector3.Dot(v0 - hp, a), 0f, L);
            Vector3 v1 = hp + a * tE + side + u * shelf;
            Vector3 v2 = kp + side + u * shelf;
            Vector3 v3 = kp + a * (ch.SeatKnee * k) + side + u * Mathf.Min(shelf, 0f);
            // down from the knee along the shin; a shin tucked back under the seat does not pull the coat with it
            Vector3 s = ap - kp;
            s = s.sqrMagnitude > 1e-6f ? s.normalized : -up;
            Vector3 aH = a - up * Vector3.Dot(a, up);
            if (aH.sqrMagnitude > 1e-6f) { aH.Normalize(); s -= aH * Mathf.Min(0f, Vector3.Dot(s, aH)); }
            if (s.sqrMagnitude < 1e-6f) s = -up;
            s.Normalize();
            float l01 = (v1 - v0).magnitude, l12 = (v2 - v1).magnitude, l23 = (v3 - v2).magnitude;
            float acc = 0f, w = ch.SeatFront;
            ch.Seat[0] = v0;
            for (int i = 1; i <= n; i++)
            {
                acc += (ch.T[i] - ch.T[i - 1]).magnitude;
                Vector3 pt;
                float t = acc;
                if (t <= l01) pt = l01 > 1e-6f ? Vector3.Lerp(v0, v1, t / l01) : v1;
                else if ((t -= l01) <= l12) pt = l12 > 1e-6f ? Vector3.Lerp(v1, v2, t / l12) : v2;
                else if ((t -= l12) <= l23) pt = l23 > 1e-6f ? Vector3.Lerp(v2, v3, t / l23) : v3;
                else pt = v3 + s * (t - l23);
                ch.Seat[i] = Vector3.Lerp(ch.T[i], pt, w);
            }
            float floor = Mathf.Min(Vector3.Dot(ankleL.position, up), Vector3.Dot(ankleR.position, up)) - 0.12f * k;
            for (int i = 1; i <= n; i++)
            {
                float hgt = Vector3.Dot(ch.Seat[i], up);
                if (hgt < floor) ch.Seat[i] += up * (floor - hgt);
            }
        }

        /// <summary>Task 31: one line per simulated chain at build (bones, stiffness, collider radii) and a warning for a
        /// chain whose anchor joint lies inside one of its colliders at rest: such a chain buzzes against it forever
        /// (Sweep's side locks in the head sphere; fixed by export_char --root_clear 1).</summary>
        void LogChains(CharPack d)
        {
            var rest = new Dictionary<string, Matrix4x4>();
            foreach (var b in d.Bones) rest[b.Name] = b.Rest;
            var sb = new System.Text.StringBuilder("[UncleSwap] springs: " + chains.Count + " chains");
            int warn = 0;
            foreach (var c in d.Chains)
            {
                if (c.Kinematic || c.Bones.Count == 0) continue;
                sb.Append("\n  ").Append(c.Bones[0].Name).Append(" x").Append(c.Bones.Count).Append(" stiff ").Append(c.Stiff.ToString("F2")).Append(" cols");
                Matrix4x4 m;
                Vector3 anchor = rest.TryGetValue(c.Bones[0].Name, out m) ? (Vector3)m.GetColumn(3) : Vector3.zero;
                foreach (var kv in c.Colliders)
                {
                    var col = kv.Key >= 0 && kv.Key < d.Colliders.Count ? d.Colliders[kv.Key] : null;
                    if (col == null) continue;
                    sb.Append(' ').Append(col.Name).Append(':').Append(kv.Value.ToString("F3"));
                    Matrix4x4 cm;
                    if (!rest.TryGetValue(col.Bone, out cm)) continue;
                    Vector3 a = cm.MultiplyPoint3x4(col.A), bb = cm.MultiplyPoint3x4(col.B);
                    float dist = (anchor - ClosestOnSeg(anchor, a, bb)).magnitude, rr = kv.Value + particleR;
                    if (dist < rr) { sb.Append("(anchor inside, " + dist.ToString("F3") + ")"); warn++; }
                }
            }
            if (warn > 0) sb.Append("\n  ").Append(warn).Append(" anchor(s) inside a collider: those chains may jitter in place");
            Debug.Log(sb.ToString());
        }

        /// <summary>Called once per frame after the body is posed. `frameRef` = the car while riding, else null.</summary>
        public void Step(float dt, Transform frameRef)
        {
            float k = root.lossyScale.x; // bind -> world
            bool frameChanged = frameRef != frame;
            Matrix4x4 newToWorld = frameRef != null ? frameRef.localToWorldMatrix : Matrix4x4.identity;

            // rest pose for the chains, collect targets
            foreach (var ch in chains)
                for (int i = 0; i < ch.Bones.Length; i++) ch.Bones[i].localRotation = ch.RestLocal[i];
            TailSway(dt);
            WindBend();
            foreach (var c in cols)
            {
                if (c.Bone == null) continue;
                c.WA = c.Bone.TransformPoint(c.A);
                c.WB = c.Bone.TransformPoint(c.B);
            }
            Quaternion[] legSwing = new Quaternion[legs.Count];
            Vector3[] legHead = new Vector3[legs.Count];
            for (int i = 0; i < legs.Count; i++)
            {
                var l = legs[i];
                if (l == null) { legSwing[i] = Quaternion.identity; continue; }
                Vector3 cur = l.Bone.TransformVector(l.Seg);
                Vector3 restFollow = l.Bone.parent.rotation * l.RelRest * l.Seg;
                legSwing[i] = Quaternion.FromToRotation(restFollow, cur);
                legHead[i] = l.Bone.position;
            }


            // variable substeps no longer than the tuned one (h); per-substep constants rescaled to real time,
            // so the result does not depend on the frame rate (fixed steps + empty frames made the skirt shake)
            float frameDt = Mathf.Min(dt, 0.1f);
            int steps = frameDt > 0f ? Mathf.Clamp(Mathf.CeilToInt(frameDt / h - 1e-4f), 1, 12) : 0;
            float sub = steps > 0 ? frameDt / steps : 0f;
            float ratio = sub / h;
            float velScale = lastSub > 0f && sub > 0f ? sub / lastSub : 1f;
            Vector3 g = Vector3.down * k;

            foreach (var ch in chains)
            {
                int n = ch.Bones.Length;
                for (int i = 0; i < n; i++) ch.T[i] = ch.Bones[i].position;
                ch.T[n] = ch.Bones[n - 1].TransformPoint(ch.Seg[n - 1]);
                bool drape = ch.Seat != null && drapeOk && Seated > 0f;
                if (drape) SeatDrape(ch, n, k, frameRef != null ? frameRef.up : Vector3.up);
                for (int li = 0; li < legs.Count && ch.LegW != null && li < ch.LegW.Length; li++)
                {
                    float w = ch.LegW[li];
                    if (w <= 0f || legs[li] == null) continue;
                    Quaternion q = Quaternion.Slerp(Quaternion.identity, legSwing[li], w);
                    Vector3 hp = legHead[li];
                    Vector3 shift = hp + q * (ch.T[0] - hp) - ch.T[0];
                    for (int i = 1; i <= n; i++) ch.T[i] = hp + q * (ch.T[i] - hp) - shift;
                }
                if (drape)
                {
                    float sw = Mathf.SmoothStep(0f, 1f, Seated);
                    for (int i = 1; i <= n; i++) ch.T[i] = Vector3.Lerp(ch.T[i], ch.Seat[i], sw);
                }
                for (int i = 0; i < n; i++) ch.Len[i] = (ch.T[i + 1] - ch.T[i]).magnitude;

                // kinematic chains (and everything when physics is off): rest shape + leg follow, no simulation
                if (ch.Kinematic || !Enabled)
                {
                    for (int i = 0; i <= n; i++) { ch.Pos[i] = ch.T[i]; ch.Show[i] = ch.T[i]; }
                    ch.Ready = false;
                    FollowShow(ch, n, frameRef);
                    continue;
                }

                // particles live in the reference frame: bring them to world
                bool teleport = ch.Ready && !frameChanged && (newToWorld.MultiplyPoint3x4(ch.Pos[0]) - ch.T[0]).magnitude > 0.5f * k;
                if (DebugLog && ch.Ready)
                    dbgRootMove = Mathf.Max(dbgRootMove, (newToWorld.MultiplyPoint3x4(ch.Pos[0]) - ch.T[0]).magnitude / k);
                if (!ch.Ready) dbgResetInit++; else if (frameChanged) dbgResetFrame++; else if (teleport) dbgResetTele++;
                if (!ch.Ready || frameChanged || teleport)
                {
                    for (int i = 0; i <= n; i++) { ch.Pos[i] = ch.T[i]; ch.Prev[i] = ch.T[i]; ch.TPrev[i] = ch.T[i]; ch.Show[i] = ch.T[i]; }
                    ch.Ready = true;
                }
                else
                {
                    for (int i = 0; i <= n; i++)
                    {
                        ch.Pos[i] = newToWorld.MultiplyPoint3x4(ch.Pos[i]);
                        ch.Prev[i] = newToWorld.MultiplyPoint3x4(ch.Prev[i]);
                        ch.TPrev[i] = newToWorld.MultiplyPoint3x4(ch.TPrev[i]);
                        ch.Show[i] = newToWorld.MultiplyPoint3x4(ch.Show[i]);
                    }
                    // carried along by the chain's own anchor (where it hangs from), not the uncle's root: the body
                    // follows the root with its own smoothing, the mismatch shook the tail every frame (user)
                    float carry = ch.Tail ? CarryTail : CarryOther;
                    if (carry > 0f)
                    {
                        Vector3 d = (ch.T[0] - ch.TPrev[0]) * carry;
                        if (d.sqrMagnitude < 0.25f * k * k)
                            for (int i = 1; i <= n; i++) { ch.Pos[i] += d; ch.Prev[i] += d; ch.Show[i] += d; }
                    }
                }

                float baseStiff = ch.Tail ? ch.Stiff * TailStiffScale : ch.Stiff;
                // v0.52.6: a chain with no stiffness is loose cloth (Tachyon's cuff overhang): it hangs on gravity, asleep too
                bool calm = Calm && ch.Stiff > 0f;
                float stiff = 1f - Mathf.Pow(1f - (calm ? Mathf.Max(ch.Stiff, 0.5f) : baseStiff), ratio);
                float grav = calm ? 0f : ch.Grav;
                float keep = Mathf.Pow(1f - ch.Drag, ratio);
                float pinW;
                if (pinFirst.TryGetValue(ch.Name, out pinW) && pinW > 0f) stiff *= 1f - pinW;   // held cuff: the overhang hangs on gravity alone
                for (int s = 0; s < steps; s++)
                {
                    float t = (s + 1f) / steps;
                    float vs = s == 0 ? velScale : 1f;
                    ch.Pos[0] = Vector3.Lerp(ch.TPrev[0], ch.T[0], t);
                    for (int i = 1; i <= n; i++)
                    {
                        Vector3 Ti = Vector3.Lerp(ch.TPrev[i], ch.T[i], t);
                        Vector3 Tp = Vector3.Lerp(ch.TPrev[i - 1], ch.T[i - 1], t);
                        Vector3 goal;
                        if (i == 1) goal = Ti;
                        else
                        {
                            Vector3 Tpp = Vector3.Lerp(ch.TPrev[i - 2], ch.T[i - 2], t);
                            Quaternion rq = Quaternion.FromToRotation(Tp - Tpp, ch.Pos[i - 1] - ch.Pos[i - 2]);
                            goal = ch.Pos[i - 1] + rq * (Ti - Tp);
                        }
                        // the rest shape may lie inside a limb (skirt vs sitting thighs): take the goal out first,
                        // otherwise stiffness and the bend cone keep pulling the cloth into the leg
                        for (int c = 0; !Calm && c < ch.Cols.Length; c++)
                        {
                            var col = cols[ch.Cols[c]];
                            if (col.Bone != null) PushOut(col, (ch.ColR[c] + particleR) * k, ch.Pos[i], ch.Pos[i - 1], ref goal);
                        }
                        ch.GoalDir[i] = (goal - ch.Pos[i - 1]).normalized;
                        Vector3 cur = ch.Pos[i];
                        Vector3 nxt = cur + (cur - ch.Prev[i]) * (keep * vs) + (goal - cur) * stiff + g * (grav * sub * sub)
                            + Wind * (WindGain * ch.WindResp * k * sub * sub);
                        ch.Prev[i] = cur;
                        ch.Pos[i] = nxt;
                    }
                    Solve(ch, k, true, !Calm);
                    float pin;
                    if (n >= 1 && pinFirst.TryGetValue(ch.Name, out pin) && pin > 0f)
                    {
                        Vector3 T1 = Vector3.Lerp(ch.TPrev[1], ch.T[1], t);
                        Vector3 d1 = Vector3.Lerp(ch.Pos[1], T1, pin) - ch.Pos[1];
                        ch.Pos[1] += d1; ch.Prev[1] += d1;   // carried, no kick
                        Solve(ch, k, false, !Calm);          // the hanging part follows the held segment
                    }
                }
                // steps == 0 (game paused): particles stay as they are
                // displayed shape: light low-pass on top of the sim (kills residual contact buzz)
                float a = frameDt > 0f ? 1f - Mathf.Exp(-frameDt / 0.025f) : 0f;
                ch.Show[0] = ch.T[0];
                for (int i = 1; i <= n; i++) ch.Show[i] = Vector3.Lerp(ch.Show[i], ch.Pos[i], a);

                FollowShow(ch, n, frameRef);

                if (DebugLog) Measure(ch, n);

                // back into the reference frame
                Matrix4x4 toFrame = newToWorld.inverse;
                for (int i = 0; i <= n; i++)
                {
                    ch.TPrev[i] = toFrame.MultiplyPoint3x4(ch.T[i]);
                    ch.Pos[i] = toFrame.MultiplyPoint3x4(ch.Pos[i]);
                    ch.Prev[i] = toFrame.MultiplyPoint3x4(ch.Prev[i]);
                    ch.Show[i] = toFrame.MultiplyPoint3x4(ch.Show[i]);
                }
            }
            frame = frameRef;
            frameToWorld = newToWorld;
            if (sub > 0f) lastSub = sub;
            if (DebugLog) Report(steps, frameDt);
        }

        /// <summary>
        /// Sway the tail's rest shape: a wave running down the chain, side to side across the body plus a slow lift.
        /// The simulation chases it, so the tail swings with a natural lag.
        /// </summary>
        void TailSway(float dt)
        {
            tailPhase += Mathf.Min(dt, 0.1f) * TailHz * Mathf.PI * 2f;
            if (tailPhase > 1000f) tailPhase -= Mathf.PI * 200f;
            if (TailAmp < 0.01f && TailLift < 0.01f && TailBounce < 0.01f) return;
            float ph = TailPhase >= 0f ? TailPhase : tailPhase;
            Vector3 right = root.rotation * Vector3.right; // bind space: +X = character right
            // task 31 bug 3: a big wag (amp above 9: talking 12, happy wag 18) swung the lower tail sideways through the
            // backs of the thighs and it bounced off them every frame (both packs). The tail now also lifts back by half
            // its side swing (smooth |x|), so the arc passes behind the legs. Idle sway (9) and walking are unchanged.
            float back = TailPhase < 0f ? 0.5f * Mathf.Clamp01((TailAmp - 9f) / 6f) : 0f;
            foreach (var ch in chains)
            {
                if (!ch.Tail) continue;
                int n = ch.Bones.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector3 seg = ch.Bones[i].TransformVector(ch.Seg[i]);
                    Vector3 side = Vector3.Cross(seg, right);
                    if (side.sqrMagnitude < 1e-12f) continue;
                    float along = n > 1 ? (float)i / (n - 1) : 0f;
                    // the root barely moves, the tip swings most and later (a wave down the tail)
                    float grow = TailPhase >= 0f ? 0.25f + 1.1f * along : 0.6f + 0.6f * along;
                    float swing = TailAmp * grow * Mathf.Sin(ph - i * TailWave);
                    float lift = TailLift * Mathf.Sin(tailPhase * 0.37f + 1f - i * 0.3f)
                        + TailBounce * along * Mathf.Sin(2f * ph - 0.6f - i * TailWave);   // a little bounce per step
                    if (back > 0f) lift += back * (Mathf.Sqrt(swing * swing + 4f) - 2f);
                    ch.Bones[i].rotation = Quaternion.AngleAxis(swing, side.normalized) * Quaternion.AngleAxis(lift, right) * ch.Bones[i].rotation;
                }
            }
        }

        /// <summary>Bend the rest shape of loose chains towards the wind; the simulation follows with its own lag.</summary>
        void WindBend()
        {
            float strength = Wind.magnitude;
            if (strength < 0.01f) return;
            Vector3 dir = Wind / strength;
            float t = Time.time;
            for (int c = 0; c < chains.Count; c++)
            {
                var ch = chains[c];
                if (ch.Kinematic || ch.WindResp <= 0f) continue;
                int n = ch.Bones.Length;
                for (int i = 0; i < n; i++)
                {
                    Vector3 seg = ch.Bones[i].TransformVector(ch.Seg[i]);
                    Vector3 axis = Vector3.Cross(seg, dir);
                    if (axis.sqrMagnitude < 1e-12f) continue;
                    float along = n > 1 ? (float)i / (n - 1) : 0f;
                    float flutter = 0.75f + 0.25f * Mathf.Sin(t * 11f + c * 1.3f + i * 0.9f);
                    float a = Mathf.Min(WindBendMax * strength * ch.WindResp * (0.7f + 0.3f * along) * flutter, Vector3.Angle(seg, dir));
                    ch.Bones[i].rotation = Quaternion.AngleAxis(a, axis.normalized) * ch.Bones[i].rotation;
                }
            }
        }

        static float Ang(Vector3 a, Vector3 b) { return a.sqrMagnitude > 1e-12f && b.sqrMagnitude > 1e-12f ? Vector3.Angle(a, b) : 0f; }

        void Measure(Chain ch, int n)
        {
            for (int i = 0; i < n; i++)
            {
                Vector3 ds = root.InverseTransformDirection(ch.Show[i + 1] - ch.Show[i]);
                Vector3 dt = root.InverseTransformDirection(ch.T[i + 1] - ch.T[i]);
                Vector3 dp = root.InverseTransformDirection(ch.Pos[i + 1] - ch.Pos[i]);
                ch.JitShow = Mathf.Max(ch.JitShow, Ang(ds, ch.LastShow[i]));
                ch.JitT = Mathf.Max(ch.JitT, Ang(dt, ch.LastT[i]));
                ch.JitPos = Mathf.Max(ch.JitPos, Ang(dp, ch.LastPos[i]));
                ch.LastShow[i] = ds; ch.LastT[i] = dt; ch.LastPos[i] = dp;
            }
        }

        void Report(int steps, float dt)
        {
            dbgFrames++; dbgSteps += steps;
            dbgDtMin = Mathf.Min(dbgDtMin, dt); dbgDtMax = Mathf.Max(dbgDtMax, dt);
            if (Time.realtimeSinceStartup < dbgNext) return;
            dbgNext = Time.realtimeSinceStartup + 2f;
            var sorted = new List<Chain>(chains);
            sorted.Sort((a, b) => b.JitShow.CompareTo(a.JitShow));
            var sb = new System.Text.StringBuilder();
            // hits below are per second (the window is 2 s)
            sb.AppendFormat("[UncleSwap] physics: frames {0} steps/frame {1:F2} dt {2:F4}..{3:F4} frame={4}\n",
                dbgFrames, dbgFrames > 0 ? (float)dbgSteps / dbgFrames : 0f, dbgDtMin, dbgDtMax, frame != null ? frame.name : "world");
            sb.AppendFormat("  resets: init {0} frameChange {1} teleport {2}; max root move between frames {3:F3} bind units\n", dbgResetInit, dbgResetFrame, dbgResetTele, dbgRootMove);
            for (int i = 0; i < Mathf.Min(8, sorted.Count); i++)
            {
                var c = sorted[i];
                sb.AppendFormat("  {0,-24} x{4} maxStep show {1,6:F1} pos {2,6:F1} target {3,6:F1} deg  hits/s", c.Name, c.JitShow, c.JitPos, c.JitT, c.Bones.Length);
                for (int k = 0; k < c.Hits.Length; k++) sb.AppendFormat(" {0}:{1}", cols[c.Cols[k]].Bone != null ? cols[c.Cols[k]].Bone.name : "?", c.Hits[k] / 2);
                sb.Append("\n");
            }
            Debug.Log(sb.ToString());
            foreach (var c in chains) { c.JitShow = c.JitT = c.JitPos = 0f; for (int k = 0; k < c.Hits.Length; k++) c.Hits[k] = 0; }
            dbgFrames = dbgSteps = dbgResetInit = dbgResetFrame = dbgResetTele = 0; dbgRootMove = 0f; dbgDtMin = 1f; dbgDtMax = 0f;
        }

        /// <summary>Direction from the limb axis to point p, without the along-axis part.</summary>
        static Vector3 SideOf(Col col, Vector3 p)
        {
            Vector3 side = p - ClosestOnSeg(p, col.WA, col.WB);
            Vector3 ax = col.WB - col.WA;
            if (ax.sqrMagnitude > 1e-12f) side -= Vector3.Project(side, ax);
            return side;
        }

        /// <summary>
        /// Which side of the limb a point belongs to: the side where it was a moment ago (`was`), so it can slide
        /// around the limb but never jump through it. If `was` itself is deep inside (a reset straight into a
        /// sitting pose), fall back to the parent point's side.
        /// </summary>
        static Vector3 KeepSide(Col col, float rr, Vector3 was, Vector3 bas)
        {
            Vector3 side = SideOf(col, was);
            if (side.sqrMagnitude < rr * rr * 0.25f) side = SideOf(col, bas);
            return side;
        }

        /// <summary>
        /// Push p out of a capsule, onto the side it came from. Near the axis the plain normal flips every step
        /// (a sitting thigh under a hanging skirt); this does not.
        /// </summary>
        static bool PushOut(Col col, float rr, Vector3 was, Vector3 bas, ref Vector3 p)
        {
            Vector3 cp = ClosestOnSeg(p, col.WA, col.WB);
            Vector3 v = p - cp;
            if (v.sqrMagnitude >= rr * rr) return false;
            Vector3 side = KeepSide(col, rr, was, bas);
            Vector3 nrm;
            if (side.sqrMagnitude > 1e-12f && (v.sqrMagnitude < 1e-12f || Vector3.Dot(v, side) < 0f))
                nrm = side.normalized;
            else
                nrm = v.sqrMagnitude > 1e-12f ? v.normalized : (p - bas).normalized;
            p = cp + nrm * rr;
            return true;
        }

        /// <summary>Rotate the chain bones so their segments point along the displayed particles.</summary>
        void FollowShow(Chain ch, int n, Transform car)
        {
            // Lay the tail toward the driver, then curl the tip forward along the cushion.
            // Only the displayed directions change. The outdoor spring simulation keeps running.
            float tuck = ch.Tail && TuckSeatedTail ? Mathf.SmoothStep(0f, 1f, Seated) : 0f;
            Vector3 up = Vector3.zero, inward = Vector3.zero, forward = Vector3.zero;
            if (tuck > 0f)
            {
                up = car != null ? car.up : Vector3.up;
                inward = Vector3.ProjectOnPlane(-root.right, up).normalized;
                forward = Vector3.Cross(up, inward);
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 cur = ch.Bones[i].TransformVector(ch.Seg[i]);
                Vector3 want = ch.Show[i + 1] - ch.Show[i];
                if (tuck > 0f)
                {
                    float along = (i + 0.5f) / n;
                    float angle = Mathf.Lerp(-45f, 135f, along) * Mathf.Deg2Rad;
                    Vector3 seatedDir = inward * Mathf.Cos(angle) + forward * Mathf.Sin(angle)
                        - up * Mathf.Lerp(0.7f, 0.05f, along);
                    want = Vector3.Slerp(want.normalized, seatedDir.normalized, tuck);
                }
                if (want.sqrMagnitude > 1e-12f && cur.sqrMagnitude > 1e-12f)
                    ch.Bones[i].rotation = Quaternion.FromToRotation(cur, want) * ch.Bones[i].rotation;
            }
        }

        void Solve(Chain ch, float k, bool carryVelocity, bool collide = true)
        {
            int n = ch.Bones.Length;
            for (int it = 0; it < iters; it++)
            {
                for (int i = 1; i <= n; i++)
                {
                    Vector3 bas = ch.Pos[i - 1];
                    Vector3 d = ch.Pos[i] - bas;
                    Vector3 gd = ch.GoalDir[i];
                    Vector3 dn = d.sqrMagnitude > 1e-12f ? d.normalized : gd;
                    if (Vector3.Dot(dn, gd) < ch.CosBend)
                    {
                        Vector3 axis = Vector3.Cross(gd, dn);
                        if (axis.sqrMagnitude > 1e-12f) dn = Quaternion.AngleAxis(ch.Bend, axis.normalized) * gd;
                    }
                    float len = ch.Len[i - 1];
                    Vector3 nw = bas + dn * len;
                    for (int c = 0; collide && c < ch.Cols.Length; c++)
                    {
                        var col = cols[ch.Cols[c]];
                        if (col.Bone == null) continue;
                        float rr = (ch.ColR[c] + particleR) * k;
                        if (PushOut(col, rr, ch.Pos[i], bas, ref nw)) ch.Hits[c]++;
                        float s; Vector3 ps, pc;
                        SegSeg(bas, nw, col.WA, col.WB, out s, out ps, out pc);
                        Vector3 v = ps - pc;
                        float vl = v.magnitude;
                        if (s > 0.05f && s < 0.95f && vl < rr)
                        {
                            ch.Hits[c]++;
                            Vector3 side = KeepSide(col, rr, ch.Pos[i], bas);
                            // segment crossed the limb: go back to the side it came from instead of through it
                            if (vl < 1e-6f || (side.sqrMagnitude > 1e-12f && Vector3.Dot(v, side) < 0f))
                                nw += side.normalized * (rr + vl) / Mathf.Max(s, 0.5f);
                            else
                                nw += v / vl * (rr - vl) / Mathf.Max(s, 0.5f);
                        }
                        nw = bas + (nw - bas).normalized * len;
                    }
                    if (carryVelocity) ch.Prev[i] += nw - ch.Pos[i]; // inelastic: corrections carry no velocity
                    ch.Pos[i] = nw;
                }
            }
        }

        static Vector3 ClosestOnSeg(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float L = ab.sqrMagnitude;
            float t = L < 1e-12f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / L);
            return a + ab * t;
        }

        static void SegSeg(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s, out Vector3 c1, out Vector3 c2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float t;
            if (a < 1e-12f && e < 1e-12f) { s = 0f; c1 = p1; c2 = p2; return; }
            if (a < 1e-12f) { s = 0f; t = Mathf.Clamp01(f / e); }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e < 1e-12f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float den = a * e - b * b;
                    s = den > 1e-12f ? Mathf.Clamp01((b * f - c * e) / den) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                    else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
                }
            }
            c1 = p1 + d1 * s;
            c2 = p2 + d2 * t;
        }
    }
}
