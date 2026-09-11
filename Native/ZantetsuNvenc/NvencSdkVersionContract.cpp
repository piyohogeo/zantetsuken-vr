// Phase 0.11 NVENC SDK version contract.
//
// Including the official header and asserting its version at compile time is
// the whole purpose of this translation unit: if the SDK the build was pointed
// at is not 13.0, this file does not compile. Nothing is declared, copied, or
// wrapped from the header, and no NVENC entry point is referenced.

#include <nvEncodeAPI.h>

static_assert(
    NVENCAPI_MAJOR_VERSION == 13,
    "Phase 0.11 requires NVIDIA Video Codec SDK 13.0: major version must be 13.");

static_assert(
    NVENCAPI_MINOR_VERSION == 0,
    "Phase 0.11 requires NVIDIA Video Codec SDK 13.0: minor version must be 0.");

namespace
{
    // Keeps this translation unit from being empty.
    const bool kNvencSdkVersionContractCompiled = true;
}
