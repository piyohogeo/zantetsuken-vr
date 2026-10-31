using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The vertex of a skinned model as the skin draw and the CPU bake read it (DESIGN 4.5.12): the bind position, the
    /// attributes of <see cref="VpRenderVertex"/> (oct8x2 normal, uint8x2 UV) and four bone influences -- the bone's
    /// number in the model's palette, and its weight as an integer of 255. 24 bytes, one layout for the CPU, the
    /// Direct3D 12 and the Vulkan shaders (VpSkinVertex.hlsl reads it as six 32-bit words, so that no float3 alignment
    /// rule of a target widens the stride).
    /// <para>
    /// The weights are made once, by <see cref="VpSkinWeights.TryQuantize"/>, and both the GPU skinning and the CPU
    /// bake read these very integers: the two never see different weights. A bone of weight zero contributes
    /// nothing, whatever number it names.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpSkinVertex
    {
        public const int Stride = 24;

        /// <summary>Weights are integers of this sum.</summary>
        public const int WeightSum = 255;

        /// <summary>The most bones a model's palette may hold: a bone's number is one byte.</summary>
        public const int MaxBones = 256;

        public Vector3 position;
        public sbyte normalX, normalY;
        public byte u, v;
        public byte bone0, bone1, bone2, bone3;
        public byte weight0, weight1, weight2, weight3;

        /// <summary>The 16-byte attributes of this vertex at the bind pose: the same normal and UV bytes.</summary>
        public VpRenderVertex BindAttributes
        {
            get
            {
#if VP_DIAGNOSTIC_LEGACY32
                // The comparison build's 32-byte vertex holds floats, decoded from the same bytes (the product refuses that stride).
                float2 e = new float2(normalX, normalY) / 127f;
                float3 n = new float3(e, 1f - math.abs(e.x) - math.abs(e.y));
                if (n.z < 0f) n.xy = (1f - math.abs(n.yx)) * math.select(new float2(-1f), new float2(1f), e >= 0f);
                return new VpRenderVertex { position = position, normal = math.normalize(n), uv0 = new Vector2((u + .5f) / 256f, (v + .5f) / 256f) };
#else
                return new VpRenderVertex { position = position, normalX = normalX, normalY = normalY, u = u, v = v };
#endif
            }
        }

        public byte Bone(int influence) => influence == 0 ? bone0 : influence == 1 ? bone1 : influence == 2 ? bone2 : bone3;

        public byte Weight(int influence) => influence == 0 ? weight0 : influence == 1 ? weight1 : influence == 2 ? weight2 : weight3;

        public int WeightTotal => weight0 + weight1 + weight2 + weight3;
    }

    /// <summary>How the four float weights of a vertex become the four integers of 255 the skin vertex carries.</summary>
    public static class VpSkinWeights
    {
        /// <summary>How far from one the four weights may sum, as the cold input gate of the direct skin allows.</summary>
        public const float SumTolerance = 1e-5f;

        /// <summary>
        /// The largest-remainder quantization: each weight's floor of its share of 255, and the remainder to the
        /// weights of the largest fractional parts, the lowest influence first among equals. Deterministic: the same
        /// four floats give the same four bytes, so two vertices that share a position and weights at a seam are
        /// skinned by the same arithmetic. False for a weight that is not finite or negative, or a sum not within
        /// <see cref="SumTolerance"/> of one (nothing is normalised silently).
        /// </summary>
        public static bool TryQuantize(float w0, float w1, float w2, float w3, out byte q0, out byte q1, out byte q2, out byte q3)
        {
            q0 = q1 = q2 = q3 = 0;
            if (!float.IsFinite(w0) || !float.IsFinite(w1) || !float.IsFinite(w2) || !float.IsFinite(w3) || w0 < 0f || w1 < 0f || w2 < 0f || w3 < 0f)
            {
                return false;
            }

            float sum = w0 + w1 + w2 + w3;
            if (!float.IsFinite(sum) || math.abs(sum - 1f) > SumTolerance)
            {
                return false;
            }

            // The shares of 255 in double, so that the floors and the fractions are exact enough to order.
            double scale = VpSkinVertex.WeightSum / (double)sum;
            double s0 = w0 * scale, s1 = w1 * scale, s2 = w2 * scale, s3 = w3 * scale;
            int b0 = (int)math.floor(s0), b1 = (int)math.floor(s1), b2 = (int)math.floor(s2), b3 = (int)math.floor(s3);
            double f0 = s0 - b0, f1 = s1 - b1, f2 = s2 - b2, f3 = s3 - b3;
            int remainder = VpSkinVertex.WeightSum - (b0 + b1 + b2 + b3);
            // At most four units are left (each floor dropped less than one). Given one at a time to the largest
            // fraction standing, the lowest influence among equals; a weight of exactly zero has no fraction and takes
            // nothing while any other fraction stands.
            for (int given = 0; given < remainder; given++)
            {
                int best = -1;
                double bestFraction = -1.0;
                if (f0 > bestFraction) { best = 0; bestFraction = f0; }
                if (f1 > bestFraction) { best = 1; bestFraction = f1; }
                if (f2 > bestFraction) { best = 2; bestFraction = f2; }
                if (f3 > bestFraction) { best = 3; bestFraction = f3; }
                switch (best)
                {
                    case 0: b0++; f0 = -2.0; break;
                    case 1: b1++; f1 = -2.0; break;
                    case 2: b2++; f2 = -2.0; break;
                    default: b3++; f3 = -2.0; break;
                }
            }

            if (b0 + b1 + b2 + b3 != VpSkinVertex.WeightSum || b0 > 255 || b1 > 255 || b2 > 255 || b3 > 255)
            {
                return false;
            }

            q0 = (byte)b0; q1 = (byte)b1; q2 = (byte)b2; q3 = (byte)b3;
            return true;
        }

        /// <summary>The weight a byte stands for.</summary>
        public static float Of(byte quantized) => quantized / (float)VpSkinVertex.WeightSum;
    }

    /// <summary>
    /// The box of one bone of a model: every vertex whose source weight on that bone is above zero, at the bind pose,
    /// in the model's own frame (DESIGN 4.5.12). A bone no vertex is weighted to has extents below zero and is in no
    /// union. 32 bytes, read by VpSkinBounds.compute.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpSkinBoneBox
    {
        public const int Stride = 32;

        public Vector3 centre;
        public float pad0;
        public Vector3 extents;
        public float pad1;

        public bool IsEmpty => extents.x < 0f;

        public static VpSkinBoneBox Empty => new VpSkinBoneBox { extents = new Vector3(-1f, -1f, -1f) };
    }

    /// <summary>
    /// One drawn individual of the skin draw, as its shaders read it (DESIGN 4.5.12): where its current and its
    /// previous palette begin in the palette buffer, how many bones the palette holds, where its model's bone boxes
    /// begin, and flags. 32 bytes, one a logical instance slot of the batch.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpSkinRecord
    {
        public const int Stride = 32;

        /// <summary>The slot draws: a record without it is of no individual, and its bounds are empty.</summary>
        public const uint ActiveFlag = 1u;

        public uint paletteStart;
        public uint previousPaletteStart;
        public uint boneCount;
        public uint boneBoxStart;
        public uint flags;
        public uint pad0, pad1, pad2;
    }

    /// <summary>
    /// The bounds of one instance in its own frame, as the GPU selection reads them where the batch's instances carry
    /// bounds of their own rather than their command's (<see cref="VpGpuCullSetup.InstanceBounds"/>): written by
    /// VpSkinBounds.compute from the instance's palette and its model's bone boxes. Extents below zero: nothing, kept
    /// by no selection. 32 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpInstanceBounds
    {
        public const int Stride = 32;

        public Vector3 centre;
        public float pad0;
        public Vector3 extents;
        public float pad1;

        public bool IsEmpty => extents.x < 0f;

        public Bounds ToBounds() => new Bounds(centre, extents * 2f);
    }
}
