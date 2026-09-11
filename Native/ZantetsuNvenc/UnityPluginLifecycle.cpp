// Phase 0.11 Unity plugin lifecycle and current D3D11 device binding.
//
// This translation unit does one thing: while Unity's current renderer is
// D3D11, it publishes the exact ID3D11Device that Unity is using - taken from
// IUnityGraphicsD3D11 at the device events Unity raises - into the one device
// binding, and clears that binding again when Unity is about to reset or shut
// the device down. The binding owns the reference and the locking; this file
// owns only when to publish and when to clear.
//
// Holding that device proves three things and no more:
//   * Unity's current renderer was D3D11 at that event,
//   * GetDevice() returned a non-null device at that event, and
//   * this plugin holds a COM reference to it.
// It does not prove that the adapter is NVIDIA, that it runs under WDDM, that
// NVENC is present or supports asynchronous encode, that the device is
// healthy, or that any later encoder session will succeed. Every one of those
// is answered elsewhere, later.
//
// Nothing here loads the NVENC DLL, calls an NVENC entry point, opens a
// session, queries a capability, registers a GPU command, waits, polls,
// sleeps, starts a thread or queue, touches the filesystem, logs, or calls
// back into managed code.
//
// The observation export reports those two facts - plugin loaded, device held -
// as fixed-width values. It never dereferences, addrefs, or releases the device
// it reports on.
//
// The session exports open one retained encoder session against that same
// device, observe what that session's encoder supports, initialize that encoder
// with the fixed request, prepare and release its fixed sets of NV12 input
// surfaces, output bitstream buffers, and completion events, and close it. The session owner keeps its own reference, so it is not affected
// by what the graphics lifecycle does next, and only an opaque handle to it
// crosses the boundary.

#include <atomic>
#include <new>

#include <d3d11.h>

#include "D3D11DeviceBinding.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IUnityInterface.h"
#include "NvencEncoderSession.h"
#include "ZantetsuNvencGraphicsObservationV1.h"
#include "ZantetsuNvencSessionV1.h"

namespace
{
    // Borrowed from Unity for the plugin's lifetime; no COM ownership is
    // claimed over the interface pointers themselves.
    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;

    // The one place this plugin holds the current device. Publishing,
    // clearing, and handing out independent references all go through it, so a
    // later owner can take a reference that outlives the next clear.
    zantetsu::D3D11DeviceBinding g_deviceBinding;

    // True from the moment UnityPluginLoad has finished its work until
    // UnityPluginUnload has finished undoing it.
    std::atomic<bool> g_loaded{false};

    /// Observes the device Unity is currently using, but only while the
    /// current renderer is D3D11.
    void AcquireCurrentDevice()
    {
        if (g_unityInterfaces == nullptr || g_unityGraphics == nullptr)
        {
            return;
        }

        if (g_unityGraphics->GetRenderer() != kUnityGfxRendererD3D11)
        {
            g_deviceBinding.Clear();
            return;
        }

        // A device that cannot be obtained now must not leave an older one
        // standing as if it were still the current device.
        IUnityGraphicsD3D11* d3d11 =
            g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (d3d11 == nullptr)
        {
            g_deviceBinding.Clear();
            return;
        }

        ID3D11Device* device = d3d11->GetDevice();
        if (device == nullptr)
        {
            g_deviceBinding.Clear();
            return;
        }

        // The exact pointer Unity handed back is the one that is published;
        // the binding takes its own reference to it.
        g_deviceBinding.PublishBorrowed(device);
    }

