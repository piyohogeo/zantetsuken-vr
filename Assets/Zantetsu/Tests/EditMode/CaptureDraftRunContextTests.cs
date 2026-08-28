using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureDraftRunContextTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Reflection helpers for the internal type ----

        private static Type GetDraftType()
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability.CaptureDraftRunContext");
            Assert.That(type, Is.Not.Null, "CaptureDraftRunContext type not found.");
            return type;
        }

        private static ConstructorInfo GetCtor()
        {
            ConstructorInfo ctor = GetDraftType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(TraceRunContext), typeof(long), typeof(int) },
                null);
            Assert.That(ctor, Is.Not.Null, "CaptureDraftRunContext constructor not found.");
            return ctor;
        }

        private static object CreateDraft(TraceRunContext context, long testCaseId, int captureProfileId)
        {
            return GetCtor().Invoke(new object[] { context, testCaseId, captureProfileId });
        }

        private static Exception CtorException(TraceRunContext context, long testCaseId, int captureProfileId)
        {
            try
            {
                GetCtor().Invoke(new object[] { context, testCaseId, captureProfileId });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static object GetProperty(object draft, string name)
        {
            PropertyInfo prop = GetDraftType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, name + " property not found.");
            return prop.GetValue(draft);
        }

        private static bool IdenticalTo(object draft, object other)
        {
            MethodInfo method = GetDraftType().GetMethod(
                "IdenticalTo",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetDraftType() },
                null);
            Assert.That(method, Is.Not.Null, "IdenticalTo method not found.");
            return (bool)method.Invoke(draft, new object[] { other });
        }

        private static TraceRunContext MakeContext(
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

        // ---- Construction / validation ----

        [Test]
        public void Constructor_RejectsNullContext_ParamNameTraceRunContext()
        {
            Exception ex = CtorException(null, 100, 5);
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("traceRunContext"));
        }

        [Test]
        public void Constructor_RejectsNonPositiveTestCaseId_ParamNameTestCaseId()
        {
            TraceRunContext context = MakeContext();

            Exception zero = CtorException(context, 0, 5);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("testCaseId"));

            Exception negative = CtorException(context, -1, 5);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("testCaseId"));
        }

        [Test]
        public void Constructor_RejectsNonPositiveCaptureProfileId_ParamNameCaptureProfileId()
        {
            TraceRunContext context = MakeContext();

            Exception zero = CtorException(context, 100, 0);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("captureProfileId"));

            Exception negative = CtorException(context, 100, -1);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("captureProfileId"));
        }

        [Test]
        public void Constructor_CopiesAllSixPropertiesExactly()
        {
            TraceRunContext context = MakeContext(testRunId: 7, buildId: "build-7", sceneId: "scene-7", randomSeed: 777L);
            object draft = CreateDraft(context, 200, 9);

            Assert.That((long)GetProperty(draft, "TestRunId"), Is.EqualTo(7L));
            Assert.That((long)GetProperty(draft, "TestCaseId"), Is.EqualTo(200L));
            Assert.That((string)GetProperty(draft, "BuildId"), Is.EqualTo("build-7"));
            Assert.That((string)GetProperty(draft, "SceneId"), Is.EqualTo("scene-7"));
            Assert.That((long)GetProperty(draft, "RandomSeed"), Is.EqualTo(777L));
            Assert.That((int)GetProperty(draft, "CaptureProfileId"), Is.EqualTo(9));
        }

        [Test]
        public void Constructor_DoesNotCopyOrNormalizeBuildIdAndSceneId()
        {
            string buildId = "Build-Mixed-Case-7";
            string sceneId = "Scene_With_Underscore";
            TraceRunContext context = MakeContext(buildId: buildId, sceneId: sceneId);
            object draft = CreateDraft(context, 200, 9);

            Assert.That(ReferenceEquals((string)GetProperty(draft, "BuildId"), buildId), Is.True);
            Assert.That(ReferenceEquals((string)GetProperty(draft, "SceneId"), sceneId), Is.True);
            Assert.That((string)GetProperty(draft, "BuildId"), Is.EqualTo(buildId));
            Assert.That((string)GetProperty(draft, "SceneId"), Is.EqualTo(sceneId));
        }

        [Test]
        public void Constructor_DoesNotMutateSourceContext()
        {
            TraceRunContext context = MakeContext(testRunId: 7, buildId: "build-7", sceneId: "scene-7", randomSeed: 777L);

            long testRunId = context.TestRunId;
            string buildId = context.BuildId;
            string sceneId = context.SceneId;
            long randomSeed = context.RandomSeed;

            CreateDraft(context, 200, 9);

            Assert.That(context.TestRunId, Is.EqualTo(testRunId));
            Assert.That(context.BuildId, Is.EqualTo(buildId));
            Assert.That(context.SceneId, Is.EqualTo(sceneId));
            Assert.That(context.RandomSeed, Is.EqualTo(randomSeed));
        }

        // ---- Comparison ----

        [Test]
        public void IdenticalTo_AcceptsEqualContexts()
        {
            TraceRunContext context = MakeContext();
            object a = CreateDraft(context, 200, 9);
            object b = CreateDraft(context, 200, 9);

            Assert.That(IdenticalTo(a, b), Is.True);
        }

        [Test]
        public void IdenticalTo_RejectsEachSingleFieldDifference()
        {
            object baseline = CreateDraft(MakeContext(), 200, 9);

            Assert.That(IdenticalTo(baseline, CreateDraft(MakeContext(testRunId: 2), 200, 9)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateDraft(MakeContext(), 201, 9)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateDraft(MakeContext(buildId: "build-2"), 200, 9)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateDraft(MakeContext(sceneId: "scene-2"), 200, 9)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateDraft(MakeContext(randomSeed: 99999L), 200, 9)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateDraft(MakeContext(), 200, 10)), Is.False);
        }

        [Test]
        public void IdenticalTo_NullOther_ReturnsFalse()
        {
            object draft = CreateDraft(MakeContext(), 200, 9);

            Assert.That(IdenticalTo(draft, null), Is.False);
        }

        // ---- Type shape ----

        [Test]
        public void Type_IsInternalSealedClassWithNoPublicConstructor()
        {
            Type type = GetDraftType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsValueType, Is.False);

            ConstructorInfo[] publicCtors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(publicCtors, Is.Empty);
        }

        [Test]
        public void Type_PropertiesHaveNoSetter()
        {
            Type type = GetDraftType();

            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(properties.Length, Is.EqualTo(6));

            foreach (PropertyInfo prop in properties)
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must have no setter.");
            }
        }

        [Test]
        public void Type_HoldsExactlySixBackingFields_NoManifestHashOrContextReference()
        {
            Type type = GetDraftType();

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(6));

            int longCount = 0;
            int intCount = 0;
            int stringCount = 0;

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsArray, Is.False, "Field " + field.Name + " must not be an array.");
                Assert.That(typeof(TraceRunManifest).IsAssignableFrom(field.FieldType), Is.False, "Field " + field.Name + " must not hold a manifest.");
                Assert.That(typeof(CaptureRunReference).IsAssignableFrom(field.FieldType), Is.False, "Field " + field.Name + " must not hold a run reference.");
                Assert.That(typeof(TraceRunContext).IsAssignableFrom(field.FieldType), Is.False, "Field " + field.Name + " must not hold a trace run context.");

                if (field.FieldType == typeof(long)) { longCount++; }
                else if (field.FieldType == typeof(int)) { intCount++; }
                else if (field.FieldType == typeof(string)) { stringCount++; }
                else
                {
                    Assert.Fail("Unexpected field type " + field.FieldType + " on " + field.Name + ".");
                }
            }

            Assert.That(longCount, Is.EqualTo(3));
            Assert.That(intCount, Is.EqualTo(1));
            Assert.That(stringCount, Is.EqualTo(2));
        }

        [Test]
        public void Type_HasNoManifestRelatedProperties()
        {
            Type type = GetDraftType();

            string[] propertyNames = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToArray();

            Assert.That(propertyNames, Does.Not.Contain("TraceRunManifest"));
            Assert.That(propertyNames, Does.Not.Contain("CaptureRunReference"));
            Assert.That(propertyNames, Does.Not.Contain("RunManifestContentSha256"));
            Assert.That(propertyNames, Does.Not.Contain("EventCount"));
            Assert.That(propertyNames, Does.Not.Contain("TriggerHistoryCount"));
            Assert.That(propertyNames, Does.Not.Contain("CapturedPostRollCount"));
            Assert.That(propertyNames, Does.Not.Contain("CaptureFrameId"));
            Assert.That(propertyNames, Does.Not.Contain("TraceRunContext"));
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

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty);
        }
    }
}
