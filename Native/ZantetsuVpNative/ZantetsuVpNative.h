// The contract between the Zantetsu VP native draw plugin (Direct3D 12) and its managed side (Zantetsu.Rendering's
// VpNativeDraw*). Plain C structures of fixed layout, mirrored field for field in C#; ZVN_CONTRACT_VERSION is in every
// structure's first field and a mismatch is refused, never guessed at.
//
// The plugin never allocates for the managed side and never frees what it was given. Event data (ZvnDraw) is owned by
// the managed side, which must keep it unchanged until the plugin has set `consumed`: the event runs on the render
// thread, after IssuePluginEventAndData returns.
#pragma once
#include <stdint.h>

#define ZVN_CONTRACT_VERSION 1
#define ZVN_NAME_LENGTH 64
#define ZVN_MAX_VIEWPORTS 4

// What a shader stage binds, as Unity reported it when the Editor compiled the variant (VpNativeShaderBake).
enum ZvnBindingKind
{
    ZvnBinding_ConstantBuffer = 0,   // a root constant buffer view; extra = the buffer's size in bytes
    ZvnBinding_CommandConstant = 1,  // the one root constant the command signature sets per command (VpNativeCommand)
    ZvnBinding_Buffer = 2,           // a shader resource view of a buffer (raw or structured; the draw says which)
    ZvnBinding_Texture = 3,          // a shader resource view of a texture; extra = ZvnTextureDimension
    ZvnBinding_Sampler = 4,          // a static sampler; extra = ZvnSamplerKind, extra2 = anisotropy (0 or 1 = none)
};

enum ZvnStage
{
    ZvnStage_Vertex = 0,
    ZvnStage_Pixel = 1,
};

enum ZvnTextureDimension
{
    ZvnTexture_2D = 0,
    ZvnTexture_Cube = 1,
    ZvnTexture_2DArray = 2,
};

enum ZvnSamplerKind
{
    ZvnSampler_PointClamp = 0,
    ZvnSampler_LinearClamp = 1,
    ZvnSampler_LinearWrap = 2,
    ZvnSampler_TrilinearClamp = 3,
    ZvnSampler_TrilinearWrap = 4,
    ZvnSampler_CompareLinearClampLessEqual = 5,
    ZvnSampler_CompareLinearClampGreaterEqual = 6,
};

enum ZvnCullMode
{
    ZvnCull_None = 1,
    ZvnCull_Front = 2,
    ZvnCull_Back = 3,
};

// D3D12_COMPARISON_FUNC values, repeated so that the managed side needs no D3D header.
enum ZvnCompare
{
    ZvnCompare_Never = 1,
    ZvnCompare_Less = 2,
    ZvnCompare_Equal = 3,
    ZvnCompare_LessEqual = 4,
    ZvnCompare_Greater = 5,
    ZvnCompare_NotEqual = 6,
    ZvnCompare_GreaterEqual = 7,
    ZvnCompare_Always = 8,
};

typedef struct ZvnBinding
{
    uint32_t kind;           // ZvnBindingKind
    uint32_t stage;          // ZvnStage
    uint32_t shaderRegister; // b, t or s register
    uint32_t extra;          // by kind, see ZvnBindingKind
    uint32_t extra2;
    char name[ZVN_NAME_LENGTH];
} ZvnBinding;

// One pipeline: one pass of one shader variant with one draw state, against one target format. Everything a PSO
// needs is here; nothing is read from Unity's state.
typedef struct ZvnPipelineDesc
{
    uint32_t version;              // ZVN_CONTRACT_VERSION
    const void* vertexBytecode;    // DXBC as the Editor compiled it
    uint32_t vertexBytecodeSize;
    const void* pixelBytecode;     // may be null with size 0 for a depth-only pass
    uint32_t pixelBytecodeSize;
    const ZvnBinding* bindings;
    uint32_t bindingCount;
    uint32_t renderTargetFormat;   // DXGI_FORMAT, 0 (UNKNOWN) for no colour target
    uint32_t depthFormat;          // DXGI_FORMAT, 0 for no depth target
    uint32_t sampleCount;          // 1, 2, 4, 8
    uint32_t cullMode;             // ZvnCullMode
    uint32_t frontCounterClockwise;
    uint32_t depthFunc;            // ZvnCompare
    uint32_t depthWrite;           // 0 or 1
    uint32_t colourWrite;          // 0 or 1 (all channels)
    int32_t depthBias;             // D3D12_RASTERIZER_DESC.DepthBias
    float slopeScaledDepthBias;
    float depthBiasClamp;
    uint32_t argumentStride;       // bytes per command in the argument buffer: the command number, then the five indexed arguments (24)
    char name[ZVN_NAME_LENGTH];
} ZvnPipelineDesc;

