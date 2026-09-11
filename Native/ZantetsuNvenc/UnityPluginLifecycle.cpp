// Phase 0.11 Unity plugin lifecycle and current D3D11 device binding.
//
// This translation unit does one thing: while Unity's current renderer is
// D3D11, it holds a COM reference to the exact ID3D11Device that Unity is
// using, taken from IUnityGraphicsD3D11 at the device events Unity raises, and
// releases it again when Unity is about to reset or shut that device down.
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

#include <atomic>

#include <d3d11.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IUnityInterface.h"

namespace
{
    // Borrowed from Unity for the plugin's lifetime; no COM ownership is
    // claimed over the interface pointers themselves.
    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;

    // The one device reference this plugin owns. Published and cleared by a
    // single exchange, so a reader never sees a half-written pointer and the
    // reference is released exactly once by whoever takes it out.
    std::atomic<ID3D11Device*> g_device{nullptr};

    /// Publishes a device reference and releases whatever it replaced, exactly
    /// once. The incoming pointer is already addref'd by the caller.
    void PublishDevice(ID3D11Device* device)
    {
        ID3D11Device* previous = g_device.exchange(device, std::memory_order_acq_rel);
        if (previous != nullptr)
        {
            previous->Release();
        }
    }

    /// Takes the held device out and releases it exactly once. A second call
    /// finds nothing to release.
    void ClearDevice()
    {
        PublishDevice(nullptr);
    }

    /// Acquires the device Unity is currently using, but only while the
    /// current renderer is D3D11.
    void AcquireCurrentDevice()
    {
        if (g_unityInterfaces == nullptr || g_unityGraphics == nullptr)
        {
            return;
        }

        if (g_unityGraphics->GetRenderer() != kUnityGfxRendererD3D11)
        {
            return;
        }

        IUnityGraphicsD3D11* d3d11 =
            g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (d3d11 == nullptr)
        {
            return;
        }

        ID3D11Device* device = d3d11->GetDevice();
        if (device == nullptr)
        {
            return;
        }

        // The exact pointer Unity handed back is the one that is kept.
        device->AddRef();
        PublishDevice(device);
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
                ClearDevice();
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
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_unityGraphics != nullptr)
    {
        g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    }

    ClearDevice();

    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
}
