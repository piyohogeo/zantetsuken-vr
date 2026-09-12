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
// Beyond the session itself, this owner holds what one Phase 0.11 Run needs
// and nothing more: the fixed conversion shaders and, per slot, the NV12 input
// surface with its plane views and registration, the output bitstream buffer,
// and the completion event. Nothing here binds state, issues GPU work, or
// encodes.

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

    /// The fixed number of source RGBA surfaces a Phase 0.11 session binds.
    /// It is deliberately its own count and its own set: a source surface and
    /// an encode sample slot are bound to each other only while a frame is in
    /// flight, so they are never the same array.
    constexpr uint32_t kSourceSurfaceSlotCount = 8;

    /// The one encoded size, which is also the one input surface size and the
    /// one accepted source size: this bring-up neither scales nor crops.
    constexpr uint32_t kInputSurfaceWidth = 1280;
    constexpr uint32_t kInputSurfaceHeight = 720;

    /// An owner must not be destroyed while it still holds an encoder, a
    /// completion-event handle, an output bitstream buffer, an input surface,
    /// a conversion shader, or a source surface binding: everything has to be
    /// released and the session closed successfully first, and an owner whose
    /// release or close was refused stays alive with what it holds.

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

        /// Binds the fixed set of source RGBA surfaces, exactly once per
        /// owner, taking this session's own reference to each texture and
        /// creating the shader resource view it will be read through. The
        /// encoder must already be initialized and nothing else may be
        /// prepared yet.
        ///
        /// Each texture is accepted only as the fixed source it has to be: the
        /// exact device this session is open on, and a descriptor that is
        /// typeless RGBA8 at the fixed size with one mip, one slice, no
        /// multisampling, and a shader resource binding. The sRGB view this
        /// creates is what gives that storage its colour interpretation. Nothing is converted, copied, recreated, or retried,
        /// and no other format is accepted in its place. All of them must
        /// succeed; a failure releases what it took, in reverse.
        ///
        /// The pointers are the caller's resources, passed as raw addresses:
        /// each is asked for its 2D interface rather than assumed to be one,
        /// and the reference that query returns is what this session holds. It
        /// destroys none of them and hands none of them back.
        bool TryBindSourceSurfaces(void* const* textures, uint32_t count);

        /// Releases the whole set in reverse order - each view, then each
        /// texture reference - exactly once per owner. Everything prepared on
        /// top of the sources goes first: a session that still holds an input
        /// surface, an output buffer, or a completion event refuses before
        /// this release's one attempt is spent.
        bool TryReleaseSourceSurfaces();

        /// Creates the fixed conversion pipeline and the fixed set of NV12
        /// input surfaces, exactly once per owner: the three shader objects
        /// first, in pipeline order, then one registered surface per slot. The
        /// encoder must already be initialized and no other slot resource may
        /// be prepared yet. All of it must succeed; a failure part of the way
        /// through unwinds what it took in reverse, and a step of that
        /// unwinding which itself fails leaves that slot and every slot still
        /// held with this session - and then the shaders too, since they are
        /// not taken out from under surfaces that are still registered.
        ///
        /// Nothing is bound, drawn, or issued here: these are objects the
        /// session owns, not a pipeline that has run.
        bool TryPrepareInputSurfaces();

        /// Unregisters and releases the whole set in reverse order, exactly
        /// once per owner: every slot first, then the chroma, luma, and vertex
        /// shaders. The completion events and output buffers go before all of
        /// it: a session that still has either refuses. A refused unregister
        /// stops the release with that slot and the ones before it still held,
        /// and leaves the shaders alone.
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
        /// this session must destroy it. The NV12 input surface is four facts
        /// again - the texture this session must release, the two plane render
        /// target views the conversion will draw through, and the registration
        /// the driver must be told to forget - so a half-prepared slot is never
        /// mistaken for a finished or an empty one.
        ///
        /// None of it is exposed: there is no getter and no slot in the ABI.
        struct EncodeSampleSlot
        {
            HANDLE completionEvent;
            bool completionEventRegistered;
            NV_ENC_OUTPUT_PTR outputBitstreamBuffer;
            ID3D11Texture2D* inputTexture;
            ID3D11RenderTargetView* inputLumaRenderTargetView;
            ID3D11RenderTargetView* inputChromaRenderTargetView;
            NV_ENC_REGISTERED_PTR registeredInputResource;
        };

        bool TryReleaseCompletionEventSlot(EncodeSampleSlot& slot);
        void RollBackPreparedCompletionEvents(uint32_t count);
        bool AnyCompletionEventHeld() const;

        bool TryDestroyOutputBitstreamBuffer(EncodeSampleSlot& slot);
        void RollBackPreparedOutputBitstreamBuffers(uint32_t count);
        bool AnyOutputBitstreamBufferHeld() const;

        /// One source surface, as far as it exists: the 2D interface
        /// reference this session got from the caller's resource and the view
        /// the conversion reads it through.
        /// Both are facts of their own, so a half-bound surface is never
        /// mistaken for a finished or an empty one. Neither is exposed.
        struct SourceSurfaceSlot
        {
            ID3D11Texture2D* texture;
            ID3D11ShaderResourceView* shaderResourceView;
        };

        /// Whether this texture is the fixed source this session accepts: the
        /// exact device, and the exact descriptor. Nothing is inferred from a
        /// Unity-side name, and no other format is tried.
        bool IsAcceptedSourceTexture(ID3D11Texture2D* texture) const;

        void ReleaseSourceSurfaceSlot(SourceSurfaceSlot& slot);
        void RollBackBoundSourceSurfaces(uint32_t count);
        bool AnySourceSurfaceHeld() const;
        bool AreSourceSurfacesFullyBound() const;

        bool TryReleaseInputSurfaceSlot(EncodeSampleSlot& slot);
        void RollBackPreparedInputSurfaces(uint32_t count);

        /// Whether any slot still holds a texture, a plane view, or a
        /// registration. The shaders are deliberately not part of this: the
        /// rollback asks it to decide whether the shaders may follow.
        bool AnyInputSurfaceSlotResourceHeld() const;

        /// Whether anything of the input side is still held - any slot
        /// resource or any of the three shaders. A session holding only
        /// shaders is still holding something and is not closable.
        bool AnyInputSurfaceHeld() const;

        /// Whether every slot holds its texture, both plane views, and its
        /// registration, and all three conversion shaders exist. Anything less
        /// is not a prepared input set.
        bool AreInputSurfacesFullyPrepared() const;

        /// Creates the three conversion shader objects in pipeline order,
        /// taking ownership of each the moment it exists. A failure keeps the
        /// real HRESULT and releases what it already made, in reverse.
        bool TryCreateConversionShaders();

        /// Releases the three in the reverse of the order they were created.
        /// A COM release cannot be refused, so this reports nothing.
        void ReleaseConversionShaders();

        // The fixed conversion pipeline this session owns: one fullscreen
        // triangle vertex shader and the two plane pixel shaders, held
        // directly. There is no shader owner, registry, version table, or
        // cache - these three are all there is, and none of them is exposed.
        ID3D11VertexShader* _conversionVertexShader = nullptr;
        ID3D11PixelShader* _conversionLumaPixelShader = nullptr;
        ID3D11PixelShader* _conversionChromaPixelShader = nullptr;

        // The fixed set of source surfaces this session binds, kept apart
        // from the encode sample slots.
        SourceSurfaceSlot _sourceSlots[kSourceSurfaceSlotCount] = {};
        bool _sourceSurfacesBindAttempted = false;
        bool _sourceSurfacesReleaseAttempted = false;

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
