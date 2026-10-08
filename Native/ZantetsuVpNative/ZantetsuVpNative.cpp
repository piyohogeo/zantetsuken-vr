// Zantetsu VP native draw plugin (Direct3D 12): one ExecuteIndirect for many indirect draw commands (DESIGN 4.5.8).
//
// Unity 6000.3.22f1 issues Graphics.RenderPrimitivesIndexedIndirect's commands one native draw each, with state set
// between them (measured: 8,010 ExecuteIndirect of one command a frame for 1,602 commands in five passes). This plugin
// records, into the command list Unity is recording, ONE ExecuteIndirect for a run of commands that share a draw
// state, with pipeline state of its own -- a root signature and a PSO made from shader bytecode the Editor compiled
// from the product's own shaders, bound as Unity reported the variant binds -- and the buffers the product already
// holds on the GPU. The command's number reaches the shader as a root constant the command signature sets per command
// from the command's own 24-byte argument entry; no CPU work is done per command.
//
// Nothing here guesses at Unity's internal state: the device, the command list being recorded and the resource states
// come through IUnityGraphicsD3D12v8 only; the render target is whatever Unity has bound (the event is configured to
// have it bound). A graphics device that is not Direct3D 12, or an interface this was not written for, is an explicit
// "not available" with its reason -- never a silent change of route (the managed side decides, and records).
//
// Lifetime: constants are copied into an upload ring and descriptors written into a shader-visible ring, both divided
// into regions stamped with the frame fence value of the frame that used them; a region is reused only when that
// fence has passed, and a frame that finds no free region is refused and counted -- it never waits. A pipeline
// released is kept until the fence of the frame that last used it has passed. The event data belongs to the managed
// side, which keeps it until `consumed` is set here.
//
// Threads: ZvnCreatePipeline/ZvnReleasePipeline run on the main thread; the event runs on Unity's rendering thread
// (DontCare queue access). The shared state is behind one mutex.

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dxgi.h>
#include <d3d12.h>
#include <d3dcompiler.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"

#include "ZantetsuVpNative.h"

namespace
{
    // The one event id the managed side issues; configured once the device exists.
    const int kDrawEventId = 0x5A560001;
    const int kSentinelEventId = 0x5A560002;
    const uint32_t kDefaultRegions = 4;
    const uint32_t kDefaultRegionBytes = 256 * 1024;
    const uint32_t kDefaultRegionDescriptors = 1024;
    const uint32_t kConstantAlignment = 256;

    template <typename T>
    void Release(T*& object)
    {
        if (object != nullptr)
        {
            object->Release();
            object = nullptr;
        }
    }

    void Append(std::string& text, const char* format, ...)
    {
        char line[1024];
        va_list arguments;
        va_start(arguments, format);
        vsnprintf(line, sizeof(line), format, arguments);
        va_end(arguments);
        text += line;
    }

    int CopyOut(const std::string& text, char* out, int outSize)
    {
        if (out != nullptr && outSize > 0)
        {
            size_t n = text.size() < (size_t)(outSize - 1) ? text.size() : (size_t)(outSize - 1);
            memcpy(out, text.data(), n);
            out[n] = 0;
        }

        return (int)text.size();
    }

    struct RootParameterUse
    {
        uint32_t binding;    // index into the pipeline's bindings
        uint32_t parameter;  // root parameter index
    };

    struct DescriptorTable
    {
        ZvnStage stage;
        uint32_t parameter;
        std::vector<uint32_t> bindings; // in table order
    };

    struct Pipeline
    {
        std::string name;
        std::vector<ZvnBinding> bindings;
        ID3D12RootSignature* rootSignature = nullptr;
        ID3D12PipelineState* pipelineState = nullptr;
        ID3D12CommandSignature* commandSignature = nullptr;
        std::vector<RootParameterUse> constantBuffers;
        std::vector<DescriptorTable> tables;
        uint32_t commandConstantParameter = UINT32_MAX;
        uint32_t argumentStride = 24;
        uint64_t lastUsedFence = 0;
        bool released = false;
        bool alive = false;

        void Destroy()
        {
            Release(commandSignature);
            Release(pipelineState);
            Release(rootSignature);
            alive = false;
        }
    };

    struct Region
    {
        uint64_t fence = 0;     // the frame fence value of the frame that used it; 0 = never used
        uint32_t usedBytes = 0;
        uint32_t usedDescriptors = 0;
    };

    struct Rings
    {
        ID3D12Resource* upload = nullptr;
        uint8_t* uploadCpu = nullptr;
        D3D12_GPU_VIRTUAL_ADDRESS uploadGpu = 0;
        ID3D12DescriptorHeap* descriptors = nullptr;
        D3D12_CPU_DESCRIPTOR_HANDLE descriptorsCpu = {};
        D3D12_GPU_DESCRIPTOR_HANDLE descriptorsGpu = {};
        uint32_t descriptorSize = 0;
        std::vector<Region> regions;
        uint32_t regionBytes = kDefaultRegionBytes;
        uint32_t regionDescriptors = kDefaultRegionDescriptors;
        uint32_t current = 0;
        uint64_t currentFence = 0; // the frame fence value the current region is stamped with

        void Destroy()
        {
            if (upload != nullptr && uploadCpu != nullptr)
            {
                upload->Unmap(0, nullptr);
            }

            uploadCpu = nullptr;
            Release(upload);
            Release(descriptors);
            regions.clear();
        }
    };

    std::mutex s_mutex;
    IUnityInterfaces* s_interfaces = nullptr;
    IUnityGraphics* s_graphics = nullptr;
    IUnityGraphicsD3D12v8* s_d3d12 = nullptr;
    ID3D12Device* s_device = nullptr;
    UnityGfxRenderer s_renderer = kUnityGfxRendererNull;
    bool s_loaded = false;
    std::string s_unavailable = "the plugin was not loaded by Unity";
    std::string s_lastError;
    std::vector<Pipeline> s_pipelines;
    Rings s_rings;
    ZvnCounters s_counters = {};
    uint32_t s_requestedRegions = kDefaultRegions;
    uint32_t s_requestedRegionBytes = kDefaultRegionBytes;
    uint32_t s_requestedRegionDescriptors = kDefaultRegionDescriptors;

    // Test control (2026-10-08): while consumption is held, an event does its work but its block's `consumed` mark is
    // withheld; the marks are given on ZvnReleaseHeld, in the order the events ran. A test on a device whose rendering
    // is not threaded (the Editor: the event runs inside Submit) can thus see the "submitted, not consumed" state the
    // managed lifetimes guard, and one on a threaded Player can widen it. Never on in a product run.
    bool s_holdConsumption = false;
    std::vector<ZvnDraw*> s_held;

