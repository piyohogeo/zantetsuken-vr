// Phase 0.11 NVENC driver API loading boundary.
//
// The NVENC entry points live in nvEncodeAPI64.dll, which is part of the
// installed NVIDIA driver and is never redistributed with this project. This
// owner loads that one module from System32, takes the two entry points it
// needs, asks the driver what API version it supports, and - only if that is at
// least the SDK this build compiled against - obtains the function table.
//
// The module and the table live and die together here: the table is usable only
// while this owner is alive, and the module reference is released exactly once
// when it is destroyed. A load that fails part of the way releases what it took
// and publishes nothing.
//
// No encoder session is opened, no D3D11 device is touched, and no capability
// is queried. Which function pointers in the table must be non-null is decided
// by whoever first calls one, not here.

#ifndef ZANTETSU_NVENC_DRIVER_API_H
#define ZANTETSU_NVENC_DRIVER_API_H

#include <windows.h>

#include <nvEncodeAPI.h>

namespace zantetsu
{
    /// How far the load got. Nothing finer is recorded: the version query is
    /// the only thing that can say "this driver is too old", and everything
    /// else that goes wrong is an observation that could not be completed.
    enum class NvencDriverApiLoadStatus
    {
        Loaded,
        DriverApiUnsupported,
        ObservationFailed,
    };

    /// The version this build asks the driver for, in the form
    /// NvEncodeAPIGetMaxSupportedVersion reports: the four least significant
    /// bits are the minor version and the rest is the major version. This is
    /// not NVENCAPI_VERSION, which is the struct-version encoding.
    constexpr uint32_t kNvencRequiredDriverApiVersion =
        (NVENCAPI_MAJOR_VERSION << 4) | NVENCAPI_MINOR_VERSION;

    /// Compares a reported maximum against what this build requires. Pure, so
    /// the boundary's one judgement can be checked without a driver.
    constexpr bool IsNvencDriverApiVersionSupported(uint32_t maximumSupportedVersion)
    {
        return maximumSupportedVersion >= kNvencRequiredDriverApiVersion;
    }

    class NvencDriverApi
    {
    public:
        NvencDriverApi() = default;
        ~NvencDriverApi();

        NvencDriverApi(const NvencDriverApi&) = delete;
        NvencDriverApi& operator=(const NvencDriverApi&) = delete;

        /// Loads the driver module and obtains the function table, exactly
        /// once per owner. A second call on a loaded owner fails as an
        /// observation that was not made.
        NvencDriverApiLoadStatus Load();

        bool IsLoaded() const { return _loaded; }

        /// The version the driver reported, valid once the query itself
        /// succeeded - which includes the unsupported case.
        uint32_t MaximumSupportedVersion() const { return _maximumSupportedVersion; }

        /// The last Win32 error or NVENCSTATUS behind an ObservationFailed,
        /// kept as the raw value it was.
        DWORD LastWin32Error() const { return _lastWin32Error; }
        NVENCSTATUS LastNvencStatus() const { return _lastNvencStatus; }

        /// The function table, valid only while this owner is alive and only
        /// after Load returned Loaded.
        const NV_ENCODE_API_FUNCTION_LIST& FunctionList() const { return _functionList; }

    private:
        void ReleaseModule();

        HMODULE _module = nullptr;
        NV_ENCODE_API_FUNCTION_LIST _functionList = {};
        uint32_t _maximumSupportedVersion = 0;
        DWORD _lastWin32Error = 0;
        NVENCSTATUS _lastNvencStatus = NV_ENC_SUCCESS;
        bool _loaded = false;
    };
}

#endif
