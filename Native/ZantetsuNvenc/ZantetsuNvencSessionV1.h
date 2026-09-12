// Phase 0.11 native encoder session ABI, version 1.
//
// Ten calls: open one retained session, observe that session's encoder
// capabilities once, initialize that encoder once with the fixed request,
// prepare and release its fixed sets of NV12 input surfaces, output bitstream
// buffers, and completion events, and close that exact session. None of the
// textures, registrations, event handles, or buffers ever cross this boundary. What crosses the boundary is
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

// Only a conversion collection reports this: the command has not completed yet
// and was not consumed - it stays outstanding, its slot does not go idle, and
// the same generation is collected again to come back for it. It is not a
// failure and carries no raw value: nothing is invented to describe something
// that simply has not happened.
#define ZANTETSU_NVENC_SESSION_V1_STATUS_PENDING 4u

// A submit or an output collection reports this for the one controllable
// outcome that is not a completion: a map the driver refused without taking
// anything, or a bitstream with nothing usable to copy that was nevertheless
// unlocked and unmapped safely. The caller can report an ordinary failure and
// carry on; nothing is in an unknown state.
#define ZANTETSU_NVENC_SESSION_V1_STATUS_NOT_SUBMITTED 5u

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

/// What a completion-event preparation or release came to. Creating and
/// closing the event handles is the OS's work, so a Win32 error belongs here.
typedef struct ZantetsuNvencSessionCompletionEventResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    uint32_t lastWin32Error;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionCompletionEventResultV1;

/// How many source RGBA surfaces one session binds. Fixed, and separate from
/// the encode sample slots: the two sets are bound to each other only while a
/// frame is in flight, which is not this ABI's business.
#define ZANTETSU_NVENC_SESSION_V1_SOURCE_SURFACE_COUNT 8

/// The fixed set of source surfaces, handed over in one request.
///
/// Each entry is an ID3D11Resource* the caller already owns - what the graphics
/// contract promises for a native texture pointer - passed as an integer so no
/// COM type crosses the boundary. The session asks that resource for the 2D
/// interface it needs and keeps the reference that query returns; the caller's
/// own ownership is untouched. Nothing is returned about them.
typedef struct ZantetsuNvencSessionSourceSurfaceRequestV1
{
    uint32_t abiVersion;
    uint32_t surfaceCount;
    uint64_t surfaces[ZANTETSU_NVENC_SESSION_V1_SOURCE_SURFACE_COUNT];
} ZantetsuNvencSessionSourceSurfaceRequestV1;

/// What a source surface binding or release came to. Everything here is D3D11's
/// work - a resource that is not a 2D texture, a descriptor that is not the
/// fixed RGBA8 source, a texture from another device, or a view the device
/// refused - so an HRESULT is the only raw value there is, and only a call that
/// actually failed sets it. A descriptor this session does not accept fails no
/// call, and leaves the HRESULT at zero.
typedef struct ZantetsuNvencSessionSourceSurfaceResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastHResult;
} ZantetsuNvencSessionSourceSurfaceResultV1;

/// What one attempt to submit a picture came to.
///
/// OK means the encoder accepted it. NOT_SUBMITTED is the narrow controllable
/// case - the map was refused and gave nothing back, so nothing changed hands.
/// FAILED means what this process owns is no longer known, and nothing was
/// unmapped on a guess. The driver's own status is the only raw value.
typedef struct ZantetsuNvencSessionSubmitResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionSubmitResultV1;

/// What one attempt to collect a submitted picture's bitstream came to.
///
/// OK means the access unit was copied and both the lock and the map were given
/// back; the length is then 1..destinationCapacity. NOT_SUBMITTED is used here
/// for the controllable rejection - nothing usable to copy, with the lock and
/// the map given back safely, so the slot is usable again. FAILED means the
/// wait, the lock, the unlock, or the unmap did not resolve. No pointer and no
/// slot state is reported: only the length and the raw values.
typedef struct ZantetsuNvencSessionOutputResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    uint32_t validLength;
    uint32_t lastWin32Error;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionOutputResultV1;

/// How many GPU conversion command slots one session prepares. Fixed, and its
/// own count again: a command binds a source to a sample slot for one frame.
#define ZANTETSU_NVENC_SESSION_V1_CONVERSION_COMMAND_COUNT 8

