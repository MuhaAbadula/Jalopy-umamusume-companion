using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// v0.25.6 twitch detector (debug). Each frame, after the whole pose (physics, backpack, hands), the world positions
    /// of key points are kept; a point whose second difference (a jolt) is several times its usual size is logged
    /// with what was going on: phase, contacts, pins, the uncle's animator state, his root jolt, the frame time.
    /// Logs only while her walk runs outside the car. "[Umamusume companion] twitch: ..."
    /// </summary>
    public partial class CompanionDriver
    {
        class Probe { public string Name; public Transform T; public Vector3 P0, P1, P2; public int N; public float Ema, NextLog; }
        List<Probe> probes;
        Probe rootProbe, uHipProbe;
        int twitchLogs;
        float lastProbeT;

        void InitProbes()
        {
            probes = new List<Probe>();
            foreach (var n in new[] { "Hip", "Chest", "Head", "Knee_L", "Knee_R", "Ankle_L", "Ankle_R", "Elbow_L", "Elbow_R", "Wrist_L", "Wrist_R" })
            {
                Transform t;
                if (boneByName != null && boneByName.TryGetValue(n, out t)) probes.Add(new Probe { Name = n, T = t });
            }
            rootProbe = new Probe { Name = "uncle root", T = transform };
            uHipProbe = uHip != null ? new Probe { Name = "uncle hip", T = uHip } : null;
        }

        static float Jolt(Probe p, float leg)
        {
            p.P0 = p.P1; p.P1 = p.P2; p.P2 = p.T.position; p.N++;
            if (p.N < 3) return 0f;
            return (p.P2 - 2f * p.P1 + p.P0).magnitude / leg;
        }

        void ProbeTwitch()
        {
            if (boneByName == null || !WalkDebug) return;
            if (probes == null) InitProbes();
            float now = Time.time, dt = now - lastProbeT;
            lastProbeT = now;
            bool walking = NewWalk && walkW > 0.3f && !InCar();
            if (!walking) { foreach (var p in probes) p.N = 0; rootProbe.N = 0; if (uHipProbe != null) uHipProbe.N = 0; return; }
            float leg = Mathf.Max(1e-3f, walkLegLen);
            float rootJ = Jolt(rootProbe, leg);
            float hipJ = uHipProbe != null ? Jolt(uHipProbe, leg) : 0f;
            foreach (var p in probes)
            {
                float j = Jolt(p, leg);
                if (p.N < 4) { p.Ema = j; continue; }
                bool spike = p.N > 30 && j > 6f * p.Ema && j > 0.004f;
                if (spike && now >= p.NextLog && twitchLogs < 200)
                {
                    p.NextLog = now + 0.1f;
                    twitchLogs++;
                    float cl, cr, dy;
                    gestures.WalkContacts(walkPhase, out cl, out cr, out dy);
                    string anim = "?";
                    if (animator != null)
                    {
                        var ci = animator.GetCurrentAnimatorClipInfo(0);
                        var st = animator.GetCurrentAnimatorStateInfo(0);
                        anim = (ci != null && ci.Length > 0 && ci[0].clip != null ? ci[0].clip.name : "-") + " t" + st.normalizedTime.ToString("F2")
                            + (animator.IsInTransition(0) ? " (transition)" : "") + " ws " + animator.GetFloat("walkSpeed").ToString("F2");
                    }
                    Debug.Log("[Umamusume companion] twitch: " + p.Name + " jolt " + j.ToString("F4") + " (usual " + p.Ema.ToString("F4") + ") t " + now.ToString("F3")
                        + " dt " + (dt * 1000f).ToString("F1") + "ms ph " + walkPhase.ToString("F3") + " c " + cl.ToString("F2") + "/" + cr.ToString("F2")
                        + " pin " + (legL != null ? legL.LockW.ToString("F2") : "-") + "/" + (legR != null ? legR.LockW.ToString("F2") : "-")
                        + " | uncle root jolt " + rootJ.ToString("F4") + " hip jolt " + hipJ.ToString("F4") + " | " + anim
                        + " | speed " + (rootSpeed / leg).ToString("F2") + " mix " + gestures.WalkBlend.ToString("F2"));
                }
                p.Ema = Mathf.Lerp(p.Ema, j, spike ? 0.01f : 0.05f);
            }
        }
    }
}
