// Phase 0.11 current D3D11 device ownership boundary.
//
// One place holds the device Unity is currently using, and one place hands out
// independent references to it. Both matter: a reader that only loaded a raw
// pointer and then addrefed it could be addrefing an object the lifecycle had
// already cleared and released. Here the read and the AddRef happen together
// under a shared lock, while a publish or a clear swaps the pointer under an
// exclusive one and releases the old reference only after that lock is gone.
//
// A reference handed out by TryAcquireOwned is the caller's own: it stays valid
// for as long as the caller keeps it, whatever the lifecycle does afterwards,
// and the caller releases it exactly once. No narrower lifetime is implied.
//
// No lock is ever held across a COM Release, an NVENC call, a wait, an
// allocation, logging, or a managed callback.

#ifndef ZANTETSU_NVENC_D3D11_DEVICE_BINDING_H
#define ZANTETSU_NVENC_D3D11_DEVICE_BINDING_H

#include <d3d11.h>
#include <windows.h>

namespace zantetsu
{
    class D3D11DeviceBinding
    {
    public:
        D3D11DeviceBinding() = default;

        D3D11DeviceBinding(const D3D11DeviceBinding&) = delete;
        D3D11DeviceBinding& operator=(const D3D11DeviceBinding&) = delete;

        /// Publishes the device the caller borrowed from Unity. This binding
        /// takes its own reference; a null device is a clear.
        void PublishBorrowed(ID3D11Device* device);

        /// Drops the held device. Whatever was held is released exactly once.
        void Clear();

        /// Hands back an independent reference to the held device, or false
        /// and a null pointer when none is held. A caller that receives true
        /// releases that reference exactly once.
        bool TryAcquireOwned(ID3D11Device** device) const;

        /// Whether a device is currently held. Presence only.
        bool HasDevice() const;

    private:
        mutable SRWLOCK _lock = SRWLOCK_INIT;
        ID3D11Device* _device = nullptr;
    };
}

#endif
