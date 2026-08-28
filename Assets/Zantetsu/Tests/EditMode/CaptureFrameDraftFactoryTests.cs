using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftFactoryTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers for internal types ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureFrameDraftFactory");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static ConstructorInfo GetRunCtor()
        {
            ConstructorInfo ctor = GetRunType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceRunContext), typeof(long), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");
            return ctor;
        }

        private static object CreateRun(TraceRunContext context, long testCaseId, int captureProfileId)
        {
            return GetRunCtor().Invoke(new object[] { context, testCaseId, captureProfileId });
        }

        private static ConstructorInfo GetFactoryCtor()
        {
            ConstructorInfo ctor = GetFactoryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    GetRunType(),
                    typeof(CaptureFrameIdSequence),
                    typeof(CaptureSource),
                    typeof(CaptureEye),
                    typeof(CaptureImageRect).MakeByRefType(),
                    typeof(int),
                    typeof(CapturePixelFormat)
                },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureFrameDraftFactory constructor not found.");
            return ctor;
        }

        private static MethodInfo GetCreateMethod()
        {
            MethodInfo method = GetFactoryType().GetMethod(
                "Create",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[]
                {
                    typeof(long), typeof(long), typeof(long), typeof(int), typeof(long),
                    typeof(long), typeof(long), typeof(long), typeof(uint), typeof(long),
                    typeof(CaptureFrameTiming).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(CapturePoseSample).MakeByRefType(),
                    typeof(int)
                },
                null);
            Assert.That(method, Is.Not.Null, "CaptureFrameDraftFactory.Create method not found.");
            return method;
        }

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static object GetField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, target.GetType().Name + "." + name + " field not found.");
            return field.GetValue(target);
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

        private static TraceRunContext MakeTraceRunContext(
            long testRunId = 1,
            string buildId = "build-1",
            string sceneId = "scene-1",
            long randomSeed = 12345)
        {
            return new TraceRunContext(
                testRunId,
                1000,
                buildId,
                "6000.3.22f1",
                ValidSha256,
                sceneId,
                randomSeed,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));
        }

        private static object MakeRun(long testRunId = 1, long testCaseId = 100, int captureProfileId = 5)
        {
            return CreateRun(MakeTraceRunContext(testRunId: testRunId), testCaseId, captureProfileId);
        }

        private static CaptureFrameIdSequence MakeSequenceAt(long lastIssued)
        {
            ConstructorInfo ctor = typeof(CaptureFrameIdSequence).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
            Assert.That(ctor, Is.Not.Null);
            return (CaptureFrameIdSequence)ctor.Invoke(new object[] { lastIssued });
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(0.5, 0.01, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static object CreateFactory(
            object run,
            CaptureFrameIdSequence sequence,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left,
            CaptureImageRect? imageRect = null,
            int arrayIndex = 0,
            CapturePixelFormat pixelFormat = CapturePixelFormat.Rgba32)
        {
            CaptureImageRect rect = imageRect ?? new CaptureImageRect(0, 0, 2, 2);
            return GetFactoryCtor().Invoke(new object[] { run, sequence, source, eye, rect, arrayIndex, pixelFormat });
        }

        private static object MakeFactory(object run = null, CaptureFrameIdSequence sequence = null)
        {
            return CreateFactory(run ?? MakeRun(), sequence ?? new CaptureFrameIdSequence());
        }

        private static object CreateDraft(
            object factory,
            long timestamp,
            long unityFrameId,
            long fixedStepId,
            int threadId,
            long openXRFrameId,
            long slashId,
            long frontEdgeId,
            long objectId,
            uint objectGeneration,
            long taskId,
            CaptureFrameTiming timing,
            CapturePoseSample head,
            CapturePoseSample left,
            CapturePoseSample right,
            int commitPathId)
        {
            return GetCreateMethod().Invoke(factory, new object[]
            {
                timestamp, unityFrameId, fixedStepId, threadId, openXRFrameId,
                slashId, frontEdgeId, objectId, objectGeneration, taskId,
                timing, head, left, right, commitPathId
            });
        }

        private static object CreateSimple(object factory, int commitPathId = 1)
        {
            return CreateDraft(factory, 1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), commitPathId);
        }

        private static Exception CreateException(
            object factory,
            CaptureFrameTiming timing,
            CapturePoseSample head,
            CapturePoseSample left,
            CapturePoseSample right,
            int commitPathId)
        {
            try
            {
                CreateDraft(factory, 1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                    timing, head, left, right, commitPathId);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static Exception FactoryCtorException(
            object run,
            CaptureFrameIdSequence sequence,
            CaptureSource source,
            CaptureEye eye,
            CaptureImageRect imageRect,
            int arrayIndex,
            CapturePixelFormat pixelFormat)
        {
            try
            {
                GetFactoryCtor().Invoke(new object[] { run, sequence, source, eye, imageRect, arrayIndex, pixelFormat });
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Constructor contracts ----

        [Test]
        public void Constructor_NullDependencies_Rejected()
        {
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            CaptureImageRect rect = new CaptureImageRect(0, 0, 2, 2);

            Exception nullRun = FactoryCtorException(null, sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32);
            Assert.That(nullRun, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullRun).ParamName, Is.EqualTo("run"));

            Exception nullSequence = FactoryCtorException(MakeRun(), null, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32);
            Assert.That(nullSequence, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullSequence).ParamName, Is.EqualTo("captureFrameIds"));
        }

        [Test]
        public void Constructor_InvalidFixedSettings_Rejected()
        {
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            CaptureImageRect rect = new CaptureImageRect(0, 0, 2, 2);

            // Source.
            Exception badSource = FactoryCtorException(MakeRun(), sequence, CaptureSource.None, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32);
            Assert.That(badSource, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)badSource).ParamName, Is.EqualTo("source"));

            // Eye.
            Exception badEye = FactoryCtorException(MakeRun(), sequence, CaptureSource.UnityRenderTexture, CaptureEye.None, rect, 0, CapturePixelFormat.Rgba32);
            Assert.That(badEye, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)badEye).ParamName, Is.EqualTo("eye"));

            // Image rectangle (default has zero width and height).
            Exception badRect = FactoryCtorException(MakeRun(), sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, default(CaptureImageRect), 0, CapturePixelFormat.Rgba32);
            Assert.That(badRect, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)badRect).ParamName, Is.EqualTo("imageRect"));

            // Array index.
            Exception badArrayIndex = FactoryCtorException(MakeRun(), sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, -1, CapturePixelFormat.Rgba32);
            Assert.That(badArrayIndex, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)badArrayIndex).ParamName, Is.EqualTo("arrayIndex"));

            // Pixel format.
            Exception badPixelFormat = FactoryCtorException(MakeRun(), sequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.None);
            Assert.That(badPixelFormat, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)badPixelFormat).ParamName, Is.EqualTo("format"));
        }

        [Test]
        public void Constructor_DoesNotConsumeSequence()
        {
            // Success path.
            CaptureFrameIdSequence successSequence = new CaptureFrameIdSequence();
            MakeFactory(sequence: successSequence);
            Assert.That(successSequence.LastIssued, Is.EqualTo(0));

            // Failure path (invalid image rect).
            CaptureFrameIdSequence failureSequence = new CaptureFrameIdSequence();
            Exception ex = FactoryCtorException(MakeRun(), failureSequence, CaptureSource.UnityRenderTexture, CaptureEye.Left, default(CaptureImageRect), 0, CapturePixelFormat.Rgba32);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(failureSequence.LastIssued, Is.EqualTo(0));
        }

        // ---- Create contracts ----

        [Test]
        public void Create_FirstIdsAreOneTwoThree()
        {
            object factory = MakeFactory();

            Assert.That((long)GetProperty(CreateSimple(factory), "CaptureFrameId"), Is.EqualTo(1L));
            Assert.That((long)GetProperty(CreateSimple(factory), "CaptureFrameId"), Is.EqualTo(2L));
            Assert.That((long)GetProperty(CreateSimple(factory), "CaptureFrameId"), Is.EqualTo(3L));
        }

        [Test]
        public void Factories_WithSeparateSequences_AreIndependent()
        {
            CaptureFrameIdSequence s1 = new CaptureFrameIdSequence();
            CaptureFrameIdSequence s2 = new CaptureFrameIdSequence();
            object f1 = MakeFactory(sequence: s1);
            object f2 = MakeFactory(sequence: s2);

            Assert.That((long)GetProperty(CreateSimple(f1), "CaptureFrameId"), Is.EqualTo(1L));
            Assert.That((long)GetProperty(CreateSimple(f2), "CaptureFrameId"), Is.EqualTo(1L));
            Assert.That((long)GetProperty(CreateSimple(f1), "CaptureFrameId"), Is.EqualTo(2L));

            Assert.That(s1.LastIssued, Is.EqualTo(2));
            Assert.That(s2.LastIssued, Is.EqualTo(1));
        }

        [Test]
        public void Create_TestRunId_FromRunContext()
        {
            object factory = MakeFactory(run: MakeRun(testRunId: 77));

            object draft = CreateSimple(factory);
            object request = GetProperty(draft, "Request");
            object traceContext = GetProperty(request, "TraceContext");

            Assert.That((long)GetProperty(draft, "TestRunId"), Is.EqualTo(77L));
            Assert.That((long)GetField(traceContext, "TestRunId"), Is.EqualTo(77L));
        }

        [Test]
        public void Create_TraceContext_AllFieldsTransferred()
        {
            object factory = MakeFactory(run: MakeRun(testRunId: 7));

            object draft = CreateDraft(factory, 111, 222, 333, 44, 555, 666, 777, 888, 99, 1000,
                MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);

            object traceContext = GetProperty(GetProperty(draft, "Request"), "TraceContext");

            Assert.That((long)GetField(traceContext, "Timestamp"), Is.EqualTo(111L));
            Assert.That((long)GetField(traceContext, "UnityFrameId"), Is.EqualTo(222L));
            Assert.That((long)GetField(traceContext, "FixedStepId"), Is.EqualTo(333L));
            Assert.That((int)GetField(traceContext, "ThreadId"), Is.EqualTo(44));
            Assert.That((long)GetField(traceContext, "CaptureFrameId"), Is.EqualTo(1L));
            Assert.That((long)GetField(traceContext, "OpenXRFrameId"), Is.EqualTo(555L));
            Assert.That((long)GetField(traceContext, "TestRunId"), Is.EqualTo(7L));
            Assert.That((long)GetField(traceContext, "SlashId"), Is.EqualTo(666L));
            Assert.That((long)GetField(traceContext, "FrontEdgeId"), Is.EqualTo(777L));
            Assert.That((long)GetField(traceContext, "ObjectId"), Is.EqualTo(888L));
            Assert.That((uint)GetField(traceContext, "ObjectGeneration"), Is.EqualTo(99u));
            Assert.That((long)GetField(traceContext, "TaskId"), Is.EqualTo(1000L));
        }

        [Test]
        public void Create_FixedCaptureSettings_Transferred()
        {
            object factory = CreateFactory(
                MakeRun(),
                new CaptureFrameIdSequence(),
                CaptureSource.OpenXRProjection,
                CaptureEye.Right,
                new CaptureImageRect(1, 2, 3, 4),
                7,
                CapturePixelFormat.Rgba32);

            object request = GetProperty(CreateSimple(factory), "Request");

            Assert.That((CaptureSource)GetProperty(request, "Source"), Is.EqualTo(CaptureSource.OpenXRProjection));
            Assert.That((CaptureEye)GetProperty(request, "Eye"), Is.EqualTo(CaptureEye.Right));

            object imageRect = GetProperty(request, "ImageRect");
            Assert.That((int)GetProperty(imageRect, "X"), Is.EqualTo(1));
            Assert.That((int)GetProperty(imageRect, "Y"), Is.EqualTo(2));
            Assert.That((int)GetProperty(imageRect, "Width"), Is.EqualTo(3));
            Assert.That((int)GetProperty(imageRect, "Height"), Is.EqualTo(4));

            Assert.That((int)GetProperty(request, "ArrayIndex"), Is.EqualTo(7));

            object pixelLayout = GetProperty(request, "PixelLayout");
            Assert.That((CapturePixelFormat)GetProperty(pixelLayout, "Format"), Is.EqualTo(CapturePixelFormat.Rgba32));
            Assert.That((int)GetProperty(pixelLayout, "Width"), Is.EqualTo(3));
            Assert.That((int)GetProperty(pixelLayout, "Height"), Is.EqualTo(4));
            Assert.That((int)GetProperty(pixelLayout, "BytesPerPixel"), Is.EqualTo(4));
            Assert.That((int)GetProperty(request, "RequiredByteCount"), Is.EqualTo(3 * 4 * 4));
        }

        [Test]
        public void Create_TimingPosesCommitPathAndRun_Preserved()
        {
            object run = MakeRun();
            object factory = MakeFactory(run: run);

            CaptureFrameTiming timing = MakeTiming();
            CapturePoseSample head = MakePose(1f, 2f, 3f);
            CapturePoseSample left = MakePose(4f, 5f, 6f);
            CapturePoseSample right = MakePose(7f, 8f, 9f);

            object draft = CreateDraft(factory, 1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                timing, head, left, right, 42);

            Assert.That(ReferenceEquals(GetProperty(draft, "Run"), run), Is.True);

            object heldTiming = GetProperty(draft, "Timing");
            Assert.That((double)GetProperty(heldTiming, "PredictedDisplayTimeSeconds"), Is.EqualTo(timing.PredictedDisplayTimeSeconds));
            Assert.That((double)GetProperty(heldTiming, "PredictedDisplayPeriodSeconds"), Is.EqualTo(timing.PredictedDisplayPeriodSeconds));
            Assert.That((bool)GetProperty(heldTiming, "ShouldRender"), Is.EqualTo(timing.ShouldRender));
            Assert.That((double)GetProperty(heldTiming, "AppGpuTimeMilliseconds"), Is.EqualTo(timing.AppGpuTimeMilliseconds));
            Assert.That((double)GetProperty(heldTiming, "CompositorGpuTimeMilliseconds"), Is.EqualTo(timing.CompositorGpuTimeMilliseconds));
            Assert.That((long)GetProperty(heldTiming, "DroppedFrameCount"), Is.EqualTo(timing.DroppedFrameCount));

            object heldHead = GetProperty(draft, "HeadPose");
            Assert.That((bool)GetProperty(heldHead, "IsAvailable"), Is.True);
            Assert.That((Vector3)GetProperty(heldHead, "Position"), Is.EqualTo(new Vector3(1f, 2f, 3f)));

            object heldLeft = GetProperty(draft, "LeftControllerPose");
            Assert.That((bool)GetProperty(heldLeft, "IsAvailable"), Is.True);
            Assert.That((Vector3)GetProperty(heldLeft, "Position"), Is.EqualTo(new Vector3(4f, 5f, 6f)));

            object heldRight = GetProperty(draft, "RightControllerPose");
            Assert.That((bool)GetProperty(heldRight, "IsAvailable"), Is.True);
            Assert.That((Vector3)GetProperty(heldRight, "Position"), Is.EqualTo(new Vector3(7f, 8f, 9f)));

            Assert.That((int)GetProperty(draft, "CommitPathId"), Is.EqualTo(42));
        }

        [Test]
        public void Create_UnavailablePoses_NotCompletedToIdentity()
        {
            object factory = MakeFactory();

            object draft = CreateDraft(factory, 1000, 200, 300, 4, 500, 600, 700, 800, 9, 1000,
                MakeTiming(),
                CapturePoseSample.Unavailable,
                MakePose(4f, 5f, 6f),
                CapturePoseSample.Unavailable,
                1);

            Assert.That((bool)GetProperty(GetProperty(draft, "HeadPose"), "IsAvailable"), Is.False);
            Assert.That((bool)GetProperty(GetProperty(draft, "RightControllerPose"), "IsAvailable"), Is.False);
            Assert.That((bool)GetProperty(GetProperty(draft, "LeftControllerPose"), "IsAvailable"), Is.True);
            Assert.That((Vector3)GetProperty(GetProperty(draft, "LeftControllerPose"), "Position"), Is.EqualTo(new Vector3(4f, 5f, 6f)));
        }

        [Test]
        public void Create_InvalidDraftInput_ConsumesIssuedId()
        {
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object factory = MakeFactory(sequence: sequence);

            object first = CreateSimple(factory);
            Assert.That((long)GetProperty(first, "CaptureFrameId"), Is.EqualTo(1L));
            Assert.That(sequence.LastIssued, Is.EqualTo(1));

            // The draft constructor rejects an invalid timing. The ID issued
            // before the failure must not be reused.
            Exception ex = CreateException(factory, default(CaptureFrameTiming), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(sequence.LastIssued, Is.EqualTo(2));

            object third = CreateSimple(factory);
            Assert.That((long)GetProperty(third, "CaptureFrameId"), Is.EqualTo(3L));
        }

        [Test]
        public void Sequence_Exhausted_OverflowException()
        {
            CaptureFrameIdSequence sequence = MakeSequenceAt(long.MaxValue - 1);
            object factory = MakeFactory(sequence: sequence);

            object last = CreateSimple(factory);
            Assert.That((long)GetProperty(last, "CaptureFrameId"), Is.EqualTo(long.MaxValue));

            Exception ex = CreateException(factory, MakeTiming(), MakePose(1f, 2f, 3f), MakePose(4f, 5f, 6f), MakePose(7f, 8f, 9f), 1);
            Assert.That(ex, Is.TypeOf<OverflowException>());
        }

        // ---- Ownership and type shape ----

        [Test]
        public void Factory_DoesNotDisposeOrMutateDependencies()
        {
            CaptureFrameIdSequence sequence = new CaptureFrameIdSequence();
            object run = MakeRun();
            object factory = MakeFactory(run: run, sequence: sequence);

            object draft = CreateSimple(factory);

            // The run context is returned unchanged by reference.
            Assert.That(ReferenceEquals(GetProperty(draft, "Run"), run), Is.True);

            // The sequence is neither reset nor disposed: it advanced by exactly
            // one issue and remains usable.
            Assert.That(sequence.LastIssued, Is.EqualTo(1));
            Assert.That(sequence.Next(), Is.EqualTo(2));

            // The factory retains no produced draft or request.
            foreach (FieldInfo field in GetFactoryType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(GetDraftType()), "Factory must not retain a produced draft.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRequest)), "Factory must not retain a produced request.");
            }
        }

        [Test]
        public void Factory_HasNoManifestHashRunReferenceOrRecordFields()
        {
            Type type = GetFactoryType();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string fullName = field.FieldType.FullName ?? field.FieldType.Name;

                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(TraceRunManifest)), field.Name + " must not hold a manifest.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureRunReference)), field.Name + " must not hold a run reference.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameRecord)), field.Name + " must not hold a record.");

                foreach (string fragment in new[] { "Manifest", "Sha256", "Hash", "Record" })
                {
                    Assert.That(fullName.IndexOf(fragment, StringComparison.Ordinal), Is.LessThan(0), field.Name + " must not hold " + fragment);
                }

                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False, field.Name + " must not hold a UnityEngine.Object.");
            }
        }

        [Test]
        public void Factory_HoldsOnlyExpectedFields()
        {
            Type type = GetFactoryType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(7));

            int enumCount = 0;
            int structCount = 0;
            int referenceCount = 0;

            foreach (FieldInfo field in fields)
            {
                if (field.FieldType.IsEnum) { enumCount++; }
                else if (field.FieldType.IsValueType) { structCount++; }
                else { referenceCount++; }
            }

            // Source, Eye, PixelFormat are enums; ImageRect and ArrayIndex are
            // value types; Run and Sequence are references.
            Assert.That(enumCount, Is.EqualTo(3));
            Assert.That(structCount, Is.EqualTo(2));
            Assert.That(referenceCount, Is.EqualTo(2));
        }

        [Test]
        public void Factory_IsInternalSealedWithNoPublicConstructor()
        {
            Type type = GetFactoryType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
        }

        [Test]
        public void Factory_NotIDisposableMonoBehaviourOrScriptableObject()
        {
            Type type = GetFactoryType();

            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Factory_HasNoStaticMutableState()
        {
            Type type = GetFactoryType();

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }
    }
}
