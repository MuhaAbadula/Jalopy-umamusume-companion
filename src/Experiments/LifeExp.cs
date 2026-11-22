using System;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 87: what Experiments (CompanionDriver) needs from her body: one hand held out (the vial offer, the pour), a look at the
    /// player, a face, the game's click, extra crosshair targets and a pat that is answered with "not now".
    /// </summary>
    public partial class Life
    {
        /// <summary>The crosshair is on something of hers that can be clicked (the offered vial).</summary>
        public static Func<bool> ExtraCursor;
        /// <summary>True while she must not be distracted by a pat (pouring); the pat gets addon_busy.</summary>
        public static Func<bool> PatBusy;

        public Transform HandR { get { return armR != null ? armR.Hand : null; } }
        public float ArmLength { get { return armR != null ? ArmLen(armR) : 0f; } }
        public Vector3 ShoulderR { get { return armR != null ? armR.Upper.position : Vector3.zero; } }
        /// <summary>Middle of her right palm (wrist to knuckles), after this frame's pose.</summary>
        public Vector3 PalmR { get { return armR != null ? (armR.Hand.position + Knuckles(armR)) * 0.5f : Vector3.zero; } }
        /// <summary>Wrist to knuckles of the right hand (unit), the way the fingers point.</summary>
        public Vector3 HandAxisR
        {
            get
            {
                if (armR == null) return Vector3.forward;
                Vector3 d = Knuckles(armR) - armR.Hand.position;
                return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.forward;
            }
        }

        /// <summary>After PoseBones: her right wrist to a point, elbow towards pole (world direction).</summary>
        public void HandTo(Vector3 wrist, Vector3 pole, Vector3 palmDir, float w)
        {
            if (armR == null || w <= 0.001f) return;
            SolveArm(armR, wrist, pole, w);
            PoseFingers(armR, false, w);
            if (palmDir.sqrMagnitude > 1e-6f) OrientHand(armR, palmDir.normalized, Vector3.up, w);
        }

        /// <summary>In the car she sits right of the driver: the vial goes to him in her LEFT hand.</summary>
        public Vector3 ShoulderL { get { return armL != null ? armL.Upper.position : Vector3.zero; } }
        public Vector3 PalmL { get { return armL != null ? (armL.Hand.position + Knuckles(armL)) * 0.5f : Vector3.zero; } }

        /// <summary>After PoseBones: her left wrist to a point, elbow towards pole (world direction).</summary>
        public void HandToL(Vector3 wrist, Vector3 pole, Vector3 palmDir, float w)
        {
            if (armL == null || w <= 0.001f) return;
            SolveArm(armL, wrist, pole, w);
            PoseFingers(armL, false, w);
            if (palmDir.sqrMagnitude > 1e-6f) OrientHand(armL, palmDir.normalized, Vector3.up, w);
        }

        /// <summary>Fingertips of the right / left hand (wrist to knuckles ~ half the hand, recon 36): past a long cuff.</summary>
        public Vector3 TipR { get { return armR != null ? Tip(armR) : Vector3.zero; } }
        public Vector3 TipL { get { return armL != null ? Tip(armL) : Vector3.zero; } }
        Vector3 Tip(Arm a) { Vector3 kn = Knuckles(a); return kn + (kn - a.Hand.position) * 0.9f; }

        /// <summary>
        /// After PoseBones, before HandTo: she bends over from the hips (Waist, straight back like the morning lean) towards a
        /// point, just as far as needed for her right shoulder to come within reach of it (up to maxDeg), and looks down at it.
        /// Returns the bend in degrees (for the log).
        /// </summary>
        public float BendTo(Vector3 point, float maxDeg, float w)
        {
            if (w <= 0.001f || armR == null) return 0f;
            Transform pivot = mornWaist != null ? mornWaist : chest != null ? chest.T : null;
            if (pivot == null) return 0f;
            Vector3 up = Vector3.up;
            Vector3 fwd = Vector3.ProjectOnPlane(point - pivot.position, up);
            if (fwd.sqrMagnitude < 1e-6f) return 0f;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(up, fwd);   // a positive turn about it tips the top forward
            float L = ArmLen(armR);
            Vector3 P = pivot.position, S = armR.Upper.position - P;
            float best = maxDeg;
            for (float a = 0f; a <= maxDeg; a += 2.5f)
            {
                Vector3 s = P + Quaternion.AngleAxis(a, right) * S;
                if ((point - s).magnitude <= 0.8f * L) { best = a; break; }
            }
            float deg = best * w;
            pivot.rotation = Quaternion.AngleAxis(deg, right) * pivot.rotation;
            // a little more in the chest so the back rounds over the engine instead of a plank
            if (chest != null && pivot != chest.T) chest.T.rotation = Quaternion.AngleAxis(deg * 0.15f, right) * chest.T.rotation;
            // head down at the point
            if (head != null)
            {
                float yaw, pitch;
                YawPitch(head, point, 50f, 50f, out yaw, out pitch);
                yaw *= w; pitch *= w;
                if (neck != null)
                {
                    Quaternion N = neck.Deform();
                    neck.T.rotation = Quaternion.AngleAxis(yaw * 0.4f, N * Vector3.up) * Quaternion.AngleAxis(pitch * 0.4f, N * Vector3.right) * neck.T.rotation;
                    yaw *= 0.6f; pitch *= 0.6f;
                }
                Quaternion H = head.Deform();
                head.T.rotation = Quaternion.AngleAxis(yaw, H * Vector3.up) * Quaternion.AngleAxis(pitch, H * Vector3.right) * head.T.rotation;
            }
            return deg;
        }

        public void LookAtPlayer(float dur) { Attend(Look.Player, dur, 1f); }

        public void Mood(string face, float dur) { Emote(face, dur, 0.9f); }

        public static bool Click() { return GameClick(); }
    }
}