    /// The one place the project's canonical identifiers become the SDK's own
    /// GUIDs and enumerations. Anything this build does not name is rejected
    /// rather than guessed.
    bool TryMapInitializeRequest(
        const ZantetsuNvencSessionInitializeRequestV1& request,
        zantetsu::NvencEncoderInitializationRequest& mapped)
    {
        if (request.abiVersion != ZANTETSU_NVENC_SESSION_V1_VERSION ||
            request.codecId != ZANTETSU_NVENC_CODEC_V1_H264 ||
            request.profileId != ZANTETSU_NVENC_PROFILE_V1_HIGH ||
            request.presetId != ZANTETSU_NVENC_PRESET_V1_P1 ||
            request.tuningId != ZANTETSU_NVENC_TUNING_V1_LOW_LATENCY ||
            request.rateControlId != ZANTETSU_NVENC_RATE_CONTROL_V1_CONSTANT_QP ||
            request.chromaFormatId != ZANTETSU_NVENC_CHROMA_FORMAT_V1_420 ||
            request.levelId != ZANTETSU_NVENC_LEVEL_V1_AUTO)
        {
            return false;
        }

        if (request.enablePictureTypeDecision > 1 ||
            request.repeatSequenceAndPictureParameterSets > 1 ||
            request.outputAccessUnitDelimiter > 1 ||
            request.disableSequenceAndPictureParameterSets > 1 ||
            request.progressiveEncoding > 1 ||
            request.enableEncodeAsync > 1 ||
            request.enableOutputInVideoMemory > 1)
        {
            return false;
        }

        mapped.encodeGuid = NV_ENC_CODEC_H264_GUID;
        mapped.presetGuid = NV_ENC_PRESET_P1_GUID;
        mapped.profileGuid = NV_ENC_H264_PROFILE_HIGH_GUID;
        mapped.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;
        mapped.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP;

        // yuv420.
        mapped.chromaFormatIdc = 1;
        mapped.level = NV_ENC_LEVEL_AUTOSELECT;

        mapped.encodeWidth = request.encodeWidth;
        mapped.encodeHeight = request.encodeHeight;
        mapped.maximumEncodeWidth = request.maximumEncodeWidth;
        mapped.maximumEncodeHeight = request.maximumEncodeHeight;
        mapped.frameRateNumerator = request.frameRateNumerator;
        mapped.frameRateDenominator = request.frameRateDenominator;

        mapped.enablePictureTypeDecision = request.enablePictureTypeDecision;
        mapped.gopLength = request.gopLength;
        mapped.idrPeriod = request.idrPeriod;
        mapped.frameIntervalP = request.frameIntervalP;

        mapped.qpIntra = request.qpIntra;
        mapped.qpInterP = request.qpInterP;
        mapped.qpInterB = request.qpInterB;

        mapped.repeatSequenceAndPictureParameterSets =
            request.repeatSequenceAndPictureParameterSets;
        mapped.outputAccessUnitDelimiter = request.outputAccessUnitDelimiter;
        mapped.disableSequenceAndPictureParameterSets =
            request.disableSequenceAndPictureParameterSets;

        mapped.progressiveEncoding = request.progressiveEncoding;
        mapped.enableEncodeAsync = request.enableEncodeAsync;
        mapped.enableOutputInVideoMemory = request.enableOutputInVideoMemory;
        return true;
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        switch (eventType)
        {
            case kUnityGfxDeviceEventInitialize:
            case kUnityGfxDeviceEventAfterReset:
                AcquireCurrentDevice();
                break;

            case kUnityGfxDeviceEventBeforeReset:
            case kUnityGfxDeviceEventShutdown:
                // Nothing but an ordinary device reference exists yet: no
                // session, registered resource, event, or buffer has to be
                // unwound before it is let go.
                g_deviceBinding.Clear();
                break;

            default:
                break;
        }
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unityInterfaces = unityInterfaces;
    if (g_unityInterfaces == nullptr)
    {
        return;
    }

    g_unityGraphics = g_unityInterfaces->Get<IUnityGraphics>();
    if (g_unityGraphics == nullptr)
    {
        return;
    }

    g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);

    // The initialize event is already past when a plugin loads late, so the
    // current device is taken here as well.
    AcquireCurrentDevice();

