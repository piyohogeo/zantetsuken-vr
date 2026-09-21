using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What one run of <see cref="ConvexCutAndMeshJob"/> did, beyond the kernel's own result.</summary>
    internal struct PhysicsCutJobReport
    {
        public ConvexCutOwnerResult kernel;

        /// <summary>How many collider meshes were written, one per produced convex.</summary>
        public int meshesWritten;

        /// <summary>
        /// 1 when every produced convex got a mesh. The kernel succeeding is not this: a run whose meshes did not all
        /// fit the slots reserved for them is not a complete run, and nothing of it is handed over.
        /// </summary>
        public byte meshesComplete;

        /// <summary>
        /// 1 only where the job itself reached the end of its body, set last on every path it can leave by. A report
        /// nobody wrote is all zeros, so this tells a run that did not happen from one that did — the kernel's own
        /// success cannot, because its Ok is zero as well.
        /// </summary>
        public byte ranToEnd;
    }

    /// <summary>
    /// The output one owner cut was reserved: the B-rep arena the kernel writes its split convexes into, one outcome
    /// per input convex, and the scratch. It is taken before the work is offered, held until the cut is over, and
    /// given back exactly once — by the products when there are products, and by the runner when there are not. A
    /// construction that fails part way gives back what it had already taken.
    /// </summary>
    internal sealed unsafe class PhysicsCutArena : IDisposable
    {
        private NativeArray<float3> _vertices;
        private NativeArray<int> _faceOffsets;
        private NativeArray<int> _faceIndices;
        private NativeArray<int> _faceEdges;
        private NativeArray<BrepEdge> _edges;
        private NativeArray<ConvexCutOutcome> _outcomes;
        private NativeArray<byte> _scratch;
        private bool _disposed;

        internal PhysicsCutArena(in ConvexCutOwnerCapacity capacity, int convexCount)
        {
            try
            {
                // A zero-length native array has no pointer to give, so every run is allocated at least one element;
                // the capacities the kernel is told are the ones it asked for.
                _vertices = new NativeArray<float3>(math.max(1, capacity.vertices), Allocator.Persistent);
                _faceOffsets = new NativeArray<int>(math.max(1, capacity.faceOffsets), Allocator.Persistent);
                _faceIndices = new NativeArray<int>(math.max(1, capacity.faceIndices), Allocator.Persistent);
                _faceEdges = new NativeArray<int>(math.max(1, capacity.faceIndices), Allocator.Persistent);
                _edges = new NativeArray<BrepEdge>(math.max(1, capacity.edges), Allocator.Persistent);
                _outcomes = new NativeArray<ConvexCutOutcome>(math.max(1, convexCount), Allocator.Persistent);
                _scratch = new NativeArray<byte>(math.max(1, capacity.scratchBytes), Allocator.Persistent);
            }
            catch
            {
                // Nothing of a half-built arena is kept: what was taken before the failure goes back here.
                Dispose();
                throw;
            }

            Bank = new ConvexBrepBank
            {
                vertices = (float3*)_vertices.GetUnsafePtr(),
                faceOffsets = (int*)_faceOffsets.GetUnsafePtr(),
                faceIndices = (int*)_faceIndices.GetUnsafePtr(),
                faceEdges = (int*)_faceEdges.GetUnsafePtr(),
                edges = (BrepEdge*)_edges.GetUnsafePtr(),
            };
            Output = new ConvexCutOwnerOutput
            {
                bank = Bank,
                vertexCapacity = capacity.vertices,
                faceOffsetCapacity = capacity.faceOffsets,
                faceIndexCapacity = capacity.faceIndices,
                edgeCapacity = capacity.edges,
                outcomes = (ConvexCutOutcome*)_outcomes.GetUnsafePtr(),
                scratch = (byte*)_scratch.GetUnsafePtr(),
                scratchBytes = capacity.scratchBytes,
            };
        }

        internal ConvexBrepBank Bank { get; }

        internal ConvexCutOwnerOutput Output { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release(ref _vertices);
            Release(ref _faceOffsets);
            Release(ref _faceIndices);
            Release(ref _faceEdges);
            Release(ref _edges);
            Release(ref _outcomes);
            Release(ref _scratch);
        }

        private static void Release<T>(ref NativeArray<T> array)
            where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }

            array = default;
        }
    }

    /// <summary>
    /// The numerical work of one owner cut, as one job (DESIGN 4.3, 7.2): the existing kernel clips, reduces and
    /// integrates the mass properties, and the same job writes the collider mesh data of every convex it produced.
    /// It is not split into further jobs, it allocates nothing, and it touches no Unity object — the mesh data it
    /// writes into was allocated on the main thread and is applied there.
    /// <para>
    /// The meshes are vertex only, which is what a convex <see cref="MeshCollider"/> needs: the cook builds the hull
    /// from the points. Each produced convex is one mesh, in the order the split convexes come, positive then
    /// negative. A slot no convex was written into keeps zero vertices, and the main thread destroys its mesh rather
    /// than baking it.
    /// </para>
    /// <para>
    /// The report says both what the kernel did and whether every produced convex really got its mesh, because those
    /// are different things: a kernel that succeeded is not by itself a run that may be handed over.
    /// </para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    internal unsafe struct ConvexCutAndMeshJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public ConvexCutOwnerInput input;
        [NativeDisableUnsafePtrRestriction] public ConvexCutOwnerOutput output;
        public Mesh.MeshDataArray meshData;
        public NativeArray<float3x2> bounds;
        public NativeArray<int> vertexCounts;
        public NativeArray<PhysicsCutJobReport> report;

        public void Execute()
        {
            var written = new PhysicsCutJobReport();
            ConvexCutOwnerKernel.Execute(in input, in output, ref written.kernel);
            for (int m = 0; m < vertexCounts.Length; m++)
            {
                vertexCounts[m] = 0;
                bounds[m] = default;
            }

            if (written.kernel.status != ConvexCutOwnerStatus.Ok)
            {
                // Nothing produced is written: a failed run has no adopted convexes to cook.
                for (int m = 0; m < meshData.Length; m++)
                {
                    Empty(meshData[m]);
                }

                written.ranToEnd = 1;
                report[0] = written;
                return;
            }

            int slot = 0;
            bool complete = true;
            for (int c = 0; c < input.convexCount; c++)
            {
                ConvexCutOutcome outcome = output.outcomes[c];
                if (!outcome.IsSplit)
                {
                    continue;
                }

                complete &= Write(ref slot, in outcome.positive);
                complete &= Write(ref slot, in outcome.negative);
            }

            for (int m = slot; m < meshData.Length; m++)
            {
                Empty(meshData[m]);
            }

            written.meshesWritten = slot;
            written.meshesComplete = complete ? (byte)1 : (byte)0;
            written.ranToEnd = 1;
            report[0] = written;
        }

        private bool Write(ref int slot, in ConvexBrepRange range)
        {
            if (slot >= meshData.Length)
            {
                // More produced convexes than there are slots: the reservation came from the kernel's own worst case,
                // so this is an error and not a step. The run is reported incomplete and nothing is handed over.
                return false;
            }

            Mesh.MeshData data = meshData[slot];
            int n = range.vertexCount;
            if (n <= 0)
            {
                Empty(data);
                slot++;
                return false;
            }

            SetPositionOnlyLayout(data, n);
            NativeArray<float3> vertices = data.GetVertexData<float3>();
            float3* source = output.bank.vertices + range.vertexBase;
            var lower = new float3(float.MaxValue);
            var upper = new float3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                float3 p = source[i];
                vertices[i] = p;
                lower = math.min(lower, p);
                upper = math.max(upper, p);
            }

            data.subMeshCount = 1;
            data.SetSubMesh(
                0,
                new SubMeshDescriptor(0, 0, MeshTopology.Triangles)
                {
                    bounds = new Bounds((lower + upper) * 0.5f, upper - lower),
                    firstVertex = 0,
                    vertexCount = n,
                },
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
            bounds[slot] = new float3x2(lower, upper);
            vertexCounts[slot] = n;
            slot++;
            return true;
        }

        private static void Empty(Mesh.MeshData data)
        {
            SetPositionOnlyLayout(data, 0);
            data.subMeshCount = 0;
        }

        /// <summary>
        /// The one vertex layout every cooked element has: positions alone, and no index buffer.
        /// <para>
        /// **The attribute is handed over in a native array, not a managed one.** <see
        /// cref="Mesh.MeshData.SetVertexBufferParams(int, VertexAttributeDescriptor[])"/> takes its attributes as
        /// <c>params</c>, so each call allocates a managed array -- which Burst cannot compile (BC1028), and this job
        /// is Burst-compiled. The overload taking a <see cref="NativeArray{T}"/> says the same thing with memory the
        /// job can hold itself; <see cref="Allocator.Temp"/> is the allocator a job's own scratch uses, freed here in
        /// the call that made it.
        /// </para>
        /// </summary>
        private static void SetPositionOnlyLayout(Mesh.MeshData data, int vertexCount)
        {
            var attributes = new NativeArray<VertexAttributeDescriptor>(
                1, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            attributes[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3);
            data.SetVertexBufferParams(vertexCount, attributes);
            attributes.Dispose();
            data.SetIndexBufferParams(0, IndexFormat.UInt16);
        }
    }

    /// <summary>
    /// The cook of what one cut produced (DESIGN 4.3: an urgent managed job, never called from Burst). One mesh per
    /// element, each baked once with the profile the runner chose; a slot with no mesh is skipped.
    /// <para>
    /// <see cref="UnityEngine.Physics.BakeMesh"/> gives no success value, so this cannot report whether a cook
    /// succeeded. What is known here is that the bake ran to the end and the job itself did not fail; whether the
    /// cooked hull is what the B-rep was is not asked at all (DESIGN 7.3), and no success value is invented for it.
    /// </para>
    /// </summary>
    internal struct BakeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> ids;

        /// <summary>
        /// 1 per element once that element has been dealt with: the bake call returned, or there was no mesh in that
        /// slot. The caller requires every element before it hands anything over. This says the call was made and came
        /// back, nothing more — <see cref="UnityEngine.Physics.BakeMesh"/> reports no success of its own.
        /// </summary>
        [WriteOnly] public NativeArray<byte> done;

        public MeshColliderCookingOptions cooking;

        public void Execute(int index)
        {
            int id = ids[index];
            if (id == 0)
            {
                done[index] = 1;
                return;
            }

            UnityEngine.Physics.BakeMesh(id, true, cooking);
            done[index] = 1;
        }
    }
}
