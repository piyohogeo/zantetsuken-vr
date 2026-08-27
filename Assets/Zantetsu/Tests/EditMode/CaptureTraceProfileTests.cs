using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureTraceProfileTests
    {
        private static string ThrowsParamName(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return ex.ParamName;
            }
        }

        [Test]
        public void Properties_MatchInputs()
        {
            CaptureTraceProfile profile = new CaptureTraceProfile(7, 4096, 32, 10000);

            Assert.That(profile.CaptureProfileId, Is.EqualTo(7));
            Assert.That(profile.PostRollCapacity, Is.EqualTo(4096));
            Assert.That(profile.MaxInFlightDraftCount, Is.EqualTo(32));
            Assert.That(profile.MaxDraftCountPerRun, Is.EqualTo(10000));
        }

        [Test]
        public void Constructor_CaptureProfileId_ZeroAndNegative_Rejected()
        {
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(0, 4096, 32, 10000)), Is.EqualTo("captureProfileId"));
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(-1, 4096, 32, 10000)), Is.EqualTo("captureProfileId"));
        }

        [Test]
        public void Constructor_MaxInFlightDraftCount_ZeroAndNegative_Rejected()
        {
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 4096, 0, 10000)), Is.EqualTo("maxInFlightDraftCount"));
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 4096, -1, 10000)), Is.EqualTo("maxInFlightDraftCount"));
        }

        [Test]
        public void Constructor_MaxDraftCountPerRun_ZeroNegativeAndTooLarge_Rejected()
        {
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 4096, 1, 0)), Is.EqualTo("maxDraftCountPerRun"));
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 4096, 1, -1)), Is.EqualTo("maxDraftCountPerRun"));
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 4096, 1, 100001)), Is.EqualTo("maxDraftCountPerRun"));
        }

        [Test]
        public void Constructor_MaxInFlightExceedsMaxDraft_Rejected()
        {
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 4096, 5, 4)), Is.EqualTo("maxInFlightDraftCount"));
        }

        [Test]
        public void Constructor_TerminalReserveExceedsPostRoll_Rejected()
        {
            // Terminal reserve (maxInFlight + 1 = 33) exceeds postRollCapacity 32 by one.
            Assert.That(ThrowsParamName(() => new CaptureTraceProfile(1, 32, 32, 10000)), Is.EqualTo("postRollCapacity"));

            // Boundary: postRollCapacity 33 exactly fits the terminal reserve.
            CaptureTraceProfile accepted = new CaptureTraceProfile(1, 33, 32, 10000);
            Assert.That(accepted.PostRollCapacity, Is.EqualTo(33));
            Assert.That(accepted.MaxInFlightDraftCount, Is.EqualTo(32));
        }

        [Test]
        public void Constructor_MinimalValues_Accepted()
        {
            CaptureTraceProfile profile = new CaptureTraceProfile(1, 2, 1, 1);

            Assert.That(profile.CaptureProfileId, Is.EqualTo(1));
            Assert.That(profile.PostRollCapacity, Is.EqualTo(2));
            Assert.That(profile.MaxInFlightDraftCount, Is.EqualTo(1));
            Assert.That(profile.MaxDraftCountPerRun, Is.EqualTo(1));
        }

        [Test]
        public void Constructor_MaxDraftCountUpperBound_Accepted()
        {
            CaptureTraceProfile profile = new CaptureTraceProfile(1, 2, 1, 100000);

            Assert.That(profile.MaxDraftCountPerRun, Is.EqualTo(100000));
        }

        [Test]
        public void NoPublicSettersMutableCollectionsOrUnityObjects()
        {
            Type type = typeof(CaptureTraceProfile);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, type.Name + "." + property.Name + " must not have a public setter.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, type.Name + "." + field.Name + " must not be an array.");
                Assert.That(typeof(System.Collections.ICollection).IsAssignableFrom(field.FieldType), Is.False, type.Name + "." + field.Name + " must not be a mutable collection.");
                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False, type.Name + "." + field.Name + " must not be a Unity Object.");
            }
        }

        [Test]
        public void TypeShape_SealedNonIDisposableNonMonoBehaviourNonScriptableObject()
        {
            Assert.That(typeof(CaptureTraceProfile).IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureTraceProfile)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(CaptureTraceProfile)), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(typeof(CaptureTraceProfile)), Is.False);
        }
    }
}
