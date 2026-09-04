using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCapturePublicationArtifactPublisherContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }

            _owners.Clear();

            for (int i = _sandboxes.Count - 1; i >= 0; i--)
            {
                string sandbox = _sandboxes[i];
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, true);
                }
            }

            _sandboxes.Clear();
        }

        // ---- Type shape / source contract ----

        [Test]
        public void Publisher_Type_InternalSealed_ImplementsInterface_NotDisposable()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactPublisher);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsClass, Is.True);
            Assert.That(typeof(IPngJsonCapturePublicationArtifactPublisher).IsAssignableFrom(type), Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null), Is.Null);
        }

        [Test]
        public void Publisher_Constructor_NullStore_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new PngJsonCapturePublicationArtifactPublisher(null));
        }

        [Test]
        public void Publisher_Body_SingleReadonlyStoreField_NoAttemptCollectionOrReservationMapping()
        {
            Type type = typeof(PngJsonCapturePublicationArtifactPublisher);

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(instanceFields.Length, Is.EqualTo(1));
            FieldInfo field = instanceFields[0];
            Assert.That(field.FieldType, Is.EqualTo(typeof(CaptureArtifactFileStore)));
            Assert.That(field.IsInitOnly, Is.True);

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty);

            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublisher.cs");
            Assert.That(source, Does.Not.Contain("List<"));
            Assert.That(source, Does.Not.Contain("Dictionary"));
            Assert.That(source, Does.Not.Contain("HashSet"));
            Assert.That(source, Does.Not.Contain("Concurrent"));
            Assert.That(source, Does.Not.Contain("static readonly"));
            Assert.That(source, Does.Not.Contain("Registry"));
        }

        [Test]
        public void Attempt_PrivateNested_InterfaceStaysEmpty()
        {
            // The public attempt contract exposes no members.
            Type attemptInterface = typeof(IPngJsonCapturePublicationArtifactPublishAttempt);
            Assert.That(attemptInterface.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty);
            Assert.That(attemptInterface.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty);

            // The concrete attempt is private and nested inside the publisher.
            Type[] nested = typeof(PngJsonCapturePublicationArtifactPublisher).GetNestedTypes(BindingFlags.NonPublic);
            Type attempt = Array.Find(nested, t => !t.IsVisible && typeof(IPngJsonCapturePublicationArtifactPublishAttempt).IsAssignableFrom(t));
            Assert.That(attempt, Is.Not.Null);
            Assert.That(attempt.IsNestedPrivate, Is.True);

            // It holds exactly publisher, batch, token, reservation, and ended.
            FieldInfo[] fields = attempt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(fields.Length, Is.EqualTo(5));
        }

        [Test]
        public void Source_PublishReserved_NoAdditionalReservation_AndReceiptCheck()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublisher.cs");

            // PublishReserved must never acquire a second reservation.
            int tryReserve = source.IndexOf("TryReservePublish", StringComparison.Ordinal);
            int publishReserved = source.IndexOf("_store.PublishReserved", StringComparison.Ordinal);
            Assert.That(tryReserve, Is.GreaterThanOrEqualTo(0));
            Assert.That(publishReserved, Is.GreaterThan(tryReserve));
            string publishBody = source.Substring(source.IndexOf("public PngJsonCapturePublicationArtifactPublishReceipt PublishReserved", StringComparison.Ordinal));
            Assert.That(publishBody, Does.Not.Contain("TryReservePublish"));

            // The generic receipt must be validated before issuing the PngJson receipt.
            Assert.That(source, Does.Contain("IsIssuedFor"));
            Assert.That(source, Does.Contain("Store returned an invalid publication receipt."));
        }

        [Test]
        public void Source_DescriptorMapping_LiteralsPresent()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCapturePublicationArtifactPublisher.cs");
            Assert.That(source, Does.Contain("\"frame/\" + id + \"/image\""));
            Assert.That(source, Does.Contain("CaptureArtifactKind.FrameImage"));
            Assert.That(source, Does.Contain("\"image/png\""));
            Assert.That(source, Does.Contain("\"frame/\" + id + \"/metadata\""));
            Assert.That(source, Does.Contain("CaptureArtifactKind.FrameMetadata"));
            Assert.That(source, Does.Contain("\"application/vnd.zantetsu.capture-frame+json\""));
        }

        // ---- TryBegin ----

        [Test]
        public void TryBegin_NullBatch_Rejected()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();

            Assert.Throws<ArgumentNullException>(() => publisher.TryBegin(null, null));
        }

        [Test]
        public void TryBegin_NullToken_Rejected()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out _);

            Assert.Throws<ArgumentNullException>(() => publisher.TryBegin(batch, null));
        }

        [Test]
        public void TryBegin_ForeignToken_Rejected()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out _);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch otherBatch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken otherToken);

            Assert.Throws<ArgumentException>(() => publisher.TryBegin(batch, otherToken));
            Assert.That(otherBatch, Is.Not.Null);
        }

        [Test]
        public void TryBegin_CorruptedBatch_Rejected()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            // Corrupt the prepared-step array with a null element.
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch corrupted =
                (PngJsonCapturePublicationArtifactRecoveryExecutionBatch)FormatterServices.GetUninitializedObject(
                    typeof(PngJsonCapturePublicationArtifactRecoveryExecutionBatch));
            SetField(corrupted, "_actionPlan", actionPlan);
            SetField(corrupted, "_preparedSteps", new PngJsonCapturePublicationArtifactRecoveryPreparedStep[batch.Count]);

            Assert.That(batch.IsValidWithToken(token), Is.True);
            Assert.That(corrupted.IsValidWithToken(token), Is.False);
            Assert.Throws<ArgumentException>(() => publisher.TryBegin(corrupted, token));
        }

        [Test]
        public void TryBegin_BufferUnavailable_Null_NoFilesystemChange()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactVerificationBufferPool pool = new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout, pool);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);

            CaptureArtifactVerificationBufferPool.Lease held = pool.TryRent();
            Assert.That(held, Is.Not.Null);
            try
            {
                Assert.That(publisher.TryBegin(batch, token), Is.Null);
            }
            finally
            {
                pool.Return(held);
            }

            Assert.That(pool.OutstandingRentCount, Is.Zero);
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
        }

        [Test]
        public void TryBegin_AttemptBoundExactly_BatchTokenReservation()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            Type attemptType = attempt.GetType();
            Assert.That(ReferenceEquals(GetField(attempt, "_publisher"), publisher), Is.True);
            Assert.That(ReferenceEquals(GetField(attempt, "_batch"), batch), Is.True);
            Assert.That(ReferenceEquals(GetField(attempt, "_token"), token), Is.True);
            CaptureArtifactPublishReservation reservation = (CaptureArtifactPublishReservation)GetField(attempt, "_reservation");
            Assert.That(reservation, Is.Not.Null);
            Assert.That(ReferenceEquals(reservation.Store, store), Is.True);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));
            Assert.That((bool)GetField(attempt, "_ended"), Is.False);
        }

        [Test]
        public void TryBegin_ForeignRootLayout_Rejected_NoReservation_NoFilesystemChange()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            // The batch is minted against the forged C:\staging / D:\final
            // layout, which differs from the store's sandbox layout.
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            Assert.That(ReferenceEquals(store.RootLayout, batch.RootLayout), Is.False);

            Assert.Throws<ArgumentException>(() => publisher.TryBegin(batch, token));
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.Zero);
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(layout.FinalRunRoot), Is.False);
        }

        // ---- Descriptor mapping ----

        [Test]
        public void Descriptor_PngMapping_Exact()
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            CaptureArtifactDescriptor descriptor = BuildDescriptor(operation);

            Assert.That(descriptor.ArtifactId, Is.EqualTo("frame/10/image"));
            Assert.That(descriptor.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameImage));
            Assert.That(descriptor.FormatId, Is.EqualTo("image/png"));
            Assert.That(descriptor.FormatVersion, Is.EqualTo(1));
            Assert.That(descriptor.StagingRelativePath, Is.EqualTo("frames/10.png.stage"));
            Assert.That(descriptor.FinalRelativePath, Is.EqualTo("frames/10.png"));
            Assert.That(descriptor.ByteLength, Is.EqualTo(operation.ExpectedByteCount));
            Assert.That(descriptor.ContentHash, Is.EqualTo(operation.ExpectedContentSha256));
        }

        [Test]
        public void Descriptor_SidecarMapping_Exact()
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 1);

            CaptureArtifactDescriptor descriptor = BuildDescriptor(operation);

            Assert.That(descriptor.ArtifactId, Is.EqualTo("frame/10/metadata"));
            Assert.That(descriptor.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameMetadata));
            Assert.That(descriptor.FormatId, Is.EqualTo("application/vnd.zantetsu.capture-frame+json"));
            Assert.That(descriptor.FormatVersion, Is.EqualTo(2));
            Assert.That(descriptor.StagingRelativePath, Is.EqualTo("frames/10.json.stage"));
            Assert.That(descriptor.FinalRelativePath, Is.EqualTo("frames/10.json"));
            Assert.That(descriptor.ByteLength, Is.EqualTo(operation.ExpectedByteCount));
            Assert.That(descriptor.ContentHash, Is.EqualTo(operation.ExpectedContentSha256));
        }

        // ---- Publish (real filesystem) ----

        [Test]
        public void Publish_SingleAttempt_MultipleArtifacts_MovedNonOverwriting_NoExtraReservation()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(
                out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                png,
                sidecar,
                layout);

            PngJsonCapturePublicationArtifactPublishOperation pngOperation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);
            PngJsonCapturePublicationArtifactPublishOperation sidecarOperation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 1);

            WriteStaged(layout, pngOperation.Entry.PngStagingRelativePath, png);
            WriteStaged(layout, sidecarOperation.Entry.SidecarStagingRelativePath, sidecar);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));

            PngJsonCapturePublicationArtifactPublishReceipt pngReceipt = publisher.PublishReserved(attempt, pngOperation, token);
            Assert.That(pngReceipt, Is.Not.Null);
            Assert.That(pngReceipt.IsValid, Is.True);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));

            PngJsonCapturePublicationArtifactPublishReceipt sidecarReceipt = publisher.PublishReserved(attempt, sidecarOperation, token);
            Assert.That(sidecarReceipt, Is.Not.Null);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));

            publisher.End(attempt);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.Zero);

            string finalPng = Path.Combine(layout.FinalRunRoot, "frames", "10.png");
            string finalSidecar = Path.Combine(layout.FinalRunRoot, "frames", "10.json");
            Assert.That(File.Exists(finalPng), Is.True);
            Assert.That(File.Exists(finalSidecar), Is.True);
            Assert.That(File.ReadAllBytes(finalPng), Is.EqualTo(png));
            Assert.That(File.ReadAllBytes(finalSidecar), Is.EqualTo(sidecar));
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "frames", "10.json.stage")), Is.False);
        }

        [Test]
        public void Publish_DestinationExists_NotSuccess_StagingPreserved()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(
                out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                png,
                sidecar,
                layout);

            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            WriteStaged(layout, operation.Entry.PngStagingRelativePath, png);

            // Pre-create the final destination.
            string finalPng = Path.Combine(layout.FinalRunRoot, "frames", "10.png");
            Directory.CreateDirectory(Path.GetDirectoryName(finalPng));
            File.WriteAllBytes(finalPng, new byte[] { 0, 1, 2, 3 });

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            Assert.Throws<IOException>(() => publisher.PublishReserved(attempt, operation, token));

            // Staging source is untouched; the pre-existing destination is untouched.
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage")), Is.True);
            Assert.That(File.ReadAllBytes(finalPng), Is.EqualTo(new byte[] { 0, 1, 2, 3 }));

            publisher.End(attempt);
        }

        [Test]
        public void Publish_StoreException_Propagates_NoReceipt()
        {
            byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            byte[] sidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(
                out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                png,
                sidecar,
                layout);

            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            // Write a staging file whose content does not match the plan hash.
            WriteStaged(layout, operation.Entry.PngStagingRelativePath, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            Assert.Throws<InvalidDataException>(() => publisher.PublishReserved(attempt, operation, token));

            // No move happened.
            Assert.That(File.Exists(Path.Combine(layout.StagingRunRoot, "frames", "10.png.stage")), Is.True);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "frames", "10.png")), Is.False);

            publisher.End(attempt);
        }

        // ---- Rejection before side effect ----

        [Test]
        public void Publish_ForeignAttempt_RejectedBeforeSideEffect()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            Assert.Throws<ArgumentException>(() => publisher.PublishReserved(new ForeignAttempt(), operation, token));
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.Zero);
        }

        [Test]
        public void Publish_ForeignPublisher_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore storeA = new CaptureArtifactFileStore(layout);
            CaptureArtifactFileStore storeB = new CaptureArtifactFileStore(new CaptureRunRootLayout(Path.Combine(sandbox, "b-s"), Path.Combine(sandbox, "b-f"), 2));
            PngJsonCapturePublicationArtifactPublisher publisherA = new PngJsonCapturePublicationArtifactPublisher(storeA);
            PngJsonCapturePublicationArtifactPublisher publisherB = new PngJsonCapturePublicationArtifactPublisher(storeB);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);
            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisherA.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            Assert.Throws<ArgumentException>(() => publisherB.PublishReserved(attempt, operation, token));
            Assert.Throws<ArgumentException>(() => publisherB.End(attempt));

            publisherA.End(attempt);
        }

        [Test]
        public void Publish_ForeignBatch_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);
            BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan otherPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken otherToken, layout);
            PngJsonCapturePublicationArtifactPublishOperation foreignOperation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(otherPlan, otherToken, 0);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            Assert.Throws<ArgumentException>(() => publisher.PublishReserved(attempt, foreignOperation, token));
        }

        [Test]
        public void Publish_ForeignToken_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);
            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken foreignToken, layout);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            Assert.Throws<ArgumentException>(() => publisher.PublishReserved(attempt, operation, foreignToken));
        }

        [Test]
        public void Publish_EndedAttempt_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);
            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            publisher.End(attempt);

            Assert.Throws<InvalidOperationException>(() => publisher.PublishReserved(attempt, operation, token));
        }

        // ---- End ----

        [Test]
        public void End_NullAttempt_Rejected()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();

            Assert.Throws<ArgumentNullException>(() => publisher.End(null));
        }

        [Test]
        public void End_ForeignAttempt_Rejected()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();

            Assert.Throws<ArgumentException>(() => publisher.End(new ForeignAttempt()));
        }

        [Test]
        public void End_DoubleEnd_FailClosed()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            publisher.End(attempt);

            Assert.Throws<InvalidOperationException>(() => publisher.End(attempt));
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.Zero);
        }

        [Test]
        public void End_ReleasesBuffer_NextTryBeginSucceeds()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token, layout);

            IPngJsonCapturePublicationArtifactPublishAttempt first = publisher.TryBegin(batch, token);
            Assert.That(first, Is.Not.Null);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));

            publisher.End(first);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.Zero);

            IPngJsonCapturePublicationArtifactPublishAttempt second = publisher.TryBegin(batch, token);
            Assert.That(second, Is.Not.Null);
            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));

            publisher.End(second);
        }

        // ---- Owner release fail-closed ----

        [Test]
        public void OwnerReleased_OperationFailClosed()
        {
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(
                out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                out CaptureRunInitializationSessionOwnershipLease owner);

            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);
            Assert.That(operation.IsValidIndexLocal(token), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(operation.IsValidIndexLocal(token), Is.False);
        }

        [Test]
        public void OwnerReleased_ReceiptFailClosed()
        {
            PngJsonCapturePublicationArtifactPublisher publisher = MakePublisher();

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(
                out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                out CaptureRunInitializationSessionOwnershipLease owner);

            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);
            PngJsonCapturePublicationArtifactPublishReceipt receipt =
                PngJsonCapturePublicationArtifactPublishReceipt.Create(publisher, operation, token);
            Assert.That(receipt.IsValid, Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void OwnerReleased_PublishReservedRejectedBeforeSideEffect()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            PngJsonCapturePublicationArtifactPublisher publisher = new PngJsonCapturePublicationArtifactPublisher(store);

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildPublishPngSidecarBatch(
                out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                out CaptureRunInitializationSessionOwnershipLease owner,
                layout);

            PngJsonCapturePublicationArtifactPublishOperation operation =
                PngJsonCapturePublicationArtifactPublishOperation.CreateIndexLocal(actionPlan, token, 0);

            IPngJsonCapturePublicationArtifactPublishAttempt attempt = publisher.TryBegin(batch, token);
            Assert.That(attempt, Is.Not.Null);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.Throws<ArgumentException>(() => publisher.PublishReserved(attempt, operation, token));
            Assert.That(store.VerificationBufferPool.OutstandingRentCount, Is.EqualTo(1));
        }

        // ---- Forge helpers ----

        private sealed class ForeignAttempt : IPngJsonCapturePublicationArtifactPublishAttempt
        {
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;

            public FakeHandle(string lockPath, bool isCreated = true, List<string> disposeLog = null)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public void Dispose()
            {
                _disposeLog?.Add(LockPath);
            }
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            public FakeInspector(CaptureRunInitializationRootObservation staging, CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakePublicationInspector : ICaptureRunPublicationRecoveryInspector
        {
            public CaptureRunPublicationRecoveryInspectionSnapshot Inspect(CaptureRunPublicationRecoveryInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        private sealed class FakeArtifactInspector : IPngJsonCapturePublicationArtifactInspector
        {
            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
        }

        private static long Probe(CaptureRunPublicationEvidenceStatus status, long expectedByteLength, long limit)
        {
            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
                    return expectedByteLength;

                case CaptureRunPublicationEvidenceStatus.Mismatch:
                    return 1;

                case CaptureRunPublicationEvidenceStatus.Invalid:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.LimitExceeded:
                    return checked(limit + 1);

                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunMarkerBinding MakeMarkerBinding(CaptureRunRootLayout layout)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            bool rootExists,
            CaptureRunMarkerObservationStatus initStatus,
            CaptureRunInitializationMarker initMarker,
            CaptureRunMarkerObservationStatus readyStatus,
            CaptureRunReadyMarker readyMarker,
            bool hasNonMarker = false,
            bool hasUnknown = false,
            bool hasInitTmp = false,
            bool hasReadyTmp = false)
        {
            return new CaptureRunInitializationRootObservation(
                role, rootExists, hasInitTmp, initStatus, initMarker,
                hasReadyTmp, readyStatus, readyMarker, hasNonMarker, hasUnknown, false);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(CaptureRunRootRole role, CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeRootObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            layout = layout ?? MakeLayout();
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(null, out owner, layout),
                maximumPlanBytes,
                maximumEntryCount,
                maximumPathBytes);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeRecoverySnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation(1000, 4, 64, out owner, layout);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(plan ?? MakePlan(), indexAuthoritative, captureIndexTemporary, out owner, layout));
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan = null,
            bool indexAuthoritative = false,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunRootLayout layout = null)
        {
            return MakeRecoveryAuthority(plan, indexAuthoritative, captureIndexTemporary, out _, layout);
        }

        private static PngJsonCapturePublicationPlanEntry MakeEntry(long captureFrameId)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new PngJsonCapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                16,
                32,
                HashA,
                HashA);
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId = 1,
            PngJsonCapturePublicationPlanEntry[] entries = null)
        {
            return new PngJsonCapturePublicationPlan(
                testRunId,
                InitId,
                HashA,
                entries ?? new[] { MakeEntry(10) });
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            PngJsonCapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        private static PngJsonCapturePublicationArtifactInspectionOperation MakeOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount = 1000)
        {
            return PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactEntryObservation MakeIndexObservation(
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            int index,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar)
        {
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(index);
            PngJsonCapturePublicationPlanEntry entry = paths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, operation.MaximumSidecarByteCount);
            return PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                token, operation, paths,
                stagingPng, Probe(stagingPng, entry.PngByteLength, pngLimit),
                stagingSidecar, Probe(stagingSidecar, entry.SidecarByteLength, sidecarLimit),
                finalPng, Probe(finalPng, entry.PngByteLength, pngLimit),
                finalSidecar, Probe(finalSidecar, entry.SidecarByteLength, sidecarLimit));
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotArray(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar,
            long maximumPngByteCount = 1000)
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, maximumPngByteCount);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            Assert.That(operation.EntryCount, Is.EqualTo(stagingPng.Length));

            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[stagingPng.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(token, operation, i, stagingPng[i], stagingSidecar[i], finalPng[i], finalSidecar[i]);
            }

            return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                new FakeArtifactInspector(), operation, traceStatus, traceCount, entries);
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotSingle(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar,
            long maximumPngByteCount = 1000)
        {
            return MakeSnapshotArray(
                authority,
                traceStatus,
                traceCount,
                new[] { stagingPng },
                new[] { stagingSidecar },
                new[] { finalPng },
                new[] { finalSidecar },
                maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPlan(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
        {
            return PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(
                PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(snapshot));
        }

        private static PngJsonCapturePublicationArtifactRecoveryExecutionBatch BuildBatch(
            PngJsonCapturePublicationArtifactRecoveryActionPlan plan)
        {
            return PngJsonCapturePublicationArtifactRecoveryExecutionBatch.Create(plan);
        }

        private PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPublishPngSidecarPlan(
            byte[] png,
            byte[] sidecar,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            string pngHash = Sha256(png);
            string sidecarHash = Sha256(sidecar);
            PngJsonCapturePublicationPlanEntry entry = new PngJsonCapturePublicationPlanEntry(
                10,
                "frames/10.png.stage",
                "frames/10.json.stage",
                "frames/10.png",
                "frames/10.json",
                png.LongLength,
                sidecar.LongLength,
                pngHash,
                sidecarHash);

            PngJsonCapturePublicationPlan plan = new PngJsonCapturePublicationPlan(1, InitId, HashA, new[] { entry });
            PngJsonCapturePublicationArtifactInspectionAuthority authority = MakeRecoveryAuthority(plan, false, null, out owner, layout);
            return BuildPlan(MakeSnapshotSingle(authority, EvMatchesExpected, 1, EvMatchesExpected, EvMatchesExpected, EvAbsent, EvAbsent));
        }

        private PngJsonCapturePublicationArtifactRecoveryExecutionBatch BuildPublishPngSidecarBatch(
            out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            CaptureRunRootLayout layout = null)
        {
            actionPlan = BuildPublishPngSidecarPlan(DefaultPng, DefaultSidecar, out _, layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(actionPlan);
            Assert.That(batch.TryValidate(out token), Is.True);
            return batch;
        }

        private PngJsonCapturePublicationArtifactRecoveryExecutionBatch BuildPublishPngSidecarBatch(
            out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            byte[] png,
            byte[] sidecar,
            CaptureRunRootLayout layout = null)
        {
            actionPlan = BuildPublishPngSidecarPlan(png, sidecar, out _, layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(actionPlan);
            Assert.That(batch.TryValidate(out token), Is.True);
            return batch;
        }

        private PngJsonCapturePublicationArtifactRecoveryExecutionBatch BuildPublishPngSidecarBatch(
            out PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            actionPlan = BuildPublishPngSidecarPlan(DefaultPng, DefaultSidecar, out owner, layout);
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = BuildBatch(actionPlan);
            Assert.That(batch.TryValidate(out token), Is.True);
            return batch;
        }

        private static readonly byte[] DefaultPng = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

        private static readonly byte[] DefaultSidecar = new byte[] { 123, 34, 105, 100, 34, 58, 49, 48, 125 };

        private static string Sha256(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = hex[hash[i] >> 4];
                chars[i * 2 + 1] = hex[hash[i] & 15];
            }

            return new string(chars);
        }

        private static CaptureArtifactDescriptor BuildDescriptor(
            PngJsonCapturePublicationArtifactPublishOperation operation)
        {
            MethodInfo build = typeof(PngJsonCapturePublicationArtifactPublisher)
                .GetMethod("BuildDescriptor", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(build, Is.Not.Null);
            return (CaptureArtifactDescriptor)build.Invoke(null, new object[] { operation });
        }

        private PngJsonCapturePublicationArtifactPublisher MakePublisher()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);
            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            CaptureArtifactFileStore store = new CaptureArtifactFileStore(layout);
            return new PngJsonCapturePublicationArtifactPublisher(store);
        }

        private static (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-publisher-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            return (sandbox, staging, final);
        }

        private static void WriteStaged(CaptureRunRootLayout layout, string relativePath, byte[] bytes)
        {
            string path = Path.Combine(layout.StagingRunRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }
    }
}
