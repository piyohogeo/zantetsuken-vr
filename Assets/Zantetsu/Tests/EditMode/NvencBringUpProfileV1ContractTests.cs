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

        /// <summary>
        /// The one fixed encoder request: H.264 High, all-IDR, constant QP 28,
        /// NV12 in and Annex B out, at the fixed size and rate. These are the
        /// values this bring-up asks for - the category strings are
        /// project-owned identifiers, not NVIDIA GUID names or ABI values, and
        /// none of this says what a session will accept.
        /// </summary>
        [Test]
        public void Profile_FixedEncoderRequestMatchesDesign()
        {
            NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);

            Assert.That(NvencBringUpProfileV1.CodecId, Is.EqualTo("h264"));
            Assert.That(NvencBringUpProfileV1.EncodeProfileId, Is.EqualTo("high"));
            Assert.That(NvencBringUpProfileV1.PresetId, Is.EqualTo("p1"));
            Assert.That(NvencBringUpProfileV1.TuningId, Is.EqualTo("low-latency"));
            Assert.That(NvencBringUpProfileV1.RateControlId, Is.EqualTo("constant-qp"));
            Assert.That(NvencBringUpProfileV1.InputFormatId, Is.EqualTo("nv12"));
            Assert.That(NvencBringUpProfileV1.ChromaFormatId, Is.EqualTo("4:2:0"));
            Assert.That(NvencBringUpProfileV1.LevelId, Is.EqualTo("auto"));
            Assert.That(NvencBringUpProfileV1.OutputFormatId, Is.EqualTo("annex-b"));

            // The rate the encoder is asked for is the profile's own frame
            // rate, expressed as a fraction.
            Assert.That(NvencBringUpProfileV1.FrameRateNumerator, Is.EqualTo(30));
            Assert.That(NvencBringUpProfileV1.FrameRateDenominator, Is.EqualTo(1));
            Assert.That(NvencBringUpProfileV1.FrameRateNumerator,
                Is.EqualTo(NvencBringUpProfileV1.TargetFramesPerSecond));

            // The encoded size and the largest size requested are the fixed
            // input size: nothing scales, crops, or changes resolution.
            Assert.That(profile.EncodeWidth, Is.EqualTo(1280));
            Assert.That(profile.EncodeHeight, Is.EqualTo(720));
            Assert.That(profile.EncodeWidth, Is.EqualTo(NvencBringUpProfileV1.Width));
            Assert.That(profile.EncodeHeight, Is.EqualTo(NvencBringUpProfileV1.Height));
            Assert.That(profile.MaximumEncodeWidth, Is.EqualTo(NvencBringUpProfileV1.Width));
            Assert.That(profile.MaximumEncodeHeight, Is.EqualTo(NvencBringUpProfileV1.Height));

            // All-IDR: the caller decides each picture type, every picture is
            // an IDR, and a GOP is one picture long.
            Assert.That(NvencBringUpProfileV1.EnablePictureTypeDecision, Is.False);
            Assert.That(NvencBringUpProfileV1.GopLength, Is.EqualTo(1));
            Assert.That(NvencBringUpProfileV1.IdrPeriod, Is.EqualTo(1));
            Assert.That(NvencBringUpProfileV1.FrameIntervalP, Is.EqualTo(1));
            Assert.That(NvencBringUpProfileV1.ForceIdrEveryFrame, Is.True);

            // One complete constant-QP request. The inter-picture values do
            // not ask for a P or B picture.
            Assert.That(NvencBringUpProfileV1.QpIntra, Is.EqualTo(28));
            Assert.That(NvencBringUpProfileV1.QpInterP, Is.EqualTo(28));
            Assert.That(NvencBringUpProfileV1.QpInterB, Is.EqualTo(28));

            // Parameter-set repetition is requested here and nowhere else.
            Assert.That(
                NvencBringUpProfileV1.RepeatSequenceAndPictureParameterSets, Is.True);
            Assert.That(NvencBringUpProfileV1.OutputAccessUnitDelimiter, Is.False);
            Assert.That(
                NvencBringUpProfileV1.DisableSequenceAndPictureParameterSets, Is.False);

            Assert.That(NvencBringUpProfileV1.ProgressiveEncoding, Is.True);
            Assert.That(NvencBringUpProfileV1.EnableEncodeAsync, Is.True);
            Assert.That(NvencBringUpProfileV1.EnableOutputInVideoMemory, Is.False);

            // Still the only constructor input.
            Assert.That(profile.ProfileId, Is.EqualTo(7));
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
