using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the stateless Phase 0.11 NVENC bring-up admission
    /// validator. All capability and input snapshots are fakes; no real OS,
    /// GPU, or NVENC is used.
    /// </summary>
    public class NvencBringUpAdmissionContractTests
    {
        private static NvencBringUpProfileV1 MakeProfile(int profileId = 7)
        {
            return new NvencBringUpProfileV1(profileId);
        }

        private static NvencBringUpCapabilityV1 MakeCapability(
            bool isWindows10OrNewer = true,
            bool isActiveAdapterNvidia = true,
            bool isCurrentGraphicsApiD3D11 = true,
            bool activeAdapterSupportsWddm = true,
            bool activeAdapterSupportsAsyncEncode = true,
            bool activeAdapterSupportsCompletionEvent = true,
            bool isActiveAdapterTcc = false,
            bool activeAdapterCanUseOutputInVidmemZero = true,
            bool activeAdapterSupportsH264Encode = true,
            bool activeAdapterSupportsH264HighProfile = true,
            bool activeAdapterSupportsNv12Input = true,
            int maximumEncodeWidth = 4096,
            int maximumEncodeHeight = 4096)
        {
            return new NvencBringUpCapabilityV1(
                isWindows10OrNewer,
                isActiveAdapterNvidia,
                isCurrentGraphicsApiD3D11,
                activeAdapterSupportsWddm,
                activeAdapterSupportsAsyncEncode,
                activeAdapterSupportsCompletionEvent,
                isActiveAdapterTcc,
                activeAdapterCanUseOutputInVidmemZero,
                activeAdapterSupportsH264Encode,
                activeAdapterSupportsH264HighProfile,
                activeAdapterSupportsNv12Input,
                maximumEncodeWidth,
                maximumEncodeHeight);
        }

        private static NvencBringUpInputLayoutV1 MakeInput(
            int profileId = 7,
            int width = NvencBringUpProfileV1.Width,
            int height = NvencBringUpProfileV1.Height,
            int imageRectX = 0,
            int imageRectY = 0,
            CaptureEye eye = CaptureEye.Left,
            CapturePixelFormat pixelFormat = CapturePixelFormat.Rgba32,
            GraphicsFormat graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB,
            CaptureColorSpace colorSpace = CaptureColorSpace.Srgb,
            int sampleCount = 1,
            bool hasMipmaps = false,
            bool hasDynamicResolution = false,
            bool isTextureArray = false,
            int arrayIndex = 0,
            int mipLevel = 0,
            NvencInputOrientation orientation = NvencInputOrientation.TopLeft)
        {
            return new NvencBringUpInputLayoutV1(
                profileId,
                width,
                height,
                new CaptureImageRect(imageRectX, imageRectY, width, height),
                eye,
                pixelFormat,
                graphicsFormat,
                colorSpace,
                sampleCount,
                hasMipmaps,
                hasDynamicResolution,
                isTextureArray,
                arrayIndex,
                mipLevel,
                orientation);
        }

        private static NvencBringUpAdmissionDecision Evaluate(
            NvencBringUpProfileV1 profile,
            NvencBringUpCapabilityV1 capability,
            NvencBringUpInputLayoutV1 input)
        {
            return NvencBringUpAdmissionValidatorV1.Evaluate(profile, capability, input);
        }

        [Test]
        public void Evaluate_NullProfile_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() =>
                NvencBringUpAdmissionValidatorV1.Evaluate(null, MakeCapability(), MakeInput()));
        }

        [Test]
        public void Evaluate_UninitializedCapability_Rejected()
        {
            Assert.Throws<ArgumentException>(() =>
                NvencBringUpAdmissionValidatorV1.Evaluate(MakeProfile(), default, MakeInput()));
        }

        [Test]
        public void Evaluate_UninitializedInput_Rejected()
        {
            Assert.Throws<ArgumentException>(() =>
                NvencBringUpAdmissionValidatorV1.Evaluate(MakeProfile(), MakeCapability(), default));
        }

        [Test]
        public void Evaluate_AllConditionsMatch_Supported()
        {
            Assert.That(
                Evaluate(MakeProfile(7), MakeCapability(), MakeInput(7)),
                Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
        }

        [Test]
        public void Evaluate_EachCapabilityConditionBroken_Unsupported()
        {
            NvencBringUpProfileV1 profile = MakeProfile(7);
            NvencBringUpInputLayoutV1 input = MakeInput(7);

            Assert.That(Evaluate(profile, MakeCapability(isWindows10OrNewer: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(isActiveAdapterNvidia: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(isCurrentGraphicsApiD3D11: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterSupportsWddm: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterSupportsAsyncEncode: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterSupportsCompletionEvent: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(isActiveAdapterTcc: true), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterCanUseOutputInVidmemZero: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterSupportsH264Encode: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterSupportsH264HighProfile: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, MakeCapability(activeAdapterSupportsNv12Input: false), input), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
        }

        /// <summary>
        /// The fixed 1280x720 must fit inside the observed maximums: one pixel
        /// short in either dimension is not admitted, an exact fit is, and a
        /// larger maximum is too.
        /// </summary>
        [Test]
        public void Evaluate_MaximumEncodeDimensions_AreComparedAgainstTheFixedSize()
        {
            NvencBringUpProfileV1 profile = MakeProfile(7);
            NvencBringUpInputLayoutV1 input = MakeInput(7);

            Assert.That(
                Evaluate(
                    profile,
                    MakeCapability(maximumEncodeWidth: NvencBringUpProfileV1.Width - 1),
                    input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(
                Evaluate(
                    profile,
                    MakeCapability(maximumEncodeHeight: NvencBringUpProfileV1.Height - 1),
                    input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));

            // An observation that found no usable dimension at all.
            Assert.That(
                Evaluate(
                    profile,
                    MakeCapability(maximumEncodeWidth: 0, maximumEncodeHeight: 0),
                    input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(
                Evaluate(
                    profile,
                    MakeCapability(maximumEncodeWidth: -1, maximumEncodeHeight: -1),
                    input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));

            // Exactly the fixed size.
            Assert.That(
                Evaluate(
                    profile,
                    MakeCapability(
                        maximumEncodeWidth: NvencBringUpProfileV1.Width,
                        maximumEncodeHeight: NvencBringUpProfileV1.Height),
                    input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Supported));

            // Larger than the fixed size.
            Assert.That(
                Evaluate(
                    profile,
                    MakeCapability(
                        maximumEncodeWidth: NvencBringUpProfileV1.Width + 1,
                        maximumEncodeHeight: NvencBringUpProfileV1.Height + 1),
                    input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
            Assert.That(
                Evaluate(profile, MakeCapability(maximumEncodeWidth: 8192, maximumEncodeHeight: 8192), input),
                Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
        }

        /// <summary>
        /// A snapshot reporting no encoder support at all is still an
        /// initialized snapshot: the constructor keeps the observed values and
        /// the admission boundary is what refuses them.
        /// </summary>
        [Test]
        public void Capability_UnsupportedEncoderFacts_AreStillAnInitializedSnapshot()
        {
            NvencBringUpCapabilityV1 capability = MakeCapability(
                activeAdapterSupportsH264Encode: false,
                activeAdapterSupportsH264HighProfile: false,
                activeAdapterSupportsNv12Input: false,
                maximumEncodeWidth: 0,
                maximumEncodeHeight: -1);

            Assert.That(capability.IsInitialized, Is.True);
            Assert.That(capability.ActiveAdapterSupportsH264Encode, Is.False);
            Assert.That(capability.ActiveAdapterSupportsH264HighProfile, Is.False);
            Assert.That(capability.ActiveAdapterSupportsNv12Input, Is.False);
            Assert.That(capability.MaximumEncodeWidth, Is.Zero);
            Assert.That(capability.MaximumEncodeHeight, Is.EqualTo(-1));

            Assert.That(
                Evaluate(MakeProfile(7), capability, MakeInput(7)),
                Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));

            // The values a supported observation reports are kept as observed
            // too.
            NvencBringUpCapabilityV1 supported = MakeCapability(
                maximumEncodeWidth: 4096, maximumEncodeHeight: 2160);

            Assert.That(supported.IsInitialized, Is.True);
            Assert.That(supported.ActiveAdapterSupportsH264Encode, Is.True);
            Assert.That(supported.ActiveAdapterSupportsH264HighProfile, Is.True);
            Assert.That(supported.ActiveAdapterSupportsNv12Input, Is.True);
            Assert.That(supported.MaximumEncodeWidth, Is.EqualTo(4096));
            Assert.That(supported.MaximumEncodeHeight, Is.EqualTo(2160));
        }

        [Test]
        public void Evaluate_EachInputConditionBroken_Unsupported()
        {
            NvencBringUpProfileV1 profile = MakeProfile(7);
            NvencBringUpCapabilityV1 capability = MakeCapability();

            Assert.That(Evaluate(profile, capability, MakeInput(profileId: 8)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(width: 640)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(height: 360)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(imageRectX: 1)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(imageRectY: 1)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(eye: CaptureEye.Right)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            // An undefined CapturePixelFormat value (not a production format)
            // must be rejected without adding a new production format.
            Assert.That(Evaluate(profile, capability, MakeInput(pixelFormat: (CapturePixelFormat)int.MaxValue)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(graphicsFormat: GraphicsFormat.R8G8B8A8_UNorm)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(colorSpace: CaptureColorSpace.Linear)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(sampleCount: 4)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(hasMipmaps: true)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(hasDynamicResolution: true)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(isTextureArray: true)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(arrayIndex: 1)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(mipLevel: 1)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profile, capability, MakeInput(orientation: NvencInputOrientation.BottomLeft)), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
        }

        [Test]
        public void Validator_IsStaticClass_WithNoMutableState()
        {
            Type type = typeof(NvencBringUpAdmissionValidatorV1);

            Assert.That(type.IsAbstract && type.IsSealed, Is.True);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, type.Name + "." + field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Evaluate_MultipleSnapshots_NoStateLeak()
        {
            NvencBringUpProfileV1 profileA = MakeProfile(7);
            NvencBringUpProfileV1 profileB = MakeProfile(8);

            NvencBringUpCapabilityV1 supportedCapability = MakeCapability();
            NvencBringUpCapabilityV1 unsupportedCapability = MakeCapability(isCurrentGraphicsApiD3D11: false);

            NvencBringUpInputLayoutV1 inputA = MakeInput(7);
            NvencBringUpInputLayoutV1 inputB = MakeInput(8);

            // Interleave supported and unsupported evaluations in both orders;
            // results must be independent of evaluation order.
            Assert.That(Evaluate(profileA, supportedCapability, inputA), Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
            Assert.That(Evaluate(profileB, unsupportedCapability, inputB), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profileA, supportedCapability, inputA), Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
            Assert.That(Evaluate(profileA, unsupportedCapability, inputA), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profileB, supportedCapability, inputB), Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
            Assert.That(Evaluate(profileA, unsupportedCapability, inputA), Is.EqualTo(NvencBringUpAdmissionDecision.Unsupported));
            Assert.That(Evaluate(profileA, supportedCapability, inputA), Is.EqualTo(NvencBringUpAdmissionDecision.Supported));
        }

        [Test]
        public void ProductionSources_NoGlobalOverrideNoIoNoThreadNoNvencNoUnityProbe()
        {
            string root = Path.Combine(Application.dataPath, "..");
            string directory = Path.Combine(root, "Assets/Zantetsu/Runtime/Observability");
            string[] files =
            {
                "NvencBringUpProfileV1.cs",
                "NvencBringUpCapabilityV1.cs",
                "NvencBringUpInputLayoutV1.cs",
                "NvencInputOrientation.cs",
                "NvencColorConversion.cs",
                "NvencBringUpAdmissionDecision.cs",
                "NvencBringUpAdmissionValidatorV1.cs",
            };

            string text = string.Empty;
            foreach (string file in files)
            {
                text += File.ReadAllText(Path.Combine(directory, file));
            }

            Assert.That(text, Does.Not.Contain("File."));
            Assert.That(text, Does.Not.Contain("Directory."));
            Assert.That(text, Does.Not.Contain("FileStream"));
            Assert.That(text, Does.Not.Contain("Thread"));
            Assert.That(text, Does.Not.Contain("Task"));
            Assert.That(text, Does.Not.Contain("ThreadPool"));
            Assert.That(text, Does.Not.Contain("DllImport"));
            Assert.That(text, Does.Not.Contain("NvEnc"));
            Assert.That(text, Does.Not.Contain("SystemInfo"));
            Assert.That(text, Does.Not.Contain("GraphicsDevice"));
            Assert.That(text, Does.Not.Contain("AsyncGPUReadback"));
            Assert.That(text, Does.Not.Contain("Application."));
        }
    }
}
