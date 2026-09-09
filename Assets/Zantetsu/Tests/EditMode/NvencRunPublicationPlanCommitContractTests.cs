using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the NVENC Run Publication Plan commit candidate and
    /// Operation materialization boundary: the pure plan builder, the
    /// immutable commit operation, and the coordinator's single non-waiting
    /// preparation entry. Uses the finalized-and-frozen Run pipeline; no real
    /// GPU, NVENC, filesystem, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunPublicationPlanCommitContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string OtherHash64 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        // ---- Builder ----

        [Test]
        public void Builder_SingleFrame_PlanMatchesFinalizationResult()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                CapturePublicationPlan plan = NvencRunPublicationPlanBuilder.Build(
                    result, h.SessionIssue, Hash64);

                Assert.That(plan.IsValid, Is.True);
                Assert.That(plan.TestRunId, Is.EqualTo(h.Context.TestRunId));
                Assert.That(plan.RunInitializationId, Is.EqualTo(h.SessionIssue.Session.RunInitializationId));
                Assert.That(plan.RunManifestContentHash, Is.EqualTo(Hash64));

                Assert.That(plan.ArtifactCount, Is.EqualTo(1));
                Assert.That(ReferenceEquals(plan.GetArtifact(0), result.Descriptor), Is.True);
                Assert.That(plan.GetArtifact(0).ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
                Assert.That(plan.GetArtifact(0).StagingRelativePath, Does.Not.Contain(".partial"));
                Assert.That(plan.GetArtifact(0).FinalRelativePath, Does.Not.Contain(".partial"));

                Assert.That(plan.CaptureFrameEvidenceCount, Is.EqualTo(1));
                CaptureFrameEvidenceEntry entry = plan.GetCaptureFrameEvidence(0);
                Assert.That(entry.CaptureFrameId, Is.EqualTo(1));
                Assert.That(entry.ArtifactCount, Is.EqualTo(1));
                Assert.That(entry.GetArtifactId(0), Is.EqualTo(result.Descriptor.ArtifactId));
            }
        }

        [Test]
        public void Builder_120Frames_AllFramesInOrder()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 120);

                CapturePublicationPlan plan = NvencRunPublicationPlanBuilder.Build(
                    result, h.SessionIssue, Hash64);

                Assert.That(plan.IsValid, Is.True);
                Assert.That(plan.ArtifactCount, Is.EqualTo(1));
                Assert.That(plan.CaptureFrameEvidenceCount, Is.EqualTo(120));

                for (int i = 0; i < 120; i++)
                {
                    CaptureFrameEvidenceEntry entry = plan.GetCaptureFrameEvidence(i);
                    Assert.That(entry.CaptureFrameId, Is.EqualTo(result.FrameRelation.GetCaptureFrameId(i)));
                    Assert.That(entry.ArtifactCount, Is.EqualTo(1));
                    Assert.That(entry.GetArtifactId(0), Is.EqualTo(result.Descriptor.ArtifactId));
                }

                Assert.That(result.FrameRelation.GetCaptureFrameId(0), Is.EqualTo(1));
                Assert.That(result.FrameRelation.GetCaptureFrameId(119), Is.EqualTo(120));
            }
        }

        [Test]
        public void Builder_NullResultOrIssue_Throws()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                Assert.Throws<ArgumentNullException>(() =>
                    NvencRunPublicationPlanBuilder.Build(null, h.SessionIssue, Hash64));
                Assert.Throws<ArgumentNullException>(() =>
                    NvencRunPublicationPlanBuilder.Build(result, null, Hash64));
            }
        }

        [Test]
        public void Builder_ReleasedLease_Throws()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                h.SessionIssue.OwnershipLease.Dispose();

                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanBuilder.Build(result, h.SessionIssue, Hash64));
            }
        }

        [Test]
        public void Builder_TamperedDescriptorKind_Throws()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                SetBackingField(result.Descriptor, "ArtifactKind", CaptureArtifactKind.None);

                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanBuilder.Build(result, h.SessionIssue, Hash64));
            }
        }

        [Test]
        public void Builder_TamperedRelation_Throws()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                // A relation with duplicate frame ids is invalid and must be
                // rejected without throwing from the finalization result's
                // validity re-check.
                SetField(result.FrameRelation, "_captureFrameIds", new long[] { 1, 1 });

                Assert.Throws<ArgumentException>(() =>
                    NvencRunPublicationPlanBuilder.Build(result, h.SessionIssue, Hash64));
            }
        }

        // ---- Coordinator entry ----

        [Test]
        public void Prepare_FinalizedRun_IssuesCorrelatedOperation()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);
                Assert.That(operation, Is.Not.Null);
                Assert.That(operation.IsValid, Is.True);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.True);

                Assert.That(ReferenceEquals(operation.FinalizationResult, result), Is.True);
                Assert.That(ReferenceEquals(operation.Plan.GetArtifact(0), result.Descriptor), Is.True);
                Assert.That(operation.TraceFreezeReceipt, Is.Not.Null);
                Assert.That(ReferenceEquals(operation.RootLayout, h.Context.RootLayout), Is.True);
                Assert.That(operation.TestRunId, Is.EqualTo(h.Context.TestRunId));
                Assert.That(operation.RunInitializationId, Is.EqualTo(h.SessionIssue.Session.RunInitializationId));
                Assert.That(operation.RunManifestContentHash, Is.EqualTo(Hash64));

                // The entry mutates no disposition, slot, lease, or context state.
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
                Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
                Assert.That(h.SessionIssue.IsValid, Is.True);
                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));
            }
        }

        [Test]
        public void Prepare_SameHash_ReturnsSameOperationReference()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation first),
                    Is.True);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation second),
                    Is.True);
                Assert.That(ReferenceEquals(first, second), Is.True);
            }
        }

        [Test]
        public void Prepare_DifferentHash_RejectedWithoutRepublish()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation first),
                    Is.True);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(OtherHash64, out NvencRunPublicationPlanCommitOperation rejected),
                    Is.False);
                Assert.That(rejected, Is.Null);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation again),
                    Is.True);
                Assert.That(ReferenceEquals(first, again), Is.True);
            }
        }

        [Test]
        public void Prepare_Incomplete_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                StopAbandonedBackend(h);
                CompleteTraceFreeze(h);
                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Incomplete));

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void Prepare_Poisoned_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                Assert.That(h.State.TryPoison(), Is.True);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.False);
                Assert.That(operation, Is.Null);
            }
        }

        [Test]
        public void Prepare_LeaseReleased_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        [Test]
        public void Prepare_SlotCommitted_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                Assert.That(h.Slot.TryCommit(h.Context, result), Is.True);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.False);
                Assert.That(operation, Is.Null);
            }
        }

        [Test]
        public void Prepare_SlotDiscarded_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);

                Assert.That(h.Slot.TryDiscardRegistered(h.Context, result), Is.True);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.False);
                Assert.That(operation, Is.Null);
            }
        }

        [Test]
        public void Prepare_ContextNotFinalized_Refuses()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                SetField(h.Context, "_state", (int)NvencRunChunkContextState.Open);

                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.False);
                Assert.That(operation, Is.Null);
            }
        }

        [Test]
        public void Prepare_TraceReceiptCorrupted_Poisons()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                // Null out the retained Trace freeze receipt: a Finalized
                // disposition with a corrupted receipt is a fatal invariant.
                SetField(h.RunCoordinator, "_traceFreezeReceipt", null);

                Assert.Throws<InvalidOperationException>(() =>
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out _));
                Assert.That(h.State.IsPoisoned, Is.True);
            }
        }

        [Test]
        public void Prepare_InvalidManifestHash_RejectsWithoutPoison()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);

                // A null manifest hash is a caller input error, not corruption:
                // it is rejected at the entry without poisoning the process.
                ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(() =>
                    h.RunCoordinator.TryPreparePublicationPlanCommit(null, out _));
                Assert.That(nullEx.ParamName, Is.EqualTo("runManifestContentHash"));
                Assert.That(h.State.IsPoisoned, Is.False);

                // A malformed manifest hash is likewise rejected without
                // poisoning.
                ArgumentException malformedEx = Assert.Throws<ArgumentException>(() =>
                    h.RunCoordinator.TryPreparePublicationPlanCommit(new string('g', 64), out _));
                Assert.That(malformedEx.ParamName, Is.EqualTo("runManifestContentHash"));
                Assert.That(h.State.IsPoisoned, Is.False);

                // The gate was never claimed and the run still prepares normally.
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);
                Assert.That(operation, Is.Not.Null);
            }
        }

        // ---- Operation construction fail-closed ----

        [Test]
        public void Operation_ForeignFinalizationResultOrPlan_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);
                NvencTraceFreezeReceipt receipt = null;
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);
                receipt = operation.TraceFreezeReceipt;

                NvencChunkFinalizationResult foreignResult = MakeForeignResult("chunk/foreign");
                CapturePublicationPlan foreignPlan = NvencRunPublicationPlanBuilder.Build(
                    foreignResult, h.SessionIssue, Hash64);

                // A foreign finalization result must be rejected.
                Assert.Throws<ArgumentException>(() => new NvencRunPublicationPlanCommitOperation(
                    h.RunCoordinator, receipt, foreignResult, operation.Plan));

                // A foreign plan must be rejected.
                Assert.Throws<ArgumentException>(() => new NvencRunPublicationPlanCommitOperation(
                    h.RunCoordinator, receipt, result, foreignPlan));
            }
        }

        [Test]
        public void Operation_ForeignReceiptOrCoordinator_Rejected()
        {
            using (Harness h1 = Harness.Create())
            {
                NvencChunkFinalizationResult result1 = FinalizeAndFreeze(h1, 1);
                Assert.That(
                    h1.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation op1),
                    Is.True);

                using (Harness h2 = Harness.Create())
                {
                    FinalizeAndFreeze(h2, 1);
                    Assert.That(
                        h2.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation op2),
                        Is.True);

                    // A foreign Trace freeze receipt must be rejected.
                    Assert.Throws<ArgumentException>(() => new NvencRunPublicationPlanCommitOperation(
                        h1.RunCoordinator, op2.TraceFreezeReceipt, result1, op1.Plan));

                    // A foreign coordinator must be rejected.
                    Assert.Throws<ArgumentException>(() => new NvencRunPublicationPlanCommitOperation(
                        h2.RunCoordinator, op1.TraceFreezeReceipt, result1, op1.Plan));
                }
            }
        }

        [Test]
        public void Operation_ReconstructedSamePlan_NotIssued()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation issued),
                    Is.True);

                // Reconstructing an operation from the exact same receipt,
                // result, and plan is graph-valid but is not the exact
                // retained operation, so it must never be reported as issued.
                NvencRunPublicationPlanCommitOperation reconstructed =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, issued.TraceFreezeReceipt, issued.FinalizationResult, issued.Plan);

                Assert.That(reconstructed.IsValid, Is.False);
                Assert.That(reconstructed.IsIssuedFor(h.RunCoordinator), Is.False);
            }
        }

        [Test]
        public void Operation_ReconstructedDifferentHash_NotIssued()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation issued),
                    Is.True);

                // A plan built with a different valid manifest hash is
                // graph-valid but is not the exact retained operation.
                CapturePublicationPlan otherPlan = NvencRunPublicationPlanBuilder.Build(
                    issued.FinalizationResult, h.SessionIssue, OtherHash64);

                NvencRunPublicationPlanCommitOperation forged =
                    new NvencRunPublicationPlanCommitOperation(
                        h.RunCoordinator, issued.TraceFreezeReceipt, issued.FinalizationResult, otherPlan);

                Assert.That(forged.IsValid, Is.False);
                Assert.That(forged.IsIssuedFor(h.RunCoordinator), Is.False);
            }
        }

        // ---- Invalidation after issuance ----

        [Test]
        public void Operation_InvalidatedWhenSlotCommitted()
        {
            using (Harness h = Harness.Create())
            {
                NvencChunkFinalizationResult result = FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);
                Assert.That(operation.IsValid, Is.True);

                Assert.That(h.Slot.TryCommit(h.Context, result), Is.True);

                Assert.That(operation.IsValid, Is.False);
                Assert.That(operation.IsIssuedFor(h.RunCoordinator), Is.False);
            }
        }

        [Test]
        public void Operation_InvalidatedWhenLeaseReleased()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);
                Assert.That(operation.IsValid, Is.True);

                h.SessionIssue.OwnershipLease.Dispose();

                Assert.That(operation.IsValid, Is.False);
            }
        }

        [Test]
        public void Operation_InvalidatedWhenDispositionCorrupted()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);
                Assert.That(operation.IsValid, Is.True);

                SetField(h.RunCoordinator, "_disposition", NvencRunEvidenceDisposition.None);

                Assert.That(operation.IsValid, Is.False);
            }
        }

        // ---- Shape and source ----

        [Test]
        public void Operation_SealedFourReadonlyFields_NotDisposable_NoPublicCtor()
        {
            Type type = typeof(NvencRunPublicationPlanCommitOperation);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(4));

            Type[] expected =
            {
                typeof(NvencCaptureRunCoordinator),
                typeof(NvencTraceFreezeReceipt),
                typeof(NvencChunkFinalizationResult),
                typeof(CapturePublicationPlan),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }
        }

        [Test]
        public void Operation_BasenameConstants_Fixed()
        {
            Assert.That(NvencRunPublicationPlanCommitOperation.PreCommitBasename,
                Is.EqualTo("publication.plan.nvenc-precommit.tmp"));
            Assert.That(NvencRunPublicationPlanCommitOperation.FinalBasename,
                Is.EqualTo("publication.plan"));
        }

        [Test]
        public void Builder_StaticFieldless_NoInstanceCtor()
        {
            Type type = typeof(NvencRunPublicationPlanBuilder);

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                Is.Empty);
            Assert.That(type.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
                Is.Empty);
        }

        [Test]
        public void BuilderAndOperation_Sources_NoFilesystemStoreCodecHashRenameThreadTask()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanBuilder.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitOperation.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "Path.",
                "WritePlan", "ReadPlan", "ReadOrRecoverPlan", "SerializeCanonical", "DeserializeCanonical",
                "JsonUtility", "ComputeHash", "HashAlgorithm", "IncrementalHash", "SHA256", "SHA384", "SHA512",
                "MD5", "System.Security.Cryptography", "Close(", "Flush(", "Move(", "rename", "Rename",
                "Retry", "Rollback", "Truncate", "Task", "new Thread", "ThreadPool", "SpinWait",
                "lock (", "Monitor", "UnityEngine", "Application.", "DllImport", "IntPtr", "SafeHandle",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "builder/operation source must not contain: " + word);
            }
        }

        [Test]
        public void BuilderAndOperation_Sources_DoNotTouchPhase01PublicationPlanTmpPath()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanBuilder.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunPublicationPlanCommitOperation.cs"));

            // The Phase 0 / 0.1 publication.plan.tmp path is unchanged.
            Assert.That(source, Does.Not.Contain("publication.plan.tmp"));
            // The NVENC pre-commit basename is the new, separate path.
            Assert.That(source, Does.Contain("publication.plan.nvenc-precommit.tmp"));
        }

        // ---- Helpers ----

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void StopFinalizedBackend(Harness h, int frameCount)
        {
            for (long id = 1; id <= frameCount; id++)
            {
                h.AcceptAndAppendChunk(id, 64, Seed);
            }

            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            for (long id = 1; id <= frameCount; id++)
            {
                Assert.That(h.RunCoordinator.TryReflectCompletion(
                    MakeCompletion(id, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            }
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            Assert.That(h.Worker.TeardownCompleted, Is.True);
            Assert.That(h.Worker.IsStopped, Is.True);

            h.SubmitWorker.Dispose();
        }

        private static void StopAbandonedBackend(Harness h)
        {
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not converge the abandon request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
            Assert.That(outcome.IsAbandoned, Is.True);

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            h.SubmitWorker.Dispose();
        }

        private static void CompleteTraceFreeze(Harness h)
        {
            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

            ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
            FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
        }

        private static NvencChunkFinalizationResult FinalizeAndFreeze(Harness h, int frameCount)
        {
            StopFinalizedBackend(h, frameCount);
            CompleteTraceFreeze(h);

            Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult result), Is.True);
            return result;
        }

        private static NvencChunkFinalizationResult MakeForeignResult(string artifactId)
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);
            FakeWriter writer = new FakeWriter();
            NvencRunChunkSink sink = new NvencRunChunkSink(state, buffer, writer);
            NvencRunChunkFinalizationCoordinator coordinator = new NvencRunChunkFinalizationCoordinator(writer);

            CaptureFrameWorkToken token = MakeToken(1);
            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new PatternSource(64, Seed), out _),
                Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
            Assert.That(sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation = NvencRunChunkFinalizationOperation.Create(sink, evidence, artifactId);
            return coordinator.Execute(operation);
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status,
            int producedArtifactCount = 0,
            long testRunId = 1)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, testRunId, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, producedArtifactCount, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                "<" + propertyName + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, propertyName + " backing field not found.");
            field.SetValue(target, value);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
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

        private static NvencOrderedSubmitWorkerService BuildSubmitWorker(NvencCaptureProcessState state)
        {
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samples = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool sync = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitCredits = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCredits = new NvencFrameCompletionCreditPool(state);
            NvencSourceResourceReleaseCoordinator release = new NvencSourceResourceReleaseCoordinator(
                state, work, samples, sync, submitCredits, frameCredits,
                new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
            NvencOrderedSubmitProcessor processor = new NvencOrderedSubmitProcessor(
                state,
                new NvencFixedSpscQueue<NvencSubmissionRecord>(),
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>(),
                work, samples, release, new FakeSubmitter());
            return new NvencOrderedSubmitWorkerService(state, processor);
        }

        // ---- Fakes ----

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                return NvencRunPublicationPlanCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
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

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private int _finalizeCount;

            internal int CallCount => Volatile.Read(ref _finalizeCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);

                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
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
            internal bool Result = true;
            internal int ResultLength = 1024;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = ResultLength;
                return Result;
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
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
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
            internal FakeWriter Writer = new FakeWriter();
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

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            internal FakeCommitter Committer;
            internal NvencRunPublicationPlanCommitService Service;

            private readonly Action _settledHandler;

            internal bool SubmitDrained
            {
                set => SetField(SubmitWorker, "_drainCompleted", value);
            }

            internal Harness()
            {
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue();
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

                Committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator commitCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(Committer);
                Service = new NvencRunPublicationPlanCommitService(State, commitCoordinator);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze, Service);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                AppendChunk(frameId, length, seed);
            }

            internal void AppendChunk(long frameId, int length, byte seed)
            {
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
                if (!Worker.IsStopped)
                {
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;
                SettledEvent.Dispose();

                if (!Service.IsStopped)
                {
                    if (!Service.TryStopWithoutRequest())
                    {
                        State.TryPoison();
                        Service.Notify();
                    }
                }

                FieldInfo serviceField = typeof(NvencRunPublicationPlanCommitService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread serviceThread = (Thread)serviceField?.GetValue(Service);
                if (serviceThread != null)
                {
                    Assert.That(serviceThread.Join(WatchdogTimeoutMs), Is.True,
                        "publication service worker did not physically exit during teardown");
                }

                Service.Dispose();
            }
        }
    }
}
