using System.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    public partial class Life
    {
        /// <summary>Time of her own horn press (N2): her "horn" reaction (OnHorn) keeps quiet on it.</summary>
        float modHornAt = -100f;

        /// <summary>
        /// Task 38, N2 "honk at oncoming cars": an oncoming car 78-157 u ahead (30-60 m). Her driver-side hand to the wheel
        /// centre, CarHornC.Trigger(), StopAction() 0.5-1.2 s later (a coroutine on the horn, plus a backstop in Tick: the game
        /// breaks a horn held 15 s). At the press she waves at the car with the window-side hand and looks at it, then
        /// prank_horn and the giggle. No undone.
        /// </summary>
        class PrankHorn : PrankKind
        {
            public override string Id { get { return "N2"; } }
            /// <summary>Bug 3 (v0.41.3): the default key prank_n* has no lines in the pack.</summary>
            public override string Line { get { return "prank_horn"; } }

            CarHornC horn;
            Vector3 hornAim;
            Transform target;
            float hold, hornOffAt = -1f, dist;

            bool Find()
            {
                if (horn == null) { horn = L.CabPart<CarHornC>(); if (horn != null) hornAim = VisibleCentre(horn.transform); }
                return horn != null;
            }

            /// <summary>The nearest AI car 78-157 u ahead (within ~37 deg of our heading) driving towards us.</summary>
            Transform Oncoming(out float d)
            {
                d = 0f;
                if (L.car == null) return null;
                Vector3 from = L.head != null ? L.head.T.position : L.uncle.position;
                Vector3 cf = L.car.transform.forward;
                Transform best = null;
                float bd = float.MaxValue;
                foreach (var c in L.aiCars)
                {
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    Vector3 v = c.transform.position - from;
                    float m = v.magnitude;
                    if (m < 78f || m > 157f || m >= bd) continue;
                    if (Vector3.Dot(v, cf) < m * 0.8f) continue;   // ahead of us
                    var rb = c.GetComponent<Rigidbody>();
                    Vector3 way = rb != null && rb.velocity.sqrMagnitude > 1f ? rb.velocity.normalized : c.transform.forward;
                    if (Vector3.Dot(way, cf) > -0.6f) continue;    // coming our way (same test as LookForWave)
                    best = c.transform;
                    bd = m;
                }
                if (best != null) d = bd;
                return best;
            }

            public override string Can()
            {
                if (L.car == null) return "no car";
                if (!Find()) return "no horn found";
                if (horn.isDead) return "horn dead";
                string b = L.BatteryWhy();
                if (b != null) return b;
                target = Oncoming(out dist);
                if (target == null) return "no car";
                return null;
            }

            public override Beat Start(out string note)
            {
                hold = Random.Range(0.5f, 1.2f);
                note = target.name + " " + dist.ToString("F0") + " u ahead, hold " + hold.ToString("F1") + " s";
                return new Beat { Dur = 0.9f + hold + 0.9f, Look = Look.Reach, LookW = 0.6f, Face = "smug", FaceW = 0.5f, ActAt = 0.4f };
            }

            public override void Arm(Beat b, float env)
            {
                if (horn != null) L.WantReachTo("horn", horn.transform, hornAim, false, false, ReachCurve(b));
                if (b.Acted && target != null) b.Look = Look.Spot;   // eyes on the car once it honks
            }

            public override string Act(float now)
            {
                if (!Find() || horn.isDead) return "horn dead";
                string b = L.BatteryWhy();
                if (b != null) return b;
                L.modHornAt = Time.time;
                horn.Trigger();
                horn.StartCoroutine(StopLater(horn, hold));
                hornOffAt = now + hold;
                // wave at the car with the window-side hand (the driver-side one is on the horn)
                if (L.armL != null && L.armR != null)
                {
                    Vector3 fwd, up, winDir;
                    Arm outer, inner;
                    L.ArmSides(out outer, out inner, out fwd, out up, out winDir);
                    L.StartAct(Life.Act.Wave, 2.0f, outer);
                    L.nextWaveOk = now + 40f;
                    if (target != null) L.waveSeen = target;
                }
                How = "honk " + hold.ToString("F1") + " s at " + (target != null ? target.name : "?") + ", wave";
                return null;
            }

            static IEnumerator StopLater(CarHornC h, float t)
            {
                yield return new WaitForSeconds(t);
                if (h != null) h.StopAction();
            }

            /// <summary>After the wave (2 s): her line and the giggle.</summary>
            public override float CheckDelay { get { return 2.0f; } }

            public override void Tick(float now, bool inCar, bool idle)
            {
                if (target != null && L.pk == this)
                {
                    Vector3 fwd, up;
                    L.Facing(out fwd, out up);
                    L.spotPos = target.position + up * 0.8f;
                }
                // backstop: never leave the horn on (the coroutine stops it first; StopAction twice is harmless)
                if (hornOffAt >= 0f && now >= hornOffAt + 0.3f)
                {
                    hornOffAt = -1f;
                    if (horn != null) horn.StopAction();
                }
            }

            public override void End()
            {
                target = null;
            }
        }
    }
}
