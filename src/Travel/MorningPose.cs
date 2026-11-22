using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 07: the motel wake-up pose. She stands by the player's bed (Morning.cs placed her), bends over towards his
    /// face, waves the hand away from the bed at head height (palm to him, 3 swings; task 11), the other arm hangs,
    /// ears forward, smiling; the driver says the alarm line meanwhile.
    /// Started and ended by the driver (MornPoseBegin / MornPoseEnd); blends out to her standing idle on its own.
    /// </summary>
    public partial class Life
    {
        const float MornLeanIn = 1.0f, MornOutLen = 0.7f, MornLeanDeg = 11f;
        // task 15: the lean is a hip hinge (all at Waist, the upper body stays straight), the chest a little up,
        // the shoulders a little back; the neck takes only a small part of the look-down pitch, the head the rest
        const float MornChestUpDeg = 3f, MornShoulderBackDeg = 4f, MornNeckPitchK = 0.15f;
        // task 11 wave: from +1.0 s (the lids start to open) raise in 0.4 s, 3 swings at 2 Hz, hold 0.3 s, down in 0.5 s
        const float MornWaveStart = 1.0f, MornWaveIn = 0.4f, MornWaveHz = 2.0f, MornWaveSwings = 3f, MornWaveHold = 0.3f, MornWaveOut = 0.5f;

        /// <summary>Which hand waves: +1 the one on her right, -1 on her left (the driver picks the side away from the bed).</summary>
        public int MornWaveSide = 1;

        Transform mornWaist;                 // linked bone (the pose copy rewrites it every frame): no drift
        Transform mornSpine;                 // only read for the log
        float mornT = -1f, mornOutT = -1f, mornLeanW, mornWaveW, mornArmW;
        bool mornWaveLogged, mornLeanLogged;
        EarMove mornEarL, mornEarR;

        /// <summary>Task 13: the morning in the room is on (set by the driver): she keeps her head and eyes on him.</summary>
        public bool MornStay;
        float mornStayW;

        /// <summary>The wake-up pose is on (also while it blends out).</summary>
        public bool MornPoseOn { get { return mornT >= 0f; } }

        void MornInit(Dictionary<string, Transform> bones)
        {
            bones.TryGetValue("Waist", out mornWaist);
            bones.TryGetValue("Spine", out mornSpine);
        }

        public bool MornPoseBegin()
        {
            if (!Enabled || head == null || armL == null || armR == null) return false;
            float now = Time.time;
            mornT = 0f;
            mornOutT = -1f;
            mornWaveLogged = mornLeanLogged = false;
            beat = null;
            if (GestureWeight > 0f && StopGesture != null) StopGesture();
            mornEarL = new EarMove { Name = "fwd_l", Start = now + 0.3f, In = 0.25f, Hold = 30f, Out = 0.6f, W = 1f };
            mornEarR = new EarMove { Name = "fwd_r", Start = now + 0.33f, In = 0.25f, Hold = 30f, Out = 0.6f, W = 1f };
            earMoves.Add(mornEarL);
            earMoves.Add(mornEarR);
            Debug.Log("[UncleSwap] Morning: pose lean");
            return true;
        }

        public void MornPoseEnd(string why)
        {
            if (mornT < 0f || mornOutT >= 0f) return;
            mornOutT = 0f;
            float now = Time.time;
            foreach (var m in new[] { mornEarL, mornEarR })
                if (m != null) m.Hold = Mathf.Max(0f, Mathf.Min(m.Hold, now - m.Start - m.In));
            Debug.Log("[UncleSwap] Morning: pose ending (" + why + ", " + mornT.ToString("0.0") + " s)");
        }

        /// <summary>From Update, after DozeApply: timeline, smile.</summary>
        void MornFace(float dt)
        {
            // task 13: standing in the room after the wave: head and eyes on him while he is in the room
            bool stay = MornStay && mornT < 0f && head != null && GestureWeight <= 0.01f;   // the driver clears MornStay when he is far
            mornStayW = Mathf.MoveTowards(mornStayW, stay ? 1f : 0f, dt / 0.8f);
            if (mornStayW > 0.01f) { lookAtPlayerOutside = true; nextOutsideGlance = Time.time + 1f; }
            if (mornT < 0f) { mornLeanW = mornWaveW = mornArmW = 0f; return; }
            mornT += dt;
            float outK = 1f;
            if (mornOutT >= 0f)
            {
                mornOutT += dt;
                outK = 1f - Mathf.SmoothStep(0f, 1f, mornOutT / MornOutLen);
                if (mornOutT >= MornOutLen)
                {
                    mornT = mornOutT = -1f;
                    mornLeanW = mornWaveW = mornArmW = 0f;
                    mornEarL = mornEarR = null;
                    Debug.Log("[UncleSwap] Morning: pose done");
                    return;
                }
            }
            mornLeanW = Mathf.SmoothStep(0f, 1f, mornT / MornLeanIn) * outK;
            float tw = mornT - MornWaveStart, waveEnd = MornWaveIn + MornWaveSwings / MornWaveHz + MornWaveHold;
            mornWaveW = Mathf.SmoothStep(0f, 1f, tw / MornWaveIn) * (1f - Mathf.SmoothStep(0f, 1f, (tw - waveEnd) / MornWaveOut)) * outK;
            mornArmW = 0.85f * mornLeanW;   // the other arm hangs relaxed while she leans
            Face.Want("happy", 0.9f * mornLeanW, 0.2f);
            lookAtPlayerOutside = true;          // the eyes on him (PoseBones, outside branch)
            nextOutsideGlance = Time.time + 1f;
        }

        /// <summary>From PoseBones, last before the eyes: lean towards his face, head at him, the wave.</summary>
        void MornPose()
        {
            if (mornStayW > 0.001f && mornLeanW <= 0.001f) MornStayHead();
            if (mornLeanW <= 0.001f && mornWaveW <= 0.001f && mornArmW <= 0.001f) return;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            Vector3 fwd, up;
            Facing(out fwd, out up);
            Vector3 toCam = Vector3.ProjectOnPlane(eye - head.T.position, up);
            if (toCam.sqrMagnitude > 1e-4f) fwd = toCam.normalized;
            Vector3 right = Vector3.Cross(up, fwd);   // Unity: a positive turn about "right" tips the top forward

            // task 15: bend over from the hips with a straight back. The Waist pivot sits at the pelvis, so turning
            // only Waist tips the whole upper body as one piece; v0.33 put 40 % into the chest and that curve read
            // as a slouch. The chest goes a little up instead (back), the shoulders a little back.
            float lean = MornLeanDeg * mornLeanW, chestUp = MornChestUpDeg * mornLeanW;
            if (mornWaist != null) mornWaist.rotation = Quaternion.AngleAxis(lean, right) * mornWaist.rotation;
            else if (chest != null) chest.T.rotation = Quaternion.AngleAxis(lean, right) * chest.T.rotation;
            if (chest != null && mornWaist != null) chest.T.rotation = Quaternion.AngleAxis(-chestUp, right) * chest.T.rotation;

            // head and neck at his face; the neck takes little of the pitch (a bent neck reads as a hunch)
            float yaw, pitch;
            YawPitch(head, eye, 60f, 40f, out yaw, out pitch);
            yaw *= mornLeanW;
            pitch *= mornLeanW;
            if (neck != null)
            {
                Quaternion N = neck.Deform();
                neck.T.rotation = Quaternion.AngleAxis(yaw * 0.4f, N * Vector3.up) * Quaternion.AngleAxis(pitch * MornNeckPitchK, N * Vector3.right) * neck.T.rotation;
                yaw *= 0.6f; pitch *= 1f - MornNeckPitchK;
            }
            Quaternion H = head.Deform();
            head.T.rotation = Quaternion.AngleAxis(yaw, H * Vector3.up) * Quaternion.AngleAxis(pitch, H * Vector3.right)
                * Quaternion.AngleAxis(8f * mornLeanW, H * Vector3.forward) * head.T.rotation;   // a little head tilt

            if (!mornLeanLogged && mornLeanW > 0.98f)
            {
                mornLeanLogged = true;
                Debug.Log("[UncleSwap] Morning: pose lean hips " + lean.ToString("0") + " spine 0 chest " + (mornWaist != null ? -chestUp : lean).ToString("0") +
                          " neck pitch " + (pitch * MornNeckPitchK / (1f - MornNeckPitchK)).ToString("0") + " head pitch " + pitch.ToString("0") +
                          " (forward from vertical: waist-spine " + MornTip(mornWaist, mornSpine, fwd, up) + ", spine-chest " + MornTip(mornSpine, chest != null ? chest.T : null, fwd, up) +
                          ", chest-neck " + MornTip(chest != null ? chest.T : null, neck != null ? neck.T : null, fwd, up) + ", neck-head " + MornTip(neck != null ? neck.T : null, head.T, fwd, up) + ")");
            }

            if (mornWaveW <= 0.001f && mornArmW <= 0.001f) return;
            // task 11: the hand away from the bed up at head height in front of her shoulder, palm to him, swinging
            // side to side; the other arm hangs relaxed. v0.31 reached for the camera (above her: the hand ended
            // by her own face) and only aimed the fingers, so the palm twisted.
            Arm onRight = Vector3.Dot(armR.Upper.position - armL.Upper.position, right) >= 0f ? armR : armL;
            Arm onLeft = onRight == armR ? armL : armR;
            float side = MornWaveSide < 0 ? -1f : 1f;
            Arm a = side > 0f ? onRight : onLeft, o = side > 0f ? onLeft : onRight;
            float len = ArmLen(a);
            Vector3 sh = a.Upper.position;
            float tw = mornT - MornWaveStart - MornWaveIn * 0.5f;
            float swing = tw > 0f ? Mathf.Sin(tw * Mathf.PI * 2f * MornWaveHz) * Mathf.Clamp01(tw / 0.15f) : 0f;   // -1..1
            if (mornWaveW > 0.001f)
            {
                // v0.33.8 (task 15 bug): a wider side-to-side swing of the forearm (was +-0.13 arm, read as a squeeze),
                // the palm turned to his eye (he looks from above her head), the fingers rocking with the swing
                Vector3 wrist = sh + up * (0.45f * len) + right * (side * 0.28f * len) + fwd * (0.30f * len) + right * (swing * 0.22f * len);
                SolveArm(a, wrist, -up * 0.6f + right * (side * 0.8f) - fwd * 0.2f, mornWaveW);
                Vector3 toEye = eye - a.Hand.position;
                toEye = toEye.sqrMagnitude > 1e-6f ? toEye.normalized : fwd;
                Vector3 upP = Vector3.ProjectOnPlane(up, toEye), rightP = Vector3.ProjectOnPlane(right, toEye);
                if (upP.sqrMagnitude < 1e-6f || rightP.sqrMagnitude < 1e-6f) { upP = up; rightP = right; }
                upP.Normalize(); rightP.Normalize();
                // fingers up (in the plane facing him), rocking with the swing; thumb side towards her middle = palm to him
                Vector3 fingers = (upP + rightP * (swing * 0.45f)).normalized;
                MornAimHand(a, fingers, -rightP * side, mornWaveW);
                PoseFingers(a, false, mornWaveW);
            }
            if (o != null && mornArmW > 0.001f)
            {
                MornShoulderBack(o == armL ? shoulderL : shoulderR, o, fwd, MornShoulderBackDeg * mornLeanW);   // reset every frame (DozeResetBones)
                // the other arm: straight down by her side, elbow back, palm to the thigh
                float lo = ArmLen(o);
                Vector3 so = o.Upper.position;
                Vector3 wo = so - up * (0.93f * lo) - right * (side * 0.10f * lo) + fwd * (0.05f * lo);
                SolveArm(o, wo, -fwd - right * (side * 0.3f), mornArmW);
                MornAimHand(o, (-up + fwd * 0.08f).normalized, fwd, mornArmW);
            }
            if (!mornWaveLogged && mornWaveW > 0.95f)
            {
                mornWaveLogged = true;
                Vector3 palmN = Vector3.Cross(Knuckles(a) - a.Hand.position, MornAcross(a));
                Debug.Log("[UncleSwap] Morning: pose wave (" + (side > 0f ? "right" : "left") + " hand, wrist " +
                          (Vector3.Dot(a.Hand.position - head.T.position, up) / len).ToString("F2") + " arm above the head bone, " +
                          (Vector3.Dot(a.Hand.position - sh, fwd) / len).ToString("F2") + " arm in front of the shoulder, lean " +
                          (MornLeanDeg * mornLeanW).ToString("0") + " deg, palm normal . to his eye " + Vector3.Dot(palmN.normalized, (eye - a.Hand.position).normalized).ToString("F2") +
                          " (left hand: -1 = palm to him), swing 0.22 arm)");
            }
        }

        /// <summary>Task 15: turn a shoulder (clavicle) so its outer end goes back by deg (sign-safe for both sides).</summary>
        static void MornShoulderBack(Transform sh, Arm arm, Vector3 fwd, float deg)
        {
            if (sh == null || arm == null || deg < 0.01f) return;
            Vector3 v = arm.Upper.position - sh.position;
            Vector3 axis = Vector3.Cross(v, -fwd);
            if (axis.sqrMagnitude < 1e-12f) return;
            sh.rotation = Quaternion.AngleAxis(deg, axis.normalized) * sh.rotation;
        }

        /// <summary>Log only: how far the segment a->b tips forward from vertical, degrees.</summary>
        static string MornTip(Transform a, Transform b, Vector3 fwd, Vector3 up)
        {
            if (a == null || b == null) return "?";
            Vector3 d = b.position - a.position;
            float u = Vector3.Dot(d, up), f = Vector3.Dot(d, fwd);
            if (u * u + f * f < 1e-10f) return "?";
            return (Mathf.Atan2(f, u) * Mathf.Rad2Deg).ToString("0");
        }

        /// <summary>Task 13: head and neck turned to his face (up to 60/30 deg), the rest of her body in its idle stance.</summary>
        void MornStayHead()
        {
            var cam = Camera.main;
            if (cam == null || head == null) return;
            float yaw, pitch;
            YawPitch(head, cam.transform.position, 60f, 30f, out yaw, out pitch);
            float w = 0.85f * mornStayW;
            yaw *= w;
            pitch *= w;
            if (neck != null)
            {
                Quaternion N = neck.Deform();
                neck.T.rotation = Quaternion.AngleAxis(yaw * 0.4f, N * Vector3.up) * Quaternion.AngleAxis(pitch * 0.4f, N * Vector3.right) * neck.T.rotation;
                yaw *= 0.6f; pitch *= 0.6f;
            }
            Quaternion H = head.Deform();
            head.T.rotation = Quaternion.AngleAxis(yaw, H * Vector3.up) * Quaternion.AngleAxis(pitch, H * Vector3.right) * head.T.rotation;
        }

        /// <summary>Across the hand from the little-finger side to the index side (the thumb side).</summary>
        Vector3 MornAcross(Arm a)
        {
            var list = a == armL ? fingersL : fingersR;
            Finger idx = null, thumb = null;
            foreach (var f in list)
            {
                if (f.T.parent != a.Hand) continue;
                if (f.Index) idx = f;
                else if (f.Thumb) thumb = f;
            }
            if (idx != null)
            {
                Finger far = null;
                float best = 0f;
                foreach (var f in list)
                {
                    if (f.T.parent != a.Hand || f.Index || f.Thumb) continue;
                    float d = (f.T.position - idx.T.position).sqrMagnitude;
                    if (d > best) { best = d; far = f; }
                }
                if (far != null) return idx.T.position - far.T.position;
            }
            if (thumb != null) return thumb.T.position - Knuckles(a);
            return Vector3.zero;
        }

        /// <summary>Turn the hand so the fingers point along "fingers" and the thumb side along "across"
        /// (the palm then faces where the anatomy puts it).</summary>
        void MornAimHand(Arm a, Vector3 fingers, Vector3 across, float w)
        {
            Vector3 cf = Knuckles(a) - a.Hand.position, ca = MornAcross(a);
            if (cf.sqrMagnitude < 1e-10f) return;
            Quaternion delta;
            if (ca.sqrMagnitude < 1e-10f || Vector3.Cross(cf, ca).sqrMagnitude < 1e-12f) delta = Quaternion.FromToRotation(cf, fingers);
            else delta = Quaternion.LookRotation(fingers, across) * Quaternion.Inverse(Quaternion.LookRotation(cf, ca));
            a.Hand.rotation = Quaternion.Slerp(Quaternion.identity, delta, w) * a.Hand.rotation;
        }
    }
}
