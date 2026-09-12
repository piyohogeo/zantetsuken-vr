// Contract test for the current D3D11 device ownership boundary.
//
// It runs against real D3D11 devices - WARP, so no particular adapter is
// needed - and checks one thing above all: a reference taken out of the
// binding stays usable after the binding has been cleared and every other
// reference released. Nothing here races, sleeps, stresses, or reads a
// reference count; each step is ordered by the test itself.
//
// Exit code 0 means every check held. Any failure prints what it was and
// returns non-zero.

#include <cstdio>

#include <d3d11.h>

#include "D3D11DeviceBinding.h"
#include "D3D11ThreadProtection.h"

namespace
{
    int g_failures = 0;

    void Check(bool condition, const char* what)
    {
        if (!condition)
        {
            std::printf("FAILED: %s\n", what);
            ++g_failures;
            return;
        }

        std::printf("  ok: %s\n", what);
    }

    /// One real D3D11 device on the software rasterizer.
    ID3D11Device* CreateWarpDevice(UINT creationFlags = 0)
    {
        ID3D11Device* device = nullptr;
        D3D_FEATURE_LEVEL featureLevel = D3D_FEATURE_LEVEL_11_0;

        const HRESULT hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            creationFlags,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &device,
            &featureLevel,
            nullptr);

        if (FAILED(hr) || device == nullptr)
        {
            std::printf("FAILED: could not create a WARP device (hr 0x%08lX)\n",
                static_cast<unsigned long>(hr));
            ++g_failures;
            return nullptr;
        }

        return device;
    }

    // Uses only CPU-side D3D state on a private WARP device: no worker,
    // rendering, Present, encoding, event, or GPU-completion wait is needed.
    void CheckThreadProtection(UINT creationFlags)
    {
        ID3D11Device* device = CreateWarpDevice(creationFlags);
        if (device == nullptr)
        {
            return;
        }

        ID3D11DeviceContext* context = nullptr;
        device->GetImmediateContext(&context);
        Check(context != nullptr, "the WARP device supplies its immediate context");
        if (context == nullptr)
        {
            device->Release();
            return;
        }

        if ((creationFlags & D3D11_CREATE_DEVICE_SINGLETHREADED) != 0)
        {
            const UINT flagsBefore = device->GetCreationFlags();
            Check((flagsBefore & D3D11_CREATE_DEVICE_SINGLETHREADED) != 0,
                "the rejected WARP device was created SINGLETHREADED");

            // A SINGLETHREADED device may omit this interface entirely. The
            // helper must reject the creation flag before it requires QI.
            ID3D11Multithread* optionalMultithread = nullptr;
            const HRESULT optionalQueryHr = context->QueryInterface(
                __uuidof(ID3D11Multithread), reinterpret_cast<void**>(&optionalMultithread));
            const bool hasProtection = SUCCEEDED(optionalQueryHr) && optionalMultithread != nullptr;
            const BOOL protectionBefore = hasProtection
                ? optionalMultithread->GetMultithreadProtected() : FALSE;

            Check(zantetsu::EnsureD3D11MultithreadProtection(device, context) == E_INVALIDARG,
                "a SINGLETHREADED device is rejected before protection is required");
            Check(device->GetCreationFlags() == flagsBefore,
                "rejecting SINGLETHREADED leaves device creation flags unchanged");
            if (hasProtection)
            {
                Check(optionalMultithread->GetMultithreadProtected() == protectionBefore,
                    "rejecting SINGLETHREADED leaves its protection state unchanged");
            }

            if (optionalMultithread != nullptr) { optionalMultithread->Release(); }
            context->Release();
            device->Release();
            return;
        }

        ID3D11Multithread* multithread = nullptr;
        const HRESULT queryHr = context->QueryInterface(
            __uuidof(ID3D11Multithread), reinterpret_cast<void**>(&multithread));
        Check(SUCCEEDED(queryHr) && multithread != nullptr,
            "the WARP immediate context exposes multithread protection");
        if (FAILED(queryHr) || multithread == nullptr)
        {
            if (multithread != nullptr) { multithread->Release(); }
            context->Release();
            device->Release();
            return;
        }

        // This test owns the device and has no other threads using it, so it
        // can establish a deterministic OFF precondition without affecting
        // another component's shared-device state.
        multithread->SetMultithreadProtected(FALSE);
        Check(!multithread->GetMultithreadProtected(),
            "protection is OFF before the production helper runs");

        Check(FAILED(zantetsu::EnsureD3D11MultithreadProtection(nullptr, context)),
            "a missing device is rejected");
        Check(FAILED(zantetsu::EnsureD3D11MultithreadProtection(device, nullptr)),
            "a missing context is rejected");
        Check(FAILED(zantetsu::EnsureD3D11MultithreadProtection(nullptr, nullptr)),
            "missing device and context are rejected");
        Check(!multithread->GetMultithreadProtected(),
            "rejected missing inputs leave protection OFF");

        Check(SUCCEEDED(zantetsu::EnsureD3D11MultithreadProtection(device, context)),
            "the production helper accepts an initially unprotected context");
        Check(multithread->GetMultithreadProtected() != FALSE,
            "the production helper turns protection ON");
        Check(SUCCEEDED(zantetsu::EnsureD3D11MultithreadProtection(device, context)),
            "repeated protection preparation succeeds");
        Check(multithread->GetMultithreadProtected() != FALSE,
            "repeated preparation leaves protection ON");

        multithread->Release();
        context->Release();
        device->Release();
    }
}

