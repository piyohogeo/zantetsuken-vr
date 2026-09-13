using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Managed end-to-end test for the Phase 0.11 Fresh NVENC normal completion
    /// path. One process state, one root layout, one artifact store, one Session
    /// Ownership Lease, one Publication Service, and one Run Coordinator drive
    /// the whole chain - finalized chunk, plan commit, artifact publication,
    /// capture index commit, CaptureComplete, staging cleanup, lock release, Run
    /// completion - with the production committer, publisher, index committer,
    /// completer, cleaner, and releaser against a real filesystem sandbox.
    /// </summary>
    /// <remarks>
    /// The pre-publication half still uses the existing Tier A fakes for the
    /// encoder, the source, and the submit path: no NVENC, GPU texture, or real
    /// encoder session is involved. The chunk writer is a fake in the same
    /// sense, but it writes the real staging chunk bytes and their real SHA-256
    /// so the production publisher verifies a genuine file. Nothing here sleeps,
    /// repeats probabilistically, or waits unbounded, and Settled is never
    /// treated as evidence - every wait re-checks the real condition inside a
    /// bounded watchdog.
    /// </remarks>
    public class NvencCapturePublicationPhase011EndToEndTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const int ChunkByteLength = 4096;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string RunInitializationMarkerName = "run.init";

        private const string RunReadyMarkerName = "run.ready";

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
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

        [Test]
        public void FreshRun_NormalCompletion_PublishesEverythingAndReadmitsTheProcess()
        {
            RequireWindows();

            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                h.StartWorkers();

                // ---- 1. the Run produces its one finalized staging chunk ----
                FinalizeChunkAndFreeze(h);

                byte[] chunkBytes = h.Writer.WrittenBytes;
                string chunkHash = h.Writer.ContentHash;
                Assert.That(chunkBytes, Has.Length.EqualTo(ChunkByteLength));
                Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
                Assert.That(File.ReadAllBytes(sandbox.StagingChunkPath), Is.EqualTo(chunkBytes));

                // The markers the initialization would have written are the ones
                // the production cleaner will later verify and remove.
                sandbox.WriteMarkers();

                // ---- 2. plan commit ----
                Assert.That(h.RunCoordinator.TryPreparePublicationPlanCommit(
                    Hash64, out NvencRunPublicationPlanCommitOperation planOperation), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                    "service did not publish the plan commit terminal");
                Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(
                    out NvencRunPublicationPlanCommitExecutionResult planResult), Is.True);
                Assert.That(planResult.Status,
                    Is.EqualTo(NvencRunPublicationPlanCommitStatus.Committed));
                Assert.That(File.Exists(sandbox.StagingPlanPath), Is.True);

                // ---- 3. artifact publication ----
                Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(
                    out NvencRunArtifactPublicationOperation artifactOperation), Is.True);
                Assert.That(artifactOperation.Descriptor.ContentHash, Is.EqualTo(chunkHash));
                Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.ArtifactPublicationCompleted,
                    "service did not publish the artifact publication terminal");
                Assert.That(h.RunCoordinator.TryCollectArtifactPublication(
                    out NvencRunArtifactPublicationAttemptResult artifactResult), Is.True);
                Assert.That(artifactResult.IsPublished, Is.True);

                // The chunk moved: it is in the final root and gone from staging.
                Assert.That(File.Exists(sandbox.FinalChunkPath), Is.True);
                Assert.That(File.Exists(sandbox.StagingChunkPath), Is.False);

                // ---- 4. capture index commit ----
                Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitOperation indexOperation), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
                WaitForServiceState(h, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                    "service did not publish the capture index terminal");
                Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(
                    out NvencRunCaptureIndexCommitAttemptResult indexResult), Is.True);
                Assert.That(indexResult.IsCommitted, Is.True);
                Assert.That(File.Exists(sandbox.CaptureIndexPath), Is.True);

                byte[] expectedIndexBytes =
                    CapturePublicationPlanCodec.SerializeCanonical(indexOperation.Plan);

                // ---- 5. CaptureComplete on the same Service Worker ----
                Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(
                    out NvencRunCaptureCompleteOperation completeOperation), Is.True);
                Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);
                WaitForServiceStop(h, "publication worker did not stop after CaptureComplete");
                Assert.That(h.Service.State,
                    Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteCompleted));
                Assert.That(h.RunCoordinator.TryCollectCaptureComplete(
                    out NvencRunCaptureCompleteAttemptResult completeResult), Is.True);
                Assert.That(completeResult.IsCompleted, Is.True);
                Assert.That(ReferenceEquals(completeResult.Operation, completeOperation), Is.True);

                // The one Worker is physically stopped and released before any
                // cleanup is prepared.
                Assert.That(h.Service.IsStopped, Is.True);
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));

                // ---- 6. staging cleanup ----
                Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                    out NvencRunCaptureCompleteCleanupOperation cleanupOperation), Is.True);
                NvencRunCaptureCompleteCleanupAttemptResult cleanupResult =
                    h.CleanupExecution.Execute(cleanupOperation);
                Assert.That(cleanupResult.IsCleaned, Is.True);
                Assert.That(ReferenceEquals(cleanupResult.Cleaner, h.CleanupCleaner), Is.True);
                Assert.That(h.RunCoordinator.TryReflectCaptureCompleteCleanup(cleanupResult), Is.True);

                // ---- 7. lock release and Run completion ----
                Assert.That(h.RunCoordinator.TryPrepareSessionOwnershipRelease(
                    out NvencRunSessionOwnershipReleaseOperation releaseOperation), Is.True);
                Assert.That(h.RunCoordinator.TryReleaseSessionOwnership(
                    out NvencRunSessionOwnershipReleaseReceipt releaseReceipt), Is.True);
                Assert.That(releaseReceipt.IsIssuedFor(h.Releaser, releaseOperation), Is.True);

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);

                // ---- 8. the final state of the whole chain ----
                Assert.That(h.State.IsAccepting, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.State.IsRunAbandoned, Is.False);
                Assert.That(h.RunCoordinator.Disposition,
                    Is.EqualTo(NvencRunEvidenceDisposition.CaptureComplete));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.True);

                Assert.That(h.SessionIssue.OwnershipLease.IsReleaseComplete, Is.True);
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.False);
                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));

                // The staging side is gone and the published side is complete.
                Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
                Assert.That(Directory.Exists(sandbox.Layout.FinalRunRoot), Is.True);
                Assert.That(File.Exists(sandbox.FinalChunkPath), Is.True);
                Assert.That(File.Exists(sandbox.CaptureIndexPath), Is.True);
                Assert.That(File.Exists(sandbox.FinalInitPath), Is.True);
                Assert.That(File.Exists(sandbox.FinalReadyPath), Is.True);
                Assert.That(File.Exists(sandbox.FinalPlanPath), Is.False);
                Assert.That(File.Exists(sandbox.CaptureIndexTemporaryPath), Is.False);

                // The published bytes are the ones the Run graph produced.
                Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(chunkBytes));
                Assert.That(Sha256Hex(File.ReadAllBytes(sandbox.FinalChunkPath)),
                    Is.EqualTo(chunkHash));
                Assert.That(File.ReadAllBytes(sandbox.CaptureIndexPath), Is.EqualTo(expectedIndexBytes));
                Assert.That(indexOperation.TestRunId, Is.EqualTo(h.Context.TestRunId));
                Assert.That(indexOperation.RunInitializationId, Is.EqualTo(InitId));

                // ---- a re-completion is idempotent and re-runs nothing ----
                long finalChunkStamp = File.GetLastWriteTimeUtc(sandbox.FinalChunkPath).Ticks;
                long captureIndexStamp = File.GetLastWriteTimeUtc(sandbox.CaptureIndexPath).Ticks;

                Assert.That(h.RunCoordinator.TryCompleteRun(), Is.True);

                Assert.That(h.FirstHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.SecondHandle.DisposeCallCount, Is.EqualTo(1));
                Assert.That(h.Service.IsStopped, Is.True);
                Assert.That(File.GetLastWriteTimeUtc(sandbox.FinalChunkPath).Ticks,
                    Is.EqualTo(finalChunkStamp));
                Assert.That(File.GetLastWriteTimeUtc(sandbox.CaptureIndexPath).Ticks,
                    Is.EqualTo(captureIndexStamp));
                Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(chunkBytes));

                // ---- the next Run can be admitted ----
                Assert.That(h.State.TryBeginAdmission(), Is.True);
                h.State.EndAdmission();
            }
        }

        // ---- End-to-end fixture helpers ----

        private static void RequireWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The Phase 0.11 publication path requires Windows no-follow file handles.");
            }
        }

        private Sandbox CreateSandbox()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "zantetsuken-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);
            return new Sandbox(root);
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

        /// <summary>
        /// Drives the Run to a finalized chunk and a frozen trace using only the
        /// existing entries, so the publication chain starts from the same state
        /// a real Run reaches.
        /// </summary>
        private static void FinalizeChunkAndFreeze(Harness h)
        {
            h.AcceptAndAppendChunk(1, ChunkByteLength, Seed);
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            Assert.That(h.RunCoordinator.TryReflectCompletion(
                MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);

            // The Submit Worker drains and stops on its own: the real
            // conditions are confirmed inside a watchdog, never assumed from a
            // settle and never forced through its private state.
            WaitForSubmitDrain(h);

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            CollectTerminal(h, "worker did not converge the finalize request");

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");
            Assert.That(h.Worker.TeardownCompleted, Is.True);

            // Only a physically stopped Submit Worker may be disposed.
            h.SubmitWorker.Dispose();

            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(
                MakeForcedDropSet(h), MakeCheckpoint(h), out _), Is.True);
        }

        /// <summary>
        /// Confirms the Submit Worker's own drain the way the product reports
        /// it: the requested drain completed and the worker thread physically
        /// exited. Both are real conditions re-checked inside a bounded
        /// watchdog, so no settle observation stands in as evidence.
        /// </summary>
        private static void WaitForSubmitDrain(Harness h)
        {
            SpinWait.SpinUntil(
                () => h.SubmitWorker.DrainCompleted && h.SubmitWorker.IsStopped,
                WatchdogTimeoutMs);

            Assert.That(h.SubmitWorker.DrainCompleted, Is.True,
                "the submit worker did not complete its drain");
            Assert.That(h.SubmitWorker.IsStopped, Is.True,
                "the submit worker did not physically stop after its drain");
            Assert.That(h.SubmitWorker.TryGetFailure(out _), Is.False);
        }

        /// <summary>
        /// The real staging and final trees for one Run, plus the marker files
        /// a completed initialization would already have written.
        /// </summary>
        private sealed class Sandbox
        {
            internal Sandbox(string root)
            {
                Layout = new CaptureRunRootLayout(
                    Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

                Directory.CreateDirectory(StagingChunksDirectory);
                Directory.CreateDirectory(Layout.FinalRunRoot);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal string StagingChunksDirectory =>
                Path.Combine(Layout.StagingRunRoot, "chunks");

            internal string StagingChunkPath =>
                Path.Combine(Layout.StagingRunRoot, StagingChunkRelative);

            internal string FinalChunkPath =>
                Path.Combine(Layout.FinalRunRoot, StagingChunkRelative);

            internal string StagingPlanPath =>
                Path.Combine(Layout.StagingRunRoot, PublicationPlanName);

            internal string FinalPlanPath =>
                Path.Combine(Layout.FinalRunRoot, PublicationPlanName);

            internal string CaptureIndexPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string CaptureIndexTemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string StagingInitPath =>
                Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName);

            internal string StagingReadyPath =>
                Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName);

            internal string FinalInitPath =>
                Path.Combine(Layout.FinalRunRoot, RunInitializationMarkerName);

            internal string FinalReadyPath =>
                Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName);

            private static string StagingChunkRelative =>
                NvencRunChunkArtifactDescriptorFactory.StagingRelativePath
                    .Replace('/', Path.DirectorySeparatorChar);

            internal void WriteMarkers()
            {
                CaptureRunInitializationDocumentSet documents =
                    new CaptureRunInitializationDocumentSet(Layout, InitId);

                File.WriteAllBytes(StagingInitPath, documents.GetStagingInitializationBytes());
                File.WriteAllBytes(StagingReadyPath, documents.GetStagingReadyBytes());
                File.WriteAllBytes(FinalInitPath, documents.GetFinalInitializationBytes());
                File.WriteAllBytes(FinalReadyPath, documents.GetFinalReadyBytes());
            }
        }

        /// <summary>
        /// Writes the Run's one chunk to its real staging path and reports its
        /// real length and SHA-256, so the production publisher verifies a
        /// genuine file. No NVENC or GPU resource is involved.
        /// </summary>
        private sealed class SandboxChunkWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private readonly string _path;
            private readonly MemoryStream _bytes = new MemoryStream();

            internal SandboxChunkWriter(CaptureRunRootLayout layout)
            {
                _path = Path.Combine(
                    layout.StagingRunRoot,
                    NvencRunChunkArtifactDescriptorFactory.StagingRelativePath
                        .Replace('/', Path.DirectorySeparatorChar));
            }

            internal byte[] WrittenBytes { get; private set; }

            internal string ContentHash { get; private set; }

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                _bytes.Write(buffer, offset, validLength);
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(
                NvencRunChunkFinalizationOperation operation)
            {
                WrittenBytes = _bytes.ToArray();
                File.WriteAllBytes(_path, WrittenBytes);
                ContentHash = Sha256Hex(WrittenBytes);

                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, ContentHash);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        // ---- Helpers ----

        private static void WaitForServiceStop(Harness h, string message)
        {
            FieldInfo field = typeof(NvencRunPublicationService).GetField(
                "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
            Thread worker = (Thread)field?.GetValue(h.Service);
            if (worker != null)
            {
                Assert.That(worker.Join(WatchdogTimeoutMs), Is.True, message);
            }

            Assert.That(h.Service.IsStopped, Is.True, message);
        }

        private static void WaitForServiceState(
            Harness h,
            NvencRunPublicationServiceState expected,
            string message)
        {
            SpinWait.SpinUntil(() => h.Service.State == expected, WatchdogTimeoutMs);
            Assert.That(h.Service.State, Is.EqualTo(expected), message);
        }

        /// <summary>
        /// Collects the requested Run chunk terminal by confirming the real
        /// condition inside a watchdog. A settle observed after the request may
        /// be a raise that was already in flight when the request was accepted,
        /// so it is used only as a wake hint; see TerminalConvergence.
        /// </summary>
        private static NvencRunChunkTerminalOutcome CollectTerminal(Harness h, string message)
        {
            return TerminalConvergence.Collect(
                h.RunCoordinator.TryCollectTerminal, h.Worker, h.SettledEvent, WatchdogTimeoutMs, message);
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static ForcedDropFrameIdSet MakeForcedDropSet(Harness h)
        {
            h.DraftQueue.BeginProducerDrain();
            h.DraftQueue.CloseAfterProducerJoin();
            TerminalIntentOwnershipSnapshot snapshot = h.DraftQueue.CreateOwnershipSnapshot(0);
            return h.DraftRegistry.ForceDropPendingForFreeze(h.DraftQueue, snapshot);
        }

        private static FreezeTerminalCheckpoint MakeCheckpoint(Harness h)
        {
            return new FreezeTerminalCheckpoint(
                1000, 1, 1, Thread.CurrentThread.ManagedThreadId, h.Context.TestRunId);
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, 0, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue(
            CaptureRunRootLayout layout,
            out CountingHandle firstHandle,
            out CountingHandle secondHandle)
        {
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            firstHandle = new CountingHandle(pathSet.FirstLockPath);
            secondHandle = new CountingHandle(pathSet.SecondLockPath);
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents =
                new CaptureRunInitializationDocumentSet(layout, InitId);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(documents);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        // ---- Fakes ----

        /// <summary>
        /// A lock handle that counts its own releases, so this test can
        /// confirm the production releaser released each of them once.
        /// </summary>
        private sealed class CountingHandle : ICaptureRunLockHandle
        {
            private int _disposeCalls;

            internal CountingHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            internal int DisposeCallCount => _disposeCalls;

            public void Dispose()
            {
                _disposeCalls++;
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
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

        private sealed class PatternSource : INvencOutputBitstreamSource
        {
            private readonly int _length;
            private readonly byte _seed;

            internal PatternSource(int length, byte seed)
            {
                _length = length;
                _seed = seed;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = 1024;
                return true;
            }
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            public bool TryGetEvidence(in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                evidence = default;
                return false;
            }
        }

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter
        {
            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                return true;
            }
        }

        private sealed class FakeTeardown : INvencOutputWorkerTeardown
        {
            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class FakeMainThreadTeardown : INvencMainThreadTextureTeardown
        {
            internal NvencRunChunkContext BoundContext;

            public bool IsBoundTo(NvencRunChunkContext context)
            {
                return BoundContext != null && context != null && ReferenceEquals(BoundContext, context);
            }

            public NvencMainThreadTextureTeardownReceipt TearDown()
            {
                return new NvencMainThreadTextureTeardownReceipt(this, BoundContext);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal SandboxChunkWriter Writer;
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator FinalizationCoordinator;
            internal NvencRunChunkContext Context;
            internal NvencRunLocalRegistrySlot Slot;

            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal FakeOutputSource Source;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal FakeTeardown Teardown;
            internal FakeMainThreadTeardown MainThreadTeardown;

            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencCaptureRunCoordinator RunCoordinator;
            internal NvencCaptureBackendJoinCoordinator BackendJoin;
            internal ManualResetEventSlim SettledEvent;

            internal NvencRunPublicationPlanCommitter Committer;
            internal NvencRunArtifactPublisher Publisher;
            internal NvencRunCaptureIndexCommitter IndexCommitter;
            internal NvencRunCaptureCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal NvencRunCaptureCompleteCleaner CleanupCleaner;
            internal NvencRunCaptureCompleteCleanupExecutionCoordinator CleanupExecution;
            internal NvencRunSessionOwnershipReleaser Releaser;
            internal NvencRunSessionOwnershipReleaseExecutionCoordinator ReleaseExecution;

            internal CaptureRunRootLayout Layout;
            internal CaptureArtifactFileStore Store;
            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal CountingHandle FirstHandle;
            internal CountingHandle SecondHandle;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            private readonly Action _settledHandler;

            // Set only after Start returned normally, so teardown never treats
            // an unstarted Submit Worker as a live one.
            private bool _submitWorkerStarted;

            internal Harness(CaptureRunRootLayout layout)
            {
                Layout = layout;
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new SandboxChunkWriter(layout);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue(
                    layout,
                    out CountingHandle firstHandle,
                    out CountingHandle secondHandle);
                FirstHandle = firstHandle;
                SecondHandle = secondHandle;
                Context = new NvencRunChunkContext(SessionIssue, Sink, FinalizationCoordinator, "chunk/0");
                Slot = new NvencRunLocalRegistrySlot(Context);

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Source = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);

                SubmitSyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitSubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                SubmitReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitSyncSlots,
                    SubmitToOutputCredits, FrameCompletionCredits,
                    new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmitSubmissionQueue, OutputQueue,
                    WorkSlots, SampleSlots, SubmitReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                MainThreadTeardown = new FakeMainThreadTeardown { BoundContext = Context };
                BackendJoin = new NvencCaptureBackendJoinCoordinator(
                    State, SubmitWorker, Worker, Context,
                    WorkSlots, SampleSlots, SubmitSyncSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer, Processor, MainThreadTeardown);

                TraceLogger = new TraceLogger(16, Context.TestRunId);
                TraceRecorder = new TraceFlightRecorder(TraceLogger, 16, 2);
                TraceRunContext traceRunContext = new TraceRunContext(
                    Context.TestRunId, 1000, "build-1", "6000.3.22f1", Hash64, "scene-1", 12345, 0.02, 3, "High", 1,
                    new Vector3(0f, -4.9f, 0f));
                CaptureDraftRunContext draftRun = new CaptureDraftRunContext(traceRunContext, 100, 5);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(5, 4096, 2, 4);
                DraftRegistry = new CaptureFrameDraftRegistry(draftRun, traceProfile);
                DraftQueue = new CaptureFrameDraftTerminalIntentQueue(DraftRegistry, traceProfile);
                FreezeTerminalTraceBufferBuilder freezeBuilder = new FreezeTerminalTraceBufferBuilder(DraftRegistry);
                FreezeTerminalCoordinator = new CaptureFrameFreezeTerminalCoordinator(TraceRecorder, freezeBuilder);
                TraceFreeze = new NvencTraceFreezeCoordinator(
                    TraceLogger, TraceRecorder, FreezeTerminalCoordinator, Context, SessionIssue);

                // Every publication-side collaborator below is the
                // production concrete, wired to the one root layout and the
                // one artifact store this Run owns.
                Store = new CaptureArtifactFileStore(layout);

                Committer = new NvencRunPublicationPlanCommitter(layout);
                NvencRunPublicationPlanCommitExecutionCoordinator commitCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(Committer);
                Publisher = new NvencRunArtifactPublisher(Store);
                NvencRunArtifactPublicationExecutionCoordinator artifactCoordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(Publisher);
                IndexCommitter = new NvencRunCaptureIndexCommitter(layout);
                NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCoordinator =
                    new NvencRunCaptureIndexCommitExecutionCoordinator(IndexCommitter);
                RunCompleter = new NvencRunCaptureCompleter();
                NvencRunCaptureCompleteExecutionCoordinator captureCompleteCoordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(RunCompleter);
                Service = new NvencRunPublicationService(
                    State, commitCoordinator, artifactCoordinator, captureIndexCoordinator,
                    captureCompleteCoordinator);

                CleanupCleaner = new NvencRunCaptureCompleteCleaner(layout);
                CleanupExecution =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(CleanupCleaner);

                Releaser = new NvencRunSessionOwnershipReleaser();
                ReleaseExecution =
                    new NvencRunSessionOwnershipReleaseExecutionCoordinator(Releaser);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin,
                    SessionIssue, TraceFreeze, Service, CleanupExecution, ReleaseExecution);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            /// <summary>
            /// Builds the graph. It starts no worker explicitly and asserts
            /// nothing, so the caller's using owns the Harness before any
            /// assertion can fail. The Output Worker's own thread still starts
            /// inside its constructor, and <see cref="Dispose"/> covers it.
            /// </summary>
            internal static Harness Create(CaptureRunRootLayout layout)
            {
                return new Harness(layout);
            }

            /// <summary>
            /// Starts the workers from inside the caller's using, so every
            /// started thread is covered by <see cref="Dispose"/>. The Submit
            /// Worker runs for real: no private drain state is forced and no
            /// started thread is skipped.
            /// </summary>
            internal void StartWorkers()
            {
                SubmitWorker.Start();
                _submitWorkerStarted = true;

                SettledEvent.Reset();
                Worker.Notify();
                Assert.That(SettledEvent.Wait(WatchdogTimeoutMs), Is.True,
                    "worker did not settle initially");
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }

            internal void WaitForPhysicalStop(string message)
            {
                FieldInfo field = typeof(NvencOrderedOutputWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread workerThread = (Thread)field?.GetValue(Worker);
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }

            public void Dispose()
            {
                // A Submit Worker that was never started is not alive, and must
                // not be poisoned, notified, or waited on as though it were.
                bool submitWorkerAlive = _submitWorkerStarted && !SubmitWorker.IsStopped;

                // One poison covers both workers: a test that failed part way
                // through can have left either of them running.
                if (!Worker.IsStopped || submitWorkerAlive)
                {
                    State.TryPoison();
                }

                if (!Worker.IsStopped)
                {
                    Worker.Notify();
                }

                if (submitWorkerAlive)
                {
                    SubmitWorker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;

                // Only a live Submit Worker is waited on, and only its real stop
                // condition ends the wait.
                bool submitWorkerStopped = !submitWorkerAlive
                    || SpinWait.SpinUntil(() => SubmitWorker.IsStopped, WatchdogTimeoutMs);

                if (submitWorkerStopped)
                {
                    // Idempotent after the normal path's own disposal, and the
                    // release for a worker that was never started.
                    SubmitWorker.Dispose();
                }

                if (!Service.IsStopped)
                {
                    if (!Service.TryStopWithoutRequest())
                    {
                        State.TryPoison();
                        Service.Notify();
                    }
                }

                FieldInfo serviceField = typeof(NvencRunPublicationService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread serviceThread = (Thread)serviceField?.GetValue(Service);
                if (serviceThread != null)
                {
                    Assert.That(serviceThread.Join(WatchdogTimeoutMs), Is.True,
                        "publication service worker did not physically exit during teardown");
                }

                Service.Dispose();

                // Every worker that could raise into this event has stopped.
                SettledEvent.Dispose();

                // Reported last, after the rest of the teardown ran, and never
                // swallowed: a Submit Worker left running would pollute the
                // tests that follow.
                Assert.That(submitWorkerStopped, Is.True,
                    "the submit worker thread did not physically exit during teardown");
            }
        }
    }
}
