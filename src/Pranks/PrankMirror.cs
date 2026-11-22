using UnityEngine;
using Random = UnityEngine.Random;

namespace UmamusumeCompanion
{
    public partial class Life
    {
        /// <summary>
        /// Task 40, N5 "the mirror is mine" (concept 07 rev 2): the rear-view mirror RV_Mirror_001 (not the door mirrors or the
        /// sun visors, they carry MirrorLogicC too). Her driver-side hand to the glass (stops short, recon 36: 22 cm), her own
        /// iTween.RotateTo turns it so the glass shows her head to his eye (the glass normal = MirrorReflection's transform.up,
        /// turned to halfway between "to his eye" and "to her head", recon 36 §4: about 44 deg), currentPos = 1, so his click on
        /// the mirror sends it back to the game's rest preset. Then she looks at it and straightens her hat (hand on the brim),
        /// prank_mirror, giggle. Undone = he puts it back (currentPos -> 0 or it moved off her angle). Left alone, she turns it back
        /// herself 30-40 s later (hand to the mirror again). Skips when the mirror renders nothing (graphics setting: car.mirrors
        /// off) or the car key hangs on it (keyHolder).
        /// Log: Prank: N5 start|acted|done|caught|undone|skip ...; Prank: N5 mirror back by herself.
        /// </summary>
        class PrankMirror : PrankKind
        {
            public override string Id { get { return "N5"; } }
            public override string Line { get { return "prank_mirror"; } }

            MirrorLogicC ml;
            Transform mesh;
            MirrorReflection refl;
            GameObject reflGo;     // the car.mirrors entry under it (render on/off)
            Vector3 aim;
            bool layerLogged;

            // bug 4 (v0.42.4): from the roof the wide brim hides her face; she pulls the mirror down to her face first
            // (render 40: 0.5 u = 19 cm shows her face whether she looks at him, ahead or at the mirror)
            const float Drop = 0f;   // bug 5 (v0.42.6): "the mirror slid down" - it stays at its mount, turn only
            Vector3 homeLocal, pFrom, pTo;
            bool dropped;
            float pT0 = -1f;
            const float PDur = 1.0f;

            int startPos;
            Vector3 startEuler;
            Quaternion turnedLocal;
            bool sheTurned, backActed;
            float turnedAt, backAt = -1f, backT = -1f;

            bool Find()
            {
                if (ml != null && mesh != null) return true;
                ml = null;
                Transform root = L.carCtl != null ? L.carCtl.transform.root : L.car != null ? L.car.transform.root : null;
                if (root != null)
                    foreach (var m in root.GetComponentsInChildren<MirrorLogicC>(true))
                        if (m.transform.parent != null && (m.name.StartsWith("RV_Mirror") || m.transform.parent.name.StartsWith("RV_Mirror"))) { ml = m; break; }
                if (ml == null)
                {
                    var t = L.NearestOf<MirrorLogicC>("RV_Mirror");
                    if (t != null) ml = t.GetComponent<MirrorLogicC>();
                }
                if (ml == null || ml.transform.parent == null) return false;
                mesh = ml.transform.parent;
                refl = mesh.GetComponentInChildren<MirrorReflection>(true);
                reflGo = null;
                if (L.car != null && L.car.mirrors != null)
                    foreach (var g in L.car.mirrors)
                        if (g != null && g.transform.IsChildOf(mesh)) { reflGo = g; break; }
                aim = VisibleCentre(ml.transform);
                return true;
            }

            /// <summary>Null = the mirror shows a picture now.</summary>
            string RenderWhy()
            {
                if (refl == null) return "no reflection on it";
                var g = reflGo != null ? reflGo : refl.gameObject;
                return g.activeSelf ? null : "mirror render off (graphics)";
            }