    // Every event ends here: the result, then the consumed mark after everything was read -- or, held, later.
    void Consume(ZvnDraw* draw, ZvnResult result)
    {
        draw->result = (uint32_t)result;
        if (s_holdConsumption)
        {
            s_held.push_back(draw);
            s_counters.eventsHeld++;
            return;
        }

        MemoryBarrier();
        draw->consumed = 1;
    }

    void SetError(const char* format, ...)
    {
        char line[1024];
        va_list arguments;
        va_start(arguments, format);
        vsnprintf(line, sizeof(line), format, arguments);
        va_end(arguments);
        s_lastError = line;
    }

    void DestroyEverythingLocked()
    {
        for (size_t i = 0; i < s_pipelines.size(); i++)
        {
            s_pipelines[i].Destroy();
        }

        s_pipelines.clear();
        s_rings.Destroy();
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        if (eventType == kUnityGfxDeviceEventInitialize)
        {
            s_renderer = s_graphics != nullptr ? s_graphics->GetRenderer() : kUnityGfxRendererNull;
            s_d3d12 = nullptr;
            s_device = nullptr;
            if (s_renderer != kUnityGfxRendererD3D12)
            {
                s_unavailable = "the graphics device is not Direct3D 12 (Unity renderer " + std::to_string((int)s_renderer) + ")";
                return;
            }

            s_d3d12 = s_interfaces->Get<IUnityGraphicsD3D12v8>();
            if (s_d3d12 == nullptr)
            {
                s_unavailable = "Unity does not offer IUnityGraphicsD3D12v8 (resource state requests) on this device";
                return;
            }

            s_device = s_d3d12->GetDevice();
            if (s_device == nullptr)
            {
                s_unavailable = "IUnityGraphicsD3D12v8 gave no device";
                return;
            }

            // The draw event: recorded on the rendering thread into the command list Unity is recording, with the
            // render target Unity has bound, and Unity told that the command list's bindings are changed by it.
            UnityD3D12PluginEventConfig config = {};
            config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
            config.flags = kUnityD3D12EventConfigFlag_ModifiesCommandBuffersState;
            config.ensureActiveRenderTextureIsBound = true;
            s_d3d12->ConfigureEvent(kDrawEventId, &config);

            // The sentinel event: records nothing, touches no state; it marks its block consumed, which tells the
            // managed side that every event recorded before it in the same stream has run (or will never run).
            UnityD3D12PluginEventConfig sentinel = {};
            sentinel.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
            sentinel.flags = 0;
            sentinel.ensureActiveRenderTextureIsBound = false;
            s_d3d12->ConfigureEvent(kSentinelEventId, &sentinel);
            s_unavailable.clear();
        }
        else if (eventType == kUnityGfxDeviceEventShutdown)
        {
            // Held marks are never given after the device is gone: the plugin touches no block from here on.
            s_held.clear();
            s_holdConsumption = false;
            DestroyEverythingLocked();
            s_d3d12 = nullptr;
            s_device = nullptr;
            s_renderer = kUnityGfxRendererNull;
            s_unavailable = "the graphics device was shut down";
        }
    }

    bool AvailableLocked()
    {
        return s_loaded && s_d3d12 != nullptr && s_device != nullptr;
    }

    // ---- rings --------------------------------------------------------------------------------------------------

