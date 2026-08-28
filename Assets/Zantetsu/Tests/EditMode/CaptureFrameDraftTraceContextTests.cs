using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameDraftTraceContextTests
    {
        // ---- Reflection helpers for internal types ----

        private static Type GetDraftType()
        {
            Type type = typeof(CaptureFrameTraceContext).Assembly.GetType("Zantetsu.Observability.CaptureFrameDraftTraceContext");
            Assert.That(type, Is.Not.Null, "CaptureFrameDraftTraceContext type not found.");
            return type;
        }

        private static Type GetDraftStatusType()
        {
            Type type = typeof(CaptureFrameTraceContext).Assembly.GetType("Zantetsu.Observability.CaptureFrameDraftStatus");
            Assert.That(type, Is.Not.Null, "CaptureFrameDraftStatus type not found.");
            return type;
        }

        private static Type GetEmissionStateType()
        {
            Type type = typeof(CaptureFrameTraceContext).Assembly.GetType("Zantetsu.Observability.DraftDropTraceEmissionState");
            Assert.That(type, Is.Not.Null, "DraftDropTraceEmissionState type not found.");
            return type;
        }

        private static ConstructorInfo GetDraftCtor()
        {
            ConstructorInfo ctor = GetDraftType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(CaptureFrameTraceContext).MakeByRefType() },
                null);
            Assert.That(ctor, Is.Not.Null, "Draft context constructor not found.");
            return ctor;
        }

        private static object CreateDraft(CaptureFrameTraceContext source)
        {
            return GetDraftCtor().Invoke(new object[] { source });
        }

        private static object GetField(object draft, string name)
        {
            FieldInfo field = GetDraftType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, name + " field not found.");
            return field.GetValue(draft);
        }

        private static bool IdenticalToDraft(object draft, object other)
        {
            MethodInfo method = GetDraftType().GetMethod(
                "IdenticalTo",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetDraftType().MakeByRefType() },
                null);
            Assert.That(method, Is.Not.Null, "IdenticalTo(CaptureFrameDraftTraceContext) not found.");
            return (bool)method.Invoke(draft, new object[] { other });
        }

        private static bool IdenticalToSource(object draft, CaptureFrameTraceContext source)
        {
            MethodInfo method = GetDraftType().GetMethod(
                "IdenticalTo",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(CaptureFrameTraceContext).MakeByRefType() },
                null);
            Assert.That(method, Is.Not.Null, "IdenticalTo(CaptureFrameTraceContext) not found.");
            return (bool)method.Invoke(draft, new object[] { source });
        }

        private static CaptureFrameTraceContext MakeSource()
        {
            return new CaptureFrameTraceContext(
                timestamp: 12345L,
                unityFrameId: 100L,
                fixedStepId: 200L,
                threadId: 3,
                captureFrameId: 55L,
                openXRFrameId: 77L,
                testRunId: 99L,
                slashId: 11L,
                frontEdgeId: 22L,
                objectId: 33L,
                objectGeneration: 44U,
                taskId: 66L);
        }

        private static CaptureFrameTraceContext With(
            CaptureFrameTraceContext s,
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
                timestamp ?? s.Timestamp,
                unityFrameId ?? s.UnityFrameId,
                fixedStepId ?? s.FixedStepId,
                threadId ?? s.ThreadId,
                captureFrameId ?? s.CaptureFrameId,
                openXRFrameId ?? s.OpenXRFrameId,
                testRunId ?? s.TestRunId,
                slashId ?? s.SlashId,
                frontEdgeId ?? s.FrontEdgeId,
                objectId ?? s.ObjectId,
                objectGeneration ?? s.ObjectGeneration,
                taskId ?? s.TaskId);
        }

        // ---- Enum contracts ----

        [Test]
        public void Enums_UnderlyingType_IsInt()
        {
            Assert.That(Enum.GetUnderlyingType(GetDraftStatusType()), Is.EqualTo(typeof(int)));
            Assert.That(Enum.GetUnderlyingType(GetEmissionStateType()), Is.EqualTo(typeof(int)));
        }

        [Test]
        public void Enums_AreInternal()
        {
            Assert.That(GetDraftStatusType().IsNotPublic, Is.True);
            Assert.That(GetEmissionStateType().IsNotPublic, Is.True);
        }

        [Test]
        public void CaptureFrameDraftStatus_NamesAndValues_MatchExactly()
        {
            Type type = GetDraftStatusType();

            Assert.That(Enum.GetName(type, 0), Is.EqualTo("Pending"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("Staged"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("Dropped"));
        }

        [Test]
        public void DraftDropTraceEmissionState_NamesAndValues_MatchExactly()
        {
            Type type = GetEmissionStateType();

            Assert.That(Enum.GetName(type, 0), Is.EqualTo("None"));
            Assert.That(Enum.GetName(type, 1), Is.EqualTo("Pending"));
            Assert.That(Enum.GetName(type, 2), Is.EqualTo("Attempted"));
        }

        [Test]
        public void CaptureFrameDraftStatus_HasNoAliasesGapsOrExtraValues()
        {
            Type type = GetDraftStatusType();

            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(3));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(3));

            for (int i = 0; i <= 2; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 3), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        [Test]
        public void DraftDropTraceEmissionState_HasNoAliasesGapsOrExtraValues()
        {
            Type type = GetEmissionStateType();

            Assert.That(Enum.GetNames(type).Length, Is.EqualTo(3));
            Assert.That(Enum.GetValues(type).Length, Is.EqualTo(3));

            for (int i = 0; i <= 2; i++)
            {
                Assert.That(Enum.GetName(type, i), Is.Not.Null, "Missing name for value " + i);
                Assert.That(Enum.IsDefined(type, i), Is.True, "Value " + i + " is not defined.");
            }

            Assert.That(Enum.IsDefined(type, 3), Is.False);
            Assert.That(Enum.IsDefined(type, -1), Is.False);
        }

        // ---- Copy contracts ----

        [Test]
        public void DraftContext_CopiesAllTwelveFieldsExactly()
        {
            CaptureFrameTraceContext source = MakeSource();
            object draft = CreateDraft(source);

            Assert.That((long)GetField(draft, "Timestamp"), Is.EqualTo(12345L));
            Assert.That((long)GetField(draft, "UnityFrameId"), Is.EqualTo(100L));
            Assert.That((long)GetField(draft, "FixedStepId"), Is.EqualTo(200L));
            Assert.That((int)GetField(draft, "ThreadId"), Is.EqualTo(3));
            Assert.That((long)GetField(draft, "CaptureFrameId"), Is.EqualTo(55L));
            Assert.That((long)GetField(draft, "OpenXRFrameId"), Is.EqualTo(77L));
            Assert.That((long)GetField(draft, "TestRunId"), Is.EqualTo(99L));
            Assert.That((long)GetField(draft, "SlashId"), Is.EqualTo(11L));
            Assert.That((long)GetField(draft, "FrontEdgeId"), Is.EqualTo(22L));
            Assert.That((long)GetField(draft, "ObjectId"), Is.EqualTo(33L));
            Assert.That((uint)GetField(draft, "ObjectGeneration"), Is.EqualTo(44U));
            Assert.That((long)GetField(draft, "TaskId"), Is.EqualTo(66L));
        }

        [Test]
        public void DraftContext_PreservesZeroNegativeAndUintMaxValues()
        {
            CaptureFrameTraceContext source = new CaptureFrameTraceContext(
                timestamp: 0L,
                unityFrameId: -1L,
                fixedStepId: 0L,
                threadId: -7,
                captureFrameId: -55L,
                openXRFrameId: 0L,
                testRunId: -99L,
                slashId: -11L,
                frontEdgeId: 0L,
                objectId: -33L,
                objectGeneration: uint.MaxValue,
                taskId: long.MinValue);

            object draft = CreateDraft(source);

            Assert.That((long)GetField(draft, "Timestamp"), Is.EqualTo(0L));
            Assert.That((long)GetField(draft, "UnityFrameId"), Is.EqualTo(-1L));
            Assert.That((long)GetField(draft, "FixedStepId"), Is.EqualTo(0L));
            Assert.That((int)GetField(draft, "ThreadId"), Is.EqualTo(-7));
            Assert.That((long)GetField(draft, "CaptureFrameId"), Is.EqualTo(-55L));
            Assert.That((long)GetField(draft, "OpenXRFrameId"), Is.EqualTo(0L));
            Assert.That((long)GetField(draft, "TestRunId"), Is.EqualTo(-99L));
            Assert.That((long)GetField(draft, "SlashId"), Is.EqualTo(-11L));
            Assert.That((long)GetField(draft, "FrontEdgeId"), Is.EqualTo(0L));
            Assert.That((long)GetField(draft, "ObjectId"), Is.EqualTo(-33L));
            Assert.That((uint)GetField(draft, "ObjectGeneration"), Is.EqualTo(uint.MaxValue));
            Assert.That((long)GetField(draft, "TaskId"), Is.EqualTo(long.MinValue));
        }

        [Test]
        public void DraftContext_DoesNotMutateSourceContext()
        {
            CaptureFrameTraceContext source = MakeSource();

            long timestamp = source.Timestamp;
            long unityFrameId = source.UnityFrameId;
            long fixedStepId = source.FixedStepId;
            int threadId = source.ThreadId;
            long captureFrameId = source.CaptureFrameId;
            long openXRFrameId = source.OpenXRFrameId;
            long testRunId = source.TestRunId;
            long slashId = source.SlashId;
            long frontEdgeId = source.FrontEdgeId;
            long objectId = source.ObjectId;
            uint objectGeneration = source.ObjectGeneration;
            long taskId = source.TaskId;

            CreateDraft(source);

            Assert.That(source.Timestamp, Is.EqualTo(timestamp));
            Assert.That(source.UnityFrameId, Is.EqualTo(unityFrameId));
            Assert.That(source.FixedStepId, Is.EqualTo(fixedStepId));
            Assert.That(source.ThreadId, Is.EqualTo(threadId));
            Assert.That(source.CaptureFrameId, Is.EqualTo(captureFrameId));
            Assert.That(source.OpenXRFrameId, Is.EqualTo(openXRFrameId));
            Assert.That(source.TestRunId, Is.EqualTo(testRunId));
            Assert.That(source.SlashId, Is.EqualTo(slashId));
            Assert.That(source.FrontEdgeId, Is.EqualTo(frontEdgeId));
            Assert.That(source.ObjectId, Is.EqualTo(objectId));
            Assert.That(source.ObjectGeneration, Is.EqualTo(objectGeneration));
            Assert.That(source.TaskId, Is.EqualTo(taskId));
        }

        // ---- Comparison contracts ----

        [Test]
        public void IdenticalToDraft_AcceptsIdenticalDrafts()
        {
            CaptureFrameTraceContext source = MakeSource();

            Assert.That(IdenticalToDraft(CreateDraft(source), CreateDraft(source)), Is.True);
        }

        [Test]
        public void IdenticalToDraft_RejectsEachSingleFieldDifference()
        {
            CaptureFrameTraceContext baseline = MakeSource();
            object draft = CreateDraft(baseline);

            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, timestamp: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, unityFrameId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, fixedStepId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, threadId: 999))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, captureFrameId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, openXRFrameId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, testRunId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, slashId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, frontEdgeId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, objectId: 99999L))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, objectGeneration: 999U))), Is.False);
            Assert.That(IdenticalToDraft(draft, CreateDraft(With(baseline, taskId: 99999L))), Is.False);
        }

        [Test]
        public void IdenticalToSource_MatchesDraftToSource()
        {
            CaptureFrameTraceContext source = MakeSource();
            object draft = CreateDraft(source);

            Assert.That(IdenticalToSource(draft, source), Is.True);
            Assert.That(IdenticalToSource(draft, With(source, timestamp: 99999L)), Is.False);
        }

        [Test]
        public void IdenticalToSource_RejectsEachSingleFieldDifference()
        {
            CaptureFrameTraceContext baseline = MakeSource();
            object draft = CreateDraft(baseline);

            Assert.That(IdenticalToSource(draft, With(baseline, timestamp: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, unityFrameId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, fixedStepId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, threadId: 999)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, captureFrameId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, openXRFrameId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, testRunId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, slashId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, frontEdgeId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, objectId: 99999L)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, objectGeneration: 999U)), Is.False);
            Assert.That(IdenticalToSource(draft, With(baseline, taskId: 99999L)), Is.False);
        }

        // ---- Type shape contracts ----

        [Test]
        public void Type_IsInternalReadonlyValueTypeWithExactlyTwelveValueTypeFields()
        {
            Type type = GetDraftType();

            Assert.That(type.IsNotPublic, Is.True);
            Assert.That(type.IsValueType, Is.True);
            Assert.That(type.IsPrimitive, Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(12));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Field " + field.Name + " must be a value type.");
                Assert.That(field.FieldType.IsArray, Is.False, "Field " + field.Name + " must not be an array.");
                Assert.That(field.IsInitOnly, Is.True, "Field " + field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Type_HasNoPublicSetter()
        {
            Type type = GetDraftType();

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must have no setter.");
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

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty);
        }
    }
}
