using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The capacity arithmetic at the top of its range. An input is a description — how many indices the ranges
    /// claim, how long the view is, how many topology vertices there are — and the capacity entries read only that,
    /// so these ask about sizes no machine could hold without holding any of it. Nothing here allocates more than a
    /// handful of values.
    /// <para>
    /// What is checked is that a figure too large to express never comes back as a small one. A reservation made from
    /// a wrapped product would be smaller than the run needs, which is the one outcome the arithmetic must not have.
    /// </para>
    /// </summary>
    public unsafe class MeshCutCapacityArithmeticTests
    {
        /// <summary>
        /// One input described by its sizes alone. The vertex and index pointers are real but tiny: the capacity
        /// entries do not read through them, which is the property that lets this test exist.
        /// </summary>
        private static MeshCutInput Described(
            MeshCutIndexRange* ranges, int rangeCount, int indexViewLength, int topologyVertexCount,
            VpRenderVertex* vertices, uint* indices)
        {
            return new MeshCutInput
            {
                vertices = vertices,
                vertexViewLength = 1,
                indices = indices,
                indexViewLength = indexViewLength,
                ranges = ranges,
                rangeCount = rangeCount,
                topology = new RenderCutTopologyMap { topologyVertexCount = topologyVertexCount },
                plane = new float4(0f, 1f, 0f, 0f),
            };
        }

        /// <summary>
        /// A triangle count near the top of an int: the index figure for it is over three times that, which no int
        /// holds. The estimate says so and leaves no figure to reserve from.
        /// </summary>
        [Test]
        public void AnIndexFigureTooLargeForAnInt_IsRefused_AndNotRoundedDown()
        {
            var vertex = new NativeArray<VpRenderVertex>(1, Allocator.Temp);
            var index = new NativeArray<uint>(1, Allocator.Temp);
            var ranges = new NativeArray<MeshCutIndexRange>(1, Allocator.Temp);
            try
            {
                // 2147483643 = 3 * 715827881: a whole number of triangles, and as many as an int can describe.
                const int indexCount = 2147483643;
                ranges[0] = new MeshCutIndexRange { indexStart = 0, indexCount = indexCount };
                MeshCutInput input = Described(
                    (MeshCutIndexRange*)ranges.GetUnsafePtr(), 1, indexCount, 1024,
                    (VpRenderVertex*)vertex.GetUnsafePtr(), (uint*)index.GetUnsafePtr());

                MeshCutCapacity capacity = default;
                MeshCutKernel.EstimateCapacity(in input, ref capacity);

                Assert.That(capacity.invalidInput, Is.Zero, "the input itself is well described");
                Assert.That(capacity.capacityOverflow, Is.Not.Zero, "and its figures cannot be expressed");
                Assert.That(capacity.triangleCount, Is.EqualTo(indexCount / 3), "the triangle count is exact all the same");
                TestContext.WriteLine(
                    "index figure over an int: triangles " + capacity.triangleCount
                    + ", overflow " + capacity.capacityOverflow
                    + ", figures left at vertices " + capacity.newVertices + ", indices " + capacity.newIndices);
            }
            finally
            {
                ranges.Dispose();
                index.Dispose();
                vertex.Dispose();
            }
        }

        /// <summary>
        /// A topology count large enough that the classification part alone is over an int of bytes: four bytes a
        /// vertex for the distances, and a byte more for the side memo. The estimate refuses it rather than laying
        /// out into a wrapped size.
        /// </summary>
        [Test]
        public void AScratchFigureTooLargeForAnInt_IsRefused_AndNotRoundedDown()
        {
            var vertex = new NativeArray<VpRenderVertex>(1, Allocator.Temp);
            var index = new NativeArray<uint>(1, Allocator.Temp);
            var ranges = new NativeArray<MeshCutIndexRange>(1, Allocator.Temp);
            try
            {
                ranges[0] = new MeshCutIndexRange { indexStart = 0, indexCount = 30 };
                MeshCutInput input = Described(
                    (MeshCutIndexRange*)ranges.GetUnsafePtr(), 1, 30, int.MaxValue - 16,
                    (VpRenderVertex*)vertex.GetUnsafePtr(), (uint*)index.GetUnsafePtr());

                MeshCutCapacity capacity = default;
                MeshCutKernel.EstimateCapacity(in input, ref capacity);

                Assert.That(capacity.invalidInput, Is.Zero, "the input itself is well described");
                Assert.That(capacity.capacityOverflow, Is.Not.Zero, "and its scratch figure cannot be expressed");
                Assert.That(capacity.scratchBytes, Is.GreaterThanOrEqualTo(0), "no figure is negative");
                TestContext.WriteLine(
                    "scratch figure over an int: topology vertices " + (int.MaxValue - 16)
                    + ", overflow " + capacity.capacityOverflow
                    + ", scratch left at " + capacity.scratchBytes);
            }
            finally
            {
                ranges.Dispose();
                index.Dispose();
                vertex.Dispose();
            }
        }

        /// <summary>
        /// The run itself, not only the capacity entries: a total that wraps to a **small positive** number would lay
        /// out a scratch block for a couple of triangles while the ranges address billions of indices. Six ranges of
        /// the largest whole-triangle count come to 2^32 - 10 triangles, and one more range of twelve carries the
        /// total two past 2^32, which an int sum reads as 2.
        /// <para>
        /// The kernel does not rely on a caller having asked about capacity first, so <see cref="MeshCutKernel.Execute"/>
        /// refuses this itself, before classifying or writing anything. Nothing large is allocated here: the ranges
        /// are a description, and the run never gets far enough to read through them.
        /// </para>
        /// </summary>
        [Test]
        public void ATotalThatWrapsToASmallPositive_IsRefusedByTheRunItself()
        {
            const int largest = 2147483643;   // 3 * 715827881: the most whole triangles a range can describe
            const int rangeCount = 7;
            var vertex = new NativeArray<VpRenderVertex>(1, Allocator.Temp);
            var index = new NativeArray<uint>(1, Allocator.Temp);
            var ranges = new NativeArray<MeshCutIndexRange>(rangeCount, Allocator.Temp);
            var outputRanges = new NativeArray<MeshCutIndexRange>(2 * rangeCount, Allocator.Temp);
            try
            {
                for (int r = 0; r < rangeCount - 1; r++)
                {
                    ranges[r] = new MeshCutIndexRange { indexStart = 0, indexCount = largest };
                }

                ranges[rangeCount - 1] = new MeshCutIndexRange { indexStart = 0, indexCount = 36 };

                // What an int sum would have made of it, worked out here so the case is readable.
                int wrapped = 0;
                for (int r = 0; r < rangeCount; r++)
                {
                    unchecked { wrapped += ranges[r].indexCount / 3; }
                }

                Assert.That(wrapped, Is.GreaterThan(0), "the layout: an int sum reads this as a positive count");
                Assert.That(wrapped, Is.LessThan(1000), "and a small one, which is what makes it dangerous");

                MeshCutInput input = Described(
                    (MeshCutIndexRange*)ranges.GetUnsafePtr(), rangeCount, largest, 1024,
                    (VpRenderVertex*)vertex.GetUnsafePtr(), (uint*)index.GetUnsafePtr());
                var output = new MeshCutOutput
                {
                    outputRanges = (MeshCutIndexRange*)outputRanges.GetUnsafePtr(),
                    scratch = null,
                    scratchBytes = 0,
                };

                MeshCutResult result = default;
                MeshCutKernel.Execute(in input, in output, ref result);

                Assert.That(
                    result.status, Is.EqualTo(MeshCutStatus.InvalidInput),
                    "a total that does not fit an int is refused before anything is laid out");
                Assert.That(result.triangleCount, Is.Zero, "and no count of it is reported");
                TestContext.WriteLine(
                    "a total wrapping to a small positive: an int sum would read " + wrapped
                    + " triangles; Execute answered " + result.status);
            }
            finally
            {
                outputRanges.Dispose();
                ranges.Dispose();
                index.Dispose();
                vertex.Dispose();
            }
        }

        /// <summary>
        /// The count itself, at the capacity entry: ranges whose index counts add up past an int are not a count at
        /// all, and are refused as a malformed input rather than wrapping into a small one.
        /// </summary>
        [Test]
        public void ATriangleCountPastAnInt_IsRefusedAsInvalidInput()
        {
            var vertex = new NativeArray<VpRenderVertex>(1, Allocator.Temp);
            var index = new NativeArray<uint>(1, Allocator.Temp);
            var ranges = new NativeArray<MeshCutIndexRange>(4, Allocator.Temp);
            try
            {
                const int indexCount = 2147483643;
                for (int r = 0; r < ranges.Length; r++)
                {
                    ranges[r] = new MeshCutIndexRange { indexStart = 0, indexCount = indexCount };
                }

                MeshCutInput input = Described(
                    (MeshCutIndexRange*)ranges.GetUnsafePtr(), ranges.Length, indexCount, 1024,
                    (VpRenderVertex*)vertex.GetUnsafePtr(), (uint*)index.GetUnsafePtr());

                MeshCutCapacity capacity = default;
                MeshCutKernel.EstimateCapacity(in input, ref capacity);

                Assert.That(capacity.invalidInput, Is.Not.Zero, "a count that does not fit an int is not a count");
                Assert.That(capacity.newVertices, Is.Zero, "and nothing is offered to reserve from");
                Assert.That(capacity.newIndices, Is.Zero);
            }
            finally
            {
                ranges.Dispose();
                index.Dispose();
                vertex.Dispose();
            }
        }
    }
}
