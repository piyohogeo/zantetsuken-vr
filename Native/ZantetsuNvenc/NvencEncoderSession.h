// Phase 0.11 retained NVENC encoder session.
//
// One owner holds everything a session needs to stay usable: the driver module
// and its function table, an independent reference to the exact D3D11 device
// Unity is using, and the encoder handle opened on that device. They are opened
// in that order and closed in the reverse one, so the session never outlives
// what it was opened against.
//
// This is not a probe session that is opened and thrown away. The session
// stays open until it is closed explicitly, so the units that come after can
// query and initialize this very encoder.
//
// Nothing here queries a capability, initializes an encoder, or creates an
// event, texture, or buffer.

#ifndef ZANTETSU_NVENC_ENCODER_SESSION_H
#define ZANTETSU_NVENC_ENCODER_SESSION_H

#include <d3d11.h>
#include <windows.h>

#include <nvEncodeAPI.h>

#include "D3D11DeviceBinding.h"
#include "NvencDriverApi.h"

namespace zantetsu
{
    /// How far the open got.
    ///
    /// Unsupported is reserved for the configuration genuinely not being able
    /// to encode here: a driver older than this build requires, no current
    /// D3D11 device to open against, or the driver answering that this device
    /// has no encoder or is not an encodable one. A missing entry point, an
    /// invalid or vanished device, a rejected parameter, or any other error is
    /// Failed - an observation that did not complete - and is never folded
    /// into "unsupported". No second opinion about the adapter is sought from
    /// another API either.
    enum class NvencEncoderSessionOpenStatus
    {
        Opened,
        Unsupported,
        Failed,
    };

    /// How far the close got. A close that the driver refuses leaves the
    /// session owner exactly as it was.
    enum class NvencEncoderSessionCloseStatus
    {
        Closed,
        Failed,
    };

    /// The fixed number of encode sample slots a Phase 0.11 session prepares,
    /// each with its own completion event and output bitstream buffer. It is a
    /// constant of the design: nothing here grows, shrinks, or adds one later.
    constexpr uint32_t kEncodeSampleSlotCount = 8;

    /// The one encoded size, which is also the one input surface size: this
    /// bring-up neither scales nor crops.
    constexpr uint32_t kInputSurfaceWidth = 1280;
    constexpr uint32_t kInputSurfaceHeight = 720;

    /// An owner must not be destroyed while it still holds an encoder, a
    /// completion-event handle, an output bitstream buffer, or an input
    /// surface: everything has to be released and the session closed
    /// successfully first, and an owner whose release or close was refused
    /// stays alive with what it holds.
    /// What the encoder session reports about itself, as observed. Every value
    /// comes from the session that is currently open on the current device;
    /// that a session opened at all is not taken as a substitute for any of
    /// them.
    ///
    /// When H.264 is not among the codecs the encoder enumerates, the profile,
    /// input format, and capability queries are not made at all and the whole
    /// observation reads as unsupported - false and zero - which is a
    /// completed observation, not a failure.
    struct NvencEncoderCapabilityObservation
    {
        bool supportsAsyncEncode;
        bool supportsH264Encode;
        bool supportsH264HighProfile;
        bool supportsNv12Input;
        int32_t maximumEncodeWidth;
        int32_t maximumEncodeHeight;
    };

    /// The fixed encoder request, already mapped from the project's canonical
    /// identifiers to the SDK's own GUIDs and enumerations. Every field is one
    /// this initialization actually sets; nothing is carried "just in case".
    struct NvencEncoderInitializationRequest
    {
        GUID encodeGuid;
        GUID presetGuid;
        GUID profileGuid;
        NV_ENC_TUNING_INFO tuningInfo;
        NV_ENC_PARAMS_RC_MODE rateControlMode;

        uint32_t encodeWidth;
        uint32_t encodeHeight;
        uint32_t maximumEncodeWidth;
        uint32_t maximumEncodeHeight;
        uint32_t frameRateNumerator;
        uint32_t frameRateDenominator;

