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
// The session exports open and close one retained encoder session against that
// same device. The session owner keeps its own reference, so it is not affected
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