            public override string Can()
            {
                if (L.car == null) return "no car";
                if (!Find()) return "no rear-view mirror";
                string r = RenderWhy();
                if (r != null) return r;
                if (ml.keyHolder != null && ml.keyHolder.transform.childCount > 0) return "key on the mirror";
                if (sheTurned) return "her mirror still turned";
                if (HatAway) return "her hat is away";
                if (ml.positionRotation == null || ml.positionRotation.Length < 2) return "no presets";
                if (Camera.main == null) return "no camera";
                Vector3 e;
                float deg;
                string why = Turn(out e, out deg);
                if (why != null) return why;
                return null;
            }

            string geo;
            float checkAt = -1f;

            /// <summary>Her eyes (middle of the eye bones), else the front of her head sphere.</summary>
            bool Eyes(out Vector3 e)
            {
                if (eyesSmooth) { e = mesh.parent.TransformPoint(eyesLocal); return true; }
                e = Vector3.zero;
                if (L.eyeL != null && L.eyeR != null) { e = (L.eyeL.T.position + L.eyeR.T.position) * 0.5f; return true; }
                Vector3 c;
                float r;
                if (!L.HeadSphere(out c, out r)) return false;
                e = c + (L.head.Deform() * Vector3.forward) * (r * 0.5f);
                return true;
            }

            /// <summary>
            /// What the middle of the glass shows. Bug 6 (v0.42.8, probe 2 of the user's run): the mirror shader samples the
            /// reflection texture at the glass's own screen position (a true mirror); v0.42.5-0.42.7 assumed the _ProjMatrix patch
            /// and aimed at the wrong part of the texture. So: his eye ray through the middle of the drawn glass meets the
            /// reflection plane (through the Mirror pivot + m_ClipPlaneOffset * up) at G, and is reflected there.
            /// d = turn of the mirror mesh about pivot.
            /// </summary>
            void Centre(Quaternion d, Vector3 pivot, Vector3 eye, out Vector3 G, out Vector3 n, out float dist)
            {
                var rr = refl.GetComponent<Renderer>();
                Vector3 glassNow = rr != null && rr.bounds.size.sqrMagnitude > 1e-8f ? rr.bounds.center : refl.transform.position;
                Vector3 P = pivot + d * (refl.transform.position - mesh.position);
                n = d * refl.transform.up;
                Vector3 P0 = P + n * refl.m_ClipPlaneOffset;
                Vector3 Qc = pivot + d * (glassNow - mesh.position);
                Vector3 v = Qc - eye;
                dist = v.magnitude;
                float den = Vector3.Dot(n, v);
                G = Mathf.Abs(den) > 1e-6f ? eye + v * (Vector3.Dot(n, P0 - eye) / den) : Qc;
            }

            /// <summary>Degrees between what the middle of the glass shows and her eyes (mirror as it is now).</summary>
            float Miss(out float fieldW, out float fieldH)
            {
                fieldW = fieldH = 0f;
                var cam = Camera.main;
                Vector3 E, G, n;
                float dist;
                if (cam == null || refl == null || !Eyes(out E)) return -1f;
                Vector3 eye = cam.transform.position;
                Centre(Quaternion.identity, mesh.position, eye, out G, out n, out dist);
                var mf = refl.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && dist > 1e-4f)
                {
                    Vector3 ext = mf.sharedMesh.bounds.extents, ls = refl.transform.lossyScale;
                    fieldW = 2f * Mathf.Atan(Mathf.Abs(ext.x * ls.x) / dist) * Mathf.Rad2Deg;
                    fieldH = 2f * Mathf.Atan(Mathf.Abs(ext.z * ls.z) / dist) * Mathf.Rad2Deg;
                }
                return Vector3.Angle(Vector3.Reflect((G - eye).normalized, n.normalized), E - G);
            }

            int probeN;
            float trackLogAt = -1f;