/// What a conversion command preparation, release, cancellation, or collection
/// came to. This boundary is both D3D11 work - devices, fences, drawing - and
/// Win32 handle work, so both raw values are here, and only the kind of call
/// that actually failed sets one. A collection reports the HRESULT of the
/// callback it waited for, not some earlier unrelated failure of the session.
typedef struct ZantetsuNvencSessionConversionResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastHResult;
    uint32_t lastWin32Error;
} ZantetsuNvencSessionConversionResultV1;

/// One conversion to arm: which source, which encode sample slot, which sync
/// slot, and the generation that is also the fence value it will signal.
typedef struct ZantetsuNvencSessionConversionArmRequestV1
{
    uint32_t abiVersion;
    uint32_t syncSlotIndex;
    uint32_t sourceSlotIndex;
    uint32_t sampleSlotIndex;
    uint64_t generation;
} ZantetsuNvencSessionConversionArmRequestV1;

/// What an arming came to, and the opaque event data pointer the caller issues
/// the render event with. The pointer names memory inside the command slot; it
/// is not allocated per arming, is valid until that command's completion is
/// collected, and is not something to read, write, or free.
typedef struct ZantetsuNvencSessionConversionArmResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastHResult;
    int32_t reserved;
    uint64_t eventData;
} ZantetsuNvencSessionConversionArmResultV1;

/// What an NV12 input surface preparation or release came to. Creating the
/// textures is D3D11's work and registering them is the driver's, so both raw
/// values are here - and only the call that actually failed sets one.
typedef struct ZantetsuNvencSessionInputSurfaceResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastHResult;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionInputSurfaceResultV1;

/// What an output bitstream buffer preparation or release came to. The driver
/// makes and destroys these, so its status is the only failure value there is.
typedef struct ZantetsuNvencSessionOutputBufferResultV1
{
    uint32_t abiVersion;
    uint32_t status;
    int32_t lastNvencStatus;
} ZantetsuNvencSessionOutputBufferResultV1;

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

// Binds the fixed set of source RGBA surfaces to that exact session, once, and
// creates the shader resource view each one is read through. All of them are
// bound or none are.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when either pointer is null, either size is not exactly its struct's, the
// owner handle is zero, the request's ABI version is not this one, its surface
// count is not the fixed one, or any surface is null. A binding that fails
// reports FAILED with the raw HRESULT of the D3D11 call that failed - the
// interface query or the view - or zero when a descriptor simply was not the
// accepted one; it releases what it took, in reverse. The encoder must already be initialized, and nothing else may be
// prepared yet.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencBindSessionSourceSurfacesV1(
    uint64_t sessionOwner,
    const ZantetsuNvencSessionSourceSurfaceRequestV1* request,
    uint32_t requestSize,
    ZantetsuNvencSessionSourceSurfaceResultV1* destination,
    uint32_t destinationSize);

// Releases that whole set - each view, then each reference - once and in
// reverse order.
//
// Returns 1 when the result was written, 0 under the same conditions as the
// binding. The NV12 input surfaces, output buffers, and completion events are
// released first: a session that still has any of them reports FAILED without
// spending the release.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionSourceSurfacesV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionSourceSurfaceResultV1* destination,
    uint32_t destinationSize);

// Creates the fixed set of NV12 input surfaces on that exact session's
// initialized encoder and registers each one, once. All of them are prepared
// or none are.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. A preparation that fails reports FAILED with the raw
// HRESULT or NVENCSTATUS of the call that failed; it unwinds what it took, and
// the session is closable again unless a step of that unwinding was itself
// refused. The textures and registrations stay on the native side.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionInputSurfacesV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionInputSurfaceResultV1* destination,
    uint32_t destinationSize);

// Unregisters and releases that whole set, once and in reverse order.
//
// Returns 1 when the result was written, 0 under the same conditions as the
// preparation. The completion events and output buffers are released first: a
// session that still has either reports FAILED. A refused unregister stops
// there, keeping what is still held, so the session is not yet closable.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionInputSurfacesV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionInputSurfaceResultV1* destination,
    uint32_t destinationSize);

// Maps one sample slot's registered input and submits exactly one picture for
// it, once per sample generation.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. Only the slot and a positive generation are named; no
// token, frame id, pointer, or handle crosses this call, and every per-picture
// value is fixed by the profile. A refused map that gave nothing back reports
// NOT_SUBMITTED; anything that leaves ownership unknown reports FAILED and
// unmaps nothing on a guess.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencSubmitSessionEncodePictureV1(
    uint64_t sessionOwner,
    uint32_t sampleSlotIndex,
    uint64_t generation,
    ZantetsuNvencSessionSubmitResultV1* destination,
    uint32_t destinationSize);

