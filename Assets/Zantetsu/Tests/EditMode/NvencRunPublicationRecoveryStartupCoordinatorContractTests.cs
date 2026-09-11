using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the application-side owner of Phase 0.11 recovery:
    /// which initialization outcome starts a worker and which does not, that
    /// exactly one worker is started per Run and that starting it never makes
    /// the caller wait, how a terminal is collected once and frees the slot,
    /// and what happens to the lease when composition is refused or the
    /// recovery faults.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The initialization entry, the worker factory, and every coordinator the
    /// factory wires are the production types. The fixture supplies the two
    /// boundaries it must drive: a lock backend whose handles count their
    /// disposals, and the artifact opener the recovery inspects through. The
    /// generic initialization recovery inspector is a fixture-local fake, as no
    /// production one exists; it returns a canonical observation pair and reads
    /// nothing.
    /// </para>
    /// <para>
    /// The contract tests use two recovery shapes as vehicles for reaching a
    /// terminal and a fault: a plan the opener reports as an unreadable
    /// finished document, which stops without changing anything, and a plan
    /// whose open fails. The classification table, the cleaner's own delete and
    /// flush order, and the partial release retry's failure shapes belong to
    /// the existing worker, cleaner, and managed end-to-end fixtures and are
    /// not repeated here.
    /// </para>
    /// <para>
    /// Two integration sentinels at the end run over a real temporary tree with
    /// the production no-follow opener, commit and cleanup filesystem, and OS
    /// lock backend, so the owner's two remaining terminal branches are
    /// connected end to end: an Incomplete Run that is cleaned up and released,
    /// and a Deferred verification that stops without touching the tree. The
    /// collision branch is covered above and the CaptureComplete branch by the
    /// managed end-to-end fixture.
    /// </para>
    /// </remarks>
    public class NvencRunPublicationRecoveryStartupCoordinatorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string FreshInitId = "fedcba9876543210fedcba9876543210";

        private const string WriterHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string PrecommitTemporaryName =
            "publication.plan.nvenc-precommit.tmp";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const string PartialChunkName = "chunk-0.nvenc-idr-chunk-v1.h264.partial";

        private const int VerificationBufferLength = 64 * 1024;

        private const int MaximumRootEntryCount = 4;

        private const int WatchdogMilliseconds = 10000;

        private const int GateWaitMilliseconds = 30000;

        private static bool IsWindows =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private readonly List<IDisposable> _ownedDisposables = new List<IDisposable>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _ownedDisposables.Count - 1; i >= 0; i--)
            {
                try
                {
                    _ownedDisposables[i].Dispose();
                }
                catch (Exception)
                {
                }
            }

            _ownedDisposables.Clear();

            foreach (string sandbox in _sandboxes)
            {
                try
                {
                    if (Directory.Exists(sandbox))
                    {
                        Directory.Delete(sandbox, true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _sandboxes.Clear();
        }

        // ---- Construction ----

        [Test]
        public void Constructor_RejectsNullCollaborators()
        {
            Harness h = MakeHarness();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryStartupCoordinator(null, h.Factory))
                    .ParamName,
                Is.EqualTo("entryCoordinator"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencRunPublicationRecoveryStartupCoordinator(h.Entry, null))
                    .ParamName,
                Is.EqualTo("workerFactory"));
        }

        /// <summary>
        /// The owner holds the entry coordinator and the one worker factory and
        /// nothing else: the process state and the verification buffer pool
        /// stay the factory's, so this type can neither create a second one nor
        /// re-implement the factory's admission.
        /// </summary>
        [Test]
        public void Fields_AreTheEntryCoordinatorAndTheOneFactoryOnly()
        {
            Type type = typeof(NvencRunPublicationRecoveryStartupCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            List<Type> readOnlyTypes = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsPublic, Is.False);
                if (field.IsInitOnly)
                {
                    readOnlyTypes.Add(field.FieldType);
                }

                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(NvencCaptureProcessState)));
                Assert.That(
                    field.FieldType,
                    Is.Not.EqualTo(typeof(CaptureArtifactVerificationBufferPool)));
            }

            Assert.That(readOnlyTypes, Is.EquivalentTo(new[]
            {
                typeof(CaptureRunInitializationEntryCoordinator),
                typeof(NvencRunPublicationRecoveryWorkerFactory),
            }));
            Assert.That(
                type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                Is.Empty);
        }

        // ---- Outcomes that start no recovery ----

        [Test]
        public void Open_LockContention_StartsNothingAndReturnsFalse()
        {
            Harness h = MakeHarness();
            h.Backend.OnAcquire = _ => false;

            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.False);

            Assert.That(outcome, Is.Null);
            Assert.That(ownershipLease, Is.Null);
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(0));
            h.AssertRecoveryNeverComposed();
        }

        /// <summary>
        /// The fresh path is unchanged: the existing coordinators run, the
        /// exact outcome and lease come back to the caller, and no recovery
        /// type is involved.
        /// </summary>
        [Test]
        public void Open_Fresh_HandsTheExactOutcomeAndLeaseBackWithoutARecovery()
        {
            Harness h = MakeHarness(RunShape.Fresh);

            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.True);

            Own(ownershipLease);

            Assert.That(outcome, Is.Not.Null);
            Assert.That(outcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
            Assert.That(outcome.Session, Is.Not.Null);
            Assert.That(ownershipLease, Is.Not.Null);
            Assert.That(
                outcome.LockIdentityEvidence.IsIssuedFor(ownershipLease), Is.True,
                "the lease handed back is the one this outcome was correlated with.");

            // The existing fresh collaborators ran exactly as before.
            Assert.That(h.IdSource.CallCount, Is.EqualTo(1));
            Assert.That(h.FreshProvisioner.CallCount, Is.GreaterThan(0));
            Assert.That(h.FreshWriter.CallCount, Is.GreaterThan(0));

            // No recovery worker, and nothing of the recovery graph entered.
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(h.Coordinator.ActiveRecoveryOpenOutcome, Is.Null);
            Assert.That(h.Coordinator.ActiveRecoveryOwnershipLease, Is.Null);
            Assert.That(h.Coordinator.RecoveryWorkerState,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
            Assert.That(h.Coordinator.TryGetRecoveryFailure(out Exception failure), Is.False);
            Assert.That(failure, Is.Null);
            Assert.That(
                h.Coordinator.TryCollectRecoveryTerminal(
                    out NvencRunPublicationRecoveryTerminalResult terminal),
                Is.False);
            Assert.That(terminal.IsValid, Is.False);
            h.AssertRecoveryNeverComposed();

            // The lease is still the caller's, and still holds the lock.
            Assert.That(ownershipLease.IsCreated, Is.True);
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(0));
        }

        // ---- One recovery worker per Run ----

        [Test]
        public void Open_PublicationRecovery_StartsExactlyOneWorkerForThatRun()
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery, PlanOpen.Blocked);

            try
            {
                Assert.That(
                    h.Coordinator.TryOpen(
                        h.Layout,
                        MaximumRootEntryCount,
                        out CaptureRunInitializationOpenOutcome outcome,
                        out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                    Is.True);

                Assert.That(outcome, Is.Not.Null);
                Assert.That(outcome.Status,
                    Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
                Assert.That(ownershipLease, Is.Null,
                    "a started recovery owns the lease, so none is handed back.");

                // The owner holds that exact Run's recovery.
                Assert.That(h.Coordinator.HasActiveRecovery, Is.True);
                Assert.That(
                    ReferenceEquals(h.Coordinator.ActiveRecoveryOpenOutcome, outcome), Is.True);
                Assert.That(
                    h.Coordinator.ActiveRecoveryOpenOutcome.LockIdentityEvidence.IsIssuedFor(
                        h.Coordinator.ActiveRecoveryOwnershipLease),
                    Is.True);
                Assert.That(h.Coordinator.RecoveryWorkerState,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Running));

                // It was started: the worker thread is inside the opener.
                WaitUntilOpenerEntered(h.Opener);
                Assert.That(h.Inspector.InspectCount, Is.EqualTo(1));

                // A second open is refused while that recovery is active, and
                // refused before the lock is touched.
                int acquisitions = h.Backend.AcquireCount;
                Assert.Throws<InvalidOperationException>(
                    () => h.Coordinator.TryOpen(
                        h.Layout,
                        MaximumRootEntryCount,
                        out CaptureRunInitializationOpenOutcome second,
                        out CaptureRunInitializationSessionOwnershipLease secondLease));

                Assert.That(h.Backend.AcquireCount, Is.EqualTo(acquisitions));
                Assert.That(h.Inspector.InspectCount, Is.EqualTo(1));
                Assert.That(h.Coordinator.HasActiveRecovery, Is.True);
                Assert.That(
                    ReferenceEquals(h.Coordinator.ActiveRecoveryOpenOutcome, outcome), Is.True);
            }
            finally
            {
                h.FinishBlockedRecovery();
            }
        }

        /// <summary>
        /// The start is asynchronous: the call returns while the recovery is
        /// still blocked inside the inspection, and every main-thread
        /// observation answers without waiting for it.
        /// </summary>
        [Test]
        public void Open_PublicationRecovery_DoesNotWaitForTheBlockedInspection()
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery, PlanOpen.Blocked);

            try
            {
                Assert.That(
                    h.Coordinator.TryOpen(
                        h.Layout,
                        MaximumRootEntryCount,
                        out CaptureRunInitializationOpenOutcome outcome,
                        out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                    Is.True);

                // The inspection cannot have finished: its opener is still
                // waiting on a gate this test has not opened.
                Assert.That(h.Opener.Gate.IsSet, Is.False);
                Assert.That(ownershipLease, Is.Null);
                Assert.That(outcome, Is.Not.Null);

                // Each observation is one non-waiting read.
                Assert.That(h.Coordinator.RecoveryWorkerState,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Running));
                Assert.That(
                    h.Coordinator.TryGetRecoveryFailure(out Exception failure), Is.False);
                Assert.That(failure, Is.Null);
                Assert.That(
                    h.Coordinator.TryCollectRecoveryTerminal(
                        out NvencRunPublicationRecoveryTerminalResult pending),
                    Is.False);
                Assert.That(pending.IsValid, Is.False);
                Assert.That(h.Coordinator.HasActiveRecovery, Is.True);
                Assert.That(h.Opener.Gate.IsSet, Is.False);
                Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(0));
            }
            finally
            {
                h.FinishBlockedRecovery();
            }
        }

        // ---- One terminal, once ----

        [Test]
        public void Terminal_IsCollectedOnceThenTheSlotIsFreeForTheNextRun()
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery);

            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.True);

            Assert.That(ownershipLease, Is.Null);
            CaptureRunInitializationSessionOwnershipLease recoveryLease =
                h.Coordinator.ActiveRecoveryOwnershipLease;
            Assert.That(recoveryLease, Is.Not.Null);

            // The main thread polls; it never waits on the worker.
            NvencRunPublicationRecoveryTerminalResult terminal =
                CollectTerminal(h.Coordinator);

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsStopped, Is.True,
                "an unreadable finished plan stops without changing anything.");
            Assert.That(ReferenceEquals(terminal.OpenOutcome, outcome), Is.True);
            Assert.That(ReferenceEquals(terminal.OwnershipLease, recoveryLease), Is.True);

            // The worker released the lease; the slot is free and nothing is
            // retained.
            Assert.That(recoveryLease.IsReleaseComplete, Is.True);
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(1));
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(h.Coordinator.ActiveRecoveryOpenOutcome, Is.Null);
            Assert.That(h.Coordinator.ActiveRecoveryOwnershipLease, Is.Null);
            Assert.That(h.Coordinator.RecoveryWorkerState,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));

            // At most once, and the second attempt re-runs nothing.
            int inspections = h.Inspector.InspectCount;
            int opens = h.Opener.CallCount;
            Assert.That(
                h.Coordinator.TryCollectRecoveryTerminal(
                    out NvencRunPublicationRecoveryTerminalResult again),
                Is.False);
            Assert.That(again.IsValid, Is.False);
            Assert.That(h.Coordinator.TryGetRecoveryFailure(out Exception failure), Is.False);
            Assert.That(failure, Is.Null);
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(inspections));
            Assert.That(h.Opener.CallCount, Is.EqualTo(opens));
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(1));

            // The freed slot admits the next Run through the same factory,
            // process state, and buffer pool.
            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome nextOutcome,
                    out CaptureRunInitializationSessionOwnershipLease nextLease),
                Is.True);

            Assert.That(nextLease, Is.Null);
            Assert.That(ReferenceEquals(nextOutcome, outcome), Is.False);
            Assert.That(h.Coordinator.HasActiveRecovery, Is.True);

            NvencRunPublicationRecoveryTerminalResult nextTerminal =
                CollectTerminal(h.Coordinator);
            Assert.That(nextTerminal.IsValid, Is.True);
            Assert.That(ReferenceEquals(nextTerminal.OpenOutcome, nextOutcome), Is.True);
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
        }

        // ---- Refused composition leaves nothing behind ----

        [Test]
        public void Open_WhileDraining_StartsNoRecoveryAndReleasesTheLease()
        {
            AssertRefusedProcessState(drain: true);
        }

        [Test]
        public void Open_AfterPoison_StartsNoRecoveryAndReleasesTheLease()
        {
            AssertRefusedProcessState(drain: false);
        }

        private void AssertRefusedProcessState(bool drain)
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery);

            if (drain)
            {
                Assert.That(h.ProcessState.TryBeginDrain(), Is.True);
            }
            else
            {
                Assert.That(h.ProcessState.TryPoison(), Is.True);
            }

            NvencCaptureProcessStatus statusBefore = h.ProcessState.State;

            // The factory's own admission is what refuses; nothing is
            // re-implemented here.
            Assert.Throws<InvalidOperationException>(
                () => h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease));

            // No worker, nothing retained, and the lease the entry produced is
            // released in reverse acquisition order.
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(h.Coordinator.ActiveRecoveryOpenOutcome, Is.Null);
            Assert.That(h.Coordinator.ActiveRecoveryOwnershipLease, Is.Null);
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(1));
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(1));
            Assert.That(h.DisposeLog, Is.EqualTo(new[]
            {
                h.SecondHandle.LockPath,
                h.FirstHandle.LockPath,
            }));

            // The recovery graph never ran, and the process state is left
            // exactly where it was.
            Assert.That(h.Opener.CallCount, Is.EqualTo(0));
            Assert.That(h.ProcessState.State, Is.EqualTo(statusBefore));

            // A retry while the process is still refused behaves the same way.
            Assert.Throws<InvalidOperationException>(
                () => h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome again,
                    out CaptureRunInitializationSessionOwnershipLease againLease));
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(h.Opener.CallCount, Is.EqualTo(0));
        }

        // ---- The one explicit release-retry control ----

        [Test]
        public void RequestReleaseRetry_WithoutAnActiveRecovery_IsRefused()
        {
            Harness h = MakeHarness(RunShape.Fresh);

            // Before any Run.
            Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);

            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.True);
            Own(ownershipLease);

            // A session-ready Run started no worker, so there is nothing to
            // resume and nothing is touched by asking.
            Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(outcome.Status,
                Is.EqualTo(CaptureRunInitializationOpenStatus.SessionReady));
            Assert.That(ownershipLease.IsCreated, Is.True);
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(0));
            h.AssertRecoveryNeverComposed();
        }

        [Test]
        public void RequestReleaseRetry_WhileRunningOrAfterCompletion_IsRefused()
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery, PlanOpen.Blocked);

            try
            {
                Assert.That(
                    h.Coordinator.TryOpen(
                        h.Layout,
                        MaximumRootEntryCount,
                        out CaptureRunInitializationOpenOutcome outcome,
                        out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                    Is.True);

                Assert.That(outcome, Is.Not.Null);
                Assert.That(ownershipLease, Is.Null);

                // Running: the worker is inside the inspection.
                WaitUntilOpenerEntered(h.Opener);
                Assert.That(h.Coordinator.RecoveryWorkerState,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Running));

                int opens = h.Opener.CallCount;
                Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);
                Assert.That(h.Coordinator.RecoveryWorkerState,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Running));
                Assert.That(h.Opener.CallCount, Is.EqualTo(opens));
                Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(0));

                // Completed, before the terminal is collected.
                h.Opener.Gate.Set();
                WaitUntilState(
                    h.Coordinator, NvencRunPublicationRecoveryWorkerState.Completed);

                opens = h.Opener.CallCount;
                Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);
                Assert.That(h.Coordinator.RecoveryWorkerState,
                    Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Completed));
                Assert.That(h.Opener.CallCount, Is.EqualTo(opens));
                Assert.That(h.Inspector.InspectCount, Is.EqualTo(1));

                // The completed terminal is still there to collect.
                Assert.That(
                    h.Coordinator.TryCollectRecoveryTerminal(
                        out NvencRunPublicationRecoveryTerminalResult terminal),
                    Is.True);
                Assert.That(terminal.IsValid, Is.True);
            }
            finally
            {
                h.FinishBlockedRecovery();
            }
        }

        /// <summary>
        /// A partially released lease parks the worker, and the one explicit
        /// request resumes that exact worker's retained release stage - once.
        /// </summary>
        [Test]
        public void RequestReleaseRetry_WhenParked_ResumesTheRetainedReleaseExactlyOnce()
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery);
            h.Backend.FailFirstReleaseOfTheSecondLock = true;

            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.True);

            Assert.That(ownershipLease, Is.Null);
            CaptureRunInitializationSessionOwnershipLease recoveryLease =
                h.Coordinator.ActiveRecoveryOwnershipLease;
            Assert.That(recoveryLease, Is.Not.Null);
            Own(recoveryLease);

            WaitUntilState(
                h.Coordinator,
                NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry);

            // The park is a partial release, not a finished one.
            Assert.That(recoveryLease.IsCreated, Is.False);
            Assert.That(recoveryLease.CanRelease, Is.True);
            Assert.That(recoveryLease.IsReleaseComplete, Is.False);
            Assert.That(h.Coordinator.TryGetRecoveryFailure(out Exception parked), Is.True);
            Assert.That(parked, Is.Not.Null);
            Assert.That(
                h.Coordinator.TryCollectRecoveryTerminal(
                    out NvencRunPublicationRecoveryTerminalResult pending),
                Is.False);
            Assert.That(pending.IsValid, Is.False);

            int inspections = h.Inspector.InspectCount;
            int opens = h.Opener.CallCount;

            // One request is accepted; an immediate second finds the state
            // already moved on.
            Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.True);
            Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);

            // The same worker resumes its retained release branch: the
            // terminal arrives with nothing re-inspected and nothing cleaned
            // up again.
            NvencRunPublicationRecoveryTerminalResult terminal =
                CollectTerminal(h.Coordinator);

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsStopped, Is.True);
            Assert.That(ReferenceEquals(terminal.OpenOutcome, outcome), Is.True);
            Assert.That(ReferenceEquals(terminal.OwnershipLease, recoveryLease), Is.True);
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(inspections));
            Assert.That(h.Opener.CallCount, Is.EqualTo(opens));
            h.AssertFileSystemsNeverEntered();

            // The retry finished the release: the handle that failed was
            // released on its second attempt, and the one that had already
            // succeeded was not touched again.
            Assert.That(recoveryLease.IsReleaseComplete, Is.True);
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(2));
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(1));

            // The slot is free, so the request is refused again.
            Assert.That(h.Coordinator.HasActiveRecovery, Is.False);
            Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);
        }

        // ---- A fault is not a success ----

        [Test]
        public void Faulted_IsNeitherATerminalNorAReleasedLease()
        {
            Harness h = MakeHarness(RunShape.PublicationRecovery, PlanOpen.IoFailure);

            Assert.That(
                h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome outcome,
                    out CaptureRunInitializationSessionOwnershipLease ownershipLease),
                Is.True);

            Assert.That(ownershipLease, Is.Null);
            CaptureRunInitializationSessionOwnershipLease recoveryLease =
                h.Coordinator.ActiveRecoveryOwnershipLease;
            Assert.That(recoveryLease, Is.Not.Null);

            // The lease is the fixture's to clean up only because the owner
            // deliberately leaves a faulted recovery alone.
            Own(recoveryLease);

            WaitUntilState(
                h.Coordinator, NvencRunPublicationRecoveryWorkerState.Faulted);

            Assert.That(h.Coordinator.TryGetRecoveryFailure(out Exception failure), Is.True);
            Assert.That(failure, Is.InstanceOf<IOException>());

            // No terminal is guessed, and no lock release is inferred.
            Assert.That(
                h.Coordinator.TryCollectRecoveryTerminal(
                    out NvencRunPublicationRecoveryTerminalResult terminal),
                Is.False);
            Assert.That(terminal.IsValid, Is.False);
            Assert.That(recoveryLease.IsReleaseComplete, Is.False);
            Assert.That(recoveryLease.IsCreated, Is.True);
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(0));

            // The faulted recovery keeps its slot: this unit adds no retry, no
            // deadline, and no forced unlock.
            Assert.That(h.Coordinator.HasActiveRecovery, Is.True);
            Assert.That(
                ReferenceEquals(h.Coordinator.ActiveRecoveryOpenOutcome, outcome), Is.True);
            Assert.Throws<InvalidOperationException>(
                () => h.Coordinator.TryOpen(
                    h.Layout,
                    MaximumRootEntryCount,
                    out CaptureRunInitializationOpenOutcome second,
                    out CaptureRunInitializationSessionOwnershipLease secondLease));
            Assert.That(h.Inspector.InspectCount, Is.EqualTo(1));

            // A faulted recovery is not resumable through the retry request
            // either, and asking changes nothing.
            int opens = h.Opener.CallCount;
            Assert.That(h.Coordinator.TryRequestRecoveryReleaseRetry(), Is.False);
            Assert.That(h.Coordinator.RecoveryWorkerState,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.Faulted));
            Assert.That(h.Opener.CallCount, Is.EqualTo(opens));
            Assert.That(h.FirstHandle.DisposeCount, Is.EqualTo(0));
            Assert.That(h.SecondHandle.DisposeCount, Is.EqualTo(0));
        }

        // ---- The two remaining terminal branches, over a real tree ----

        /// <summary>
        /// An Incomplete Run opened through the owner is cleaned up by the
        /// production cleaner and its lock released, all without this fixture
        /// touching the worker.
        /// </summary>
        [Test]
        public void Incomplete_IsCleanedUpAndReleasedThroughTheOwner()
        {
            RequireRealTreeCapabilities();

            SandboxHarness h = MakeSandboxHarness(SandboxShape.Incomplete);
            h.AssertIncompleteTreeSeeded();

            CaptureRunInitializationOpenOutcome outcome = null;
            CaptureRunInitializationSessionOwnershipLease callerLease = null;
            bool checksPassed = false;

            try
            {
                Assert.That(
                    h.Startup.TryOpen(
                        h.Layout, MaximumRootEntryCount, out outcome, out callerLease),
                    Is.True);

                Assert.That(callerLease, Is.Null,
                    "a started recovery owns the lease, so none is handed back.");
                Assert.That(outcome.Status,
                    Is.EqualTo(CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
                Assert.That(h.Startup.HasActiveRecovery, Is.True);
                Assert.That(
                    ReferenceEquals(h.Startup.ActiveRecoveryOpenOutcome, outcome), Is.True);

                CaptureRunInitializationSessionOwnershipLease recoveryLease =
                    h.Startup.ActiveRecoveryOwnershipLease;
                Assert.That(recoveryLease, Is.Not.Null);
                Assert.That(
                    outcome.LockIdentityEvidence.IsBoundTo(recoveryLease), Is.True);

                CaptureRunLockPathSet pathSet = outcome.LockPathSet;
                Assert.That(pathSet, Is.Not.Null);

                NvencRunPublicationRecoveryTerminalResult terminal =
                    CollectTerminal(h.Startup);

                Assert.That(terminal.IsIncompleteReleased, Is.True);
                Assert.That(terminal.PublicationRecoveryDecision.Disposition,
                    Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));
                Assert.That(ReferenceEquals(terminal.OpenOutcome, outcome), Is.True);
                Assert.That(
                    ReferenceEquals(terminal.OwnershipLease, recoveryLease), Is.True);

                // The orphan cleanup ran to its existing contract: both Run
                // roots are gone with everything they held, and the trusted
                // bases remain.
                h.AssertBothRunRootsDiscarded();

                // The lease is fully released and the same lock paths are free.
                Assert.That(recoveryLease.IsReleaseComplete, Is.True);
                Assert.That(recoveryLease.CanRelease, Is.False);
                AssertBothLocksCanBeAcquiredAgain(h.LockBackend, pathSet);

                // The slot is empty again.
                AssertSlotIsEmpty(h.Startup);

                checksPassed = true;
            }
            finally
            {
                Exception cleanupFailure = FinishActiveRecovery(h.Startup);
                Exception leaseFailure = ReleaseQuietly(callerLease);

                if (checksPassed && (cleanupFailure ?? leaseFailure) != null)
                {
                    throw new AssertionException(
                        "the recovery could not be finished at teardown.",
                        cleanupFailure ?? leaseFailure);
                }
            }
        }

        /// <summary>
        /// A Deferred verification - the one verification buffer is already
        /// rented - stops the recovery without changing a single file and
        /// releases only the ownership lease.
        /// </summary>
        [Test]
        public void DeferredVerification_StopsWithoutChangingTheTreeThroughTheOwner()
        {
            RequireRealTreeCapabilities();

            SandboxHarness h = MakeSandboxHarness(SandboxShape.Deferred);

            // The exact pool the factory was given hands out its only buffer to
            // this fixture, so the production inspector takes its own
            // BufferUnavailable path.
            CaptureArtifactVerificationBufferPool.Lease held = h.BufferPool.TryRent();

            try
            {
                Assert.That(held, Is.Not.Null, "the pool must hand out its only buffer.");
                Assert.That(h.BufferPool.OutstandingRentCount, Is.EqualTo(1));

                // The Run's own two roots, which are the file set this
                // recovery observes. The lock namespace lives beside them
                // under the trusted bases and is created by opening the Run.
                TreeSnapshot beforeStaging = TreeSnapshot.Of(h.Layout.StagingRunRoot);
                TreeSnapshot beforeFinal = TreeSnapshot.Of(h.Layout.FinalRunRoot);

                CaptureRunInitializationOpenOutcome outcome = null;
                CaptureRunInitializationSessionOwnershipLease callerLease = null;
                bool checksPassed = false;

                try
                {
                    Assert.That(
                        h.Startup.TryOpen(
                            h.Layout, MaximumRootEntryCount, out outcome, out callerLease),
                        Is.True);

                    Assert.That(callerLease, Is.Null);
                    Assert.That(outcome.Status,
                        Is.EqualTo(
                            CaptureRunInitializationOpenStatus.PublicationRecoveryRequired));
                    Assert.That(h.Startup.HasActiveRecovery, Is.True);

                    CaptureRunInitializationSessionOwnershipLease recoveryLease =
                        h.Startup.ActiveRecoveryOwnershipLease;
                    Assert.That(recoveryLease, Is.Not.Null);
                    Assert.That(
                        outcome.LockIdentityEvidence.IsBoundTo(recoveryLease), Is.True);

                    CaptureRunLockPathSet pathSet = outcome.LockPathSet;

                    NvencRunPublicationRecoveryTerminalResult terminal =
                        CollectTerminal(h.Startup);

                    Assert.That(terminal.IsStopped, Is.True);
                    Assert.That(terminal.PublicationRecoveryDecision.Disposition,
                        Is.EqualTo(NvencRunPublicationRecoveryDisposition.Deferred));
                    Assert.That(terminal.PublicationRecoveryDecision.AuthoritativePlan, Is.Null,
                        "a deferred verification settles no authoritative plan.");
                    Assert.That(ReferenceEquals(terminal.OpenOutcome, outcome), Is.True);
                    Assert.That(
                        ReferenceEquals(terminal.OwnershipLease, recoveryLease), Is.True);

                    // Nothing on disk moved, and the Capture Index was never
                    // created.
                    beforeStaging.AssertUnchanged("after the deferred recovery stopped");
                    beforeFinal.AssertUnchanged("after the deferred recovery stopped");
                    Assert.That(File.Exists(h.CaptureIndexPath), Is.False);
                    Assert.That(File.Exists(h.CaptureIndexTemporaryPath), Is.False);

                    // The buffer is still this fixture's only rent: neither the
                    // owner nor the release returned or replaced it.
                    Assert.That(h.BufferPool.OutstandingRentCount, Is.EqualTo(1));

                    // Only the ownership lease was released.
                    Assert.That(recoveryLease.IsReleaseComplete, Is.True);
                    Assert.That(recoveryLease.CanRelease, Is.False);
                    AssertBothLocksCanBeAcquiredAgain(h.LockBackend, pathSet);

                    AssertSlotIsEmpty(h.Startup);

                    checksPassed = true;
                }
                finally
                {
                    Exception cleanupFailure = FinishActiveRecovery(h.Startup);
                    Exception leaseFailure = ReleaseQuietly(callerLease);

                    if (checksPassed && (cleanupFailure ?? leaseFailure) != null)
                    {
                        throw new AssertionException(
                            "the recovery could not be finished at teardown.",
                            cleanupFailure ?? leaseFailure);
                    }
                }
            }
            finally
            {
                h.BufferPool.Return(held);
            }

            Assert.That(h.BufferPool.OutstandingRentCount, Is.Zero);
        }

        // ---- Fixture helpers ----

        private static void RequireRealTreeCapabilities()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Phase 0.11 recovery requires Windows no-follow file handles.");
            }

            if (!CaptureArtifactNoFollowOpen.Create().IsSupported)
            {
                Assert.Ignore("No-follow artifact open is not available on this platform.");
            }

            CaptureIndexCommitFileSystem fileSystem = CaptureIndexCommitFileSystem.Create();
            if (!fileSystem.IsSupported || !fileSystem.IsDirectoryFlushSupported)
            {
                Assert.Ignore(
                    "No-follow commit and directory flush are not available on this platform.");
            }
        }

        /// <summary>
        /// Nothing is retained after a collected terminal, and the owner is
        /// back in the state that admits the next open.
        /// </summary>
        private static void AssertSlotIsEmpty(
            NvencRunPublicationRecoveryStartupCoordinator startup)
        {
            Assert.That(startup.HasActiveRecovery, Is.False);
            Assert.That(startup.ActiveRecoveryOpenOutcome, Is.Null);
            Assert.That(startup.ActiveRecoveryOwnershipLease, Is.Null);
            Assert.That(startup.RecoveryWorkerState,
                Is.EqualTo(NvencRunPublicationRecoveryWorkerState.NotStarted));
            Assert.That(startup.TryGetRecoveryFailure(out Exception failure), Is.False,
                failure?.ToString());
            Assert.That(startup.TryRequestRecoveryReleaseRetry(), Is.False);
            Assert.That(
                startup.TryCollectRecoveryTerminal(
                    out NvencRunPublicationRecoveryTerminalResult again),
                Is.False);
            Assert.That(again.IsValid, Is.False);
        }

        /// <summary>
        /// Both of the Run's real locks are free again: each is acquired
        /// through the production backend and released here, whatever the
        /// assertions find.
        /// </summary>
        private static void AssertBothLocksCanBeAcquiredAgain(
            CaptureRunLockOsBackend backend, CaptureRunLockPathSet pathSet)
        {
            CaptureRunLockAcquisitionCoordinator coordinator =
                new CaptureRunLockAcquisitionCoordinator(backend);

            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);

            try
            {
                Assert.That(acquired, Is.True,
                    "the released Run locks must be acquirable again.");
                Assert.That(lease, Is.Not.Null);
                Assert.That(lease.IsCreated, Is.True);
            }
            finally
            {
                ReleaseQuietly(lease);
            }
        }

        /// <summary>
        /// Releases what this fixture owns and returns the failure instead of
        /// throwing it, so a teardown can never replace an earlier failure.
        /// </summary>
        private static Exception ReleaseQuietly(IDisposable resource)
        {
            try
            {
                resource?.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>
        /// Finishes a recovery the owner may still hold, using only what the
        /// owner offers: a bounded convergence on the terminal and one explicit
        /// release-retry request each time it is parked. Nothing is forced or
        /// unlocked, a faulted recovery is left exactly as it is, and the
        /// failure this could not resolve is returned rather than thrown.
        /// </summary>
        private static Exception FinishActiveRecovery(
            NvencRunPublicationRecoveryStartupCoordinator startup)
        {
            try
            {
                Stopwatch watchdog = Stopwatch.StartNew();
                while (startup.HasActiveRecovery)
                {
                    if (startup.TryCollectRecoveryTerminal(
                            out NvencRunPublicationRecoveryTerminalResult collected))
                    {
                        return null;
                    }

                    if (startup.RecoveryWorkerState
                        == NvencRunPublicationRecoveryWorkerState.AwaitingReleaseRetry)
                    {
                        startup.TryRequestRecoveryReleaseRetry();
                    }
                    else if (startup.RecoveryWorkerState
                        == NvencRunPublicationRecoveryWorkerState.Faulted)
                    {
                        // The owner keeps a faulted recovery, and this fixture
                        // does not work around that.
                        return null;
                    }

                    if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                    {
                        return new TimeoutException(
                            "the recovery did not reach a terminal within "
                            + WatchdogMilliseconds + " ms; it is "
                            + startup.RecoveryWorkerState + ".");
                    }

                    Thread.Yield();
                }

                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private void Own(IDisposable resource)
        {
            if (resource != null)
            {
                _ownedDisposables.Add(resource);
            }
        }

        /// <summary>
        /// Polls the way a main thread would, with a bound, and never waits on
        /// the worker.
        /// </summary>
        private static NvencRunPublicationRecoveryTerminalResult CollectTerminal(
            NvencRunPublicationRecoveryStartupCoordinator coordinator)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (true)
            {
                if (coordinator.TryCollectRecoveryTerminal(
                        out NvencRunPublicationRecoveryTerminalResult terminal))
                {
                    return terminal;
                }

                if (coordinator.TryGetRecoveryFailure(out Exception failure))
                {
                    Assert.Fail("the recovery failed instead of reaching a terminal: " + failure);
                }

                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail(
                        "no terminal was collectable in time; the worker is "
                        + coordinator.RecoveryWorkerState + ".");
                }

                Thread.Yield();
            }
        }

        private static void WaitUntilState(
            NvencRunPublicationRecoveryStartupCoordinator coordinator,
            NvencRunPublicationRecoveryWorkerState expected)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (coordinator.RecoveryWorkerState != expected)
            {
                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail(
                        "the recovery worker did not reach " + expected + " in time; it is "
                        + coordinator.RecoveryWorkerState + ".");
                }

                Thread.Yield();
            }
        }

        private static void WaitUntilOpenerEntered(FakeOpener opener)
        {
            Stopwatch watchdog = Stopwatch.StartNew();
            while (!opener.Entered.IsSet)
            {
                if (watchdog.ElapsedMilliseconds > WatchdogMilliseconds)
                {
                    Assert.Fail("the recovery worker never reached the artifact opener.");
                }

                Thread.Yield();
            }
        }

        private enum SandboxShape
        {
            /// <summary>No finished plan: uncommitted NVENC artifacts only.</summary>
            Incomplete,

            /// <summary>A canonical plan and the chunk it declares.</summary>
            Deferred,
        }

        /// <summary>
        /// One Run's real tree with the production opener, filesystem, OS lock
        /// backend, process state, buffer pool, factory, and owner. The only
        /// fixture seam is the initialization recovery inspector.
        /// </summary>
        private sealed class SandboxHarness
        {
            internal SandboxHarness(
                string root,
                CaptureRunRootLayout layout,
                CaptureRunLockOsBackend lockBackend,
                CaptureArtifactVerificationBufferPool bufferPool,
                NvencRunPublicationRecoveryStartupCoordinator startup)
            {
                Root = root;
                Layout = layout;
                LockBackend = lockBackend;
                BufferPool = bufferPool;
                Startup = startup;
            }

            internal string Root { get; }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunLockOsBackend LockBackend { get; }

            internal CaptureArtifactVerificationBufferPool BufferPool { get; }

            internal NvencRunPublicationRecoveryStartupCoordinator Startup { get; }

            internal string ChunksPath =>
                Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName);

            internal string CaptureIndexPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string CaptureIndexTemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            /// <summary>
            /// The Incomplete shape as a file set: canonical markers in both
            /// roots, no finished plan, uncommitted NVENC entries, and a final
            /// side holding nothing but its markers.
            /// </summary>
            internal void AssertIncompleteTreeSeeded()
            {
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, PublicationPlanName)),
                    Is.False);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, PrecommitTemporaryName)),
                    Is.True);
                Assert.That(File.Exists(Path.Combine(ChunksPath, PartialChunkName)), Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName)),
                    Is.True);
                Assert.That(
                    File.Exists(Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName)),
                    Is.True);
                Assert.That(File.Exists(CaptureIndexPath), Is.False);
                Assert.That(File.Exists(CaptureIndexTemporaryPath), Is.False);
            }

            /// <summary>
            /// Both Run roots are gone with everything they held, and the
            /// trusted bases remain.
            /// </summary>
            internal void AssertBothRunRootsDiscarded()
            {
                Assert.That(Directory.Exists(Layout.StagingRunRoot), Is.False);
                Assert.That(Directory.Exists(Layout.FinalRunRoot), Is.False);
                Assert.That(Directory.Exists(ChunksPath), Is.False);
                Assert.That(
                    Directory.Exists(Path.GetDirectoryName(Layout.StagingRunRoot)), Is.True);
                Assert.That(
                    Directory.Exists(Path.GetDirectoryName(Layout.FinalRunRoot)), Is.True);
            }
        }

        /// <summary>
        /// A whole temporary tree recorded by relative path, byte length, exact
        /// bytes, and SHA-256, so "unchanged" means unchanged in content and
        /// shape rather than in modification time.
        /// </summary>
        private sealed class TreeSnapshot
        {
            private readonly string _root;
            private readonly List<string> _directories;
            private readonly Dictionary<string, Entry> _files;

            private TreeSnapshot(
                string root, List<string> directories, Dictionary<string, Entry> files)
            {
                _root = root;
                _directories = directories;
                _files = files;
            }

            internal static TreeSnapshot Of(string root)
            {
                List<string> directories = new List<string>();
                foreach (string directory in Directory.GetDirectories(
                    root, "*", SearchOption.AllDirectories))
                {
                    directories.Add(Relative(root, directory));
                }

                directories.Sort(StringComparer.Ordinal);

                Dictionary<string, Entry> files = new Dictionary<string, Entry>(
                    StringComparer.Ordinal);
                foreach (string file in Directory.GetFiles(
                    root, "*", SearchOption.AllDirectories))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    files[Relative(root, file)] = new Entry(bytes, Sha256Hex(bytes));
                }

                return new TreeSnapshot(root, directories, files);
            }

            internal void AssertUnchanged(string message)
            {
                TreeSnapshot now = Of(_root);

                Assert.That(now._directories, Is.EqualTo(_directories), message);
                Assert.That(now._files.Count, Is.EqualTo(_files.Count), message);

                foreach (KeyValuePair<string, Entry> expected in _files)
                {
                    Assert.That(now._files.ContainsKey(expected.Key), Is.True,
                        expected.Key + " " + message);

                    Entry observed = now._files[expected.Key];
                    Assert.That(observed.Bytes.Length, Is.EqualTo(expected.Value.Bytes.Length),
                        expected.Key + " " + message);
                    Assert.That(observed.Bytes, Is.EqualTo(expected.Value.Bytes),
                        expected.Key + " " + message);
                    Assert.That(observed.Sha256, Is.EqualTo(expected.Value.Sha256),
                        expected.Key + " " + message);
                    Assert.That(
                        new FileInfo(Path.Combine(_root, expected.Key)).Length,
                        Is.EqualTo(expected.Value.Bytes.LongLength),
                        expected.Key + " " + message);
                }
            }

            private static string Relative(string root, string path)
            {
                return path.Substring(root.Length + 1);
            }

            private readonly struct Entry
            {
                internal Entry(byte[] bytes, string sha256)
                {
                    Bytes = bytes;
                    Sha256 = sha256;
                }

                internal byte[] Bytes { get; }

                internal string Sha256 { get; }
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                char[] hex = new char[hash.Length * 2];
                const string Digits = "0123456789abcdef";
                for (int i = 0; i < hash.Length; i++)
                {
                    hex[i * 2] = Digits[hash[i] >> 4];
                    hex[(i * 2) + 1] = Digits[hash[i] & 0xF];
                }

                return new string(hex);
            }
        }

        private static CaptureFrameEvidenceEntry[] MakeFrameEvidence()
        {
            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
            }

            return entries;
        }

        /// <summary>
        /// Builds one Run's real tree in the requested shape and the production
        /// owner over it.
        /// </summary>
        private SandboxHarness MakeSandboxHarness(SandboxShape shape)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "zantetsuken-phase011-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            CaptureRunInitializationDocumentSet documents =
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);

            string chunksDirectory = Path.Combine(layout.StagingRunRoot, ChunksDirectoryName);
            Directory.CreateDirectory(chunksDirectory);
            Directory.CreateDirectory(layout.FinalRunRoot);

            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunInitializationMarkerName),
                documents.GetStagingInitializationBytes());
            File.WriteAllBytes(
                Path.Combine(layout.StagingRunRoot, RunReadyMarkerName),
                documents.GetStagingReadyBytes());
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunInitializationMarkerName),
                documents.GetFinalInitializationBytes());
            File.WriteAllBytes(
                Path.Combine(layout.FinalRunRoot, RunReadyMarkerName),
                documents.GetFinalReadyBytes());

            if (shape == SandboxShape.Incomplete)
            {
                // Uncommitted NVENC entries at their fixed paths. Their content
                // is never read by this path, so any small bytes will do.
                File.WriteAllBytes(
                    Path.Combine(layout.StagingRunRoot, PrecommitTemporaryName),
                    new byte[] { 0x7b, 0x22, 0x3f, 0x01 });
                File.WriteAllBytes(
                    Path.Combine(chunksDirectory, PartialChunkName),
                    new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88 });
            }
            else
            {
                byte[] chunkBytes = new byte[4096];
                for (int i = 0; i < chunkBytes.Length; i++)
                {
                    chunkBytes[i] = (byte)((i * 17) + 3);
                }

                CapturePublicationPlan plan = new CapturePublicationPlan(
                    layout.TestRunId,
                    InitId,
                    WriterHash,
                    new[]
                    {
                        NvencRunChunkArtifactDescriptorFactory.Create(
                            ArtifactId, chunkBytes.LongLength, Sha256Hex(chunkBytes)),
                    },
                    MakeFrameEvidence());

                File.WriteAllBytes(
                    Path.Combine(layout.StagingRunRoot, PublicationPlanName),
                    CapturePublicationPlanCodec.SerializeCanonical(plan));

                string finalChunkPath = Path.Combine(
                    layout.FinalRunRoot,
                    NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                        '/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(finalChunkPath));
                File.WriteAllBytes(finalChunkPath, chunkBytes);
            }

            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);

            FakeInitializationInspector inspector = new FakeInitializationInspector(
                MakeObservation(
                    CaptureRunRootRole.Staging,
                    binding.StagingInitialization,
                    binding.StagingReady,
                    hasNonMarkerEntries: true),
                MakeObservation(
                    CaptureRunRootRole.Final,
                    binding.FinalInitialization,
                    binding.FinalReady,
                    hasNonMarkerEntries: false));

            RecordingFreshStart freshStart = new RecordingFreshStart();
            CaptureRunLockOsBackend lockBackend = CaptureRunLockOsBackend.Create();

            CaptureRunInitializationEntryCoordinator entry =
                new CaptureRunInitializationEntryCoordinator(
                    new CaptureRunLockAcquisitionCoordinator(lockBackend),
                    new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                        inspector,
                        new CaptureRunInitializationRecoveryExecutionCoordinator(
                            freshStart, freshStart, freshStart)),
                    new CaptureRunInitializationRecoverySessionRoutingCoordinator(
                        new CaptureRunInitializationRecoveryStartFreshCoordinator(
                            freshStart,
                            new CaptureRunInitializationExecutionCoordinator(
                                freshStart, freshStart))));

            CaptureArtifactVerificationBufferPool bufferPool =
                new CaptureArtifactVerificationBufferPool(VerificationBufferLength);

            return new SandboxHarness(
                root,
                layout,
                lockBackend,
                bufferPool,
                new NvencRunPublicationRecoveryStartupCoordinator(
                    entry,
                    new NvencRunPublicationRecoveryWorkerFactory(
                        new NvencCaptureProcessState(), bufferPool)));
        }

        /// <summary>
        /// The collaborators a publication recovery must never reach: the
        /// initialization cleanup, provisioning, marker writing, and fresh
        /// initialization ID.
        /// </summary>
        private sealed class RecordingFreshStart
            : ICaptureRunInitializationRecoveryCleanupBackend,
              ICaptureRunRootProvisioner,
              ICaptureRunMarkerAtomicWriter,
              ICaptureRunInitializationIdSource
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                throw new NotSupportedException(
                    "A publication recovery performs no initialization cleanup.");
            }

            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                throw new NotSupportedException(
                    "A publication recovery provisions no Run root.");
            }

            public CaptureRunMarkerWriteReceipt WriteAtomic(
                CaptureRunMarkerWriteOperation operation)
            {
                throw new NotSupportedException("A publication recovery writes no marker.");
            }

            public string Create()
            {
                throw new NotSupportedException(
                    "A publication recovery issues no fresh initialization ID.");
            }
        }

        private enum RunShape
        {
            Fresh,
            PublicationRecovery,
        }

        private enum PlanOpen
        {
            /// <summary>An unreadable finished plan: the recovery stops.</summary>
            InvalidFileKind,

            /// <summary>The same, behind a gate this fixture opens.</summary>
            Blocked,

            /// <summary>The plan cannot be opened at all: the recovery faults.</summary>
            IoFailure,
        }

        private Harness MakeHarness(
            RunShape shape = RunShape.PublicationRecovery,
            PlanOpen planOpen = PlanOpen.InvalidFileKind)
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                1);
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);

            CaptureRunInitializationRootObservation staging;
            CaptureRunInitializationRootObservation final;
            if (shape == RunShape.PublicationRecovery)
            {
                // Both roots initialized and ready for the same Run, with a
                // non-marker entry present.
                staging = MakeObservation(
                    CaptureRunRootRole.Staging,
                    binding.StagingInitialization,
                    binding.StagingReady,
                    hasNonMarkerEntries: true);
                final = MakeObservation(
                    CaptureRunRootRole.Final,
                    binding.FinalInitialization,
                    binding.FinalReady,
                    hasNonMarkerEntries: false);
            }
            else
            {
                // A leftover initialization temporary on one side and nothing
                // on the other: the existing cleanup-then-start-fresh path.
                staging = new CaptureRunInitializationRootObservation(
                    CaptureRunRootRole.Staging, true, true,
                    CaptureRunMarkerObservationStatus.Absent, null,
                    false, CaptureRunMarkerObservationStatus.Absent, null,
                    false, false, false);
                final = new CaptureRunInitializationRootObservation(
                    CaptureRunRootRole.Final, false, false,
                    CaptureRunMarkerObservationStatus.Absent, null,
                    false, CaptureRunMarkerObservationStatus.Absent, null,
                    false, false, false);
            }

            Harness h = new Harness(layout, planOpen);

            h.Inspector = new FakeInitializationInspector(staging, final);
            h.Entry = new CaptureRunInitializationEntryCoordinator(
                new CaptureRunLockAcquisitionCoordinator(h.Backend),
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    h.Inspector,
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        h.RecoveryCleanup, h.RecoveryProvisioner, h.RecoveryWriter)),
                new CaptureRunInitializationRecoverySessionRoutingCoordinator(
                    new CaptureRunInitializationRecoveryStartFreshCoordinator(
                        h.IdSource,
                        new CaptureRunInitializationExecutionCoordinator(
                            h.FreshProvisioner, h.FreshWriter))));

            // One process state, one verification buffer pool, one factory for
            // the whole process.
            h.Factory = new NvencRunPublicationRecoveryWorkerFactory(
                h.ProcessState,
                h.Opener,
                h.BufferPool,
                h.CommitFileSystem,
                h.CleanupFileSystem);

            h.Coordinator = new NvencRunPublicationRecoveryStartupCoordinator(
                h.Entry, h.Factory);

            return h;
        }

        private static CaptureRunInitializationRootObservation MakeObservation(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker initialization,
            CaptureRunReadyMarker ready,
            bool hasNonMarkerEntries)
        {
            return new CaptureRunInitializationRootObservation(
                role, true, false,
                CaptureRunMarkerObservationStatus.Canonical, initialization,
                false, CaptureRunMarkerObservationStatus.Canonical, ready,
                hasNonMarkerEntries, false, false);
        }

        private sealed class Harness
        {
            internal Harness(CaptureRunRootLayout layout, PlanOpen planOpen)
            {
                Layout = layout;
                Opener = new FakeOpener(planOpen);
                Backend = new FakeLockBackend(DisposeLog);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal List<string> DisposeLog { get; } = new List<string>();

            internal FakeLockBackend Backend { get; }

            internal FakeOpener Opener { get; }

            internal NvencCaptureProcessState ProcessState { get; } =
                new NvencCaptureProcessState();

            internal CaptureArtifactVerificationBufferPool BufferPool { get; } =
                new CaptureArtifactVerificationBufferPool(VerificationBufferLength);

            internal RefusingCommitFileSystem CommitFileSystem { get; } =
                new RefusingCommitFileSystem();

            internal RefusingCleanupFileSystem CleanupFileSystem { get; } =
                new RefusingCleanupFileSystem();

            internal FakeInitializationInspector Inspector { get; set; }

            internal FakeCleanupBackend RecoveryCleanup { get; } = new FakeCleanupBackend();

            internal FakeProvisioner RecoveryProvisioner { get; } = new FakeProvisioner();

            internal FakeWriter RecoveryWriter { get; } = new FakeWriter();

            internal FakeIdSource IdSource { get; } = new FakeIdSource();

            internal FakeProvisioner FreshProvisioner { get; } = new FakeProvisioner();

            internal FakeWriter FreshWriter { get; } = new FakeWriter();

            internal CaptureRunInitializationEntryCoordinator Entry { get; set; }

            internal NvencRunPublicationRecoveryWorkerFactory Factory { get; set; }

            internal NvencRunPublicationRecoveryStartupCoordinator Coordinator { get; set; }

            internal FakeLockHandle FirstHandle => Backend.CreatedHandles[0];

            internal FakeLockHandle SecondHandle => Backend.CreatedHandles[1];

            /// <summary>Nothing of the recovery graph was entered.</summary>
            internal void AssertRecoveryNeverComposed()
            {
                Assert.That(Opener.CallCount, Is.EqualTo(0));
                AssertFileSystemsNeverEntered();
            }

            /// <summary>
            /// Neither filesystem surface was touched: a stopped recovery
            /// commits nothing and cleans up nothing.
            /// </summary>
            internal void AssertFileSystemsNeverEntered()
            {
                Assert.That(CommitFileSystem.CallCount, Is.EqualTo(0));
                Assert.That(CleanupFileSystem.CallCount, Is.EqualTo(0));
            }

            /// <summary>
            /// Opens the gate a blocked recovery is waiting on and takes its
            /// terminal, so no worker thread outlives the test.
            /// </summary>
            internal void FinishBlockedRecovery()
            {
                Opener.Gate.Set();

                Stopwatch watchdog = Stopwatch.StartNew();
                while (Coordinator.HasActiveRecovery
                    && watchdog.ElapsedMilliseconds <= WatchdogMilliseconds)
                {
                    if (Coordinator.TryCollectRecoveryTerminal(
                            out NvencRunPublicationRecoveryTerminalResult ignored))
                    {
                        break;
                    }

                    if (Coordinator.TryGetRecoveryFailure(out Exception faulted)
                        && faulted != null)
                    {
                        break;
                    }

                    Thread.Yield();
                }
            }
        }

        /// <summary>
        /// A lock backend whose handles count their disposals, so a lease
        /// release is observable without a real OS lock.
        /// </summary>
        private sealed class FakeLockBackend : ICaptureRunLockBackend
        {
            private readonly List<string> _disposeLog;

            internal FakeLockBackend(List<string> disposeLog)
            {
                _disposeLog = disposeLog;
            }

            internal Func<string, bool> OnAcquire { get; set; }

            /// <summary>
            /// Arms a one-time release failure on the second lock's handle -
            /// the one a lease releases first - so exactly one of the two
            /// releases fails and the lease ends up partially released.
            /// </summary>
            internal bool FailFirstReleaseOfTheSecondLock { get; set; }

            internal List<FakeLockHandle> CreatedHandles { get; } =
                new List<FakeLockHandle>();

            internal int AcquireCount { get; private set; }

            public bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle)
            {
                AcquireCount++;

                if (OnAcquire != null && !OnAcquire(absoluteLockPath))
                {
                    handle = null;
                    return false;
                }

                FakeLockHandle created = new FakeLockHandle(absoluteLockPath, _disposeLog);
                if (FailFirstReleaseOfTheSecondLock && CreatedHandles.Count == 1)
                {
                    created.FailFirstRelease();
                }

                CreatedHandles.Add(created);
                handle = created;
                return true;
            }
        }

        private sealed class FakeLockHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;
            private int _disposeCount;
            private int _throwOnce;

            internal FakeLockHandle(string lockPath, List<string> disposeLog)
            {
                LockPath = lockPath;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated => Volatile.Read(ref _disposeCount) == 0;

            internal int DisposeCount => Volatile.Read(ref _disposeCount);

            /// <summary>
            /// Fails the first release attempt only, so the lease ends up
            /// partially released and the worker parks.
            /// </summary>
            internal void FailFirstRelease()
            {
                Volatile.Write(ref _throwOnce, 1);
            }

            public void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                lock (_disposeLog)
                {
                    _disposeLog.Add(LockPath);
                }

                if (Interlocked.Exchange(ref _throwOnce, 0) == 1)
                {
                    throw new IOException(
                        "Fake lock handle release failure: " + LockPath + ".");
                }
            }
        }

        /// <summary>
        /// The artifact opener the production recovery inspector observes
        /// through. The publication plan is reported as an unreadable finished
        /// document - which stops the recovery without changing anything - or
        /// as an open failure, and every other name is absent.
        /// </summary>
        private sealed class FakeOpener : ICaptureArtifactNoFollowOpener
        {
            private readonly PlanOpen _planOpen;
            private int _callCount;

            internal FakeOpener(PlanOpen planOpen)
            {
                _planOpen = planOpen;
                Gate = new ManualResetEventSlim(planOpen != PlanOpen.Blocked);
            }

            internal ManualResetEventSlim Gate { get; }

            internal ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported => true;

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                Interlocked.Increment(ref _callCount);
                Entered.Set();

                if (!Gate.IsSet)
                {
                    Gate.Wait(GateWaitMilliseconds);
                }

                if (relativePath != NvencRunPublicationPlanCommitOperation.FinalBasename)
                {
                    return CaptureArtifactNoFollowOpenResult.Of(
                        CaptureArtifactNoFollowOpenStatus.Absent);
                }

                return CaptureArtifactNoFollowOpenResult.Of(
                    _planOpen == PlanOpen.IoFailure
                        ? CaptureArtifactNoFollowOpenStatus.IoFailure
                        : CaptureArtifactNoFollowOpenStatus.InvalidFileKind);
            }
        }

        /// <summary>
        /// A commit filesystem the stop path must never reach.
        /// </summary>
        private sealed class RefusingCommitFileSystem : ICaptureIndexCommitFileSystem
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported => Refuse<bool>();

            public bool IsDirectoryFlushSupported => Refuse<bool>();

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath) =>
                Refuse<CaptureIndexCommitDirectory>();

            public CaptureIndexFileOpen TryOpen(
                CaptureIndexCommitDirectory directory, string name) =>
                Refuse<CaptureIndexFileOpen>();

            public CaptureIndexCommitFile CreateNew(
                CaptureIndexCommitDirectory directory, string name) =>
                Refuse<CaptureIndexCommitFile>();

            public void FlushFileData(CaptureIndexCommitFile file) =>
                Refuse<bool>();

            public void Rename(
                CaptureIndexCommitFile file,
                CaptureIndexCommitDirectory directory,
                string newName) =>
                Refuse<bool>();

            public void Delete(CaptureIndexCommitFile file) => Refuse<bool>();

            public void FlushDirectory(CaptureIndexCommitDirectory directory) =>
                Refuse<bool>();

            private T Refuse<T>()
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException(
                    "A stopped recovery commits no Capture Index.");
            }
        }

        /// <summary>
        /// A cleanup filesystem the stop path must never reach.
        /// </summary>
        private sealed class RefusingCleanupFileSystem : ICaptureCompleteCleanupFileSystem
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public bool IsSupported => Refuse<bool>();

            public bool IsDirectoryFlushSupported => Refuse<bool>();

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath) =>
                Refuse<CaptureIndexCommitDirectory>();

            public CaptureIndexDirectoryOpen TryOpenDirectory(string absolutePath) =>
                Refuse<CaptureIndexDirectoryOpen>();

            public CaptureIndexFileOpen TryOpen(
                CaptureIndexCommitDirectory directory, string name) =>
                Refuse<CaptureIndexFileOpen>();

            public void Delete(CaptureIndexCommitFile file) => Refuse<bool>();

            public void FlushDirectory(CaptureIndexCommitDirectory directory) =>
                Refuse<bool>();

            public void DeleteDirectory(CaptureIndexCommitDirectory directory) =>
                Refuse<bool>();

            public bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory) =>
                Refuse<bool>();

            private T Refuse<T>()
            {
                Interlocked.Increment(ref _callCount);
                throw new NotSupportedException("A stopped recovery cleans up nothing.");
            }
        }

        /// <summary>
        /// The one fixture-local fake of a boundary with no production
        /// implementation: it returns the canonical observation pair it was
        /// built with and opens, enumerates, and reads nothing.
        /// </summary>
        private sealed class FakeInitializationInspector
            : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            internal FakeInitializationInspector(
                CaptureRunInitializationRootObservation staging,
                CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            internal int InspectCount { get; private set; }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
                CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                InspectCount++;
                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend
            : ICaptureRunInitializationRecoveryCleanupBackend
        {
            internal int CallCount { get; private set; }

            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                CallCount++;
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            internal int CallCount { get; private set; }

            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                CallCount++;
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            internal int CallCount { get; private set; }

            public CaptureRunMarkerWriteReceipt WriteAtomic(
                CaptureRunMarkerWriteOperation operation)
            {
                CallCount++;
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakeIdSource : ICaptureRunInitializationIdSource
        {
            internal int CallCount { get; private set; }

            public string Create()
            {
                CallCount++;
                return FreshInitId;
            }
        }
    }
}