        uint32_t enablePictureTypeDecision;
        uint32_t gopLength;
        uint32_t idrPeriod;
        int32_t frameIntervalP;

        uint32_t qpIntra;
        uint32_t qpInterP;
        uint32_t qpInterB;

        uint32_t repeatSequenceAndPictureParameterSets;
        uint32_t outputAccessUnitDelimiter;
        uint32_t disableSequenceAndPictureParameterSets;
        uint32_t chromaFormatIdc;
        uint32_t level;
        uint32_t progressiveEncoding;
        uint32_t enableEncodeAsync;
        uint32_t enableOutputInVideoMemory;
    };

    class NvencEncoderSession
    {
    public:
        NvencEncoderSession() = default;
        ~NvencEncoderSession();

        NvencEncoderSession(const NvencEncoderSession&) = delete;
        NvencEncoderSession& operator=(const NvencEncoderSession&) = delete;

        /// Opens one session against the device the binding currently holds,
        /// exactly once per owner. A second call makes no attempt and fails.
        NvencEncoderSessionOpenStatus Open(const D3D11DeviceBinding& binding);

        /// Destroys the session and releases what it was opened against, in
        /// reverse order, exactly once per owner. A refused destroy releases
        /// nothing and is never repeated - neither by this owner nor by a
        /// second call, which makes no attempt at all.
        NvencEncoderSessionCloseStatus Close();

        /// Observes this session's encoder capabilities, exactly once per
        /// owner. The session stays open whatever the answer is - a failed
        /// observation closes nothing, reopens nothing, and falls back to no
        /// other device or API - and a second call makes no attempt and
        /// touches no driver entry point.
        ///
        /// The caller serializes this against Close; neither is made safe to
        /// call while the other runs.
        bool TryObserveCapabilities(NvencEncoderCapabilityObservation* observation);

        /// Applies the fixed request to this session's encoder, exactly once
        /// per owner: the preset configuration is fetched once, the fixed
        /// values are written over it, and the encoder is initialized once.
        ///
        /// The session stays open whatever the answer is - a failed
        /// initialization retries nothing, reopens nothing, and falls back to
        /// no other preset, tuning, device, or API - and a second call makes no
        /// attempt and touches no driver entry point.
        ///
        /// The caller serializes this against the other calls, as it does for
        /// the capability observation.
        bool TryInitializeEncoder(const NvencEncoderInitializationRequest& request);

        /// Creates and registers the fixed set of completion events, exactly
        /// once per owner. The encoder must already be initialized. Every one
        /// of the slots must succeed for the set to be prepared; a failure
        /// part of the way through unwinds what it took in reverse, and a step
        /// of that unwinding which itself fails leaves that slot and every
        /// slot still held with this session rather than guessing. A second
        /// call makes no attempt, touching neither the OS nor the driver.
        bool TryPrepareCompletionEvents();

        /// Unregisters and closes the whole set in reverse order, exactly once
        /// per owner. A refused unregister or close stops the release with
        /// that slot and the ones before it still held; nothing is assumed to
        /// have happened, and nothing is retried here.
        bool TryReleaseCompletionEvents();

        /// Creates the fixed set of NV12 input surfaces, one per slot and
        /// exactly once per owner, and registers each with the encoder. The
        /// encoder must already be initialized and no other slot resource may
        /// be prepared yet. All of the slots must succeed; a failure part of
        /// the way through unwinds what it took in reverse, and a step of that
        /// unwinding which itself fails leaves that slot and every slot still
        /// held with this session.
        bool TryPrepareInputSurfaces();

        /// Unregisters and releases the whole set in reverse order, exactly
        /// once per owner. The completion events and output buffers go first:
        /// a session that still has either refuses. A refused unregister stops
        /// the release with that slot and the ones before it still held.
        bool TryReleaseInputSurfaces();

        /// Creates the fixed set of output bitstream buffers, one per slot and
        /// exactly once per owner. The encoder must already be initialized and
        /// the input surfaces already prepared. All of the slots must succeed;
        /// a failure part of the way through destroys what it made in reverse,
        /// and a destroy that is itself refused stops there, leaving that
        /// buffer and the ones before it with this session.
        bool TryPrepareOutputBitstreamBuffers();