// One resource bound for one draw, by the index of its binding in the pipeline's list.
typedef struct ZvnResource
{
    uint32_t binding;      // index into ZvnPipelineDesc.bindings
    uint32_t kind;         // 0 raw buffer (ByteAddressBuffer), 1 structured buffer, 2 texture
    void* resource;        // ID3D12Resource*, as GraphicsBuffer/Texture.GetNativeBufferPtr/GetNativeTexturePtr gives it
    uint32_t firstElement;
    uint32_t elementCount; // for a buffer: elements (raw: 4-byte words); for a texture: unused
    uint32_t stride;       // structured: bytes per element
    uint32_t format;       // texture: DXGI_FORMAT of the view (0 = the resource's own)
    uint32_t dimension;    // texture: ZvnTextureDimension
    uint32_t mipLevels;    // texture: 0 = all
    uint32_t arraySize;    // texture: slices for an array view (0 = all)
} ZvnResource;

// One constant buffer for one draw: the bytes `offset` bytes after the start of the ZvnDraw, `size` of them, which the
// plugin copies into its upload ring -- or, with `resource` set, a buffer the GPU wrote (the per-frame constants a
// compute pass assembled from what Unity set), bound as it is at `resourceOffset` (a multiple of 256) with no copy.
typedef struct ZvnConstants
{
    uint32_t binding;
    uint32_t offset;
    uint32_t size;
    void* resource;          // ID3D12Resource*, or null for bytes in the event data
    uint32_t resourceOffset;
} ZvnConstants;

typedef struct ZvnViewport
{
    float x, y, width, height, minDepth, maxDepth;
    int32_t scissorLeft, scissorTop, scissorRight, scissorBottom;
} ZvnViewport;

// One batched issue: the event data of IssuePluginEventAndData. The ZvnResource and ZvnConstants arrays and the
// constant bytes follow it in the same allocation, at the offsets given. The plugin writes `result` and then
// `consumed` (1) when it has recorded the draw or refused it; the managed side reads them after the frame.
typedef struct ZvnDraw
{
    uint32_t version;            // ZVN_CONTRACT_VERSION
    uint32_t pipeline;           // from ZvnCreatePipeline
    void* argumentBuffer;        // ID3D12Resource* of the command entries the selection wrote
    uint32_t argumentOffset;     // bytes: the first command's entry
    uint32_t commandCount;       // commands in this issue (one ExecuteIndirect)
    void* indexBuffer;           // ID3D12Resource*
    uint32_t indexBufferBytes;
    uint32_t indexFormat;        // DXGI_FORMAT_R32_UINT (42) or DXGI_FORMAT_R16_UINT (57)
    uint32_t viewportCount;      // 1..ZVN_MAX_VIEWPORTS, each viewport an ExecuteIndirect of its own with the same commands
    ZvnViewport viewports[ZVN_MAX_VIEWPORTS];
    uint32_t resourceCount;
    uint32_t resourcesOffset;    // bytes from the start of this structure
    uint32_t constantsCount;
    uint32_t constantsOffset;
    uint32_t totalBytes;         // of the whole allocation
    uint32_t serial;             // the managed side's own number for this issue, echoed in counters
    volatile uint32_t result;    // ZvnResult, written by the plugin
    volatile uint32_t consumed;  // 0 until the plugin has handled this draw
} ZvnDraw;

enum ZvnResult
{
    ZvnResult_Recorded = 0,
    ZvnResult_NotAvailable = 1,      // no device
    ZvnResult_BadVersion = 2,
    ZvnResult_NoPipeline = 3,
    ZvnResult_NoCommandList = 4,     // Unity gave no recording state
    ZvnResult_RingFull = 5,          // the upload or descriptor ring has no free region (the frame fence has not passed)
    ZvnResult_BadResource = 6,       // a binding the pipeline needs was not given, or a resource is null
    ZvnResult_BadConstants = 7,      // a constant buffer's bytes do not fit the binding
};

// Counters the managed side reads for its records.
typedef struct ZvnCounters
{
    uint32_t version;
    uint64_t drawsRecorded;          // ExecuteIndirect calls recorded
    uint64_t commandsRecorded;       // commands those calls covered
    uint64_t drawsRefused;
    uint32_t lastRefusal;            // ZvnResult of the last refusal
    uint32_t lastRefusalSerial;
    uint64_t constantBytesUploaded;
    uint64_t descriptorsWritten;
    uint32_t ringRegions;            // regions the rings hold
    uint32_t ringRegionBytes;
    uint64_t sentinelsRun;           // sentinel events run (each marks its block consumed and does nothing else)
    uint64_t eventsHeld;             // events whose consumed mark was withheld by the test control (ZvnHoldConsumption)
} ZvnCounters;
