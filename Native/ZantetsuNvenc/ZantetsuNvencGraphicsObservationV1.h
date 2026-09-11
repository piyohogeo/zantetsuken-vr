// Phase 0.11 native graphics observation ABI, version 1.
//
// This is the whole surface the managed side sees. It carries three fixed-width
// values and no more: the ABI version, whether UnityPluginLoad has run, and
// whether the plugin currently holds a D3D11 device. Booleans are uint32_t with
// the value 0 or 1.
//
// No NVIDIA type, Unity type, COM pointer, raw address, adapter identity,
// vendor, driver model, NVENC capability, device health, status code, error
// message, generation, timestamp, or callback count crosses this boundary.
// Holding a device means only that Unity's renderer was D3D11 at a device event
// and that a device was returned then; nothing further is implied.

#ifndef ZANTETSU_NVENC_GRAPHICS_OBSERVATION_V1_H
#define ZANTETSU_NVENC_GRAPHICS_OBSERVATION_V1_H

#include <stdint.h>

#if defined(_WIN32)
#define ZANTETSU_NVENC_API __declspec(dllexport)
#define ZANTETSU_NVENC_CALL __stdcall
#else
#define ZANTETSU_NVENC_API
#define ZANTETSU_NVENC_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define ZANTETSU_NVENC_GRAPHICS_OBSERVATION_V1_VERSION 1u

typedef struct ZantetsuNvencGraphicsObservationV1
{
    uint32_t abiVersion;
    uint32_t isUnityPluginLoaded;
    uint32_t hasCurrentD3D11Device;
} ZantetsuNvencGraphicsObservationV1;

// Writes one observation snapshot.
//
// Returns 1 when the snapshot was written. Returns 0 - leaving the destination
// untouched - when the destination is null or its size is not exactly
// sizeof(ZantetsuNvencGraphicsObservationV1).
int32_t ZANTETSU_NVENC_API ZANTETSU_NVENC_CALL ZantetsuNvencGetGraphicsObservationV1(
    ZantetsuNvencGraphicsObservationV1* destination,
    uint32_t destinationSize);

#ifdef __cplusplus
}
#endif

#endif
