using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class PhaseZeroCaptureProfileSetTests
    {
        private static CaptureImageRect MakeRect(int x = 0, int y = 0, int width = 2, int height = 2)
        {
            return new CaptureImageRect(x, y, width, height);
        }

        [Test]
        public void CreateUnityLeftEye_TraceProfileValuesFixed()
        {
            PhaseZeroCaptureProfileSet set = PhaseZeroCaptureProfileSet.CreateUnityLeftEye(7, MakeRect());

            Assert.That(set.TraceProfile.CaptureProfileId, Is.EqualTo(7));
            Assert.That(set.TraceProfile.PostRollCapacity, Is.EqualTo(4096));
            Assert.That(set.TraceProfile.MaxInFlightDraftCount, Is.EqualTo(32));
            Assert.That(set.TraceProfile.MaxDraftCountPerRun, Is.EqualTo(10000));
        }

        [Test]
        public void CreateUnityLeftEye_ProfileIdsMatch()
        {
            PhaseZeroCaptureProfileSet set = PhaseZeroCaptureProfileSet.CreateUnityLeftEye(7, MakeRect());

            Assert.That(set.FrameProfile.ProfileId, Is.EqualTo(7));
            Assert.That(set.TraceProfile.CaptureProfileId, Is.EqualTo(7));
            Assert.That(set.FrameProfile.ProfileId, Is.EqualTo(set.TraceProfile.CaptureProfileId));
        }

        [Test]
        public void CreateUnityLeftEye_FrameProfileMatchesExistingFactory()
        {
            CaptureImageRect rect = MakeRect(1, 2, 3, 4);
            PhaseZeroCaptureProfileSet set = PhaseZeroCaptureProfileSet.CreateUnityLeftEye(7, rect);
            CaptureFrameProfile expected = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(7, rect);

            Assert.That(set.FrameProfile.ProfileId, Is.EqualTo(expected.ProfileId));
            Assert.That(set.FrameProfile.TargetFramesPerSecond, Is.EqualTo(expected.TargetFramesPerSecond));
            Assert.That(set.FrameProfile.MinimumIntervalSeconds, Is.EqualTo(expected.MinimumIntervalSeconds));
            Assert.That(set.FrameProfile.Source, Is.EqualTo(expected.Source));
            Assert.That(set.FrameProfile.Eye, Is.EqualTo(expected.Eye));
            Assert.That(set.FrameProfile.ImageRect.X, Is.EqualTo(expected.ImageRect.X));
            Assert.That(set.FrameProfile.ImageRect.Y, Is.EqualTo(expected.ImageRect.Y));
            Assert.That(set.FrameProfile.ImageRect.Width, Is.EqualTo(expected.ImageRect.Width));
            Assert.That(set.FrameProfile.ImageRect.Height, Is.EqualTo(expected.ImageRect.Height));
            Assert.That(set.FrameProfile.ArrayIndex, Is.EqualTo(expected.ArrayIndex));
            Assert.That(set.FrameProfile.PixelFormat, Is.EqualTo(expected.PixelFormat));
            Assert.That(set.FrameProfile.PixelLayout.Format, Is.EqualTo(expected.PixelLayout.Format));
            Assert.That(set.FrameProfile.PixelLayout.Width, Is.EqualTo(expected.PixelLayout.Width));
            Assert.That(set.FrameProfile.PixelLayout.Height, Is.EqualTo(expected.PixelLayout.Height));
        }

        [Test]
        public void CreateUnityLeftEye_ImageRectUnchanged()
        {
            CaptureImageRect rect = MakeRect(5, 6, 7, 8);
            PhaseZeroCaptureProfileSet set = PhaseZeroCaptureProfileSet.CreateUnityLeftEye(3, rect);

            Assert.That(set.FrameProfile.ImageRect.X, Is.EqualTo(5));
            Assert.That(set.FrameProfile.ImageRect.Y, Is.EqualTo(6));
            Assert.That(set.FrameProfile.ImageRect.Width, Is.EqualTo(7));
            Assert.That(set.FrameProfile.ImageRect.Height, Is.EqualTo(8));
        }

        [Test]
        public void ExistingCaptureFrameProfile_Unchanged()
        {
            CaptureImageRect rect = MakeRect();

            CaptureFrameProfile direct = new CaptureFrameProfile(7, 45.0, CaptureSource.UnityRenderTexture, CaptureEye.Left, rect, 0, CapturePixelFormat.Rgba32);
            Assert.That(direct.ProfileId, Is.EqualTo(7));
            Assert.That(direct.TargetFramesPerSecond, Is.EqualTo(45.0));

            CaptureFrameProfile factory = CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(3, rect);
            Assert.That(factory.ProfileId, Is.EqualTo(3));
            Assert.That(factory.TargetFramesPerSecond, Is.EqualTo(45.0));
            Assert.That(factory.Source, Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That(factory.Eye, Is.EqualTo(CaptureEye.Left));
            Assert.That(factory.ArrayIndex, Is.EqualTo(0));
            Assert.That(factory.PixelFormat, Is.EqualTo(CapturePixelFormat.Rgba32));
        }

        [Test]
        public void NoPublicConstructor()
        {
            ConstructorInfo[] constructors = typeof(PhaseZeroCaptureProfileSet).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

            Assert.That(constructors, Is.Empty, "PhaseZeroCaptureProfileSet must not expose a public constructor.");
        }

        [Test]
        public void NoPublicSettersMutableCollectionsOrUnityObjects()
        {
            Type type = typeof(PhaseZeroCaptureProfileSet);

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
            Assert.That(typeof(PhaseZeroCaptureProfileSet).IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(PhaseZeroCaptureProfileSet)), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(typeof(PhaseZeroCaptureProfileSet)), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(typeof(PhaseZeroCaptureProfileSet)), Is.False);
        }
    }
}
