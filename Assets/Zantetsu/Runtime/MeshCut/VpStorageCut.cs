using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>How one cut of a stored geometry ended.</summary>
    public enum VpStorageCutStatus
    {
        Ok = 0,

        /// <summary>The adapter, the geometry or the kernel refused the input; nothing was reserved or published.</summary>
        InvalidInput = 1,

        /// <summary>The storage could not hold the output: index space, a descriptor, vertices or metadata room.</summary>
        StorageCapacity = 2,

        /// <summary>
        /// The run used up its attempts while the reservations were still growing. Nothing was published and nothing is
        /// held; this is not a failed cut but an unfinished one. <see cref="VpStorageCutResult.required"/> asks for no
        /// less than was already tried and more of whatever came up short, and calling again with it carries on.
        /// </summary>
        CapacityRetry = 3,

        /// <summary>
        /// The kernel asked for no more than it had been given, so running again would ask the same question. Something
        /// is wrong with the requirement itself; repeating cannot help.
        /// </summary>
        CapacityStalled = 4,

        /// <summary>A requirement came back too large to express, so no reservation could satisfy it.</summary>
        CapacityOverflow = 5,

        /// <summary>The kernel's own construction invariant failed. No output; report the input and plane.</summary>
        InternalError = 6,
    }

    /// <summary>What one side of a cut turned out to be.</summary>
    public enum VpStorageCutSideKind
    {
        /// <summary>No triangle of the geometry is on this side; no geometry, no index range, no descriptor.</summary>
        Empty = 0,

        /// <summary>A new geometry the storage owns, with its own index range to retire when it is no longer wanted.</summary>
        Produced = 1,

        /// <summary>
        /// The plane did not cut: this side is the input geometry itself, borrowed from whoever already owns it. It is
        /// not a new owner and must not be retired on the strength of this result.
        /// </summary>
        ReusesInput = 2,
    }

    /// <summary>One side of a cut: what it is and, when there is one, the geometry that stands for it.</summary>
    public readonly struct VpStorageCutSide
    {
        public readonly VpStorageCutSideKind kind;
        public readonly VpStoredGeometry geometry;

        internal VpStorageCutSide(VpStorageCutSideKind kind, VpStoredGeometry geometry)
        {
            this.kind = kind;
            this.geometry = geometry;
        }

        /// <summary>A geometry this cut created and the caller now owns.</summary>
        public bool IsProduced => kind == VpStorageCutSideKind.Produced;

        /// <summary>The input geometry, lent back unchanged; its owner is unchanged too.</summary>
        public bool IsBorrowed => kind == VpStorageCutSideKind.ReusesInput;

        public bool IsEmpty => kind == VpStorageCutSideKind.Empty;
    }

    /// <summary>
    /// What the caller brings to one cut, all of it optional. The three reservation sizes are the first attempt's; a
    /// figure that is not positive is taken from the kernel's capacity query instead, and one that turns out to be too
    /// small is not an error, only a re-run. The two node arrays, when both are given, ask the kernel for the node
    /// correspondence of the cut - the topology edge and parameter of each intersection node - written into arrays the
    /// caller owns and keeps. Leaving them out is the ordinary case: the cut then produces no such record and costs
    /// nothing for it.
    /// </summary>
    public struct VpStorageCutOptions
    {
        public int newVertexCapacity;
        public int newIndexCapacity;
        public int scratchBytes;

        /// <summary>
        /// How many kernel runs this one call may spend growing its reservations, <see cref="VpStorageCut.MaxAttempts"/>
        /// when it is not positive. Spending fewer is not giving up: the call then reports what to reserve next and
        /// another call carries on, so a caller that would rather come back than keep working can say so.
        /// </summary>
        public int maxAttempts;

        /// <summary>Optional, caller-owned: the topology edge of each intersection node, packed as (lo &lt;&lt; 32 | hi).</summary>
        public NativeArray<long> nodeEdgeKeys;

        /// <summary>Optional, caller-owned: each intersection node's parameter from its edge's lo endpoint.</summary>
        public NativeArray<float> nodeParams;
    }

    /// <summary>The outcome of one cut, with the kernel's own figures for diagnostics.</summary>
    public struct VpStorageCutResult
    {
        public VpStorageCutStatus status;
        public VpStorageCutSide positive, negative;

        /// <summary>The kernel result of the attempt that ended the run.</summary>
        public MeshCutResult kernel;

        /// <summary>How many kernel runs it took, counting re-runs after a reservation turned out to be too small.</summary>
        public int attempts;

        /// <summary>
        /// On <see cref="VpStorageCutStatus.CapacityRetry"/>, the options to call again with: what the caller brought,
        /// its node arrays and pacing included, with the reservation figures raised — none of them below what was
        /// already tried, and the one that came up short larger than before. The node arrays are the caller's still;
        /// carrying them here borrows the reference and takes nothing over. Empty on every other status.
        /// </summary>
        public VpStorageCutOptions required;
    }

    /// <summary>
    /// Runs the cut kernel over a geometry a <see cref="VpCpuGeometryStorage"/> owns and places the result in that same
    /// storage, synchronously and on the main thread (DESIGN 4.5.6 / 6.1). The kernel writes its new vertices, their
    /// topology ids and both sides' indices straight into the storage's reserved space, so nothing is copied in
    /// afterwards and no intermediate buffer exists.
    /// <para>
    /// The order is fixed: the input adapter holds the read lease, the kernel's capacity query gives the reservation
    /// figures, the storage sets aside the vertices, mapping entries, one index run and the metadata room, the kernel
    /// runs against that space, and only a successful run has its used part published as the two sides' geometries. A
    /// reservation that turns out to be too small publishes nothing: the index reservation goes back, the uncommitted
    /// vertices and metadata are abandoned, and the run is repeated against what the kernel said it needs, up to
    /// <see cref="MaxAttempts"/> times in one call. Reaching that limit is not a failed cut: the run reports
    /// <see cref="VpStorageCutStatus.CapacityRetry"/> with the figures to reserve next, and a further call with them
    /// carries on, so a geometry whose cut needs several rounds is never given up on. The input lease is held
    /// throughout and released by the caller, who owns the adapter; the scratch and the output range array live only
    /// for the call, and the storage keeps one cut output reservation open at a time.
    /// </para>
    /// <para>
    /// Both sides are handed back together, after both have been published. The indices of one run are one contiguous
    /// reservation, the positive side first and the negative side after it, and the two are published as separate
    /// ranges with their own descriptors, so either side can be retired and its space reused without touching the
    /// other. The sides share the vertices the cut appended, their mapping entries and the block list that names the
    /// parent's vertices as well, so neither copies the parent and both survive the parent's retirement. A side with no
    /// triangle gets no geometry and no index range, and when the plane misses the geometry the side that keeps it all
    /// borrows the input geometry instead of becoming a second owner of it.
    /// </para>
    /// </summary>
    public static unsafe class VpStorageCut
    {
        /// <summary>Kernel runs one cut may take: the first, and the re-runs after a reservation proved too small.</summary>
        public const int MaxAttempts = 3;

        /// <summary>
        /// Cuts <paramref name="input"/>'s geometry by <paramref name="plane"/> and stores the result in
        /// <paramref name="storage"/>. Returns true only when <paramref name="result"/> carries
        /// <see cref="VpStorageCutStatus.Ok"/>, in which case each side is a produced geometry, the borrowed input or
        /// empty. On every other status nothing is published and nothing is committed: the storage is exactly as it was
        /// except for the index descriptor generations a cancelled reservation used and the bytes written into space
        /// that is free again.
        /// </summary>
        public static bool TryExecute(
            VpCpuGeometryStorage storage,
            VpStorageCutInput input,
            float4 plane,
            out VpStorageCutResult result)
        {
            return TryExecute(storage, input, plane, default, out result);
        }

        /// <summary>
        /// The same cut, with what <paramref name="options"/> brings: the first attempt's reservation sizes wherever it
        /// names a positive one, so a caller that already knows what a cut of this shape costs can skip a round, and the
        /// arrays for the node correspondence when it wants that record. A reservation figure that turns out to be too
        /// small is not an error: the attempt publishes nothing and the run repeats against what the kernel says it
        /// needs, exactly as when the capacity query is used.
        /// </summary>
        public static bool TryExecute(
            VpCpuGeometryStorage storage,
            VpStorageCutInput input,
            float4 plane,
            in VpStorageCutOptions options,
            out VpStorageCutResult result)
        {
            result = default;
            result.status = VpStorageCutStatus.InvalidInput;
            if (storage == null
                || input == null
                || input.IsDisposed
                || !input.TryGetInput(plane, out MeshCutInput kernelInput)
                || !storage.TryGetSubmeshes(input.Geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly parentSubmeshes))
            {
                return false;
            }

            int rangeCount = input.RangeCount;
            if (rangeCount <= 0 || parentSubmeshes.Length != rangeCount)
            {
                return false;
            }

            var capacity = new MeshCutCapacity();
            MeshCutKernel.QueryCapacity(in kernelInput, ref capacity);
            if (capacity.invalidInput != 0)
            {
                return false;
            }

            VpStoredGeometry parent = input.Geometry;
            int newVertexCapacity = options.newVertexCapacity > 0 ? options.newVertexCapacity : capacity.newVertices;
            int newIndexCapacity = options.newIndexCapacity > 0 ? options.newIndexCapacity : capacity.newIndices;
            int scratchBytes = math.max(1, options.scratchBytes > 0 ? options.scratchBytes : capacity.scratchBytes);
            bool wantsNodes = options.nodeEdgeKeys.IsCreated && options.nodeParams.IsCreated;
            int attemptLimit = options.maxAttempts > 0 ? options.maxAttempts : MaxAttempts;

            // The query already knows when no triangle crosses the plane: that run reserves no output at all, so a cut
            // that changes nothing leaves no trace. A later disagreement is simply a capacity failure and re-runs.
            bool reservesNothing = capacity.wholeMeshSide != 0;

            var outputRanges = new NativeArray<MeshCutIndexRange>(2 * rangeCount, Allocator.Persistent);
            try
            {
                // Inside the try, so that a failure to take it still gives the range array back.
                var submeshes = new VpGeometrySubmesh[2 * rangeCount];
                for (int attempt = 1; attempt <= attemptLimit; attempt++)
                {
                    result.attempts = attempt;
                    var scratch = new NativeArray<byte>(scratchBytes, Allocator.Persistent);
                    VpCutOutputReservation reservation = null;
                    try
                    {
                        if (!reservesNothing
                            && !storage.TryReserveCutOutput(
                                parent,
                                newVertexCapacity,
                                newIndexCapacity,
                                2 * rangeCount,
                                parent.blockCount + 1,
                                out reservation))
                        {
                            result.status = VpStorageCutStatus.StorageCapacity;
                            return false;
                        }

                        var output = new MeshCutOutput
                        {
                            outputRanges = (MeshCutIndexRange*)outputRanges.GetUnsafePtr(),
                            scratch = (byte*)scratch.GetUnsafePtr(),
                            scratchBytes = scratch.Length,
                        };
                        if (wantsNodes)
                        {
                            // An optional record for a caller that asked for it, into that caller's own arrays.
                            output.nodeEdgeKeys = (long*)options.nodeEdgeKeys.GetUnsafePtr();
                            output.nodeParams = (float*)options.nodeParams.GetUnsafePtr();
                            output.nodeCapacity = math.min(options.nodeEdgeKeys.Length, options.nodeParams.Length);
                        }

                        if (reservation != null)
                        {
                            output.newVertices = (VpRenderVertex*)reservation.NewVertices.GetUnsafePtr();
                            output.newVertexBase = reservation.NewVertexBase;
                            output.newVertexCapacity = reservation.NewVertexCapacity;
                            output.newVertexTopology = (int*)reservation.NewVertexTopology.GetUnsafePtr();
                            output.newIndices = (uint*)reservation.NewIndices.GetUnsafePtr();

                            // The base of DESIGN 4.5.6 is where the reservation sits in the storage's index buffer, so
                            // the produced ranges come back as the positions the two sides are really published at.
                            output.newIndexBase = (uint)reservation.IndexStart;
                            output.newIndexCapacity = reservation.NewIndexCapacity;
                        }

                        var kernel = new MeshCutResult();
                        MeshCutKernel.Execute(in kernelInput, in output, ref kernel);
                        result.kernel = kernel;
                        switch (kernel.status)
                        {
                            case MeshCutStatus.Ok:
                                return TryFinish(storage, parent, reservation, in kernel, outputRanges, submeshes, parentSubmeshes, rangeCount, ref result);

                            case MeshCutStatus.CapacityVertex:
                            case MeshCutStatus.CapacityIndex:
                                reservesNothing = false;
                                bool vertexFell = kernel.status == MeshCutStatus.CapacityVertex;
                                if (!TryAdvance(
                                        vertexFell ? kernel.requiredVertexCapacity : kernel.requiredIndexCapacity,
                                        vertexFell ? newVertexCapacity : newIndexCapacity,
                                        ref result))
                                {
                                    return false;
                                }

                                newVertexCapacity = math.max(newVertexCapacity, kernel.requiredVertexCapacity);
                                newIndexCapacity = math.max(newIndexCapacity, kernel.requiredIndexCapacity);
                                break;

                            case MeshCutStatus.CapacityScratch:
                                reservesNothing = false;
                                if (!TryAdvance(kernel.recommendedScratchBytes, scratchBytes, ref result))
                                {
                                    return false;
                                }

                                scratchBytes = kernel.recommendedScratchBytes;
                                break;

                            case MeshCutStatus.InternalError:
                                result.status = VpStorageCutStatus.InternalError;
                                return false;

                            default:
                                result.status = VpStorageCutStatus.InvalidInput;
                                return false;
                        }
                    }
                    finally
                    {
                        // Whatever happened, an open reservation goes back before the next attempt or the return.
                        if (reservation != null && !reservation.IsClosed)
                        {
                            storage.TryCancelCutOutput(reservation);
                        }

                        scratch.Dispose();
                    }
                }

                // The attempt limit, which is a pause and not a verdict: nothing was published and nothing is held. What
                // the caller brought is carried over as it stands — the pacing, and the node arrays it still owns and
                // still wants written — with only the three reservation figures raised, so another call with this
                // carries on exactly as the caller meant it.
                result.status = VpStorageCutStatus.CapacityRetry;
                result.required = options;
                result.required.newVertexCapacity = newVertexCapacity;
                result.required.newIndexCapacity = newIndexCapacity;
                result.required.scratchBytes = scratchBytes;
                return false;
            }
            finally
            {
                outputRanges.Dispose();
            }
        }

        /// <summary>
        /// Whether a figure the kernel asked for is a step forward. One that is not larger than what it was already
        /// given would have the next attempt ask the same question again, and a negative one is a requirement too large
        /// to express: neither is a retry, and each ends the run with a status of its own.
        /// </summary>
        private static bool TryAdvance(int wanted, int given, ref VpStorageCutResult result)
        {
            if (wanted < 0)
            {
                result.status = VpStorageCutStatus.CapacityOverflow;
                return false;
            }

            if (wanted <= given)
            {
                result.status = VpStorageCutStatus.CapacityStalled;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Turns a successful kernel run into stored geometries: the reuse case gives the input back borrowed and keeps
        /// the reservation unused, and the ordinary case publishes the used part of the reservation as the two sides.
        /// </summary>
        private static bool TryFinish(
            VpCpuGeometryStorage storage,
            VpStoredGeometry parent,
            VpCutOutputReservation reservation,
            in MeshCutResult kernel,
            NativeArray<MeshCutIndexRange> outputRanges,
            VpGeometrySubmesh[] submeshes,
            NativeArray<VpGeometrySubmesh>.ReadOnly parentSubmeshes,
            int rangeCount,
            ref VpStorageCutResult result)
        {
            bool positiveReuses = kernel.positive.reusesInput != 0;
            bool negativeReuses = kernel.negative.reusesInput != 0;
            if (positiveReuses || negativeReuses)
            {
                // Nothing was written and nothing is owned: the reservation, if one was taken, goes back whole.
                if (reservation != null && !reservation.IsClosed)
                {
                    storage.TryCancelCutOutput(reservation);
                }

                result.positive = new VpStorageCutSide(
                    positiveReuses ? VpStorageCutSideKind.ReusesInput : VpStorageCutSideKind.Empty,
                    positiveReuses ? parent : default);
                result.negative = new VpStorageCutSide(
                    negativeReuses ? VpStorageCutSideKind.ReusesInput : VpStorageCutSideKind.Empty,
                    negativeReuses ? parent : default);
                result.status = VpStorageCutStatus.Ok;
                return true;
            }

            if (reservation == null)
            {
                result.status = VpStorageCutStatus.InternalError;
                return false;
            }

            int positiveIndexCount = kernel.positive.indexCount;
            int negativeIndexCount = kernel.negative.indexCount;
            if (!TryDescribeSide(outputRanges, submeshes, parentSubmeshes, 0, rangeCount, 0, positiveIndexCount, out int positiveSubmeshCount)
                || !TryDescribeSide(outputRanges, submeshes, parentSubmeshes, rangeCount, rangeCount, positiveSubmeshCount, negativeIndexCount, out int negativeSubmeshCount))
            {
                result.status = VpStorageCutStatus.InternalError;
                return false;
            }

            if (!storage.TryCommitCutOutput(
                    reservation,
                    kernel.newVertexCount,
                    kernel.newTopologyVertexCount,
                    positiveIndexCount,
                    negativeIndexCount,
                    submeshes,
                    positiveSubmeshCount,
                    negativeSubmeshCount,
                    out VpStoredGeometry positive,
                    out VpStoredGeometry negative))
            {
                result.status = VpStorageCutStatus.StorageCapacity;
                return false;
            }

            result.positive = new VpStorageCutSide(
                positiveIndexCount > 0 ? VpStorageCutSideKind.Produced : VpStorageCutSideKind.Empty, positive);
            result.negative = new VpStorageCutSide(
                negativeIndexCount > 0 ? VpStorageCutSideKind.Produced : VpStorageCutSideKind.Empty, negative);
            result.status = VpStorageCutStatus.Ok;
            return true;
        }

        /// <summary>
        /// Describes one side's submeshes from the kernel's output ranges, keeping the input's submesh order and giving
        /// each its source material index. Offsets are taken from the counts, so they are relative to the side's own
        /// published range and stay in order even where the kernel joined a side's caps to the last submesh that has
        /// triangles. A side with no index gets no descriptor at all. Returns false when the ranges do not account for
        /// exactly the side's indices.
        /// </summary>
        private static bool TryDescribeSide(
            NativeArray<MeshCutIndexRange> outputRanges,
            VpGeometrySubmesh[] submeshes,
            NativeArray<VpGeometrySubmesh>.ReadOnly parentSubmeshes,
            int rangeStart,
            int rangeCount,
            int writeStart,
            int sideIndexCount,
            out int submeshCount)
        {
            submeshCount = 0;
            if (sideIndexCount == 0)
            {
                return true;
            }

            int covered = 0;
            for (int r = 0; r < rangeCount; r++)
            {
                int count = outputRanges[rangeStart + r].indexCount;
                if (count < 0 || count % 3 != 0)
                {
                    return false;
                }

                submeshes[writeStart + r] = new VpGeometrySubmesh(covered, count, parentSubmeshes[r].materialIndex);
                covered += count;
            }

            submeshCount = rangeCount;
            return covered == sideIndexCount;
        }
    }
}