            /// <summary>
            /// Bug 5 (v0.42.6 probe): the glass shows only ~10 x 4 deg (MirrorReflection's projection), and her eyes move a few cm once
            /// she looks up at the mirror, so a one-off aim drops her eyes off the short side of the glass. While the mirror is hers
            /// (after her 1 s turn, he has not clicked it, she is not turning it back) it follows her eyes, at most 25 deg/s, at its
            /// mount. Log once ~3 s after the turn: the residual (model, checked against the probe).
            /// </summary>
            // bug 7 (v0.42.10): "the mirror shakes": her eyes, smoothed in the car's frame (~0.8 s), a dead zone and a slow ease
            bool eyesSmooth, trackMoving, eyesInit;
            Vector3 eyesLocal;
            const float EyeTau = 0.8f, DeadZone = 2.5f, Settle = 0.4f, MaxSpeed = 10f;

            void Track(float now, bool inCar)
            {
                if (mesh == null || ml == null || ml.currentPos != 1 || backT >= 0f || !inCar || now < turnedAt + 1.05f) { eyesInit = false; trackMoving = false; return; }
                Vector3 raw;
                if (!Eyes(out raw)) return;
                Vector3 loc = mesh.parent.InverseTransformPoint(raw);
                float dt = Time.deltaTime;
                eyesLocal = eyesInit ? Vector3.Lerp(eyesLocal, loc, 1f - Mathf.Exp(-dt / EyeTau)) : loc;
                eyesInit = true;
                Vector3 e;
                float deg;
                Quaternion target;
                eyesSmooth = true;
                string why;
                try { why = Turn(out e, out deg, out target); }
                finally { eyesSmooth = false; }
                if (why == "no eye/head") return;
                float off = Quaternion.Angle(mesh.rotation, target);
                if (!trackMoving && off > DeadZone) trackMoving = true;   // her eyes left the middle: follow
                if (trackMoving)
                {
                    float speed = Mathf.Clamp(off * 1.5f, 1f, MaxSpeed);   // eases in near the target
                    mesh.rotation = Quaternion.RotateTowards(mesh.rotation, target, speed * dt);
                    if (off < Settle) trackMoving = false;
                }
                if (trackLogAt >= 0f && now >= trackLogAt)
                {
                    trackLogAt = -1f;
                    float fw, fh;
                    PLog("N5 tracking: the middle of the glass is " + Miss(out fw, out fh).ToString("F1") + " deg off her eyes (glass shows " + fw.ToString("F1") + " x " + fh.ToString("F1") + " deg)");
                    if (DebugPanel.Allowed)
                    {
                        try { Probe(); }
                        catch (System.Exception ex) { PLog("N5 probe failed: " + ex.Message); }
                    }
                }
            }

