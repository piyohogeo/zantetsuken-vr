using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureArtifactStreamingVerificationContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";
        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private static CaptureArtifactVerificationExecutionDisposition Completed => CaptureArtifactVerificationExecutionDisposition.Completed;
        private static CaptureArtifactVerificationExecutionDisposition Deferred => CaptureArtifactVerificationExecutionDisposition.Deferred;

        // ---- Enum contracts ----

        [Test]
        public void FailureReason_ExplicitValuesAndAppendOnlyShape()
        {
            AssertEnumContract(typeof(CaptureArtifactVerificationFailureReason), new[]
            {
                "None", "FileAbsent", "ShorterThanDeclared", "LongerThanDeclared",
                "HashMismatch", "ReadIoFailure", "CheckedLengthOverflow",
                "FileChangedDuringRead", "ReparsePointOrInvalidFileKind",
                "PathOrRunCorrelationMismatch", "BufferUnavailable", "Cancelled"
            });
        }

        [Test]
        public void ExecutionDisposition_ExplicitValues()
        {
            AssertEnumContract(typeof(CaptureArtifactVerificationExecutionDisposition), new[]
            {
                "None", "Completed", "Deferred"
            });
        }

        [Test]
        public void RecoveryDisposition_AppendOnlyWithDeferred()
        {
            AssertEnumContract(typeof(CapturePublicationRecoveryDisposition), new[]
            {
                "None", "PublishMissingArtifacts", "CaptureComplete",
                "ArtifactSourceMissing", "RunRootCollision", "Deferred"
            });
        }

        private static void AssertEnumContract(Type type, string[] expectedNames)
        {
            Assert.That(type.IsPublic, Is.False);
            Assert.That(Enum.GetUnderlyingType(type), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetNames(type), Is.EqualTo(expectedNames));
            Array values = Enum.GetValues(type);
            Assert.That(values.Length, Is.EqualTo(expectedNames.Length));
            for (int i = 0; i < expectedNames.Length; i++)
            {
                Assert.That((int)values.GetValue(i), Is.EqualTo(i));
            }
        }

        // ---- Result contract ----

        [Test]
        public void Result_AllValidCombinations_Accepted()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact-bytes");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);

            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Mismatch, CaptureArtifactVerificationFailureReason.ShorterThanDeclared, 2).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Mismatch, CaptureArtifactVerificationFailureReason.LongerThanDeclared, 99).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Mismatch, CaptureArtifactVerificationFailureReason.HashMismatch, payload.LongLength).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.ReadIoFailure, 0).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.CheckedLengthOverflow, 0).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.FileChangedDuringRead, payload.LongLength).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind, payload.LongLength).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch, 0).IsValid, Is.True);
            Assert.That(R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.Cancelled, 0).IsValid, Is.True);
            Assert.That(R(descriptor, Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0).IsValid, Is.True);
        }

        [Test]
        public void Result_InvalidCombinations_Rejected()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact-bytes");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);

            // Deferred must never bind to a content status or a non-buffer reason.
            Assert.Throws<ArgumentException>(() => R(descriptor, Deferred, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0));
            Assert.Throws<ArgumentException>(() => R(descriptor, Deferred, CaptureArtifactVerificationStatus.Mismatch, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0));
            Assert.Throws<ArgumentException>(() => R(descriptor, Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.None, 0));
            Assert.Throws<ArgumentException>(() => R(descriptor, Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.BufferUnavailable, 5));

            // BufferUnavailable must never bind to Completed.
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0));

            // Completed + None status is never a valid result.
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.None, 0));

            // MatchesExpected only pairs with None and the declared length.
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.FileAbsent, payload.LongLength));
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, 0));

            // Absent only pairs with FileAbsent and zero observed length.
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.HashMismatch, 0));
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 5));

            // Mismatch only pairs with the three length/hash reasons.
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.Mismatch, CaptureArtifactVerificationFailureReason.None, 2));

            // Invalid only pairs with the six invalid reasons.
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.FileAbsent, 0));
            Assert.Throws<ArgumentException>(() => R(descriptor, Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.None, 0));

            // Uninitialized disposition is never accepted.
            Assert.Throws<ArgumentException>(() => R(descriptor, CaptureArtifactVerificationExecutionDisposition.None, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.None, 0));
        }

        [Test]
        public void Result_DefaultIsInvalid()
        {
            Assert.That(default(CaptureArtifactVerificationResult).IsValid, Is.False);
        }

        // ---- Streaming verifier ----

        [Test]
        public void Streaming_HashMatch_AndMaxReadWithinFixedBuffer()
        {
            byte[] payload = Encoding.UTF8.GetBytes("format-neutral artifact payload");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            byte[] buffer = new byte[4]; // force multiple reads
            RecordingStream stream = new RecordingStream(payload);

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(descriptor, stream, buffer);

            Assert.That(result.ExecutionDisposition, Is.EqualTo(Completed));
            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
            Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.None));
            Assert.That(result.ObservedByteLength, Is.EqualTo(payload.LongLength));
            Assert.That(stream.MaxReadRequest, Is.LessThanOrEqualTo(buffer.Length));
            Assert.That(stream.MaxReadRequest, Is.LessThanOrEqualTo(CaptureArtifactFileStore.VerificationBufferLength));
        }

        [Test]
        public void Streaming_HashMismatch()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            byte[] corrupted = Encoding.UTF8.GetBytes("artifact");
            corrupted[0] ^= 0xFF;

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(
                descriptor, new RecordingStream(corrupted), new byte[8]);

            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Mismatch));
            Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.HashMismatch));
            Assert.That(result.ObservedByteLength, Is.EqualTo(payload.LongLength));
        }

        [Test]
        public void Streaming_ShorterThanDeclared()
        {
            byte[] payload = Encoding.UTF8.GetBytes("abc");
            CaptureArtifactDescriptor descriptor = MakeDescriptorWithDeclaredLength("a", payload, 5);

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(
                descriptor, new RecordingStream(payload), new byte[16]);

            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Mismatch));
            Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.ShorterThanDeclared));
            Assert.That(result.ObservedByteLength, Is.EqualTo(3));
        }

        [Test]
        public void Streaming_LongerThanDeclared()
        {
            byte[] payload = Encoding.UTF8.GetBytes("abcdef");
            CaptureArtifactDescriptor descriptor = MakeDescriptorWithDeclaredLength("a", payload, 3);

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(
                descriptor, new RecordingStream(payload), new byte[16]);

            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Mismatch));
            Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.LongerThanDeclared));
            Assert.That(result.ObservedByteLength, Is.EqualTo(6));
        }

        [Test]
        public void Streaming_NonEmptyEofBoundary_MatchesExactly()
        {
            byte[] payload = new byte[17];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(
                descriptor, new RecordingStream(payload), new byte[5]);

            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
            Assert.That(result.ObservedByteLength, Is.EqualTo(17));
        }

        [Test]
        public void Streaming_MidReadFailure_Invalid()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact bytes");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            RecordingStream stream = new RecordingStream(payload) { ThrowOnReadCall = 2 };

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(
                descriptor, stream, new byte[4]);

            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
            Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.ReadIoFailure));
        }

        [Test]
        public void Streaming_LengthChangedDuringRead_Invalid()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact bytes");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            RecordingStream stream = new RecordingStream(payload);
            stream.OnAfterRead = () => stream.ReportedLength = stream.ReportedLength + 1;

            CaptureArtifactVerificationResult result = CaptureArtifactStreamingVerifier.Verify(
                descriptor, stream, new byte[64]);

            Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
            Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.FileChangedDuringRead));
        }

        [Test]
        public void Streaming_CheckedLengthAccumulation_IsChecked()
        {
            string verifier = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/CaptureArtifactStreamingVerifier.cs"));
            Assert.That(verifier, Does.Contain("checked(observed + read)"));
        }

        // ---- Production verification-path source contract ----

        [Test]
        public void Production_VerificationPath_HasNoReadAllBytesOrArtifactLengthAllocation()
        {
            string store = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/CaptureArtifactFileStore.cs"));
            string verifier = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/CaptureArtifactStreamingVerifier.cs"));

            Assert.That(store, Does.Not.Contain("File.ReadAllBytes"));
            Assert.That(verifier, Does.Not.Contain("File.ReadAllBytes"));
            Assert.That(store, Does.Not.Contain("new byte[descriptor.ByteLength"));
            Assert.That(store, Does.Not.Contain("new byte[(int)descriptor.ByteLength"));
            Assert.That(verifier, Does.Not.Contain("new byte[descriptor.ByteLength"));
            Assert.That(store, Does.Contain("CaptureArtifactNoFollowOpen.TryOpen"));

            string noFollow = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/CaptureArtifactNoFollowOpen.cs"));
            Assert.That(noFollow, Does.Contain("FileFlagOpenReparsePoint"));
            Assert.That(noFollow, Does.Contain("GetFileInformationByHandle"));
            Assert.That(noFollow, Does.Contain("GetFinalPathNameByHandle"));
        }

        [Test]
        public void Publish_Source_ReservesBufferBeforeMove()
        {
            string store = File.ReadAllText(Path.Combine(
                RepositoryRoot(), "Assets/Zantetsu/Runtime/Observability/CaptureArtifactFileStore.cs"));
            int rent = store.IndexOf("CaptureArtifactVerificationBufferPool.Lease lease = _verificationBufferPool.TryRent();", StringComparison.Ordinal);
            int move = store.IndexOf("File.Move(source, target);", StringComparison.Ordinal);
            Assert.That(rent, Is.GreaterThanOrEqualTo(0));
            Assert.That(move, Is.GreaterThan(rent));
        }

        // ---- Store buffer pool ----

        [Test]
        public void Store_BufferUnavailable_Deferred()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
                store.WriteStaging(new CaptureArtifactWriteRequest(descriptor, payload));

                CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
                Assert.That(held, Is.Not.Null);
                try
                {
                    CaptureArtifactVerificationResult result = store.VerifyStaging(descriptor);
                    Assert.That(result.ExecutionDisposition, Is.EqualTo(Deferred));
                    Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.None));
                    Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.BufferUnavailable));
                    Assert.That(result.ObservedByteLength, Is.EqualTo(0));
                }
                finally
                {
                    pool.Return(held);
                }

                Assert.That(pool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_BufferReturnedExactlyOnce_OnSuccessMismatchAndInvalid()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
                store.WriteStaging(new CaptureArtifactWriteRequest(descriptor, payload));
                string stagingPath = Path.Combine(layout.StagingRunRoot, descriptor.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar));

                // Success terminal path.
                Assert.That(store.VerifyStaging(descriptor).Status, Is.EqualTo(CaptureArtifactVerificationStatus.MatchesExpected));
                Assert.That(pool.OutstandingRentCount, Is.Zero);

                // Mismatch terminal path: overwrite with same-length wrong bytes.
                byte[] wrong = Encoding.UTF8.GetBytes("artifact PAYLOAD");
                File.WriteAllBytes(stagingPath, wrong);
                Assert.That(store.VerifyStaging(descriptor).Status, Is.EqualTo(CaptureArtifactVerificationStatus.Mismatch));
                Assert.That(pool.OutstandingRentCount, Is.Zero);

                // I/O failure terminal path: an exclusive lock prevents re-open
                // and the known open failure converges to Invalid + ReadIoFailure
                // without leaking, while the buffer is returned exactly once.
                File.WriteAllBytes(stagingPath, payload);
                using (FileStream locked = new FileStream(stagingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    CaptureArtifactVerificationResult result = store.VerifyStaging(descriptor);
                    Assert.That(result.ExecutionDisposition, Is.EqualTo(Completed));
                    Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
                    Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.ReadIoFailure));
                }
                Assert.That(pool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_Publish_DeferredStaging_DoesNotMove()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
                store.WriteStaging(new CaptureArtifactWriteRequest(descriptor, payload));

                string stagingPath = Path.Combine(layout.StagingRunRoot, descriptor.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar));
                string finalPath = Path.Combine(layout.FinalRunRoot, descriptor.FinalRelativePath.Replace('/', Path.DirectorySeparatorChar));

                CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
                try
                {
                    Assert.Throws<CaptureArtifactVerificationDeferredException>(() => store.Publish(descriptor));
                }
                finally
                {
                    pool.Return(held);
                }

                Assert.That(File.Exists(stagingPath), Is.True, "Staging must not be moved.");
                Assert.That(File.Exists(finalPath), Is.False, "Final must not be created.");
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_DirectoryAtArtifactPath_InvalidFileKind()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);

                string artifactPath = Path.Combine(layout.StagingRunRoot, descriptor.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(artifactPath);

                CaptureArtifactVerificationResult result = store.VerifyStaging(descriptor);
                Assert.That(result.ExecutionDisposition, Is.EqualTo(Completed));
                Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
                Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind));
                Assert.That(pool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_UnsupportedNoFollow_ThrowsBeforeAnyChange()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);

                // Inject an unsupported no-follow capability through the seam.
                // Capability insufficiency is not a content mismatch: the store
                // must refuse to exist before any Run root, Plan, or chunk is
                // created, so a normal artifact can never become a collision.
                CaptureArtifactNoFollowOpen.OverrideIsSupported(false);
                try
                {
                    Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                        () => new CaptureArtifactFileStore(
                            layout, new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength)));
                }
                finally
                {
                    CaptureArtifactNoFollowOpen.OverrideIsSupported(null);
                }

                // No filesystem change: the run roots were never created.
                Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
                Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_PublishReserved_ForeignReservation_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactFileStore storeA = new CaptureArtifactFileStore(
                    layout, new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength));
                CaptureArtifactFileStore storeB = new CaptureArtifactFileStore(
                    layout, new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength));

                CaptureArtifactPublishReservation reservationA = storeA.TryReservePublish();
                Assert.That(reservationA, Is.Not.Null);
                try
                {
                    // A reservation minted by store A must be rejected by store B
                    // before any filesystem change.
                    Assert.Throws<ArgumentException>(() => storeB.PublishReserved(descriptor, reservationA));
                }
                finally
                {
                    storeA.ReleasePublishReservation(reservationA);
                }

                Assert.That(storeA.VerificationBufferPool.OutstandingRentCount, Is.Zero);
                Assert.That(storeB.VerificationBufferPool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_PublishReserved_StaleReservation_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);

                CaptureArtifactPublishReservation reservation = store.TryReservePublish();
                Assert.That(reservation, Is.Not.Null);
                store.ReleasePublishReservation(reservation);

                // A returned reservation is no longer active and must be
                // rejected before any filesystem change.
                Assert.Throws<InvalidOperationException>(() => store.PublishReserved(descriptor, reservation));
                Assert.That(pool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Store_ParentDirectoryJunction_RejectsOutsideRootFile()
        {
            if (!CaptureArtifactNoFollowOpen.IsSupported)
            {
                Assert.Ignore("Parent-directory junction test requires Windows no-follow open support.");
            }

            (string sandbox, string staging, string final) = MakeSandbox();
            string outside = Path.Combine(sandbox, "outside");
            string junction = null;
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact payload");
                CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
                store.WriteStaging(new CaptureArtifactWriteRequest(descriptor, payload));

                string artifactsDir = Path.Combine(layout.StagingRunRoot, "artifacts");
                Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Combine(outside, "a.stage"), payload);

                // Replace the artifacts directory with a junction to an outside
                // directory, so the artifact path now traverses a parent
                // reparse point whose target lies outside the run root.
                Directory.Delete(artifactsDir, true);
                CreateJunction(artifactsDir, outside);
                junction = artifactsDir;

                CaptureArtifactVerificationResult result = store.VerifyStaging(descriptor);
                Assert.That(result.ExecutionDisposition, Is.EqualTo(Completed));
                Assert.That(result.Status, Is.EqualTo(CaptureArtifactVerificationStatus.Invalid));
                Assert.That(result.FailureReason, Is.EqualTo(CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch));
                Assert.That(pool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (junction != null && Directory.Exists(junction))
                {
                    Directory.Delete(junction, false); // remove the link, not the target
                }
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void BufferPool_StaleReturn_DoesNotReleaseCurrentLease()
        {
            CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(64);
            CaptureArtifactVerificationBufferPool.Lease first = pool.TryRent();
            Assert.That(first, Is.Not.Null);
            pool.Return(first);
            Assert.That(pool.OutstandingRentCount, Is.Zero);

            CaptureArtifactVerificationBufferPool.Lease second = pool.TryRent();
            Assert.That(second, Is.Not.Null);
            // A stale return of the first lease must not release the second borrow.
            pool.Return(first);
            Assert.That(pool.OutstandingRentCount, Is.EqualTo(1));

            pool.Return(second);
            Assert.That(pool.OutstandingRentCount, Is.Zero);
        }

        [Test]
        public void BufferPool_ConcurrentRent_YieldsAtMostOneLease()
        {
            CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(64);
            int successes = 0;
            List<CaptureArtifactVerificationBufferPool.Lease> leases = new List<CaptureArtifactVerificationBufferPool.Lease>();
            object gate = new object();
            Thread[] threads = new Thread[8];
            using (ManualResetEvent start = new ManualResetEvent(false))
            {
                for (int i = 0; i < threads.Length; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        start.WaitOne();
                        CaptureArtifactVerificationBufferPool.Lease lease = pool.TryRent();
                        if (lease != null)
                        {
                            lock (gate)
                            {
                                successes++;
                                leases.Add(lease);
                            }
                        }
                    });
                    threads[i].Start();
                }

                start.Set();
                for (int i = 0; i < threads.Length; i++) threads[i].Join();
            }

            Assert.That(successes, Is.EqualTo(1));
            foreach (CaptureArtifactVerificationBufferPool.Lease lease in leases) pool.Return(lease);
            Assert.That(pool.OutstandingRentCount, Is.Zero);
        }

        // ---- Recovery classification ----

        [Test]
        public void Recovery_DeferredVsCompletedInvalid_AreDistinct()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            CapturePublicationPlan plan = MakePlan(new[] { descriptor });

            CapturePublicationRecoverySnapshot deferredSnapshot = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));

            CapturePublicationRecoverySnapshot invalidSnapshot = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Invalid, CaptureArtifactVerificationFailureReason.ReadIoFailure, 0),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength));

            Assert.That(CapturePublicationRecoveryClassifier.Classify(deferredSnapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred));
            Assert.That(CapturePublicationRecoveryClassifier.Classify(invalidSnapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.RunRootCollision));
        }

        [Test]
        public void Recovery_DeferredAtAnyPosition_YieldsDeferred()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor[] descriptors =
            {
                MakeDescriptor("a", payload),
                MakeDescriptor("b", payload),
                MakeDescriptor("c", payload)
            };
            CapturePublicationPlan plan = MakePlan(descriptors);

            for (int deferredAt = 0; deferredAt < descriptors.Length; deferredAt++)
            {
                CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                    plan,
                    i => i == deferredAt
                        ? R(plan.GetArtifact(i), Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0)
                        : R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                    i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength));

                Assert.That(
                    CapturePublicationRecoveryClassifier.Classify(snapshot),
                    Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred),
                    "Deferred at index " + deferredAt + " must classify as Deferred.");
            }
        }

        [Test]
        public void Recovery_MismatchBeforeDeferred_YieldsDeferred()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor[] descriptors = { MakeDescriptor("a", payload), MakeDescriptor("b", payload) };
            CapturePublicationPlan plan = MakePlan(descriptors);

            // Mismatch at index 0, Deferred at index 1: Deferred must win
            // regardless of artifact order.
            CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                plan,
                i => i == 0
                    ? R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Mismatch, CaptureArtifactVerificationFailureReason.HashMismatch, payload.LongLength)
                    : R(plan.GetArtifact(i), Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength));

            Assert.That(CapturePublicationRecoveryClassifier.Classify(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred));
        }

        [Test]
        public void Recovery_DeferredExecutesZeroStoreMutations()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            CapturePublicationPlan plan = MakePlan(new[] { descriptor });
            CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Deferred, CaptureArtifactVerificationStatus.None, CaptureArtifactVerificationFailureReason.BufferUnavailable, 0),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));

            CountingStore store = new CountingStore();
            CapturePublicationRecoveryCoordinator coordinator = new CapturePublicationRecoveryCoordinator(store);

            Assert.That(coordinator.ExecuteMissing(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred));
            Assert.That(store.PublishCalls, Is.Zero);
        }

        [Test]
        public void Recovery_ExecuteMissing_NonReservableStore_DeferredWithoutPublish()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            CapturePublicationPlan plan = MakePlan(new[] { descriptor });
            CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));

            // A store without a reservation capability cannot guarantee that a
            // Deferred outcome leaves the file set unchanged, so publication is
            // refused before any publish call.
            DeferredPublishStore store = new DeferredPublishStore();
            CapturePublicationRecoveryCoordinator coordinator = new CapturePublicationRecoveryCoordinator(store);

            Assert.That(coordinator.ExecuteMissing(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred));
            Assert.That(store.PublishCalls, Is.Zero);
        }

        [Test]
        public void Recovery_ExecuteMissing_PartialPublishStore_DeferredWithoutChanges()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor[] descriptors = { MakeDescriptor("a", payload), MakeDescriptor("b", payload) };
            CapturePublicationPlan plan = MakePlan(descriptors);
            CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));

            // This store would publish the first artifact and then throw
            // Deferred for the second, but it cannot reserve the whole
            // execution, so the coordinator must refuse before the first
            // publish and leave the store untouched.
            PartialPublishStore store = new PartialPublishStore();
            CapturePublicationRecoveryCoordinator coordinator = new CapturePublicationRecoveryCoordinator(store);

            Assert.That(coordinator.ExecuteMissing(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred));
            Assert.That(store.PublishCalls, Is.Zero);
        }

        [Test]
        public void Recovery_ExecuteMissing_PreExhaustedBuffer_ReturnsDeferredWithoutChanges()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact");
                CaptureArtifactDescriptor a = MakeDescriptor("a", payload);
                CaptureArtifactDescriptor b = MakeDescriptor("b", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
                store.WriteStaging(new CaptureArtifactWriteRequest(a, payload));
                store.WriteStaging(new CaptureArtifactWriteRequest(b, payload));

                CapturePublicationPlan plan = MakePlan(new[] { a, b });
                CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                    plan,
                    i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                    i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));

                // Exhaust the buffer before ExecuteMissing: the whole execution
                // must become Deferred with zero filesystem changes.
                CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
                Assert.That(held, Is.Not.Null);
                try
                {
                    CapturePublicationRecoveryCoordinator coordinator = new CapturePublicationRecoveryCoordinator(store);
                    Assert.That(coordinator.ExecuteMissing(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.Deferred));
                }
                finally
                {
                    pool.Return(held);
                }

                Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, a.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.True, "First staging must not be moved.");
                Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, b.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.True, "Second staging must not be moved.");
                Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, a.FinalRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, b.FinalRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Recovery_ExecuteMissing_PublishesAllWhenBufferAvailable()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes("artifact");
                CaptureArtifactDescriptor a = MakeDescriptor("a", payload);
                CaptureArtifactDescriptor b = MakeDescriptor("b", payload);
                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 3);
                CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
                CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
                store.WriteStaging(new CaptureArtifactWriteRequest(a, payload));
                store.WriteStaging(new CaptureArtifactWriteRequest(b, payload));

                CapturePublicationPlan plan = MakePlan(new[] { a, b });
                CapturePublicationRecoverySnapshot snapshot = MakeSnapshot(
                    plan,
                    i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                    i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));

                CapturePublicationRecoveryCoordinator coordinator = new CapturePublicationRecoveryCoordinator(store);
                Assert.That(coordinator.ExecuteMissing(snapshot), Is.EqualTo(CapturePublicationRecoveryDisposition.CaptureComplete));

                Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, a.FinalRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.True);
                Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, b.FinalRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.True);
                Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, a.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, b.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar))), Is.False);
                Assert.That(pool.OutstandingRentCount, Is.Zero);
            }
            finally
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            }
        }

        [Test]
        public void Recovery_CompletedClassification_Unchanged()
        {
            byte[] payload = Encoding.UTF8.GetBytes("artifact");
            CaptureArtifactDescriptor descriptor = MakeDescriptor("a", payload);
            CapturePublicationPlan plan = MakePlan(new[] { descriptor });

            // staging matches, final absent -> PublishMissingArtifacts.
            CapturePublicationRecoverySnapshot missingFinal = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));
            Assert.That(CapturePublicationRecoveryClassifier.Classify(missingFinal), Is.EqualTo(CapturePublicationRecoveryDisposition.PublishMissingArtifacts));

            // staging absent, final absent -> ArtifactSourceMissing.
            CapturePublicationRecoverySnapshot missingSource = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.Absent, CaptureArtifactVerificationFailureReason.FileAbsent, 0));
            Assert.That(CapturePublicationRecoveryClassifier.Classify(missingSource), Is.EqualTo(CapturePublicationRecoveryDisposition.ArtifactSourceMissing));

            // staging and final both match -> CaptureComplete.
            CapturePublicationRecoverySnapshot complete = MakeSnapshot(
                plan,
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength),
                i => R(plan.GetArtifact(i), Completed, CaptureArtifactVerificationStatus.MatchesExpected, CaptureArtifactVerificationFailureReason.None, payload.LongLength));
            Assert.That(CapturePublicationRecoveryClassifier.Classify(complete), Is.EqualTo(CapturePublicationRecoveryDisposition.CaptureComplete));
        }

        // ---- Helpers ----

        private static CaptureArtifactVerificationResult R(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationExecutionDisposition disposition,
            CaptureArtifactVerificationStatus status,
            CaptureArtifactVerificationFailureReason reason,
            long observedByteLength)
        {
            return new CaptureArtifactVerificationResult(descriptor, disposition, status, reason, observedByteLength);
        }

        private static CaptureArtifactDescriptor MakeDescriptor(string id, byte[] payload)
        {
            return new CaptureArtifactDescriptor(
                id, CaptureArtifactKind.TraceBundle, "application/octet-stream", 1,
                "artifacts/" + id + ".stage", "artifacts/" + id, payload.LongLength, Hash(payload));
        }

        private static CaptureArtifactDescriptor MakeDescriptorWithDeclaredLength(string id, byte[] payload, long declaredLength)
        {
            return new CaptureArtifactDescriptor(
                id, CaptureArtifactKind.TraceBundle, "application/octet-stream", 1,
                "artifacts/" + id + ".stage", "artifacts/" + id, declaredLength, Hash(payload));
        }

        private static CapturePublicationPlan MakePlan(CaptureArtifactDescriptor[] descriptors)
        {
            string[] ids = new string[descriptors.Length];
            for (int i = 0; i < descriptors.Length; i++) ids[i] = descriptors[i].ArtifactId;
            return new CapturePublicationPlan(
                1, InitId, HashA, descriptors, new[] { new CaptureFrameEvidenceEntry(1, ids) });
        }

        private static CapturePublicationRecoverySnapshot MakeSnapshot(
            CapturePublicationPlan plan,
            Func<int, CaptureArtifactVerificationResult> stagingFactory,
            Func<int, CaptureArtifactVerificationResult> finalFactory)
        {
            CaptureArtifactRecoveryObservation[] observations = new CaptureArtifactRecoveryObservation[plan.ArtifactCount];
            for (int i = 0; i < observations.Length; i++)
            {
                observations[i] = new CaptureArtifactRecoveryObservation(
                    plan.GetArtifact(i), stagingFactory(i), finalFactory(i));
            }
            return new CapturePublicationRecoverySnapshot(plan, observations);
        }

        private static string Hash(byte[] payload)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(payload);
                StringBuilder sb = new StringBuilder(64);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsu-stream-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            return (sandbox, staging, final);
        }

        private static void CreateJunction(string linkPath, string targetPath)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process process = Process.Start(startInfo))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("mklink /J failed: " + process.StandardError.ReadToEnd());
                }
            }
        }

        private sealed class RecordingStream : Stream
        {
            private readonly byte[] _content;
            private long _position;

            internal RecordingStream(byte[] content)
            {
                _content = content ?? throw new ArgumentNullException(nameof(content));
                ReportedLength = content.Length;
            }

            internal long ReportedLength { get; set; }
            internal int ThrowOnReadCall { get; set; }
            internal Action OnAfterRead { get; set; }
            internal int MaxReadRequest { get; private set; }
            private int _readCalls;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => ReportedLength;
            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count > MaxReadRequest) MaxReadRequest = count;
                _readCalls++;
                if (ThrowOnReadCall != 0 && _readCalls == ThrowOnReadCall)
                {
                    throw new IOException("injected read failure");
                }

                int available = (int)Math.Min(count, _content.Length - _position);
                if (available <= 0) return 0;
                Array.Copy(_content, _position, buffer, offset, available);
                _position += available;
                OnAfterRead?.Invoke();
                return available;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class CountingStore : ICaptureArtifactStore
        {
            internal int PublishCalls;

            public CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactPublishReceipt Publish(CaptureArtifactDescriptor descriptor)
            {
                PublishCalls++;
                return null;
            }

            public CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class DeferredPublishStore : ICaptureArtifactStore
        {
            internal int PublishCalls;

            public CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactPublishReceipt Publish(CaptureArtifactDescriptor descriptor)
            {
                PublishCalls++;
                throw new CaptureArtifactVerificationDeferredException("buffer unavailable");
            }

            public CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class PartialPublishStore : ICaptureArtifactStore
        {
            internal int PublishCalls;
            private int _published;

            public CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactPublishReceipt Publish(CaptureArtifactDescriptor descriptor)
            {
                PublishCalls++;
                if (_published++ == 0)
                {
                    return new CaptureArtifactPublishReceipt(this, descriptor, "C:\\final\\" + descriptor.ArtifactId);
                }

                throw new CaptureArtifactVerificationDeferredException("buffer unavailable");
            }

            public CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }

            public CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor)
            {
                throw new NotSupportedException();
            }
        }
    }
}
