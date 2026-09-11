#include "D3D11DeviceBinding.h"

namespace zantetsu
{
    void D3D11DeviceBinding::PublishBorrowed(ID3D11Device* device)
    {
        // The new reference is taken before the lock, so nothing but the
        // pointer swap happens while writers are excluded.
        if (device != nullptr)
        {
            device->AddRef();
        }

        ID3D11Device* previous = nullptr;

        AcquireSRWLockExclusive(&_lock);
        previous = _device;
        _device = device;
        ReleaseSRWLockExclusive(&_lock);

        // Released outside the lock: a reference that was handed out under the
        // shared lock before this swap keeps the object alive regardless.
        if (previous != nullptr)
        {
            previous->Release();
        }
    }

    void D3D11DeviceBinding::Clear()
    {
        PublishBorrowed(nullptr);
    }

    bool D3D11DeviceBinding::TryAcquireOwned(ID3D11Device** device) const
    {
        if (device == nullptr)
        {
            return false;
        }

        *device = nullptr;

        ID3D11Device* acquired = nullptr;

        // Read and AddRef together: a clear cannot take the object away
        // between the two.
        AcquireSRWLockShared(&_lock);
        acquired = _device;
        if (acquired != nullptr)
        {
            acquired->AddRef();
        }
        ReleaseSRWLockShared(&_lock);

        if (acquired == nullptr)
        {
            return false;
        }

        *device = acquired;
        return true;
    }

    bool D3D11DeviceBinding::HasDevice() const
    {
        AcquireSRWLockShared(&_lock);
        const bool present = _device != nullptr;
        ReleaseSRWLockShared(&_lock);
        return present;
    }
}