    bool EnsureRingsLocked()
    {
        if (s_rings.upload != nullptr)
        {
            return true;
        }

        uint32_t regions = s_requestedRegions < 2 ? 2 : s_requestedRegions;
        s_rings.regionBytes = (s_requestedRegionBytes + kConstantAlignment - 1) / kConstantAlignment * kConstantAlignment;
        s_rings.regionDescriptors = s_requestedRegionDescriptors;

        D3D12_HEAP_PROPERTIES heap = {};
        heap.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC description = {};
        description.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        description.Width = (UINT64)s_rings.regionBytes * regions;
        description.Height = 1;
        description.DepthOrArraySize = 1;
        description.MipLevels = 1;
        description.SampleDesc.Count = 1;
        description.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        HRESULT result = s_device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &description, D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&s_rings.upload));
        if (FAILED(result))
        {
            SetError("the constant upload ring could not be made: 0x%08x", (unsigned)result);
            return false;
        }

        s_rings.upload->SetName(L"Zantetsu VP native constants");
        void* mapped = nullptr;
        D3D12_RANGE noRead = { 0, 0 };
        result = s_rings.upload->Map(0, &noRead, &mapped);
        if (FAILED(result))
        {
            SetError("the constant upload ring could not be mapped: 0x%08x", (unsigned)result);
            s_rings.Destroy();
            return false;
        }

        s_rings.uploadCpu = (uint8_t*)mapped;
        s_rings.uploadGpu = s_rings.upload->GetGPUVirtualAddress();

        D3D12_DESCRIPTOR_HEAP_DESC heapDescription = {};
        heapDescription.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDescription.NumDescriptors = s_rings.regionDescriptors * regions;
        heapDescription.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        result = s_device->CreateDescriptorHeap(&heapDescription, IID_PPV_ARGS(&s_rings.descriptors));
        if (FAILED(result))
        {
            SetError("the descriptor ring could not be made: 0x%08x", (unsigned)result);
            s_rings.Destroy();
            return false;
        }

        s_rings.descriptors->SetName(L"Zantetsu VP native descriptors");
        s_rings.descriptorsCpu = s_rings.descriptors->GetCPUDescriptorHandleForHeapStart();
        s_rings.descriptorsGpu = s_rings.descriptors->GetGPUDescriptorHandleForHeapStart();
        s_rings.descriptorSize = s_device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        s_rings.regions.assign(regions, Region());
        s_rings.current = 0;
        s_rings.currentFence = 0;
        s_counters.ringRegions = regions;
        s_counters.ringRegionBytes = s_rings.regionBytes;
        return true;
    }

    // The region for the frame being recorded: the one in use if the frame fence value has not moved since, else the
    // next, if the GPU is done with it. False when it is not: nothing waits.
    bool TakeRegionLocked(uint64_t frameFence, uint64_t completedFence, Region*& region)
    {
        if (s_rings.currentFence == frameFence && s_rings.regions[s_rings.current].fence == frameFence)
        {
            region = &s_rings.regions[s_rings.current];
            return true;
        }

        uint32_t next = (s_rings.current + 1) % (uint32_t)s_rings.regions.size();
        Region& candidate = s_rings.regions[next];
        if (candidate.fence != 0 && candidate.fence > completedFence)
        {
            return false;
        }

        candidate.fence = frameFence;
        candidate.usedBytes = 0;
        candidate.usedDescriptors = 0;
        s_rings.current = next;
        s_rings.currentFence = frameFence;
        region = &candidate;
        return true;
    }

    // ---- pipeline creation ------------------------------------------------------------------------------------------

    D3D12_SHADER_VISIBILITY Visibility(uint32_t stage)
    {
        return stage == ZvnStage_Vertex ? D3D12_SHADER_VISIBILITY_VERTEX : D3D12_SHADER_VISIBILITY_PIXEL;
    }

    D3D12_STATIC_SAMPLER_DESC StaticSampler(const ZvnBinding& binding)
    {
        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.ShaderRegister = binding.shaderRegister;
        sampler.ShaderVisibility = Visibility(binding.stage);
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
        D3D12_TEXTURE_ADDRESS_MODE clamp = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        D3D12_TEXTURE_ADDRESS_MODE wrap = D3D12_TEXTURE_ADDRESS_MODE_WRAP;
        bool anisotropic = binding.extra2 > 1;
        sampler.MaxAnisotropy = anisotropic ? binding.extra2 : 1;
        switch (binding.extra)
        {
            case ZvnSampler_PointClamp:
                sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = clamp;
                break;
            case ZvnSampler_LinearClamp:
                sampler.Filter = anisotropic ? D3D12_FILTER_ANISOTROPIC : D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = clamp;
                break;
            case ZvnSampler_LinearWrap:
                sampler.Filter = anisotropic ? D3D12_FILTER_ANISOTROPIC : D3D12_FILTER_MIN_MAG_LINEAR_MIP_POINT;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = wrap;
                break;
            case ZvnSampler_TrilinearClamp:
                sampler.Filter = anisotropic ? D3D12_FILTER_ANISOTROPIC : D3D12_FILTER_MIN_MAG_MIP_LINEAR;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = clamp;
                break;
            case ZvnSampler_TrilinearWrap:
                sampler.Filter = anisotropic ? D3D12_FILTER_ANISOTROPIC : D3D12_FILTER_MIN_MAG_MIP_LINEAR;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = wrap;
                break;
            case ZvnSampler_CompareLinearClampLessEqual:
                sampler.Filter = D3D12_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = clamp;
                sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_LESS_EQUAL;
                sampler.MaxAnisotropy = 1;
                break;
            case ZvnSampler_CompareLinearClampGreaterEqual:
            default:
                sampler.Filter = D3D12_FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT;
                sampler.AddressU = sampler.AddressV = sampler.AddressW = clamp;
                sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_GREATER_EQUAL;
                sampler.MaxAnisotropy = 1;
                break;
        }

        return sampler;
    }

    bool BuildRootSignature(Pipeline& pipeline)
    {
        std::vector<D3D12_ROOT_PARAMETER> parameters;
        std::vector<D3D12_STATIC_SAMPLER_DESC> samplers;
        std::vector<std::vector<D3D12_DESCRIPTOR_RANGE>> ranges; // one vector per table, kept alive until serialisation
        uint32_t commandConstantRegister = UINT32_MAX;
        uint32_t commandConstantStages = 0;

        // The command constant: one root parameter the command signature writes. Both stages may read it only at one
        // register; the shaders this is for read it in the vertex stage alone.
        for (size_t i = 0; i < pipeline.bindings.size(); i++)
        {
            const ZvnBinding& binding = pipeline.bindings[i];
            if (binding.kind != ZvnBinding_CommandConstant)
            {
                continue;
            }

            if (commandConstantRegister != UINT32_MAX && commandConstantRegister != binding.shaderRegister)
            {
                SetError("%s: the command constant is at b%u in one stage and b%u in another; one register is needed", pipeline.name.c_str(), commandConstantRegister, binding.shaderRegister);
                return false;
            }

            commandConstantRegister = binding.shaderRegister;
            commandConstantStages |= 1u << binding.stage;
        }

        if (commandConstantRegister == UINT32_MAX)
        {
            SetError("%s: no command constant binding (VpNativeCommand): the variant is not the plugin's", pipeline.name.c_str());
            return false;
        }

        D3D12_ROOT_PARAMETER constant = {};
        constant.ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        constant.Constants.ShaderRegister = commandConstantRegister;
        constant.Constants.Num32BitValues = 1;
        constant.ShaderVisibility = commandConstantStages == (1u << ZvnStage_Vertex) ? D3D12_SHADER_VISIBILITY_VERTEX
            : commandConstantStages == (1u << ZvnStage_Pixel) ? D3D12_SHADER_VISIBILITY_PIXEL : D3D12_SHADER_VISIBILITY_ALL;
        pipeline.commandConstantParameter = (uint32_t)parameters.size();
        parameters.push_back(constant);

        for (size_t i = 0; i < pipeline.bindings.size(); i++)
        {
            const ZvnBinding& binding = pipeline.bindings[i];
            if (binding.kind == ZvnBinding_ConstantBuffer)
            {
                D3D12_ROOT_PARAMETER parameter = {};
                parameter.ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
                parameter.Descriptor.ShaderRegister = binding.shaderRegister;
                parameter.ShaderVisibility = Visibility(binding.stage);
                pipeline.constantBuffers.push_back({ (uint32_t)i, (uint32_t)parameters.size() });
                parameters.push_back(parameter);
            }
            else if (binding.kind == ZvnBinding_Sampler)
            {
                samplers.push_back(StaticSampler(binding));
            }
        }

        for (uint32_t stage = 0; stage < 2; stage++)
        {
            DescriptorTable table;
            table.stage = (ZvnStage)stage;
            std::vector<D3D12_DESCRIPTOR_RANGE> stageRanges;
            for (size_t i = 0; i < pipeline.bindings.size(); i++)
            {
                const ZvnBinding& binding = pipeline.bindings[i];
                if (binding.stage != stage || (binding.kind != ZvnBinding_Buffer && binding.kind != ZvnBinding_Texture))
                {
                    continue;
                }

                D3D12_DESCRIPTOR_RANGE range = {};
                range.RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
                range.NumDescriptors = 1;
                range.BaseShaderRegister = binding.shaderRegister;
                range.OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;
                stageRanges.push_back(range);
                table.bindings.push_back((uint32_t)i);
            }

            if (stageRanges.empty())
            {
                continue;
            }

            ranges.push_back(stageRanges);
            D3D12_ROOT_PARAMETER parameter = {};
            parameter.ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
            parameter.DescriptorTable.NumDescriptorRanges = (UINT)ranges.back().size();
            parameter.DescriptorTable.pDescriptorRanges = ranges.back().data();
            parameter.ShaderVisibility = Visibility(stage);
            table.parameter = (uint32_t)parameters.size();
            parameters.push_back(parameter);
            pipeline.tables.push_back(table);
        }

        // The ranges' addresses must not move: fix them up now that the vector is complete.
        size_t rangeIndex = 0;
        for (size_t p = 0; p < parameters.size(); p++)
        {
            if (parameters[p].ParameterType == D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE)
            {
                parameters[p].DescriptorTable.pDescriptorRanges = ranges[rangeIndex++].data();
            }
        }

        D3D12_ROOT_SIGNATURE_DESC description = {};
        description.NumParameters = (UINT)parameters.size();
        description.pParameters = parameters.data();
        description.NumStaticSamplers = (UINT)samplers.size();
        description.pStaticSamplers = samplers.empty() ? nullptr : samplers.data();
        description.Flags = D3D12_ROOT_SIGNATURE_FLAG_DENY_HULL_SHADER_ROOT_ACCESS | D3D12_ROOT_SIGNATURE_FLAG_DENY_DOMAIN_SHADER_ROOT_ACCESS | D3D12_ROOT_SIGNATURE_FLAG_DENY_GEOMETRY_SHADER_ROOT_ACCESS;

        ID3DBlob* serialised = nullptr;
        ID3DBlob* errors = nullptr;
        HRESULT result = D3D12SerializeRootSignature(&description, D3D_ROOT_SIGNATURE_VERSION_1, &serialised, &errors);
        if (FAILED(result))
        {
            SetError("%s: the root signature could not be serialised: 0x%08x %s", pipeline.name.c_str(), (unsigned)result, errors != nullptr ? (const char*)errors->GetBufferPointer() : "");
            Release(errors);
            return false;
        }

        Release(errors);
        result = s_device->CreateRootSignature(0, serialised->GetBufferPointer(), serialised->GetBufferSize(), IID_PPV_ARGS(&pipeline.rootSignature));
        Release(serialised);
        if (FAILED(result))
        {
            SetError("%s: the root signature could not be created: 0x%08x", pipeline.name.c_str(), (unsigned)result);
            return false;
        }

        return true;
    }

    bool BuildPipelineState(Pipeline& pipeline, const ZvnPipelineDesc& desc)
    {
        D3D12_GRAPHICS_PIPELINE_STATE_DESC state = {};
        state.pRootSignature = pipeline.rootSignature;
        state.VS.pShaderBytecode = desc.vertexBytecode;
        state.VS.BytecodeLength = desc.vertexBytecodeSize;
        state.PS.pShaderBytecode = desc.pixelBytecodeSize > 0 ? desc.pixelBytecode : nullptr;
        state.PS.BytecodeLength = desc.pixelBytecodeSize;
        state.BlendState.AlphaToCoverageEnable = FALSE;
        state.BlendState.IndependentBlendEnable = FALSE;
        for (int i = 0; i < 8; i++)
        {
            state.BlendState.RenderTarget[i].SrcBlend = D3D12_BLEND_ONE;
            state.BlendState.RenderTarget[i].DestBlend = D3D12_BLEND_ZERO;
            state.BlendState.RenderTarget[i].BlendOp = D3D12_BLEND_OP_ADD;
            state.BlendState.RenderTarget[i].SrcBlendAlpha = D3D12_BLEND_ONE;
            state.BlendState.RenderTarget[i].DestBlendAlpha = D3D12_BLEND_ZERO;
            state.BlendState.RenderTarget[i].BlendOpAlpha = D3D12_BLEND_OP_ADD;
            state.BlendState.RenderTarget[i].LogicOp = D3D12_LOGIC_OP_NOOP;
            state.BlendState.RenderTarget[i].RenderTargetWriteMask = desc.colourWrite != 0 ? D3D12_COLOR_WRITE_ENABLE_ALL : 0;
        }

        state.SampleMask = UINT_MAX;
        state.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        state.RasterizerState.CullMode = desc.cullMode == ZvnCull_None ? D3D12_CULL_MODE_NONE : desc.cullMode == ZvnCull_Front ? D3D12_CULL_MODE_FRONT : D3D12_CULL_MODE_BACK;
        state.RasterizerState.FrontCounterClockwise = desc.frontCounterClockwise != 0 ? TRUE : FALSE;
        state.RasterizerState.DepthBias = desc.depthBias;
        state.RasterizerState.DepthBiasClamp = desc.depthBiasClamp;
        state.RasterizerState.SlopeScaledDepthBias = desc.slopeScaledDepthBias;
        state.RasterizerState.DepthClipEnable = TRUE;
        state.RasterizerState.MultisampleEnable = desc.sampleCount > 1 ? TRUE : FALSE;
        state.RasterizerState.ConservativeRaster = D3D12_CONSERVATIVE_RASTERIZATION_MODE_OFF;
        state.DepthStencilState.DepthEnable = desc.depthFormat != 0 ? TRUE : FALSE;
        state.DepthStencilState.DepthWriteMask = desc.depthWrite != 0 ? D3D12_DEPTH_WRITE_MASK_ALL : D3D12_DEPTH_WRITE_MASK_ZERO;
        state.DepthStencilState.DepthFunc = (D3D12_COMPARISON_FUNC)desc.depthFunc;
        state.DepthStencilState.StencilEnable = FALSE;
        state.InputLayout.NumElements = 0;
        state.IBStripCutValue = D3D12_INDEX_BUFFER_STRIP_CUT_VALUE_DISABLED;
        state.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        state.NumRenderTargets = desc.renderTargetFormat != 0 ? 1 : 0;
        state.RTVFormats[0] = (DXGI_FORMAT)desc.renderTargetFormat;
        state.DSVFormat = (DXGI_FORMAT)desc.depthFormat;
        state.SampleDesc.Count = desc.sampleCount == 0 ? 1 : desc.sampleCount;
        state.SampleDesc.Quality = 0;
        state.NodeMask = 0;
        HRESULT result = s_device->CreateGraphicsPipelineState(&state, IID_PPV_ARGS(&pipeline.pipelineState));
        if (FAILED(result))
        {
            SetError("%s: the pipeline state could not be created: 0x%08x (rtv %u dsv %u samples %u)", pipeline.name.c_str(), (unsigned)result, desc.renderTargetFormat, desc.depthFormat, desc.sampleCount);
            return false;
        }

        return true;
    }

    bool BuildCommandSignature(Pipeline& pipeline, const ZvnPipelineDesc& desc)
    {
        if (desc.argumentStride < 24 || (desc.argumentStride % 4) != 0)
        {
            SetError("%s: the argument stride must be at least 24 bytes and a multiple of 4 (given %u)", pipeline.name.c_str(), desc.argumentStride);
            return false;
        }

        D3D12_INDIRECT_ARGUMENT_DESC arguments[2] = {};
        arguments[0].Type = D3D12_INDIRECT_ARGUMENT_TYPE_CONSTANT;
        arguments[0].Constant.RootParameterIndex = pipeline.commandConstantParameter;
        arguments[0].Constant.DestOffsetIn32BitValues = 0;
        arguments[0].Constant.Num32BitValuesToSet = 1;
        arguments[1].Type = D3D12_INDIRECT_ARGUMENT_TYPE_DRAW_INDEXED;
        D3D12_COMMAND_SIGNATURE_DESC description = {};
        description.ByteStride = desc.argumentStride;
        description.NumArgumentDescs = 2;
        description.pArgumentDescs = arguments;
        HRESULT result = s_device->CreateCommandSignature(&description, pipeline.rootSignature, IID_PPV_ARGS(&pipeline.commandSignature));
        if (FAILED(result))
        {
            SetError("%s: the command signature could not be created: 0x%08x", pipeline.name.c_str(), (unsigned)result);
            return false;
        }

        pipeline.argumentStride = desc.argumentStride;
        return true;
    }

    // Pipelines released whose last frame has passed are destroyed here, on the main thread, never on the render one.
    void ReapReleasedLocked(uint64_t completedFence)
    {
        for (size_t i = 0; i < s_pipelines.size(); i++)
        {
            Pipeline& pipeline = s_pipelines[i];
            if (pipeline.alive && pipeline.released && pipeline.lastUsedFence <= completedFence)
            {
                pipeline.Destroy();
            }
        }
    }

    // ---- the draw event ---------------------------------------------------------------------------------------------

    void Refuse(ZvnDraw* draw, ZvnResult result)
    {
        s_counters.drawsRefused++;
        s_counters.lastRefusal = (uint32_t)result;
        s_counters.lastRefusalSerial = draw != nullptr ? draw->serial : 0;
        if (draw != nullptr)
        {
            Consume(draw, result);
        }
    }

    const ZvnResource* FindResource(const ZvnDraw* draw, uint32_t binding)
    {
        const ZvnResource* resources = (const ZvnResource*)((const uint8_t*)draw + draw->resourcesOffset);
        for (uint32_t i = 0; i < draw->resourceCount; i++)
        {
            if (resources[i].binding == binding)
            {
                return &resources[i];
            }
        }

        return nullptr;
    }

    const ZvnConstants* FindConstants(const ZvnDraw* draw, uint32_t binding)
    {
        const ZvnConstants* constants = (const ZvnConstants*)((const uint8_t*)draw + draw->constantsOffset);
        for (uint32_t i = 0; i < draw->constantsCount; i++)
        {
            if (constants[i].binding == binding)
            {
                return &constants[i];
            }
        }

        return nullptr;
    }

    // The D3D12 resource of a bound resource: the pointer as given, or -- for a render buffer (a render texture's
    // depth, which the managed side cannot name as a texture) -- what Unity says it is.
    ID3D12Resource* NativeResource(const ZvnResource& resource)
    {
        if (resource.resource == nullptr)
        {
            return nullptr;
        }

        if (resource.kind == 3)
        {
            return s_d3d12->TextureFromRenderBuffer((UnityRenderBuffer)resource.resource);
        }

        return (ID3D12Resource*)resource.resource;
    }

    bool WriteDescriptor(const ZvnBinding& binding, const ZvnResource& resource, D3D12_CPU_DESCRIPTOR_HANDLE handle)
    {
        ID3D12Resource* native = NativeResource(resource);
        if (native == nullptr)
        {
            return false;
        }

        D3D12_SHADER_RESOURCE_VIEW_DESC view = {};
        view.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        if (binding.kind == ZvnBinding_Buffer)
        {
            view.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
            view.Buffer.FirstElement = resource.firstElement;
            view.Buffer.NumElements = resource.elementCount;
            if (resource.kind == 0)
            {
                view.Format = DXGI_FORMAT_R32_TYPELESS;
                view.Buffer.Flags = D3D12_BUFFER_SRV_FLAG_RAW;
            }
            else
            {
                view.Format = DXGI_FORMAT_UNKNOWN;
                view.Buffer.StructureByteStride = resource.stride;
            }
        }
        else
        {
            D3D12_RESOURCE_DESC description = native->GetDesc();
            view.Format = resource.format != 0 ? (DXGI_FORMAT)resource.format : description.Format;
            if (resource.dimension == ZvnTexture_Cube)
            {
                view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURECUBE;
                view.TextureCube.MipLevels = resource.mipLevels != 0 ? resource.mipLevels : description.MipLevels;
            }
            else if (resource.dimension == ZvnTexture_2DArray)
            {
                view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DARRAY;
                view.Texture2DArray.MipLevels = resource.mipLevels != 0 ? resource.mipLevels : description.MipLevels;
                view.Texture2DArray.ArraySize = resource.arraySize != 0 ? resource.arraySize : description.DepthOrArraySize;
            }
            else if (description.SampleDesc.Count > 1)
            {
                view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2DMS;
            }
            else
            {
                view.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
                view.Texture2D.MipLevels = resource.mipLevels != 0 ? resource.mipLevels : description.MipLevels;
            }
        }

        s_device->CreateShaderResourceView(native, &view, handle);
        s_counters.descriptorsWritten++;
        return true;
    }

    D3D12_RESOURCE_STATES ReadStateFor(const Pipeline& pipeline, uint32_t bindingIndex)
    {
        // The same resource may be read by both stages under two bindings: the state asked for is the union.
        D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
        const ZvnBinding& binding = pipeline.bindings[bindingIndex];
        for (size_t i = 0; i < pipeline.bindings.size(); i++)
        {
            const ZvnBinding& other = pipeline.bindings[i];
            if (other.kind == binding.kind && strncmp(other.name, binding.name, ZVN_NAME_LENGTH) == 0)
            {
                state |= other.stage == ZvnStage_Vertex ? D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE : D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
            }
        }

        return state;
    }

    void UNITY_INTERFACE_API OnDrawEvent(int eventId, void* data)
    {
        if (data == nullptr || (eventId != kDrawEventId && eventId != kSentinelEventId))
        {
            return;
        }

        ZvnDraw* draw = (ZvnDraw*)data;
        std::lock_guard<std::mutex> lock(s_mutex);

        // Once the device is gone (shutdown, or never there) the plugin touches no block at all: the managed side
        // frees the blocks of events that can no longer run once it sees the plugin unavailable, and a write here
        // would be into memory it may have freed.
        if (!AvailableLocked())
        {
            s_counters.drawsRefused++;
            s_counters.lastRefusal = (uint32_t)ZvnResult_NotAvailable;
            return;
        }

        if (eventId == kSentinelEventId)
        {
            // Consumed, nothing else: the order of the stream is what the managed side reads from it.
            s_counters.sentinelsRun++;
            Consume(draw, ZvnResult_Recorded);
            return;
        }

        if (draw->version != ZVN_CONTRACT_VERSION)
        {
            Refuse(draw, ZvnResult_BadVersion);
            return;
        }

        if (draw->pipeline >= s_pipelines.size() || !s_pipelines[draw->pipeline].alive || s_pipelines[draw->pipeline].released)
        {
            Refuse(draw, ZvnResult_NoPipeline);
            return;
        }

        Pipeline& pipeline = s_pipelines[draw->pipeline];
        UnityGraphicsD3D12RecordingState recording = {};
        if (!s_d3d12->CommandRecordingState(&recording) || recording.commandList == nullptr)
        {
            Refuse(draw, ZvnResult_NoCommandList);
            return;
        }

        if (!EnsureRingsLocked())
        {
            Refuse(draw, ZvnResult_RingFull);
            return;
        }

        ID3D12Fence* fence = s_d3d12->GetFrameFence();
        uint64_t frameFence = s_d3d12->GetNextFrameFenceValue();
        uint64_t completed = fence != nullptr ? fence->GetCompletedValue() : 0;
        Region* region = nullptr;
        if (!TakeRegionLocked(frameFence, completed, region))
        {
            Refuse(draw, ZvnResult_RingFull);
            return;
        }

        // Room for this draw's constants and descriptors, before anything is recorded. A constant buffer the GPU
        // wrote is bound where it is and takes no room.
        uint32_t constantBytes = 0;
        for (size_t i = 0; i < pipeline.constantBuffers.size(); i++)
        {
            const ZvnBinding& binding = pipeline.bindings[pipeline.constantBuffers[i].binding];
            const ZvnConstants* constants = FindConstants(draw, pipeline.constantBuffers[i].binding);
            if (constants == nullptr)
            {
                Refuse(draw, ZvnResult_BadConstants);
                return;
            }

            if (constants->resource != nullptr)
            {
                if ((constants->resourceOffset % kConstantAlignment) != 0)
                {
                    Refuse(draw, ZvnResult_BadConstants);
                    return;
                }

                continue;
            }

            if (constants->size > binding.extra || constants->offset + constants->size > draw->totalBytes)
            {
                Refuse(draw, ZvnResult_BadConstants);
                return;
            }

            constantBytes += (binding.extra + kConstantAlignment - 1) / kConstantAlignment * kConstantAlignment;
        }

        uint32_t descriptorCount = 0;
        for (size_t t = 0; t < pipeline.tables.size(); t++)
        {
            descriptorCount += (uint32_t)pipeline.tables[t].bindings.size();
        }

        if (region->usedBytes + constantBytes > s_rings.regionBytes || region->usedDescriptors + descriptorCount > s_rings.regionDescriptors)
        {
            Refuse(draw, ZvnResult_RingFull);
            return;
        }

        if (draw->argumentBuffer == nullptr || draw->indexBuffer == nullptr || draw->commandCount == 0 || draw->viewportCount == 0 || draw->viewportCount > ZVN_MAX_VIEWPORTS)
        {
            Refuse(draw, ZvnResult_BadResource);
            return;
        }

        for (size_t t = 0; t < pipeline.tables.size(); t++)
        {
            for (size_t b = 0; b < pipeline.tables[t].bindings.size(); b++)
            {
                const ZvnResource* resource = FindResource(draw, pipeline.tables[t].bindings[b]);
                if (resource == nullptr || NativeResource(*resource) == nullptr)
                {
                    Refuse(draw, ZvnResult_BadResource);
                    return;
                }
            }
        }

        ID3D12GraphicsCommandList* list = recording.commandList;
        uint32_t regionIndex = s_rings.current;

        // Resource states: what the selection wrote (UAV) and what Unity last used these as become readable here.
        ID3D12Resource* arguments = (ID3D12Resource*)draw->argumentBuffer;
        s_d3d12->RequestResourceState(arguments, D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT | D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        s_d3d12->RequestResourceState((ID3D12Resource*)draw->indexBuffer, D3D12_RESOURCE_STATE_INDEX_BUFFER);
        for (size_t t = 0; t < pipeline.tables.size(); t++)
        {
            for (size_t b = 0; b < pipeline.tables[t].bindings.size(); b++)
            {
                uint32_t bindingIndex = pipeline.tables[t].bindings[b];
                const ZvnResource* resource = FindResource(draw, bindingIndex);
                ID3D12Resource* native = NativeResource(*resource);
                if (native == arguments)
                {
                    continue; // asked for above, with the indirect-argument state
                }

                s_d3d12->RequestResourceState(native, ReadStateFor(pipeline, bindingIndex));
            }
        }

        // Constants into the upload ring; a GPU-written one is asked into the constant buffer state and bound as it is.
        uint8_t* regionCpu = s_rings.uploadCpu + (size_t)regionIndex * s_rings.regionBytes;
        D3D12_GPU_VIRTUAL_ADDRESS regionGpu = s_rings.uploadGpu + (UINT64)regionIndex * s_rings.regionBytes;
        std::vector<D3D12_GPU_VIRTUAL_ADDRESS> constantAddresses(pipeline.constantBuffers.size());
        for (size_t i = 0; i < pipeline.constantBuffers.size(); i++)
        {
            const ZvnBinding& binding = pipeline.bindings[pipeline.constantBuffers[i].binding];
            const ZvnConstants* constants = FindConstants(draw, pipeline.constantBuffers[i].binding);
            if (constants->resource != nullptr)
            {
                ID3D12Resource* written = (ID3D12Resource*)constants->resource;
                s_d3d12->RequestResourceState(written, D3D12_RESOURCE_STATE_VERTEX_AND_CONSTANT_BUFFER);
                constantAddresses[i] = written->GetGPUVirtualAddress() + constants->resourceOffset;
                continue;
            }

            uint32_t at = region->usedBytes;
            uint32_t padded = (binding.extra + kConstantAlignment - 1) / kConstantAlignment * kConstantAlignment;
            memset(regionCpu + at, 0, padded);
            memcpy(regionCpu + at, (const uint8_t*)draw + constants->offset, constants->size);
            constantAddresses[i] = regionGpu + at;
            region->usedBytes += padded;
            s_counters.constantBytesUploaded += constants->size;
        }

        // Descriptors into the descriptor ring, one table after another.
        std::vector<D3D12_GPU_DESCRIPTOR_HANDLE> tableStarts(pipeline.tables.size());
        for (size_t t = 0; t < pipeline.tables.size(); t++)
        {
            uint32_t first = regionIndex * s_rings.regionDescriptors + region->usedDescriptors;
            D3D12_GPU_DESCRIPTOR_HANDLE gpu = s_rings.descriptorsGpu;
            gpu.ptr += (UINT64)first * s_rings.descriptorSize;
            tableStarts[t] = gpu;
            for (size_t b = 0; b < pipeline.tables[t].bindings.size(); b++)
            {
                uint32_t bindingIndex = pipeline.tables[t].bindings[b];
                D3D12_CPU_DESCRIPTOR_HANDLE cpu = s_rings.descriptorsCpu;
                cpu.ptr += (SIZE_T)(first + b) * s_rings.descriptorSize;
                WriteDescriptor(pipeline.bindings[bindingIndex], *FindResource(draw, bindingIndex), cpu);
            }

            region->usedDescriptors += (uint32_t)pipeline.tables[t].bindings.size();
        }

        // The draw state of this issue, then one ExecuteIndirect per viewport over the same commands.
        list->SetGraphicsRootSignature(pipeline.rootSignature);
        list->SetPipelineState(pipeline.pipelineState);
        list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D12_INDEX_BUFFER_VIEW indexView = {};
        indexView.BufferLocation = ((ID3D12Resource*)draw->indexBuffer)->GetGPUVirtualAddress();
        indexView.SizeInBytes = draw->indexBufferBytes;
        indexView.Format = (DXGI_FORMAT)draw->indexFormat;
        list->IASetIndexBuffer(&indexView);
        ID3D12DescriptorHeap* heaps[1] = { s_rings.descriptors };
        list->SetDescriptorHeaps(1, heaps);
        for (size_t i = 0; i < pipeline.constantBuffers.size(); i++)
        {
            list->SetGraphicsRootConstantBufferView(pipeline.constantBuffers[i].parameter, constantAddresses[i]);
        }

        for (size_t t = 0; t < pipeline.tables.size(); t++)
        {
            list->SetGraphicsRootDescriptorTable(pipeline.tables[t].parameter, tableStarts[t]);
        }

        for (uint32_t v = 0; v < draw->viewportCount; v++)
        {
            const ZvnViewport& viewport = draw->viewports[v];
            D3D12_VIEWPORT port = { viewport.x, viewport.y, viewport.width, viewport.height, viewport.minDepth, viewport.maxDepth };
            D3D12_RECT scissor = { viewport.scissorLeft, viewport.scissorTop, viewport.scissorRight, viewport.scissorBottom };
            list->RSSetViewports(1, &port);
            list->RSSetScissorRects(1, &scissor);
            list->ExecuteIndirect(pipeline.commandSignature, draw->commandCount, arguments, draw->argumentOffset, nullptr, 0);
            s_counters.drawsRecorded++;
            s_counters.commandsRecorded += draw->commandCount;
        }

        pipeline.lastUsedFence = frameFence;
        Consume(draw, ZvnResult_Recorded);
    }
}

