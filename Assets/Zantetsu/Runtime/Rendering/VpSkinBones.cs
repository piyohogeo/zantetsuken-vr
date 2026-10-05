using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One individual's bones, all of them: the Transform of this individual that each bone of its model's
    /// <see cref="VpSkinBoneMap"/> is, in the map's order. It sits beside the individual's SkinnedMeshRenderer, whose
    /// own bone list is the map's shortened one. The Transforms are the individual's own -- nothing here is shared --
    /// and none of them, nor any ancestor, is removed by shortening the renderer's list.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VpSkinBones : MonoBehaviour
    {
        [SerializeField] private VpSkinBoneMap map;
        [SerializeField] private Transform[] bones = Array.Empty<Transform>();

        public VpSkinBoneMap Map => map;
        public int BoneCount => bones.Length;

        /// <summary>This individual's Transform for the model's bone; null when the individual has none there.</summary>
        public Transform BoneAt(int original) => bones[original];

        /// <summary>The first bone of that name, by the model's order; -1 when none is named so.</summary>
        public int Find(string boneName)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] != null && bones[i].name == boneName) return i;
            }

            return -1;
        }

        /// <summary>Set by the model's preparation and by the scene's build: the model's map and this individual's Transforms in its order.</summary>
        public void Set(VpSkinBoneMap boneMap, Transform[] allBones)
        {
            map = boneMap;
            bones = allBones != null ? (Transform[])allBones.Clone() : Array.Empty<Transform>();
        }

        /// <summary>
        /// Whether these bones, the map and <paramref name="renderer"/> are of one correspondence: the map describes
        /// the renderer's mesh, there is a Transform for every bone of the map, each named as the map names it, and
        /// the renderer's own list is the shortened one -- its entry c is this individual's bone
        /// <c>map.OriginalOf(c)</c>.
        /// </summary>
        public bool IsConsistentWith(SkinnedMeshRenderer renderer, out string why)
        {
            why = null;
            if (map == null)
            {
                why = "no bone map";
                return false;
            }

            if (renderer == null || !map.Describes(renderer.sharedMesh, out why))
            {
                why ??= "no renderer";
                return false;
            }

            if (bones.Length != map.BoneCount)
            {
                why = bones.Length + " bones for the map's " + map.BoneCount;
                return false;
            }

            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || bones[i].name != map.BoneName(i))
                {
                    why = "bone " + i + " is " + (bones[i] != null ? bones[i].name : "missing") + ", the map's " + map.BoneName(i);
                    return false;
                }
            }

            Transform[] rendered = renderer.bones;
            if (rendered.Length != map.CompactCount)
            {
                why = "the renderer lists " + rendered.Length + " bones, the map's shortened list " + map.CompactCount;
                return false;
            }

            for (int c = 0; c < rendered.Length; c++)
            {
                if (rendered[c] != bones[map.OriginalOf(c)])
                {
                    why = "the renderer's bone " + c + " is not this individual's bone " + map.OriginalOf(c) + " (" + map.BoneName(map.OriginalOf(c)) + ")";
                    return false;
                }
            }

            return true;
        }
    }
}
