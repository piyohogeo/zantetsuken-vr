using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftTerminalIntentTests
    {
        private const string KnownPngSha256 = "630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd";

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetStatusType() => GetTypeFromAssembly("TerminalIntentEnqueueStatus");

        private static Type GetIntentType() => GetTypeFromAssembly("CaptureFrameDraftTerminalIntent");

        private static Type GetEntryType() => GetTypeFromAssembly("CaptureFramePngStagingEntry");

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

        private static CaptureFrameRequest MakeRequest(long captureFrameId, long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1, 20, 3, 4, captureFrameId, 30, testRunId, 5, 6, 7, 8u, 9);
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameRequest MakeRequestWithContext(CaptureFrameTraceContext context)
        {
            return new CaptureFrameRequest(context, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTraceContext MutateContext(
            long? timestamp = null,
            long? unityFrameId = null,
            long? fixedStepId = null,
            int? threadId = null,
            long? captureFrameId = null,
            long? openXRFrameId = null,
            long? testRunId = null,
            long? slashId = null,
            long? frontEdgeId = null,
            long? objectId = null,
            uint? objectGeneration = null,
            long? taskId = null)
        {
            return new CaptureFrameTraceContext(
                timestamp ?? 1,
                unityFrameId ?? 20,
                fixedStepId ?? 3,
                threadId ?? 4,
                captureFrameId ?? 7,
                openXRFrameId ?? 30,
                testRunId ?? 1,
                slashId ?? 5,
                frontEdgeId ?? 6,
                objectId ?? 7,
                objectGeneration ?? 8u,
                taskId ?? 9);
        }

        private static ConstructorInfo GetEntryCtor()
        {
            ConstructorInfo ctor = GetEntryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(long), typeof(NativeArray<byte>), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFramePngStagingEntry constructor not found.");
            return ctor;
        }

        private static object MakeEntry(long captureFrameId, long testRunId, int pngLength)
        {
            ConstructorInfo ctor = GetEntryCtor();

            byte[] data = new byte[pngLength];
            for (int i = 0; i < pngLength; i++)
            {
                data[i] = (byte)i;
            }

            NativeArray<byte> png = new NativeArray<byte>(data, Allocator.Persistent);
            try
            {
                return ctor.Invoke(new object[] { testRunId, captureFrameId, png, KnownPngSha256 });
            }
            catch
            {
                if (png.IsCreated)
                {
                    png.Dispose();
                }

                throw;
            }
        }

        // ---- Intent invoke helpers ----

        private static object CreateStage(CaptureFrameRequest request, object entry)
        {
            MethodInfo method = GetIntentType().GetMethod("CreateStage", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "CreateStage method not found.");
            return method.Invoke(null, new object[] { request, entry });
        }

        private static Exception CreateStageException(CaptureFrameRequest request, object entry)
        {
            try
            {
                CreateStage(request, entry);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object CreateDrop(CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            MethodInfo method = GetIntentType().GetMethod("CreateDrop", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "CreateDrop method not found.");
            return method.Invoke(null, new object[] { request, reason });
        }

        private static Exception CreateDropException(CaptureFrameRequest request, CaptureFrameDropReason reason)
        {
            try
            {
                CreateDrop(request, reason);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static bool HasIdenticalRequest(object intent, CaptureFrameRequest request)
        {
            MethodInfo method = GetIntentType().GetMethod("HasIdenticalRequest", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "HasIdenticalRequest method not found.");
            return (bool)method.Invoke(intent, new object[] { request });
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

        private sealed class IntentScope
        {
            public readonly List<object> AllEntries = new List<object>();
        }

        private static object MakeEntryTracked(IntentScope scope, long captureFrameId, long testRunId, int pngLength)
        {
            object entry = MakeEntry(captureFrameId, testRunId, pngLength);
            try
            {
                scope.AllEntries.Add(entry);
            }
            catch
            {
                ((IDisposable)entry).Dispose();
                throw;
            }

            return entry;
        }

        private static Exception[] CleanupScope(IntentScope scope)
        {
            Exception[] errors = null;

            for (int i = scope.AllEntries.Count - 1; i >= 0; i--)
            {
                object entry = scope.AllEntries[i];
                scope.AllEntries.RemoveAt(i);
                try
                {
                    ((IDisposable)entry).Dispose();
                }
                catch (Exception ex)
                {
                    errors = AppendCleanupException(errors, ex);
                }
            }

            return errors;
        }

        private static void RunBody(IntentScope scope, Action body)
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

            Exception[] errors = CleanupScope(scope);
            ThrowCleanupAndBody(bodyException, errors);
        }

        // ---- Status enum contracts ----

        [Test]
        public void Status_UnderlyingTypeIsInt()
        {
            Assert.That(Enum.GetUnderlyingType(GetStatusType()), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void Status_NamesAndValues_MatchExactly()
        {
            Type type = GetStatusType();

            Assert.That(Enum.GetName(type, 0), Is.EqualTo("Accepted"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("Backpressured"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("DraftAlreadyTerminal"));
            Assert.That(Enum.GetName(type, 3), Is.EqualTo("IntentLimitExceeded"));
            Assert.That(Enum.GetName(type, 4), Is.EqualTo("RunNotAccepting"));
            Assert.That(Enum.GetName(type, 5), Is.EqualTo("InvalidIntent"));
        }

        [Test]
        public void Status_NoAliasesOrGaps()
        {
            Type type = GetStatusType();

            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(6));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(6));

            for (int i = 0; i <= 5; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 6), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Stage intent ----

        [Test]
        public void CreateStage_AllValuesAndDerivedProperties()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);
                CaptureFrameRequest request = MakeRequest(7);

                object intent = CreateStage(request, entry);

                Assert.That(HasIdenticalRequest(intent, request), Is.True);
                Assert.That(ReferenceEquals(GetProperty(intent, "StagingEntry"), entry), Is.True);
                Assert.That((int)GetProperty(intent, "DropReason"), Is.EqualTo(0)); // None
                Assert.That((bool)GetProperty(intent, "IsStage"), Is.True);
                Assert.That((bool)GetProperty(intent, "IsDrop"), Is.False);
                Assert.That((bool)GetProperty(intent, "HasPrivateBuffer"), Is.True);
                Assert.That((int)GetProperty(intent, "PrivateBufferByteCount"), Is.EqualTo(16));
            });
        }

        [Test]
        public void CreateStage_EntrySameReference()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);
                object intent = CreateStage(MakeRequest(7), entry);

                Assert.That(ReferenceEquals(GetProperty(intent, "StagingEntry"), entry), Is.True);
            });
        }

        [Test]
        public void CreateStage_InvalidOrDefaultRequest_Rejected()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);

                Exception ex = CreateStageException(default, entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
            });
        }

        [Test]
        public void CreateStage_NonPositiveIds_Rejected()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);

                foreach (long testRunId in new[] { 0L, -1L })
                {
                    CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, 7, 30, testRunId, 5, 6, 7, 8u, 9);
                    Exception ex = CreateStageException(MakeRequestWithContext(context), entry);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "testRunId " + testRunId);
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
                }

                foreach (long captureFrameId in new[] { 0L, -1L })
                {
                    CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, 1, 5, 6, 7, 8u, 9);
                    Exception ex = CreateStageException(MakeRequestWithContext(context), entry);
                    Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "captureFrameId " + captureFrameId);
                    Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
                }
            });
        }

        [Test]
        public void CreateStage_NullEntry_Rejected()
        {
            Exception ex = CreateStageException(MakeRequest(7), null);
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("stagingEntry"));
        }

        [Test]
        public void CreateStage_DisposedEntry_Rejected()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);
                ((IDisposable)entry).Dispose();

                Exception ex = CreateStageException(MakeRequest(7), entry);
                Assert.That(ex, Is.TypeOf<ObjectDisposedException>());
            });
        }

        [Test]
        public void CreateStage_TestRunIdMismatch_Rejected()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 2, 16); // different run
                Exception ex = CreateStageException(MakeRequest(7, testRunId: 1), entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingEntry"));
            });
        }

        [Test]
        public void CreateStage_CaptureFrameIdMismatch_Rejected()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 8, 1, 16); // different frame ID
                Exception ex = CreateStageException(MakeRequest(7), entry);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingEntry"));
            });
        }

        // ---- Drop intent ----

        [Test]
        public void CreateDrop_Reasons6To8_Accepted()
        {
            foreach (int reason in new[] { 6, 7, 8 })
            {
                object intent = CreateDrop(MakeRequest(7), (CaptureFrameDropReason)reason);

                Assert.That((int)GetProperty(intent, "DropReason"), Is.EqualTo(reason));
                Assert.That(GetProperty(intent, "StagingEntry"), Is.Null);
                Assert.That((bool)GetProperty(intent, "IsStage"), Is.False);
                Assert.That((bool)GetProperty(intent, "IsDrop"), Is.True);
                Assert.That((bool)GetProperty(intent, "HasPrivateBuffer"), Is.False);
                Assert.That((int)GetProperty(intent, "PrivateBufferByteCount"), Is.EqualTo(0));
            }
        }

        [Test]
        public void CreateDrop_InvalidReasons_Rejected()
        {
            foreach (int reason in new[] { 0, 1, 2, 3, 4, 5, 9, -1, 10, int.MaxValue })
            {
                Exception ex = CreateDropException(MakeRequest(7), (CaptureFrameDropReason)reason);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "reason " + reason);
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("dropReason"));
            }
        }

        [Test]
        public void CreateDrop_InvalidRequest_Rejected()
        {
            Exception ex = CreateDropException(default, CaptureFrameDropReason.PngEncodeFailed);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
        }

        [Test]
        public void CreateDrop_NonPositiveIds_Rejected()
        {
            foreach (long testRunId in new[] { 0L, -1L })
            {
                CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, 7, 30, testRunId, 5, 6, 7, 8u, 9);
                Exception ex = CreateDropException(MakeRequestWithContext(context), CaptureFrameDropReason.PngEncodeFailed);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "testRunId " + testRunId);
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
            }

            foreach (long captureFrameId in new[] { 0L, -1L })
            {
                CaptureFrameTraceContext context = new CaptureFrameTraceContext(1, 20, 3, 4, captureFrameId, 30, 1, 5, 6, 7, 8u, 9);
                Exception ex = CreateDropException(MakeRequestWithContext(context), CaptureFrameDropReason.PngEncodeFailed);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>(), "captureFrameId " + captureFrameId);
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
            }
        }

        // ---- Request matching ----

        [Test]
        public void HasIdenticalRequest_FullMatch_AndDifferencesRejected()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);
                CaptureFrameRequest request = MakeRequest(7);
                object intent = CreateStage(request, entry);

                Assert.That(HasIdenticalRequest(intent, request), Is.True);

                // Each of the 12 trace context fields, individually different.
                CaptureFrameTraceContext[] contextVariants = new[]
                {
                    MutateContext(timestamp: 99),
                    MutateContext(unityFrameId: 99),
                    MutateContext(fixedStepId: 99),
                    MutateContext(threadId: 99),
                    MutateContext(captureFrameId: 8),
                    MutateContext(openXRFrameId: 99),
                    MutateContext(testRunId: 2),
                    MutateContext(slashId: 99),
                    MutateContext(frontEdgeId: 99),
                    MutateContext(objectId: 99),
                    MutateContext(objectGeneration: 99u),
                    MutateContext(taskId: 99),
                };

                foreach (CaptureFrameTraceContext variant in contextVariants)
                {
                    Assert.That(HasIdenticalRequest(intent, MakeRequestWithContext(variant)), Is.False);
                }

                // Source difference.
                CaptureFrameRequest diffSource = new CaptureFrameRequest(request.TraceContext, CaptureSource.OpenXRProjection, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
                Assert.That(HasIdenticalRequest(intent, diffSource), Is.False);

                // Eye difference.
                CaptureFrameRequest diffEye = new CaptureFrameRequest(request.TraceContext, CaptureSource.UnityRenderTexture, CaptureEye.Right, new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
                Assert.That(HasIdenticalRequest(intent, diffEye), Is.False);

                // Image rect difference.
                CaptureFrameRequest diffRect = new CaptureFrameRequest(request.TraceContext, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 3, 2), 0, CapturePixelFormat.Rgba32);
                Assert.That(HasIdenticalRequest(intent, diffRect), Is.False);

                // Array index difference.
                CaptureFrameRequest diffArrayIndex = new CaptureFrameRequest(request.TraceContext, CaptureSource.UnityRenderTexture, CaptureEye.Left, new CaptureImageRect(0, 0, 2, 2), 1, CapturePixelFormat.Rgba32);
                Assert.That(HasIdenticalRequest(intent, diffArrayIndex), Is.False);
            });
        }

        // ---- Ownership / no side effects ----

        [Test]
        public void CreateStage_Failure_DoesNotDisposeEntry()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);

                Exception ex1 = CreateStageException(default, entry);
                Assert.That(ex1, Is.TypeOf<ArgumentException>());
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True);

                Exception ex2 = CreateStageException(MakeRequest(8), entry);
                Assert.That(ex2, Is.TypeOf<ArgumentException>());
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True);
            });
        }

        [Test]
        public void Intent_DoesNotOwnEntry_NotDisposable()
        {
            IntentScope scope = new IntentScope();
            RunBody(scope, () =>
            {
                object entry = MakeEntryTracked(scope, 7, 1, 16);
                object intent = CreateStage(MakeRequest(7), entry);

                // The intent never disposes the entry.
                Assert.That((bool)GetProperty(entry, "IsCreated"), Is.True);
                Assert.That(typeof(IDisposable).IsAssignableFrom(GetIntentType()), Is.False);
            });
        }

        // ---- Type shape ----

        [Test]
        public void Intent_InternalSealed_NoPublicCtorOrSetter_NotMonoBehaviourOrScriptableObject()
        {
            Type type = GetIntentType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be read-only.");
            }
        }

        [Test]
        public void Intent_HoldsOnlyRequestEntryAndReason()
        {
            Type type = GetIntentType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));

            bool hasRequest = false;
            bool hasEntry = false;
            bool hasReason = false;
            foreach (FieldInfo field in fields)
            {
                Type fieldType = field.FieldType;

                hasRequest |= fieldType == typeof(CaptureFrameRequest);
                hasEntry |= fieldType == GetEntryType();
                hasReason |= fieldType == typeof(CaptureFrameDropReason);

                // No direct logger, recorder, queue, registry, manifest, lease, or native array.
                Assert.That(typeof(TraceLogger).IsAssignableFrom(fieldType), Is.False);
                Assert.That(typeof(TraceFlightRecorder).IsAssignableFrom(fieldType), Is.False);
                Assert.That(typeof(CaptureFrameRequestQueue).IsAssignableFrom(fieldType), Is.False);
                Assert.That(typeof(CaptureFrameRenderTargetLeaseRegistry).IsAssignableFrom(fieldType), Is.False);
                bool isNativeArray = fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(NativeArray<>);
                Assert.That(isNativeArray, Is.False, "Intent must not hold a NativeArray.");
            }

            Assert.That(hasRequest, Is.True);
            Assert.That(hasEntry, Is.True);
            Assert.That(hasReason, Is.True);
        }
    }
}
