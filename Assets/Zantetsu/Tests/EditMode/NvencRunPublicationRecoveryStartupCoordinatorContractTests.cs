using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
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
    /// Only two recovery shapes are used, as vehicles for reaching a terminal
    /// and a fault: a plan the opener reports as an unreadable finished
    /// document, which stops without changing anything, and a plan whose open
    /// fails. The four dispositions, the filesystem cleanup, and the partial
    /// release retry belong to the existing worker fixture and managed
    /// end-to-end tests and are not repeated here.
    /// </para>
    /// </remarks>
    public class NvencRunPublicationRecoveryStartupCoordinatorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string FreshInitId = "fedcba9876543210fedcba9876543210";

        private const int VerificationBufferLength = 64 * 1024;

        private const int MaximumRootEntryCount = 4;

        private const int WatchdogMilliseconds = 10000;

        private const int GateWaitMilliseconds = 30000;

        private static bool IsWindows =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private readonly List<IDisposable> _ownedDisposables = new List<IDisposable>();

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

        // ---- Fixture helpers ----

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
