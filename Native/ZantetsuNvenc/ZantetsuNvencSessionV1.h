// Phase 0.11 native encoder session ABI, version 1.
//
// Eight calls: open one retained session, observe that session's encoder
// capabilities once, initialize that encoder once with the fixed request,
// prepare and release its fixed set of completion events, prepare and release
// its fixed set of output bitstream buffers, and close that exact session.
// Neither the event handles nor the buffers ever cross this boundary. What crosses the boundary is
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

// The project's own category identifiers. They name what this build asks for;
// the mapping from them to the SDK's GUIDs and enumerations happens in one
// place on the native side, and no NVIDIA GUID or SDK enumeration crosses this
// boundary.
#define ZANTETSU_NVENC_CODEC_V1_H264 1u
#define ZANTETSU_NVENC_PROFILE_V1_HIGH 1u
#define ZANTETSU_NVENC_PRESET_V1_P1 1u
#define ZANTETSU_NVENC_TUNING_V1_LOW_LATENCY 1u
#define ZANTETSU_NVENC_RATE_CONTROL_V1_CONSTANT_QP 1u
#define ZANTETSU_NVENC_CHROMA_FORMAT_V1_420 1u
#define ZANTETSU_NVENC_LEVEL_V1_AUTO 1u

/// The fixed encoder request. Every field is one the initialization sets.
typedef struct ZantetsuNvencSessionInitializeRequestV1
{
    uint32_t abiVersion;

    uint32_t codecId;
    uint32_t profileId;
    uint32_t presetId;
    uint32_t tuningId;
    uint32_t rateControlId;
    uint32_t chromaFormatId;
    uint32_t levelId;

    uint32_t encodeWidth;
    uint32_t encodeHeight;
    uint32_t maximumEncodeWidth;
    uint32_t maximumEncodeHeight;
    uint32_t frameRateNumerator;
    uint32_t frameRateDenominator;

    uint32_t enablePictureTypeDecision;
    uint32_t gopLength;
    uint32_t idrPeriod;
    int32_t frameIntervalP;

    uint32_t qpIntra;
    uint32_t qpInterP;
    uint32_t qpInterB;

    uint32_t repeatSequenceAndPictureParameterSets;
    uint32_t outputAccessUnitDelimiter;
    uint32_t disableSequenceAndPictureParameterSets;

    uint32_t progressiveEncoding;
    uint32_t enableEncodeAsync;
    uint32_t enableOutputInVideoMemory;
} ZantetsuNvencSessionInitializeRequestV1;

typedef struct ZantetsuNvencSessionInitializeResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionInitializeResultV1;

/// What a completion-event or output-buffer preparation or release came to.
/// The Win32 error is only ever set by the completion-event calls.
typedef struct ZantetsuNvencSessionCompletionEventResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    uint32_t lastWin32Error;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionCompletionEventResultV1;

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

// Initializes that exact session's encoder with the fixed request, once.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// and the session's one initialization attempt unspent - when the owner handle
// is zero, either pointer is null, either size is wrong, the request's ABI
// version is not this one, a category identifier is undefined, or a boolean
// field is neither zero nor one. An initialization the driver refuses reports
// FAILED with its raw status and leaves the session open, so the caller still
// closes it the ordinary way.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencInitializeSessionEncoderV1(
    uint64_t sessionOwner,
    const ZantetsuNvencSessionInitializeRequestV1* request,
    uint32_t requestSize,
    ZantetsuNvencSessionInitializeResultV1* destination,
    uint32_t destinationSize);

// Creates and registers the fixed set of completion events on that exact
// session's initialized encoder, once. All of them are prepared or none are.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. A preparation that fails reports FAILED with its raw
// Win32 error or NVENCSTATUS; it unwinds what it took, and the session is
// closable again unless a step of that unwinding was itself refused. The event
// handles stay on the native side.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionCompletionEventsV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCompletionEventResultV1* destination,
    uint32_t destinationSize);

// Unregisters and closes that whole set, once and in reverse order.
//
// Returns 1 when the result was written, 0 under the same conditions as the
// preparation. A refused unregister or close reports FAILED and stops there,
// keeping what is still held, so the session is not yet closable.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionCompletionEventsV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCompletionEventResultV1* destination,
    uint32_t destinationSize);

// Creates the fixed set of output bitstream buffers on that exact session's
// initialized encoder, once. All of them are prepared or none are.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. A preparation that fails reports FAILED with its raw
// NVENCSTATUS; it destroys what it made, and the session is closable again
// unless a destroy was itself refused. The buffers stay on the native side.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionOutputBuffersV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCompletionEventResultV1* destination,
    uint32_t destinationSize);

// Destroys that whole set, once and in reverse order.
//
// Returns 1 when the result was written, 0 under the same conditions as the
// preparation. The completion events are released first: a session that still
// has them reports FAILED. A refused destroy stops there, keeping what is
// still held, so the session is not yet closable.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionOutputBuffersV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionCompletionEventResultV1* destination,
    uint32_t destinationSize);

#ifdef __cplusplus
}
#endif

#endif
