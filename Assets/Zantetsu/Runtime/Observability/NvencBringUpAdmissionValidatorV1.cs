using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, side-effect-free admission boundary for the NVENC bring-up
    /// profile. It compares a profile, a capability snapshot, and an
    /// input-layout snapshot and returns
    /// <see cref="NvencBringUpAdmissionDecision.Supported"/> only when every
    /// fixed condition matches; any single mismatch returns
    /// <see cref="NvencBringUpAdmissionDecision.Unsupported"/>. It performs no
    /// fallback, degradation, or runtime reconfiguration, holds no state, and
    /// never contacts Unity static APIs, the OS, the filesystem, NVENC, or a
    /// D3D handle.
    /// </summary>
    internal static class NvencBringUpAdmissionValidatorV1
    {
        internal static NvencBringUpAdmissionDecision Evaluate(
            NvencBringUpProfileV1 profile,
            in NvencBringUpCapabilityV1 capability,
            in NvencBringUpInputLayoutV1 input)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (!capability.IsInitialized)
            {
                throw new ArgumentException("Capability snapshot must be initialized.", nameof(capability));
            }

            if (!input.IsInitialized)
            {
                throw new ArgumentException("Input layout snapshot must be initialized.", nameof(input));
            }

            if (!capability.IsWindows10OrNewer) return NvencBringUpAdmissionDecision.Unsupported;
            if (!capability.IsActiveAdapterNvidia) return NvencBringUpAdmissionDecision.Unsupported;
            if (!capability.IsCurrentGraphicsApiD3D11) return NvencBringUpAdmissionDecision.Unsupported;
            if (!capability.ActiveAdapterSupportsWddm) return NvencBringUpAdmissionDecision.Unsupported;
            if (!capability.ActiveAdapterSupportsAsyncEncode) return NvencBringUpAdmissionDecision.Unsupported;
            if (!capability.ActiveAdapterSupportsCompletionEvent) return NvencBringUpAdmissionDecision.Unsupported;
            if (capability.IsActiveAdapterTcc) return NvencBringUpAdmissionDecision.Unsupported;
            if (!capability.ActiveAdapterCanUseOutputInVidmemZero) return NvencBringUpAdmissionDecision.Unsupported;

            if (input.ProfileId != profile.ProfileId) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.Width != NvencBringUpProfileV1.Width) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.Height != NvencBringUpProfileV1.Height) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.ImageRect.X != profile.ImageRect.X
                || input.ImageRect.Y != profile.ImageRect.Y
                || input.ImageRect.Width != profile.ImageRect.Width
                || input.ImageRect.Height != profile.ImageRect.Height)
            {
                return NvencBringUpAdmissionDecision.Unsupported;
            }

            if (input.Eye != profile.Eye) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.PixelFormat != profile.PixelFormat) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.GraphicsFormat != profile.GraphicsFormat) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.ColorSpace != profile.ColorSpace) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.SampleCount != profile.SampleCount) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.HasMipmaps != profile.HasMipmaps) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.HasDynamicResolution != profile.HasDynamicResolution) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.IsTextureArray != profile.IsTextureArray) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.ArrayIndex != profile.ArrayIndex) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.MipLevel != profile.MipLevel) return NvencBringUpAdmissionDecision.Unsupported;
            if (input.Orientation != profile.Orientation) return NvencBringUpAdmissionDecision.Unsupported;

            return NvencBringUpAdmissionDecision.Supported;
        }
    }
}
