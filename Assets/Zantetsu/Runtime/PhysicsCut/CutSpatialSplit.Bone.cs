#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Globalization;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    // NPC-BONE-SPLIT-1: the bone-aware split of an NPC's pieces. A fragment the world's NPC bone records know (its lineage began
    // at a prepared character's cut) is decided by its bones alone (NpcBoneSplitSearch), on the same background work, in place of
    // the plane, the bent partition, the graph and the mesh-led plane -- none of which runs for it, and none of which it falls back
    // to: a decision that is not Separable ends the attempt as a search that found nothing does (marked with the input). What the
    // decision places is published by the existing path, as the graph's placement is: distributed by its sides, classified by its
    // convexes' sides, the masses by the kernel, no plane. Nothing else's selection changes.
    // NPC-BONE-FALLBACK-1 (BoneFallback; on by default since 2026-11-04, NPC-BONE-FALLBACK-DEFAULT-1; -zantetsuNpcBoneFallbackOff turns it
    // off, the bones alone as before): a decision of NotSeparable,
    // and only that, goes on to the method as before on the same attempt (its stages prepared at the issue as for any owner, its
    // searches in the order its settings give); its ends and its publication are that method's own (an Unfinished stays Unfinished),
    // counted apart from the bones'. A publication of it gives each side its own part of the rig (review 1): cut by the plane where the
    // method split by one, otherwise the bones with a convex on the side -- never the bones' sides, and nothing taken off comes back.
    public sealed partial class CutSpatialSplit
    {
        /// <summary>Bone-aware decisions that ran, and how they ended.</summary>
        public int BoneSearches { get; private set; }
        public int BoneSeparable { get; private set; }
        public int BoneNotSeparable { get; private set; }
        public int BoneInsufficient { get; private set; }
        public int BoneOverTheLimits { get; private set; }

        /// <summary>Publications of a bone-aware placement.</summary>
        public int PublishedBone { get; private set; }

        /// <summary>The decisions' time on the worker (development builds; 0 otherwise).</summary>
        public double BoneSeconds { get; private set; }
        public double MaxBoneSeconds { get; private set; }

        /// <summary>The last decision's record (tests, the record).</summary>
        public NpcBoneSplitRecord LastBone { get; private set; }

        /// <summary>Attempts issued for an NPC's fragment (each decided by its bones first).</summary>
        public int BoneIssued { get; private set; }

        /// <summary>
        /// NPC-BONE-FALLBACK-1: an NPC's attempt the bones decide NotSeparable goes on to the method as before. Read at the issue. On by
        /// default since 2026-11-04 (NPC-BONE-FALLBACK-DEFAULT-1, PC and Quest alike); off (the bones alone) by
        /// <c>-zantetsuNpcBoneFallbackOff</c> (<see cref="CutWorldRoot.ReadNpcBoneFallback"/>).
        /// </summary>
        public bool BoneFallback { get; set; } = true;

        /// <summary>Attempts that fell back (the bones said NotSeparable, the method as before ran), and how they ended.</summary>
        public int BoneFellBack { get; private set; }

        private readonly int[] _fallbackEnds = new int[(int)SpatialSplitEnd.Count];

        public int FallbackEndedAs(SpatialSplitEnd end) => _fallbackEnds[(int)end];

        /// <summary>Publications of a fallback, by the route of the method as before that made them (never counted as the bones').</summary>
        public int PublishedBoneFallback { get; private set; }
        public int PublishedFallbackPlane { get; private set; }
        public int PublishedFallbackBent { get; private set; }
        public int PublishedFallbackGraph { get; private set; }
        public int PublishedFallbackCut { get; private set; }

        /// <summary>
        /// NPC-BONE-FALLBACK-1 review 1: what the records follow a fallback's split by. A plane (the plane search's, the mesh-led one's):
        /// each side's part of the rig cut by it, as a commit cuts. No plane (the bent partition, the graph): the bones with a convex on
        /// each side (the published shapes', which carry their convexes' bones).
        /// </summary>
        private void NoteFallbackSplit(LogicalFragmentId source, Attempt attempt, PhysicsOwnerShape positiveShape, PhysicsOwnerShape negativeShape)
        {
            NpcBoneSplitRegistry bones = _world.NpcBones;
            if (bones == null) return;
            if (!attempt.graphLed && !IsBentResult(in attempt.search))
            {
                bones.NoteSplitPlane(source, attempt.search.planeGeometry);
                return;
            }

            int n = attempt.boneInput.lineage.nodeCount;
            bones.NoteSplitConvexBones(source, ConvexBonesOf(positiveShape, n), ConvexBonesOf(negativeShape, n));
        }

        private static bool[] ConvexBonesOf(PhysicsOwnerShape shape, int n)
        {
            var has = new bool[Math.Max(0, n)];
            if (shape == null || !shape.HasConvexBones) return has;
            for (int c = 0; c < shape.ConvexCount; c++)
            {
                int bone = shape.ConvexBone(c);
                if ((uint)bone < (uint)n) has[bone] = true;
            }

            return has;
        }

        private void CountFallbackPublished(Attempt attempt)
        {
            PublishedBoneFallback++;
            if (attempt.graphLed) PublishedFallbackGraph++;
            else if (IsBentResult(in attempt.search)) PublishedFallbackBent++;
            else if (attempt.cut) PublishedFallbackCut++;
            else PublishedFallbackPlane++;
        }

        /// <summary>
        /// The NPC's part of an issue (the main thread): when <paramref name="fragment"/> is an NPC's, the attempt is made a
        /// bone-aware one -- its lineage held until the attempt is freed, the references the decision reads copied (they are
        /// never written after they are made), its convexes' bones read from the shape, and the sides' bytes taken. False for
        /// any other fragment, whose attempt goes on as before.
        /// </summary>
        private bool TakeBone(LogicalFragmentId fragment, Attempt attempt, PhysicsOwnerShape shape)
        {
            NpcBoneSplitRegistry bones = _world.NpcBones;
            if (bones == null || !bones.TryGet(fragment, out NpcBoneFragment record))
            {
                return false;
            }

            NpcBoneLineage lineage = record.Lineage;
            lineage.Hold();
            attempt.boneLineage = lineage;
            attempt.boneLed = true;
            int convexes = shape.ConvexCount;
            var convexBones = new int[convexes];
            for (int c = 0; c < convexes; c++) convexBones[c] = shape.ConvexBone(c);
            string insufficient = lineage.Insufficient;
            if (insufficient == null && attempt.anchors != null) insufficient = "the fragment has anchors";
            if (insufficient == null && !shape.HasConvexBones) insufficient = "the shape carries no convex bones";
            unsafe
            {
                attempt.boneInput = new NpcBoneSplitSearch.Input
                {
                    convexCount = convexes,
                    convexBones = convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = lineage.NodeCount, parent = lineage.Parent, present = lineage.Present,
                        baseBones = lineage.Vertices != null && !lineage.Vertices.IsReleased ? lineage.Vertices.Pointer : null,
                        baseStart = lineage.BaseStart, baseCount = lineage.BaseCount, chunks = lineage.Chunks, localCount = lineage.LocalCount,
                    },
                    jointKept = record.Piece.JointKept,
                    segmentLo = record.Piece.Lo,
                    segmentHi = record.Piece.Hi,
                    boneSides = new byte[2 * lineage.NodeCount],
                    insufficient = insufficient,
                    maxTriangles = attempt.limits.maxTriangles,
                    maxOriginSteps = NpcBoneSplitSearch.DefaultMaxOriginSteps,
                };
            }

            attempt.graphSides = PhysicsCutBlocks.Take<byte>(convexes + (attempt.indexCount / 3));
            attempt.bonePiece = record.Piece;
            BoneIssued++;
            return true;
        }

        private void CountBone(Attempt attempt)
        {
            if (!attempt.boneLed || !attempt.bone.Entered)
            {
                return;
            }

            LastBone = attempt.bone;
            BoneSearches++;
            switch (attempt.bone.verdict)
            {
                case NpcBoneSplitVerdict.Separable: BoneSeparable++; break;
                case NpcBoneSplitVerdict.NotSeparable: BoneNotSeparable++; break;
                case NpcBoneSplitVerdict.Insufficient: BoneInsufficient++; break;
                case NpcBoneSplitVerdict.OverTheLimits: BoneOverTheLimits++; break;
            }

            if (attempt.boneFellBack)
            {
                BoneFellBack++;
                _fallbackEnds[(int)LastEnd]++;   // End sets the end before it counts
            }

#if ZANTETSU_OBSERVE
            BoneSeconds += attempt.bone.seconds;
            MaxBoneSeconds = Math.Max(MaxBoneSeconds, attempt.bone.seconds);
            if (attempt.boneCaptured && Capture != null)
            {
                // The data set's live decision (NPC-BONE-EVAL-1): its record and the triangles' sides (the convexes' bytes are recoded by the
                // sharing out; their counts are in the record), for the comparison's check that its own run is of the same input.
                string sidesHash = null;
                unsafe
                {
                    if (attempt.bone.verdict == NpcBoneSplitVerdict.Separable && attempt.graphSides.IsCreated)
                    {
                        sidesHash = SpatialSplitReplay.SidesHash((byte*)attempt.graphSides.GetUnsafeReadOnlyPtr() + attempt.convexCount, attempt.indexCount / 3);
                    }
                }

                SpatialSplitBoneSample.WriteResult(Capture, CaptureWorld, attempt.serial, in attempt.bone, sidesHash, LastEnd.ToString());
            }
#endif
        }

        /// <summary>The bone-aware split's tallies, as a text of its own (the world's end, the city walk's summary).</summary>
        public string DescribeBone()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(384);
            text.Append("NPC bone split: issued ").Append(BoneIssued).Append(", decided ").Append(BoneSearches)
                .Append(" (apart ").Append(BoneSeparable).Append(", connected ").Append(BoneNotSeparable).Append(", not known ").Append(BoneInsufficient)
                .Append(", over the limits ").Append(BoneOverTheLimits).Append("), published ").Append(PublishedBone)
                .Append("; worker ms ");
