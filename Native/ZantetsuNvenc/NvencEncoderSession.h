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

    /// The fixed number of completion events a Phase 0.11 session prepares,
    /// one per encode sample slot. It is a constant of the design: nothing
    /// here grows, shrinks, or adds one later.
    constexpr uint32_t kCompletionEventCount = 8;

    /// An owner must not be destroyed while it still holds an encoder or any
    /// completion-event handle: the events have to be released and the session
    /// closed successfully first, and an owner whose release or close was
    /// refused stays alive with what it holds.
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

        bool IsOpen() const { return _encoder != nullptr; }

        DWORD LastWin32Error() const { return _lastWin32Error; }
        NVENCSTATUS LastNvencStatus() const { return _lastNvencStatus; }

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
        NVENCSTATUS _lastNvencStatus = NV_ENC_SUCCESS;
        bool _openAttempted = false;
        bool _closeAttempted = false;
        bool _capabilityObservationAttempted = false;
        bool _initializationAttempted = false;
        // Kept as internal state: the completion-event registration admits
        // itself against it.
        bool _encoderInitialized = false;

        /// One slot of the fixed set. Holding a handle means the session is
        /// responsible for it; whether the driver also has it registered is
        /// the separate fact beside it, so a partial failure is never
        /// mistaken for either state.
        struct CompletionEventSlot
        {
            HANDLE handle;
            bool registered;
        };

        bool TryReleaseCompletionEventSlot(CompletionEventSlot& slot);
        void RollBackPreparedCompletionEvents(uint32_t count);
        bool AnyCompletionEventHeld() const;

        // The fixed set this session owns, and the two attempts that may touch
        // it.
        CompletionEventSlot _completionEvents[kCompletionEventCount] = {};
        bool _completionEventsPrepareAttempted = false;
        bool _completionEventsReleaseAttempted = false;
    };
}

#endif