            /// <summary>
            /// Bug 5: what the glass really shows, from the matrices the game rendered with. The mirror material's _ProjMatrix maps a
            /// glass vertex (object space) to the texture coordinate it samples; the reflection texture shows world X where the main
            /// camera would see R(X) (R = reflection across the plane through pivot + m_ClipPlaneOffset * up). Logs the texture
            /// coordinates of the glass middle and corners and of her eyes, the reflection camera, what lies on the reflected ray, and
            /// saves the reflection texture as ModSaves/UmamusumeCompanion/mirror_probe_N.png (green = glass middle, yellow = the part of the
            /// texture the glass shows, red = her eyes).
            /// </summary>
            void Probe()
            {
                var cam = Camera.main;
                var rend = refl.GetComponent<Renderer>();
                var mf = refl.GetComponent<MeshFilter>();
                Vector3 E;
                if (cam == null || rend == null || rend.sharedMaterial == null || mf == null || mf.sharedMesh == null || !Eyes(out E)) { PLog("N5 probe: nothing to read"); return; }
                var mat = rend.sharedMaterial;
                // the glass samples the texture at its own screen position (bug 6), so project the glass with the main camera
                Matrix4x4 l2w = refl.transform.localToWorldMatrix;
                var rt = mat.HasProperty("_ReflectionTex") ? mat.GetTexture("_ReflectionTex") as RenderTexture : null;
                Bounds b = mf.sharedMesh.bounds;
                System.Func<Vector3, Vector2> uvOf = v => { Vector3 vp = cam.WorldToViewportPoint(l2w.MultiplyPoint(v)); return new Vector2(vp.x, vp.y); };
                Vector2 uvC = uvOf(b.center);
                var cs = new Vector2[4];
                for (int i = 0; i < 4; i++)
                    cs[i] = uvOf(b.center + new Vector3((i == 1 || i == 2) ? b.extents.x : -b.extents.x, 0f, i >= 2 ? b.extents.z : -b.extents.z));
                Vector3 n = refl.transform.up.normalized;
                Vector3 P0 = refl.transform.position + n * refl.m_ClipPlaneOffset;
                System.Func<Vector3, Vector3> R = x => x - 2f * n * Vector3.Dot(n, x - P0);
                Vector3 vpE = cam.WorldToViewportPoint(R(E));
                Vector3 eye = cam.transform.position, eyeR = R(eye);
                var rc = refl.ReflectionCamera;
                Vector3 vpGlass = cam.WorldToViewportPoint(rend.bounds.center);
                string rtInfo = rt != null ? rt.width + "x" + rt.height + " " + rt.name : "none";
                // her eyes in glass units: (along its long side, along its short side), 1 = at the edge
                Vector2 ax = (cs[1] - cs[0] + cs[2] - cs[3]) * 0.25f, az = (cs[3] - cs[0] + cs[2] - cs[1]) * 0.25f;   // half-axes in uv
                Vector2 dE = new Vector2(vpE.x, vpE.y) - uvC;
                float det = ax.x * az.y - ax.y * az.x;
                Vector2 frac = Mathf.Abs(det) > 1e-9f ? new Vector2((dE.x * az.y - dE.y * az.x) / det, (ax.x * dE.y - ax.y * dE.x) / det) : Vector2.zero;
                PLog("N5 probe: her eyes on the glass at " + frac.ToString("F2") + " of its half-size (long, short side; inside when both are within 1)");
                PLog("N5 probe: glass middle samples uv " + uvC.ToString("F3") + ", glass corners uv " + cs[0].ToString("F3") + " " + cs[1].ToString("F3") + " "
                    + cs[2].ToString("F3") + " " + cs[3].ToString("F3") + "; her eyes at uv " + new Vector2(vpE.x, vpE.y).ToString("F3")
                    + (vpE.z < 0f ? " (BEHIND the reflected view)" : "") + "; the glass is at screen " + new Vector2(vpGlass.x, vpGlass.y).ToString("F3")
                    + "; main cam fov " + cam.fieldOfView.ToString("F0") + " aspect " + cam.aspect.ToString("F2") + " pos " + eye.ToString("F2") + " fwd " + cam.transform.forward.ToString("F2")
                    + "; reflection cam " + (rc != null ? "fov " + rc.fieldOfView.ToString("F0") + " near " + rc.nearClipPlane.ToString("F2") + " far " + rc.farClipPlane.ToString("F1") : "none")
                    + ", reflected eye " + eyeR.ToString("F2") + ", her eyes " + E.ToString("F2") + ", texture " + rtInfo);
                // the reflection camera has no oblique clip: whatever is between the reflected eye and the glass plane covers the picture
                Vector3 vC = cam.ViewportToWorldPoint(new Vector3(uvC.x, uvC.y, 1f)) - eye;
                Vector3 dir = R(eye + vC) - eyeR;
                RaycastHit hit;
                string block = Physics.Raycast(eyeR, dir.normalized, out hit, (E - eyeR).magnitude)
                    ? hit.collider.name + " (layer " + hit.collider.gameObject.layer + ") at " + hit.distance.ToString("F2") + " u from the reflected eye, glass plane at "
                      + Vector3.Distance(eyeR, eye) * 0.5f + " u"
                    : "nothing with a collider";
                PLog("N5 probe: first hit on the reflected ray of the glass middle: " + block);
                if (rt == null) return;
                var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(rt, tmp);
                var prev = RenderTexture.active;
                RenderTexture.active = tmp;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(tmp);
                System.Action<Vector2, Color, int> cross = (uv, c, r) =>
                {
                    int x0 = Mathf.RoundToInt(uv.x * tex.width), y0 = Mathf.RoundToInt(uv.y * tex.height);
                    for (int k = -r; k <= r; k++)
                        for (int t = -1; t <= 1; t++)
                        {
                            int a = x0 + k, bb = y0 + t, c2 = x0 + t, d2 = y0 + k;
                            if (a >= 0 && a < tex.width && bb >= 0 && bb < tex.height) tex.SetPixel(a, bb, c);
                            if (c2 >= 0 && c2 < tex.width && d2 >= 0 && d2 < tex.height) tex.SetPixel(c2, d2, c);
                        }
                };
                for (int i = 0; i < 4; i++)
                    for (int k = 0; k <= 200; k++) cross(Vector2.Lerp(cs[i], cs[(i + 1) % 4], k / 200f), Color.yellow, 0);
                cross(uvC, Color.green, 14);
                cross(new Vector2(vpE.x, vpE.y), Color.red, 14);
                tex.Apply();
                string dir2 = CompanionStorage.SaveDirectory;
                System.IO.Directory.CreateDirectory(dir2);
                string path = System.IO.Path.Combine(dir2, "mirror_probe_" + (++probeN) + ".png");
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.Destroy(tex);
                PLog("N5 probe: saved " + path);
            }

