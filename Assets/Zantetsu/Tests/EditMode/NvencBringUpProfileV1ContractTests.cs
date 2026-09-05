using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC bring-up profile and its
    /// immutable capability and input-layout snapshots. No real OS, GPU, or
    /// NVENC is used.
    /// </summary>
    public class NvencBringUpProfileV1ContractTests
    {
        [Test]
        public void Profile_FixedValuesMatchDesign()
        {
            NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);

            Assert.That(NvencBringUpProfileV1.Width, Is.EqualTo(1280));
            Assert.That(NvencBringUpProfileV1.Height, Is.EqualTo(720));
            Assert.That(NvencBringUpProfileV1.TargetFramesPerSecond, Is.EqualTo(30));
            Assert.That(NvencBringUpProfileV1.CadenceTickCount, Is.EqualTo(120));
            Assert.That(NvencBringUpProfileV1.SubmissionWindowDeadlineMs, Is.EqualTo(4000));
            Assert.That(NvencBringUpProfileV1.FinalizationDeadlineMs, Is.EqualTo(30000));
            Assert.That(NvencBringUpProfileV1.RecoveryFinalizationDeadlineMs, Is.EqualTo(10000));

            Assert.That(profile.ImageRect.X, Is.EqualTo(0));
            Assert.That(profile.ImageRect.Y, Is.EqualTo(0));
            Assert.That(profile.ImageRect.Width, Is.EqualTo(1280));
            Assert.That(profile.ImageRect.Height, Is.EqualTo(720));
            Assert.That(profile.Source, Is.EqualTo(CaptureSource.UnityRenderTexture));
            Assert.That(profile.Eye, Is.EqualTo(CaptureEye.Left));
            Assert.That(profile.PixelFormat, Is.EqualTo(CapturePixelFormat.Rgba32));
            Assert.That(profile.GraphicsFormat, Is.EqualTo(GraphicsFormat.R8G8B8A8_SRGB));
            Assert.That(profile.ColorSpace, Is.EqualTo(CaptureColorSpace.Srgb));
            Assert.That(profile.Orientation, Is.EqualTo(NvencInputOrientation.TopLeft));
            Assert.That(profile.ColorConversion, Is.EqualTo(NvencColorConversion.Bt709LimitedRangeNv12));
            Assert.That(profile.SampleCount, Is.EqualTo(1));
            Assert.That(profile.HasMipmaps, Is.False);
            Assert.That(profile.HasDynamicResolution, Is.False);
            Assert.That(profile.IsTextureArray, Is.False);
            Assert.That(profile.ArrayIndex, Is.EqualTo(0));
            Assert.That(profile.MipLevel, Is.EqualTo(0));
        }

        [Test]
        public void Profile_AllCapacitiesAreEight()
        {
            Assert.That(NvencBringUpProfileV1.WorkSlotCount, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.EncodeSampleSlotCount, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.SourceSurfaceLeaseCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.GpuConversionSyncCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.SubmissionQueueCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.SubmitToOutputQueueCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.FrameCompletionQueueCapacity, Is.EqualTo(8));
        }

        [Test]
        public void Profile_MaxAccessUnitByteLength_IsSixteenMiB()
        {
            Assert.That(NvencBringUpProfileV1.MaxAccessUnitByteLength, Is.EqualTo(16L * 1024L * 1024L));
        }

        [Test]
        public void Profile_ZeroProfileId_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpProfileV1(0));
        }

        [Test]
        public void Profile_NegativeProfileId_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpProfileV1(-1));
        }

        [Test]
        public void Profile_PositiveProfileId_Preserved()
        {
            NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);
            Assert.That(profile.ProfileId, Is.EqualTo(7));
        }

        [Test]
        public void Profile_Immutable_NoPublicMutableFields_NonDisposable_Sealed()
        {
            Type type = typeof(NvencBringUpProfileV1);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " must be readonly.");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(true), Is.Null, type.Name + "." + property.Name + " must not have a setter.");
            }
        }

        [Test]
        public void CapabilitySnapshot_IsReadonlyStruct_WithReadonlyFields()
        {
            Type type = typeof(NvencBringUpCapabilityV1);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " must be readonly.");
            }
        }

        [Test]
        public void InputLayoutSnapshot_IsReadonlyStruct_WithReadonlyFields()
        {
            Type type = typeof(NvencBringUpInputLayoutV1);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " must be readonly.");
            }
        }

        [Test]
        public void CapabilitySnapshot_DefaultIsUninitialized()
        {
            NvencBringUpCapabilityV1 capability = default;
            Assert.That(capability.IsInitialized, Is.False);
        }

        [Test]
        public void InputLayoutSnapshot_DefaultIsUninitialized()
        {
            NvencBringUpInputLayoutV1 input = default;
            Assert.That(input.IsInitialized, Is.False);
        }

        [Test]
        public void InputLayout_WidthHeightProductOverflow_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpInputLayoutV1(
                7,
                int.MaxValue,
                2,
                new CaptureImageRect(0, 0, int.MaxValue, 2),
                CaptureEye.Left,
                CapturePixelFormat.Rgba32,
                GraphicsFormat.R8G8B8A8_SRGB,
                CaptureColorSpace.Srgb,
                1,
                false,
                false,
                false,
                0,
                0,
                NvencInputOrientation.TopLeft));
        }

        [Test]
        public void InputLayout_ImageRectDimensionMismatch_Rejected()
        {
            Assert.Throws<ArgumentException>(() => new NvencBringUpInputLayoutV1(
                7,
                1280,
                720,
                new CaptureImageRect(0, 0, 640, 720),
                CaptureEye.Left,
                CapturePixelFormat.Rgba32,
                GraphicsFormat.R8G8B8A8_SRGB,
                CaptureColorSpace.Srgb,
                1,
                false,
                false,
                false,
                0,
                0,
                NvencInputOrientation.TopLeft));
        }

        [Test]
        public void InputLayout_NoneEnumValues_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpInputLayoutV1(
                7, 1280, 720, new CaptureImageRect(0, 0, 1280, 720),
                CaptureEye.None, CapturePixelFormat.Rgba32, GraphicsFormat.R8G8B8A8_SRGB,
                CaptureColorSpace.Srgb, 1, false, false, false, 0, 0, NvencInputOrientation.TopLeft));

            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpInputLayoutV1(
                7, 1280, 720, new CaptureImageRect(0, 0, 1280, 720),
                CaptureEye.Left, CapturePixelFormat.None, GraphicsFormat.R8G8B8A8_SRGB,
                CaptureColorSpace.Srgb, 1, false, false, false, 0, 0, NvencInputOrientation.TopLeft));

            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpInputLayoutV1(
                7, 1280, 720, new CaptureImageRect(0, 0, 1280, 720),
                CaptureEye.Left, CapturePixelFormat.Rgba32, GraphicsFormat.R8G8B8A8_SRGB,
                CaptureColorSpace.None, 1, false, false, false, 0, 0, NvencInputOrientation.TopLeft));

            Assert.Throws<ArgumentOutOfRangeException>(() => new NvencBringUpInputLayoutV1(
                7, 1280, 720, new CaptureImageRect(0, 0, 1280, 720),
                CaptureEye.Left, CapturePixelFormat.Rgba32, GraphicsFormat.R8G8B8A8_SRGB,
                CaptureColorSpace.Srgb, 1, false, false, false, 0, 0, NvencInputOrientation.None));
        }
    }
}
