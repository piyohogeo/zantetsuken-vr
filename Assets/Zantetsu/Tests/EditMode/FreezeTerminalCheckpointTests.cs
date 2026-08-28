using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class FreezeTerminalCheckpointTests
    {
        private static Type GetCheckpointType()
        {
            Type type = typeof(TraceLogger).Assembly.GetType("Zantetsu.Observability.FreezeTerminalCheckpoint");
            Assert.That(type, Is.Not.Null, "FreezeTerminalCheckpoint type not found.");
            return type;
        }

        private static ConstructorInfo GetCtor()
        {
            ConstructorInfo ctor = GetCheckpointType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(long), typeof(long), typeof(int), typeof(long) },
                null);
            Assert.That(ctor, Is.Not.Null, "Checkpoint constructor not found.");
            return ctor;
        }

        private static object CreateCheckpoint(long timestamp, long frameId, long fixedStepId, int threadId, long testRunId)
        {
            return GetCtor().Invoke(new object[] { timestamp, frameId, fixedStepId, threadId, testRunId });
        }

        private static Exception CtorException(long timestamp, long frameId, long fixedStepId, int threadId, long testRunId)
        {
            try
            {
                GetCtor().Invoke(new object[] { timestamp, frameId, fixedStepId, threadId, testRunId });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static object DefaultCheckpoint()
        {
            return Activator.CreateInstance(GetCheckpointType());
        }

        private static object GetField(object checkpoint, string name)
        {
            FieldInfo field = GetCheckpointType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, name + " field not found.");
            return field.GetValue(checkpoint);
        }

        private static bool GetIsValid(object checkpoint)
        {
            PropertyInfo prop = GetCheckpointType().GetProperty("IsValid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, "IsValid property not found.");
            return (bool)prop.GetValue(checkpoint);
        }

        private static void SetField(object checkpoint, string name, object value)
        {
            FieldInfo field = GetCheckpointType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, name + " field not found.");
            field.SetValue(checkpoint, value);
        }

        private static bool IdenticalTo(object checkpoint, object other)
        {
            MethodInfo method = GetCheckpointType().GetMethod("IdenticalTo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "IdenticalTo method not found.");
            return (bool)method.Invoke(checkpoint, new object[] { other });
        }

        private static void AssertParamName(string expected, Exception exception)
        {
            Assert.That(exception, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)exception).ParamName, Is.EqualTo(expected));
        }

        [Test]
        public void Constructor_PreservesAllValues()
        {
            object checkpoint = CreateCheckpoint(123, 456, 789, 7, 42);

            Assert.That((long)GetField(checkpoint, "Timestamp"), Is.EqualTo(123));
            Assert.That((long)GetField(checkpoint, "FrameId"), Is.EqualTo(456));
            Assert.That((long)GetField(checkpoint, "FixedStepId"), Is.EqualTo(789));
            Assert.That((int)GetField(checkpoint, "ThreadId"), Is.EqualTo(7));
            Assert.That((long)GetField(checkpoint, "TestRunId"), Is.EqualTo(42));
        }

        [Test]
        public void Constructor_AcceptsZeroTimestampFrameFixedStep()
        {
            object checkpoint = CreateCheckpoint(0, 0, 0, 1, 1);

            Assert.That((long)GetField(checkpoint, "Timestamp"), Is.EqualTo(0));
            Assert.That((long)GetField(checkpoint, "FrameId"), Is.EqualTo(0));
            Assert.That((long)GetField(checkpoint, "FixedStepId"), Is.EqualTo(0));
            Assert.That((int)GetField(checkpoint, "ThreadId"), Is.EqualTo(1));
            Assert.That((long)GetField(checkpoint, "TestRunId"), Is.EqualTo(1));
        }

        [Test]
        public void Constructor_RejectsNegativeTimestamp_ExactParamName()
        {
            AssertParamName("timestamp", CtorException(-1, 0, 0, 1, 1));
        }

        [Test]
        public void Constructor_RejectsNegativeFrameId_ExactParamName()
        {
            AssertParamName("frameId", CtorException(0, -1, 0, 1, 1));
        }

        [Test]
        public void Constructor_RejectsNegativeFixedStepId_ExactParamName()
        {
            AssertParamName("fixedStepId", CtorException(0, 0, -1, 1, 1));
        }

        [Test]
        public void Constructor_RejectsNonPositiveThreadId_ExactParamName()
        {
            AssertParamName("threadId", CtorException(0, 0, 0, 0, 1));
            AssertParamName("threadId", CtorException(0, 0, 0, -1, 1));
        }

        [Test]
        public void Constructor_RejectsNonPositiveTestRunId_ExactParamName()
        {
            AssertParamName("testRunId", CtorException(0, 0, 0, 1, 0));
            AssertParamName("testRunId", CtorException(0, 0, 0, 1, -1));
        }

        [Test]
        public void Default_IsInvalid()
        {
            Assert.That(GetIsValid(DefaultCheckpoint()), Is.False);
        }

        [Test]
        public void MinimumValidValues_IsValid()
        {
            Assert.That(GetIsValid(CreateCheckpoint(0, 0, 0, 1, 1)), Is.True);
        }

        [Test]
        public void IsValid_RechecksAllInvariants_RejectsBypassedNegativeFields()
        {
            // Bypass the constructor: set valid thread/run IDs and then a single
            // negative field to confirm IsValid re-checks every invariant.
            Assert.That(IsValidWithNegativeField("Timestamp", -1L), Is.False);
            Assert.That(IsValidWithNegativeField("FrameId", -1L), Is.False);
            Assert.That(IsValidWithNegativeField("FixedStepId", -1L), Is.False);
        }

        private static bool IsValidWithNegativeField(string fieldName, long negativeValue)
        {
            object checkpoint = DefaultCheckpoint();
            SetField(checkpoint, "ThreadId", 1);
            SetField(checkpoint, "TestRunId", 1L);
            SetField(checkpoint, fieldName, negativeValue);
            return GetIsValid(checkpoint);
        }

        [Test]
        public void IdenticalTo_AcceptsIdenticalValues()
        {
            object a = CreateCheckpoint(1, 2, 3, 4, 5);
            object b = CreateCheckpoint(1, 2, 3, 4, 5);

            Assert.That(IdenticalTo(a, b), Is.True);
        }

        [Test]
        public void IdenticalTo_RejectsEachSingleFieldDifference()
        {
            object baseline = CreateCheckpoint(1, 2, 3, 4, 5);

            Assert.That(IdenticalTo(baseline, CreateCheckpoint(9, 2, 3, 4, 5)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateCheckpoint(1, 9, 3, 4, 5)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateCheckpoint(1, 2, 9, 4, 5)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateCheckpoint(1, 2, 3, 9, 5)), Is.False);
            Assert.That(IdenticalTo(baseline, CreateCheckpoint(1, 2, 3, 4, 9)), Is.False);
        }

        [Test]
        public void Type_IsReadonlyStructWithNoReferenceOrArrayFields()
        {
            Type type = GetCheckpointType();

            Assert.That(type.IsValueType, Is.True);
            Assert.That(type.IsPrimitive, Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Field " + field.Name + " must be a value type.");
                Assert.That(field.FieldType.IsArray, Is.False, "Field " + field.Name + " must not be an array.");
            }
        }

        [Test]
        public void Type_HasNoSeparateIsValidField_AndNoPublicSetter()
        {
            Type type = GetCheckpointType();

            Assert.That(type.GetField("IsValid", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Null, "IsValid must be a computed property, not a field.");

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must have no setter.");
            }
        }

        [Test]
        public void Type_IsNotDisposableMonoBehaviourOrScriptableObject()
        {
            Type type = GetCheckpointType();

            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Type_HasNoStaticMutableState()
        {
            Type type = GetCheckpointType();

            FieldInfo[] staticFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(staticFields, Is.Empty);
        }
    }
}