            /// <summary>Local euler of the mirror mesh that puts her eyes in the middle of the glass for his eye, and the turn.</summary>
            string Turn(out Vector3 euler, out float deg)
            {
                Quaternion target;
                return Turn(out euler, out deg, out target);
            }

            string Turn(out Vector3 euler, out float deg, out Quaternion target)
            {
                euler = Vector3.zero; deg = 0f; target = mesh != null ? mesh.rotation : Quaternion.identity;
                var cam = Camera.main;
                Vector3 E;
                if (cam == null || refl == null || mesh.parent == null || !Eyes(out E)) return "no eye/head";
                Vector3 eye = cam.transform.position;
                Vector3 shift = dropped ? Vector3.zero : -L.uncle.up * Drop;   // where she will pull it to
                Vector3 pivot = mesh.position + shift;
                Quaternion q = mesh.rotation, inv0 = Quaternion.Inverse(mesh.rotation);
                Vector3 G = Vector3.zero, n = Vector3.up;
                float dist;
                for (int i = 0; i < 8; i++)
                {
                    Centre(q * inv0, pivot, eye, out G, out n, out dist);
                    Vector3 want = ((eye - G).normalized + (E - G).normalized).normalized;
                    Vector3 nn = Vector3.Dot(n, want) > 0f ? n : -n;
                    q = Quaternion.FromToRotation(nn, want) * q;
                }
                Centre(q * inv0, pivot, eye, out G, out n, out dist);
                deg = Quaternion.Angle(mesh.rotation, q);
                euler = (Quaternion.Inverse(mesh.parent.rotation) * q).eulerAngles;
                target = q;
                var U = L.uncle;
                geo = "her frame (x right, y up, z fwd): eye->centre " + U.InverseTransformDirection(G - eye).ToString("F2")
                    + ", eye->her eyes " + U.InverseTransformDirection(E - eye).ToString("F2")
                    + ", solve misses by " + Vector3.Angle(Vector3.Reflect((G - eye).normalized, n.normalized), E - G).ToString("F1") + " deg";
                if (deg < 2f) return "already on her";
                if (deg > 80f) return "angle off (" + deg.ToString("F0") + " deg)";
                return null;
            }

            public override Beat Start(out string note)
            {
                note = "mirror pos " + ml.currentPos;
                // hand to the glass 0.1-0.6 s, turn at 0.55 s (1 s), hand back by 1.9 s; then she looks at it and straightens her hat
                return new Beat { Dur = 3.6f, Look = Look.Reach, LookW = 0.85f, Face = "focused", FaceW = 0.4f, ActAt = 0.55f };
            }

