namespace Zantetsu.Observability
{
    /// <summary>
    /// What one open NVENC encoder session reported about itself: the encode
    /// facts the bring-up admission compares, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every value was observed from the session currently open on the device
    /// Unity is using. That such a session opened at all is not treated as
    /// evidence for any of these: each was asked for and answered separately.
    /// </para>
    /// <para>
    /// An encoder that does not enumerate H.264 reports false and zero
    /// throughout - a completed observation of no support, not a failed one -
    /// and nothing here says whether an encoder session would initialize with
    /// a particular configuration. No GUID, adapter name, or driver string is
    /// carried, and a default value is an uninitialized snapshot.
    /// </para>
    /// </remarks>
    internal readonly struct NvencEncoderCapabilityObservationV1
    {
        private readonly bool _initialized;

        internal NvencEncoderCapabilityObservationV1(
            bool supportsAsyncEncode,
            bool supportsH264Encode,
            bool supportsH264HighProfile,
            bool supportsNv12Input,
            int maximumEncodeWidth,
            int maximumEncodeHeight)
        {
            SupportsAsyncEncode = supportsAsyncEncode;
            SupportsH264Encode = supportsH264Encode;
            SupportsH264HighProfile = supportsH264HighProfile;
            SupportsNv12Input = supportsNv12Input;
            MaximumEncodeWidth = maximumEncodeWidth;
            MaximumEncodeHeight = maximumEncodeHeight;
            _initialized = true;
        }

        internal bool IsInitialized => _initialized;

        internal bool SupportsAsyncEncode { get; }

        internal bool SupportsH264Encode { get; }

        internal bool SupportsH264HighProfile { get; }

        internal bool SupportsNv12Input { get; }

        internal int MaximumEncodeWidth { get; }

        internal int MaximumEncodeHeight { get; }
    }
}
