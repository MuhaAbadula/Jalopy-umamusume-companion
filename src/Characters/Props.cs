using System.Collections.Generic;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// Task 30: props from the pack. export_char.py writes a prop's triangles to submeshes named "&lt;prop&gt;|&lt;material&gt;"
    /// (props in the rig profile). The body mesh skips them; each prop gets its own skinned renderer on her bones.
    /// "backpack" also has the back-seat behaviour (Backpack.cs); "hat" and any other prop are just worn as exported.
    /// <see cref="Get"/> is the hook for later prop logic (e.g. the hat in the cab). No prop in the pack = nothing happens.
    /// </summary>
    public static class Props
    {
        static readonly List<Prop> list = new List<Prop>();

        /// <summary>Prop name of a submesh ("backpack", "hat"), null for the body.</summary>
        public static string NameOf(CharPack.SubMesh s)
        {
            int bar = s.Material != null ? s.Material.IndexOf('|') : -1;
            return bar > 0 ? s.Material.Substring(0, bar) : null;
        }

        public static bool IsProp(CharPack.SubMesh s) { return NameOf(s) != null; }

        /// <summary>The prop of the picked character, null when her pack has none by that name.</summary>
        public static Prop Get(string name)
        {
            foreach (var p in list) if (string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>"backpack", "backpack,hat" or "none".</summary>
        public static string Names
        {
            get
            {
                if (list.Count == 0) return "none";
                var n = new string[list.Count];
                for (int i = 0; i < n.Length; i++) n[i] = list[i].Name;
                return string.Join(",", n);
            }
        }

        public static IList<Prop> All { get { return list; } }

        /// <summary>Builds every prop of the pack (old ones are dropped first).</summary>
        public static void Build(CharPack d, Material[] allMats, Transform girlRoot, Transform[] bones, Matrix4x4[] bindposes)
        {
            Clear();
            var byName = new Dictionary<string, List<int>>();
            var names = new List<string>();
            for (int s = 0; s < d.SubMeshes.Count; s++)
            {
                string n = NameOf(d.SubMeshes[s]);
                if (n == null) continue;
                if (!byName.ContainsKey(n)) { byName[n] = new List<int>(); names.Add(n); }
                byName[n].Add(s);
            }
            foreach (var n in names) list.Add(new Prop(n, d, byName[n], allMats, girlRoot, bones, bindposes));
            // task 40 bug 2 (task 41 contract): an alternative head ("head_nohat") starts hidden; the body's own hair submesh
            // is what it replaces while the hat is off
            body = girlRoot.GetComponent<SkinnedMeshRenderer>();
            hairSub = -1;
            hairIdx = null;
            hatless = false;
            if (Get(NoHat) != null)
            {
                Get(NoHat).Renderer.enabled = false;
                for (int s = 0; s < d.SubMeshes.Count; s++)
                {
                    var sm = d.SubMeshes[s];
                    if (IsProp(sm) || sm.Material == null || !sm.Material.EndsWith("_hair")) continue;
                    hairSub = s;
                    hairIdx = sm.Indices;
                    break;
                }
                Debug.Log("[UncleSwap] Props: " + NoHat + " hidden at start, swaps with body hair " + (hairSub >= 0 ? d.SubMeshes[hairSub].Material : "(none found)")
                    + ", " + Get(NoHat).ShapeMap.Length + " shapes copied");
            }
        }

        public const string NoHat = "head_nohat";
        static SkinnedMeshRenderer body;
        static int hairSub = -1;
        static int[] hairIdx;
        static bool hatless;

        /// <summary>Her hat is off (N6): hide the hat and the body hair, show head_nohat (casual hair: ears up, bow). False = back.
        /// Without a head_nohat prop only the hat is hidden/shown, as before.</summary>
        public static void SetHatless(bool on)
        {
            var hat = Get("hat");
            if (hat != null && hat.Renderer != null) hat.Renderer.enabled = !on;
            var nh = Get(NoHat);
            if (nh == null || nh.Renderer == null || on == hatless) return;
            hatless = on;
            nh.Renderer.enabled = on;
            if (body != null && body.sharedMesh != null && hairSub >= 0 && hairSub < body.sharedMesh.subMeshCount)
                body.sharedMesh.SetTriangles(on ? new int[0] : hairIdx, hairSub, false);
            Debug.Log("[UncleSwap] Props: head " + (on ? "hatless (" + NoHat + ")" : "with hat (body hair)"));
        }

        /// <summary>Every frame after the face: props with blend shapes (head_nohat's ears) take the body's weights.</summary>
        public static void SyncShapes(SkinnedMeshRenderer from)
        {
            if (from == null) return;
            foreach (var p in list)
            {
                if (p.ShapeMap.Length == 0 || p.Renderer == null || !p.Renderer.enabled) continue;
                for (int k = 0; k < p.ShapeMap.Length; k++) p.Renderer.SetBlendShapeWeight(k, from.GetBlendShapeWeight(p.ShapeMap[k]));
            }
        }

        public static void Clear()
        {
            hatless = false;
            body = null;
            hairSub = -1;
            foreach (var p in list) p.Destroy();
            list.Clear();
        }
    }

    /// <summary>One prop: its worn skinned renderer, the bones it is weighted to, its rest mesh data.</summary>
    public class Prop
    {
        public readonly string Name;
        /// <summary>Worn copy on her skeleton (enable/disable to show/hide it on her).</summary>
        public readonly SkinnedMeshRenderer Renderer;
        /// <summary>Her bones this prop is weighted to (e.g. the hat's own Sp_ bones and Head).</summary>
        public readonly Transform[] Bones;
        public readonly Material[] Materials;

        // rest shape (bind space) for static copies, e.g. the backpack on the seat
        public readonly Vector3[] RestPositions, RestNormals;
        public readonly Vector2[] UVs;
        public readonly List<int[]> Triangles;
        /// <summary>Prop blend shape k = body blend shape ShapeMap[k] (only the pack shapes that move this prop's vertices).</summary>
        public readonly int[] ShapeMap;

        public int Vertices { get { return RestPositions.Length; } }

        public Prop(string name, CharPack d, List<int> subs, Material[] allMats, Transform girlRoot, Transform[] bones, Matrix4x4[] bindposes)
        {
            Name = name;
            // compact vertex set of the prop
            var map = new Dictionary<int, int>();
            var src = new List<int>();
            Triangles = new List<int[]>();
            Materials = new Material[subs.Count];
            for (int k = 0; k < subs.Count; k++)
            {
                var idx = d.SubMeshes[subs[k]].Indices;
                var t = new int[idx.Length];
                for (int i = 0; i < idx.Length; i++)
                {
                    int n;
                    if (!map.TryGetValue(idx[i], out n)) { n = map[idx[i]] = src.Count; src.Add(idx[i]); }
                    t[i] = n;
                }
                Triangles.Add(t);
                Materials[k] = allMats[subs[k]];
            }
            int nv = src.Count;
            RestPositions = new Vector3[nv];
            RestNormals = new Vector3[nv];
            UVs = new Vector2[nv];
            var bw = new BoneWeight[nv];
            var used = new HashSet<int>();
            for (int i = 0; i < nv; i++)
            {
                RestPositions[i] = d.Positions[src[i]];
                RestNormals[i] = d.Normals[src[i]];
                UVs[i] = d.UVs[src[i]];
                var w = d.Weights[src[i]];
                bw[i] = w;
                if (w.weight0 > 0f) used.Add(w.boneIndex0);
                if (w.weight1 > 0f) used.Add(w.boneIndex1);
                if (w.weight2 > 0f) used.Add(w.boneIndex2);
                if (w.weight3 > 0f) used.Add(w.boneIndex3);
            }
            var ub = new List<Transform>();
            foreach (int bi in used) if (bi >= 0 && bi < bones.Length && bones[bi] != null) ub.Add(bones[bi]);
            Bones = ub.ToArray();

            // worn: skinned on her skeleton, same bind space as the body (child of the body's root, identity)
            string label = char.ToUpperInvariant(name[0]) + name.Substring(1);
            var wornMesh = new Mesh { name = label };
            wornMesh.vertices = RestPositions;
            wornMesh.normals = RestNormals;
            wornMesh.uv = UVs;
            wornMesh.boneWeights = bw;
            wornMesh.bindposes = bindposes;
            wornMesh.subMeshCount = Triangles.Count;
            for (int k = 0; k < Triangles.Count; k++) wornMesh.SetTriangles(Triangles[k], k);
            // the pack's shapes that move this prop (head_nohat: the Ear(...) shapes), same order as on the body
            var shapeList = new List<int>();
            for (int si = 0; si < d.Shapes.Count; si++)
            {
                var full = d.Shapes[si].Delta;
                if (full == null) continue;
                var delta = new Vector3[nv];
                bool any = false;
                for (int i = 0; i < nv; i++)
                {
                    int j = src[i];
                    if (j < full.Length) { delta[i] = full[j]; if (!any && full[j].sqrMagnitude > 1e-12f) any = true; }
                }
                if (!any) continue;
                wornMesh.AddBlendShapeFrame(d.Shapes[si].Name, 100f, delta, null, null);
                shapeList.Add(si);
            }
            ShapeMap = shapeList.ToArray();
            wornMesh.RecalculateBounds();
            var go = new GameObject(label);
            go.transform.SetParent(girlRoot, false);
            Renderer = go.AddComponent<SkinnedMeshRenderer>();
            Renderer.sharedMesh = wornMesh;
            Renderer.bones = bones;
            Renderer.rootBone = bones[0];
            Renderer.sharedMaterials = Materials;
            Renderer.updateWhenOffscreen = true;
            Renderer.quality = SkinQuality.Bone4;
        }

        public void Destroy()
        {
            if (Renderer != null) Object.Destroy(Renderer.gameObject);
        }
    }
}