        /// Destroys the whole set in reverse order, exactly once per owner.
        /// The completion events are released first: a session that still has
        /// them refuses. A refused destroy stops the release with that buffer
        /// and the ones before it still held.
        bool TryReleaseOutputBitstreamBuffers();

        bool IsOpen() const { return _encoder != nullptr; }

        DWORD LastWin32Error() const { return _lastWin32Error; }
        NVENCSTATUS LastNvencStatus() const { return _lastNvencStatus; }

        /// The last HRESULT a D3D11 call failed with. Only a D3D11 failure
        /// ever sets it; an NVENC failure never invents one.
        HRESULT LastHResult() const { return _lastHResult; }

    private:
        void ReleaseDeviceAndDriver();

        bool TryObserveH264Support(bool& supported);
        bool TryObserveH264HighProfileSupport(bool& supported);
        bool TryObserveNv12InputSupport(bool& supported);
        bool TryQueryCap(NV_ENC_CAPS cap, int& value);

        NvencDriverApi _driverApi;
        const NV_ENCODE_API_FUNCTION_LIST* _functionList = nullptr;
        ID3D11Device* _device = nullptr;
        void* _encoder = nullptr;
        PNVENCDESTROYENCODER _destroyEncoder = nullptr;
        DWORD _lastWin32Error = 0;
        HRESULT _lastHResult = S_OK;
        NVENCSTATUS _lastNvencStatus = NV_ENC_SUCCESS;
        bool _openAttempted = false;
        bool _closeAttempted = false;
        bool _capabilityObservationAttempted = false;
        bool _initializationAttempted = false;
        // Kept as internal state: the completion-event registration admits
        // itself against it.
        bool _encoderInitialized = false;

        /// One encode sample slot's resources, as far as they exist. Holding
        /// a completion-event handle means the session is responsible for it;
        /// whether the driver also has it registered is the separate fact
        /// beside it, so a partial failure is never mistaken for either state.
        /// The output bitstream buffer is held the same way: non-null means
        /// this session must destroy it. The NV12 input surface is two facts
        /// again - the texture this session must release, and the registration
        /// the driver must be told to forget - so a half-prepared slot is
        /// never mistaken for either.
        ///
        /// This is not a finished slot - the input resource belongs to a later
        /// unit - and none of it is exposed: there is no getter and no slot in
        /// the ABI.
        struct EncodeSampleSlot
        {
            HANDLE completionEvent;
            bool completionEventRegistered;
            NV_ENC_OUTPUT_PTR outputBitstreamBuffer;
            ID3D11Texture2D* inputTexture;
            NV_ENC_REGISTERED_PTR registeredInputResource;
        };

        bool TryReleaseCompletionEventSlot(EncodeSampleSlot& slot);
        void RollBackPreparedCompletionEvents(uint32_t count);
        bool AnyCompletionEventHeld() const;

        bool TryDestroyOutputBitstreamBuffer(EncodeSampleSlot& slot);
        void RollBackPreparedOutputBitstreamBuffers(uint32_t count);
        bool AnyOutputBitstreamBufferHeld() const;

        bool TryReleaseInputSurfaceSlot(EncodeSampleSlot& slot);
        void RollBackPreparedInputSurfaces(uint32_t count);
        bool AnyInputSurfaceHeld() const;

        // The fixed set of slots this session owns, and the attempts that may
        // touch each kind of resource in them.
        EncodeSampleSlot _slots[kEncodeSampleSlotCount] = {};
        bool _completionEventsPrepareAttempted = false;
        bool _completionEventsReleaseAttempted = false;
        bool _outputBitstreamBuffersPrepareAttempted = false;
        bool _outputBitstreamBuffersReleaseAttempted = false;
        bool _inputSurfacesPrepareAttempted = false;
        bool _inputSurfacesReleaseAttempted = false;
    };
}

#endif