int main()
{
    std::printf("D3D11 device ownership boundary contract\n");

    zantetsu::D3D11DeviceBinding binding;

    // 1. Nothing published yet.
    ID3D11Device* acquired = nullptr;
    Check(!binding.TryAcquireOwned(&acquired), "an empty binding hands out nothing");
    Check(acquired == nullptr, "the failed acquisition left a null pointer");
    Check(!binding.HasDevice(), "an empty binding observes no device");

    ID3D11Device* first = CreateWarpDevice();
    if (first == nullptr)
    {
        return 1;
    }

    // 2. Published, then acquired.
    binding.PublishBorrowed(first);
    Check(binding.HasDevice(), "a published device is observed");

    ID3D11Device* owned = nullptr;
    Check(binding.TryAcquireOwned(&owned), "a published device can be acquired");
    Check(owned == first, "the acquired reference is the exact device published");

    // 3. Replacement: a second device takes the first one's place, and the
    //    reference already handed out is unaffected.
    ID3D11Device* second = CreateWarpDevice();
    if (second == nullptr)
    {
        owned->Release();
        first->Release();
        return 1;
    }

    binding.PublishBorrowed(second);
    Check(binding.HasDevice(), "the replacement is observed");

    ID3D11Device* ownedSecond = nullptr;
    Check(binding.TryAcquireOwned(&ownedSecond), "the replacement can be acquired");
    Check(ownedSecond == second, "the replacement acquisition is the second device");
    if (ownedSecond != nullptr)
    {
        ownedSecond->Release();
    }

    // Back to the first device, so the rest of the test is about it.
    binding.PublishBorrowed(first);
    second->Release();

    // 4. Everything else lets go: the creation reference and the binding's.
    first->Release();
    binding.Clear();
    Check(!binding.HasDevice(), "a cleared binding observes no device");

    // Repeated clears find nothing to release.
    binding.Clear();
    binding.Clear();
    Check(!binding.HasDevice(), "repeated clears leave the binding empty");

    // 5. The reference taken earlier is still usable.
    const D3D_FEATURE_LEVEL level = owned->GetFeatureLevel();
    Check(level != 0, "the acquired reference is still usable after the clear");

    ID3D11DeviceContext* context = nullptr;
    owned->GetImmediateContext(&context);
    Check(context != nullptr, "the acquired reference still serves its device context");
    if (context != nullptr)
    {
        context->Release();
    }

    // 6. A cleared binding hands out nothing again.
    ID3D11Device* afterClear = nullptr;
    Check(!binding.TryAcquireOwned(&afterClear), "a cleared binding hands out nothing");
    Check(afterClear == nullptr, "the failed acquisition after the clear left a null pointer");

    // 7. The one reference this test still owns, released once.
    owned->Release();

    // 8. Worker use of an immediate context requires a compatible device and
    //    shared D3D protection. Exercise the exact helper used by the plugin.
    CheckThreadProtection(0);
    CheckThreadProtection(D3D11_CREATE_DEVICE_SINGLETHREADED);

    if (g_failures != 0)
    {
        std::printf("%d check(s) failed\n", g_failures);
        return 1;
    }

    std::printf("D3D11 device ownership boundary contract: PASSED\n");
    return 0;
}