extern "C"
{
    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
    {
        {
            std::lock_guard<std::mutex> lock(s_mutex);
            s_interfaces = unityInterfaces;
            s_graphics = unityInterfaces->Get<IUnityGraphics>();
            s_loaded = true;
            s_unavailable = "the graphics device has not been initialised yet";
            s_counters.version = ZVN_CONTRACT_VERSION;
        }

        if (s_graphics != nullptr)
        {
            s_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);

            // The device may exist already (the plugin loaded after it): take the event that was missed.
            OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
        }
        else
        {
            std::lock_guard<std::mutex> lock(s_mutex);
            s_unavailable = "Unity gave no IUnityGraphics";
        }
    }

    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
    {
        if (s_graphics != nullptr)
        {
            s_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
        }

        std::lock_guard<std::mutex> lock(s_mutex);
        DestroyEverythingLocked();
        s_interfaces = nullptr;
        s_graphics = nullptr;
        s_d3d12 = nullptr;
        s_device = nullptr;
        s_loaded = false;
    }

    // The version of this plugin's contract with its managed side. A managed side of another version does not use it.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnContractVersion()
    {
        return ZVN_CONTRACT_VERSION;
    }

    // 1 when the plugin can record draws (Direct3D 12 with IUnityGraphicsD3D12v8); else 0. The reason is in the state.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnIsAvailable()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        return AvailableLocked() ? 1 : 0;
    }

    // The plugin's state in words, for a log. Returns the text's length; writes at most outSize - 1 characters.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnDescribeState(char* out, int outSize)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        std::string text;
        Append(text, "contract %d; loaded by Unity %d; Unity renderer %d; IUnityGraphicsD3D12v8 %d; device %d; pipelines %u; %s",
            ZVN_CONTRACT_VERSION, s_loaded ? 1 : 0, (int)s_renderer, s_d3d12 != nullptr ? 1 : 0, s_device != nullptr ? 1 : 0,
            (unsigned)s_pipelines.size(), s_unavailable.empty() ? "available" : ("NOT available: " + s_unavailable).c_str());
        return CopyOut(text, out, outSize);
    }

    // The reason of the last failed call, in words.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnLastError(char* out, int outSize)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        return CopyOut(s_lastError, out, outSize);
    }

    // The size of the rings, before the first draw: regions (frames in flight the rings can hold), bytes of
    // constants and descriptors per region. Ignored once the rings exist.
    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnConfigureRings(uint32_t regions, uint32_t regionBytes, uint32_t regionDescriptors)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        s_requestedRegions = regions;
        s_requestedRegionBytes = regionBytes;
        s_requestedRegionDescriptors = regionDescriptors;
    }

    // Makes a pipeline. Returns its id (>= 0), or -1 with the reason in ZvnLastError.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnCreatePipeline(const ZvnPipelineDesc* desc)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        if (desc == nullptr || desc->version != ZVN_CONTRACT_VERSION)
        {
            SetError("the pipeline description is of another contract version");
            return -1;
        }

        if (!AvailableLocked())
        {
            SetError("%s", s_unavailable.c_str());
            return -1;
        }

        if (desc->vertexBytecode == nullptr || desc->vertexBytecodeSize == 0 || desc->bindings == nullptr || desc->bindingCount == 0)
        {
            SetError("the pipeline description has no vertex shader or no bindings");
            return -1;
        }

        ID3D12Fence* fence = s_d3d12->GetFrameFence();
        ReapReleasedLocked(fence != nullptr ? fence->GetCompletedValue() : 0);

        Pipeline pipeline;
        pipeline.name.assign(desc->name, strnlen(desc->name, ZVN_NAME_LENGTH));
        pipeline.bindings.assign(desc->bindings, desc->bindings + desc->bindingCount);
        if (!BuildRootSignature(pipeline) || !BuildPipelineState(pipeline, *desc) || !BuildCommandSignature(pipeline, *desc))
        {
            pipeline.Destroy();
            return -1;
        }

        pipeline.alive = true;
        for (size_t i = 0; i < s_pipelines.size(); i++)
        {
            if (!s_pipelines[i].alive)
            {
                s_pipelines[i] = pipeline;
                return (int)i;
            }
        }

        s_pipelines.push_back(pipeline);
        return (int)s_pipelines.size() - 1;
    }

    // Releases a pipeline: no draw after this uses it; its objects go once the frame that last used it has passed.
    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnReleasePipeline(int pipeline)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        if (pipeline < 0 || (size_t)pipeline >= s_pipelines.size() || !s_pipelines[pipeline].alive)
        {
            return;
        }

        s_pipelines[pipeline].released = true;
        ID3D12Fence* fence = s_d3d12 != nullptr ? s_d3d12->GetFrameFence() : nullptr;
        ReapReleasedLocked(fence != nullptr ? fence->GetCompletedValue() : UINT64_MAX);
    }

    // A pipeline's root layout in words, for a record.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnDescribePipeline(int pipeline, char* out, int outSize)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        std::string text;
        if (pipeline < 0 || (size_t)pipeline >= s_pipelines.size() || !s_pipelines[pipeline].alive)
        {
            text = "no such pipeline";
            return CopyOut(text, out, outSize);
        }

        const Pipeline& p = s_pipelines[pipeline];
        Append(text, "%s: root parameters: [%u] command constant", p.name.c_str(), p.commandConstantParameter);
        for (size_t i = 0; i < p.constantBuffers.size(); i++)
        {
            const ZvnBinding& b = p.bindings[p.constantBuffers[i].binding];
            Append(text, "; [%u] cbv %s b%u %s %u B", p.constantBuffers[i].parameter, b.stage == ZvnStage_Vertex ? "vs" : "ps", b.shaderRegister, b.name, b.extra);
        }

        for (size_t t = 0; t < p.tables.size(); t++)
        {
            Append(text, "; [%u] table %s:", p.tables[t].parameter, p.tables[t].stage == ZvnStage_Vertex ? "vs" : "ps");
            for (size_t b = 0; b < p.tables[t].bindings.size(); b++)
            {
                const ZvnBinding& binding = p.bindings[p.tables[t].bindings[b]];
                Append(text, " t%u %s", binding.shaderRegister, binding.name);
            }
        }

        Append(text, "; argument stride %u", p.argumentStride);
        return CopyOut(text, out, outSize);
    }

    UnityRenderingEventAndData UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnGetRenderEventAndDataFunc()
    {
        return OnDrawEvent;
    }

    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnDrawEventId()
    {
        return kDrawEventId;
    }

    // The sentinel event's id: issued with a block whose header alone is read; marks it consumed and does nothing else.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnSentinelEventId()
    {
        return kSentinelEventId;
    }

    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnGetCounters(ZvnCounters* out)
    {
        if (out == nullptr)
        {
            return;
        }

        std::lock_guard<std::mutex> lock(s_mutex);
        *out = s_counters;
    }

    // Test control: hold (1) or give (0) the consumed marks of the events from here on. See s_holdConsumption.
    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnHoldConsumption(int hold)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        s_holdConsumption = hold != 0;
    }

    // Test control: gives the withheld consumed marks of the first `count` held events (all when count < 0), in the
    // order the events ran, and returns how many were given. Nothing is written once the device is gone.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnReleaseHeld(int count)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        if (!AvailableLocked())
        {
            s_held.clear();
            return 0;
        }

        size_t n = count < 0 || (size_t)count > s_held.size() ? s_held.size() : (size_t)count;
        for (size_t i = 0; i < n; i++)
        {
            MemoryBarrier();
            s_held[i]->consumed = 1;
        }

        s_held.erase(s_held.begin(), s_held.begin() + n);
        return (int)n;
    }

    // Test control: how many events ran whose consumed mark is withheld.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnHeldCount()
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        return (int)s_held.size();
    }

    // The frame fence: what the GPU has completed and what the frame being recorded will signal. For records.
    void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnFrameFence(uint64_t* completed, uint64_t* next)
    {
        std::lock_guard<std::mutex> lock(s_mutex);
        uint64_t c = 0, n = 0;
        if (AvailableLocked())
        {
            ID3D12Fence* fence = s_d3d12->GetFrameFence();
            c = fence != nullptr ? fence->GetCompletedValue() : 0;
            n = s_d3d12->GetNextFrameFenceValue();
        }

        if (completed != nullptr) *completed = c;
        if (next != nullptr) *next = n;
    }

    // The input and output signatures of a compiled shader (D3DReflect). Unity's DXBC carries no resource reflection,
    // so bindings cannot be read from it; this is for checking the stage's signature only.
    int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnReflectShader(const void* bytecode, int byteCount, char* out, int outSize)
    {
        if (bytecode == nullptr || byteCount <= 0)
        {
            return -1;
        }

        ID3D12ShaderReflection* reflection = nullptr;
        HRESULT result = D3DReflect(bytecode, (SIZE_T)byteCount, IID_PPV_ARGS(&reflection));
        if (FAILED(result) || reflection == nullptr)
        {
            return result < 0 ? (int)result : -2;
        }

        std::string text;
        D3D12_SHADER_DESC shader = {};
        reflection->GetDesc(&shader);
        Append(text, "shader version %u bound resources %u constant buffers %u\n", shader.Version, shader.BoundResources, shader.ConstantBuffers);
        for (UINT i = 0; i < shader.InputParameters; i++)
        {
            D3D12_SIGNATURE_PARAMETER_DESC input = {};
            reflection->GetInputParameterDesc(i, &input);
            Append(text, "input %s %u register %u mask %u\n", input.SemanticName, input.SemanticIndex, input.Register, (unsigned)input.Mask);
        }

        for (UINT i = 0; i < shader.OutputParameters; i++)
        {
            D3D12_SIGNATURE_PARAMETER_DESC output = {};
            reflection->GetOutputParameterDesc(i, &output);
            Append(text, "output %s %u register %u mask %u\n", output.SemanticName, output.SemanticIndex, output.Register, (unsigned)output.Mask);
        }

        reflection->Release();
        return CopyOut(text, out, outSize);
    }
}
