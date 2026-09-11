using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the dedicated single-Run recovery worker: the caller
    /// never runs the recovery, one thread drives one coordinator, a partial
    /// release parks until a retry is requested, and every other failure stops
    /// the worker with the exception it retained.
    /// </summary>
    /// <remarks>
    /// Waits are bounded watchdogs over the service's own observable state, not
    /// sleeps or unbounded joins. Where a test needs the worker to be caught
    /// mid-recovery, the blocking primitives are created and released by the
    /// test itself: the block is entered right after Start and always lifted in
    /// a finally, and the thread is joined within a bounded watchdog before
    /// those primitives are disposed. No private field is rewritten.
    /// </remarks>
    public class NvencRunPublicationRecoveryWorkerServiceContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const int WatchdogMilliseconds = 10000;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<NvencRunPublicationRecoveryWorkerService> _services =
            new List<NvencRunPublicationRecoveryWorkerService>();

        private int _runIds;

        [TearDown]
        public void TearDown()
        {
            foreach (NvencRunPublicationRecoveryWorkerService service in _services)
            {
                try
                {
                    if (service.IsStopped)
                    {
                        service.Dispose();
                    }
                }
                catch (InvalidOperationException)
                {
                }
            }

            _services.Clear();

            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                try
                {
                    owner.Dispose();
                }
                catch (AggregateException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            _owners.Clear();
        }

        // ---- Construction ----

        [Test]
        public void Constructor_NullRecovery_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryWorkerService(null));
            Assert.That(ex.ParamName, Is.EqualTo("recovery"));
        }

        [Test]
        public void Constructor_StartsNoThreadAndTouchesNothing()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryWorkerService service = h.Service();

            Assert.That(service.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
            Assert.That(service.IsStopped, Is.False, "no thread exists yet.");
            Assert.That(service.TryGetFailure(out Exception failure), Is.False);
            Assert.That(failure, Is.Null);
            Assert.That(
                service.TryCollectTerminal(
                    out NvencRunPublicationRecoveryTerminalResult terminal),
                Is.False);
            Assert.That(terminal.IsValid, Is.False);
            Assert.That(service.TryRequestReleaseRetry(), Is.False);

            h.AssertNothingEntered("after construction");
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.CanRelease, Is.True);
        }

        // ---- The caller never runs the recovery ----

        [Test]
        public void Start_RunsTheRecoveryOnItsOwnThreadWithoutMakingTheCallerWait()
        {
            Harness h = MakeHarness();

            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            {
                h.Inspector.Entered = entered;
                h.Inspector.Gate = release;

                NvencRunPublicationRecoveryWorkerService service = h.Service();

                // Start is called from a helper thread and must return while
                // the inspection is still blocked. No duration is asserted:
                // the signal that it returned is what distinguishes an
                // asynchronous start from one that waits for the recovery.
                using (ManualResetEventSlim startReturned = new ManualResetEventSlim(false))
                {
                    Thread starter = new Thread(() =>
                    {
                        service.Start();
                        startReturned.Set();
                    })
                    {
                        IsBackground = true,
                        Name = "Zantetsu.Test.RecoveryWorkerStarter",
                    };

                    try
                    {
                        starter.Start();

                        Assert.That(
                            entered.Wait(WatchdogMilliseconds), Is.True,
                            "the worker thread must reach the inspection.");
                        Assert.That(
                            startReturned.Wait(WatchdogMilliseconds), Is.True,
                            "Start must return while the recovery is still blocked.");

                        Assert.That(h.Inspector.CallerThreadId, Is.Not.EqualTo(
                            starter.ManagedThreadId),
                            "the recovery must not run on the calling thread.");
                        Assert.That(h.Inspector.CallerThreadName,
                            Is.EqualTo(
                                NvencRunPublicationRecoveryWorkerService.WorkerThreadName));
                        Assert.That(service.State,
                            Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Running));
                        Assert.That(service.IsStopped, Is.False);
                    }
                    finally
                    {
                        release.Set();
                        Join(starter);
                    }

                    WaitUntilStopped(service);
                }
            }
        }

        [Test]
        public void Start_CalledTwice_KeepsOneThreadAndOneInspection()
        {
            Harness h = MakeHarness();
            NvencRunPublicationRecoveryWorkerService service = h.Service();

            service.Start();
            service.Start();

            WaitUntilStopped(service);

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Inspector.DistinctThreadCount, Is.EqualTo(1));
            Assert.That(service.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Completed));

            // A start after the worker stopped adds no second run either.
            service.Start();
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
        }

        // ---- Every classification completes and is collected once ----

        [Test]
        public void Worker_EachDisposition_CompletesAndIsCollectedExactlyOnce()
        {
            foreach (Shape shape in new[]
            {
                Shape.Recoverable, Shape.Incomplete, Shape.Collision, Shape.Deferred,
            })
            {
                Harness h = MakeHarness();
                h.Inspector.Shape = shape;
                NvencRunPublicationRecoveryWorkerService service = h.Service();

                service.Start();
                WaitUntilStopped(service);

                Assert.That(service.State,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Completed),
                    shape.ToString());
                Assert.That(service.TryGetFailure(out Exception failure), Is.False,
                    shape.ToString());
                Assert.That(failure, Is.Null, shape.ToString());

                Assert.That(
                    service.TryCollectTerminal(
                        out NvencRunPublicationRecoveryTerminalResult terminal),
                    Is.True, shape.ToString());
                Assert.That(terminal.IsValid, Is.True, shape.ToString());
                Assert.That(
                    ReferenceEquals(terminal.OpenOutcome, h.OpenOutcome), Is.True,
                    shape.ToString());
                Assert.That(
                    ReferenceEquals(terminal.OwnershipLease, h.Owner), Is.True,
                    shape.ToString());
                Assert.That(service.State,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Collected),
                    shape.ToString());

                // At most once.
                Assert.That(
                    service.TryCollectTerminal(
                        out NvencRunPublicationRecoveryTerminalResult again),
                    Is.False, shape.ToString());
                Assert.That(again.IsValid, Is.False, shape.ToString());
            }
        }

        [Test]
        public void TryCollectTerminal_BeforeCompletionWhileParkedAndWhenFaulted_IsFalse()
        {
            // Before completion: the worker is held inside the inspection.
            Harness running = MakeHarness();
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            {
                running.Inspector.Entered = entered;
                running.Inspector.Gate = release;
                NvencRunPublicationRecoveryWorkerService service = running.Service();

                try
                {
                    service.Start();
                    Assert.That(entered.Wait(WatchdogMilliseconds), Is.True);

                    Assert.That(
                        service.TryCollectTerminal(
                            out NvencRunPublicationRecoveryTerminalResult running_),
                        Is.False);
                    Assert.That(running_.IsValid, Is.False);
                }
                finally
                {
                    release.Set();
                }

                WaitUntilStopped(service);
            }

            // Parked for a release retry.
            Harness parked = MakeHarness(throwingFirstRelease: true);
            parked.Inspector.Shape = Shape.Incomplete;
            NvencRunPublicationRecoveryWorkerService parkedService = parked.Service();
            parkedService.Start();
            WaitUntilState(
                parkedService, NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry);

            try
            {
                Assert.That(
                    parkedService.TryCollectTerminal(
                        out NvencRunPublicationRecoveryTerminalResult whileParked),
                    Is.False);
                Assert.That(whileParked.IsValid, Is.False);
            }
            finally
            {
                // A failure above must not leave this worker parked forever.
                FinishParkedWorker(parkedService);
            }

            // Faulted.
            Harness faulted = MakeHarness();
            faulted.Inspector.Throw = new IOException("inspection faulted");
            NvencRunPublicationRecoveryWorkerService faultedService = faulted.Service();
            faultedService.Start();
            WaitUntilStopped(faultedService);

            Assert.That(faultedService.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Faulted));
            Assert.That(
                faultedService.TryCollectTerminal(
                    out NvencRunPublicationRecoveryTerminalResult whenFaulted),
                Is.False);
            Assert.That(whenFaulted.IsValid, Is.False);
        }

        // ---- Failures with the lock still held ----

        [Test]
        public void Worker_InspectionException_FaultsWithThatExactFailure()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("inspection faulted");
            h.Inspector.Throw = failure;
            NvencRunPublicationRecoveryWorkerService service = h.Service();

            service.Start();
            WaitUntilStopped(service);

            Assert.That(service.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Faulted));
            Assert.That(service.TryGetFailure(out Exception retained), Is.True);
            Assert.That(ReferenceEquals(retained, failure), Is.True);

            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.Owner.IsCreated, Is.True, "the lock was never touched.");
            Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(0));
            Assert.That(service.TryRequestReleaseRetry(), Is.False);
        }

        [Test]
        public void Worker_StageExceptionWhileTheLockIsHeld_FaultsWithoutRetrying()
        {
            Harness h = MakeHarness();
            h.Inspector.Shape = Shape.Incomplete;
            IOException failure = new IOException("cleanup faulted");
            h.IncompleteCleaner.Throw = failure;
            NvencRunPublicationRecoveryWorkerService service = h.Service();

            service.Start();
            WaitUntilStopped(service);

            Assert.That(service.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Faulted));
            Assert.That(service.TryGetFailure(out Exception retained), Is.True);
            Assert.That(ReferenceEquals(retained, failure), Is.True);

            // No automatic retry, and the lock is still fully held.
            Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
            Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(0));
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(service.TryRequestReleaseRetry(), Is.False);
        }

        [Test]
        public void Worker_UnusableReceiptAfterACompletedRelease_Faults()
        {
            Harness h = MakeHarness(foreignStopReceipt: true);
            h.Inspector.Shape = Shape.Collision;
            NvencRunPublicationRecoveryWorkerService service = h.Service();

            service.Start();
            WaitUntilStopped(service);

            Assert.That(service.State,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Faulted));
            Assert.That(service.TryGetFailure(out Exception retained), Is.True);
            Assert.That(retained, Is.Not.Null);

            // The lease is fully released, which is not a partial release, so
            // no retry is possible and the releaser is not touched again.
            Assert.That(h.Owner.IsReleaseComplete, Is.True);
            Assert.That(service.TryRequestReleaseRetry(), Is.False);
            Assert.That(h.StopReleaser.CallCount, Is.EqualTo(1));
            Assert.That(
                service.TryCollectTerminal(
                    out NvencRunPublicationRecoveryTerminalResult terminal),
                Is.False);
            Assert.That(terminal.IsValid, Is.False);
        }

        // ---- Partial release parks, and one request is one attempt ----

        [Test]
        public void Worker_PartialRelease_ParksAliveThenFinishesOnARequestedRetry()
        {
            Harness h = MakeHarness(throwingFirstRelease: true);
            h.Inspector.Shape = Shape.Incomplete;
            NvencRunPublicationRecoveryWorkerService service = h.Service();

            service.Start();
            WaitUntilState(service, NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry);

            bool retryRequested = false;
            try
            {
                // Parked, alive, and holding the exact exception the lease's own
                // disposal produced.
                Assert.That(service.IsStopped, Is.False);
                Assert.That(service.TryGetFailure(out Exception retained), Is.True);
                Assert.That(retained, Is.TypeOf<AggregateException>());
                Assert.That(
                    ((AggregateException)retained).InnerExceptions, Has.Count.EqualTo(1));

                Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
                Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
                Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(1));
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

                // Only a requested retry runs the second attempt.
                Assert.That(service.TryRequestReleaseRetry(), Is.True);
                retryRequested = true;
                WaitUntilStopped(service);

                Assert.That(service.State,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Completed));
                Assert.That(service.TryGetFailure(out Exception cleared), Is.False);
                Assert.That(cleared, Is.Null);

                // The upstream stages did not run again; only the release did.
                Assert.That(h.Inspector.CallCount, Is.EqualTo(1));
                Assert.That(h.IncompleteCleaner.CallCount, Is.EqualTo(1));
                Assert.That(h.IncompleteReleaser.CallCount, Is.EqualTo(2));

                // Only the handle that had not been released is disposed again.
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(2));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.Owner.IsReleaseComplete, Is.True);

                Assert.That(
                    service.TryCollectTerminal(
                        out NvencRunPublicationRecoveryTerminalResult terminal),
                    Is.True);
                Assert.That(terminal.IsIncompleteReleased, Is.True);
            }
            finally
            {
                // A failure anywhere above must not leave this worker parked.
                if (!retryRequested)
                {
                    FinishParkedWorker(service);
                }
            }
        }

        [Test]
        public void TryRequestReleaseRetry_OutsideTheParkedState_IsRefused()
        {
            // Not started.
            Harness notStarted = MakeHarness();
            NvencRunPublicationRecoveryWorkerService notStartedService = notStarted.Service();
            Assert.That(notStartedService.TryRequestReleaseRetry(), Is.False);
            Assert.That(notStarted.Inspector.CallCount, Is.EqualTo(0));

            // Running.
            Harness running = MakeHarness();
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            {
                running.Inspector.Entered = entered;
                running.Inspector.Gate = release;
                NvencRunPublicationRecoveryWorkerService service = running.Service();

                try
                {
                    service.Start();
                    Assert.That(entered.Wait(WatchdogMilliseconds), Is.True);
                    Assert.That(service.TryRequestReleaseRetry(), Is.False);
                }
                finally
                {
                    release.Set();
                }

                WaitUntilStopped(service);

                // Completed.
                Assert.That(service.TryRequestReleaseRetry(), Is.False);
            }

            // Parked, then refused a second time while that attempt is in
            // flight.
            Harness parked = MakeHarness(throwingFirstRelease: true);
            parked.Inspector.Shape = Shape.Incomplete;
            NvencRunPublicationRecoveryWorkerService parkedService = parked.Service();
            parkedService.Start();
            WaitUntilState(
                parkedService, NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry);

            bool retryRequested = false;
            try
            {
                Assert.That(parkedService.TryRequestReleaseRetry(), Is.True);
                retryRequested = true;
                Assert.That(parkedService.TryRequestReleaseRetry(), Is.False,
                    "one request is one attempt.");

                WaitUntilStopped(parkedService);
                Assert.That(parked.IncompleteReleaser.CallCount, Is.EqualTo(2));
            }
            finally
            {
                if (!retryRequested)
                {
                    FinishParkedWorker(parkedService);
                }
            }

            // Faulted.
            Harness faulted = MakeHarness();
            faulted.Inspector.Throw = new IOException("inspection faulted");
            NvencRunPublicationRecoveryWorkerService faultedService = faulted.Service();
            faultedService.Start();
            WaitUntilStopped(faultedService);
            Assert.That(faultedService.TryRequestReleaseRetry(), Is.False);
        }

        // ---- Dispose ----

        [Test]
        public void Dispose_BeforeStartAndAfterStop_IsAllowedAndIdempotent()
        {
            Harness notStarted = MakeHarness();
            NvencRunPublicationRecoveryWorkerService beforeStart = notStarted.Service();

            beforeStart.Dispose();
            beforeStart.Dispose();
            Assert.That(beforeStart.IsStopped, Is.True);

            Harness stopped = MakeHarness();
            NvencRunPublicationRecoveryWorkerService afterStop = stopped.Service();
            afterStop.Start();
            WaitUntilStopped(afterStop);

            afterStop.Dispose();
            afterStop.Dispose();
        }

        [Test]
        public void Dispose_WhileTheWorkerIsAlive_IsRefused()
        {
            // Running.
            Harness running = MakeHarness();
            using (ManualResetEventSlim entered = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            {
                running.Inspector.Entered = entered;
                running.Inspector.Gate = release;
                NvencRunPublicationRecoveryWorkerService service = running.Service();

                try
                {
                    service.Start();
                    Assert.That(entered.Wait(WatchdogMilliseconds), Is.True);
                    Assert.Throws<InvalidOperationException>(() => service.Dispose());
                }
                finally
                {
                    release.Set();
                }

                WaitUntilStopped(service);
                service.Dispose();
            }

            // Parked for a release retry: alive, and never force-stopped.
            Harness parked = MakeHarness(throwingFirstRelease: true);
            parked.Inspector.Shape = Shape.Incomplete;
            NvencRunPublicationRecoveryWorkerService parkedService = parked.Service();
            parkedService.Start();
            WaitUntilState(
                parkedService, NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry);

            bool retryRequested = false;
            try
            {
                Assert.Throws<InvalidOperationException>(() => parkedService.Dispose());
                Assert.That(parkedService.State,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry));

                Assert.That(parkedService.TryRequestReleaseRetry(), Is.True);
                retryRequested = true;
                WaitUntilStopped(parkedService);
                parkedService.Dispose();
            }
            finally
            {
                if (!retryRequested)
                {
                    FinishParkedWorker(parkedService);
                }
            }
        }

        // ---- Fixture helpers ----

        /// <summary>
        /// Releases a parked worker and confirms its physical stop, so no test
        /// - including one that failed an assertion - leaves a thread waiting
        /// on a signal. Best effort: it never masks the original failure.
        /// </summary>
        private static void FinishParkedWorker(
            NvencRunPublicationRecoveryWorkerService service)
        {
            try
            {
                if (service.State
                    == NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry)
                {
                    service.TryRequestReleaseRetry();
                }

                Stopwatch watchdog = Stopwatch.StartNew();
                while (!service.IsStopped
                    && watchdog.ElapsedMilliseconds <= WatchdogMilliseconds)
                {
                    Thread.Yield();
                }

                if (service.IsStopped)
                {
                    service.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>Bounded join for a helper thread this fixture started.</summary>
        private static void Join(Thread thread)
        {
            Assert.That(
                thread.Join(WatchdogMilliseconds), Is.True,
                "the helper thread did not finish in time.");
        }

        /// <summary>
        /// Bounded watchdog over the service's own physical stop: no sleep, and
        /// no unbounded join.
        /// </summary>
        private static void WaitUntilStopped(NvencRunPublicationRecoveryWorkerService service)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (!service.IsStopped)
            {
                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail("the recovery worker did not physically stop in time.");
                }

                Thread.Yield();
            }
        }

        private static void WaitUntilState(
            NvencRunPublicationRecoveryWorkerService service,
            NvencRunPublicationRecoveryWorkerState expected)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (service.State != expected)
            {
                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail(
                        "the recovery worker did not reach " + expected + " in time; it is "
                        + service.State + ".");
                }

                Thread.Yield();
            }
        }

        private Harness MakeHarness(
            bool throwingFirstRelease = false, bool foreignStopReceipt = false)
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout,
                throwingFirstRelease,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out CountingHandle firstHandle,
                out CountingHandle secondHandle);

            return new Harness(
                this, layout, openOutcome, owner, firstHandle, secondHandle, foreignStopReceipt);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            bool throwingFirstRelease,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(
                        MakeRootObservation(
                            CaptureRunRootRole.Staging,
                            binding.StagingInitialization,
                            binding.StagingReady,
                            hasNonMarkerEntry: true),
                        MakeRootObservation(
                            CaptureRunRootRole.Final,
                            binding.FinalInitialization,
                            binding.FinalReady,
                            hasNonMarkerEntry: true)),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath, throwingFirstRelease);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);

            CaptureRunLockIdentityEvidence identity =
                CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4));

            Assert.That(result.Status,
                Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired),
                "the fixture must reach a publication-recovery outcome.");

            return new CaptureRunInitializationOpenOutcome(result, null, identity);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker init,
            CaptureRunReadyMarker ready,
            bool hasNonMarkerEntry)
        {
            return new CaptureRunInitializationRootObservation(
                role, true, false, CaptureRunMarkerObservationStatus.Canonical, init,
                false, CaptureRunMarkerObservationStatus.Canonical, ready,
                hasNonMarkerEntry, false, false);
        }

        private CaptureRunRootLayout MakeLayout()
        {
            string root = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-fake"
                : "/zantetsuken-fake";
            _runIds++;

            return new CaptureRunRootLayout(
                Path.Combine(root, "staging-" + _runIds),
                Path.Combine(root, "final-" + _runIds),
                1);
        }

        private enum Shape
        {
            Recoverable,
            Incomplete,
            Collision,
            Deferred,
        }

        /// <summary>
        /// One Run holding its lease, the recovery coordinator built over
        /// counting fakes, and the worker service under test.
        /// </summary>
        private sealed class Harness
        {
            private readonly NvencRunPublicationRecoveryWorkerServiceContractTests _fixture;
            private readonly bool _foreignStopReceipt;

            internal Harness(
                NvencRunPublicationRecoveryWorkerServiceContractTests fixture,
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease owner,
                CountingHandle firstHandle,
                CountingHandle secondHandle,
                bool foreignStopReceipt)
            {
                _fixture = fixture;
                Layout = layout;
                OpenOutcome = openOutcome;
                Owner = owner;
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                _foreignStopReceipt = foreignStopReceipt;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal CountingHandle FirstHandle { get; }

            internal CountingHandle SecondHandle { get; }

            internal FakePublicationRecoveryInspector Inspector { get; } =
                new FakePublicationRecoveryInspector();

            internal FakeCaptureIndexInspector CaptureIndexInspector { get; } =
                new FakeCaptureIndexInspector();

            internal FakeCommitter Committer { get; } = new FakeCommitter();

            internal FakeCompleter Completer { get; } = new FakeCompleter();

            internal FakeCaptureCompleteCleaner CaptureCompleteCleaner { get; } =
                new FakeCaptureCompleteCleaner();

            internal FakeCaptureCompleteReleaser CaptureCompleteReleaser { get; } =
                new FakeCaptureCompleteReleaser();

            internal FakeIncompleteCleaner IncompleteCleaner { get; } =
                new FakeIncompleteCleaner();

            internal FakeIncompleteReleaser IncompleteReleaser { get; } =
                new FakeIncompleteReleaser();

            internal FakeStopReleaser StopReleaser { get; } = new FakeStopReleaser();

            /// <summary>
            /// The worker service over this Run's recovery coordinator,
            /// registered so the fixture can dispose it once it has stopped.
            /// </summary>
            internal NvencRunPublicationRecoveryWorkerService Service()
            {
                if (_foreignStopReceipt)
                {
                    FakeStopReleaser foreign = new FakeStopReleaser();
                    StopReleaser.Forge = (self, operation) =>
                        NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                            foreign, operation);
                }

                NvencRunPublicationRecoveryEntryCoordinator entry =
                    new NvencRunPublicationRecoveryEntryCoordinator(
                        new NvencRunPublicationRecoveryOrchestrationCoordinator(
                            new NvencRunPublicationRecoveryInspectionExecutionCoordinator(
                                Inspector)),
                        new NvencRunCaptureIndexRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(
                                CaptureIndexInspector)),
                        new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                    Committer)),
                            new NvencRunCaptureCompleteRecoveryExecutionCoordinator(Completer)),
                        new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                            new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                                CaptureCompleteCleaner)),
                        new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                            CaptureCompleteReleaser),
                        new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                            new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                                IncompleteCleaner)),
                        new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                            IncompleteReleaser),
                        new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                            StopReleaser));

                NvencRunPublicationRecoveryWorkerService service =
                    new NvencRunPublicationRecoveryWorkerService(
                        new NvencRunPublicationRecoveryCoordinator(entry, OpenOutcome, Owner));
                _fixture._services.Add(service);
                return service;
            }

            internal void AssertNothingEntered(string message)
            {
                Assert.That(Inspector.CallCount, Is.EqualTo(0), message);
                Assert.That(CaptureIndexInspector.CallCount, Is.EqualTo(0), message);
                Assert.That(Committer.CallCount, Is.EqualTo(0), message);
                Assert.That(Completer.CallCount, Is.EqualTo(0), message);
                Assert.That(CaptureCompleteCleaner.CallCount, Is.EqualTo(0), message);
                Assert.That(CaptureCompleteReleaser.CallCount, Is.EqualTo(0), message);
                Assert.That(IncompleteCleaner.CallCount, Is.EqualTo(0), message);
                Assert.That(IncompleteReleaser.CallCount, Is.EqualTo(0), message);
                Assert.That(StopReleaser.CallCount, Is.EqualTo(0), message);
                Assert.That(FirstHandle.DisposeCallCount, Is.EqualTo(0), message);
                Assert.That(SecondHandle.DisposeCallCount, Is.EqualTo(0), message);
            }
        }

        /// <summary>
        /// Reports one classification shape, records which thread called it,
        /// and can be held there by a gate the test owns.
        /// </summary>
        private sealed class FakePublicationRecoveryInspector
            : INvencRunPublicationRecoveryInspector
        {
            private readonly HashSet<int> _threadIds = new HashSet<int>();

            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal int DistinctThreadCount
            {
                get
                {
                    lock (_threadIds)
                    {
                        return _threadIds.Count;
                    }
                }
            }

            internal volatile int CallerThreadId;

            internal volatile string CallerThreadName;

            internal Exception Throw { get; set; }

            internal Shape Shape { get; set; } = Shape.Recoverable;

            /// <summary>Set by the inspector once it is inside the call.</summary>
            internal ManualResetEventSlim Entered { get; set; }

            /// <summary>Waited on by the inspector; released by the test.</summary>
            internal ManualResetEventSlim Gate { get; set; }

            public NvencRunPublicationRecoveryInspectionSnapshot Inspect(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                CallerThreadId = Thread.CurrentThread.ManagedThreadId;
                CallerThreadName = Thread.CurrentThread.Name;

                lock (_threadIds)
                {
                    _threadIds.Add(Thread.CurrentThread.ManagedThreadId);
                }

                Entered?.Set();
                Gate?.Wait();

                if (Throw != null)
                {
                    throw Throw;
                }

                return MakeSnapshot(operation);
            }

            private NvencRunPublicationRecoveryInspectionSnapshot MakeSnapshot(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                if (Shape == Shape.Incomplete)
                {
                    return new NvencRunPublicationRecoveryInspectionSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default);
                }

                CapturePublicationPlan plan = MakePlan(operation);

                if (Shape == Shape.Collision)
                {
                    return new NvencRunPublicationRecoveryInspectionSnapshot(
                        operation,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        true,
                        default);
                }

                return new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan,
                    false,
                    Shape == Shape.Deferred
                        ? new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Deferred,
                            CaptureArtifactVerificationStatus.None,
                            CaptureArtifactVerificationFailureReason.BufferUnavailable,
                            0)
                        : new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength));
            }

            private static CapturePublicationPlan MakePlan(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CaptureArtifactDescriptor[] artifacts = new[]
                {
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        ArtifactId, ChunkByteLength, Hash64),
                };

                CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
                }

                return new CapturePublicationPlan(
                    operation.TestRunId, operation.RunInitializationId, Hash64, artifacts, entries);
            }
        }

        private sealed class FakeCaptureIndexInspector : INvencRunCaptureIndexRecoveryInspector
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexRecoveryInspectionSnapshot Inspect(
                NvencRunCaptureIndexRecoveryInspectionOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                return new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    operation,
                    NvencRunCaptureIndexObservationStatus.MatchesAuthoritative,
                    NvencRunCaptureIndexObservationStatus.Absent);
            }
        }

        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
            }
        }

        private sealed class FakeCompleter : INvencRunCaptureCompleteRecoveryCompleter
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteRecoveryReceipt Complete(
                NvencRunCaptureCompleteRecoveryOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                return NvencRunCaptureCompleteRecoveryReceipt.Completed(this, operation);
            }
        }

        private sealed class FakeCaptureCompleteCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                return NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(
                    this, operation);
            }
        }

        private sealed class FakeIncompleteCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal Exception Throw { get; set; }

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Throw != null)
                {
                    throw Throw;
                }

                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                    this, operation);
            }
        }

        private sealed class FakeCaptureCompleteReleaser
            : INvencRunCaptureCompleteRecoveryOwnershipReleaser
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt Release(
                NvencRunCaptureCompleteRecoveryOwnershipReleaseOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                operation.OwnershipLease.Dispose();

                return NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt.Released(
                    this, operation);
            }
        }

        private sealed class FakeIncompleteReleaser
            : INvencRunPublicationRecoveryIncompleteOwnershipReleaser
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                operation.OwnershipLease.Dispose();

                return NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt.Released(
                    this, operation);
            }
        }

        private sealed class FakeStopReleaser : INvencRunPublicationRecoveryStopOwnershipReleaser
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal Func<
                FakeStopReleaser,
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation,
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt> Forge
            { get; set; }

            public NvencRunPublicationRecoveryStopOwnershipReleaseReceipt Release(
                NvencRunPublicationRecoveryStopOwnershipReleaseOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                operation.OwnershipLease.Dispose();

                return Forge != null
                    ? Forge(this, operation)
                    : NvencRunPublicationRecoveryStopOwnershipReleaseReceipt.Released(
                        this, operation);
            }
        }

        /// <summary>
        /// A lock handle that counts its own releases and can fail the first
        /// one, producing the ordinary API's partial release.
        /// </summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private readonly bool _throwFirstRelease;
            private int _disposeCalls;

            internal CountingHandle(string lockPath, bool throwFirstRelease = false)
            {
                LockPath = lockPath;
                _throwFirstRelease = throwFirstRelease;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => Volatile.Read(ref _disposeCalls);

            public void Dispose()
            {
                int calls = Interlocked.Increment(ref _disposeCalls);

                if (_throwFirstRelease && calls == 1)
                {
                    throw new InvalidOperationException("First release fails.");
                }
            }
        }

        private sealed class FakeRecoveryInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            internal FakeRecoveryInspector(
                CaptureRunInitializationRootObservation staging,
                CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
                CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }
    }
}
