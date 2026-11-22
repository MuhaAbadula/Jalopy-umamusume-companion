using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UmamusumeCompanion
{
    /// <summary>
    /// char.bin v3/v4 as written by blender/export_char.py. Everything rig-specific lives here, so the mod
    /// works with any character that has a profile. Matrices/points are in the uncle's Unity bind space.
    /// </summary>
    public class CharPack
    {
        public class Bone { public string Name; public int Parent; public Matrix4x4 Rest; }
        public class Hand { public string UncleHand, UncleKnuckle, Wrist, Knuckle; }
        public class Partial { public string Bone, Base, Full; public float Weight; }
        public class SubMesh { public string Material, Texture, Shader; public int[] Indices; }
        public class Shape { public string Name, Role; public Vector3[] Delta; }
        public class Collider { public string Name, Bone; public Vector3 A, B; }
        public class Leg { public string Bone; public Vector3 Seg; }
        public class ChainBone { public string Name; public Vector3 Seg; }
        public class Chain
        {
            public float Stiff, Drag, Grav, Bend;
            public List<ChainBone> Bones = new List<ChainBone>();
            public List<KeyValuePair<int, float>> Colliders = new List<KeyValuePair<int, float>>();
            public float[] LegWeights;
            public bool Kinematic;      // no simulation: rest shape + leg follow only
        }

        public float Scale;
        public List<Bone> Bones = new List<Bone>();
        public List<KeyValuePair<string, string>> Map = new List<KeyValuePair<string, string>>();
        public List<string> ArmPrefixes = new List<string>();
        public List<Hand> Hands = new List<Hand>();
        public List<Partial> Partials = new List<Partial>();
        public Vector3[] Positions, Normals;
        public Vector2[] UVs;
        public BoneWeight[] Weights;
        public List<SubMesh> SubMeshes = new List<SubMesh>();
        public List<Shape> Shapes = new List<Shape>();
        public float ParticleRadius, SubstepHz;
        public int Iterations;
        public List<Collider> Colliders = new List<Collider>();
        public List<Leg> Legs = new List<Leg>();
        public List<Chain> Chains = new List<Chain>();

        public static CharPack Load(string path)
        {
            var d = new CharPack();
            using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
            {
                if (new string(r.ReadChars(4)) != "FUKU") throw new IOException("not a char.bin");
                int ver = r.ReadInt32();
                if (ver != 3 && ver != 4) throw new IOException("char.bin version " + ver + ", expected 3 or 4 (re-export with blender/export_char.py)");
                d.Scale = r.ReadSingle();

                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var b = new Bone { Name = r.ReadString(), Parent = r.ReadInt32() };
                    var m = new Matrix4x4();
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++)
                            m[row, col] = r.ReadSingle();
                    b.Rest = m;
                    d.Bones.Add(b);
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) d.Map.Add(new KeyValuePair<string, string>(r.ReadString(), r.ReadString()));
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) d.ArmPrefixes.Add(r.ReadString());
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                    d.Hands.Add(new Hand { UncleHand = r.ReadString(), UncleKnuckle = r.ReadString(), Wrist = r.ReadString(), Knuckle = r.ReadString() });
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                    d.Partials.Add(new Partial { Bone = r.ReadString(), Base = r.ReadString(), Full = r.ReadString(), Weight = r.ReadSingle() });

                int nv = r.ReadInt32();
                d.Positions = V3(r, nv);
                d.Normals = V3(r, nv);
                d.UVs = new Vector2[nv];
                for (int i = 0; i < nv; i++) d.UVs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                var idx = new int[nv * 4];
                for (int i = 0; i < idx.Length; i++) idx[i] = r.ReadInt32();
                d.Weights = new BoneWeight[nv];
                for (int i = 0; i < nv; i++)
                    d.Weights[i] = new BoneWeight
                    {
                        boneIndex0 = idx[i * 4], boneIndex1 = idx[i * 4 + 1], boneIndex2 = idx[i * 4 + 2], boneIndex3 = idx[i * 4 + 3],
                        weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle(),
                    };

                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var s = new SubMesh { Material = r.ReadString(), Texture = r.ReadString(), Shader = r.ReadString() };
                    int c = r.ReadInt32();
                    s.Indices = new int[c];
                    for (int k = 0; k < c; k++) s.Indices[k] = r.ReadInt32();
                    d.SubMeshes.Add(s);
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var sh = new Shape { Name = r.ReadString(), Role = r.ReadString() };
                    if (ver >= 4)
                    {
                        // sparse: count, indices, deltas
                        int c = r.ReadInt32();
                        var ix = new int[c];
                        for (int k = 0; k < c; k++) ix[k] = r.ReadInt32();
                        sh.Delta = new Vector3[nv];
                        for (int k = 0; k < c; k++) sh.Delta[ix[k]] = V3(r);
                    }
                    else sh.Delta = V3(r, nv);
                    d.Shapes.Add(sh);
                }

                d.ParticleRadius = r.ReadSingle();
                d.SubstepHz = r.ReadSingle();
                d.Iterations = r.ReadInt32();
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                    d.Colliders.Add(new Collider { Name = r.ReadString(), Bone = r.ReadString(), A = V3(r), B = V3(r) });
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) d.Legs.Add(new Leg { Bone = r.ReadString(), Seg = V3(r) });
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    var ch = new Chain { Stiff = r.ReadSingle(), Drag = r.ReadSingle(), Grav = r.ReadSingle(), Bend = r.ReadSingle() };
                    int nb = r.ReadInt32();
                    for (int k = 0; k < nb; k++) ch.Bones.Add(new ChainBone { Name = r.ReadString(), Seg = V3(r) });
                    int nc = r.ReadInt32();
                    for (int k = 0; k < nc; k++) ch.Colliders.Add(new KeyValuePair<int, float>(r.ReadInt32(), r.ReadSingle()));
                    int nl = r.ReadInt32();
                    ch.LegWeights = new float[nl];
                    for (int k = 0; k < nl; k++) ch.LegWeights[k] = r.ReadSingle();
                    ch.Kinematic = r.ReadInt32() != 0;
                    d.Chains.Add(ch);
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new IOException("char.bin has trailing data");
            }
            return d;
        }

        static Vector3 V3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        static Vector3[] V3(BinaryReader r, int n)
        {
            var a = new Vector3[n];
            for (int i = 0; i < n; i++) a[i] = V3(r);
            return a;
        }
    }
}
