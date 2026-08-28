using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers for internal types ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetDraftType() => GetTypeFromAssembly("CaptureFrameDraft");

        private static Type GetRunType() => GetTypeFromAssembly("CaptureDraftRunContext");

        private static Type GetTraceContextType() => GetTypeFromAssembly("CaptureFrameDraftTraceContext");

        private static Type GetDraftStatusType() => GetTypeFromAssembly("CaptureFrameDraftStatus");

        private static Type GetEmissionStateType() => GetTypeFromAssembly("DraftDropTraceEmissionState");

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

        private static ConstructorInfo GetDraftCtor()
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
            return ctor;
        }

        private static object CreateRun(TraceRunContext context, long testCaseId, int captureProfileId)
        {
            return GetRunCtor().Invoke(new object[] { context, testCaseId, captureProfileId });
        }

        private static object CreateDraft(
            object run,
            CaptureFrameRequest request,
            CaptureFrameTiming timing,
            CapturePoseSample head,
            CapturePoseSample left,
            CapturePoseSample right,
            int commitPathId = 1)
        {
            return GetDraftCtor().Invoke(new object[] { run, request, timing, head, left, right, commitPathId });
        }

        private static Exception CtorException(
            object run,
            CaptureFrameRequest request,
            CaptureFrameTiming timing,
            CapturePoseSample head,
            CapturePoseSample left,
            CapturePoseSample right,
            int commitPathId = 1)
        {
            try
            {
                GetDraftCtor().Invoke(new object[] { run, request, timing, head, left, right, commitPathId });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
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

        private static bool HasIdenticalRequest(object draft, CaptureFrameRequest request)
        {
            MethodInfo method = GetDraftType().GetMethod(
                "HasIdenticalRequest",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(CaptureFrameRequest).MakeByRefType() },
                null);
            Assert.That(method, Is.Not.Null, "HasIdenticalRequest method not found.");
            return (bool)method.Invoke(draft, new object[] { request });
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

        private static CaptureFrameRequest MakeRequest(
            long captureFrameId = 10,
            long unityFrameId = 20,
            long openXRFrameId = 30,
            long testRunId = 1,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left,
            CaptureImageRect imageRect = default,
            int arrayIndex = 0)
        {
            if (!imageRect.IsValid)
            {
                imageRect = new CaptureImageRect(0, 0, 2, 2);
            }

            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1,
                unityFrameId,
                3,
                4,
                captureFrameId,
                openXRFrameId,
                testRunId,
                5,
                6,
                7,
                8u,
                9);

            return new CaptureFrameRequest(
                context,
                source,
                eye,
                imageRect,
                arrayIndex,
                CapturePixelFormat.Rgba32);
        }

        private static CaptureFrameTiming MakeTiming(bool shouldRender = true)
        {
            return new CaptureFrameTiming(1.0, 1.0 / 90.0, shouldRender, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        // ---- Construction / validation ----

        [Test]
        public void Constructor_RejectsNullRun_ParamNameRun()
        {
            Exception ex = CtorException(null, MakeRequest(), MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("run"));
        }

        [Test]
        public void Constructor_RejectsInvalidRequest_ParamNameRequest()
        {
            Exception ex = CtorException(MakeRun(), default, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
        }

        [Test]
        public void Constructor_RejectsInvalidTiming_ParamNameTiming()
        {
            Exception ex = CtorException(MakeRun(), MakeRequest(), default, MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("timing"));
        }

        [Test]
        public void Constructor_RejectsNonPositiveCommitPathId_ParamNameCommitPathId()
        {
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Exception zero = CtorException(MakeRun(), MakeRequest(), MakeTiming(), pose, pose, pose, 0);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("commitPathId"));

            Exception negative = CtorException(MakeRun(), MakeRequest(), MakeTiming(), pose, pose, pose, -1);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("commitPathId"));
        }

        [Test]
        public void Constructor_RejectsNonPositiveCaptureFrameId_ParamNameRequest()
        {
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Exception zero = CtorException(MakeRun(), MakeRequest(captureFrameId: 0), MakeTiming(), pose, pose, pose);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("request"));

            Exception negative = CtorException(MakeRun(), MakeRequest(captureFrameId: -1), MakeTiming(), pose, pose, pose);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("request"));
        }

        [Test]
        public void Constructor_RejectsNegativeUnityFrameId_ParamNameRequest()
        {
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Exception ex = CtorException(MakeRun(), MakeRequest(unityFrameId: -1), MakeTiming(), pose, pose, pose);
            Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("request"));
        }

        [Test]
        public void Constructor_AcceptsZeroOpenXRFrameId_RejectsNegative_ParamNameRequest()
        {
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            object zero = CreateDraft(MakeRun(), MakeRequest(openXRFrameId: 0), MakeTiming(), pose, pose, pose);
            Assert.That((long)GetProperty(zero, "OpenXRFrameId"), Is.EqualTo(0));

            Exception negative = CtorException(MakeRun(), MakeRequest(openXRFrameId: -1), MakeTiming(), pose, pose, pose);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("request"));
        }

        [Test]
        public void Constructor_RejectsNonPositiveTestRunId_ParamNameRequest()
        {
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Exception zero = CtorException(MakeRun(testRunId: 1), MakeRequest(testRunId: 0), MakeTiming(), pose, pose, pose);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("request"));

            Exception negative = CtorException(MakeRun(testRunId: 1), MakeRequest(testRunId: -1), MakeTiming(), pose, pose, pose);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("request"));
        }

        [Test]
        public void Constructor_RejectsTestRunIdMismatch_ParamNameRequest()
        {
            CapturePoseSample pose = MakePose(0f, 0f, 0f);

            Exception ex = CtorException(MakeRun(testRunId: 1), MakeRequest(testRunId: 2), MakeTiming(), pose, pose, pose);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("request"));
        }

        // ---- Held properties ----

        [Test]
        public void Constructor_HoldsAllEightProperties()
        {
            object run = MakeRun(testRunId: 1, testCaseId: 100, captureProfileId: 5);
            CaptureFrameRequest request = MakeRequest();
            CaptureFrameTiming timing = MakeTiming(shouldRender: false);
            CapturePoseSample head = MakePose(0f, 1f, 2f);
            CapturePoseSample left = MakePose(3f, 4f, 5f);
            CapturePoseSample right = MakePose(6f, 7f, 8f);

            object draft = CreateDraft(run, request, timing, head, left, right, 42);

            Assert.That(ReferenceEquals(GetProperty(draft, "Run"), run), Is.True);
            Assert.That(HasIdenticalRequest(draft, request), Is.True);
            Assert.That((int)GetProperty(draft, "CommitPathId"), Is.EqualTo(42));

            object heldTiming = GetProperty(draft, "Timing");
            Assert.That((bool)GetProperty(heldTiming, "ShouldRender"), Is.False);
            Assert.That((long)GetProperty(heldTiming, "DroppedFrameCount"), Is.EqualTo(7L));

            object heldHead = GetProperty(draft, "HeadPose");
            Assert.That((bool)GetProperty(heldHead, "IsAvailable"), Is.True);
            Assert.That((Vector3)GetProperty(heldHead, "Position"), Is.EqualTo(head.Position));

            object heldLeft = GetProperty(draft, "LeftControllerPose");
            Assert.That((Vector3)GetProperty(heldLeft, "Position"), Is.EqualTo(left.Position));

            object heldRight = GetProperty(draft, "RightControllerPose");
            Assert.That((Vector3)GetProperty(heldRight, "Position"), Is.EqualTo(right.Position));
        }

        [Test]
        public void TraceContext_MatchesRequestTwelveFields()
        {
            CaptureFrameRequest request = MakeRequest();
            object draft = CreateDraft(MakeRun(), request, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));

            object traceContext = GetProperty(draft, "TraceContext");

            Assert.That((long)GetField(traceContext, "Timestamp"), Is.EqualTo(request.TraceContext.Timestamp));
            Assert.That((long)GetField(traceContext, "UnityFrameId"), Is.EqualTo(request.TraceContext.UnityFrameId));
            Assert.That((long)GetField(traceContext, "FixedStepId"), Is.EqualTo(request.TraceContext.FixedStepId));
            Assert.That((int)GetField(traceContext, "ThreadId"), Is.EqualTo(request.TraceContext.ThreadId));
            Assert.That((long)GetField(traceContext, "CaptureFrameId"), Is.EqualTo(request.TraceContext.CaptureFrameId));
            Assert.That((long)GetField(traceContext, "OpenXRFrameId"), Is.EqualTo(request.TraceContext.OpenXRFrameId));
            Assert.That((long)GetField(traceContext, "TestRunId"), Is.EqualTo(request.TraceContext.TestRunId));
            Assert.That((long)GetField(traceContext, "SlashId"), Is.EqualTo(request.TraceContext.SlashId));
            Assert.That((long)GetField(traceContext, "FrontEdgeId"), Is.EqualTo(request.TraceContext.FrontEdgeId));
            Assert.That((long)GetField(traceContext, "ObjectId"), Is.EqualTo(request.TraceContext.ObjectId));
            Assert.That((uint)GetField(traceContext, "ObjectGeneration"), Is.EqualTo(request.TraceContext.ObjectGeneration));
            Assert.That((long)GetField(traceContext, "TaskId"), Is.EqualTo(request.TraceContext.TaskId));
        }

        [Test]
        public void ForwardingProperties_MatchRunAndRequest()
        {
            TraceRunContext source = MakeTraceRunContext(testRunId: 1, buildId: "draft-build", sceneId: "draft-scene", randomSeed: 777L);
            object richRun = CreateRun(source, 100, 5);

            CaptureFrameRequest request = MakeRequest(captureFrameId: 10, unityFrameId: 20, openXRFrameId: 30, testRunId: 1);
            object draft = CreateDraft(richRun, request, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));

            Assert.That((long)GetProperty(draft, "CaptureFrameId"), Is.EqualTo(10L));
            Assert.That((long)GetProperty(draft, "UnityFrameId"), Is.EqualTo(20L));
            Assert.That((long)GetProperty(draft, "OpenXRFrameId"), Is.EqualTo(30L));
            Assert.That((long)GetProperty(draft, "TestRunId"), Is.EqualTo(1L));
            Assert.That((long)GetProperty(draft, "TestCaseId"), Is.EqualTo(100L));
            Assert.That((string)GetProperty(draft, "BuildId"), Is.EqualTo("draft-build"));
            Assert.That((string)GetProperty(draft, "SceneId"), Is.EqualTo("draft-scene"));
            Assert.That((long)GetProperty(draft, "RandomSeed"), Is.EqualTo(777L));
            Assert.That((long)GetProperty(draft, "SlashId"), Is.EqualTo(5L));
            Assert.That((long)GetProperty(draft, "FrontEdgeId"), Is.EqualTo(6L));
            Assert.That((long)GetProperty(draft, "ObjectId"), Is.EqualTo(7L));
            Assert.That((uint)GetProperty(draft, "ObjectGeneration"), Is.EqualTo(8u));
            Assert.That((long)GetProperty(draft, "TaskId"), Is.EqualTo(9L));
            Assert.That((CaptureSource)GetProperty(draft, "Source"), Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That((CaptureEye)GetProperty(draft, "Eye"), Is.EqualTo(CaptureEye.Left));

            object imageRect = GetProperty(draft, "ImageRect");
            Assert.That((int)GetProperty(imageRect, "X"), Is.EqualTo(0));
            Assert.That((int)GetProperty(imageRect, "Y"), Is.EqualTo(0));
            Assert.That((int)GetProperty(imageRect, "Width"), Is.EqualTo(2));
            Assert.That((int)GetProperty(imageRect, "Height"), Is.EqualTo(2));

            Assert.That((int)GetProperty(draft, "ArrayIndex"), Is.EqualTo(0));
            Assert.That((int)GetProperty(draft, "CaptureProfileId"), Is.EqualTo(5));
        }

        [Test]
        public void UnavailablePoses_PreservedNotCompletedToIdentity()
        {
            CapturePoseSample unavailable = CapturePoseSample.Unavailable;
            object draft = CreateDraft(MakeRun(), MakeRequest(), MakeTiming(), unavailable, unavailable, unavailable);

            object head = GetProperty(draft, "HeadPose");
            object left = GetProperty(draft, "LeftControllerPose");
            object right = GetProperty(draft, "RightControllerPose");

            Assert.That((bool)GetProperty(head, "IsAvailable"), Is.False);
            Assert.That((bool)GetProperty(left, "IsAvailable"), Is.False);
            Assert.That((bool)GetProperty(right, "IsAvailable"), Is.False);

            Assert.That((Quaternion)GetProperty(head, "Rotation"), Is.Not.EqualTo(Quaternion.identity));
            Assert.That((Quaternion)GetProperty(left, "Rotation"), Is.Not.EqualTo(Quaternion.identity));
            Assert.That((Quaternion)GetProperty(right, "Rotation"), Is.Not.EqualTo(Quaternion.identity));
        }

        // ---- Request matching ----

        [Test]
        public void HasIdenticalRequest_AcceptsIdenticalRequest()
        {
            CaptureFrameRequest request = MakeRequest();
            object draft = CreateDraft(MakeRun(), request, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));

            Assert.That(HasIdenticalRequest(draft, request), Is.True);
        }

        [Test]
        public void HasIdenticalRequest_RejectsEachConstituentDifference()
        {
            CaptureFrameRequest request = MakeRequest();
            object draft = CreateDraft(MakeRun(), request, MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));

            Assert.That(HasIdenticalRequest(draft, MakeRequest(captureFrameId: 11)), Is.False);
            Assert.That(HasIdenticalRequest(draft, MakeRequest(source: CaptureSource.OpenXRProjection)), Is.False);
            Assert.That(HasIdenticalRequest(draft, MakeRequest(eye: CaptureEye.Right)), Is.False);
            Assert.That(HasIdenticalRequest(draft, MakeRequest(imageRect: new CaptureImageRect(1, 1, 2, 2))), Is.False);
            Assert.That(HasIdenticalRequest(draft, MakeRequest(arrayIndex: 1)), Is.False);
        }

        // ---- Type shape ----

        [Test]
        public void Type_IsInternalSealedClassWithNoPublicConstructorOrSetter()
        {
            Type type = GetDraftType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsValueType, Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must have no setter.");
            }
        }

        [Test]
        public void Type_HasNoManifestHashStatusDropPngLeaseFieldsOrProperties()
        {
            Type type = GetDraftType();

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string fullName = field.FieldType.FullName ?? field.FieldType.Name;

                Assert.That(field.FieldType, Is.Not.EqualTo(GetDraftStatusType()), field.Name + " must not hold a draft status.");
                Assert.That(field.FieldType, Is.Not.EqualTo(GetEmissionStateType()), field.Name + " must not hold an emission state.");
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureFrameDropReason)), field.Name + " must not hold a drop reason.");

                foreach (string fragment in new[] { "Manifest", "Reference", "Png", "Lease", "Readback", "Receipt", "FileStore", "Queue", "TraceLogger", "NativeArray", "RenderTexture" })
                {
                    Assert.That(fullName.IndexOf(fragment, StringComparison.Ordinal), Is.LessThan(0), field.Name + " must not hold " + fragment);
                }
            }

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                foreach (string fragment in new[] { "Manifest", "Sha256", "Hash", "Status", "Drop", "Png", "Lease", "Readback", "Receipt", "FileStore", "FilePath" })
                {
                    Assert.That(prop.Name.IndexOf(fragment, StringComparison.Ordinal), Is.LessThan(0), prop.Name + " must not contain " + fragment);
                }
            }
        }

        [Test]
        public void Type_HasNoDuplicateForwardingBackingFields()
        {
            Type type = GetDraftType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(8));

            string[] forwardedNames =
            {
                "CaptureFrameId", "UnityFrameId", "OpenXRFrameId", "TestRunId", "TestCaseId",
                "BuildId", "SceneId", "RandomSeed", "SlashId", "FrontEdgeId", "ObjectId",
                "ObjectGeneration", "TaskId", "Source", "Eye", "ImageRect", "ArrayIndex",
                "CaptureProfileId"
            };

            foreach (FieldInfo field in fields)
            {
                foreach (string name in forwardedNames)
                {
                    Assert.That(field.Name.IndexOf(name, StringComparison.Ordinal), Is.LessThan(0), field.Name + " must not duplicate " + name);
                }
            }
        }

        [Test]
        public void Type_IsNotDisposableMonoBehaviourOrScriptableObject()
        {
            Type type = GetDraftType();

            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Type_HasNoStaticMutableState()
        {
            Type type = GetDraftType();

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static), Is.Empty);
        }

        [Test]
        public void Constructor_DoesNotMutateInputRun()
        {
            TraceRunContext source = MakeTraceRunContext(testRunId: 1, buildId: "build-1", sceneId: "scene-1", randomSeed: 12345);
            object run = CreateRun(source, 100, 5);

            long testRunId = (long)GetProperty(run, "TestRunId");
            long testCaseId = (long)GetProperty(run, "TestCaseId");
            string buildId = (string)GetProperty(run, "BuildId");
            string sceneId = (string)GetProperty(run, "SceneId");
            long randomSeed = (long)GetProperty(run, "RandomSeed");
            int captureProfileId = (int)GetProperty(run, "CaptureProfileId");

            CreateDraft(run, MakeRequest(), MakeTiming(), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f), MakePose(0f, 0f, 0f));

            Assert.That((long)GetProperty(run, "TestRunId"), Is.EqualTo(testRunId));
            Assert.That((long)GetProperty(run, "TestCaseId"), Is.EqualTo(testCaseId));
            Assert.That((string)GetProperty(run, "BuildId"), Is.EqualTo(buildId));
            Assert.That((string)GetProperty(run, "SceneId"), Is.EqualTo(sceneId));
            Assert.That((long)GetProperty(run, "RandomSeed"), Is.EqualTo(randomSeed));
            Assert.That((int)GetProperty(run, "CaptureProfileId"), Is.EqualTo(captureProfileId));
        }
    }
}