    g_loaded.store(true, std::memory_order_release);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_unityGraphics != nullptr)
    {
        g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }

    g_deviceBinding.Clear();

    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;

    // Published last: the callback is unregistered and the device is released
    // before this plugin stops reporting itself as loaded.
    g_loaded.store(false, std::memory_order_release);
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencOpenSessionV1(
    ZantetsuNvencSessionOpenResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionOpenResultV1))
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->sessionOwner = 0;
    destination->lastWin32Error = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        new (std::nothrow) zantetsu::NvencEncoderSession();
    if (session == nullptr)
    {
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    const zantetsu::NvencEncoderSessionOpenStatus status =
        session->Open(g_deviceBinding);

    if (status == zantetsu::NvencEncoderSessionOpenStatus::Opened)
    {
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
        destination->sessionOwner = reinterpret_cast<uint64_t>(session);
        return 1;
    }

    destination->lastWin32Error = session->LastWin32Error();
    destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
    destination->status =
        status == zantetsu::NvencEncoderSessionOpenStatus::Unsupported
            ? ZANTETSU_NVENC_SESSION_V1_STATUS_UNSUPPORTED
            : ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;

    // An open that did not succeed holds nothing: the owner released the
    // device reference and the module itself.
    delete session;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencObserveSessionCapabilityV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCapabilityResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionCapabilityResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->supportsAsyncEncode = 0;
    destination->supportsH264Encode = 0;
    destination->supportsH264HighProfile = 0;
    destination->supportsNv12Input = 0;
    destination->maximumEncodeWidth = 0;
    destination->maximumEncodeHeight = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    zantetsu::NvencEncoderCapabilityObservation observation = {};
    if (!session->TryObserveCapabilities(&observation))
    {
        // The session remains open and is still the caller's to close.
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->supportsAsyncEncode = observation.supportsAsyncEncode ? 1u : 0u;
    destination->supportsH264Encode = observation.supportsH264Encode ? 1u : 0u;
    destination->supportsH264HighProfile = observation.supportsH264HighProfile ? 1u : 0u;
    destination->supportsNv12Input = observation.supportsNv12Input ? 1u : 0u;
    destination->maximumEncodeWidth = observation.maximumEncodeWidth;
    destination->maximumEncodeHeight = observation.maximumEncodeHeight;
    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencInitializeSessionEncoderV1(
    uint64_t sessionOwner,
    const ZantetsuNvencSessionInitializeRequestV1* request,
    uint32_t requestSize,
    ZantetsuNvencSessionInitializeResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionInitializeResultV1) ||
        request == nullptr ||
        requestSize != sizeof(ZantetsuNvencSessionInitializeRequestV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    // Mapped before anything is written or attempted: a request this build
    // does not recognise spends no initialization.
    zantetsu::NvencEncoderInitializationRequest mapped = {};
    if (!TryMapInitializeRequest(*request, mapped))
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryInitializeEncoder(mapped))
    {
        // The session remains open and is still the caller's to close.
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionCompletionEventsV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCompletionEventResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionCompletionEventResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastWin32Error = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryPrepareCompletionEvents())
    {
        // The session is still initialized and open. It is closable unless
        // the unwinding left a handle or a registration behind, which the raw
        // Win32 error or NVENCSTATUS reports.
        destination->lastWin32Error = static_cast<uint32_t>(session->LastWin32Error());
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionCompletionEventsV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCompletionEventResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionCompletionEventResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastWin32Error = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryReleaseCompletionEvents())
    {
        // What the release could not finish is still the session's, so the
        // session is not yet closable.
        destination->lastWin32Error = static_cast<uint32_t>(session->LastWin32Error());
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionInputSurfacesV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionInputSurfaceResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionInputSurfaceResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastHResult = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryPrepareInputSurfaces())
    {
        // The session is still initialized and open. It is closable unless the
        // unwinding left a texture or a registration behind, which the raw
        // HRESULT or NVENCSTATUS reports.
        destination->lastHResult = static_cast<int32_t>(session->LastHResult());
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionInputSurfacesV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionInputSurfaceResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionInputSurfaceResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastHResult = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryReleaseInputSurfaces())
    {
        // What the release could not finish - or the resources that have to go
        // first - is still the session's, so the session is not yet closable.
        destination->lastHResult = static_cast<int32_t>(session->LastHResult());
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionOutputBuffersV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionOutputBufferResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionOutputBufferResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryPrepareOutputBitstreamBuffers())
    {
        // The session is still initialized and open. It is closable unless the
        // unwinding left a buffer behind, which the raw NVENCSTATUS reports.
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionOutputBuffersV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionOutputBufferResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionOutputBufferResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (!session->TryReleaseOutputBitstreamBuffers())
    {
        // What the release could not finish - or the completion events that
        // have to go first - is still the session's, so the session is not yet
        // closable.
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencCloseSessionV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCloseResultV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencSessionCloseResultV1) ||
        sessionOwner == 0)
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_SESSION_V1_VERSION;
    destination->lastWin32Error = 0;
    destination->lastNvencStatus = 0;

    zantetsu::NvencEncoderSession* session =
        reinterpret_cast<zantetsu::NvencEncoderSession*>(sessionOwner);

    if (session->Close() != zantetsu::NvencEncoderSessionCloseStatus::Closed)
    {
        // The owner stays alive and still holds its session, so the caller's
        // handle remains the same one.
        destination->lastWin32Error = session->LastWin32Error();
        destination->lastNvencStatus = static_cast<int32_t>(session->LastNvencStatus());
        destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED;
        return 1;
    }

    delete session;
    destination->status = ZANTETSU_NVENC_SESSION_V1_STATUS_OK;
    return 1;
}

int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencGetGraphicsObservationV1(
    ZantetsuNvencGraphicsObservationV1* destination,
    uint32_t destinationSize)
{
    if (destination == nullptr ||
        destinationSize != sizeof(ZantetsuNvencGraphicsObservationV1))
    {
        return 0;
    }

    destination->abiVersion = ZANTETSU_NVENC_GRAPHICS_OBSERVATION_V1_VERSION;
    destination->isUnityPluginLoaded =
        g_loaded.load(std::memory_order_acquire) ? 1u : 0u;

    // Presence only: no reference is taken, and no address leaves this
    // function.
    destination->hasCurrentD3D11Device = g_deviceBinding.HasDevice() ? 1u : 0u;

    return 1;
}
