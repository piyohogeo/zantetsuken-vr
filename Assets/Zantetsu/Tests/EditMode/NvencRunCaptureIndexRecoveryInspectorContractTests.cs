using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC Capture Index recovery
    /// inspector and its execution coordinator: the fixed observation order
    /// under the final Run root, the bounded read, what each observation status
    /// may and may not mean, and the snapshot they produce.
    /// </summary>
    /// <remarks>
    /// The opener is a small recording fake, so every no-follow status is
    /// reachable without junctions or permission tricks; the bytes it serves
    /// come from a small temporary sandbox because the production open result
    /// carries a <see cref="FileStream"/>. One Windows-only case runs the real
    /// no-follow backend end to end. No sleep, probabilistic repetition, or
    /// reflection over production private state is used.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryInspectorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string OtherHash64 =
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const string TemporaryName = "capture.index.tmp";

        private const string FinalName = "capture.index";

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }

            _owners.Clear();

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

        // ---- Rejection before any filesystem contact ----

        [Test]
        public void Constructor_NullOpener_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryInspector(null));

            Assert.That(ex.ParamName, Is.EqualTo("opener"));
        }

        [Test]
        public void Inspect_NullOrInvalidOperation_RejectedWithoutTouchingTheFilesystem()
        {
            Harness h = MakeHarness();

            ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                () => h.Inspector.Inspect(null));
            Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
            Assert.That(h.Opener.Opens, Is.Empty);

            h.Owner.Dispose();
            Assert.That(h.Operation.IsValid, Is.False);

            ArgumentException invalidEx = Assert.Throws<ArgumentException>(
                () => h.Inspector.Inspect(h.Operation));
            Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
            Assert.That(h.Opener.Opens, Is.Empty);
        }

        [Test]
        public void Inspect_WithoutNoFollowSupport_RefusesWithoutTouchingTheFilesystem()
        {
            Harness h = MakeHarness();
            h.Opener.Supported = false;

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => h.Inspector.Inspect(h.Operation));
            Assert.That(h.Opener.Opens, Is.Empty);
        }

        // ---- Observation order and paths ----

        [Test]
        public void Inspect_ObservesTheTemporaryThenTheFinal_UnderTheFinalRunRoot()
        {
            Harness h = MakeHarness();
            h.WriteTemporary(h.AuthoritativeBytes);
            h.WriteFinal(h.AuthoritativeBytes);

            h.Inspector.Inspect(h.Operation);

            Assert.That(h.Opener.Opens, Is.EqualTo(new List<string>
            {
                Key(h.FinalRoot, TemporaryName),
                Key(h.FinalRoot, FinalName),
            }));

            // Both roots are the exact final Run root; the staging root is
            // never opened.
            Assert.That(h.Opener.Roots, Is.EqualTo(new List<string> { h.FinalRoot, h.FinalRoot }));
            Assert.That(h.Opener.Roots, Has.No.Member(h.StagingRoot));
        }

        // ---- Content statuses ----

        [Test]
        public void NeitherDocumentPresent_ObservesAbsentTwice()
        {
            Harness h = MakeHarness();

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.TemporaryIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Absent));
            Assert.That(snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Absent));
            Assert.That(ReferenceEquals(snapshot.Operation, h.Operation), Is.True);
            Assert.That(snapshot.IsValid, Is.True);
            AssertEverythingReleased(h);
        }

        [Test]
        public void AuthoritativeBytesInBothDocuments_ObserveMatchesAuthoritative()
        {
            Harness h = MakeHarness();
            h.WriteTemporary(h.AuthoritativeBytes);
            h.WriteFinal(h.AuthoritativeBytes);

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.TemporaryIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.MatchesAuthoritative));
            Assert.That(snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.MatchesAuthoritative));
            AssertEverythingReleased(h);
        }

        [Test]
        public void CanonicalBytesOfAnotherPlan_ObserveCanonicalMismatchInBothDocuments()
        {
            Harness h = MakeHarness();
            byte[] foreignBytes = CapturePublicationPlanCodec.SerializeCanonical(h.MakeForeignPlan());
            h.WriteTemporary(foreignBytes);
            h.WriteFinal(foreignBytes);

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                h.Inspector.Inspect(h.Operation);

            // One expected byte sequence, applied consistently to both
            // documents.
            Assert.That(snapshot.TemporaryIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.CanonicalMismatch));
            Assert.That(snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.CanonicalMismatch));
            AssertEverythingReleased(h);
        }

        [Test]
        public void EmptyOrNonCanonicalBytes_ObserveInvalid()
        {
            Harness h = MakeHarness();
            h.WriteTemporary(new byte[0]);
            h.WriteFinal(new byte[] { (byte)'{', (byte)'}' });

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.TemporaryIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Invalid));
            Assert.That(snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Invalid));
            AssertEverythingReleased(h);
        }

        [Test]
        public void DocumentOverTheLimit_ObservesLimitExceeded_AndStopsAtOneBytePastIt()
        {
            Harness h = MakeHarness();
            h.WriteOversizedFinal();

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.LimitExceeded));

            RecordingStream final = h.Opener.StreamFor(h.FinalRoot, FinalName);
            Assert.That(final.TotalBytesRead,
                Is.EqualTo(CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1));
            Assert.That(final.LargestReadRequest,
                Is.LessThanOrEqualTo(CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1));
            AssertEverythingReleased(h);
        }

        // ---- Unusable observations are never content statuses ----

        [Test]
        public void ReparsePointOrInvalidFileKind_ThrowsWithoutASnapshot()
        {
            AssertRefusedObservation<IOException>(
                CaptureArtifactNoFollowOpenStatus.InvalidFileKind);
        }

        [Test]
        public void PathEscapingTheRunRoot_ThrowsWithoutASnapshot()
        {
            AssertRefusedObservation<IOException>(CaptureArtifactNoFollowOpenStatus.EscapesRoot);
        }

        [Test]
        public void OpenIoFailure_ThrowsWithoutASnapshot()
        {
            AssertRefusedObservation<IOException>(CaptureArtifactNoFollowOpenStatus.IoFailure);
        }

        [Test]
        public void ReadFailureMidStream_PropagatesTheSameExceptionWithoutASnapshot()
        {
            Harness h = MakeHarness();

            // Larger than the reader's initial buffer, so the failure lands
            // between two reads rather than on the first one.
            h.WriteFinal(new byte[256 * 1024]);
            IOException failure = new IOException("read failed");
            h.Opener.FailReadsAfter(h.FinalRoot, FinalName, 1, failure);

            IOException thrown = Assert.Throws<IOException>(
                () => h.Inspector.Inspect(h.Operation));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            AssertEverythingReleased(h);
        }

        // ---- Read-only ----

        [Test]
        public void Inspection_LeavesBothDocumentsByteForByteUnchanged()
        {
            Harness h = MakeHarness();
            h.WriteTemporary(CapturePublicationPlanCodec.SerializeCanonical(h.MakeForeignPlan()));
            h.WriteFinal(h.AuthoritativeBytes);

            long temporaryLength = new FileInfo(h.TemporaryPath).Length;
            long finalLength = new FileInfo(h.FinalPath).Length;
            string temporaryHash = Sha256Hex(File.ReadAllBytes(h.TemporaryPath));
            string finalHash = Sha256Hex(File.ReadAllBytes(h.FinalPath));

            h.Inspector.Inspect(h.Operation);

            Assert.That(File.Exists(h.TemporaryPath), Is.True);
            Assert.That(File.Exists(h.FinalPath), Is.True);
            Assert.That(new FileInfo(h.TemporaryPath).Length, Is.EqualTo(temporaryLength));
            Assert.That(new FileInfo(h.FinalPath).Length, Is.EqualTo(finalLength));
            Assert.That(Sha256Hex(File.ReadAllBytes(h.TemporaryPath)), Is.EqualTo(temporaryHash));
            Assert.That(Sha256Hex(File.ReadAllBytes(h.FinalPath)), Is.EqualTo(finalHash));
            Assert.That(h.Owner.IsCreated, Is.True);
        }

        // ---- Execution coordinator ----

        [Test]
        public void Coordinator_NullInspector_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(null));

            Assert.That(ex.ParamName, Is.EqualTo("inspector"));
        }

        [Test]
        public void Coordinator_ValidOperation_CallsTheInspectorOnceAndReturnsItsExactSnapshot()
        {
            Harness h = MakeHarness();
            h.WriteFinal(h.AuthoritativeBytes);
            FakeInspector inspector = new FakeInspector(h.Inspector);

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(inspector)
                    .Execute(h.Operation);

            Assert.That(inspector.CallCount, Is.EqualTo(1));
            Assert.That(ReferenceEquals(snapshot, inspector.LastSnapshot), Is.True);
            Assert.That(ReferenceEquals(snapshot.Operation, h.Operation), Is.True);
        }

        [Test]
        public void Coordinator_NullOrInvalidOperation_RejectedWithoutInspectorContact()
        {
            Harness h = MakeHarness();
            FakeInspector inspector = new FakeInspector(h.Inspector);
            NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator coordinator =
                new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(inspector);

            Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));

            h.Owner.Dispose();

            Assert.Throws<ArgumentException>(() => coordinator.Execute(h.Operation));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
            Assert.That(h.Opener.Opens, Is.Empty);
        }

        [Test]
        public void Coordinator_NullForeignOrInvalidSnapshot_Rejected()
        {
            // Null.
            AssertSnapshotRejected((h, operation) => null);

            // A snapshot describing another Run's operation.
            AssertSnapshotRejected((h, operation) =>
                new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    MakeHarness().Operation,
                    NvencRunCaptureIndexObservationStatus.Absent,
                    NvencRunCaptureIndexObservationStatus.Absent));

            // A snapshot whose operation lost its lock after it was built.
            AssertSnapshotRejected((h, operation) =>
            {
                NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                    new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                        operation,
                        NvencRunCaptureIndexObservationStatus.Absent,
                        NvencRunCaptureIndexObservationStatus.Absent);
                h.Owner.Dispose();
                return snapshot;
            });
        }

        [Test]
        public void Coordinator_InspectorException_PropagatesSameReference()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("inspection failed");
            FakeInspector inspector = new FakeInspector(null) { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(inspector)
                    .Execute(h.Operation));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(inspector.CallCount, Is.EqualTo(1));
        }

        // ---- Windows backend integration ----

        [Test]
        public void Backend_AuthoritativeFinalIndexAndNoTemporary_IsObservedThroughRealHandles()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The Capture Index recovery inspector requires Windows no-follow file handles.");
            }

            Harness h = MakeHarness();
            h.WriteFinal(h.AuthoritativeBytes);

            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot =
                new NvencRunCaptureIndexRecoveryInspector(CaptureArtifactNoFollowOpen.Create())
                    .Inspect(h.Operation);

            Assert.That(snapshot.FinalIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.MatchesAuthoritative));
            Assert.That(snapshot.TemporaryIndexStatus,
                Is.EqualTo(NvencRunCaptureIndexObservationStatus.Absent));

            // Every handle the inspection opened is released.
            using (new FileStream(h.FinalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }

        // ---- Fixture helpers ----

        private static string Key(string root, string relativePath)
        {
            return root + "|" + relativePath;
        }

        private static void AssertEverythingReleased(Harness h)
        {
            foreach (RecordingStream stream in h.Opener.Streams)
            {
                Assert.That(stream.Disposed, Is.True, "every opened stream must be released.");
            }
        }

        private void AssertRefusedObservation<TException>(
            CaptureArtifactNoFollowOpenStatus status)
            where TException : Exception
        {
            // The same unusable observation on either document must refuse the
            // whole inspection rather than become Absent or Invalid.
            foreach (string basename in new[] { TemporaryName, FinalName })
            {
                Harness h = MakeHarness();
                h.WriteTemporary(h.AuthoritativeBytes);
                h.WriteFinal(h.AuthoritativeBytes);
                h.Opener.SetStatus(h.FinalRoot, basename, status);

                Assert.Throws<TException>(() => h.Inspector.Inspect(h.Operation), basename);
                AssertEverythingReleased(h);
            }
        }

        private void AssertSnapshotRejected(
            Func<Harness, NvencRunCaptureIndexRecoveryInspectionOperation,
                NvencRunCaptureIndexRecoveryInspectionSnapshot> forge)
        {
            Harness h = MakeHarness();
            FakeInspector inspector = new FakeInspector(null)
            {
                Forge = () => forge(h, h.Operation),
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunCaptureIndexRecoveryInspectionExecutionCoordinator(inspector)
                    .Execute(h.Operation));
            Assert.That(inspector.CallCount, Is.EqualTo(1));
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes));
            }
        }

        private Harness MakeHarness()
        {
            string sandbox = Path.Combine(
                Path.GetTempPath(), "zantetsuken-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(sandbox, "staging"), Path.Combine(sandbox, "final"), 1);

            NvencRunPublicationRecoveryInspectionOperation recoveryOperation =
                new NvencRunPublicationRecoveryInspectionOperation(
                    MakeRecoveryOutcome(layout, out CaptureRunInitializationSessionOwnershipLease owner),
                    layout);
            CapturePublicationPlan plan = MakePlan(
                recoveryOperation.TestRunId, recoveryOperation.RunInitializationId, Hash64);

            NvencRunPublicationRecoveryDecision recovery = NvencRunPublicationRecoveryClassifier.Classify(
                new NvencRunPublicationRecoveryInspectionSnapshot(
                    recoveryOperation,
                    CaptureRunPublicationDocumentObservationStatus.Canonical,
                    plan,
                    false,
                    new CaptureArtifactVerificationResult(
                        plan.GetArtifact(0),
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.MatchesExpected,
                        CaptureArtifactVerificationFailureReason.None,
                        ChunkByteLength)));

            return new Harness(
                layout,
                new NvencRunCaptureIndexRecoveryInspectionOperation(recovery),
                owner);
        }

        private static CapturePublicationPlan MakePlan(
            long testRunId, string runInitializationId, string writerHash)
        {
            CaptureArtifactDescriptor[] artifacts = new[]
            {
                NvencRunChunkArtifactDescriptorFactory.Create(ArtifactId, ChunkByteLength, Hash64),
            };

            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
            }

            return new CapturePublicationPlan(
                testRunId, runInitializationId, writerHash, artifacts, entries);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                CaptureRunRootRole.Staging,
                binding.StagingInitialization,
                binding.StagingReady,
                hasNonMarkerEntry: true);
            CaptureRunInitializationRootObservation final = MakeRootObservation(
                CaptureRunRootRole.Final,
                binding.FinalInitialization,
                binding.FinalReady,
                hasNonMarkerEntry: false);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(staging, final),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
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

        /// <summary>
        /// One Run's sandbox, recording opener, and the inspector under test.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                NvencRunCaptureIndexRecoveryInspectionOperation operation,
                CaptureRunInitializationSessionOwnershipLease owner)
            {
                Layout = layout;
                Operation = operation;
                Owner = owner;

                Directory.CreateDirectory(StagingRoot);
                Directory.CreateDirectory(FinalRoot);

                AuthoritativeBytes = CapturePublicationPlanCodec.SerializeCanonical(
                    operation.AuthoritativePlan);

                Opener = new RecordingOpener();
                Inspector = new NvencRunCaptureIndexRecoveryInspector(Opener);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunCaptureIndexRecoveryInspectionOperation Operation { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal RecordingOpener Opener { get; }

            internal NvencRunCaptureIndexRecoveryInspector Inspector { get; }

            internal byte[] AuthoritativeBytes { get; }

            internal string StagingRoot => Layout.StagingRunRoot;

            internal string FinalRoot => Layout.FinalRunRoot;

            internal string TemporaryPath => Path.Combine(FinalRoot, TemporaryName);

            internal string FinalPath => Path.Combine(FinalRoot, FinalName);

            internal CapturePublicationPlan MakeForeignPlan()
            {
                return MakePlan(
                    Operation.TestRunId, Operation.RunInitializationId, OtherHash64);
            }

            internal void WriteTemporary(byte[] bytes)
            {
                File.WriteAllBytes(TemporaryPath, bytes);
                Opener.Serve(FinalRoot, TemporaryName, TemporaryPath);
            }

            internal void WriteFinal(byte[] bytes)
            {
                File.WriteAllBytes(FinalPath, bytes);
                Opener.Serve(FinalRoot, FinalName, FinalPath);
            }

            internal void WriteOversizedFinal()
            {
                using (FileStream stream = new FileStream(
                    FinalPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] block = new byte[64 * 1024];
                    long remaining = CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1L;
                    while (remaining > 0)
                    {
                        int count = (int)Math.Min(block.Length, remaining);
                        stream.Write(block, 0, count);
                        remaining -= count;
                    }
                }

                Opener.Serve(FinalRoot, FinalName, FinalPath);
            }
        }

        /// <summary>
        /// Records the exact open order, root, and basename, and serves either a
        /// real backing file or a fixed no-follow status. Absent is the default,
        /// so a document the test did not write is simply not there.
        /// </summary>
        private sealed class RecordingOpener : ICaptureArtifactNoFollowOpener
        {
            private readonly Dictionary<string, string> _files = new Dictionary<string, string>();
            private readonly Dictionary<string, CaptureArtifactNoFollowOpenStatus> _statuses =
                new Dictionary<string, CaptureArtifactNoFollowOpenStatus>();
            private readonly Dictionary<string, Exception> _readFailures =
                new Dictionary<string, Exception>();
            private readonly Dictionary<string, int> _failAfterReads =
                new Dictionary<string, int>();
            private readonly Dictionary<string, RecordingStream> _streams =
                new Dictionary<string, RecordingStream>();

            internal bool Supported { get; set; } = true;

            internal List<string> Opens { get; } = new List<string>();

            internal List<string> Roots { get; } = new List<string>();

            internal List<RecordingStream> Streams { get; } = new List<RecordingStream>();

            public bool IsSupported => Supported;

            internal void Serve(string root, string relativePath, string backingFile)
            {
                _files[Key(root, relativePath)] = backingFile;
            }

            internal void SetStatus(
                string root, string relativePath, CaptureArtifactNoFollowOpenStatus status)
            {
                _statuses[Key(root, relativePath)] = status;
            }

            internal void FailReadsAfter(
                string root, string relativePath, int reads, Exception failure)
            {
                _readFailures[Key(root, relativePath)] = failure;
                _failAfterReads[Key(root, relativePath)] = reads;
            }

            internal RecordingStream StreamFor(string root, string relativePath)
            {
                return _streams.TryGetValue(Key(root, relativePath), out RecordingStream stream)
                    ? stream
                    : null;
            }

            public CaptureArtifactNoFollowOpenResult TryOpen(string root, string relativePath)
            {
                string key = Key(root, relativePath);
                Opens.Add(key);
                Roots.Add(root);

                if (_statuses.TryGetValue(key, out CaptureArtifactNoFollowOpenStatus status))
                {
                    return CaptureArtifactNoFollowOpenResult.Of(status);
                }

                if (!_files.TryGetValue(key, out string backingFile))
                {
                    return CaptureArtifactNoFollowOpenResult.Of(
                        CaptureArtifactNoFollowOpenStatus.Absent);
                }

                RecordingStream stream = new RecordingStream(backingFile);
                if (_readFailures.TryGetValue(key, out Exception failure))
                {
                    stream.ReadFailure = failure;
                    stream.FailAfterReads = _failAfterReads[key];
                }

                _streams[key] = stream;
                Streams.Add(stream);
                return CaptureArtifactNoFollowOpenResult.Opened(stream, null);
            }
        }

        /// <summary>
        /// A read-only stream over a real backing file that records how it was
        /// read and can fail on its first read.
        /// </summary>
        private sealed class RecordingStream : FileStream
        {
            internal RecordingStream(string path)
                : base(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            {
            }

            internal int ReadCalls { get; private set; }

            internal long TotalBytesRead { get; private set; }

            internal int LargestReadRequest { get; private set; }

            internal bool Disposed { get; private set; }

            internal Exception ReadFailure { get; set; }

            internal int FailAfterReads { get; set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                if (count > LargestReadRequest)
                {
                    LargestReadRequest = count;
                }

                if (ReadFailure != null && ReadCalls > FailAfterReads)
                {
                    throw ReadFailure;
                }

                int read = base.Read(buffer, offset, count);
                TotalBytesRead += read;
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class FakeInspector : INvencRunCaptureIndexRecoveryInspector
        {
            private readonly INvencRunCaptureIndexRecoveryInspector _inner;

            internal FakeInspector(INvencRunCaptureIndexRecoveryInspector inner)
            {
                _inner = inner;
            }

            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal Func<NvencRunCaptureIndexRecoveryInspectionSnapshot> Forge { get; set; }

            internal NvencRunCaptureIndexRecoveryInspectionSnapshot LastSnapshot { get; private set; }

            public NvencRunCaptureIndexRecoveryInspectionSnapshot Inspect(
                NvencRunCaptureIndexRecoveryInspectionOperation operation)
            {
                CallCount++;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                LastSnapshot = Forge != null ? Forge() : _inner.Inspect(operation);
                return LastSnapshot;
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            internal FakeHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
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