            public override void Arm(Beat b, float env)
            {
                float T = b.T;
                if (mesh != null)
                    L.WantReachTo("mirror", ml.transform, aim, false, false, Mathf.Clamp01(Mathf.Min((T - 0.1f) / 0.5f, (1.9f - T) / 0.4f)));
                if (b.Acted && T > 1.4f && mesh != null)
                {
                    b.Look = Look.Spot;
                    b.Face = "smug";
                    b.FaceW = 0.6f;
                    L.spotPos = ml.transform.TransformPoint(aim);
                }
                L.WantBrim(false, Mathf.Clamp01(Mathf.Min((T - 1.7f) / 0.4f, (b.Dur - 0.2f - T) / 0.35f)), 0.05f, 1f);
            }

            public override string Act(float now)
            {
                if (!Find()) return "no rear-view mirror";
                if (ml.keyHolder != null && ml.keyHolder.transform.childCount > 0) return "key on the mirror";
                Vector3 e;
                float deg;
                string why = Turn(out e, out deg);
                if (why != null) return why;
                startPos = ml.currentPos;
                startEuler = mesh.localEulerAngles;
                if (!dropped)
                {
                    homeLocal = mesh.localPosition;
                    MoveTo(mesh.parent.InverseTransformPoint(mesh.position - L.uncle.up * Drop));
                    dropped = true;
                }
                turnedLocal = Quaternion.Euler(e);
                iTween.Stop(mesh.gameObject);
                iTween.RotateTo(mesh.gameObject, iTween.Hash("rotation", e, "islocal", true, "time", 1.0f, "easetype", "easeInOutSine"));
                ml.currentPos = 1;   // his click sends it to the game's rest preset (MirrorLogicC.Trigger)
                Sound();
                sheTurned = true;
                turnedAt = now;
                backAt = now + Random.Range(30f, 40f);
                backT = -1f;
                LayerNote();
                PLog("N5 aim: " + geo);
                checkAt = now + 1.3f;   // after the 1 s turn: measure what the glass really shows
                trackLogAt = now + 4f;  // and again once she has been following it a while
                How = "mirror turned " + deg.ToString("F0") + " deg to her (from pos " + startPos + ")";
                return null;
            }

            void MoveTo(Vector3 local)
            {
                pFrom = mesh.localPosition;
                pTo = local;
                pT0 = Time.time;
            }

            /// <summary>Back up to its mount (his click or her own turn-back).</summary>
            void Raise(string why)
            {
                if (!dropped || mesh == null) return;
                dropped = false;
                MoveTo(homeLocal);
                PLog("N5 mirror up again (" + why + ")");
            }

            void Sound()
            {
                var a = ml.GetComponent<AudioSource>();
                if (a != null && ml.audioSample != null) a.PlayOneShot(ml.audioSample, 0.7f);
            }

            /// <summary>Once: does the mirror camera draw her layer at all?</summary>
            void LayerNote()
            {
                if (layerLogged || refl == null || L.head == null) return;
                layerLogged = true;
                var rr = L.head.T.root.GetComponentInChildren<SkinnedMeshRenderer>();
                int layer = rr != null ? rr.gameObject.layer : L.head.T.gameObject.layer;
                bool yes = (refl.m_ReflectLayers.value & (1 << layer)) != 0;
                PLog("N5 mirror reflects layer " + layer + " (hers): " + (yes ? "yes" : "NO, her face will not show") + ", mask " + refl.m_ReflectLayers.value);
            }

            /// <summary>He moved it: back to a preset (currentPos 0) or off her angle once her turn is over.</summary>
            string Moved(float now)
            {
                if (mesh == null || ml == null) return "mirror gone";
                if (ml.currentPos != 1) return "mirror back";
                return null;   // v0.42.3 also counted "off her angle": false hits after a debug restart
            }