// Waits for that picture, copies its access unit into the caller's storage, and
// gives the lock and the map back.
//
// Returns 1 when the result was written, 0 under the same conditions as the
// submit, or when the storage is null or its capacity is zero. This blocks on
// the slot's completion event: it is the output worker's call and no other
// thread's. The storage is written at most once and only when everything after
// it also succeeded.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencCopySessionCompletedOutputV1(
    uint64_t sessionOwner,
    uint32_t sampleSlotIndex,
    uint64_t generation,
    uint8_t* destination,
    uint32_t destinationCapacity,
    ZantetsuNvencSessionOutputResultV1* result,
    uint32_t resultSize);

// Creates the fixed set of GPU conversion command slots on that exact session,
// once: the device and context interfaces the signalling needs, then a fence
// and an auto-reset event per slot. All of them are prepared or none are.
//
// Returns 1 when the result was written, 0 - leaving the destination untouched
// - when the destination is null, its size is not exactly the struct's, or the
// owner handle is zero. The encoder must be initialized, the sources bound, and
// the input surfaces and conversion pipeline prepared. A preparation that fails
// reports FAILED with the raw HRESULT of the call that failed and releases what
// it took, in reverse.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencPrepareSessionConversionCommandsV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionConversionResultV1* destination,
    uint32_t destinationSize);

// Releases that whole set in reverse order, once.
//
// Returns 1 when the result was written, 0 under the same conditions as the
// preparation. The output buffers and completion events are released first, and
// no command may be armed, running, or waiting to be collected: a session that
// fails either condition reports FAILED without spending the release.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencReleaseSessionConversionCommandsV1(
    uint64_t sessionOwner,
    ZantetsuNvencSessionConversionResultV1* destination,
    uint32_t destinationSize);

// Returns the render event callback this plugin wants issued for a conversion,
// as an integer. The caller passes it to the graphics API that issues plugin
// events, together with the event data an arming returned, and keeps both to
// itself.
uint64_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencGetConversionEventCallbackV1(void);

// Arms exactly one conversion command and hands back the event data pointer to
// issue it with.
//
// Returns 1 when the result was written, 0 when either pointer is null, either
// size is wrong, the owner handle is zero, or the request's ABI version is not
// this one. An arming that is refused - an index out of range, something not
// prepared, a slot that is not idle, or a generation that is not newer than
// that slot's last - reports FAILED with a zero event data pointer and changes
// nothing.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencArmSessionConversionCommandV1(
    uint64_t sessionOwner,
    const ZantetsuNvencSessionConversionArmRequestV1* request,
    uint32_t requestSize,
    ZantetsuNvencSessionConversionArmResultV1* destination,
    uint32_t destinationSize);

// Takes back an armed command whose render event could not be issued. Only a
// command whose callback has not started is taken back; one already running is
// kept, because it will signal, and that is reported as FAILED.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencCancelSessionConversionCommandV1(
    uint64_t sessionOwner,
    uint32_t syncSlotIndex,
    uint64_t generation,
    ZantetsuNvencSessionConversionResultV1* destination,
    uint32_t destinationSize);

// Waits for exactly one armed command and returns its slot to idle. Called from
// a worker: it touches the command's two events and its fence only.
//
// The callback's completion is waited for first, because that is what publishes
// the result and the only edge a callback that never signalled arrives on; the
// fence is waited for after it, and only when the callback succeeded. Both
// share one deadline.
//
// A completion reports OK and is the only outcome that returns the slot to use.
// A command that has not published, or a fence that has not reached it in the
// time allowed, reports PENDING: the command is not consumed, the slot keeps
// its generation, and collecting that same generation again is how the caller
// comes back for it. Everything else reports FAILED
// with that callback's own HRESULT or the raw Win32 error: what the callback
// recorded, a refused registration, a failed wait, a refused reset - and a
// caller asking about a command that is not there, which is a broken contract
// rather than something still to come.
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL
ZantetsuNvencCollectSessionConversionCommandV1(
    uint64_t sessionOwner,
    uint32_t syncSlotIndex,
    uint64_t generation,
    uint32_t timeoutMilliseconds,
    ZantetsuNvencSessionConversionResultV1* destination,
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
    ZantetsuNvencSessionOutputBufferResultV1* destination,
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
    ZantetsuNvencSessionOutputBufferResultV1* destination,
    uint32_t destinationSize);

#ifdef __cplusplus
}
#endif

#endif
