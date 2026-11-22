using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// A detachable prop from the pack (submeshes named "backpack|&lt;material&gt;", built by Props).
    /// Worn: its own skinned renderer on her bones. In the car she "puts it on the back seat": the worn copy hides and
    /// a static copy (rest shape) is placed behind her seat, parented to the car. Getting out puts it back on.
    /// </summary>
    public class Backpack
    {
        public const string Name = "backpack";

        public bool Enabled = true;
        public float Back = 1.5f, Height = 0.3f, Side = 0f; // metres from her hips: behind, up, to her right

        readonly SkinnedMeshRenderer worn;
        readonly Mesh seatMesh;
        readonly Material[] mats;
        readonly Transform girlRoot;
        GameObject seat;
        bool onSeat;
        Vector3 lastHipLocal;
        bool hasLast;
        float settled, seatedFor;

        /// <summary>Task 30: the "backpack" prop from Props (built there), null when the pack has none.</summary>
        public static Backpack Build(Transform girlRoot)
        {
            var p = Props.Get(Name);
            return p != null ? new Backpack(p, girlRoot) : null;
        }

        Backpack(Prop p, Transform girlRoot)
        {
            this.girlRoot = girlRoot;
            worn = p.Renderer;
            mats = p.Materials;
            var pos = p.RestPositions;
            int nv = pos.Length;

            // on the seat: rest shape, pivot at the bottom centre
            var b = new Bounds(pos[0], Vector3.zero);
            foreach (var v in pos) b.Encapsulate(v);
            var pivot = new Vector3(b.center.x, b.min.y, b.center.z);
            var spos = new Vector3[nv];
            for (int i = 0; i < nv; i++) spos[i] = pos[i] - pivot;
            seatMesh = new Mesh { name = "BackpackOnSeat" };
            seatMesh.vertices = spos;
            seatMesh.normals = p.RestNormals;
            seatMesh.uv = p.UVs;
            seatMesh.subMeshCount = p.Triangles.Count;
            for (int k = 0; k < p.Triangles.Count; k++) seatMesh.SetTriangles(p.Triangles[k], k);
            seatMesh.RecalculateBounds();
        }

        public int Vertices { get { return seatMesh.vertexCount; } }

        GameObject Seat()
        {
            if (seat != null) return seat;
            seat = new GameObject("BackpackOnSeat");
            seat.AddComponent<MeshFilter>().sharedMesh = seatMesh;
            seat.AddComponent<MeshRenderer>().sharedMaterials = mats;
            seat.SetActive(false);
            return seat;
        }

        /// <summary>Settings changed: place it again on the next seated frame.</summary>
        public void Replace() { onSeat = false; }

        /// <summary>After the pose. `seated` = sitting in the car; car = the car transform; facing from her chest.</summary>
        public void Update(bool seated, Transform car, Vector3 hip, Vector3 fwd, Vector3 up)
        {
            seated = seated && Enabled && car != null;
            // "in the car" starts while she is still climbing in: wait until her hips rest in the car's frame
            if (seated && !onSeat)
            {
                Vector3 local = car.InverseTransformPoint(hip);
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                float speed = hasLast ? (hip - car.TransformPoint(lastHipLocal)).magnitude / dt : 999f;
                lastHipLocal = local;
                hasLast = true;
                settled = speed < 0.1f ? settled + dt : 0f;
                seatedFor += dt;
                // the game snaps the uncle's root to the seat at once and plays the climb-in; it shuts the door
                // after 4 s (UncleEnterCar). Before that she may stand still at the door, so time AND stillness.
                if (!(seatedFor >= 3.5f && settled >= 0.5f) && seatedFor < 8f) seated = false;
            }
            else if (!seated) { hasLast = false; settled = 0f; seatedFor = 0f; }
            if (seated && (!onSeat || seat == null))
            {
                var s = Seat();
                s.transform.SetParent(null, false);
                Vector3 right = Vector3.Cross(up, fwd);
                s.transform.position = hip - fwd * Back + up * Height + right * Side;
                s.transform.rotation = Quaternion.LookRotation(-fwd, up); // the cat looks to the front of the car
                s.transform.localScale = Vector3.one * girlRoot.lossyScale.x;
                s.transform.SetParent(car, true);
                s.SetActive(true);
                worn.enabled = false;
                onSeat = true;
            }
            else if (!seated && onSeat)
            {
                if (seat != null)
                {
                    seat.SetActive(false);
                    seat.transform.SetParent(girlRoot, false);
                }
                worn.enabled = true;
                onSeat = false;
            }
            else if (!seated && !worn.enabled) worn.enabled = true;
        }

        public void Destroy()
        {
            if (seat != null) Object.Destroy(seat);
        }
    }
}
