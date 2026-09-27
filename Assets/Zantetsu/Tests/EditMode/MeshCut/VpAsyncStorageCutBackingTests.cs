using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Tests;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// How a cut through the runner meets the storage's room once the storage commits pages on demand (DESIGN 4.5.3,
    /// 4.5.4): room another work holds -- outside this runner too -- is a wait that ends when the room comes back;
    /// pages the backing will not commit end the cut as a capacity failure, told to the owner once; an estimate too
    /// small for the input is reserved again, larger, into pages committed for it, and is no failure at all.
    /// </summary>
    public class VpAsyncStorageCutBackingTests
    {
        private const int DeadlineMilliseconds = 30000;

        private sealed class Fixture : IDisposable
        {
            public VpCpuGeometryStorage storage;
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public VpAsyncStorageCut runner;
            public readonly List<string> told = new List<string>();
            private int _frame;

            public void Frame()
            {
                dispatcher.BeginFrame(++_frame);
                dispatcher.Dispatch();
                runner.Pump();
            }

            public void RunUntilOver(VpStorageCutRequest request, string what)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Frame();
                    if (request.IsOver)
                    {
                        return;
                    }

                    Thread.Sleep(1);
                }

                Assert.Fail(what + ": it was still " + request.Stage + " when the deadline passed");
            }

            public void Dispose()
            {
                runner?.Dispose();
                dispatcher?.Shutdown(DeadlineMilliseconds);
                runner?.Pump();
                geometry?.Dispose();
                background?.Dispose();
                storage?.Dispose();
            }
        }

        private static Fixture NewFixture(IVpPageBacking pages, int vertexReserve, int vertexCommit, int indexReserve, int indexCommit)
        {
            var f = new Fixture
            {
                storage = new VpCpuGeometryStorage(
                    new VpCpuGeometryBacking
                    {
                        pages = pages,
                        vertexReserve = vertexReserve,
                        vertexInitialCommit = vertexCommit,
                        indexReserve = indexReserve,
                        indexInitialCommit = indexCommit,
                    },
                    64, 256, 256, Allocator.Persistent),
                job = new UnityJobWorkExecutor(4),
                background = WorkerPoolExecutor.BackgroundPool(2),
                geometry = WorkerPoolExecutor.GeometryPool(4),
            };
            f.storage.BackingFailureHandler = f.told.Add;
            f.dispatcher = new SharedWorkDispatcher(8, 2, 32, f.job, f.geometry, f.background);
            f.runner = new VpAsyncStorageCut(f.storage, f.dispatcher);
            return f;
        }

        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage)
        {
            SyntheticMesh mesh = SyntheticGeometry.Box(3, new float3(1f, 1f, 1f), float3.zero, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(
                storage.TryAppendCuttable(mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes, out VpStoredGeometry geometry, out _),
                Is.True, "the box is appended as a cut input");
            return geometry;
        }

        private static VpStorageCutInput Acquire(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True);
            return input;
        }

        private static float4 Tilted() => new float4(math.normalize(new float3(0.3f, 1f, 0.2f)), -0.05f);

        [Test]
        public void RoomHeldByAWorkOutsideTheRunner_IsAWait_AndTheCutGoesOnWhenItComesBack()
        {
            // A small reservation, so that another work's open reservation can hold what the cut would need.
            using (Fixture f = NewFixture(VpWindowsPageBacking.Instance, 4096, 64, 65536, 64))
            {
                VpStoredGeometry box = AppendBox(f.storage);
                Assert.That(f.storage.TryReserveCutOutput(box, f.storage.FreeVertexRoom - 8, 64, 2, 2, out VpCutOutputReservation held), Is.True,
                    "another work -- not this runner's -- holds nearly all the vertex room");

                VpStorageCutRequest cut = f.runner.Submit(Acquire(f.storage, box), Tilted(), default);
                for (int i = 0; i < 20; i++)
                {
                    f.Frame();
                }

                Assert.That(cut.Stage, Is.EqualTo(VpStorageCutStage.Ready), "the cut waits for the room, unoffered");
                Assert.That(cut.IsOver, Is.False, "and is not failed for it");

                Assert.That(f.storage.TryCancelCutOutput(held), Is.True, "the room comes back");
                f.RunUntilOver(cut, "the cut goes on");
                Assert.That(cut.Result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(f.told, Is.Empty);
            }
        }

        [Test]
        public void PagesTheBackingWillNotCommit_EndTheCutAsACapacityFailure_AndTheOwnerIsToldOnce()
        {
            var pages = new VpVirtualBackingTests.TestPageBacking { CommitLimitBytes = 2 << 20 };
            using (Fixture f = NewFixture(pages, 1 << 20, 64, 1 << 22, 64))
            {
                VpStoredGeometry box = AppendBox(f.storage);
                int free = f.storage.FreeVertexRoom;
                VpStorageCutRequest cut = f.runner.Submit(
                    Acquire(f.storage, box), Tilted(), new VpStorageCutOptions { newVertexCapacity = 200_000, newIndexCapacity = 3000 });
                f.RunUntilOver(cut, "the cut ends");
                Assert.That(cut.Result.status, Is.EqualTo(VpStorageCutStatus.StorageCapacity));
                Assert.That(cut.Result.positive.IsProduced || cut.Result.negative.IsProduced, Is.False, "nothing published");
                Assert.That(f.told.Count, Is.EqualTo(1), "the owner was told once");
                Assert.That(f.runner.ReservingCount, Is.Zero, "no room is held");
                Assert.That(f.storage.FreeVertexRoom, Is.EqualTo(free), "and none was kept");
            }

            Assert.That(pages.ReleasedTwice, Is.Zero);
        }

        [Test]
        public void AnEstimateTooSmall_IsReservedAgainLarger_IntoPagesCommittedForIt_AndIsNoFailure()
        {
            using (Fixture f = NewFixture(VpWindowsPageBacking.Instance, 1 << 20, 64, 1 << 22, 64))
            {
                VpStoredGeometry box = AppendBox(f.storage);
                int committed = f.storage.CommittedIndexCapacity;
                VpStorageCutRequest cut = f.runner.Submit(
                    Acquire(f.storage, box), Tilted(), new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3 });
                f.RunUntilOver(cut, "the cut ends");
                Assert.That(cut.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut succeeded");
                Assert.That(cut.Attempts, Is.GreaterThan(1), "after reserving again");
                Assert.That(f.told, Is.Empty, "a short estimate is not a backing failure");
                Assert.That(f.storage.CommittedIndexCapacity, Is.GreaterThanOrEqualTo(committed));
            }
        }
    }
}
