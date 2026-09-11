// Contract test for the NVENC driver API loading boundary.
//
// It runs against the NVIDIA driver installed on this machine, so it needs
// NVIDIA hardware and driver and is not part of an ordinary build. Nothing here
// opens an encoder session, touches a D3D11 device, or queries a capability.
//
// Exit code 0 means every check held. Any failure prints what it was and
// returns non-zero.

#include <cstdio>

#include "NvencDriverApi.h"

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

    const char* StatusName(zantetsu::NvencDriverApiLoadStatus status)
    {
        switch (status)
        {
            case zantetsu::NvencDriverApiLoadStatus::Loaded:
                return "Loaded";
            case zantetsu::NvencDriverApiLoadStatus::DriverApiUnsupported:
                return "DriverApiUnsupported";
            case zantetsu::NvencDriverApiLoadStatus::ObservationFailed:
                return "ObservationFailed";
            default:
                return "?";
        }
    }
}

int main()
{
    std::printf("NVENC driver API loading boundary contract\n");
    std::printf("  required version: %u.%u\n",
        static_cast<unsigned>(zantetsu::kNvencRequiredDriverApiVersion >> 4),
        static_cast<unsigned>(zantetsu::kNvencRequiredDriverApiVersion & 0xF));

    // The version comparison itself, which needs no driver.
    Check(
        zantetsu::IsNvencDriverApiVersionSupported(
            zantetsu::kNvencRequiredDriverApiVersion),
        "the required version is supported by itself");
    Check(
        !zantetsu::IsNvencDriverApiVersionSupported(
            zantetsu::kNvencRequiredDriverApiVersion - 1),
        "one step below the required version is not supported");
    Check(
        zantetsu::IsNvencDriverApiVersionSupported(
            zantetsu::kNvencRequiredDriverApiVersion + 1),
        "a newer version than required is supported");

    {
        zantetsu::NvencDriverApi driverApi;
        Check(!driverApi.IsLoaded(), "a fresh owner holds nothing");

        const zantetsu::NvencDriverApiLoadStatus status = driverApi.Load();
        std::printf("  load status: %s, driver maximum version: %u.%u\n",
            StatusName(status),
            static_cast<unsigned>(driverApi.MaximumSupportedVersion() >> 4),
            static_cast<unsigned>(driverApi.MaximumSupportedVersion() & 0xF));

        if (status != zantetsu::NvencDriverApiLoadStatus::Loaded)
        {
            std::printf(
                "FAILED: the driver API did not load (win32 error %lu, NVENCSTATUS %d)\n",
                static_cast<unsigned long>(driverApi.LastWin32Error()),
                static_cast<int>(driverApi.LastNvencStatus()));
            return 1;
        }

        Check(driverApi.IsLoaded(), "the owner reports itself loaded");
        Check(
            zantetsu::IsNvencDriverApiVersionSupported(
                driverApi.MaximumSupportedVersion()),
            "the driver supports at least the version this build requires");
        Check(
            driverApi.FunctionList().version == NV_ENCODE_API_FUNCTION_LIST_VER,
            "the function table carries the version this build asked for");

        // One owner, one load.
        Check(
            driverApi.Load() == zantetsu::NvencDriverApiLoadStatus::ObservationFailed,
            "a second load on the same owner is refused");
        Check(driverApi.IsLoaded(), "the refused second load left the owner loaded");
    }

    // The first owner is gone, module reference and all. A new one loads the
    // driver again from scratch.
    {
        zantetsu::NvencDriverApi driverApi;
        Check(
            driverApi.Load() == zantetsu::NvencDriverApiLoadStatus::Loaded,
            "a new owner loads the driver API again");
        Check(driverApi.IsLoaded(), "the new owner holds its own function table");
    }

    if (g_failures != 0)
    {
        std::printf("%d check(s) failed\n", g_failures);
        return 1;
    }

    std::printf("NVENC driver API loading boundary contract: PASSED\n");
    return 0;
}
