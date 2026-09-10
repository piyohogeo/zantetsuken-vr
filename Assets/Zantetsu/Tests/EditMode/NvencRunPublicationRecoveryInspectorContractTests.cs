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
    /// Contract tests for the Phase 0.11 NVENC publication recovery inspector
    /// and its execution coordinator: the read-only observation order, the
    /// bounded plan read, the single streamed chunk verification, and the
    /// snapshot they hand to the pure classifier.
    /// </summary>
    /// <remarks>
    /// The opener is a small recording fake, so every no-follow status is
    /// reachable without junctions or permission tricks; the bytes it serves
    /// come from a tiny temporary sandbox because the production open result
    /// carries a <see cref="FileStream"/>. One Windows-only case runs the real
    /// no-follow backend end to end. No sleep, probabilistic repetition, or
    /// reflection over production private state is used.
    /// </remarks>
    public class NvencRunPublicationRecoveryInspectorContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string ForeignInitId = "fedcba9876543210fedcba9876543210";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const int ChunkByteLength = 512;

        private const int BufferLength = 64;

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
        public void Inspect_NullOrInvalidOperation_RejectedWithoutTouchingTheFilesystem()
        {
            Harness h = MakeHarness();

            ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(
                () => h.Inspector.Inspect(null));
            Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
            Assert.That(h.Opener.Opens, Is.Empty);

            // Releasing the lock invalidates the operation through the ordinary
            // ownership API.
            h.Owner.Dispose();
            Assert.That(h.Operation.IsValid, Is.False);

            ArgumentException invalidEx = Assert.Throws<ArgumentException>(
                () => h.Inspector.Inspect(h.Operation));
            Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
            Assert.That(h.Opener.Opens, Is.Empty);
            Assert.That(h.Pool.OutstandingRentCount, Is.EqualTo(0));
        }

        // ---- Execution coordinator ----

        [Test]
        public void Coordinator_NullInspector_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryInspectionExecutionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("inspector"));
        }

        [Test]
        public void Coordinator_ValidOperation_CallsTheInspectorExactlyOnce()
        {
            Harness h = MakeHarness();
            FakeInspector inspector = new FakeInspector(h.Inspector);
            NvencRunPublicationRecoveryInspectionExecutionCoordinator coordinator =
                new NvencRunPublicationRecoveryInspectionExecutionCoordinator(inspector);

            Assert.That(ReferenceEquals(coordinator.Inspector, inspector), Is.True);

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = coordinator.Execute(h.Operation);

            Assert.That(inspector.CallCount, Is.EqualTo(1));
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(ReferenceEquals(snapshot.Operation, h.Operation), Is.True);
            Assert.That(snapshot.IsValid, Is.True);
        }

        [Test]
        public void Coordinator_NullOperation_RejectedWithoutContact()
        {
            FakeInspector inspector = new FakeInspector(null);

            Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryInspectionExecutionCoordinator(inspector)
                    .Execute(null));
            Assert.That(inspector.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Coordinator_NullForeignOrInvalidSnapshot_Rejected()
        {
            // Null.
            AssertSnapshotRejected((h, operation) => null, "a null snapshot must be rejected");

            // A snapshot describing another Run's operation.
            AssertSnapshotRejected(
                (h, operation) =>
                {
                    Harness other = MakeHarness();
                    return new NvencRunPublicationRecoveryInspectionSnapshot(
                        other.Operation,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        false,
                        default);
                },
                "a snapshot of a foreign operation must be rejected");

            // A snapshot whose operation lost its lock after it was built.
            AssertSnapshotRejected(
                (h, operation) =>
                {
                    NvencRunPublicationRecoveryInspectionSnapshot snapshot =
                        new NvencRunPublicationRecoveryInspectionSnapshot(
                            operation,
                            CaptureRunPublicationDocumentObservationStatus.Absent,
                            null,
                            false,
                            default);
                    h.Owner.Dispose();
                    return snapshot;
                },
                "an invalid snapshot must be rejected");
        }

        [Test]
        public void Coordinator_InspectorException_PropagatesSameReference()
        {
            Harness h = MakeHarness();
            IOException failure = new IOException("inspection failed");
            FakeInspector inspector = new FakeInspector(null) { ExceptionToThrow = failure };

            IOException thrown = Assert.Throws<IOException>(
                () => new NvencRunPublicationRecoveryInspectionExecutionCoordinator(inspector)
                    .Execute(h.Operation));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(inspector.CallCount, Is.EqualTo(1));
        }

        // ---- Incomplete ----

        [Test]
        public void NoPlanAndNoTemporary_ObservesIncompleteWithoutTouchingTheChunk()
        {
            Harness h = MakeHarness();

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Absent));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.False);
            AssertChunkUntouched(h);
        }

        [Test]
        public void OnlyTheTemporary_ObservesIncomplete_AndNeverReadsIt()
        {
            Harness h = MakeHarness();
            h.WriteTemporary(new byte[] { 1, 2, 3, 4 });

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);

            // Existence is the whole observation: the temporary's stream was
            // opened, never read, and closed.
            RecordingStream temporary = h.Opener.StreamFor(h.StagingRoot, TemporaryBasename);
            Assert.That(temporary, Is.Not.Null);
            Assert.That(temporary.ReadCalls, Is.EqualTo(0));
            Assert.That(temporary.Disposed, Is.True);
            AssertChunkUntouched(h);
        }

        // ---- Collision without the chunk ----

        [Test]
        public void PlanAndTemporaryTogether_ObservesCollision_WithoutChunkOrBuffer()
        {
            Harness h = MakeHarness();
            h.WritePlan(h.MakePlan());
            h.WriteTemporary(new byte[] { 9 });

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Canonical));
            Assert.That(snapshot.HasChunkVerificationResult, Is.False);
            AssertChunkUntouched(h);
        }

        [Test]
        public void ForeignOrOffShapePlan_ObservesCollision_WithoutTouchingTheChunk()
        {
            foreach (bool foreignRun in new[] { true, false })
            {
                Harness h = MakeHarness();
                h.WritePlan(foreignRun
                    ? h.MakePlan(runInitializationId: ForeignInitId)
                    : h.MakeOffShapePlan());

                NvencRunPublicationRecoveryInspectionSnapshot snapshot =
                    h.Inspector.Inspect(h.Operation);

                Assert.That(Classify(snapshot), Is.EqualTo(
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
                Assert.That(snapshot.HasChunkVerificationResult, Is.False);
                AssertChunkUntouched(h);
            }
        }

        [Test]
        public void InvalidPlanBytes_ObservesInvalid()
        {
            Harness h = MakeHarness();
            h.WritePlanBytes(new byte[] { (byte)'{', (byte)'}' });

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Invalid));
            Assert.That(snapshot.PublicationPlan, Is.Null);
            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            AssertChunkUntouched(h);
        }

        [Test]
        public void EmptyPlan_ObservesInvalid()
        {
            Harness h = MakeHarness();
            h.WritePlanBytes(new byte[0]);

            Assert.That(h.Inspector.Inspect(h.Operation).PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.Invalid));
        }

        [Test]
        public void PlanOverTheLimit_ObservesLimitExceeded_AndStopsAtOneBytePastIt()
        {
            Harness h = MakeHarness();
            h.WriteOversizedPlan();

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.PublicationPlanStatus,
                Is.EqualTo(CaptureRunPublicationDocumentObservationStatus.LimitExceeded));
            Assert.That(snapshot.PublicationPlan, Is.Null);
            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));

            // Exactly one byte past the limit is observed and nothing more.
            RecordingStream plan = h.Opener.StreamFor(h.StagingRoot, PlanBasename);
            Assert.That(plan.TotalBytesRead,
                Is.EqualTo(CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1));
            Assert.That(plan.LargestReadRequest,
                Is.LessThanOrEqualTo(CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1));
            Assert.That(plan.Disposed, Is.True);
            AssertChunkUntouched(h);
        }

        // ---- Chunk verification ----

        [Test]
        public void CanonicalPlanAndMatchingChunk_ObservesPublicationRecoveryRequired()
        {
            Harness h = MakeHarness();
            CapturePublicationPlan written = h.MakePlan();
            h.WritePlan(written);
            h.WriteChunk(h.ChunkBytes);

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);
            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(snapshot);

            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));

            // The plan the snapshot holds is the one that was read, and the
            // verification names that plan's own descriptor by reference.
            Assert.That(snapshot.PublicationPlan, Is.Not.Null);
            Assert.That(ReferenceEquals(decision.AuthoritativePlan, snapshot.PublicationPlan), Is.True);
            Assert.That(ReferenceEquals(
                    snapshot.ChunkVerification.Descriptor, snapshot.PublicationPlan.GetArtifact(0)),
                Is.True);
            Assert.That(snapshot.ChunkVerification.Status,
                Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));

            // The chunk was streamed once through the one fixed buffer: no read
            // ever asked for more than the buffer and it took several of them.
            RecordingStream chunk = h.Opener.StreamFor(h.FinalRoot, ChunkRelativePath);
            Assert.That(chunk.LargestReadRequest, Is.EqualTo(BufferLength));
            Assert.That(chunk.ReadCalls, Is.GreaterThan(1));
            Assert.That(chunk.TotalBytesRead, Is.EqualTo(ChunkByteLength));

            // The plan was opened once and the chunk once: no re-open.
            Assert.That(h.Opener.Opens, Is.EqualTo(new List<string>
            {
                Key(h.StagingRoot, TemporaryBasename),
                Key(h.StagingRoot, PlanBasename),
                Key(h.FinalRoot, ChunkRelativePath),
            }));

            AssertEverythingReleased(h);
        }

        [Test]
        public void CanonicalPlanWithAbsentChunk_ObservesCollision()
        {
            Harness h = MakeHarness();
            h.WritePlan(h.MakePlan());

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.ChunkVerification.Status,
                Is.EqualTo(CaptureArtifactVerificationStatus.Absent));
            Assert.That(snapshot.ChunkVerification.FailureReason,
                Is.EqualTo(CaptureArtifactVerificationFailureReason.FileAbsent));
            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            AssertEverythingReleased(h);
        }

        [Test]
        public void CanonicalPlanWithLengthOrHashMismatch_ObservesCollision()
        {
            // Shorter than declared.
            Harness shorter = MakeHarness();
            shorter.WritePlan(shorter.MakePlan());
            shorter.WriteChunk(new byte[ChunkByteLength - 1]);

            NvencRunPublicationRecoveryInspectionSnapshot shortSnapshot =
                shorter.Inspector.Inspect(shorter.Operation);
            Assert.That(shortSnapshot.ChunkVerification.Status,
                Is.EqualTo(CaptureArtifactVerificationStatus.Mismatch));
            Assert.That(Classify(shortSnapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            AssertEverythingReleased(shorter);

            // Right length, wrong bytes.
            Harness hashed = MakeHarness();
            hashed.WritePlan(hashed.MakePlan());
            byte[] different = new byte[ChunkByteLength];
            different[0] = 0xFF;
            hashed.WriteChunk(different);

            NvencRunPublicationRecoveryInspectionSnapshot hashSnapshot =
                hashed.Inspector.Inspect(hashed.Operation);
            Assert.That(hashSnapshot.ChunkVerification.Status,
                Is.EqualTo(CaptureArtifactVerificationStatus.Mismatch));
            Assert.That(hashSnapshot.ChunkVerification.FailureReason,
                Is.EqualTo(CaptureArtifactVerificationFailureReason.HashMismatch));
            Assert.That(Classify(hashSnapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            AssertEverythingReleased(hashed);
        }

        [Test]
        public void CanonicalPlanWithUnusableChunkKind_ObservesCollision()
        {
            foreach (var pair in new[]
            {
                (CaptureArtifactNoFollowOpenStatus.InvalidFileKind,
                    CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind),
                (CaptureArtifactNoFollowOpenStatus.EscapesRoot,
                    CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch),
                (CaptureArtifactNoFollowOpenStatus.IoFailure,
                    CaptureArtifactVerificationFailureReason.ReadIoFailure),
            })
            {
                Harness h = MakeHarness();
                h.WritePlan(h.MakePlan());
                h.Opener.SetStatus(h.FinalRoot, ChunkRelativePath, pair.Item1);

                NvencRunPublicationRecoveryInspectionSnapshot snapshot =
                    h.Inspector.Inspect(h.Operation);

                Assert.That(snapshot.ChunkVerification.ExecutionDisposition,
                    Is.EqualTo(CaptureArtifactVerificationExecutionDisposition.Completed));
                Assert.That(snapshot.ChunkVerification.Status,
                    Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
                Assert.That(snapshot.ChunkVerification.FailureReason, Is.EqualTo(pair.Item2));
                Assert.That(Classify(snapshot), Is.EqualTo(
                    NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
                AssertEverythingReleased(h);
            }
        }

        [Test]
        public void ChunkReadFailureMidStream_ObservesCompletedInvalid()
        {
            Harness h = MakeHarness();
            h.WritePlan(h.MakePlan());
            h.WriteChunk(h.ChunkBytes);
            h.Opener.FailReadsAfter(h.FinalRoot, ChunkRelativePath, 1);

            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Inspector.Inspect(h.Operation);

            Assert.That(snapshot.ChunkVerification.ExecutionDisposition,
                Is.EqualTo(CaptureArtifactVerificationExecutionDisposition.Completed));
            Assert.That(snapshot.ChunkVerification.Status,
                Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
            Assert.That(snapshot.ChunkVerification.FailureReason,
                Is.EqualTo(CaptureArtifactVerificationFailureReason.ReadIoFailure));
            Assert.That(Classify(snapshot), Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));
            AssertEverythingReleased(h);
        }

        [Test]
        public void BufferUnavailable_ObservesDeferred_WithoutTouchingTheChunk()
        {
            Harness h = MakeHarness();
            h.WritePlan(h.MakePlan());
            h.WriteChunk(h.ChunkBytes);

            byte[] chunkBefore = File.ReadAllBytes(h.ChunkPath);

            // The pool holds exactly one buffer; renting it makes the chunk
            // verification impossible, so the chunk is never opened and no
            // observed file is changed.
            CaptureArtifactVerificationBufferPool.Lease held = h.Pool.TryRent();
            Assert.That(held, Is.Not.Null);
            try
            {
                NvencRunPublicationRecoveryInspectionSnapshot snapshot =
                    h.Inspector.Inspect(h.Operation);

                Assert.That(snapshot.ChunkVerification.ExecutionDisposition,
                    Is.EqualTo(CaptureArtifactVerificationExecutionDisposition.Deferred));
                Assert.That(snapshot.ChunkVerification.FailureReason,
                    Is.EqualTo(CaptureArtifactVerificationFailureReason.BufferUnavailable));
                Assert.That(ReferenceEquals(
                        snapshot.ChunkVerification.Descriptor,
                        snapshot.PublicationPlan.GetArtifact(0)),
                    Is.True);
                Assert.That(Classify(snapshot), Is.EqualTo(
                    NvencRunPublicationRecoveryDisposition.Deferred));

                // The chunk was never opened, and only the test's own lease is
                // outstanding: the inspection took none of its own.
                Assert.That(h.Opener.Opens, Has.No.Member(Key(h.FinalRoot, ChunkRelativePath)));
                Assert.That(h.Pool.OutstandingRentCount, Is.EqualTo(1));
                foreach (RecordingStream stream in h.Opener.Streams)
                {
                    Assert.That(stream.Disposed, Is.True);
                }
            }
            finally
            {
                h.Pool.Return(held);
            }

            Assert.That(File.ReadAllBytes(h.ChunkPath), Is.EqualTo(chunkBefore));
        }

        // ---- Read-only ----

        [Test]
        public void Inspection_LeavesEveryObservedFileUnchanged_AndNeverTouchesTheLegacyTemporary()
        {
            Harness h = MakeHarness();
            h.WritePlan(h.MakePlan());
            h.WriteChunk(h.ChunkBytes);
            h.WriteTemporary(new byte[] { 5, 6 });

            // The Phase 0/0.1 temporary is present and must stay untouched.
            string legacy = Path.Combine(h.StagingRoot, "publication.plan.tmp");
            File.WriteAllBytes(legacy, new byte[] { 7, 7, 7 });

            string planHash = Sha256Hex(File.ReadAllBytes(h.PlanPath));
            string chunkHash = Sha256Hex(File.ReadAllBytes(h.ChunkPath));
            string temporaryHash = Sha256Hex(File.ReadAllBytes(h.TemporaryPath));
            string legacyHash = Sha256Hex(File.ReadAllBytes(legacy));

            h.Inspector.Inspect(h.Operation);

            Assert.That(File.Exists(h.PlanPath), Is.True);
            Assert.That(File.Exists(h.ChunkPath), Is.True);
            Assert.That(File.Exists(h.TemporaryPath), Is.True);
            Assert.That(File.Exists(legacy), Is.True);
            Assert.That(Sha256Hex(File.ReadAllBytes(h.PlanPath)), Is.EqualTo(planHash));
            Assert.That(Sha256Hex(File.ReadAllBytes(h.ChunkPath)), Is.EqualTo(chunkHash));
            Assert.That(Sha256Hex(File.ReadAllBytes(h.TemporaryPath)), Is.EqualTo(temporaryHash));
            Assert.That(Sha256Hex(File.ReadAllBytes(legacy)), Is.EqualTo(legacyHash));

            // The legacy name was never opened.
            Assert.That(h.Opener.Opens, Has.No.Member(Key(h.StagingRoot, "publication.plan.tmp")));

            // The lock is still held.
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Operation.IsValid, Is.True);
        }

        // ---- Windows backend integration ----

        [Test]
        public void Backend_CanonicalPlanAndMatchingChunk_ObservesPublicationRecoveryRequired()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The recovery inspector backend requires Windows no-follow file handles.");
            }

            Harness h = MakeHarness();
            h.WritePlan(h.MakePlan());
            h.WriteChunk(h.ChunkBytes);

            NvencRunPublicationRecoveryInspector inspector = new NvencRunPublicationRecoveryInspector(
                CaptureArtifactNoFollowOpen.Create(),
                new CaptureArtifactVerificationBufferPool(BufferLength));

            NvencRunPublicationRecoveryDecision decision = NvencRunPublicationRecoveryClassifier.Classify(
                inspector.Inspect(h.Operation));

            Assert.That(decision.Disposition, Is.EqualTo(
                NvencRunPublicationRecoveryDisposition.PublicationRecoveryRequired));
            Assert.That(decision.AuthoritativePlan, Is.Not.Null);
            Assert.That(decision.AuthoritativePlan.GetArtifact(0).ContentHash,
                Is.EqualTo(Sha256Hex(h.ChunkBytes)));

            // Every handle the inspection opened is released.
            using (new FileStream(h.PlanPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            using (new FileStream(h.ChunkPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }

        // ---- Fixture helpers ----

        private const string PlanBasename = "publication.plan";

        private const string TemporaryBasename = "publication.plan.nvenc-precommit.tmp";

        private static readonly string ChunkRelativePath =
            NvencRunChunkArtifactDescriptorFactory.FinalRelativePath;

        private static NvencRunPublicationRecoveryDisposition Classify(
            NvencRunPublicationRecoveryInspectionSnapshot snapshot)
        {
            return NvencRunPublicationRecoveryClassifier.Classify(snapshot).Disposition;
        }

        private static string Key(string root, string relativePath)
        {
            return root + "|" + relativePath;
        }

        private static void AssertChunkUntouched(Harness h)
        {
            Assert.That(h.Opener.Opens, Has.No.Member(Key(h.FinalRoot, ChunkRelativePath)));
            Assert.That(h.Pool.OutstandingRentCount, Is.EqualTo(0));
            AssertEverythingReleased(h);
        }

        private static void AssertEverythingReleased(Harness h)
        {
            foreach (RecordingStream stream in h.Opener.Streams)
            {
                Assert.That(stream.Disposed, Is.True, "every opened stream must be released.");
            }

            Assert.That(h.Pool.OutstandingRentCount, Is.EqualTo(0),
                "the verification buffer must be returned.");
        }

        private void AssertSnapshotRejected(
            Func<Harness, NvencRunPublicationRecoveryInspectionOperation,
                NvencRunPublicationRecoveryInspectionSnapshot> forge,
            string message)
        {
            Harness h = MakeHarness();
            FakeInspector inspector = new FakeInspector(null)
            {
                Forge = () => forge(h, h.Operation),
            };

            Assert.Throws<InvalidOperationException>(
                () => new NvencRunPublicationRecoveryInspectionExecutionCoordinator(inspector)
                    .Execute(h.Operation),
                message);
            Assert.That(inspector.CallCount, Is.EqualTo(1), message);
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

        private Harness MakeHarness()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "zantetsuken-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

            CaptureRunInitializationOpenOutcome outcome = MakeRecoveryOutcome(
                layout, out CaptureRunInitializationSessionOwnershipLease owner);

            return new Harness(
                layout,
                new NvencRunPublicationRecoveryInspectionOperation(outcome, layout),
                owner);
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
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
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
                Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired));

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
        /// One Run's sandbox, recording opener, single-buffer pool, and the
        /// inspector under test.
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                NvencRunPublicationRecoveryInspectionOperation operation,
                CaptureRunInitializationSessionOwnershipLease owner)
            {
                Layout = layout;
                Operation = operation;
                Owner = owner;

                Directory.CreateDirectory(StagingRoot);
                Directory.CreateDirectory(Path.Combine(FinalRoot, "chunks"));

                ChunkBytes = new byte[ChunkByteLength];
                for (int i = 0; i < ChunkBytes.Length; i++)
                {
                    ChunkBytes[i] = (byte)(i * 7);
                }

                Opener = new RecordingOpener();
                Pool = new CaptureArtifactVerificationBufferPool(BufferLength);
                Inspector = new NvencRunPublicationRecoveryInspector(Opener, Pool);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunPublicationRecoveryInspectionOperation Operation { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal RecordingOpener Opener { get; }

            internal CaptureArtifactVerificationBufferPool Pool { get; }

            internal NvencRunPublicationRecoveryInspector Inspector { get; }

            internal byte[] ChunkBytes { get; }

            internal string StagingRoot => Layout.StagingRunRoot;

            internal string FinalRoot => Layout.FinalRunRoot;

            internal string PlanPath => Path.Combine(StagingRoot, PlanBasename);

            internal string TemporaryPath => Path.Combine(StagingRoot, TemporaryBasename);

            internal string ChunkPath => Path.Combine(
                FinalRoot, ChunkRelativePath.Replace('/', Path.DirectorySeparatorChar));

            internal CapturePublicationPlan MakePlan(
                string runInitializationId = null,
                CaptureArtifactDescriptor[] descriptors = null,
                int frameCount = 2)
            {
                CaptureArtifactDescriptor[] artifacts = descriptors
                    ?? new[]
                    {
                        NvencRunChunkArtifactDescriptorFactory.Create(
                            ArtifactId, ChunkByteLength, Sha256Hex(ChunkBytes)),
                    };

                string[] ids = new string[artifacts.Length];
                for (int i = 0; i < artifacts.Length; i++)
                {
                    ids[i] = artifacts[i].ArtifactId;
                }

                Array.Sort(ids, StringComparer.Ordinal);

                CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[frameCount];
                for (int i = 0; i < frameCount; i++)
                {
                    entries[i] = new CaptureFrameEvidenceEntry(i + 1, ids);
                }

                return new CapturePublicationPlan(
                    Layout.TestRunId,
                    runInitializationId ?? InitId,
                    Hash64,
                    artifacts,
                    entries);
            }

            internal CapturePublicationPlan MakeOffShapePlan()
            {
                return MakePlan(descriptors: new[]
                {
                    new CaptureArtifactDescriptor(
                        ArtifactId,
                        CaptureArtifactKind.FrameSequence,
                        "PngFrameSequence",
                        1,
                        NvencRunChunkArtifactDescriptorFactory.StagingRelativePath,
                        NvencRunChunkArtifactDescriptorFactory.FinalRelativePath,
                        ChunkByteLength,
                        Sha256Hex(ChunkBytes)),
                });
            }

            internal void WritePlan(CapturePublicationPlan plan)
            {
                WritePlanBytes(CapturePublicationPlanCodec.SerializeCanonical(plan));
            }

            internal void WritePlanBytes(byte[] bytes)
            {
                File.WriteAllBytes(PlanPath, bytes);
                Opener.Serve(StagingRoot, PlanBasename, PlanPath);
            }

            internal void WriteOversizedPlan()
            {
                using (FileStream stream = new FileStream(
                    PlanPath, FileMode.Create, FileAccess.Write, FileShare.None))
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

                Opener.Serve(StagingRoot, PlanBasename, PlanPath);
            }

            internal void WriteTemporary(byte[] bytes)
            {
                File.WriteAllBytes(TemporaryPath, bytes);
                Opener.Serve(StagingRoot, TemporaryBasename, TemporaryPath);
            }

            internal void WriteChunk(byte[] bytes)
            {
                File.WriteAllBytes(ChunkPath, bytes);
                Opener.Serve(FinalRoot, ChunkRelativePath, ChunkPath);
            }
        }

        /// <summary>
        /// Records the exact open order and serves either a real backing file or
        /// a fixed no-follow status. Absent is the default, so a file the test
        /// did not write is simply not there.
        /// </summary>
        private sealed class RecordingOpener : ICaptureArtifactNoFollowOpener
        {
            private readonly Dictionary<string, string> _files = new Dictionary<string, string>();
            private readonly Dictionary<string, CaptureArtifactNoFollowOpenStatus> _statuses =
                new Dictionary<string, CaptureArtifactNoFollowOpenStatus>();
            private readonly Dictionary<string, int> _failReadsAfter = new Dictionary<string, int>();
            private readonly Dictionary<string, RecordingStream> _streams =
                new Dictionary<string, RecordingStream>();

            internal bool Supported { get; set; } = true;

            internal List<string> Opens { get; } = new List<string>();

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

            internal void FailReadsAfter(string root, string relativePath, int reads)
            {
                _failReadsAfter[Key(root, relativePath)] = reads;
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
                if (_failReadsAfter.TryGetValue(key, out int reads))
                {
                    stream.FailAfterReads = reads;
                }

                _streams[key] = stream;
                Streams.Add(stream);
                return CaptureArtifactNoFollowOpenResult.Opened(stream, null);
            }
        }

        /// <summary>
        /// A read-only stream over a real backing file that records how it was
        /// read and can fail part way through.
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

            internal int FailAfterReads { get; set; } = -1;

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                if (count > LargestReadRequest)
                {
                    LargestReadRequest = count;
                }

                if (FailAfterReads >= 0 && ReadCalls > FailAfterReads)
                {
                    throw new IOException("read failed");
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

        private sealed class FakeInspector : INvencRunPublicationRecoveryInspector
        {
            private readonly INvencRunPublicationRecoveryInspector _inner;

            internal FakeInspector(INvencRunPublicationRecoveryInspector inner)
            {
                _inner = inner;
            }

            internal int CallCount { get; private set; }

            internal Exception ExceptionToThrow { get; set; }

            internal Func<NvencRunPublicationRecoveryInspectionSnapshot> Forge { get; set; }

            public NvencRunPublicationRecoveryInspectionSnapshot Inspect(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CallCount++;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (Forge != null)
                {
                    return Forge();
                }

                return _inner.Inspect(operation);
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
