using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// VirtualAlloc (the product's own) with 4 KiB rounding, and a commit refused past a number of bytes per reservation:
    /// for a world whose reservation or commit is to fail on purpose, without exhausting memory.
    /// </summary>
    internal sealed class TestPagedBacking : IVpPageBacking
    {
        private readonly List<(IntPtr at, long bytes)> _reserved = new List<(IntPtr, long)>();
        public long CommitLimitBytes = long.MaxValue;
        public int Commits;
        public int Releases;

        public long Granularity => 4096;

        public bool TryReserve(long bytes, out IntPtr address, out string failure)
        {
            if (!VpWindowsPageBacking.Instance.TryReserve(bytes, out address, out failure))
            {
                return false;
            }

            _reserved.Add((address, bytes));
            return true;
        }

        public bool TryCommit(IntPtr address, long bytes, out string failure)
        {
            foreach ((IntPtr at, long size) in _reserved)
            {
                long offset = (long)address - (long)at;
                if (offset >= 0 && offset < size && offset + bytes > CommitLimitBytes)
                {
                    failure = "test: commit past " + CommitLimitBytes + " bytes refused";
                    return false;
                }
            }

            Commits++;
            return VpWindowsPageBacking.Instance.TryCommit(address, bytes, out failure);
        }

        public void Release(IntPtr address, long bytes)
        {
            Releases++;
            VpWindowsPageBacking.Instance.Release(address, bytes);
        }
    }

    /// <summary>
    /// The storage's reserved-and-committed backing and the GPU copy's growth, through the product's own world
    /// (DESIGN 4.5.3, 4.5.4): its initialisation from the profile, the bodies it takes in, the cuts its update loop
    /// carries to their commit, and its drawing. Small real memory: the world is given a page backing that commits in
    /// 4 KiB pages (the product's own VirtualAlloc underneath), a first commit of a few pages and a GPU copy of a few
    /// dozen vertices, so that ordinary bodies and cuts cross both.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private static void SmallBacking(CutWorldProfile profile)
        {
            SetPrivate(profile, "vertexReserve", 1 << 20);
            SetPrivate(profile, "vertexInitialCommit", 64);
            SetPrivate(profile, "indexReserve", 1 << 22);
            SetPrivate(profile, "indexInitialCommit", 64);
            SetPrivate(profile, "gpuVertexInitialCapacity", 32);
            SetPrivate(profile, "gpuIndexInitialCapacity", 48);
        }

        /// <summary>
        /// **Past the first commit and past the GPU copy's first capacity, what is stored and drawn is what the CPU
        /// holds.** Bodies are taken in and cut, and a child cut again, until both the CPU commit and the GPU copy have
        /// grown; then every geometry the display holds is read back from the GPU and compared with the CPU copy --
        /// its vertex blocks and its index range -- and every body is still drawn.
        /// </summary>
        [UnityTest]
        public IEnumerator PastTheFirstCommitAndTheGpusFirstCapacity_EveryDrawnGeometryIsWhatTheCpuHolds()
        {
            var pages = new TestPagedBacking();
            CutWorldRoot.nextWorldPageBacking = pages;
            CutWorldRoot root = NewWorld(out Shader _, null, null, SmallBacking);

            // Sixteen cuts in one frame would be carried over by the Main budget (the Pending of DESIGN 7.1.1), which in
            // the Editor's frames may never fit again; this case is about room, so the remainder is made deterministic,
            // as the Pending cases themselves do. The Pending cases are run unchanged beside this one.
            root.Driver.RemainingMainSeconds = () => 1.0;
            int firstVertices = root.Storage.CommittedVertexCapacity;
            int firstIndices = root.Storage.CommittedIndexCapacity;
            Assert.That(root.Display.GpuVertexCapacity, Is.EqualTo(32), "the GPU copy starts at its own first capacity");
            Assert.That(root.Storage.VertexCapacity, Is.EqualTo(1 << 20), "not at the CPU reservation");

            var bodies = new List<LogicalFragmentId>();
            for (int i = 0; i < 16; i++)
            {
                bodies.Add(AddBody(root, new Vector3(3f * (i % 4), 0f, 3f * (i / 4))));
            }

            yield return null;
            var operations = new List<CutOperationId>();
            foreach (LogicalFragmentId body in bodies)
            {
                ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
                Assert.That(root.TryAsk(in ask), Is.True);
            }

            yield return null;
            foreach (ProvisionalCutTransaction t in root.Driver.Transactions)
            {
                operations.Add(t.Operation);
            }

            Assert.That(operations.Count, Is.EqualTo(bodies.Count), "every cut was accepted");
            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (!operations.TrueForAll(o => root.Geometry.StageOf(o) == CutGeometryStage.Committed) && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!operations.TrueForAll(o => root.Geometry.StageOf(o) == CutGeometryStage.Committed))
            {
                var stages = new System.Text.StringBuilder();
                foreach (CutOperationId o in operations)
                {
                    root.Ledger.TryGetOperation(o, out LogicalCutOperation record);
                    ProvisionalCutTransaction t = root.Driver.TransactionOf(o);
                    stages.Append("op").Append(o.value).Append(' ').Append(root.Geometry.StageOf(o)).Append(" ledger ").Append(record.state)
                        .Append(" phase ").Append(t != null ? t.Phase.ToString() : "ended").Append("; ");
                }

                Assert.Fail("every cut committed: not within the deadline -- " + stages + " faults " + root.GeometryFaults
                            + "; " + root.Storage.DescribeRoom() + "; " + root.Display.DescribeGpuRoom());
            }

            // A child, cut in its turn.
            LogicalCutOperation first = OperationOf(root, operations[0]);
            ProvisionalCutAsk again = Ask(first.positive, new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in again), Is.True);
            yield return null;
            CutOperationId child = default;
            foreach (ProvisionalCutTransaction t in root.Driver.Transactions)
            {
                child = t.Operation;
            }

            yield return Until(() => root.Geometry.StageOf(child) == CutGeometryStage.Committed, "the child's cut committed");
            yield return null;
            yield return null;

            Assert.That(root.TerminationRequested, Is.False);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Storage.BackingFailure, Is.Null);
            Assert.That(root.Storage.CommittedVertexCapacity, Is.GreaterThan(firstVertices), "the CPU commit grew for the vertices");
            Assert.That(root.Storage.CommittedIndexCapacity, Is.GreaterThan(firstIndices), "and for the indices");
            Assert.That(root.Display.GpuGrowthCount, Is.GreaterThan(0), "the GPU copy grew");
            Assert.That(root.Display.GpuVertexCapacity, Is.GreaterThan(32));
            Assert.That(pages.Commits, Is.GreaterThan(3), "pages were committed beyond the first three");

            // What the GPU holds for everything the display holds, against the CPU copy.
            VpGpuIndexedGeometryBuffers gpu = root.Display.Buffers;
            var gpuVertices = new VpRenderVertex[gpu.VertexCapacity];
            var gpuIndices = new uint[gpu.IndexCapacity];
            gpu.VertexBuffer.GetData(gpuVertices);
            gpu.IndexBuffer.GetData(gpuIndices);
            int compared = 0;
            for (int slot = 0; slot < root.References.GeometryCapacity; slot++)
            {
                if (!root.References.TryGetLiveGeometryAt(slot, out VpStoredGeometry geometry))
                {
                    continue;
                }

                Assert.That(root.Storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True);
                for (int b = 0; b < blocks.Length; b++)
                {
                    Assert.That(root.Storage.TryGetCommittedVertices(blocks[b].vertexStart, blocks[b].vertexCount, out NativeArray<VpRenderVertex> cpu), Is.True);
                    for (int v = 0; v < cpu.Length; v++)
                    {
                        VpRenderVertex g = gpuVertices[blocks[b].vertexStart + v];
                        Assert.That(g.position, Is.EqualTo(cpu[v].position), "vertex " + (blocks[b].vertexStart + v) + " on the GPU");
                        Assert.That(g.normalX == cpu[v].normalX && g.normalY == cpu[v].normalY && g.u == cpu[v].u && g.v == cpu[v].v, Is.True);
                    }
                }

                Assert.That(root.Storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices), Is.True);
                try
                {
                    Assert.That(root.Storage.TryGetIndexState(geometry.indexRange, out _, out int start, out int count), Is.True);
                    for (int i = 0; i < count; i++)
                    {
                        Assert.That(gpuIndices[start + i], Is.EqualTo(indices[i]), "index " + (start + i) + " on the GPU");
                    }
                }
                finally
                {
                    root.Storage.TryReleaseIndexReadLease(lease);
                }

                compared++;
            }

            Assert.That(compared, Is.GreaterThanOrEqualTo(bodies.Count + 1), "every body's sides are held and compared");
            // Everything live is drawn: each cut's two children, except the child that was cut again, which is drawn
            // as its own two children.
            var live = new List<LogicalFragmentId>();
            foreach (CutOperationId o in operations)
            {
                LogicalCutOperation record = OperationOf(root, o);
                if (record.positive != first.positive) live.Add(record.positive);
                live.Add(record.negative);
            }

            LogicalCutOperation childRecord = OperationOf(root, child);
            live.Add(childRecord.positive);
            live.Add(childRecord.negative);
            foreach (LogicalFragmentId fragment in live)
            {
                Assert.That(IsDrawn(root, fragment), Is.True, "fragment " + fragment.value + " is drawn");
            }

            TestContext.WriteLine(root.Storage.DescribeRoom() + "; " + root.Display.DescribeGpuRoom());

            // The replaced GPU buffers are released once the GPU is past them, on the ordinary frames, with no camera.
            yield return Until(() => gpu.RetiredCount == 0, "every replaced buffer was released once its readback completed");
            yield return EndWorld(root);
            Assert.That(pages.Releases, Is.EqualTo(3), "the three reservations were released once each at the ending");
        }

        /// <summary>
        /// **Pages the backing will not commit during play are the common termination, once.** A world whose backing
        /// refuses to commit past its first pages is given more bodies and cuts than they hold: the refusal is told,
        /// the termination API is called once, and nothing else is accepted.
        /// </summary>
        [UnityTest]
        public IEnumerator PagesRefusedDuringPlay_RequestThePlayersTermination_Once()
        {
            ExpectTermination();
            int terminations = 0;
            var pages = new TestPagedBacking { CommitLimitBytes = 4096 };
            CutWorldRoot.nextWorldPageBacking = pages;
            CutWorldRoot root = NewWorld(out Shader _, null, () => terminations++, SmallBacking);
            var bodies = new List<LogicalFragmentId>();
            for (int i = 0; i < 8; i++)
            {
                bodies.Add(AddBody(root, new Vector3(3f * i, 0f, 0f)));
            }

            yield return null;
            Assert.That(root.TerminationRequested, Is.False, "the bodies fit in the first pages");

            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            foreach (LogicalFragmentId body in bodies)
            {
                ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
                root.TryAsk(in ask);
            }

            yield return Until(() => root.TerminationRequested, "the refused commit requested the termination");
            Assert.That(root.Storage.BackingFailure, Is.Not.Null, "the storage says its backing failed");
            StringAssert.Contains("backing failed", root.Storage.BackingFailure);
            yield return null;
            Assert.That(terminations, Is.EqualTo(1), "the Player's termination API was called once");
            Assert.That(root.TerminationCalls, Is.EqualTo(1));
            TestContext.WriteLine(root.Storage.DescribeRoom());
        }
        private static Action<CutWorldProfile> RoomOf(int vertexReserve)
        {
            return profile =>
            {
                SetPrivate(profile, "vertexReserve", vertexReserve);
                SetPrivate(profile, "vertexInitialCommit", vertexReserve);
                SetPrivate(profile, "indexReserve", 8192);
                SetPrivate(profile, "indexInitialCommit", 8192);
                SetPrivate(profile, "gpuVertexInitialCapacity", 64);
                SetPrivate(profile, "gpuIndexInitialCapacity", 64);
            };
        }

        // Another work's open cut output reservation of the given number of vertices, on a box of its own.
        private static VpCutOutputReservation OpenUnrelatedReservation(CutWorldRoot root, int vertices)
        {
            VpStoredGeometry other = AppendBoxGeometry(root.Storage, new Vector3(9f, 0f, 0f));
            Assert.That(root.Storage.TryReserveCutOutput(other, vertices, 3, 4, 4, out VpCutOutputReservation held), Is.True,
                "another work's open reservation");
            return held;
        }

        private static CutOperationId LastTransaction(CutWorldRoot root)
        {
            CutOperationId operation = default;
            foreach (ProvisionalCutTransaction t in root.Driver.Transactions)
            {
                operation = t.Operation;
            }

            return operation;
        }

        /// <summary>
        /// **A cut whose room cannot come back is the termination, though an unrelated reservation is open.** The box's
        /// cut asks for 208 vertices (its estimate: 12 per crossing triangle and two per auxiliary vertex); the
        /// reservation holds 200, of which the body, another work's box and its open reservation of 8 take 56. What that
        /// reservation holds would not cover the request if it came back, so the cut is not held waiting for it.
        /// </summary>
        [UnityTest]
        public IEnumerator ACutWhoseRoomCannotComeBack_IsTheTermination_ThoughAnUnrelatedReservationIsOpen()
        {
            ExpectTermination();
            int terminations = 0;
            CutWorldRoot root = NewWorld(out Shader _, null, () => terminations++, RoomOf(200));
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            OpenUnrelatedReservation(root, 8);
            yield return null;

            LogAssert.Expect(LogType.Error, new Regex("the Player is being ended"));
            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return Until(() => root.TerminationRequested, "the cut that cannot be given room requested the termination");
            StringAssert.Contains("cannot clear", root.Storage.LastRefusal);
            Assert.That(root.Storage.BackingFailure, Is.Null, "it is the reservation's limit, not the backing");
            yield return null;
            Assert.That(terminations, Is.EqualTo(1), "once");
        }

        /// <summary>
        /// **A cut short of room that will come back waits, and goes on once when it is back.** Another work's open
        /// reservation holds 200 of 400 vertices: the cut's 208 do not fit now, and would with what that reservation
        /// holds. The cut waits -- no termination, its geometry not committed, no room of its own held -- and when the
        /// room is given back it commits, as one Operation.
        /// </summary>
        [UnityTest]
        public IEnumerator ACutShortOfRoomThatWillComeBack_Waits_AndGoesOnOnceWhenItIsBack()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, RoomOf(400));
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            VpCutOutputReservation held = OpenUnrelatedReservation(root, 200);
            yield return null;
            int free = root.Storage.FreeVertexRoom;

            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            CutOperationId operation = LastTransaction(root);
            Assert.That(operation.IsSet, Is.True, "accepted");
            for (int i = 0; i < 10; i++) yield return null;
            Assert.That(root.TerminationRequested, Is.False, "a wait, not the termination");
            Assert.That(root.Geometry.StageOf(operation), Is.Not.EqualTo(CutGeometryStage.Committed), "its geometry waits");
            Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(free), "and it holds no room while it waits");
            StringAssert.Contains("can clear", root.Storage.LastRefusal);

            Assert.That(root.Storage.TryCancelCutOutput(held), Is.True, "the room comes back");
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the same cut goes on and commits");
            Assert.That(root.Ledger.OperationCount, Is.EqualTo(1), "one Operation");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The world ending while a cut waits for room leaves no room held.** The same wait; the world is ended the
        /// ordinary way while the cut waits, and the ending is confirmed: nothing the waiting cut had was kept.
        /// </summary>
        [UnityTest]
        public IEnumerator TheWorldEndingWhileACutWaitsForRoom_LeavesNothingHeld()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, RoomOf(400));
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            VpCutOutputReservation held = OpenUnrelatedReservation(root, 200);
            yield return null;
            int free = root.Storage.FreeVertexRoom;
            ProvisionalCutAsk ask = Ask(body, new float4(0f, 1f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(root.Geometry.StageOf(LastTransaction(root)), Is.Not.EqualTo(CutGeometryStage.Committed), "waiting");
            Assert.That(root.Storage.FreeVertexRoom, Is.EqualTo(free), "holding no room");

            // The other work gives its room back as the world ends (it is this case's own); the waiting cut is ended
            // with the world, never having taken any.
            Assert.That(root.Storage.TryCancelCutOutput(held), Is.True);
            yield return EndWorld(root);
            Assert.That(root.IsReleased, Is.True);
        }
    }
}
