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
    public class CaptureFrameRenderTargetDraftCadencedPipelineCoordinatorTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetPipelineCoordinatorType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftCadencedPipelineCoordinator");

        private static Type GetResultType() => GetTypeFromAssembly("CaptureFrameDraftCadencedPipelineResult");

        private static Type GetCadencedCoordinatorType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator");

        private static Type GetCadencedStatusType() => GetTypeFromAssembly("CaptureFrameDraftCadencedSubmissionStatus");

        private static Type GetSubmissionCoordinatorType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftSubmissionCoordinator");

        private static Type GetAdmissionCoordinatorType() => GetTypeFromAssembly("CaptureFrameDraftAdmissionCoordinator");

        private static Type GetSchedulerType() => GetTypeFromAssembly("CaptureFrameRenderTargetDraftScheduler");

        private static Type GetRegistryType() => GetTypeFromAssembly("CaptureFrameDraftRegistry");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureFrameDraftFactory");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        // ---- Input factories ----

        private static CaptureFrameProfile MakeFrameProfile()
        {
            return CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(1, new CaptureImageRect(0, 0, 2, 2));
        }

        private static CaptureTraceProfile MakeTraceProfile(int captureProfileId = 1, int maxInFlight = 2, int maxDraftPerRun = 10)
        {
            return new CaptureTraceProfile(captureProfileId, 4096, maxInFlight, maxDraftPerRun);
        }

        private static object MakeRun(int captureProfileId = 1)
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(TraceRunContext), typeof(long), typeof(int) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");

            TraceRunContext context = new TraceRunContext(
                1, 1000, "build-1", "6000.3.22f1", ValidSha256, "scene-1", 12345, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));
            return ctor.Invoke(new object[] { context, 100, captureProfileId });
        }

        private static object CreateRegistry(object run, CaptureTraceProfile traceProfile)
        {
            ConstructorInfo ctor = GetRegistryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetRunType(), typeof(CaptureTraceProfile) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftRegistry constructor not found.");
            return ctor.Invoke(new object[] { run, traceProfile });
        }

        private static CaptureFrameIdSequence MakeSequence(long? startAt = null)
        {
            if (startAt == null)
            {
                return new CaptureFrameIdSequence();
            }

            ConstructorInfo ctor = typeof(CaptureFrameIdSequence).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameIdSequence)ctor.Invoke(new object[] { startAt.Value });
        }

        private static object CreateFactory(object run, CaptureFrameIdSequence sequence)
        {
            ConstructorInfo ctor = GetFactoryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetRunType(), typeof(CaptureFrameIdSequence), typeof(CaptureSource), typeof(CaptureEye),
                    typeof(CaptureImageRect).MakeByRefType(), typeof(int), typeof(CapturePixelFormat)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftFactory constructor not found.");
            return ctor.Invoke(new object[] { run, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32 });
        }

        private static object CreateAdmissionCoordinator(object factory, object registry, CaptureFrameTraceObserver observer)
        {
            ConstructorInfo ctor = GetAdmissionCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetFactoryType(), GetRegistryType(), typeof(CaptureFrameTraceObserver) }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftAdmissionCoordinator constructor not found.");
            return ctor.Invoke(new object[] { factory, registry, observer });
        }

        private static object CreateScheduler(object registry, CaptureFrameRequestQueue queue, CaptureFrameRenderTargetLeaseRegistry leaseRegistry, CaptureFrameTraceObserver observer)
        {
            ConstructorInfo ctor = GetSchedulerType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetRegistryType(), typeof(CaptureFrameRequestQueue), typeof(CaptureFrameRenderTargetLeaseRegistry), typeof(CaptureFrameTraceObserver) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameRenderTargetDraftScheduler constructor not found.");
            return ctor.Invoke(new object[] { registry, queue, leaseRegistry, observer });
        }

        private static object CreateSubmissionCoordinator(object admissionCoordinator, object scheduler)
        {
            ConstructorInfo ctor = GetSubmissionCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { GetAdmissionCoordinatorType(), GetSchedulerType() }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameRenderTargetDraftSubmissionCoordinator constructor not found.");
            return ctor.Invoke(new object[] { admissionCoordinator, scheduler });
        }

        private static object CreateCadencedCoordinator(CaptureFrameCadenceSelector selector, object submissionCoordinator)
        {
            ConstructorInfo ctor = GetCadencedCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(CaptureFrameCadenceSelector), GetSubmissionCoordinatorType() }, null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameRenderTargetDraftCadencedSubmissionCoordinator constructor not found.");
            return ctor.Invoke(new object[] { selector, submissionCoordinator });
        }

        private static object CreatePipelineCoordinator(
            object cadencedCoordinator,
            CaptureFrameRenderTargetCopyPump copyPump,
            CaptureFrameRenderTargetReadbackPump readbackPump,
            CaptureFrameRequestQueue requestQueue,
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry)
        {
            ConstructorInfo ctor = GetPipelineCoordinatorType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetCadencedCoordinatorType(),
                    typeof(CaptureFrameRenderTargetCopyPump),
                    typeof(CaptureFrameRenderTargetReadbackPump),
                    typeof(CaptureFrameRequestQueue),
                    typeof(CaptureFrameRenderTargetLeaseRegistry)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameRenderTargetDraftCadencedPipelineCoordinator constructor not found.");
            return ctor.Invoke(new object[] { cadencedCoordinator, copyPump, readbackPump, requestQueue, leaseRegistry });
        }

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static object MakeDraft(object run, CaptureFrameRequest request, int commitPathId = 1)
        {
            ConstructorInfo ctor = GetDraftType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetRunType(),
                    typeof(CaptureFrameRequest).MakeByRefType(),
                    typeof(CaptureFrameTiming).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(int)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraft constructor not found.");
            return ctor.Invoke(new object[] { run, request, MakeTimingAt(0.0), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), commitPathId });
        }

        private static CaptureFrameTiming MakeTimingAt(double displayTimeSeconds, bool shouldRender = true)
        {
            return new CaptureFrameTiming(displayTimeSeconds, 1.0 / 90.0, shouldRender, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        // ---- Registry helpers ----

        private static bool RegistryTryReserve(object registry, out object reservation, out object rejectKind)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryReserve", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { null, null };
            bool ok = (bool)method.Invoke(registry, args);
            reservation = args[0];
            rejectKind = args[1];
            return ok;
        }

        private static void RegistryCommit(object registry, object reservation, object draft)
        {
            MethodInfo method = GetRegistryType().GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(registry, new object[] { reservation, draft });
        }

        private static bool RegistryTryGet(object registry, CaptureFrameRequest request, out object draft, out object status)
        {
            MethodInfo method = GetRegistryType().GetMethod("TryGet", BindingFlags.NonPublic | BindingFlags.Instance);
            object[] args = new object[] { request, null, null };
            bool ok = (bool)method.Invoke(registry, args);
            draft = args[1];
            status = args[2];
            return ok;
        }

        private static int Count(object registry, string name)
        {
            return (int)GetProperty(registry, name);
        }

        private static void PreCommitDraft(object registry, object run, long captureFrameId)
        {
            object reservation, rejectKind;
            Assert.That(RegistryTryReserve(registry, out reservation, out rejectKind), Is.True);
            RegistryCommit(registry, reservation, MakeDraft(run, MakeRequest(captureFrameId)));
        }

        private static bool LeasesIdentical(CaptureFrameRenderTargetLease a, CaptureFrameRenderTargetLease b)
        {
            if (a.SlotIndex != b.SlotIndex || a.IsValid != b.IsValid)
            {
                return false;
            }

            FieldInfo ownerTokenField = typeof(CaptureFrameRenderTargetLease).GetField("_ownerToken", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo generationField = typeof(CaptureFrameRenderTargetLease).GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance);
            return (Guid)ownerTokenField.GetValue(a) == (Guid)ownerTokenField.GetValue(b)
                && (long)generationField.GetValue(a) == (long)generationField.GetValue(b);
        }

        private static void AssertRequestIdentical(in CaptureFrameRequest expected, in CaptureFrameRequest actual)
        {
            CaptureFrameTraceContext e = expected.TraceContext;
            CaptureFrameTraceContext a = actual.TraceContext;

            Assert.That(a.Timestamp, Is.EqualTo(e.Timestamp));
            Assert.That(a.UnityFrameId, Is.EqualTo(e.UnityFrameId));
            Assert.That(a.FixedStepId, Is.EqualTo(e.FixedStepId));
            Assert.That(a.ThreadId, Is.EqualTo(e.ThreadId));
            Assert.That(a.CaptureFrameId, Is.EqualTo(e.CaptureFrameId));
            Assert.That(a.OpenXRFrameId, Is.EqualTo(e.OpenXRFrameId));
            Assert.That(a.TestRunId, Is.EqualTo(e.TestRunId));
            Assert.That(a.SlashId, Is.EqualTo(e.SlashId));
            Assert.That(a.FrontEdgeId, Is.EqualTo(e.FrontEdgeId));
            Assert.That(a.ObjectId, Is.EqualTo(e.ObjectId));
            Assert.That(a.ObjectGeneration, Is.EqualTo(e.ObjectGeneration));
            Assert.That(a.TaskId, Is.EqualTo(e.TaskId));

            Assert.That(actual.Source, Is.EqualTo(expected.Source));
            Assert.That(actual.Eye, Is.EqualTo(expected.Eye));
            Assert.That(actual.ImageRect.X, Is.EqualTo(expected.ImageRect.X));
            Assert.That(actual.ImageRect.Y, Is.EqualTo(expected.ImageRect.Y));
            Assert.That(actual.ImageRect.Width, Is.EqualTo(expected.ImageRect.Width));
            Assert.That(actual.ImageRect.Height, Is.EqualTo(expected.ImageRect.Height));
            Assert.That(actual.ArrayIndex, Is.EqualTo(expected.ArrayIndex));
            Assert.That(actual.PixelLayout.Format, Is.EqualTo(expected.PixelLayout.Format));
            Assert.That(actual.PixelLayout.Width, Is.EqualTo(expected.PixelLayout.Width));
            Assert.That(actual.PixelLayout.Height, Is.EqualTo(expected.PixelLayout.Height));
            Assert.That(actual.PixelLayout.BytesPerPixel, Is.EqualTo(expected.PixelLayout.BytesPerPixel));
            Assert.That(actual.PixelLayout.RowStrideBytes, Is.EqualTo(expected.PixelLayout.RowStrideBytes));
            Assert.That(actual.PixelLayout.ByteCount, Is.EqualTo(expected.PixelLayout.ByteCount));
            Assert.That(actual.RequiredByteCount, Is.EqualTo(expected.RequiredByteCount));
        }

        private static void FillSolidColor(RenderTexture rt, Color32 color)
        {
            int width = rt.width;
            int height = rt.height;
            Texture2D temp = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                Color32[] pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                temp.SetPixels32(pixels);
                temp.Apply();
                Graphics.CopyTexture(temp, rt);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static void DestroyTexture(RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }

            if (rt.IsCreated())
            {
                rt.Release();
            }

            UnityEngine.Object.DestroyImmediate(rt);
        }

        private static byte[] ReadBackTarget(RenderTexture rt, int width, int height)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            AsyncGPUReadback.WaitAllRequests();
            Assert.That(request.hasError, Is.False);

            NativeArray<byte> data = request.GetData<byte>();
            Assert.That(data.Length, Is.EqualTo(width * height * 4));

            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = data[i];
            }

            return result;
        }

        // ---- Cleanup helpers ----

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

        private sealed class PipelineScope
        {
            public int PoolCapacity;
            public int LeaseCapacity;
            public int QueueCapacity;
            public int BufferSlotCount;
            public int MaxInFlight;
            public int MaxDraftPerRun;
            public long? SequenceStart;
            public double TargetFps;
            public TraceLogger Logger;
            public CaptureFrameTraceObserver Observer;
            public CaptureFrameRequestQueue RequestQueue;
            public CaptureFrameRenderTargetPool Pool;
            public CaptureFrameRenderTargetLeaseRegistry LeaseRegistry;
            public CaptureFrameIdSequence Sequence;
            public CaptureFrameCadenceSelector CadenceSelector;
            public CaptureFrameReadbackBufferPool BufferPool;
            public UnityRenderTextureReadbackDispatcher Dispatcher;
            public CaptureFrameRenderTargetCopyPump CopyPump;
            public CaptureFrameRenderTargetReadbackPump ReadbackPump;
            public object Run;
            public object Registry;
            public object Factory;
            public object AdmissionCoordinator;
            public object Scheduler;
            public object SubmissionCoordinator;
            public object CadencedCoordinator;
            public object PipelineCoordinator;
            public readonly List<CaptureFrameRenderTargetLease> Held = new List<CaptureFrameRenderTargetLease>();
            public readonly List<CaptureFrameRequest> Registered = new List<CaptureFrameRequest>();
            public readonly List<RenderTexture> Sources = new List<RenderTexture>();
        }

        private static PipelineScope NewScope(
            int poolCapacity,
            int leaseCapacity,
            int queueCapacity,
            int bufferSlotCount = 2,
            int maxInFlight = 2,
            int maxDraftPerRun = 10,
            long? sequenceStart = null,
            double targetFps = 45.0)
        {
            PipelineScope scope = new PipelineScope();
            scope.PoolCapacity = poolCapacity;
            scope.LeaseCapacity = leaseCapacity;
            scope.QueueCapacity = queueCapacity;
            scope.BufferSlotCount = bufferSlotCount;
            scope.MaxInFlight = maxInFlight;
            scope.MaxDraftPerRun = maxDraftPerRun;
            scope.SequenceStart = sequenceStart;
            scope.TargetFps = targetFps;
            return scope;
        }

        private static void BuildScope(PipelineScope scope)
        {
            scope.Logger = new TraceLogger(8);
            scope.Observer = new CaptureFrameTraceObserver(scope.Logger);
            scope.RequestQueue = new CaptureFrameRequestQueue(scope.QueueCapacity);
            scope.Pool = new CaptureFrameRenderTargetPool(scope.PoolCapacity, MakeFrameProfile());
            scope.LeaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(scope.LeaseCapacity, scope.Pool);
            scope.Sequence = MakeSequence(scope.SequenceStart);
            scope.CadenceSelector = new CaptureFrameCadenceSelector(scope.TargetFps);
            scope.BufferPool = new CaptureFrameReadbackBufferPool(scope.BufferSlotCount, 64);
            scope.Dispatcher = new UnityRenderTextureReadbackDispatcher(scope.BufferPool);
            scope.CopyPump = new CaptureFrameRenderTargetCopyPump(scope.RequestQueue, scope.LeaseRegistry, scope.Pool);
            scope.ReadbackPump = new CaptureFrameRenderTargetReadbackPump(scope.RequestQueue, scope.Dispatcher, scope.LeaseRegistry, scope.Pool);
            scope.Run = MakeRun(captureProfileId: 1);
            scope.Registry = CreateRegistry(scope.Run, MakeTraceProfile(captureProfileId: 1, maxInFlight: scope.MaxInFlight, maxDraftPerRun: scope.MaxDraftPerRun));
            scope.Factory = CreateFactory(scope.Run, scope.Sequence);
            scope.AdmissionCoordinator = CreateAdmissionCoordinator(scope.Factory, scope.Registry, scope.Observer);
            scope.Scheduler = CreateScheduler(scope.Registry, scope.RequestQueue, scope.LeaseRegistry, scope.Observer);
            scope.SubmissionCoordinator = CreateSubmissionCoordinator(scope.AdmissionCoordinator, scope.Scheduler);
            scope.CadencedCoordinator = CreateCadencedCoordinator(scope.CadenceSelector, scope.SubmissionCoordinator);
            scope.PipelineCoordinator = CreatePipelineCoordinator(scope.CadencedCoordinator, scope.CopyPump, scope.ReadbackPump, scope.RequestQueue, scope.LeaseRegistry);
        }

        private static CaptureFrameRenderTargetLease Rent(PipelineScope scope)
        {
            bool rented = scope.Pool.TryRent(out CaptureFrameRenderTargetLease lease);
            if (rented)
            {
                scope.Held.Add(lease);
            }

            Assert.That(rented, Is.True);
            return lease;
        }

        private static RenderTexture CreateSource(
            PipelineScope scope,
            int width,
            int height,
            RenderTextureFormat format = RenderTextureFormat.ARGB32,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.sRGB)
        {
            RenderTexture rt = new RenderTexture(width, height, 0, format, readWrite);
            scope.Sources.Add(rt);
            rt.Create();
            return rt;
        }

        private static CaptureFrameRequest TrackSubmissionOwnership(PipelineScope scope, object draft, CaptureFrameRenderTargetLease lease)
        {
            if (draft == null)
            {
                return default;
            }

            CaptureFrameRequest request = (CaptureFrameRequest)GetProperty(draft, "Request");

            if (scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease))
            {
                scope.Registered.Add(request);
                if (LeasesIdentical(registeredLease, lease))
                {
                    scope.Held.RemoveAll(l => l.SlotIndex == lease.SlotIndex);
                }
            }

            return request;
        }

        private static Exception[] CleanupPipelineScope(PipelineScope scope)
        {
            Exception[] errors = null;
            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
            }
            catch (Exception ex)
            {
                gpuSafe = false;
                errors = AppendCleanupException(errors, ex);
            }

            try
            {
                if (scope.Dispatcher != null && scope.Dispatcher.IsCreated)
                {
                    while (scope.Dispatcher.TryCollect(out CaptureFrameReadbackResult result))
                    {
                        scope.Dispatcher.Release(result);
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
                for (int i = scope.Sources.Count - 1; i >= 0; i--)
                {
                    RenderTexture source = scope.Sources[i];
                    scope.Sources.RemoveAt(i);
                    try
                    {
                        DestroyTexture(source);
                    }
                    catch (Exception ex)
                    {
                        errors = AppendCleanupException(errors, ex);
                    }
                }

                for (int i = scope.Registered.Count - 1; i >= 0; i--)
                {
                    CaptureFrameRequest request = scope.Registered[i];
                    scope.Registered.RemoveAt(i);
                    try
                    {
                        if (scope.LeaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease lease))
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
            }

            try { if (scope.Dispatcher != null && scope.Dispatcher.IsCreated) { scope.Dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (scope.BufferPool != null && scope.BufferPool.IsCreated) { scope.BufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (scope.Pool != null && scope.Pool.IsCreated) { scope.Pool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (scope.Logger != null && scope.Logger.IsCreated) { scope.Logger.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            return errors;
        }

        private static void RunPipelineBody(PipelineScope scope, Action body)
        {
            ExceptionDispatchInfo bodyException = null;
            try
            {
                BuildScope(scope);
                body();
            }
            catch (Exception ex)
            {
                bodyException = ExceptionDispatchInfo.Capture(ex);
            }

            Exception[] errors = CleanupPipelineScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        // ---- Invoke helper ----

        private sealed class PipelineResult
        {
            public int Status = -1;
            public bool CopyCompleted;
            public bool ReadbackStarted;
            public object Draft;
            public Exception Exception;
        }

        private static PipelineResult InvokePipelineSubmit(
            object coordinator,
            CaptureFrameTiming timing,
            RenderTexture source,
            CaptureFrameRenderTargetLease lease,
            int commitPathId = 1)
        {
            PipelineResult result = new PipelineResult();
            MethodInfo method = GetPipelineCoordinatorType().GetMethod("TrySubmitCopyAndStart", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "TrySubmitCopyAndStart method not found.");

            object[] args = new object[]
            {
                1000L, 200L, 300L, 4, 500L,
                600L, 700L, 800L, 9u, 1000L,
                timing, MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f),
                commitPathId, source, lease, null
            };

            try
            {
                object boxed = method.Invoke(coordinator, args);
                result.Status = (int)GetProperty(boxed, "SubmissionStatus");
                result.CopyCompleted = (bool)GetProperty(boxed, "CopyCompleted");
                result.ReadbackStarted = (bool)GetProperty(boxed, "ReadbackStarted");
                result.Draft = args[17];
            }
            catch (Exception ex)
            {
                result.Status = -1;
                result.Draft = args[17];
                result.Exception = Unwrap(ex);
            }

            return result;
        }

        private static void AssertNullParam(ConstructorInfo ctor, object[] args, string paramName)
        {
            try
            {
                ctor.Invoke(args);
                Assert.Fail("Expected ArgumentNullException.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.TypeOf<ArgumentNullException>());
                Assert.That(((ArgumentNullException)ex.InnerException).ParamName, Is.EqualTo(paramName));
            }
        }

        private static void AssertArgumentException(ConstructorInfo ctor, object[] args, string paramName)
        {
            try
            {
                ctor.Invoke(args);
                Assert.Fail("Expected ArgumentException.");
            }
            catch (TargetInvocationException ex)
            {
                Exception inner = Unwrap(ex);
                Assert.That(inner, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)inner).ParamName, Is.EqualTo(paramName));
            }
        }

        // ---- Result contracts ----

        [Test]
        public void Result_Default_NotRunState()
        {
            object def = Activator.CreateInstance(GetResultType());

            Assert.That((int)GetProperty(def, "SubmissionStatus"), Is.EqualTo(0)); // None
            Assert.That((bool)GetProperty(def, "CopyCompleted"), Is.False);
            Assert.That((bool)GetProperty(def, "ReadbackStarted"), Is.False);
        }

        [Test]
        public void Result_AllValidStatusCombinations_Accepted()
        {
            Type resultType = GetResultType();
            Type statusType = GetCadencedStatusType();
            ConstructorInfo ctor = resultType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { statusType, typeof(bool), typeof(bool) }, null);
            Assert.That(ctor, Is.Not.Null);

            Assert.DoesNotThrow(() => ctor.Invoke(new object[] { Enum.ToObject(statusType, 1), false, false })); // NotSelected
            Assert.DoesNotThrow(() => ctor.Invoke(new object[] { Enum.ToObject(statusType, 2), false, false })); // AdmissionRejected
            Assert.DoesNotThrow(() => ctor.Invoke(new object[] { Enum.ToObject(statusType, 4), false, false })); // SchedulingBackpressured
            Assert.DoesNotThrow(() => ctor.Invoke(new object[] { Enum.ToObject(statusType, 3), true, false }));  // Scheduled
            Assert.DoesNotThrow(() => ctor.Invoke(new object[] { Enum.ToObject(statusType, 3), true, true }));   // Scheduled
        }

        [Test]
        public void Result_UndefinedStatus_Rejected()
        {
            Type resultType = GetResultType();
            Type statusType = GetCadencedStatusType();
            ConstructorInfo ctor = resultType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { statusType, typeof(bool), typeof(bool) }, null);
            Assert.That(ctor, Is.Not.Null);

            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 5), false, false }, "submissionStatus");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, -1), false, false }, "submissionStatus");
        }

        [Test]
        public void Result_InvalidFlagCombinations_Rejected()
        {
            Type resultType = GetResultType();
            Type statusType = GetCadencedStatusType();
            ConstructorInfo ctor = resultType.GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { statusType, typeof(bool), typeof(bool) }, null);
            Assert.That(ctor, Is.Not.Null);

            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 0), true, false }, "copyCompleted");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 1), true, false }, "copyCompleted");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 1), false, true }, "readbackStarted");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 2), false, true }, "readbackStarted");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 4), false, true }, "readbackStarted");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 3), false, false }, "copyCompleted");
            AssertArgumentException(ctor, new object[] { Enum.ToObject(statusType, 3), false, true }, "copyCompleted");
        }

        // ---- Constructor contracts ----

        [Test]
        public void Constructor_FiveNullDependencies_Rejected()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                ConstructorInfo ctor = GetPipelineCoordinatorType().GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[]
                    {
                        GetCadencedCoordinatorType(),
                        typeof(CaptureFrameRenderTargetCopyPump),
                        typeof(CaptureFrameRenderTargetReadbackPump),
                        typeof(CaptureFrameRequestQueue),
                        typeof(CaptureFrameRenderTargetLeaseRegistry)
                    },
                    null);

                AssertNullParam(ctor, new object[] { null, scope.CopyPump, scope.ReadbackPump, scope.RequestQueue, scope.LeaseRegistry }, "submissionCoordinator");
                AssertNullParam(ctor, new object[] { scope.CadencedCoordinator, null, scope.ReadbackPump, scope.RequestQueue, scope.LeaseRegistry }, "copyPump");
                AssertNullParam(ctor, new object[] { scope.CadencedCoordinator, scope.CopyPump, null, scope.RequestQueue, scope.LeaseRegistry }, "readbackPump");
                AssertNullParam(ctor, new object[] { scope.CadencedCoordinator, scope.CopyPump, scope.ReadbackPump, null, scope.LeaseRegistry }, "requestQueue");
                AssertNullParam(ctor, new object[] { scope.CadencedCoordinator, scope.CopyPump, scope.ReadbackPump, scope.RequestQueue, null }, "leaseRegistry");
            });
        }

        // ---- Fail closed on a pending request ----

        [Test]
        public void ExistingPendingRequest_FailClosed_NoCadenceIdSourceRegistryTouch()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                Assert.That(scope.RequestQueue.TryEnqueue(MakeRequest(99)), Is.True);

                CaptureFrameRenderTargetLease lease = Rent(scope);

                // A null source must not be validated because the coordinator
                // fails closed before touching cadence, submission, or source.
                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), null, lease);

                Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(scope.CadenceSelector.HasObservedTimestamp, Is.False);
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(0));
            });
        }

        // ---- NotSelected ----

        [Test]
        public void NotSelected_NullAndUncreatedSource_Unvalidated()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);

                PipelineResult r1 = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0, shouldRender: false), null, lease);
                Assert.That(r1.Exception, Is.Null);
                Assert.That(r1.Status, Is.EqualTo(1)); // NotSelected
                Assert.That(r1.CopyCompleted, Is.False);
                Assert.That(r1.ReadbackStarted, Is.False);
                Assert.That(r1.Draft, Is.Null);

                RenderTexture uncreated = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                scope.Sources.Add(uncreated);
                PipelineResult r2 = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.02, shouldRender: false), uncreated, lease);
                Assert.That(r2.Exception, Is.Null);
                Assert.That(r2.Status, Is.EqualTo(1)); // NotSelected

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(0));
            });
        }

        // ---- AdmissionRejected ----

        [Test]
        public void AdmissionRejected_SourceUnvalidated_OutNull_IdNotConsumed()
        {
            PipelineScope scope = NewScope(2, 2, 2, maxInFlight: 1, maxDraftPerRun: 1);
            RunPipelineBody(scope, () =>
            {
                PreCommitDraft(scope.Registry, scope.Run, 5);

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), null, default(CaptureFrameRenderTargetLease));

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(2)); // AdmissionRejected
                Assert.That(result.CopyCompleted, Is.False);
                Assert.That(result.ReadbackStarted, Is.False);
                Assert.That(result.Draft, Is.Null);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(0));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        // ---- SchedulingBackpressured ----

        [Test]
        public void SchedulingBackpressured_SourceUnvalidated_OutDraftNonNull_LeaseCallerOwned()
        {
            // The coordinator fails closed when the queue is non-empty, so the
            // only reachable backpressure here is a full lease registry.
            PipelineScope scope = NewScope(2, 1, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease otherLease = Rent(scope);
                Assert.That(scope.LeaseRegistry.TryRegister(MakeRequest(99), otherLease), Is.True);
                scope.Held.RemoveAll(l => l.SlotIndex == otherLease.SlotIndex);
                scope.Registered.Add(MakeRequest(99));

                CaptureFrameRenderTargetLease lease = Rent(scope);
                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), null, lease);
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(4)); // SchedulingBackpressured
                Assert.That(result.CopyCompleted, Is.False);
                Assert.That(result.ReadbackStarted, Is.False);
                Assert.That(result.Draft, Is.Not.Null);
                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(request, out _), Is.False); // caller-owned
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        // ---- Scheduled ----

        [Test]
        public void Scheduled_DraftQueueLeaseFullMatch()
        {
            PipelineScope scope = NewScope(2, 2, 2, bufferSlotCount: 1);
            RunPipelineBody(scope, () =>
            {
                Assert.That(scope.BufferPool.TryRent(out int reservedSlot), Is.True);
                try
                {
                    CaptureFrameRenderTargetLease lease = Rent(scope);
                    RenderTexture source = CreateSource(scope, 2, 2);

                    PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                    CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                    Assert.That(result.Exception, Is.Null);
                    Assert.That(result.Status, Is.EqualTo(3)); // Scheduled
                    Assert.That(result.CopyCompleted, Is.True);
                    Assert.That(result.ReadbackStarted, Is.False);
                    Assert.That(result.Draft, Is.Not.Null);

                    Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                    Assert.That(scope.RequestQueue.TryPeek(out CaptureFrameRequest head), Is.True);
                    AssertRequestIdentical(head, request);

                    Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                    Assert.That(LeasesIdentical(registeredLease, lease), Is.True);
                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                    Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));

                    object registeredDraft;
                    object status;
                    Assert.That(RegistryTryGet(scope.Registry, request, out registeredDraft, out status), Is.True);
                    Assert.That(ReferenceEquals(registeredDraft, result.Draft), Is.True);
                    Assert.That((int)status, Is.EqualTo(0)); // Pending
                }
                finally
                {
                    scope.BufferPool.Return(reservedSlot);
                }
            });
        }

        [Test]
        public void Scheduled_LeaseOwnerTokenSlotGenerationExactMatch()
        {
            PipelineScope scope = NewScope(2, 2, 2, bufferSlotCount: 1);
            RunPipelineBody(scope, () =>
            {
                Assert.That(scope.BufferPool.TryRent(out int reservedSlot), Is.True);
                try
                {
                    CaptureFrameRenderTargetLease lease = Rent(scope);
                    RenderTexture source = CreateSource(scope, 2, 2);

                    PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                    CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                    Assert.That(result.Status, Is.EqualTo(3));
                    Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                    Assert.That(registeredLease.SlotIndex, Is.EqualTo(lease.SlotIndex));

                    FieldInfo ownerTokenField = typeof(CaptureFrameRenderTargetLease).GetField("_ownerToken", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo generationField = typeof(CaptureFrameRenderTargetLease).GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert.That((Guid)ownerTokenField.GetValue(registeredLease), Is.EqualTo((Guid)ownerTokenField.GetValue(lease)));
                    Assert.That((long)generationField.GetValue(registeredLease), Is.EqualTo((long)generationField.GetValue(lease)));
                }
                finally
                {
                    scope.BufferPool.Return(reservedSlot);
                }
            });
        }

        // ---- Copy before readback start ----

        [Test]
        public void CopyBeforeReadbackStart_CopiedContentIsReadBack()
        {
            PipelineScope scope = NewScope(2, 2, 2, bufferSlotCount: 1);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                RenderTexture target = scope.Pool.GetRenderTexture(lease);

                // Pre-fill the target with a sentinel so a readback that started
                // before the copy would return the sentinel instead of the source.
                Color32 sentinel = new Color32(255, 0, 0, 255);
                FillSolidColor(target, sentinel);

                RenderTexture source = CreateSource(scope, 2, 2);
                Color32 copied = new Color32(0, 128, 64, 255);
                FillSolidColor(source, copied);

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Status, Is.EqualTo(3));
                Assert.That(result.CopyCompleted, Is.True);
                Assert.That(result.ReadbackStarted, Is.True);

                AsyncGPUReadback.WaitAllRequests();
                Assert.That(scope.Dispatcher.TryCollect(out CaptureFrameReadbackResult readbackResult), Is.True);
                NativeArray<byte> data = scope.BufferPool.GetBuffer(readbackResult.BufferSlotIndex);
                for (int i = 0; i < 16; i += 4)
                {
                    Assert.That(data[i], Is.EqualTo(copied.r));
                    Assert.That(data[i + 1], Is.EqualTo(copied.g));
                    Assert.That(data[i + 2], Is.EqualTo(copied.b));
                    Assert.That(data[i + 3], Is.EqualTo(copied.a));
                }

                scope.Dispatcher.Release(readbackResult);
            });
        }

        // ---- Start success ----

        [Test]
        public void StartSuccess_QueueEmpty()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                RenderTexture source = CreateSource(scope, 2, 2);

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(3));
                Assert.That(result.CopyCompleted, Is.True);
                Assert.That(result.ReadbackStarted, Is.True);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
            });
        }

        // ---- Start false / retry ----

        [Test]
        public void StartFalse_BufferPoolFull_QueueDraftLeaseTargetContentKept()
        {
            PipelineScope scope = NewScope(2, 2, 2, bufferSlotCount: 1);
            RunPipelineBody(scope, () =>
            {
                Assert.That(scope.BufferPool.TryRent(out int reservedSlot), Is.True);
                try
                {
                    CaptureFrameRenderTargetLease lease = Rent(scope);
                    RenderTexture source = CreateSource(scope, 2, 2);
                    Color32 color = new Color32(10, 20, 30, 255);
                    FillSolidColor(source, color);

                    PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                    CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                    Assert.That(result.Status, Is.EqualTo(3));
                    Assert.That(result.CopyCompleted, Is.True);
                    Assert.That(result.ReadbackStarted, Is.False);

                    Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                    Assert.That(scope.RequestQueue.TryPeek(out CaptureFrameRequest head), Is.True);
                    AssertRequestIdentical(head, request);
                    Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                    Assert.That(LeasesIdentical(registeredLease, lease), Is.True);
                    Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));

                    // The copied content is preserved in the target.
                    byte[] bytes = ReadBackTarget(scope.Pool.GetRenderTexture(lease), 2, 2);
                    for (int i = 0; i < bytes.Length; i += 4)
                    {
                        Assert.That(bytes[i], Is.EqualTo(color.r));
                        Assert.That(bytes[i + 1], Is.EqualTo(color.g));
                        Assert.That(bytes[i + 2], Is.EqualTo(color.b));
                        Assert.That(bytes[i + 3], Is.EqualTo(color.a));
                    }
                }
                finally
                {
                    scope.BufferPool.Return(reservedSlot);
                }
            });
        }

        [Test]
        public void SlotFreed_RetrySameRequest_DirectReadbackPump_Succeeds()
        {
            PipelineScope scope = NewScope(2, 2, 2, bufferSlotCount: 1);
            RunPipelineBody(scope, () =>
            {
                Assert.That(scope.BufferPool.TryRent(out int reservedSlot), Is.True);
                bool slotHeld = true;
                try
                {
                    CaptureFrameRenderTargetLease lease = Rent(scope);
                    RenderTexture source = CreateSource(scope, 2, 2);

                    PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                    CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                    Assert.That(result.Status, Is.EqualTo(3));
                    Assert.That(result.ReadbackStarted, Is.False);
                    Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                    Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);

                    // Free the slot, then retry the same request directly through
                    // the existing readback pump (no re-admission, no re-copy).
                    scope.BufferPool.Return(reservedSlot);
                    slotHeld = false;

                    Assert.That(scope.ReadbackPump.TryStartNext(), Is.True);
                    Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                    Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                    Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease afterLease), Is.True);
                    Assert.That(LeasesIdentical(afterLease, registeredLease), Is.True);
                }
                finally
                {
                    if (slotHeld)
                    {
                        scope.BufferPool.Return(reservedSlot);
                    }
                }
            });
        }

        // ---- Copy exception paths ----

        [Test]
        public void CopyException_NullSource_DraftQueueLeaseKept()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), null, lease);
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Exception, Is.TypeOf<ArgumentNullException>());
                Assert.That(result.Draft, Is.Not.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.TryPeek(out CaptureFrameRequest head), Is.True);
                AssertRequestIdentical(head, request);
                Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                Assert.That(LeasesIdentical(registeredLease, lease), Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void CopyException_FormatMismatch_DraftQueueLeaseKept()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);

                RenderTexture formatMismatch = CreateSource(scope, 2, 2, RenderTextureFormat.RGB565);

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), formatMismatch, lease);
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Exception, Is.TypeOf<ArgumentException>());
                Assert.That(result.Draft, Is.Not.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);
                Assert.That(LeasesIdentical(registeredLease, lease), Is.True);
                Assert.That(scope.Pool.RentedCount, Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void CopyException_ThenRetryWithValidSource_CopyAndReadbackSucceed()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);

                PipelineResult first = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), null, lease);
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, first.Draft, lease);

                Assert.That(first.Exception, Is.TypeOf<ArgumentNullException>());
                Assert.That(first.Draft, Is.Not.Null);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease), Is.True);

                // Retry directly through the copy pump with a valid source, then
                // the readback pump, without re-admission.
                RenderTexture source = CreateSource(scope, 2, 2);
                Assert.That(scope.CopyPump.TryCopyNext(source), Is.True);
                Assert.That(scope.ReadbackPump.TryStartNext(), Is.True);
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
            });
        }

        // ---- Scheduler exception ----

        [Test]
        public void SchedulerException_OutDraftNonNull_ExceptionTypeMaintained()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                scope.Logger.Dispose();

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), null, lease);
                CaptureFrameRequest request = TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Exception, Is.TypeOf<ObjectDisposedException>());
                Assert.That(result.Draft, Is.Not.Null);
                Assert.That(scope.LeaseRegistry.TryGet(request, out _), Is.False);
                Assert.That(Count(scope.Registry, "PendingCount"), Is.EqualTo(1));
            });
        }

        // ---- At-most-once ----

        [Test]
        public void SingleCall_SubmissionCopyStartAtMostOnce()
        {
            PipelineScope scope = NewScope(2, 2, 2);
            RunPipelineBody(scope, () =>
            {
                CaptureFrameRenderTargetLease lease = Rent(scope);
                RenderTexture source = CreateSource(scope, 2, 2);

                PipelineResult result = InvokePipelineSubmit(scope.PipelineCoordinator, MakeTimingAt(0.0), source, lease);
                TrackSubmissionOwnership(scope, result.Draft, lease);

                Assert.That(result.Status, Is.EqualTo(3));
                Assert.That(result.ReadbackStarted, Is.True);

                Assert.That(scope.Sequence.LastIssued, Is.EqualTo(1));
                Assert.That(Count(scope.Registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(scope.Dispatcher.ActiveCount, Is.EqualTo(1));
                Assert.That(scope.LeaseRegistry.Count, Is.EqualTo(1));
                Assert.That(scope.RequestQueue.Count, Is.EqualTo(0));
            });
        }

        // ---- Type shape ----

        [Test]
        public void Coordinator_HoldsOnlyFiveDependencies()
        {
            Type type = GetPipelineCoordinatorType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(5));

            bool hasSubmission = false;
            bool hasCopy = false;
            bool hasReadback = false;
            bool hasQueue = false;
            bool hasLeaseRegistry = false;
            foreach (FieldInfo field in fields)
            {
                hasSubmission |= field.FieldType == GetCadencedCoordinatorType();
                hasCopy |= field.FieldType == typeof(CaptureFrameRenderTargetCopyPump);
                hasReadback |= field.FieldType == typeof(CaptureFrameRenderTargetReadbackPump);
                hasQueue |= field.FieldType == typeof(CaptureFrameRequestQueue);
                hasLeaseRegistry |= field.FieldType == typeof(CaptureFrameRenderTargetLeaseRegistry);

                Assert.That(field.FieldType, Is.Not.EqualTo(GetDraftType()), "Coordinator must not hold a draft.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRenderTargetLease)), "Coordinator must not hold a lease.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(RenderTexture)), "Coordinator must not hold a source.");
            }

            Assert.That(hasSubmission, Is.True);
            Assert.That(hasCopy, Is.True);
            Assert.That(hasReadback, Is.True);
            Assert.That(hasQueue, Is.True);
            Assert.That(hasLeaseRegistry, Is.True);
        }

        [Test]
        public void Coordinator_InternalSealedNotDisposableMonoBehaviourScriptableObject_NoStaticState()
        {
            Type type = GetPipelineCoordinatorType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        // ---- Real render texture integration ----

        [Test]
        public void GpuIntegration_DrawCopyReadbackCollectMatchesColor_DraftPending()
        {
            CaptureFrameProfile frameProfile = MakeFrameProfile();

            CaptureFrameRequestQueue requestQueue = null;
            CaptureFrameRenderTargetPool pool = null;
            CaptureFrameReadbackBufferPool bufferPool = null;
            UnityRenderTextureReadbackDispatcher dispatcher = null;
            CaptureFrameRenderTargetLeaseRegistry leaseRegistry = null;
            TraceLogger logger = null;
            CaptureFrameRenderTargetCopyPump copyPump = null;
            CaptureFrameRenderTargetReadbackPump readbackPump = null;
            RenderTexture source = null;

            CaptureFrameRequest request = default;
            CaptureFrameRenderTargetLease lease = default;
            bool leaseHeld = false;
            bool registered = false;

            ExceptionDispatchInfo body = null;
            Exception[] errors = null;

            try
            {
                object run = MakeRun(captureProfileId: 1);
                object registry = CreateRegistry(run, MakeTraceProfile(captureProfileId: 1));
                CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
                object factory = CreateFactory(run, sequence);

                requestQueue = new CaptureFrameRequestQueue(1);
                pool = new CaptureFrameRenderTargetPool(1, frameProfile);
                bufferPool = new CaptureFrameReadbackBufferPool(1, 16);
                dispatcher = new UnityRenderTextureReadbackDispatcher(bufferPool);
                leaseRegistry = new CaptureFrameRenderTargetLeaseRegistry(1, pool);
                copyPump = new CaptureFrameRenderTargetCopyPump(requestQueue, leaseRegistry, pool);
                readbackPump = new CaptureFrameRenderTargetReadbackPump(requestQueue, dispatcher, leaseRegistry, pool);

                logger = new TraceLogger(8);
                CaptureFrameTraceObserver observer = new CaptureFrameTraceObserver(logger);
                object admissionCoordinator = CreateAdmissionCoordinator(factory, registry, observer);
                object scheduler = CreateScheduler(registry, requestQueue, leaseRegistry, observer);
                object submissionCoordinator = CreateSubmissionCoordinator(admissionCoordinator, scheduler);
                object cadencedCoordinator = CreateCadencedCoordinator(new CaptureFrameCadenceSelector(45.0), submissionCoordinator);
                object coordinator = CreatePipelineCoordinator(cadencedCoordinator, copyPump, readbackPump, requestQueue, leaseRegistry);

                bool rented = pool.TryRent(out lease);
                if (rented)
                {
                    leaseHeld = true;
                }

                Assert.That(rented, Is.True);

                Color32 expectedColor = new Color32(41, 128, 185, 255);
                source = new RenderTexture(2, 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                source.Create();
                FillSolidColor(source, expectedColor);

                PipelineResult result = InvokePipelineSubmit(coordinator, MakeTimingAt(0.0), source, lease);

                // Track ownership immediately from the lease registry's actual state.
                request = result.Draft != null ? (CaptureFrameRequest)GetProperty(result.Draft, "Request") : default;
                if (result.Draft != null && leaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease registeredLease))
                {
                    registered = true;
                    if (LeasesIdentical(registeredLease, lease))
                    {
                        leaseHeld = false;
                    }
                }

                Assert.That(result.Exception, Is.Null);
                Assert.That(result.Status, Is.EqualTo(3)); // Scheduled
                Assert.That(result.CopyCompleted, Is.True);
                Assert.That(result.ReadbackStarted, Is.True);
                Assert.That(result.Draft, Is.Not.Null);

                Assert.That(leaseRegistry.TryGet(request, out CaptureFrameRenderTargetLease scheduledLease), Is.True);
                Assert.That(LeasesIdentical(scheduledLease, lease), Is.True);

                AsyncGPUReadback.WaitAllRequests();

                Assert.That(dispatcher.TryCollect(out CaptureFrameReadbackResult readbackResult), Is.True);
                NativeArray<byte> data = bufferPool.GetBuffer(readbackResult.BufferSlotIndex);
                for (int i = 0; i < 16; i += 4)
                {
                    Assert.That(data[i], Is.EqualTo(expectedColor.r));
                    Assert.That(data[i + 1], Is.EqualTo(expectedColor.g));
                    Assert.That(data[i + 2], Is.EqualTo(expectedColor.b));
                    Assert.That(data[i + 3], Is.EqualTo(expectedColor.a));
                }

                dispatcher.Release(readbackResult);

                Assert.That(leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removed), Is.True);
                registered = false;
                lease = removed;
                leaseHeld = true;

                pool.Return(lease);
                leaseHeld = false;

                Assert.That(requestQueue.Count, Is.EqualTo(0));
                Assert.That(dispatcher.ActiveCount, Is.EqualTo(0));
                Assert.That(leaseRegistry.Count, Is.EqualTo(0));
                Assert.That(pool.RentedCount, Is.EqualTo(0));
                Assert.That(Count(registry, "EntryCount"), Is.EqualTo(1));
                Assert.That(Count(registry, "PendingCount"), Is.EqualTo(1));

                object registeredDraft;
                object status;
                Assert.That(RegistryTryGet(registry, request, out registeredDraft, out status), Is.True);
                Assert.That(ReferenceEquals(registeredDraft, result.Draft), Is.True);
                Assert.That((int)status, Is.EqualTo(0)); // still Pending
            }
            catch (Exception ex)
            {
                body = ExceptionDispatchInfo.Capture(ex);
            }

            bool gpuSafe = true;

            try
            {
                AsyncGPUReadback.WaitAllRequests();
                if (dispatcher != null && dispatcher.IsCreated)
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
                if (registered && leaseRegistry != null)
                {
                    registered = false;
                    try
                    {
                        if (leaseRegistry.TryRemove(request, out CaptureFrameRenderTargetLease removed))
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

                if (leaseHeld && pool != null)
                {
                    leaseHeld = false;
                    try { pool.Return(lease); } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }

                if (source != null)
                {
                    try { DestroyTexture(source); source = null; } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
                }
            }

            try { if (dispatcher != null && dispatcher.IsCreated) { dispatcher.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (bufferPool != null && bufferPool.IsCreated) { bufferPool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (pool != null && pool.IsCreated) { pool.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }
            try { if (logger != null && logger.IsCreated) { logger.Dispose(); } } catch (Exception ex) { errors = AppendCleanupException(errors, ex); }

            ThrowCleanupAndBody(body, errors);
        }
    }
}
