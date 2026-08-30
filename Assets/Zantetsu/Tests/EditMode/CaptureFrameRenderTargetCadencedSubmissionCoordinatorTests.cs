using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRenderTargetCadencedSubmissionCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunManifest MakeManifest(long testRunId = 1)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                "build-1",
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));

            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                logger.Enqueue(Event(1));
                recorder.TryTrigger();
                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                return TraceRunManifest.Create(snapshot, context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static CaptureRunReference MakeRun(long testRunId = 1)
        {
            TraceRunManifest manifest = MakeManifest(testRunId);
            return new CaptureRunReference(manifest, 100, 5, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static CaptureFrameProfile MakeProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
        }

        private static CaptureFrameTiming MakeTiming(double predictedDisplayTimeSeconds, bool shouldRender)
        {
            return new CaptureFrameTiming(predictedDisplayTimeSeconds, 1.0 / 90.0, shouldRender, 0.0, 0.0, 0L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static void AssertPngSignature(NativeArray<byte> png)
        {
            Assert.That(png.Length, Is.GreaterThan(8));
            for (int i = 0; i < 8; i++)
            {
                Assert.That(png[i], Is.EqualTo(PngSignature[i]), "PNG signature mismatch at byte " + i);
            }
        }

        private static bool RequestsIdentical(in CaptureFrameRequest a, in CaptureFrameRequest b)
        {
            return
                a.TraceContext.Timestamp == b.TraceContext.Timestamp &&
                a.TraceContext.UnityFrameId == b.TraceContext.UnityFrameId &&
                a.TraceContext.FixedStepId == b.TraceContext.FixedStepId &&
                a.TraceContext.ThreadId == b.TraceContext.ThreadId &&
                a.TraceContext.CaptureFrameId == b.TraceContext.CaptureFrameId &&
                a.TraceContext.OpenXRFrameId == b.TraceContext.OpenXRFrameId &&
                a.TraceContext.TestRunId == b.TraceContext.TestRunId &&
                a.TraceContext.SlashId == b.TraceContext.SlashId &&
                a.TraceContext.FrontEdgeId == b.TraceContext.FrontEdgeId &&
                a.TraceContext.ObjectId == b.TraceContext.ObjectId &&
                a.TraceContext.ObjectGeneration == b.TraceContext.ObjectGeneration &&
                a.TraceContext.TaskId == b.TraceContext.TaskId &&
                a.Source == b.Source &&
                a.Eye == b.Eye &&
                a.ImageRect.X == b.ImageRect.X &&
                a.ImageRect.Y == b.ImageRect.Y &&
                a.ImageRect.Width == b.ImageRect.Width &&
                a.ImageRect.Height == b.ImageRect.Height &&
                a.ArrayIndex == b.ArrayIndex &&
                a.PixelLayout.Format == b.PixelLayout.Format &&
                a.PixelLayout.Width == b.PixelLayout.Width &&
                a.PixelLayout.Height == b.PixelLayout.Height &&
                a.PixelLayout.BytesPerPixel == b.PixelLayout.BytesPerPixel &&
                a.PixelLayout.RowStrideBytes == b.PixelLayout.RowStrideBytes &&
                a.PixelLayout.ByteCount == b.PixelLayout.ByteCount;
        }

        private static Exception[] AppendCleanupException(Exception[] cleanupExceptions, Exception ex)
        {
            if (ex == null)
            {
                return cleanupExceptions;
            }

            if (cleanupExceptions == null || cleanupExceptions.Length == 0)
            {
                return new[] { ex };
            }

            Exception[] combined = new Exception[cleanupExceptions.Length + 1];
            Array.Copy(cleanupExceptions, combined, cleanupExceptions.Length);
            combined[cleanupExceptions.Length] = ex;
            return combined;
        }

        private static Exception[] ConcatExceptions(Exception[] first, Exception[] second)
        {
            if (first == null || first.Length == 0)
            {
                return second ?? new Exception[0];
            }

            if (second == null || second.Length == 0)
            {
                return first;
            }

            Exception[] combined = new Exception[first.Length + second.Length];
            Array.Copy(first, combined, first.Length);
            Array.Copy(second, 0, combined, first.Length, second.Length);
            return combined;
        }

        private static void ThrowCleanupAndBody(ExceptionDispatchInfo bodyException, Exception[] cleanupExceptions)
        {
            bool hasBody = bodyException != null;
            bool hasCleanup = cleanupExceptions != null && cleanupExceptions.Length > 0;

            if (hasBody && hasCleanup)
            {
                Exception[] all = new Exception[cleanupExceptions.Length + 1];
                all[0] = bodyException.SourceException;
                Array.Copy(cleanupExceptions, 0, all, 1, cleanupExceptions.Length);
                throw new AggregateException(all);
            }

            if (hasBody)
            {
                bodyException.Throw();
            }
            else if (hasCleanup)
            {
                if (cleanupExceptions.Length == 1)
                {
                    ExceptionDispatchInfo.Capture(cleanupExceptions[0]).Throw();
                }
                else
                {
                    throw new AggregateException(cleanupExceptions);
                }
            }
        }

        private sealed class RegisteredEntry
        {
            public readonly CaptureFrameRequest Request;
            public readonly CaptureFrameRenderTargetLease Lease;

            public RegisteredEntry(CaptureFrameRequest request, CaptureFrameRenderTargetLease lease)
            {
                Request = request;
                Lease = lease;
            }
        }

        private sealed class CoordinatorScope
        {
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public CaptureFrameRequestQueue Queue;
            public CaptureFrameRequestScheduler RequestScheduler;
            public CaptureFrameRecordRegistry RecordRegistry;
            public CaptureFrameRecordScheduler RecordScheduler;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public CaptureFrameRenderTargetRecordScheduler LeaseScheduler;
            public CaptureFrameIdSequence Sequence;
            public CaptureFrameRecordFactory Factory;
            public CaptureFrameRenderTargetRecordSubmissionCoordinator Submission;
            public CaptureFrameCadenceSelector Selector;
            public CaptureFrameRenderTargetCadencedSubmissionCoordinator Coordinator;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<RegisteredEntry> Registered = new List<RegisteredEntry>();

            public CaptureFrameRenderTargetLease Rent()
            {
                Assert.That(Pool.TryRent(out CaptureFrameRenderTargetLease lease), Is.True);
                Held.Add(lease);
                return lease;
            }

            public CaptureFrameCadencedSubmissionStatus SubmitAndTrack(
                CaptureFrameRenderTargetLease lease,
                double predictedDisplayTimeSeconds,
                bool shouldRender,
                out CaptureFrameRecord accepted,
                int commitPathId = 1)
            {
                CaptureFrameCadencedSubmissionStatus status = Submit(Coordinator, lease, predictedDisplayTimeSeconds, shouldRender, commitPathId, out accepted);
                if (status == CaptureFrameCadencedSubmissionStatus.Submitted)
                {
                    RemoveFromHeld(lease);
                    Registered.Add(new RegisteredEntry(accepted.Request, lease));
                }

                return status;
            }

            public void ReturnHeld(CaptureFrameRenderTargetLease lease)
            {
                Pool.Return(lease);
                RemoveFromHeld(lease);
            }

            private void RemoveFromHeld(CaptureFrameRenderTargetLease lease)
            {
                for (int i = Held.Count - 1; i >= 0; i--)
                {
                    if (Held[i].SlotIndex == lease.SlotIndex)
                    {
                        Held.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        private static CaptureFrameCadencedSubmissionStatus Submit(
            CaptureFrameRenderTargetCadencedSubmissionCoordinator coordinator,
            CaptureFrameRenderTargetLease lease,
            double predictedDisplayTimeSeconds,
            bool shouldRender,
            int commitPathId,
            out CaptureFrameRecord accepted)
        {
            return coordinator.TrySubmit(
                1000,
                200,
                300,
                4,
                500,
                600,
                700,
                800,
                9,
                1000,
                MakeTiming(predictedDisplayTimeSeconds, shouldRender),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                commitPathId,
                lease,
                out accepted);
        }

        private static CoordinatorScope NewScope(int poolCapacity, int leaseCapacity, int recordCapacity, int queueCapacity)
        {
            CoordinatorScope scope = new CoordinatorScope();
            scope.Logger = new TraceLogger(8);
            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.Queue = new CaptureFrameRequestQueue(queueCapacity);
            scope.RequestScheduler = new CaptureFrameRequestScheduler(scope.Queue, scope.Observer);
            scope.RecordRegistry = new CaptureFrameRecordRegistry(recordCapacity);
            scope.RecordScheduler = new CaptureFrameRecordScheduler(scope.RequestScheduler, scope.RecordRegistry, scope.Observer);
            scope.Pool = new CaptureFrameRenderTargetPool(poolCapacity, MakeProfile());
            scope.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(leaseCapacity, scope.Pool);
            scope.LeaseScheduler = new CaptureFrameRenderTargetRecordScheduler(scope.RecordScheduler, scope.LeaseRegistry);
            scope.Sequence = new CaptureFrameIdSequence();
            scope.Factory = new CaptureFrameRecordFactory(
                MakeRun(),
                scope.Sequence,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
            scope.Submission = new CaptureFrameRenderTargetRecordSubmissionCoordinator(scope.Factory, scope.LeaseScheduler);
            scope.Selector = new CaptureFrameCadenceSelector(CaptureFrameCadenceSelector.PhaseZeroTargetFramesPerSecond);
            scope.Coordinator = new CaptureFrameRenderTargetCadencedSubmissionCoordinator(scope.Selector, scope.Submission);
            return scope;
        }

        private static Exception[] CleanupCoordinatorScope(CoordinatorScope scope)
        {
            Exception[] errors = null;

            for (int i = scope.Registered.Count - 1; i >= 0; i--)
            {
                RegisteredEntry entry = scope.Registered[i];
                scope.Registered.RemoveAt(i);
                try
                {
                    if (scope.LeaseRegistry.TryRemove(entry.Request, out CaptureFrameRenderTargetLease lease))
                    {
                        scope.Pool.Return(lease);
                    }
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            for (int i = scope.Held.Count - 1; i >= 0; i--)
            {
                CaptureFrameRenderTargetLease lease = scope.Held[i];
                scope.Held.RemoveAt(i);
                try
                {
                    scope.Pool.Return(lease);
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            try
            {
                scope.Pool.Dispose();
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Logger != null && scope.Logger.IsCreated)
                {
                    scope.Logger.Dispose();
                }
            }
            catch (Exception ex)
            {
                errors = AppendCleanupException(errors, ex);
            }

            return errors;
        }

        private static void RunCoordinatorBody(CoordinatorScope scope, Action body)
        {
            ExceptionDispatchInfo bodyException = null;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] errors = CleanupCoordinatorScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        private static CaptureFrameRequest MakeDummyRequest()
        {
            return new CaptureFrameRequest(
                new CaptureFrameTraceContext(1, 2, 3, 4, 999, 6, 1, 8, 9, 10, 11, 12),
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRecord MakeDummyRecord()
        {
            return new CaptureFrameRecord(
                MakeRun(),
                MakeDummyRequest(),
                MakeTiming(0.0, true),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetCadencedSubmissionCoordinator(null, scope.Submission));
                Assert.Throws<ArgumentNullException>(() => new CaptureFrameRenderTargetCadencedSubmissionCoordinator(scope.Selector, null));
            });
        }

        [Test]
        public void FirstRenderableFrame_Submitted_LeaseOwnershipTransferred()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();

                Assert.That(scope.SubmitAndTrack(lease, 0.0, true, out CaptureFrameRecord accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(accepted, Is.Not.Null);

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.TryGet(accepted.Request, out CaptureFrameRecord retained), Is.True);
                Assert.That(ReferenceEquals(retained, accepted), Is.True);

                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Queue.TryPeek(out CaptureFrameRequest head), Is.True);
                Assert.That(RequestsIdentical(head, accepted.Request), Is.True);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(accepted.Request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                Assert.That(registeredLease.SlotIndex, Is.EqualTo(lease.SlotIndex));

                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void NotSelected_DefaultAndStaleLease_SubmissionNonContact()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRecord accepted = null;
                Assert.That(Submit(scope.Coordinator, default, 0.0, false, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);

                CaptureFrameRenderTargetLease stale = scope.Rent();
                scope.ReturnHeld(stale);
                Assert.That(Submit(scope.Coordinator, stale, 1.0 / 90.0, false, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void IntervalBoundary_NextSubmitted()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease l1 = scope.Rent();
                Assert.That(scope.SubmitAndTrack(l1, 0.0, true, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                CaptureFrameRenderTargetLease l2 = scope.Rent();
                Assert.That(Submit(scope.Coordinator, l2, 1.0 / 90.0, true, 1, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                scope.ReturnHeld(l2);

                CaptureFrameRenderTargetLease l3 = scope.Rent();
                Assert.That(scope.SubmitAndTrack(l3, 1.0 / 45.0, true, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
            });
        }

        [Test]
        public void NinetyHz_SelectsFortyFiveFps()
        {
            CoordinatorScope scope = NewScope(46, 45, 45, 45);
            RunCoordinatorBody(scope, () =>
            {
                int submittedCount = 0;
                int notSelectedCount = 0;

                for (int i = 0; i < 90; i++)
                {
                    double t = i / 90.0;
                    CaptureFrameRenderTargetLease lease = scope.Rent();
                    CaptureFrameCadencedSubmissionStatus status = scope.SubmitAndTrack(lease, t, true, out CaptureFrameRecord accepted);
                    if (status == CaptureFrameCadencedSubmissionStatus.Submitted)
                    {
                        submittedCount++;
                        Assert.That(accepted, Is.Not.Null);
                    }
                    else
                    {
                        Assert.That(status, Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                        notSelectedCount++;
                        scope.ReturnHeld(lease);
                    }
                }

                Assert.That(submittedCount, Is.EqualTo(45));
                Assert.That(notSelectedCount, Is.EqualTo(45));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(45));
            });
        }

        [Test]
        public void QueueFull_Backpressured_OutNull_IdConsumed_LeaseReturnable()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 1);
            RunCoordinatorBody(scope, () =>
            {
                Assert.That(scope.Queue.TryEnqueue(MakeDummyRequest()), Is.True);

                CaptureFrameRenderTargetLease lease = scope.Rent();

                CaptureFrameRecord accepted = null;
                Assert.That(Submit(scope.Coordinator, lease, 0.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.RequestQueueFull));

                scope.ReturnHeld(lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void RecordRegistryFull_Backpressured_LeaseReturnable()
        {
            CoordinatorScope scope = NewScope(2, 2, 1, 2);
            RunCoordinatorBody(scope, () =>
            {
                Assert.That(scope.RecordRegistry.TryRegister(MakeDummyRecord()), Is.True);

                CaptureFrameRenderTargetLease lease = scope.Rent();

                CaptureFrameRecord accepted = null;
                Assert.That(Submit(scope.Coordinator, lease, 0.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));

                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));
                Assert.That(scope.Logger.GetHistoryEvent(0).EventType, Is.EqualTo(TraceEventType.CaptureFrameDropped));
                Assert.That(scope.Logger.GetHistoryEvent(0).Value1, Is.EqualTo((int)CaptureFrameDropReason.FrameRecordRegistryFull));

                scope.ReturnHeld(lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void LeaseRegistryFull_Backpressured_RecordSchedulerNonContact()
        {
            CoordinatorScope scope = NewScope(2, 1, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease l1 = scope.Rent();
                Assert.That(scope.SubmitAndTrack(l1, 0.0, true, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                CaptureFrameRenderTargetLease l2 = scope.Rent();

                CaptureFrameRecord accepted = null;
                Assert.That(Submit(scope.Coordinator, l2, 1.0 / 45.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Queue.Count, Is.EqualTo(1));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(2));

                scope.ReturnHeld(l2);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void BackpressureThenSameTimestamp_NotSelected_NoExtraIdOrTrace()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 1);
            RunCoordinatorBody(scope, () =>
            {
                Assert.That(scope.Queue.TryEnqueue(MakeDummyRequest()), Is.True);

                CaptureFrameRenderTargetLease lease = scope.Rent();
                CaptureFrameRecord accepted = null;
                Assert.That(Submit(scope.Coordinator, lease, 0.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Backpressured));
                Assert.That(accepted, Is.Null);

                CaptureFrameRenderTargetLease lease2 = scope.Rent();
                Assert.That(Submit(scope.Coordinator, lease2, 0.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                scope.Logger.Drain();
                Assert.That(scope.Logger.HistoryCount, Is.EqualTo(1));

                scope.ReturnHeld(lease2);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void InvalidTiming_SelectorException_SubmissionNonContact()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();

                CaptureFrameRecord accepted = null;
                Assert.Throws<ArgumentException>(() => scope.Coordinator.TrySubmit(
                    1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                    default,
                    MakePose(1f, 2f, 3f),
                    MakePose(4f, 5f, 6f),
                    MakePose(7f, 8f, 9f),
                    1,
                    lease,
                    out accepted));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));

                scope.ReturnHeld(lease);
            });
        }

        [Test]
        public void TimestampRegression_SelectorException_IdNotConsumed()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease l1 = scope.Rent();
                Assert.That(scope.SubmitAndTrack(l1, 1.0, true, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                CaptureFrameRenderTargetLease l2 = scope.Rent();
                CaptureFrameRecord accepted = null;
                Assert.Throws<ArgumentOutOfRangeException>(() => Submit(scope.Coordinator, l2, 0.5, true, 1, out accepted));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                scope.ReturnHeld(l2);
            });
        }

        [Test]
        public void FactoryException_OutNull_CadenceMaintained_IdConsumed()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();

                CaptureFrameRecord accepted = null;
                Assert.Throws<ArgumentOutOfRangeException>(() => Submit(scope.Coordinator, lease, 0.0, true, 0, out accepted));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));

                scope.ReturnHeld(lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));

                CaptureFrameRenderTargetLease lease2 = scope.Rent();
                CaptureFrameRecord accepted2 = null;
                Assert.That(Submit(scope.Coordinator, lease2, 0.0, true, 1, out accepted2), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted2, Is.Null);
                scope.ReturnHeld(lease2);
            });
        }

        [Test]
        public void DisposedLogger_SchedulerException_RolledBack_IdAndCadenceConsumed()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();

                scope.Logger.Dispose();

                CaptureFrameRecord accepted = null;
                Assert.Throws<ObjectDisposedException>(() => Submit(scope.Coordinator, lease, 0.0, true, 1, out accepted));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));

                scope.ReturnHeld(lease);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void StaleLeaseOnSelectedFrame_ExistingException_IdConsumed_CadenceNotRolledBack()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();
                scope.ReturnHeld(lease);

                CaptureFrameRecord accepted = null;
                Assert.Throws<InvalidOperationException>(() => Submit(scope.Coordinator, lease, 0.0, true, 1, out accepted));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));

                // Cadence not rolled back: same timestamp again → NotSelected.
                CaptureFrameRenderTargetLease lease2 = scope.Rent();
                Assert.That(Submit(scope.Coordinator, lease2, 0.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                Assert.That(accepted, Is.Null);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                scope.ReturnHeld(lease2);
            });
        }

        [Test]
        public void ForeignPoolLeaseOnSelectedFrame_ExistingException()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            CaptureFrameRenderTargetPool foreignPool = new CaptureFrameRenderTargetPool(1, MakeProfile());
            CaptureFrameRenderTargetLease foreignLease = default;
            bool foreignHeld = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(foreignPool.TryRent(out foreignLease), Is.True);
                foreignHeld = true;

                CaptureFrameRecord accepted = null;
                Assert.Throws<InvalidOperationException>(() => Submit(scope.Coordinator, foreignLease, 0.0, true, 1, out accepted));
                Assert.That(accepted, Is.Null);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(scope.Queue.Count, Is.EqualTo(0));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (foreignHeld)
            {
                foreignHeld = false;
                try { foreignPool.Return(foreignLease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            try { foreignPool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            errors = ConcatExceptions(errors, CleanupCoordinatorScope(scope));
            ThrowCleanupAndBody(body, errors);
        }

        [Test]
        public void SelectorReset_ReSelectable()
        {
            CoordinatorScope scope = NewScope(3, 3, 3, 3);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease l1 = scope.Rent();
                Assert.That(scope.SubmitAndTrack(l1, 0.0, true, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                CaptureFrameRenderTargetLease l2 = scope.Rent();
                Assert.That(Submit(scope.Coordinator, l2, 0.0, true, 1, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.NotSelected));
                scope.ReturnHeld(l2);

                scope.Selector.Reset();

                CaptureFrameRenderTargetLease l3 = scope.Rent();
                Assert.That(scope.SubmitAndTrack(l3, 0.0, true, out CaptureFrameRecord accepted3), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                Assert.That(accepted3.CaptureFrameId, Is.EqualTo(2));
            });
        }

        [Test]
        public void DoesNotDisposeClearReturnDependencies()
        {
            CoordinatorScope scope = NewScope(2, 2, 2, 2);
            RunCoordinatorBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = scope.Rent();
                Assert.That(scope.SubmitAndTrack(lease, 0.0, true, out _), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));

                Assert.That(scope.Pool.IsCreated, Is.True);
                Assert.That(scope.Logger.IsCreated, Is.True);
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.RecordRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Queue.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void TypeShape_SealedNonDisposableNonMonoBehaviour()
        {
            Type type = typeof(CaptureFrameRenderTargetCadencedSubmissionCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(
                    field.FieldType == typeof(CaptureFrameCadenceSelector) || field.FieldType == typeof(CaptureFrameRenderTargetRecordSubmissionCoordinator),
                    Is.True,
                    "Unexpected retained dependency: " + field.Name);
            }
        }

        [Test]
        public void GpuIntegration_RentCadencedSubmitPumpCompleteEnqueueLeaseReturned()
        {
            CaptureFrameProfile profile = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));

            CaptureFrameRequestQueue requestQueue = new CaptureFrameRequestQueue(1);
            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(1, profile);
            CaptureFrameReadbackBufferPool bufferPool = new CaptureFrameReadbackBufferPool(1, 64);
            UnityRenderTextureReadbackDispatcher dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
            CaptureFrameRenderTargetReadbackPump pump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

            TraceLogger logger = new TraceLogger(8);
            CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
            CaptureFrameRequestScheduler requestScheduler = new CaptureFrameRequestScheduler(requestQueue, observer);
            CaptureFrameRecordRegistry recordRegistry = new CaptureFrameRecordRegistry(1);
            CaptureFrameRecordScheduler recordScheduler = new CaptureFrameRecordScheduler(requestScheduler, recordRegistry, observer);
            CaptureFrameRenderTargetRecordScheduler leaseScheduler = new CaptureFrameRenderTargetRecordScheduler(recordScheduler, leaseRegistry);

            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            CaptureFrameRecordFactory factory = new CaptureFrameRecordFactory(
                MakeRun(),
                sequence,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2),
                0,
                CapturePixelFormat.Rgba32);
            CaptureFrameRenderTargetRecordSubmissionCoordinator submission = new CaptureFrameRenderTargetRecordSubmissionCoordinator(factory, leaseScheduler);
            CaptureFrameCadenceSelector selector = new CaptureFrameCadenceSelector(CaptureFrameCadenceSelector.PhaseZeroTargetFramesPerSecond);
            CaptureFrameRenderTargetCadencedSubmissionCoordinator coordinator = new CaptureFrameRenderTargetCadencedSubmissionCoordinator(selector, submission);

            PngJsonCaptureFrameReadbackCompletionRouter router = new PngJsonCaptureFrameReadbackCompletionRouter(dispatcher, observer);
            CaptureFramePngQueue queue = new CaptureFramePngQueue(1);

            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;
            CaptureFrameRequest scheduledRequest = default;
            NativeArray<byte> png = default;
            bool pngHeld = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                Assert.That(pool.TryRent(out lease), Is.True);
                leaseHeld = true;

                CaptureFrameRecord accepted = null;
                Assert.That(Submit(coordinator, lease, 0.0, true, 1, out accepted), Is.EqualTo(CaptureFrameCadencedSubmissionStatus.Submitted));
                registered = true;
                leaseHeld = false;
                scheduledRequest = accepted.Request;
                Assert.That(accepted.CaptureFrameId, Is.EqualTo(1));

                Assert.That(pump.TryStartNext(), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(router.TryCollectEncodeAndEnqueue(queue, recordRegistry, leaseRegistry, pool), Is.EqualTo(CaptureFramePngQueueStatus.Queued));
                registered = false;

                Assert.That(queue.Count, Is.EqualTo(1));
                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(recordRegistry.Count, Is.EqualTo(1));

                Assert.That(queue.TryDequeue(out CaptureFrameRequest frameRequest, out png), Is.True);
                pngHeld = true;
                Assert.That(frameRequest.TraceContext.CaptureFrameId, Is.EqualTo(1));
                AssertPngSignature(png);
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            if (pngHeld)
            {
                pngHeld = false;
                try { png.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            }

            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
                if (dispatcher.IsCreated)
                {
                    while (dispatcher.TryCollect(out CaptureFrameReadbackResult extra))
                    {
                        dispatcher.Release(extra);
                    }
                }
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            if (gpuSafe)
            {
                if (registered)
                {
                    registered = false;
                    try
                    {
                        if (leaseRegistry.TryRemove(scheduledRequest, out CaptureFrameRenderTargetLease removed))
                        {
                            lease = removed;
                            leaseHeld = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                if (leaseHeld)
                {
                    leaseHeld = false;
                    try { pool.Return(lease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }
            }

            try { pool.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { queue.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { logger.Dispose(); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