#if ZANTETSU_OBSERVE
            text.Append((BoneSeconds * 1000.0).ToString("F3", inv)).Append(" (longest ").Append((MaxBoneSeconds * 1000.0).ToString("F3", inv)).Append(')');
#else
            text.Append("not measured (a Release build)");
#endif
            if (LastBone.Entered)
            {
                text.Append("; the last: ").Append(LastBone.verdict).Append(" (").Append(LastBone.reason).Append("), clusters ").Append(LastBone.clusters)
                    .Append(" of ").Append(LastBone.bones).Append(" bones, mesh components ").Append(LastBone.meshComponents).Append(", groups ").Append(LastBone.groups)
                    .Append(", bones held ").Append(LastBone.pieceBones).Append(", weights on bones no longer held ").Append(LastBone.removedWeights)
                    .Append(" (").Append(LastBone.fullGroups).Append(" with a convex and triangles)");
            }

            text.Append("; fallback to the method as before (NPC-BONE-FALLBACK-1) ").Append(BoneFallback ? "on" : "off").Append(": fell back ").Append(BoneFellBack)
                .Append(", published ").Append(PublishedBoneFallback).Append(" (plane ").Append(PublishedFallbackPlane).Append(", bent ").Append(PublishedFallbackBent)
                .Append(", graph ").Append(PublishedFallbackGraph).Append(", mesh-led with convexes cut ").Append(PublishedFallbackCut).Append("), ended");
            for (int e = 0; e < (int)SpatialSplitEnd.Count; e++)
            {
                if (_fallbackEnds[e] > 0) text.Append(' ').Append((SpatialSplitEnd)e).Append(' ').Append(_fallbackEnds[e]);
            }

            if (_world.NpcBones != null) text.Append("; ").Append(_world.NpcBones.Describe());
            return text.ToString();
        }

        internal sealed unsafe partial class Attempt
        {
            // The bone-aware decision (NPC-BONE-SPLIT-1): whether this attempt is an NPC fragment's, its lineage (held), what the
            // decision reads (copied at the issue) and its record.
            internal bool boneLed;
            internal NpcBoneLineage boneLineage;
            internal NpcBoneSplitSearch.Input boneInput;
            internal NpcBoneSplitRecord bone;
            // NPC-BONE-ACTIVE-2: the fragment's part of the rig at the issue (the capture's sidecar writes it).
            internal NpcBonePiece bonePiece;
            // NPC-BONE-EVAL-1: the input went to the capture with its bones (the live decision is written beside it where it is counted).
            internal bool boneCaptured;
            // NPC-BONE-FALLBACK-1: the fallback asked for at the issue (its stages prepared), and -- the worker's -- that it ran.
            internal bool boneFallback, boneFellBack;

            /// <summary>The bones decide this attempt's end and placement: an NPC's attempt that did not fall back (read after the worker).</summary>
            internal bool BoneRoute => boneLed && !boneFellBack;

            /// <summary>The decision over the attempt's own indices, the sides into its sides' bytes (the worker).</summary>
            private void RunBone()
            {
                long boneBegin = Began();
                NpcBoneSplitSearch.Input given = boneInput;
                given.indices = indices;
                given.indexCount = indexCount;
                var record = default(NpcBoneSplitRecord);
                NpcBoneSplitSearch.Decide(in given, (byte*)graphSides.GetUnsafePtr(), graphSides.IsCreated ? graphSides.Length : 0, ref record);
#if ZANTETSU_OBSERVE
                record.seconds = Since(boneBegin);
#endif
                bone = record;
            }

            /// <summary>The lineage's hold back, once (with the attempt's other parts).</summary>
            private void FreeBone()
            {
                boneLineage?.Release();
                boneLineage = null;
            }
        }
    }
}
