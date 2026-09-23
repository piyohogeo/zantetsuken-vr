using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Stage 3 buffer lifetime probe: establishes the GPU completion boundary that a deferred release of the five
    /// indexed indirect buffers could rest on, before any release code is written. Stage 1
    /// (<see cref="VpGpuGeometryBuffers"/>) observes completion with an async readback of a
    /// <see cref="GraphicsBuffer.Target.Structured"/> buffer; Stage 3 adds a
    /// <see cref="GraphicsBuffer.Target.Index"/> buffer and two <see cref="GraphicsBuffer.Target.IndirectArguments"/>
    /// buffers, for which that is not assumed to work.
    /// <para>
    /// These tests only measure and report. They assert the facts the release design needs to be true — that a
    /// readback can be requested for each of the five targets and completes without error — so that the design rests
    /// on a measured boundary rather than on a guess. A readback error or an unsupported request is never treated
    /// here as evidence that the GPU has finished with a buffer.
    /// </para>
    /// </summary>
    public class VpIndexedBufferRetirementProbeTests
    {
        private const int VertexCount = 8;
        private const int IndexCount = 12;
        private const int CommandCount = 1;
        private const int InstanceCount = 2;
        private const double ReadbackTimeoutSeconds = 10.0;

        private static GraphicsBuffer Create(GraphicsBuffer.Target target, int count, int stride, string name)
        {
            return new GraphicsBuffer(target, count, stride) { name = name };
        }

        /// <summary>
        /// Requests a readback of the first element and reports what happened, without ever taking an error or an
        /// exception as completion. Returns the request only when one was accepted.
        /// </summary>
        private static bool TryRequestFirstElement(GraphicsBuffer buffer, int stride, out AsyncGPUReadbackRequest request, out string failure)
        {
            request = default;
            failure = null;
            try
            {
                request = AsyncGPUReadback.Request(buffer, stride, 0);
                return true;
            }
            catch (Exception error)
            {
                failure = error.GetType().Name + ": " + error.Message;
                return false;
            }
        }

        private static IEnumerator WaitForDone(AsyncGPUReadbackRequest request, string label)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!request.done)
            {
                Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(ReadbackTimeoutSeconds), label + ": readback completes within ten seconds");
                yield return null;
            }
        }

        [Test]
        public void TheGraphicsDevice_SupportsAsyncGpuReadback()
        {
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True, SystemInfo.graphicsDeviceType.ToString());
        }

        /// <summary>
        /// The completion boundary for each of the five buffers, measured one target at a time: a readback of the
        /// first element is accepted and completes without error. Reported per target so a failing one is named.
        /// </summary>
        [UnityTest]
        public IEnumerator AReadback_IsAcceptedAndCompletesWithoutError_ForEachOfTheFiveBufferTargets()
        {
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True, SystemInfo.graphicsDeviceType.ToString());

            var vertexBuffer = Create(GraphicsBuffer.Target.Structured, VertexCount, VpRenderVertex.Stride, "probe vertices");
            var indexBuffer = Create(GraphicsBuffer.Target.Index, IndexCount, VpGpuIndexedGeometryBuffers.IndexStride, "probe indices");
            var forwardArguments = Create(GraphicsBuffer.Target.IndirectArguments, CommandCount, GraphicsBuffer.IndirectDrawIndexedArgs.size, "probe forward arguments");
            var shadowArguments = Create(GraphicsBuffer.Target.IndirectArguments, CommandCount, GraphicsBuffer.IndirectDrawIndexedArgs.size, "probe shadow arguments");
            var instanceBuffer = Create(GraphicsBuffer.Target.Structured, InstanceCount, VpIndexedIndirectDrawBatch.InstanceStride, "probe instances");
            try
            {
                (GraphicsBuffer buffer, int stride, string label)[] cases =
                {
                    (vertexBuffer, VpRenderVertex.Stride, "Structured vertices"),
                    (indexBuffer, VpGpuIndexedGeometryBuffers.IndexStride, "Index indices"),
                    (forwardArguments, GraphicsBuffer.IndirectDrawIndexedArgs.size, "IndirectArguments forward"),
                    (shadowArguments, GraphicsBuffer.IndirectDrawIndexedArgs.size, "IndirectArguments shadow"),
                    (instanceBuffer, VpIndexedIndirectDrawBatch.InstanceStride, "Structured instances"),
                };

                foreach ((GraphicsBuffer buffer, int stride, string label) in cases)
                {
                    bool accepted = TryRequestFirstElement(buffer, stride, out AsyncGPUReadbackRequest request, out string failure);
                    TestContext.Out.WriteLine(label + ": request " + (accepted ? "accepted" : "REJECTED (" + failure + ")"));
                    Assert.That(accepted, Is.True, label + ": a readback of the first element is accepted (" + failure + ")");

                    yield return WaitForDone(request, label);

                    TestContext.Out.WriteLine(label + ": done=" + request.done + " hasError=" + request.hasError);
                    Assert.That(request.hasError, Is.False, label + ": the readback completes without error");
                }
            }
            finally
            {
                vertexBuffer.Dispose();
                indexBuffer.Dispose();
                forwardArguments.Dispose();
                shadowArguments.Dispose();
                instanceBuffer.Dispose();
            }
        }

        /// <summary>
        /// The order the release design depends on: a readback requested AFTER the last indexed indirect draw has
        /// been queued completes, and the buffers are still valid until it does. This is the boundary itself, not a
        /// frame count: the draw is queued, the readback is requested, and only its completion is taken as the GPU
        /// having finished with the buffers.
        /// </summary>
        [UnityTest]
        public IEnumerator AReadbackRequestedAfterTheLastQueuedDraw_Completes_WithTheBuffersStillValidUntilThen()
        {
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True, SystemInfo.graphicsDeviceType.ToString());
            Shader forwardShader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(forwardShader, Is.Not.Null, "Zantetsu/VP Indexed Indirect Unlit");

            var material = new Material(forwardShader);
            var properties = new MaterialPropertyBlock();
            var pool = new VpCpuGeometryPool(64, 128, Unity.Collections.Allocator.Persistent);
            var buffers = new VpGpuIndexedGeometryBuffers(64, 128);
            var batch = new VpIndexedIndirectDrawBatch(CommandCount, InstanceCount);
            try
            {
                Mesh quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                Assert.That(quad, Is.Not.Null, "Quad.fbx");
                Assert.That(pool.TryAppend(quad, out VpGeometryRange range), Is.True, "append the quad");
                Assert.That(buffers.TryUpload(pool), Is.True, "upload the pool");

                var commands = new[] { new VpIndirectCommand(range, quad.bounds, InstanceCount) };
                var transforms = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(2f, 0f, 0f)) };
                Assert.That(batch.TryUpload(commands, transforms, false), Is.True, "upload the commands");

                // The last draw registration for these buffers. camera = null queues it for this frame's cameras.
                batch.RenderForward(material, properties, buffers, 0, 0, batch.CommandCount, null);

                // Requested after that registration: this is the candidate completion boundary.
                bool accepted = TryRequestFirstElement(buffers.IndexBuffer, VpGpuIndexedGeometryBuffers.IndexStride, out AsyncGPUReadbackRequest indexReadback, out string indexFailure);
                Assert.That(accepted, Is.True, "index readback accepted after the draw was queued (" + indexFailure + ")");
                accepted = TryRequestFirstElement(batch.ForwardArgumentBuffer, GraphicsBuffer.IndirectDrawIndexedArgs.size, out AsyncGPUReadbackRequest argumentReadback, out string argumentFailure);
                Assert.That(accepted, Is.True, "forward argument readback accepted after the draw was queued (" + argumentFailure + ")");

                Assert.That(buffers.IndexBuffer.IsValid(), Is.True, "the index buffer is valid while the readback is pending");
                Assert.That(batch.ForwardArgumentBuffer.IsValid(), Is.True, "the argument buffer is valid while the readback is pending");

                yield return WaitForDone(indexReadback, "index after draw");
                yield return WaitForDone(argumentReadback, "forward arguments after draw");

                TestContext.Out.WriteLine("after the queued draw: index hasError=" + indexReadback.hasError
                    + ", forward arguments hasError=" + argumentReadback.hasError);
                Assert.That(indexReadback.hasError, Is.False, "the index readback completes without error");
                Assert.That(argumentReadback.hasError, Is.False, "the forward argument readback completes without error");
            }
            finally
            {
                batch.Dispose();
                buffers.Dispose();
                pool.Dispose();
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
