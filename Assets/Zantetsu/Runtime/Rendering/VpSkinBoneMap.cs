using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A model's complete bone correspondence, kept when its renderer's bone list was made shorter
    /// (<see cref="VpSkinBoneCompaction"/>): every bone the model's mesh listed before, by name and with its bind pose,
    /// in the order it had, and where each stands in the shorter list -- or that it is not in it. It is the model's:
    /// made once by the model's preparation, shared by every individual, never written afterwards. Which Transform of
    /// an individual a bone is, is that individual's own (<see cref="VpSkinBones"/>).
    /// <para>
    /// What resolves a bone by name and needs its bind pose -- the physics hulls -- reads this, so that it does not
    /// depend on which bones happen to carry a skin weight.
    /// </para>
    /// </summary>
    public sealed class VpSkinBoneMap : ScriptableObject
    {
        [SerializeField] private string[] boneNames = Array.Empty<string>();
        [SerializeField] private Matrix4x4[] bindPoses = Array.Empty<Matrix4x4>();
        [SerializeField] private int[] compactOf = Array.Empty<int>();
        [SerializeField] private int[] originalOf = Array.Empty<int>();

        /// <summary>Every bone the model's mesh listed before the list was shortened.</summary>
        public int BoneCount => boneNames.Length;

        /// <summary>The bones of the shortened list.</summary>
        public int CompactCount => originalOf.Length;

        public string BoneName(int original) => boneNames[original];

        /// <summary>The bind pose the model's mesh held for the bone, before any fixed-scale preparation.</summary>
        public Matrix4x4 BindPose(int original) => bindPoses[original];

        /// <summary>Where the bone stands in the shortened list; -1 when it is not in it.</summary>
        public int CompactOf(int original) => compactOf[original];

        /// <summary>Which of the model's bones the shortened list's entry is.</summary>
        public int OriginalOf(int compact) => originalOf[compact];

        /// <summary>
        /// The map of a mesh whose bones were <paramref name="names"/> with bind poses <paramref name="binds"/>, of
        /// which <paramref name="kept"/> (ascending indices) are the shortened list. The caller owns the object.
        /// </summary>
        public static VpSkinBoneMap Create(string[] names, Matrix4x4[] binds, int[] kept)
        {
            if (names == null || binds == null || kept == null) throw new ArgumentNullException();
            if (names.Length != binds.Length) throw new ArgumentException("a name and a bind pose a bone");
            var map = CreateInstance<VpSkinBoneMap>();
            map.boneNames = (string[])names.Clone();
            map.bindPoses = (Matrix4x4[])binds.Clone();
            map.originalOf = (int[])kept.Clone();
            map.compactOf = new int[names.Length];
            for (int i = 0; i < map.compactOf.Length; i++) map.compactOf[i] = -1;
            for (int c = 0; c < kept.Length; c++)
            {
                if ((uint)kept[c] >= (uint)names.Length || (c > 0 && kept[c] <= kept[c - 1]))
                {
                    throw new ArgumentException("the kept bones are not ascending indices of the model's bones", nameof(kept));
                }

                map.compactOf[kept[c]] = c;
            }

            return map;
        }

        /// <summary>
        /// Whether the map is whole and <paramref name="compactMesh"/> is the mesh it describes: as many bind poses as
        /// the shortened list, each the kept bone's own, bit for bit.
        /// </summary>
        public bool Describes(Mesh compactMesh, out string why)
        {
            why = null;
            if (boneNames.Length == 0 || bindPoses.Length != boneNames.Length || compactOf.Length != boneNames.Length || originalOf.Length == 0)
            {
                why = "the bone map is not whole";
                return false;
            }

            for (int c = 0; c < originalOf.Length; c++)
            {
                if ((uint)originalOf[c] >= (uint)boneNames.Length || compactOf[originalOf[c]] != c)
                {
                    why = "the bone map's two directions disagree at " + c;
                    return false;
                }
            }

            if (compactMesh == null)
            {
                why = "no mesh";
                return false;
            }

            Matrix4x4[] binds = compactMesh.bindposes;
            if (binds.Length != originalOf.Length)
            {
                why = "the mesh has " + binds.Length + " bind poses, the map's shortened list " + originalOf.Length;
                return false;
            }

            for (int c = 0; c < binds.Length; c++)
            {
                Matrix4x4 kept = bindPoses[originalOf[c]];
                for (int k = 0; k < 16; k++)
                {
                    if (BitConverter.SingleToInt32Bits(binds[c][k]) != BitConverter.SingleToInt32Bits(kept[k]))
                    {
                        why = "bind pose " + c + " of the mesh is not that of bone " + originalOf[c] + " (" + boneNames[originalOf[c]] + ")";
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
