#ifndef ZANTETSU_NVENC_D3D11_THREAD_PROTECTION_H
#define ZANTETSU_NVENC_D3D11_THREAD_PROTECTION_H

#include <d3d11_4.h>

namespace zantetsu
{
    // Called with the device and its immediate context before worker NVENC
    // calls can overlap rendering. NVENC may use that context internally.
    // Protection belongs to the shared device, so it remains enabled after
    // this call and after the encoder session closes.
    inline HRESULT EnsureD3D11MultithreadProtection(
        ID3D11Device* device, ID3D11DeviceContext* context)
    {
        if (device == nullptr || context == nullptr ||
            (device->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED) != 0)
        {
            return E_INVALIDARG;
        }

        ID3D11Multithread* multithread = nullptr;
        const HRESULT hr = context->QueryInterface(
            __uuidof(ID3D11Multithread), reinterpret_cast<void**>(&multithread));
        if (FAILED(hr) || multithread == nullptr)
        {
            if (multithread != nullptr) { multithread->Release(); }
            return FAILED(hr) ? hr : E_NOINTERFACE;
        }

        // Set returns the previous state, not a success indication.
        multithread->SetMultithreadProtected(TRUE);
        const bool enabled = multithread->GetMultithreadProtected() != FALSE;
        multithread->Release();
        return enabled ? S_OK : E_FAIL;
    }
}

#endif
