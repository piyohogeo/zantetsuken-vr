using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Run chunk context: the fixed-capacity
    /// accepted frame-id sequence and the exclusive, exactly-once terminal
    /// transition that binds the sink, finalization coordinator, and finalized
    /// result.
    /// </summary>
    public class NvencRunChunkContextContractTests
    {
        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Initial state ----

        [Test]
        public void Initial_Open_NoResult()
        {
            Harness h = new Harness();

            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(0));
            Assert.That(h.Context.TryGetAcceptedFrameId(0, out _), Is.False);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
        }

        // ---- Accepted registration ----

        [Test]
        public void Accepted_ZeroOneOneHundredTwenty()
        {
            Harness h = new Harness();
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(0));

            Assert.That(h.Context.TryRecordAcceptedFrame(1), Is.True);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
            Assert.That(h.Context.TryGetAcceptedFrameId(0, out long first), Is.True);
            Assert.That(first, Is.EqualTo(1));

            for (long id = 2; id <= NvencBringUpProfileV1.CadenceTickCount; id++)
            {
                Assert.That(h.Context.TryRecordAcceptedFrame(id), Is.True);
            }

            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(NvencBringUpProfileV1.CadenceTickCount));
            for (int i = 0; i < NvencBringUpProfileV1.CadenceTickCount; i++)
            {
                Assert.That(h.Context.TryGetAcceptedFrameId(i, out long id), Is.True);
                Assert.That(id, Is.EqualTo(i + 1));
            }
        }

        [Test]
        public void Accepted_OneHundredTwentyFirstDuplicateBackwardInvalid_AtomicallyRejected()
        {
            Harness h = new Harness();
            for (long id = 1; id <= NvencBringUpProfileV1.CadenceTickCount; id++)
            {
                Assert.That(h.Context.TryRecordAcceptedFrame(id), Is.True);
            }

            int count = h.Context.AcceptedFrameCount;

            // 121st entry.
            Assert.That(h.Context.TryRecordAcceptedFrame(NvencBringUpProfileV1.CadenceTickCount + 1), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));

            // Duplicate of the last entry.
            Assert.That(h.Context.TryRecordAcceptedFrame(NvencBringUpProfileV1.CadenceTickCount), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));

            // Backward entry.
            Assert.That(h.Context.TryRecordAcceptedFrame(50), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));

            // Non-positive entries.
            Assert.That(h.Context.TryRecordAcceptedFrame(0), Is.False);
            Assert.That(h.Context.TryRecordAcceptedFrame(-5), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));
        }

        [Test]
        public void Accepted_AfterTerminal_Rejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.Freeze();
            h.Context.TryAbandon();

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        // ---- StopAccepting freeze and snapshot ----

        [Test]
        public void Terminal_PreFreeze_RejectedWithoutSideEffect()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            // Before the StopAccepting freeze, finalize and abandon are both
            // rejected with no side effect and the sequence stays unfrozen.
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
            Assert.That(h.Context.TryGetAcceptedFrameSnapshot(out _), Is.False);
        }

        [Test]
        public void Freeze_Running_RejectedWithoutChange()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            // While the process is still Running (before StopAccepting), the
            // freeze is rejected with no change, so the Run stays acceptable.
            Assert.That(h.Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot snapshot), Is.False);
            Assert.That(snapshot, Is.Null);
            Assert.That(h.Context.TryGetAcceptedFrameSnapshot(out _), Is.False);
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
            Assert.That(h.Context.CanRecordAcceptedFrame(2), Is.True);
        }

        [Test]
        public void Freeze_ZeroAccepted_Succeeds_CountZero()
        {
            Harness h = new Harness();

            NvencRunAcceptedFrameSnapshot snapshot = h.FreezeAndGet();
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot.Count, Is.EqualTo(0));
            Assert.That(snapshot.TestRunId, Is.EqualTo(h.Context.TestRunId));
            Assert.That(ReferenceEquals(snapshot.Context, h.Context), Is.True);
            Assert.That(snapshot.TryGetCaptureFrameId(0, out _), Is.False);

            Assert.That(h.Context.TryGetAcceptedFrameSnapshot(out NvencRunAcceptedFrameSnapshot held), Is.True);
            Assert.That(ReferenceEquals(held, snapshot), Is.True);
        }

        [Test]
        public void Freeze_SingleAccepted_ReflectsSequence()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(7, 64, Seed);

            NvencRunAcceptedFrameSnapshot snapshot = h.FreezeAndGet();
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.True);
            Assert.That(id, Is.EqualTo(7));
            Assert.That(snapshot.TryGetCaptureFrameId(1, out _), Is.False);
        }

        [Test]
        public void Freeze_FullCadence_Count120()
        {
            Harness h = new Harness();
            for (long id = 1; id <= 120; id++)
            {
                Assert.That(h.Context.TryRecordAcceptedFrame(id), Is.True);
            }

            NvencRunAcceptedFrameSnapshot snapshot = h.FreezeAndGet();
            Assert.That(snapshot.Count, Is.EqualTo(120));
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long first), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(snapshot.TryGetCaptureFrameId(119, out long last), Is.True);
            Assert.That(last, Is.EqualTo(120));
            Assert.That(snapshot.TryGetCaptureFrameId(120, out _), Is.False);
        }

        [Test]
        public void Freeze_Idempotent_SameReference()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            Assert.That(h.State.TryBeginDrain(), Is.True);

            Assert.That(h.Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot first), Is.True);
            Assert.That(h.Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot second), Is.True);
            Assert.That(ReferenceEquals(first, second), Is.True);
        }

        [Test]
        public void Accepted_AfterFreeze_Rejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            NvencRunAcceptedFrameSnapshot snapshot = h.FreezeAndGet();

            // The frozen sequence can no longer grow, and the snapshot's count
            // stays fixed even after the rejected registration.
            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.True);
            Assert.That(id, Is.EqualTo(1));
        }

        [Test]
        public void Freeze_SerializedOnTerminalGate_Source()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkContext.cs"));
            string body = ExtractMethodBody(source, "TryFreezeAcceptedFrames");
            Assert.That(body, Does.Contain("lock (_terminalGate)"));
        }

        [Test]
        public void Snapshot_SealedImmutable_ExactFields()
        {
            Type type = typeof(NvencRunAcceptedFrameSnapshot);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(2));

            Type[] expected =
            {
                typeof(NvencRunChunkContext),
                typeof(int),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }

            Type[] forbiddenTypes =
            {
                typeof(long[]), typeof(int[]), typeof(CaptureFrameWorkToken),
                typeof(NvencOwnedAccessUnitLease), typeof(NvencRunChunkSink),
                typeof(NvencCaptureProcessState),
            };

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(Array.IndexOf(forbiddenTypes, property.PropertyType), Is.LessThan(0),
                    property.Name + " must not expose a forbidden type.");
            }
        }

        [Test]
        public void Snapshot_ContextDivergence_FailClosed()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            NvencRunAcceptedFrameSnapshot snapshot = h.FreezeAndGet();

            // Corrupt the context's accepted count so it diverges from the
            // frozen count; reads must fail closed with a zero id.
            SetField(h.Context, "_acceptedCount", 5);
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.False);
            Assert.That(id, Is.EqualTo(0));
        }

        [Test]
        public void Snapshot_NullContext_FailClosed()
        {
            NvencRunAcceptedFrameSnapshot snapshot = new NvencRunAcceptedFrameSnapshot(null, 0);
            Assert.That(snapshot.Context, Is.Null);
            Assert.That(snapshot.TestRunId, Is.EqualTo(0));
            Assert.That(snapshot.Count, Is.EqualTo(0));
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.False);
            Assert.That(id, Is.EqualTo(0));
        }

        // ---- Finalize ----

        [Test]
        public void Finalize_AcceptedMatchesSinkRelation_Success()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.AcceptAndAppend(2, 48, Seed);

            h.Freeze();
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Sink, Is.SameAs(h.Sink));
            Assert.That(result.ArtifactId, Is.EqualTo("chunk/0"));
            Assert.That(result.AppendedCount, Is.EqualTo(2));
            Assert.That(result.LastFrameId, Is.EqualTo(2));
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));

            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
            Assert.That(ReferenceEquals(held, result), Is.True);
        }

        [Test]
        public void Finalize_ZeroAccepted_FinalizerNotContacted()
        {
            Harness h = new Harness();

            h.Freeze();
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
        }

        [Test]
        public void Finalize_CountInsufficientExtraOrderMismatch_FinalizerNotContacted()
        {
            // Insufficient: the sink has fewer appends than accepted.
            Harness insufficient = new Harness();
            insufficient.Context.TryRecordAcceptedFrame(1);
            insufficient.Context.TryRecordAcceptedFrame(2);
            insufficient.Append(1, 64, Seed);
            insufficient.Freeze();
            Assert.That(insufficient.Context.TryFinalize(out _), Is.False);
            Assert.That(insufficient.Finalizer.CallCount, Is.EqualTo(0));

            // Extra: the sink has more appends than accepted.
            Harness extra = new Harness();
            extra.Context.TryRecordAcceptedFrame(1);
            extra.Append(1, 64, Seed);
            extra.Append(2, 48, Seed);
            extra.Freeze();
            Assert.That(extra.Context.TryFinalize(out _), Is.False);
            Assert.That(extra.Finalizer.CallCount, Is.EqualTo(0));

            // Order mismatch: same count, different frame id sequence.
            Harness order = new Harness();
            order.Context.TryRecordAcceptedFrame(1);
            order.Context.TryRecordAcceptedFrame(2);
            order.Append(1, 64, Seed);
            order.Append(3, 48, Seed);
            order.Freeze();
            Assert.That(order.Context.TryFinalize(out _), Is.False);
            Assert.That(order.Finalizer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Finalize_ExactlyOnce_ResultSameReference()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            h.Freeze();
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult second), Is.False);
            Assert.That(second, Is.Null);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));

            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
            Assert.That(ReferenceEquals(held, result), Is.True);
        }

        [Test]
        public void Finalize_TransientEvidenceFailure_RetryableAfterSinkCatchesUp()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(1);
            h.Context.TryRecordAcceptedFrame(2);
            h.Append(1, 64, Seed);

            h.Freeze();

            // The sink has fewer appends than accepted; this false is before
            // any finalizer contact and must be retryable.
            Assert.That(h.Context.TryFinalize(out _), Is.False);
            Assert.That(h.Writer.CallCount, Is.EqualTo(0));

            h.Append(2, 48, Seed);

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(result.IsValid, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Finalize_FinalizerException_PropagatesNoRetryNoAbandon()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            h.Freeze();

            InvalidOperationException boom = new InvalidOperationException("boom");
            h.Writer.ExceptionToThrow = boom;

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Context.TryFinalize(out _));
            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));

            // The claim is latched before the finalizer contact: neither a
            // re-finalize nor an abandon may follow the attempted finalize.
            Assert.That(h.Context.TryFinalize(out _), Is.False);
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Accepted_AfterFinalizerException_Rejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            h.Freeze();
            h.Writer.ExceptionToThrow = new InvalidOperationException("boom");
            Assert.Throws<InvalidOperationException>(() => h.Context.TryFinalize(out _));

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        [Test]
        public void Accepted_AfterCoordinatorRejectsReceipt_Rejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            h.Freeze();

            // The finalizer is contacted but returns no receipt, so the
            // coordinator raises a fatal post-side-effect failure.
            h.Writer.BuildReceipt = false;
            Assert.Throws<InvalidOperationException>(() => h.Context.TryFinalize(out _));

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        [Test]
        public void Accepted_DuringFinalize_BlockedByClaim_AndNotGrownAfterTerminal()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            h.Freeze();

            bool acceptedDuringFinalize = true;
            h.Writer.OnFinalize = () => acceptedDuringFinalize = h.Context.TryRecordAcceptedFrame(2);

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(result.IsValid, Is.True);
            Assert.That(acceptedDuringFinalize, Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));

            // After the terminal is fixed, the accepted list can no longer grow.
            Assert.That(h.Context.TryRecordAcceptedFrame(3), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        [Test]
        public void Finalized_ThenAcceptedAddReFinalizeAbandonRejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.Freeze();
            h.Context.TryFinalize(out _);

            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));
            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.TryFinalize(out _), Is.False);
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        // ---- Abandon ----

        [Test]
        public void Abandon_OpenToAbandoned_ExactlyOnce_NoResult()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            h.Freeze();
            Assert.That(h.Context.TryAbandon(), Is.True);
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Abandoned));
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.TryFinalize(out _), Is.False);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
        }

        // ---- Result publication ----

        [Test]
        public void FinalizedResult_SwapOrNull_FailClosed()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.Freeze();
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
            Assert.That(ReferenceEquals(held, result), Is.True);

            // Nulled result fails closed.
            SetField(h.Context, "_finalizationResult", null);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult afterNull), Is.False);
            Assert.That(afterNull, Is.Null);

            // Swapped foreign result fails closed.
            Harness other = new Harness("chunk/other");
            other.AcceptAndAppend(1, 64, Seed);
            other.Freeze();
            Assert.That(other.Context.TryFinalize(out NvencChunkFinalizationResult foreign), Is.True);
            SetField(h.Context, "_finalizationResult", foreign);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult afterSwap), Is.False);
            Assert.That(afterSwap, Is.Null);
        }

        // ---- Constructor binding ----

        [Test]
        public void Constructor_NullOrInvalidInputs_Rejected()
        {
            Harness h = new Harness();

            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(null, h.Sink, h.Coordinator, "chunk/0"));
            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(MakeIssue(), null, h.Coordinator, "chunk/0"));
            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(MakeIssue(), h.Sink, null, "chunk/0"));
            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(MakeIssue(), h.Sink, h.Coordinator, null));
            Assert.Throws<ArgumentException>(() =>
                new NvencRunChunkContext(MakeIssue(), h.Sink, h.Coordinator, string.Empty));
        }

        [Test]
        public void Constructor_ForeignWriter_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);
            FakeWriter writerA = new FakeWriter();
            NvencRunChunkSink sink = new NvencRunChunkSink(state, buffer, writerA);
            FakeWriter writerB = new FakeWriter();
            NvencRunChunkFinalizationCoordinator coordinator = new NvencRunChunkFinalizationCoordinator(writerB);

            Assert.Throws<ArgumentException>(() =>
                new NvencRunChunkContext(MakeIssue(), sink, coordinator, "chunk/0"));
            Assert.That(writerA.CallCount, Is.EqualTo(0));
            Assert.That(writerB.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_FinalizerNotAppender_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);
            FakeWriter writerA = new FakeWriter();
            NvencRunChunkSink sink = new NvencRunChunkSink(state, buffer, writerA);
            FinalizerOnly finalizerOnly = new FinalizerOnly();
            NvencRunChunkFinalizationCoordinator coordinator = new NvencRunChunkFinalizationCoordinator(finalizerOnly);

            Assert.Throws<ArgumentException>(() =>
                new NvencRunChunkContext(MakeIssue(), sink, coordinator, "chunk/0"));
        }

        // ---- Ownership and allocation ----

        [Test]
        public void Context_NotDisposable_NoLeaseOwnership_NoFilesystem()
        {
            Type type = typeof(NvencRunChunkContext);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            // The context never holds or disposes the ownership lease.
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureRunInitializationSessionOwnershipLease)),
                    "context must not hold the ownership lease.");
            }

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkContext.cs"));
            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "System.IO", "new Thread", "ThreadPool",
                "Task",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "context source must not contain: " + word);
            }
        }

        [Test]
        public void AcceptedRegistration_AllocationFree()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkContext.cs"));
            string body = ExtractMethodBody(source, "TryRecordAcceptedFrame");

            string[] forbidden =
            {
                "new ", "Array.", "List", "Queue", "Dictionary", "Enumerable",
                ".Select(", ".Where(", ".ToList(", ".ToArray(",
            };

            foreach (string word in forbidden)
            {
                Assert.That(body, Does.Not.Contain(word), "accepted registration must not allocate: " + word);
            }
        }

        // ---- Helpers ----

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
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
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

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), "Method signature not found: " + methodName);

            int open = source.IndexOf('{', signature);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Method body not found: " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Method body not terminated: " + methodName);
            return null;
        }

        // ---- Fakes ----

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
            internal int CallCount;
            internal Exception ExceptionToThrow;
            internal NvencRunChunkFinalizationReceipt ReceiptToReturn;
            internal bool BuildReceipt = true;
            internal Action OnFinalize;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CallCount++;
                OnFinalize?.Invoke();
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

                if (!BuildReceipt)
                {
                    return null;
                }

                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        private sealed class FinalizerOnly : INvencRunChunkFinalizer
        {
            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
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

        private sealed class Harness
        {
            internal NvencCaptureProcessState State = new NvencCaptureProcessState();
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer = new FakeWriter();
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator Coordinator;
            internal NvencRunChunkContext Context;

            // The single writer implements both the appender (sink) and the
            // finalizer (coordinator), mirroring the production wiring.
            internal FakeWriter Finalizer => Writer;

            internal Harness(string artifactId = "chunk/0")
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                Coordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                Context = new NvencRunChunkContext(MakeIssue(), Sink, Coordinator, artifactId);
            }

            internal void AcceptAndAppend(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                Append(frameId, length, seed);
            }

            internal void Append(long frameId, int length, byte seed)
            {
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }

            internal void Freeze()
            {
                Assert.That(State.TryBeginDrain(), Is.True);
                Assert.That(Context.TryFreezeAcceptedFrames(out _), Is.True);
            }

            internal NvencRunAcceptedFrameSnapshot FreezeAndGet()
            {
                Assert.That(State.TryBeginDrain(), Is.True);
                Assert.That(Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot snapshot), Is.True);
                return snapshot;
            }
        }
    }
}
