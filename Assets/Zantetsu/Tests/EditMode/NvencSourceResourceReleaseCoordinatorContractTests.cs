using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 source surface / GPU conversion sync
    /// credit early release boundary. Uses tiny render target fixtures; no real
    /// GPU completion, fence, query, command buffer, NVENC, or native resource
    /// is used.
    /// </summary>
    public class NvencSourceResourceReleaseCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        [Test]
        public void Release_NotCompleted_NoChanges()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool), Is.True);

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);

                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(record.Surface.IsCreated, Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool), Is.True);
                Assert.That(h.WorkPool.OccupiedCount, Is.EqualTo(1));
                Assert.That(h.SamplePool.OccupiedCount, Is.EqualTo(1));
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Release_AfterEvidence_ReleasesSurfaceAndSyncOnly()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.True);

                // The sync credit is returned immediately on the worker.
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(0));
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.False);

                // The surface is handed off to the main thread, not yet
                // released, so the main-thread-only render pool is not touched
                // from the worker.
                Assert.That(record.Surface.IsCreated, Is.True);
                Assert.That(record.Surface.IsBackendOwned, Is.True);

                // Work and Sample remain reserved for the later stages.
                Assert.That(h.WorkPool.OccupiedCount, Is.EqualTo(1));
                Assert.That(h.SamplePool.OccupiedCount, Is.EqualTo(1));
                Assert.That(h.WorkPool.IsActive(record.WorkSlot), Is.True);
                Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.True);

                // Returning the sync credit makes the record invalid as expected.
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool), Is.False);

                // The main thread drains and releases the handed-off surface.
                h.DrainSurfaceReturns();
                Assert.That(record.Surface.IsCreated, Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.False);
            }
        }

        [Test]
        public void Release_DoesNotAffectOtherSevenWorks()
        {
            using (Harness h = Harness.Create(8))
            {
                NvencSubmissionRecord[] records = new NvencSubmissionRecord[8];
                for (int i = 0; i < 8; i++)
                {
                    records[i] = h.CreateRecord(i + 1);
                }

                h.Source.MarkCompleted(records[0].WorkToken);
                Assert.That(h.Coordinator.TryReleaseSourceResources(records[0]), Is.True);

                // The released work's sync credit is returned; its surface is
                // handed off, not yet released.
                Assert.That(h.SyncPool.IsActive(records[0].SyncSlot), Is.False);
                Assert.That(records[0].Surface.IsBackendOwned, Is.True);

                for (int i = 1; i < 8; i++)
                {
                    Assert.That(records[i].Surface.IsBackendOwned, Is.True);
                    Assert.That(h.SyncPool.IsActive(records[i].SyncSlot), Is.True);
                    Assert.That(records[i].IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool), Is.True);
                }

                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(7));

                // The main thread drains and releases only the handed-off surface.
                h.DrainSurfaceReturns();
                Assert.That(records[0].Surface.IsCreated, Is.False);

                for (int i = 1; i < 8; i++)
                {
                    Assert.That(records[i].Surface.IsBackendOwned, Is.True);
                }
            }
        }

        [Test]
        public void Release_RejectsForeignSource()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                FakeSourceReadCompletedSource foreign = new FakeSourceReadCompletedSource();
                h.Source.MarkCompleted(record.WorkToken);
                h.Source.OverrideEvidence(
                    new NvencSourceReadCompletedEvidence(foreign, record.WorkToken, record.SyncSlot, record.Surface));

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Release_RejectsForeignWork()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                CaptureFrameWorkToken other = new CaptureFrameWorkToken(
                    h.Owner, record.WorkToken.SlotIndex, record.WorkToken.Generation, 1, 999);
                h.Source.MarkCompleted(record.WorkToken);
                h.Source.OverrideEvidence(
                    new NvencSourceReadCompletedEvidence(h.Source, other, record.SyncSlot, record.Surface));

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Release_RejectsDifferentSurface()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                NvencSubmissionRecord other = h.CreateRecord(8);
                h.Source.MarkCompleted(record.WorkToken);
                h.Source.OverrideEvidence(
                    new NvencSourceReadCompletedEvidence(h.Source, record.WorkToken, record.SyncSlot, other.Surface));

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(other.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(2));
            }
        }

        [Test]
        public void Release_RejectsStaleOrRerentedSyncLease()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);

                Assert.That(h.SyncPool.TryReturn(record.SyncSlot), Is.True);
                Assert.That(h.SyncPool.TryRent(out NvencGpuConversionSyncLease rerented), Is.True);
                Assert.That(rerented.SlotIndex, Is.EqualTo(record.SyncSlot.SlotIndex));

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(rerented), Is.True);
            }
        }

        [Test]
        public void Release_DoubleApply_DoesNotReleaseNewGeneration()
        {
            using (Harness h = Harness.Create(2))
            {
                NvencSubmissionRecord first = h.CreateRecord(7);
                h.Source.MarkCompleted(first.WorkToken);
                Assert.That(h.Coordinator.TryReleaseSourceResources(first), Is.True);

                NvencSubmissionRecord second = h.CreateRecord(8);
                Assert.That(second.SyncSlot.SlotIndex, Is.EqualTo(first.SyncSlot.SlotIndex));
                Assert.That(second.SyncSlot.Generation, Is.EqualTo(first.SyncSlot.Generation + 1));

                // Re-applying the old record must not free the new generation.
                Assert.That(h.Coordinator.TryReleaseSourceResources(first), Is.False);
                Assert.That(second.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(second.SyncSlot), Is.True);
            }
        }

        [Test]
        public void Release_Draining_AllowsRelease()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                Assert.That(h.State.TryBeginDrain(), Is.True);

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.True);
                Assert.That(record.Surface.IsCreated, Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(0));
                Assert.That(h.WorkPool.OccupiedCount, Is.EqualTo(1));
                Assert.That(h.SamplePool.OccupiedCount, Is.EqualTo(1));

                h.DrainSurfaceReturns();
                Assert.That(record.Surface.IsCreated, Is.False);
            }
        }

        [Test]
        public void Release_Poisoned_RetainsBoth()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(record.Surface.IsCreated, Is.True);
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Release_GuardContention_FailsNonWaitingThenRetrySucceeds()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);

                bool acquired = false;
                using (ManualResetEventSlim held = new ManualResetEventSlim(false))
                using (ManualResetEventSlim release = new ManualResetEventSlim(false))
                {
                    Thread holder = new Thread(() =>
                    {
                        acquired = h.State.TryBeginResourceResolution();
                        held.Set();
                        release.Wait(WatchdogTimeoutMs);
                        if (acquired)
                        {
                            h.State.EndResourceResolution();
                        }
                    });
                    holder.IsBackground = true;
                    holder.Start();

                    Assert.That(held.Wait(WatchdogTimeoutMs), Is.True, "Holder did not acquire the gate in time.");
                    Assert.That(acquired, Is.True);

                    // Gate is held by another thread: fail without waiting, no change.
                    Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                    Assert.That(record.Surface.IsBackendOwned, Is.True);
                    Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(1));

                    release.Set();
                    Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "Holder did not finish in time.");
                }

                // Retry after the gate is free succeeds.
                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.True);
                Assert.That(record.Surface.IsCreated, Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(0));

                h.DrainSurfaceReturns();
                Assert.That(record.Surface.IsCreated, Is.False);
            }
        }

        [Test]
        public void Release_PoisonRace_ReleaseLinearizesFirst_NoPartialRelease()
        {
            using (Harness h = Harness.Create(1))
            {
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);

                // The main thread holds the resource-resolution gate, so a
                // concurrent poison blocks until the release has fully
                // linearized (both resources freed).
                Assert.That(h.State.TryBeginResourceResolution(), Is.True);

                bool poisonResult = false;
                using (ManualResetEventSlim poisonStarted = new ManualResetEventSlim(false))
                {
                    Thread poisonThread = new Thread(() =>
                    {
                        poisonStarted.Set();
                        poisonResult = h.State.TryPoison();
                    });
                    poisonThread.IsBackground = true;
                    poisonThread.Start();

                    Assert.That(poisonStarted.Wait(WatchdogTimeoutMs), Is.True, "Poison thread did not start in time.");

                    // Poison is blocked on the gate while the release runs.
                    Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.True);
                    Assert.That(h.State.IsPoisoned, Is.False);

                    h.State.EndResourceResolution();
                    Assert.That(poisonThread.Join(WatchdogTimeoutMs), Is.True, "Poison thread did not finish in time.");
                }

                Assert.That(poisonResult, Is.True);
                Assert.That(h.State.IsPoisoned, Is.True);

                // Release linearized first: the sync credit was returned and the
                // surface was handed off, never just one of them.
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.False);
                Assert.That(record.Surface.IsCreated, Is.True);
                Assert.That(record.Surface.IsBackendOwned, Is.True);

                // The main thread later drains and releases the surface.
                h.DrainSurfaceReturns();
                Assert.That(record.Surface.IsCreated, Is.False);
            }
        }

        [Test]
        public void Release_AllEightWorks_IndividuallyReleaseTheirOwnSurfaceAndSync()
        {
            using (Harness h = Harness.Create(8))
            {
                NvencSubmissionRecord[] records = new NvencSubmissionRecord[8];
                for (int i = 0; i < 8; i++)
                {
                    records[i] = h.CreateRecord(i + 1);
                }

                for (int i = 0; i < 8; i++)
                {
                    h.Source.MarkCompleted(records[i].WorkToken);
                    Assert.That(h.Coordinator.TryReleaseSourceResources(records[i]), Is.True);

                    Assert.That(h.SyncPool.IsActive(records[i].SyncSlot), Is.False);
                    Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(7 - i));

                    // The freed credit is immediately reusable.
                    Assert.That(h.SyncPool.TryRent(out NvencGpuConversionSyncLease rerented), Is.True);
                    Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(8 - i));
                    Assert.That(h.SyncPool.TryReturn(rerented), Is.True);
                    Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(7 - i));

                    // The handed-off surface is released on the main thread.
                    h.DrainSurfaceReturns();
                    Assert.That(records[i].Surface.IsCreated, Is.False);

                    for (int j = i + 1; j < 8; j++)
                    {
                        Assert.That(records[j].Surface.IsBackendOwned, Is.True);
                        Assert.That(h.SyncPool.IsActive(records[j].SyncSlot), Is.True);
                    }
                }

                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(0));
                Assert.That(h.WorkPool.OccupiedCount, Is.EqualTo(8));
                Assert.That(h.SamplePool.OccupiedCount, Is.EqualTo(8));
            }
        }

        [Test]
        public void Release_BoundaryFull_FailsNonWaitingWithoutPartialRelease()
        {
            using (Harness h = Harness.Create(1))
            {
                // Fill the boundary to capacity with placeholder records.
                for (int i = 0; i < h.Boundary.Capacity; i++)
                {
                    Assert.That(h.Boundary.TryEnqueue(default), Is.True);
                }

                Assert.That(h.Boundary.CanEnqueue, Is.False);

                // A completed work cannot be handed off: fail non-waiting with
                // no partial release (surface not handed off, sync retained).
                NvencSubmissionRecord record = h.CreateRecord(7);
                h.Source.MarkCompleted(record.WorkToken);
                Assert.That(h.Coordinator.TryReleaseSourceResources(record), Is.False);
                Assert.That(record.Surface.IsBackendOwned, Is.True);
                Assert.That(h.SyncPool.IsActive(record.SyncSlot), Is.True);
                Assert.That(h.SyncPool.OccupiedCount, Is.EqualTo(1));

                // Discard the placeholder records so the harness drains cleanly.
                while (h.Boundary.TryDequeue(out _))
                {
                }
            }
        }

        [Test]
        public void ProductionSources_NoSubmitToOutputNoFrameCompletionNoNativeNoWait()
        {
            string directory = RuntimeDirectory();
            string text =
                File.ReadAllText(Path.Combine(directory, "INvencSourceReadCompletedSource.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencSourceReadCompletedEvidence.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencSourceResourceReleaseCoordinator.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencSourceSurfaceReturnBoundary.cs"));

            Assert.That(text, Does.Not.Contain("NvencSubmitToOutputRecord"));
            Assert.That(text, Does.Not.Contain("FrameCompletion"));
            Assert.That(text, Does.Not.Contain("NvEnc"));
            Assert.That(text, Does.Not.Contain("DllImport"));
            Assert.That(text, Does.Not.Contain("IntPtr"));
            Assert.That(text, Does.Not.Contain("SafeHandle"));
            Assert.That(text, Does.Not.Contain("GraphicsFence"));
            Assert.That(text, Does.Not.Contain("CommandBuffer"));
            Assert.That(text, Does.Not.Contain("AsyncGPUReadback"));
            Assert.That(text, Does.Not.Contain("RenderTexture"));
            Assert.That(text, Does.Not.Contain("lock ("));
            Assert.That(text, Does.Not.Contain("Monitor"));
            Assert.That(text, Does.Not.Contain("SpinWait"));
            Assert.That(text, Does.Not.Contain("ManualResetEvent"));
            Assert.That(text, Does.Not.Contain("AutoResetEvent"));
            Assert.That(text, Does.Not.Contain("WaitHandle"));
            Assert.That(text, Does.Not.Contain("new Thread"));
            Assert.That(text, Does.Not.Contain("ThreadPool"));
            Assert.That(text, Does.Not.Contain("Thread.Sleep"));
            Assert.That(text, Does.Not.Contain("Task"));
            Assert.That(text, Does.Not.Contain("File."));
            Assert.That(text, Does.Not.Contain("Directory."));
            Assert.That(text, Does.Not.Contain("new []"));
            Assert.That(text, Does.Not.Contain("new List"));
            Assert.That(text, Does.Not.Contain("new Dictionary"));
        }

        private static CaptureFrameRenderTargetPool MakeRenderPool(int capacity)
        {
            return new CaptureFrameRenderTargetPool(
                capacity, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            private readonly List<CaptureFrameWorkToken> _completed = new List<CaptureFrameWorkToken>();
            private INvencSourceReadCompletedSource _evidenceSource;
            private NvencSourceReadCompletedEvidence? _overridden;

            internal FakeSourceReadCompletedSource()
            {
                _evidenceSource = this;
            }

            internal void MarkCompleted(in CaptureFrameWorkToken token)
            {
                _completed.Add(token);
            }

            internal void OverrideEvidence(NvencSourceReadCompletedEvidence evidence)
            {
                _overridden = evidence;
            }

            public bool TryGetEvidence(
                in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                foreach (CaptureFrameWorkToken token in _completed)
                {
                    if (token.IdenticalTo(record.WorkToken))
                    {
                        evidence = _overridden ?? NvencSourceReadCompletedEvidence.Create(_evidenceSource, record);
                        return true;
                    }
                }

                evidence = default;
                return false;
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State { get; }
            internal NvencCaptureWorkSlotPool WorkPool { get; }
            internal NvencEncodeSampleSlotPool SamplePool { get; }
            internal NvencGpuConversionSyncPool SyncPool { get; }
            internal Guid Owner { get; }
            internal CaptureFrameRenderTargetPool RenderPool { get; }
            internal FakeSourceReadCompletedSource Source { get; }
            internal NvencSourceSurfaceReturnBoundary Boundary { get; }
            internal NvencSourceResourceReleaseCoordinator Coordinator { get; }

            private readonly List<NvencSubmissionRecord> _records = new List<NvencSubmissionRecord>();

            private Harness(int renderCapacity)
            {
                State = new NvencCaptureProcessState();
                WorkPool = new NvencCaptureWorkSlotPool(State);
                SamplePool = new NvencEncodeSampleSlotPool(State);
                SyncPool = new NvencGpuConversionSyncPool(State);
                Owner = Guid.NewGuid();
                RenderPool = MakeRenderPool(renderCapacity);
                Source = new FakeSourceReadCompletedSource();
                Boundary = new NvencSourceSurfaceReturnBoundary();
                Coordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkPool, SamplePool, SyncPool, Source, Boundary, Owner);
            }

            internal static Harness Create(int renderCapacity)
            {
                return new Harness(renderCapacity);
            }

            internal void DrainSurfaceReturns()
            {
                Boundary.DrainAll(Owner);
            }

            internal NvencSubmissionRecord CreateRecord(long frameId)
            {
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease work), Is.True);
                Assert.That(SamplePool.TryRent(out NvencEncodeSampleSlotLease sample), Is.True);
                Assert.That(SyncPool.TryRent(out NvencGpuConversionSyncLease sync), Is.True);
                Assert.That(RenderPool.TryRent(out CaptureFrameRenderTargetLease rt), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Owner, work.SlotIndex, work.Generation, 1, frameId);
                CaptureSurfaceLease surface = new CaptureSurfaceLease(RenderPool, rt);
                surface.TransferToBackend(Owner, token);

                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    Owner, token, work, sample, sync, surface, WorkPool, SamplePool, SyncPool);
                _records.Add(record);
                return record;
            }

            public void Dispose()
            {
                Boundary.DrainAll(Owner);

                foreach (NvencSubmissionRecord record in _records)
                {
                    if (record.Surface != null && record.Surface.IsCreated)
                    {
                        if (record.Surface.IsBackendOwned)
                        {
                            record.Surface.ReleaseFromBackend(Owner, record.WorkToken);
                        }
                        else
                        {
                            record.Surface.Dispose();
                        }
                    }

                    SyncPool.TryReturn(record.SyncSlot);
                    SamplePool.TryReturn(record.SampleSlot);
                    WorkPool.TryReturn(record.WorkSlot);
                }

                RenderPool.Dispose();
            }
        }
    }
}
