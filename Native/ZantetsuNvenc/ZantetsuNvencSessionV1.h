// Phase 0.11 native encoder session ABI, version 1.
//
// Three calls: open one retained session, observe that session's encoder
// capabilities once, and close that exact session. What crosses the boundary is
// fixed-width and opaque - a status, an opaque owner handle, the observed
// capability values, and the raw failure values behind a Failed - and never a
// device pointer, an encoder handle, a function table pointer, a GUID, an
// adapter name, or a driver string.
//
// The graphics observation ABI is a separate version 1 and is unchanged by
// this one.

#ifndef ZANTETSU_NVENC_SESSION_V1_H
#define ZANTETSU_NVENC_SESSION_V1_H

#include <stdint.h>

#include "ZantetsuNvencGraphicsObservationV1.h"

#ifdef __cplusplus
extern "C" {
#endif

#define ZANTETSU_NVENC_SESSION_V1_VERSION 1u

// Status values shared by both calls.
#define ZANTETSU_NVENC_SESSION_V1_STATUS_OK 1u
#define ZANTETSU_NVENC_SESSION_V1_STATUS_UNSUPPORTED 2u
#define ZANTETSU_NVENC_SESSION_V1_STATUS_FAILED 3u

typedef struct ZantetsuNvencSessionOpenResultV1
{
    uint32_t abiVersion;
    uint32_t status;

    /// The opened session's owner, opaque to the caller. Non-zero only when
    /// the status is OK, and the only value the close call accepts.
    uint64_t sessionOwner;

    uint32_t lastWin32Error;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionOpenResultV1;

typedef struct ZantetsuNvencSessionCloseResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    uint32_t lastWin32Error;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionCloseResultV1;

/// What the open session reports about its encoder. Booleans are 0 or 1, and
/// the maximums are as observed.
typedef struct ZantetsuNvencSessionCapabilityResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    uint32_t supportsAsyncEncode;
    uint32_t supportsH264Encode;
    uint32_t supportsH264HighProfile;
    uint32_t supportsNv12Input;
    int32_t maximumEncodeWidth;
    int32_t maximumEncodeHeight;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionCapabilityResultV1;

// Opens one retained encoder session on the D3D11 device the plugin currently
// holds.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null or its size is not exactly the struct's.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencOpenSessionV1(
    ZantetsuNvencSessionOpenResultV1* destination,
    uint32_t destinationSize);

// Closes that exact session, once.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. A close the driver refuses reports FAILED and leaves
// the session owner alive, so the same handle is still the caller's.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencCloseSessionV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCloseResultV1* destination,
    uint32_t destinationSize);

// Observes that exact session's encoder capabilities, once.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. An observation that could not be completed reports
// FAILED and leaves the session open, so the caller still closes it the
// ordinary way.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencObserveSessionCapabilityV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCapabilityResultV1* destination,
    uint32_t destinationSize);

#ifdef __cplusplus
}
#endif

#endif