            public override float CheckDelay { get { return 2.9f; } }

            public override string UndoneBy(float now)
            {
                string m = Moved(now);
                if (m != null) { sheTurned = false; Raise("he clicked it"); }
                return m;
            }

            /// <summary>Left alone, 30-40 s after: she turns it back herself (hand to the mirror).</summary>
            public override void Tick(float now, bool inCar, bool idle)
            {
                if (checkAt >= 0f && now >= checkAt && mesh != null)
                {
                    checkAt = -1f;
                    float fw, fh;
                    float m = Miss(out fw, out fh);
                    PLog("N5 aim check (model) after the turn: the middle of the glass is " + m.ToString("F1") + " deg off her eyes (glass shows "
                        + fw.ToString("F1") + " x " + fh.ToString("F1") + " deg)");
                    if (DebugPanel.Allowed)
                    {
                        try { Probe(); }
                        catch (System.Exception e) { PLog("N5 probe failed: " + e.Message); }
                    }
                }
                if (pT0 >= 0f && mesh != null)
                {
                    float t = Mathf.Clamp01((now - pT0) / PDur);
                    mesh.localPosition = Vector3.Lerp(pFrom, pTo, Mathf.SmoothStep(0f, 1f, t));
                    if (t >= 1f) pT0 = -1f;
                }
                if (!sheTurned) { if (dropped && ml != null && ml.currentPos != 1) Raise("he clicked it"); return; }
                Track(now, inCar);
                if (L.pk == this)
                {
                    // her 10 s window: after the look at him she keeps admiring herself (render 40: her face fills the glass
                    // only while her head is turned up at it)
                    if (L.pState == PState.Open && idle && L.beat == null && now >= L.attnUntil && ml != null)
                    {
                        L.spotPos = ml.transform.TransformPoint(aim);
                        L.curLook = Look.Spot;
                        L.curLookW = 0.85f;
                    }
                    return;   // its own beat and 10 s window first
                }
                if (backT < 0f)
                {
                    if (Moved(now) != null) { sheTurned = false; Raise("he clicked it"); PLog("N5 mirror: he put it back, nothing to turn back"); return; }
                    if (now < backAt) return;
                    if (!idle || L.beat != null || L.PrankBusy || L.act != Life.Act.None || L.GestureWeight > 0.01f || L.patT >= 0f) return;
                    backT = 0f;
                    backActed = false;
                    L.nextBeat = Mathf.Max(L.nextBeat, now + 3f);
                }
                if (!inCar) { backT = -1f; backAt = now + 5f; return; }
                backT += Time.deltaTime;
                const float dur = 2.2f;
                float w = Mathf.Clamp01(Mathf.Min((backT - 0.1f) / 0.5f, (dur - 0.3f - backT) / 0.5f));
                L.WantReachTo("mirror", ml.transform, aim, false, false, w);
                if (idle) { L.curLook = Look.Reach; L.curLookW = 0.8f; }
                if (!backActed && backT >= 0.45f && (L.ReachDone || backT >= 1.2f))
                {
                    backActed = true;
                    if (ml.currentPos == 1)
                    {
                        Vector3 to = startPos >= 0 && startPos < ml.positionRotation.Length ? ml.positionRotation[startPos] : startEuler;
                        iTween.Stop(mesh.gameObject);
                        iTween.RotateTo(mesh.gameObject, iTween.Hash("rotation", to, "islocal", true, "time", 1.0f, "easetype", "easeInOutSine"));
                        ml.currentPos = startPos;
                        Sound();
                    }
                    Raise("she put it back");
                    PLog("N5 mirror back by herself (to pos " + startPos + ")" + (L.ReachDone ? ", hand short " + L.ReachShort.ToString("F3") + " u" : ""));
                }
                if (backT >= dur) { backT = -1f; sheTurned = false; }
            }
        }
    }
}
