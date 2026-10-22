// Vulkan implementation of the VP native draw contract. Unity owns the queue, command stream and all draw inputs.
// The plugin owns shader modules, pipelines, views and frame regions; Unity's safeFrameNumber retires those regions.
#include <vulkan/vulkan.h>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsVulkan.h"
#include "ZantetsuVpNative.h"
#include <android/log.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <cstdio>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

static_assert(sizeof(ZvnBinding)==84 && sizeof(ZvnPipelineDesc)==168, "pipeline ABI differs from C#");
static_assert(sizeof(ZvnResource)==48 && sizeof(ZvnConstants)==32, "binding ABI differs from C#");
static_assert(sizeof(ZvnDraw)==240 && sizeof(ZvnCounters)==80, "event ABI differs from C#");
static_assert(sizeof(ZvnDrawExtension)==16 && sizeof(ZvnDirectCommand)==20, "Vulkan draw extension ABI differs from C#");
static_assert(sizeof(ZvnIndirectCount)==32, "indirect count record ABI differs from C#");

namespace {
IUnityGraphics* graphics = nullptr;
IUnityGraphicsVulkanV2* unity = nullptr;
UnityVulkanInstance instance{};
std::mutex gate;
std::string error;
ZvnCounters counters{ZVN_CONTRACT_VERSION};
std::atomic<uint64_t> currentFrame{0}, safeFrame{0};
uint32_t regionCount = 8, regionBytes = 262144, regionDescriptors = 4096;
bool available = false, featuresObserved = false, enabledMulti = false, enabledFirst = false, hold = false;
PFN_vkGetInstanceProcAddr originalGipa = nullptr;
PFN_vkCreateDevice originalCreateDevice = nullptr;
int DrawEvent = 1, SentinelEvent = 2, PrepareEvent = 3, PreparedDrawEvent = 4, GroupPrepareEvent = 5, GroupPreparedEvent = 6, WarmPipelineEvent = 7, ZeroEvent = 8, BarrierEvent = 9;
std::vector<ZvnDraw*> held;
VkPhysicalDeviceProperties properties{};
// Experiment (2026-10-10): the draw by the GPU's count of compacted commands (vkCmdDrawIndexedIndirectCount).
// Support is what the physical device lists; enablement is what Unity's device creation carried -- the extension
// among its names, or drawIndirectCount in a Vulkan 1.2 features chain -- or else the one extension this intercept
// adds when the device supports it. The two are kept apart and both are in the state text and the log.
bool indirectCountSupported=false, indirectCountEnabledByUnity=false, indirectCountEnabled=false, indirectCountAddedHere=false, indirectCountReady=false;
std::string deviceNote;
PFN_vkCmdDrawIndexedIndirectCountKHR drawIndexedIndirectCount=nullptr;
uint64_t countDrawsRecorded=0, countSlotsZeroed=0, countDrawsRefusedUnzeroed=0;
// The compaction's sets rotate by frame in a ring of four (VpIndexedIndirectDrawBatch.CompactionRing): frame F writes
// set F mod 4, last written by frame F-4. Zeroing that set's counts (and compacting into its entries) is safe only when
// the GPU has finished frame F-4 -- Unity's safeFrameNumber says so -- else the zero event is refused and the managed
// side draws fixed from then on (2026-10-10, TL: "four frames" is a condition to check, not to assume). The most frames
// seen in flight (submitted, not yet known finished, the current one excluded) is kept for the state text.
constexpr uint64_t ZvnCompactionRing=4;
bool RingReuseUnsafe(uint64_t currentFrame,uint64_t safeFrame){return currentFrame>safeFrame+ZvnCompactionRing;}
uint64_t framesInFlightMax=0, zeroRefusedInFlight=0;
uint64_t computeBarriers=0, countSnapshots=0, countCopies=0;
// The count buffer ranges whose zeroing was recorded, by Unity frame number: a count draw is prepared only when its
// slot lies in a range zeroed in the same frame (TL review, 2026-10-10: a zero event refused -- no recording state,
// say -- while the draw's own event records would otherwise draw from a count that was never zeroed). Nothing here is
// dereferenced; the managed pointer is the key the draw gives again.
struct ZeroedRange { uint64_t frame; const void* buffer; uint32_t offset, bytes; };
std::vector<ZeroedRange> zeroedRanges;
void NoteZeroed(uint64_t frame,const void* buffer,uint32_t offset,uint32_t bytes) {
    for(auto it=zeroedRanges.begin();it!=zeroedRanges.end();) { if(it->frame+8<frame)it=zeroedRanges.erase(it);else ++it; }
    zeroedRanges.push_back({frame,buffer,offset,bytes});
}
bool CountSlotZeroed(uint64_t frame,const void* buffer,uint32_t offset) {
    for(const auto& r:zeroedRanges) if(r.frame==frame && r.buffer==buffer && offset>=r.offset && uint64_t(offset)+4<=uint64_t(r.offset)+r.bytes)return true;
    return false;
}
std::vector<const char*> extendedExtensionNames;

void Fail(const char* text) { error = text; __android_log_print(ANDROID_LOG_ERROR, "ZantetsuVpVulkan", "%s", text); }
bool Ok(VkResult result, const char* action) {
    if (result == VK_SUCCESS) return true;
    char text[192]; std::snprintf(text, sizeof text, "%s: VkResult %d", action, result); Fail(text); return false;
}
int Text(const std::string& value, char* out, int count) {
    if (out && count > 0) { size_t n = std::min(value.size(), size_t(count-1)); std::memcpy(out,value.data(),n);out[n]=0; }
    return int(value.size());
}

// Observe the features actually enabled on Unity's device, and enable the two supported core features required by
// indexed multi-draw. Do not confuse hardware support with device enablement. A Features2 head is copied in place
// of pEnabledFeatures; an unfamiliar preceding chain is left intact and can only be accepted if already enabled.
VKAPI_ATTR VkResult VKAPI_CALL CreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* input,
    const VkAllocationCallbacks* allocator, VkDevice* device) {
    VkPhysicalDeviceFeatures supported{}; vkGetPhysicalDeviceFeatures(physical, &supported);
    VkDeviceCreateInfo info = *input;
    VkPhysicalDeviceFeatures core{};
    VkPhysicalDeviceFeatures2 features2{};
    const auto* chain = reinterpret_cast<const VkBaseInStructure*>(input->pNext);
    const VkPhysicalDeviceFeatures* enabled = input->pEnabledFeatures;
    for (auto* item = chain; item; item = item->pNext)
        if (item->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2)
            enabled = &reinterpret_cast<const VkPhysicalDeviceFeatures2*>(item)->features;
    if (enabled) core = *enabled;
    if (input->pEnabledFeatures || (chain && chain->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2)) {
        core.multiDrawIndirect = supported.multiDrawIndirect;
        core.drawIndirectFirstInstance = supported.drawIndirectFirstInstance;
        if (input->pEnabledFeatures) info.pEnabledFeatures = &core;
        else { features2 = *reinterpret_cast<const VkPhysicalDeviceFeatures2*>(chain);features2.features = core;info.pNext = &features2; }
    }
    // Experiment: VK_KHR_draw_indirect_count -- listed by the physical device? carried by Unity's creation? else added.
    uint32_t extensionCount=0; std::vector<VkExtensionProperties> extensions;
    if (vkEnumerateDeviceExtensionProperties(physical,nullptr,&extensionCount,nullptr)==VK_SUCCESS && extensionCount) {
        extensions.resize(extensionCount);
        if (vkEnumerateDeviceExtensionProperties(physical,nullptr,&extensionCount,extensions.data())!=VK_SUCCESS) extensions.clear();
    }
    indirectCountSupported=false;
    for (const auto& e : extensions) if (!std::strcmp(e.extensionName,VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME)) indirectCountSupported=true;
    indirectCountEnabledByUnity=false;
    for (uint32_t i=0;i<input->enabledExtensionCount;i++)
        if (input->ppEnabledExtensionNames[i] && !std::strcmp(input->ppEnabledExtensionNames[i],VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME)) indirectCountEnabledByUnity=true;
    bool vulkan12InChain=false, vulkan12DrawIndirectCount=false, vulkan11InChain=false, shaderDrawParameters=false;
    std::string chainTypes;
    for (auto* item = chain; item; item = item->pNext) {
        chainTypes += std::to_string(int(item->sType)); chainTypes += ' ';
        if (item->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES) {
            vulkan12InChain=true; vulkan12DrawIndirectCount=reinterpret_cast<const VkPhysicalDeviceVulkan12Features*>(item)->drawIndirectCount;
        } else if (item->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES) {
            vulkan11InChain=true; shaderDrawParameters=reinterpret_cast<const VkPhysicalDeviceVulkan11Features*>(item)->shaderDrawParameters;
        } else if (item->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_DRAW_PARAMETERS_FEATURES) {
            shaderDrawParameters=reinterpret_cast<const VkPhysicalDeviceShaderDrawParametersFeatures*>(item)->shaderDrawParameters;
        }
    }
    // VUID-VkDeviceCreateInfo-ppEnabledExtensionNames-02831: a Vulkan 1.2 feature chain with
    // drawIndirectCount=false forbids adding KHR alone. Keep Unity's feature choice and draw fixed.
    bool indirectCountAllowed = !vulkan12InChain || vulkan12DrawIndirectCount;
    indirectCountEnabled = indirectCountAllowed && (indirectCountEnabledByUnity || vulkan12DrawIndirectCount);
    indirectCountAddedHere=false;
    if (indirectCountSupported && !indirectCountEnabled && indirectCountAllowed) {
        extendedExtensionNames.clear();
        if (input->enabledExtensionCount)
            extendedExtensionNames.assign(input->ppEnabledExtensionNames, input->ppEnabledExtensionNames + input->enabledExtensionCount);
        extendedExtensionNames.push_back(VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME);
        info.enabledExtensionCount=uint32_t(extendedExtensionNames.size()); info.ppEnabledExtensionNames=extendedExtensionNames.data();
        indirectCountAddedHere=true;
    }
    VkResult result = originalCreateDevice(physical, &info, allocator, device);
    if (result != VK_SUCCESS && indirectCountAddedHere) {
        // The addition alone must never cost Unity its device: create it as Unity asked and go without the count draw.
        info.enabledExtensionCount=input->enabledExtensionCount; info.ppEnabledExtensionNames=input->ppEnabledExtensionNames;
        indirectCountAddedHere=false;
        result = originalCreateDevice(physical, &info, allocator, device);
    }
    if (result == VK_SUCCESS) {
        featuresObserved = true; enabledMulti = core.multiDrawIndirect; enabledFirst = core.drawIndirectFirstInstance;
        indirectCountEnabled = indirectCountAllowed && (indirectCountEnabledByUnity || vulkan12DrawIndirectCount || indirectCountAddedHere);
        VkPhysicalDeviceProperties physicalProperties{}; vkGetPhysicalDeviceProperties(physical,&physicalProperties);
        char text[512];
        std::snprintf(text,sizeof text,"indirectCount: supported=%d enabledByUnity(ext)=%d vk12chain=%d vk12.drawIndirectCount=%d addedHere=%d enabled=%d; "
            "shaderDrawParameters(chain)=%d vk11chain=%d; device extensions asked=%u, chain sTypes=[%s], physical apiVersion=%u.%u.%u",
            indirectCountSupported,indirectCountEnabledByUnity,vulkan12InChain,vulkan12DrawIndirectCount,indirectCountAddedHere,indirectCountEnabled,
            shaderDrawParameters,vulkan11InChain,input->enabledExtensionCount,chainTypes.c_str(),
            VK_API_VERSION_MAJOR(physicalProperties.apiVersion),VK_API_VERSION_MINOR(physicalProperties.apiVersion),VK_API_VERSION_PATCH(physicalProperties.apiVersion));
        deviceNote=text;
        __android_log_print(ANDROID_LOG_INFO,"ZantetsuVpVulkan","device multiDrawIndirect=%d firstInstance=%d",enabledMulti,enabledFirst);
        __android_log_print(ANDROID_LOG_INFO,"ZantetsuVpVulkan","%s",text);
    }
    return result;
}
VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL Gipa(VkInstance value, const char* name) {
    if (name && std::strcmp(name,"vkCreateDevice")==0) {
        originalCreateDevice = reinterpret_cast<PFN_vkCreateDevice>(originalGipa(value,name));
        return reinterpret_cast<PFN_vkVoidFunction>(CreateDevice);
    }
    return originalGipa(value,name);
}
PFN_vkGetInstanceProcAddr UNITY_INTERFACE_API Intercept(PFN_vkGetInstanceProcAddr fn, void*) { originalGipa = fn; return Gipa; }

struct Region {
    uint64_t frame = 0;
    uint32_t used = 0, sets = 0;
    VkBuffer upload = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    void* mapped = nullptr;
    VkDescriptorPool pool = VK_NULL_HANDLE;
    std::vector<VkImageView> views;
    void Destroy() {
        for (auto view : views) vkDestroyImageView(instance.device, view, nullptr);
        views.clear();
        if (pool) vkDestroyDescriptorPool(instance.device,pool,nullptr);
        if (memory && mapped) vkUnmapMemory(instance.device,memory);
        if (upload) vkDestroyBuffer(instance.device,upload,nullptr);
        if (memory) vkFreeMemory(instance.device,memory,nullptr);
        pool = VK_NULL_HANDLE; memory = VK_NULL_HANDLE; upload = VK_NULL_HANDLE; mapped = nullptr;
    }
    bool Create() {
        VkBufferCreateInfo buffer{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
        buffer.size=regionBytes; buffer.usage=VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT; buffer.sharingMode=VK_SHARING_MODE_EXCLUSIVE;
        if (!Ok(vkCreateBuffer(instance.device,&buffer,nullptr,&upload),"upload buffer")) return false;
        VkMemoryRequirements requirements{};vkGetBufferMemoryRequirements(instance.device,upload,&requirements);
        VkPhysicalDeviceMemoryProperties memoryProperties{};vkGetPhysicalDeviceMemoryProperties(instance.physicalDevice,&memoryProperties);
        uint32_t type = UINT32_MAX;
        for(uint32_t i=0;i<memoryProperties.memoryTypeCount;i++)
            if ((requirements.memoryTypeBits&(1u<<i)) && (memoryProperties.memoryTypes[i].propertyFlags &
                (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) == (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) { type=i;break; }
        if(type==UINT32_MAX) {Fail("no coherent host-visible uniform memory");return false;}
        VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};allocate.allocationSize=requirements.size;allocate.memoryTypeIndex=type;
        if (!Ok(vkAllocateMemory(instance.device,&allocate,nullptr,&memory),"upload memory") ||
            !Ok(vkBindBufferMemory(instance.device,upload,memory,0),"bind upload memory") ||
            !Ok(vkMapMemory(instance.device,memory,0,requirements.size,0,&mapped),"map upload memory")) return false;
        VkDescriptorPoolSize sizes[]={{VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER,regionDescriptors},{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,regionDescriptors},
            {VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE,regionDescriptors},{VK_DESCRIPTOR_TYPE_SAMPLER,regionDescriptors},{VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER,regionDescriptors}};
        VkDescriptorPoolCreateInfo poolInfo{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};
        poolInfo.maxSets=512;poolInfo.poolSizeCount=5;poolInfo.pPoolSizes=sizes;
        return Ok(vkCreateDescriptorPool(instance.device,&poolInfo,nullptr,&pool),"descriptor pool");
    }
    void Reset(uint64_t next) {
        for (auto view : views) vkDestroyImageView(instance.device,view,nullptr);
        views.clear();vkResetDescriptorPool(instance.device,pool,0);used=sets=0;frame=next;
    }
};
std::vector<std::unique_ptr<Region>> regions;

// A prepared issue keeps descriptor handles, not pointers into Unity's mutable command recording state.
// Its managed block remains unconsumed until the draw callback or the camera's completion sentinel.
struct PreparedDraw {
    uint64_t frame = 0;
    uint32_t serial = 0, pipeline = 0;
    ZvnResult result = ZvnResult_Recorded;
    UnityVulkanBuffer arguments{}, indices{};
    std::vector<VkDescriptorSet> sets;
    std::vector<ZvnDirectCommand> directCommands;
    UnityVulkanBuffer count{}; uint32_t countOffset=0;   // the IndirectCount draw's count, accessed with the arguments
};
std::map<std::pair<ZvnDraw*,uint32_t>,PreparedDraw> preparedDraws;
struct PreparedGroup {
    uint64_t frame = 0;
    uint32_t serial = 0;
    ZvnResult result = ZvnResult_Recorded;
    std::vector<uint32_t> offsets;
    std::vector<PreparedDraw> draws;
};
std::map<std::pair<ZvnDraw*,uint32_t>,PreparedGroup> preparedGroups;

struct Slot { uint32_t set, binding, source; VkDescriptorType type; VkShaderStageFlags stages; VkSampler sampler=VK_NULL_HANDLE; };
struct Pipeline {
    ZvnPipelineDesc desc{};
    std::vector<ZvnBinding> bindings;
    std::vector<Slot> slots;
    VkShaderModule vertex=VK_NULL_HANDLE, pixel=VK_NULL_HANDLE;
    std::vector<VkDescriptorSetLayout> layouts;
    VkPipelineLayout layout=VK_NULL_HANDLE;
    std::map<std::pair<VkRenderPass,int>,VkPipeline> variants;
    uint64_t lastFrame=0;
    uint64_t warmCalls=0, warmCreates=0, drawVariantHits=0, drawMissesAfterWarm=0;
    uint32_t initialPrepareCalls=0, initialRecordCalls=0;
    bool released=false;
    void Destroy() {
        for(auto& entry:variants)vkDestroyPipeline(instance.device,entry.second,nullptr);
        variants.clear();
        if(layout)vkDestroyPipelineLayout(instance.device,layout,nullptr);
        for(auto value:layouts)vkDestroyDescriptorSetLayout(instance.device,value,nullptr);
        for(auto& slot:slots)if(slot.sampler)vkDestroySampler(instance.device,slot.sampler,nullptr);
        if(vertex)vkDestroyShaderModule(instance.device,vertex,nullptr);
        if(pixel)vkDestroyShaderModule(instance.device,pixel,nullptr);
    }
};
// Only the first two real preparations/records of each pipeline read the clock. Warm-only calls do not spend these probes.
struct InitialTiming {
    using Clock=std::chrono::steady_clock;
    bool enabled;
    const char* operation;
    const ZvnDraw* draw;
    const Pipeline& pipeline;
    Clock::time_point start{},last{};
    InitialTiming(bool enabled,const char* operation,const ZvnDraw* draw,const Pipeline& pipeline)
        : enabled(enabled),operation(operation),draw(draw),pipeline(pipeline) { if(enabled)start=last=Clock::now(); }
    void Log(const char* phase,double milliseconds) const {
        if(milliseconds>5.0)__android_log_print(ANDROID_LOG_INFO,"ZantetsuVpVulkan",
            "initial %s %s %.3f ms pipeline=%u serial=%u frame=%llu name=%s",operation,phase,milliseconds,
            draw->pipeline,draw->serial,static_cast<unsigned long long>(currentFrame.load()),pipeline.desc.name);
    }
    void Stage(const char* phase) {
        if(!enabled)return;
        auto now=Clock::now();Log(phase,std::chrono::duration<double,std::milli>(now-last).count());last=now;
    }
    ~InitialTiming() { if(enabled){Stage("remaining");Log("total",std::chrono::duration<double,std::milli>(Clock::now()-start).count());} }
};
std::map<int,std::unique_ptr<Pipeline>> pipelines;
int nextPipeline=0;

// Unity's Vulkan reflection index contains stage flags in the high byte and descriptor set / binding in the low
// 24 bits. Validate it against SPIR-V decorations rather than treating a D3D register as a Vulkan binding.
std::map<uint32_t,VkDescriptorType> Reflect(const void* data,uint32_t bytes) {
    std::map<uint32_t,std::map<uint32_t,uint32_t>> decorations;
    std::map<uint32_t,uint32_t> pointers, types, variables;
    std::map<uint32_t,VkDescriptorType> result;
    if(!data || bytes<20 || bytes%4) return result;
    const auto* w=static_cast<const uint32_t*>(data);if(w[0]!=0x07230203)return result;
    for(uint32_t i=5;i<bytes/4;) {
        uint32_t n=w[i]>>16,op=w[i]&65535;if(!n || i+n>bytes/4)return {};
        if(op==71 && n>=3)decorations[w[i+1]][w[i+2]]=n>=4?w[i+3]:1;
        if(op==32 && n==4)pointers[w[i+1]]=w[i+3];
        if(op==59 && n>=4)variables[w[i+2]]=w[i+1];
        if((op==25 || op==26 || op==27 || op==30) && n>=2)types[w[i+1]]=op;
        i+=n;
    }
    for(auto& v:variables) {
        auto& d=decorations[v.first];if(!d.count(33)||!d.count(34))continue;
        uint32_t target=pointers[v.second],op=types[target];
        VkDescriptorType type=VK_DESCRIPTOR_TYPE_MAX_ENUM;
        if(op==25)type=VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE;
        if(op==26)type=VK_DESCRIPTOR_TYPE_SAMPLER;
        if(op==27)type=VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        if(op==30)type=decorations[target].count(3) ? VK_DESCRIPTOR_TYPE_STORAGE_BUFFER : VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
        if(type!=VK_DESCRIPTOR_TYPE_MAX_ENUM)result[(d[34]<<16)|d[33]]=type;
    }
    return result;
}
VkSampler MakeSampler(uint32_t kind) {
    VkSamplerCreateInfo info{VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO};
    info.magFilter=info.minFilter=kind==ZvnSampler_PointClamp?VK_FILTER_NEAREST:VK_FILTER_LINEAR;
    info.mipmapMode=kind>=ZvnSampler_TrilinearClamp?VK_SAMPLER_MIPMAP_MODE_LINEAR:VK_SAMPLER_MIPMAP_MODE_NEAREST;
    bool wrap=kind==ZvnSampler_LinearWrap||kind==ZvnSampler_TrilinearWrap;
    info.addressModeU=info.addressModeV=info.addressModeW=wrap?VK_SAMPLER_ADDRESS_MODE_REPEAT:VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    info.compareEnable=kind>=ZvnSampler_CompareLinearClampLessEqual;
    info.compareOp=kind==ZvnSampler_CompareLinearClampGreaterEqual?VK_COMPARE_OP_GREATER_OR_EQUAL:VK_COMPARE_OP_LESS_OR_EQUAL;
    info.maxLod=VK_LOD_CLAMP_NONE;
    VkSampler sampler=VK_NULL_HANDLE;
    if(!Ok(vkCreateSampler(instance.device,&info,nullptr,&sampler),"sampler"))return VK_NULL_HANDLE;
    return sampler;
}
bool Shader(const void* bytes,uint32_t size,VkShaderModule& out) {
    if(!bytes || size<20 || size%4 || static_cast<const uint32_t*>(bytes)[0]!=0x07230203) {Fail("shader is not SPIR-V");return false;}
    VkShaderModuleCreateInfo info{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};info.codeSize=size;info.pCode=static_cast<const uint32_t*>(bytes);
    return Ok(vkCreateShaderModule(instance.device,&info,nullptr,&out),"shader module");
}
bool CreatePipeline(Pipeline& p,const ZvnPipelineDesc& desc) {
    p.desc=desc;p.desc.vertexBytecode=nullptr;p.desc.pixelBytecode=nullptr;p.desc.bindings=nullptr;p.bindings.assign(desc.bindings,desc.bindings+desc.bindingCount);
    if(!Shader(desc.vertexBytecode,desc.vertexBytecodeSize,p.vertex) ||
        (desc.pixelBytecodeSize && !Shader(desc.pixelBytecode,desc.pixelBytecodeSize,p.pixel)))return false;
    const auto vertex=Reflect(desc.vertexBytecode,desc.vertexBytecodeSize),pixel=Reflect(desc.pixelBytecode,desc.pixelBytecodeSize);
    for(uint32_t i=0;i<p.bindings.size();i++) {
        const auto& b=p.bindings[i];uint32_t key=b.shaderRegister&0xffffff;
        const auto& active=b.stage==ZvnStage_Vertex?vertex:pixel;
        auto actual=active.find(key);if(actual==active.end()) {Fail("reflection binding is absent from shader");return false;}
        VkShaderStageFlags stages=b.stage==ZvnStage_Vertex?VK_SHADER_STAGE_VERTEX_BIT:VK_SHADER_STAGE_FRAGMENT_BIT;
        auto same=std::find_if(p.slots.begin(),p.slots.end(),[&](const Slot& s){return s.set==(key>>16)&&s.binding==(key&65535);});
        if(same!=p.slots.end()) {
            if(same->type!=actual->second || std::strcmp(p.bindings[same->source].name,b.name)) {Fail("incompatible descriptor collision");return false;}
            same->stages|=stages;continue;
        }
        Slot slot{key>>16,key&65535,i,actual->second,stages};
        if(slot.set>3){Fail("unsupported descriptor set");return false;}
        if(slot.type==VK_DESCRIPTOR_TYPE_SAMPLER || slot.type==VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER) {
            slot.sampler=MakeSampler(slot.type==VK_DESCRIPTOR_TYPE_SAMPLER?b.extra:b.extra2);
            if(!slot.sampler)return false;
        }
        p.slots.push_back(slot);
    }
    // Every active SPIR-V descriptor must be represented, including shared samplers. Extra reflection records and
    // missing descriptors both fail before a pipeline is made.
    for (const auto* active : { &vertex, &pixel }) for (const auto& descriptor : *active)
        if (std::none_of(p.slots.begin(),p.slots.end(),[&](const Slot& slot){return (slot.set<<16|slot.binding)==descriptor.first;})) {
            Fail("SPIR-V descriptor missing from reflection");return false;
        }
    uint32_t count=0;for(auto& slot:p.slots)count=std::max(count,slot.set+1);
    p.layouts.resize(count);
    for(uint32_t set=0;set<count;set++) {
        std::vector<VkDescriptorSetLayoutBinding> bindings;
        for(auto& slot:p.slots)if(slot.set==set)bindings.push_back({slot.binding,slot.type,1,slot.stages,nullptr});
        VkDescriptorSetLayoutCreateInfo info{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};info.bindingCount=uint32_t(bindings.size());info.pBindings=bindings.data();
        if(!Ok(vkCreateDescriptorSetLayout(instance.device,&info,nullptr,&p.layouts[set]),"descriptor layout"))return false;
    }
    VkPipelineLayoutCreateInfo info{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};info.setLayoutCount=count;info.pSetLayouts=p.layouts.data();
    return Ok(vkCreatePipelineLayout(instance.device,&info,nullptr,&p.layout),"pipeline layout");
}
VkPipeline Variant(Pipeline& p,const UnityVulkanRecordingState& state,bool warming=false) {
    const auto key=std::make_pair(state.renderPass,state.subPassIndex);
    auto found=p.variants.find(key);if(found!=p.variants.end()){if(!warming)p.drawVariantHits++;return found->second;}
    if(!warming && p.warmCalls)p.drawMissesAfterWarm++;
    VkPipelineShaderStageCreateInfo stages[2]{};
    stages[0]={VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,nullptr,0,VK_SHADER_STAGE_VERTEX_BIT,p.vertex,"main",nullptr};
    stages[1]={VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,nullptr,0,VK_SHADER_STAGE_FRAGMENT_BIT,p.pixel,"main",nullptr};
    VkPipelineVertexInputStateCreateInfo vertex{VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO};
    VkPipelineInputAssemblyStateCreateInfo assembly{VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO};assembly.topology=VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    VkPipelineViewportStateCreateInfo viewport{VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO};viewport.viewportCount=viewport.scissorCount=1;
    VkPipelineRasterizationStateCreateInfo raster{VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO};
    raster.polygonMode=VK_POLYGON_MODE_FILL;raster.lineWidth=1;raster.cullMode=p.desc.cullMode==ZvnCull_Back?VK_CULL_MODE_BACK_BIT:p.desc.cullMode==ZvnCull_Front?VK_CULL_MODE_FRONT_BIT:VK_CULL_MODE_NONE;
    raster.frontFace=p.desc.frontCounterClockwise?VK_FRONT_FACE_COUNTER_CLOCKWISE:VK_FRONT_FACE_CLOCKWISE;
    raster.depthBiasEnable=p.desc.depthBias!=0 || p.desc.slopeScaledDepthBias!=0;
    raster.depthBiasConstantFactor=float(p.desc.depthBias);raster.depthBiasSlopeFactor=p.desc.slopeScaledDepthBias;raster.depthBiasClamp=p.desc.depthBiasClamp;
    VkPipelineMultisampleStateCreateInfo samples{VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO};samples.rasterizationSamples=VkSampleCountFlagBits(p.desc.sampleCount);
    VkPipelineDepthStencilStateCreateInfo depth{VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO};
    depth.depthTestEnable=p.desc.depthFormat!=0;depth.depthWriteEnable=p.desc.depthWrite;depth.depthCompareOp=VkCompareOp(p.desc.depthFunc-1);
    if(std::strstr(p.desc.name,"motion-plugin") || std::strstr(p.desc.name,"motion-direct")) {
        depth.stencilTestEnable=VK_TRUE;
        depth.front=depth.back={VK_STENCIL_OP_KEEP,VK_STENCIL_OP_REPLACE,VK_STENCIL_OP_KEEP,VK_COMPARE_OP_ALWAYS,1,1,1};
    }
    VkPipelineColorBlendAttachmentState attachment{};attachment.colorWriteMask=p.desc.colourWrite?15:0;
    VkPipelineColorBlendStateCreateInfo blend{VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO};
    blend.attachmentCount=p.desc.renderTargetFormat?1:0;blend.pAttachments=&attachment;
    VkDynamicState dynamicStates[]={VK_DYNAMIC_STATE_VIEWPORT,VK_DYNAMIC_STATE_SCISSOR};
    VkPipelineDynamicStateCreateInfo dynamic{VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO};dynamic.dynamicStateCount=2;dynamic.pDynamicStates=dynamicStates;
    VkGraphicsPipelineCreateInfo info{VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO};
    info.stageCount=p.pixel?2:1;info.pStages=stages;info.pVertexInputState=&vertex;info.pInputAssemblyState=&assembly;
    info.pViewportState=&viewport;info.pRasterizationState=&raster;info.pMultisampleState=&samples;info.pDepthStencilState=&depth;
    info.pColorBlendState=&blend;info.pDynamicState=&dynamic;info.layout=p.layout;info.renderPass=state.renderPass;info.subpass=uint32_t(state.subPassIndex);
    VkPipeline pipeline=VK_NULL_HANDLE;
    if(!Ok(vkCreateGraphicsPipelines(instance.device,VK_NULL_HANDLE,1,&info,nullptr,&pipeline),"graphics pipeline"))return VK_NULL_HANDLE;
    p.variants.emplace(key,pipeline);if(warming)p.warmCreates++;return pipeline;
}
void Retire(uint64_t safe) {
    // Keys contain managed addresses, but retirement never dereferences them: the camera sentinel may
    // already have released an abandoned allocation before the GPU's frame region becomes safe.
    for(auto it=preparedGroups.begin();it!=preparedGroups.end();) {
        if(it->second.frame<=safe)it=preparedGroups.erase(it);else ++it;
    }
    for(auto it=preparedDraws.begin();it!=preparedDraws.end();) {
        if(it->second.frame<=safe)it=preparedDraws.erase(it);else ++it;
    }
    for(auto it=pipelines.begin();it!=pipelines.end();) {
        if(it->second->released && it->second->lastFrame<=safe){it->second->Destroy();it=pipelines.erase(it);}else ++it;
    }
}
Region* RegionFor(uint64_t frame,uint64_t safe) {
    for(auto& r:regions)if(r->frame==frame && r->sets<500 && r->used<regionBytes-8192)return r.get();
    for(auto& r:regions)if(r->frame<=safe && r->frame!=frame){r->Reset(frame);return r.get();}
    if(regions.size()>=regionCount)return nullptr;
    auto r=std::make_unique<Region>();if(!r->Create()){r->Destroy();return nullptr;}r->frame=frame;
    regions.push_back(std::move(r));return regions.back().get();
}
void Consume(ZvnDraw* draw,ZvnResult result) {
    draw->result=result;
    if(hold){held.push_back(draw);counters.eventsHeld++;}else __atomic_store_n(&draw->consumed,1,__ATOMIC_RELEASE);
}
void Refuse(ZvnDraw* draw,ZvnResult reason) {
    counters.drawsRefused++;counters.lastRefusal=reason;counters.lastRefusalSerial=draw->serial;Consume(draw,reason);
}
bool Range(uint32_t offset,uint32_t count,uint32_t stride,uint32_t total) {return offset<=total && uint64_t(count)*stride<=total-offset;}
const ZvnResource* Resource(const ZvnDraw* draw,uint32_t binding) {
    const auto* resources=reinterpret_cast<const ZvnResource*>(reinterpret_cast<const char*>(draw)+draw->resourcesOffset);
    for(uint32_t i=0;i<draw->resourceCount;i++)if(resources[i].binding==binding)return resources+i;
    return nullptr;
}
const ZvnConstants* Constants(const ZvnDraw* draw,uint32_t binding) {
    const auto* constants=reinterpret_cast<const ZvnConstants*>(reinterpret_cast<const char*>(draw)+draw->constantsOffset);
    for(uint32_t i=0;i<draw->constantsCount;i++)if(constants[i].binding==binding)return constants+i;
    return nullptr;
}
const ZvnDrawExtension* Extension(const ZvnDraw* draw) {
    return reinterpret_cast<const ZvnDrawExtension*>(reinterpret_cast<const char*>(draw)+sizeof(ZvnDraw));
}
bool Direct(const ZvnDraw* draw) {
    return draw->version==ZVN_VULKAN_DRAW_VERSION && Extension(draw)->mode==ZvnDrawMode_Direct;
}
bool IndirectCountMode(const ZvnDraw* draw) {
    return draw->version==ZVN_VULKAN_DRAW_VERSION && Extension(draw)->mode==ZvnDrawMode_IndirectCount;
}
const ZvnIndirectCount* IndirectCount(const ZvnDraw* draw) {
    return reinterpret_cast<const ZvnIndirectCount*>(reinterpret_cast<const char*>(draw)+Extension(draw)->commandsOffset);
}
ZvnResult ValidateDraw(const ZvnDraw* draw) {
    if(draw->version!=ZVN_CONTRACT_VERSION && draw->version!=ZVN_VULKAN_DRAW_VERSION)return ZvnResult_BadVersion;
    if(draw->totalBytes<sizeof(ZvnDraw))return ZvnResult_BadResource;
    if(draw->version==ZVN_VULKAN_DRAW_VERSION) {
        if(draw->totalBytes<sizeof(ZvnDraw)+sizeof(ZvnDrawExtension))return ZvnResult_BadResource;
        const auto* extension=Extension(draw);
        if(extension->reserved || extension->mode==ZvnDrawMode_Group || extension->mode>ZvnDrawMode_IndirectCount)return ZvnResult_BadResource;
        if(extension->mode==ZvnDrawMode_IndirectCount) {
            // The one count record, in the block past the headers; the run itself is checked as an indirect draw's below.
            if(extension->commandsCount!=1 || extension->commandsOffset<sizeof(ZvnDraw)+sizeof(ZvnDrawExtension) || extension->commandsOffset%8 ||
               !Range(extension->commandsOffset,1,sizeof(ZvnIndirectCount),draw->totalBytes))return ZvnResult_BadResource;
            const auto* record=IndirectCount(draw);
            if(!record->countBuffer || record->reserved>ZVN_COUNT_DRAW_FROM_COPY || record->countOffset%4)return ZvnResult_BadResource;
            if(record->reserved==ZVN_COUNT_DRAW_FROM_COPY && (!record->zeroBuffer || record->zeroBytes))return ZvnResult_BadResource;
            // zeroBuffer with zeroBytes 0: a diagnosis's snapshot slot (the count copied there, 4 bytes); with bytes: a fill (unused now).
            if(record->zeroBuffer ? (record->zeroBytes%4 || record->zeroOffset%4) : (record->zeroBytes || record->zeroOffset))return ZvnResult_BadResource;
            if(!indirectCountReady)return ZvnResult_NotAvailable;
        } else if(extension->mode==ZvnDrawMode_Direct) {
            if(!extension->commandsCount || extension->commandsCount>ZVN_MAX_DIRECT_COMMANDS ||
               extension->commandsOffset<sizeof(ZvnDraw)+sizeof(ZvnDrawExtension) || extension->commandsOffset%4 ||
               !Range(extension->commandsOffset,extension->commandsCount,sizeof(ZvnDirectCommand),draw->totalBytes))return ZvnResult_BadResource;
            const auto* commands=reinterpret_cast<const ZvnDirectCommand*>(reinterpret_cast<const char*>(draw)+extension->commandsOffset);
            uint32_t indexWidth=draw->indexFormat==42?4:2;
            for(uint32_t i=0;i<extension->commandsCount;i++) {
                const auto& command=commands[i];
                if(!command.indexCount || !command.instanceCount || uint64_t(command.firstInstance)+command.instanceCount>ZVN_MAX_DIRECT_COMMANDS ||
                   uint64_t(command.firstIndex)+command.indexCount>draw->indexBufferBytes/indexWidth)return ZvnResult_BadResource;
            }
        } else if(extension->commandsOffset || extension->commandsCount)return ZvnResult_BadResource;
    }
    if(!draw->viewportCount || draw->viewportCount>ZVN_MAX_VIEWPORTS || (draw->indexFormat!=42 && draw->indexFormat!=57) ||
       (draw->resourceCount && draw->resourcesOffset%alignof(ZvnResource)) ||
       (draw->constantsCount && draw->constantsOffset%alignof(ZvnConstants)) ||
       !Range(draw->resourcesOffset,draw->resourceCount,sizeof(ZvnResource),draw->totalBytes) ||
       !Range(draw->constantsOffset,draw->constantsCount,sizeof(ZvnConstants),draw->totalBytes))return ZvnResult_BadResource;
    if(!Direct(draw) && (!draw->commandCount || draw->argumentOffset%24))return ZvnResult_BadResource;
    if(!available)return ZvnResult_NotAvailable;
    auto p=pipelines.find(int(draw->pipeline));
    if(p==pipelines.end() || p->second->released)return ZvnResult_NoPipeline;
    return ZvnResult_Recorded;
}
ZvnResult ParseGroup(const ZvnDraw* root,std::vector<uint32_t>& offsets) {
    if(root->version!=ZVN_VULKAN_DRAW_VERSION)return ZvnResult_BadVersion;
    const uint32_t headerBytes=sizeof(ZvnDraw)+sizeof(ZvnDrawExtension);
    if(root->totalBytes<headerBytes)return ZvnResult_BadResource;
    const auto* extension=Extension(root);
    if(extension->mode!=ZvnDrawMode_Group || extension->reserved || !extension->commandsCount ||
       extension->commandsCount>ZVN_MAX_GROUP_DRAWS || extension->commandsOffset<headerBytes || extension->commandsOffset%4 ||
       !Range(extension->commandsOffset,extension->commandsCount,sizeof(uint32_t),root->totalBytes))return ZvnResult_BadResource;
    const auto* table=reinterpret_cast<const uint32_t*>(reinterpret_cast<const char*>(root)+extension->commandsOffset);
    // The completion header, offset table and inline child allocations must not alias. A malformed offset
    // is checked before reading the child header, including its totalBytes field. Nested groups are refused.
    std::vector<std::pair<uint32_t,uint32_t>> intervals={{0,headerBytes},
        {extension->commandsOffset,extension->commandsOffset+extension->commandsCount*uint32_t(sizeof(uint32_t))}};
    offsets.clear();offsets.reserve(extension->commandsCount);
    for(uint32_t i=0;i<extension->commandsCount;i++) {
        uint32_t offset=table[i];
        if(offset%8 || !Range(offset,1,sizeof(ZvnDraw),root->totalBytes))return ZvnResult_BadResource;
        for(const auto& interval:intervals)if(offset>=interval.first && offset<interval.second)return ZvnResult_BadResource;
        const auto* child=reinterpret_cast<const ZvnDraw*>(reinterpret_cast<const char*>(root)+offset);
        if(child->version!=ZVN_CONTRACT_VERSION && child->version!=ZVN_VULKAN_DRAW_VERSION)return ZvnResult_BadVersion;
        uint32_t childHeader=child->version==ZVN_VULKAN_DRAW_VERSION?headerBytes:uint32_t(sizeof(ZvnDraw));
        if(child->totalBytes<childHeader || !Range(offset,1,child->totalBytes,root->totalBytes))return ZvnResult_BadResource;
        if(child->version==ZVN_VULKAN_DRAW_VERSION && Extension(child)->mode==ZvnDrawMode_Group)return ZvnResult_BadResource;
        uint32_t end=offset+child->totalBytes;
        for(const auto& interval:intervals)if(offset<interval.second && interval.first<end)return ZvnResult_BadResource;
        intervals.emplace_back(offset,end);offsets.push_back(offset);
    }
    return ZvnResult_Recorded;
}
bool Buffer(void* pointer,VkPipelineStageFlags stage,VkAccessFlags access,UnityVulkanBuffer& buffer) {
    return pointer && unity->AccessBuffer(pointer,stage,access,kUnityVulkanResourceAccess_PipelineBarrier,&buffer) && buffer.buffer;
}
bool Prepare(ZvnDraw* draw,Pipeline& pipeline,PreparedDraw& prepared) {
    bool diagnose=pipeline.initialPrepareCalls<2;if(diagnose)pipeline.initialPrepareCalls++;
    InitialTiming timing(diagnose,"Prepare",draw,pipeline);
    if(!Range(draw->resourcesOffset,draw->resourceCount,sizeof(ZvnResource),draw->totalBytes) ||
       !Range(draw->constantsOffset,draw->constantsCount,sizeof(ZvnConstants),draw->totalBytes)) {prepared.result=ZvnResult_BadResource;return false;}
    unity->EnsureOutsideRenderPass();timing.Stage("EnsureOutside");
    UnityVulkanBuffer arguments{},indices{};
    bool direct=Direct(draw);
    if((!direct && (!Buffer(draw->argumentBuffer,VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT,VK_ACCESS_INDIRECT_COMMAND_READ_BIT,arguments) ||
        uint64_t(draw->argumentOffset)+uint64_t(draw->commandCount-1)*24+24>arguments.sizeInBytes)) ||
       !Buffer(draw->indexBuffer,VK_PIPELINE_STAGE_VERTEX_INPUT_BIT,VK_ACCESS_INDEX_READ_BIT,indices) ||
       draw->indexBufferBytes>indices.sizeInBytes){Fail("invalid indirect or index buffer");prepared.result=ZvnResult_BadResource;return false;}
    timing.Stage("AccessArgumentsIndex");
    if(IndirectCountMode(draw)) {
        // The count the GPU wrote: accessed as the arguments are, for the same indirect read, before the pass.
        const auto* record=IndirectCount(draw);
        // Only a slot whose zeroing this plugin recorded in this very frame: a zero event refused or never run leaves
        // the count draw refused too (the managed side draws fixed from the next frame on).
        UnityVulkanRecordingState zeroCheck{};
        if(!unity->CommandRecordingState(&zeroCheck,kUnityVulkanGraphicsQueueAccess_DontCare) || !CountSlotZeroed(zeroCheck.currentFrameNumber,record->countBuffer,record->countOffset)) {
            countDrawsRefusedUnzeroed++;Fail("count slot not zeroed in this frame");prepared.result=ZvnResult_BadResource;return false;
        }
        // fromCopy: the draw reads a transfer copy of the count (see ZVN_COUNT_DRAW_FROM_COPY); the compute-written
        // buffer is then read by the copy alone.
        const bool fromCopy=record->reserved==ZVN_COUNT_DRAW_FROM_COPY;
        bool countCopied=false;
        if(!Buffer(record->countBuffer,fromCopy?VK_PIPELINE_STAGE_TRANSFER_BIT:VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT,
               fromCopy?VK_ACCESS_TRANSFER_READ_BIT:VK_ACCESS_INDIRECT_COMMAND_READ_BIT,prepared.count) ||
           uint64_t(record->countOffset)+4>prepared.count.sizeInBytes){Fail("invalid indirect count buffer");prepared.result=ZvnResult_BadResource;return false;}
        prepared.countOffset=record->countOffset;
        UnityVulkanBuffer zero{};
        if(record->zeroBuffer && (!Buffer(record->zeroBuffer,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_ACCESS_TRANSFER_WRITE_BIT,zero) ||
           uint64_t(record->zeroOffset)+std::max<uint32_t>(record->zeroBytes,4)>zero.sizeInBytes)){Fail("invalid count buffer to zero or snapshot");prepared.result=ZvnResult_BadResource;return false;}
        // Beside Unity's own barriers (AccessBuffer above): the compaction's compute writes of this very command
        // buffer must be visible to the indirect reader and to the vertex stage that reads the entries. One memory
        // barrier, no wait, no submission of its own. Then the previous frame's count slots are zeroed here, outside
        // the render pass: after every reader of theirs (an execution barrier) and before any compute that adds to them.
        UnityVulkanRecordingState recording{};
        if(unity->CommandRecordingState(&recording,kUnityVulkanGraphicsQueueAccess_DontCare) && recording.commandBuffer) {
            VkMemoryBarrier barrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
            barrier.srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT;barrier.dstAccessMask=VK_ACCESS_INDIRECT_COMMAND_READ_BIT|VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_TRANSFER_READ_BIT;
            vkCmdPipelineBarrier(recording.commandBuffer,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT|VK_PIPELINE_STAGE_VERTEX_SHADER_BIT|VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&barrier,0,nullptr,0,nullptr);
            if(record->zeroBuffer && record->zeroBytes==0) {
                // Diagnosis (2026-10-10): the count as this draw will read it -- after the same barrier -- copied to the
                // managed snapshot slot, which the managed side reads back for the record. Nothing of a draw depends on it.
                VkBufferCopy copy{prepared.countOffset,record->zeroOffset,4};
                vkCmdCopyBuffer(recording.commandBuffer,prepared.count.buffer,zero.buffer,1,&copy);
                if(fromCopy) {
                    // The copy's write made visible to the indirect reader: the plugin's own barrier beside Unity's below.
                    VkMemoryBarrier copied{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
                    copied.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;copied.dstAccessMask=VK_ACCESS_INDIRECT_COMMAND_READ_BIT;
                    vkCmdPipelineBarrier(recording.commandBuffer,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT,0,1,&copied,0,nullptr,0,nullptr);
                    countCopied=true;
                } else countSnapshots++;
            } else if(record->zeroBuffer) {
                VkMemoryBarrier before{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
                before.srcAccessMask=VK_ACCESS_INDIRECT_COMMAND_READ_BIT|VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;before.dstAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;
                vkCmdPipelineBarrier(recording.commandBuffer,VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT|VK_PIPELINE_STAGE_VERTEX_SHADER_BIT|VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&before,0,nullptr,0,nullptr);
                vkCmdFillBuffer(recording.commandBuffer,zero.buffer,record->zeroOffset,record->zeroBytes,0u);
                VkMemoryBarrier after{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
                after.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;after.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT|VK_ACCESS_INDIRECT_COMMAND_READ_BIT;
                vkCmdPipelineBarrier(recording.commandBuffer,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT|VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT,0,1,&after,0,nullptr,0,nullptr);
                countSlotsZeroed++;
            }
        }
        if(fromCopy) {
            // The draw's count is the copy: no copy recorded (no command buffer), no draw.
            if(!countCopied || !Buffer(record->zeroBuffer,VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT,VK_ACCESS_INDIRECT_COMMAND_READ_BIT,prepared.count)) {
                Fail("the count's copy was not recorded");prepared.result=ZvnResult_NoCommandList;return false;
            }
            prepared.countOffset=record->zeroOffset;countCopies++;
        }
    }
    if(direct) {
        const auto* extension=Extension(draw);
        const auto* commands=reinterpret_cast<const ZvnDirectCommand*>(reinterpret_cast<const char*>(draw)+extension->commandsOffset);
        prepared.directCommands.assign(commands,commands+extension->commandsCount);
    }
    // All resource accesses and their barriers precede re-entering the render pass and querying recording state.
    struct Resolved { UnityVulkanBuffer buffer{};UnityVulkanImage image{};VkImageLayout layout{};const ZvnResource* resource=nullptr;const ZvnConstants* constants=nullptr; };
    std::vector<Resolved> resolved(pipeline.slots.size());
    for(size_t i=0;i<pipeline.slots.size();i++) {
        const auto& slot=pipeline.slots[i];const auto& binding=pipeline.bindings[slot.source];auto& value=resolved[i];
        VkPipelineStageFlags stages=0;
        if(slot.stages&VK_SHADER_STAGE_VERTEX_BIT)stages|=VK_PIPELINE_STAGE_VERTEX_SHADER_BIT;
        if(slot.stages&VK_SHADER_STAGE_FRAGMENT_BIT)stages|=VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT;
        if(slot.type==VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER && binding.kind!=ZvnBinding_CommandConstant) {
            value.constants=Constants(draw,slot.source);const auto* c=value.constants;
            if(!c || (!c->resource && c->size!=binding.extra) || (!c->resource && !Range(c->offset,1,c->size,draw->totalBytes)) ||
               (c->resource && (!Buffer(c->resource,stages,VK_ACCESS_UNIFORM_READ_BIT,value.buffer) || uint64_t(c->resourceOffset)+binding.extra>value.buffer.sizeInBytes || c->resourceOffset%properties.limits.minUniformBufferOffsetAlignment))) {
                Fail(binding.name);prepared.result=ZvnResult_BadConstants;return false;
            }
        } else if(slot.type==VK_DESCRIPTOR_TYPE_STORAGE_BUFFER) {
            value.resource=Resource(draw,slot.source);const auto* r=value.resource;
            if(!r || r->kind>1 || (r->kind==1 && !r->stride) ||
                !Buffer(r->resource,stages,VK_ACCESS_SHADER_READ_BIT,value.buffer) || !r->elementCount ||
                (uint64_t(r->firstElement)+r->elementCount)*(r->kind==0?4:r->stride)>value.buffer.sizeInBytes) {
                Fail(binding.name);prepared.result=ZvnResult_BadResource;return false;
            }
        } else if(slot.type==VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE || slot.type==VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER) {
            value.resource=Resource(draw,slot.source);const auto* r=value.resource;
            if(!r || (r->kind!=2 && r->kind!=3) || !r->resource){Fail(binding.name);prepared.result=ZvnResult_BadResource;return false;}
            bool isDepth=!std::strcmp(binding.name,"_MainLightShadowmapTexture");
            value.layout=isDepth?VK_IMAGE_LAYOUT_DEPTH_STENCIL_READ_ONLY_OPTIMAL:VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
            bool success=r->kind==3 ? unity->AccessRenderBufferTexture(static_cast<UnityRenderBuffer>(r->resource),nullptr,value.layout,stages,VK_ACCESS_SHADER_READ_BIT,kUnityVulkanResourceAccess_PipelineBarrier,&value.image) :
                unity->AccessTexture(r->resource,nullptr,value.layout,stages,VK_ACCESS_SHADER_READ_BIT,kUnityVulkanResourceAccess_PipelineBarrier,&value.image);
            if(!success || !value.image.image){Fail(binding.name);prepared.result=ZvnResult_BadResource;return false;}
        }
    }
    timing.Stage("AccessResources");
    UnityVulkanRecordingState state{};
    if(!unity->CommandRecordingState(&state,kUnityVulkanGraphicsQueueAccess_DontCare) || !state.commandBuffer) {
        Fail("no Unity Vulkan command buffer for preparation");prepared.result=ZvnResult_NoCommandList;return false;
    }
    currentFrame=state.currentFrameNumber;safeFrame=state.safeFrameNumber;Retire(state.safeFrameNumber);
    timing.Stage("RecordingStateRetire");
    Region* region=RegionFor(state.currentFrameNumber,state.safeFrameNumber);
    if(!region){prepared.result=ZvnResult_RingFull;return false;}
    timing.Stage("Region");
    std::vector<VkDescriptorSet> sets(pipeline.layouts.size());
    VkDescriptorSetAllocateInfo allocate{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};allocate.descriptorPool=region->pool;
    allocate.descriptorSetCount=uint32_t(sets.size());allocate.pSetLayouts=pipeline.layouts.data();
    if(!Ok(vkAllocateDescriptorSets(instance.device,&allocate,sets.data()),"descriptor allocation")){prepared.result=ZvnResult_RingFull;return false;}
    region->sets+=uint32_t(sets.size());
    timing.Stage("DescriptorAllocate");
    std::vector<VkWriteDescriptorSet> writes(pipeline.slots.size());
    std::vector<VkDescriptorBufferInfo> buffers(writes.size());std::vector<VkDescriptorImageInfo> images(writes.size());
    for(size_t i=0;i<writes.size();i++) {
        const auto& slot=pipeline.slots[i];const auto& binding=pipeline.bindings[slot.source];const auto& value=resolved[i];
        auto& write=writes[i];write.sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;write.dstSet=sets[slot.set];write.dstBinding=slot.binding;write.descriptorType=slot.type;write.descriptorCount=1;
        if(slot.type==VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER) {
            if(binding.kind==ZvnBinding_CommandConstant || !value.constants->resource) {
                uint32_t alignment=uint32_t(properties.limits.minUniformBufferOffsetAlignment);
                uint32_t offset=(region->used+alignment-1)/alignment*alignment,size=binding.kind==ZvnBinding_CommandConstant?4:value.constants->size;
                if(uint64_t(offset)+size>regionBytes){prepared.result=ZvnResult_RingFull;return false;}
                if(binding.kind==ZvnBinding_CommandConstant){uint32_t base=draw->argumentOffset/24;std::memcpy(static_cast<char*>(region->mapped)+offset,&base,4);}
                else std::memcpy(static_cast<char*>(region->mapped)+offset,reinterpret_cast<const char*>(draw)+value.constants->offset,size);
                buffers[i]={region->upload,offset,size};region->used=offset+size;counters.constantBytesUploaded+=size;
            }else buffers[i]={value.buffer.buffer,value.constants->resourceOffset,binding.extra};
            write.pBufferInfo=&buffers[i];
        }else if(slot.type==VK_DESCRIPTOR_TYPE_STORAGE_BUFFER) {
            const auto* r=value.resource;uint32_t stride=r->kind==0?4:r->stride;
            buffers[i]={value.buffer.buffer,uint64_t(r->firstElement)*stride,uint64_t(r->elementCount)*stride};write.pBufferInfo=&buffers[i];
        }else {
            images[i].sampler=slot.sampler;
            if(slot.type!=VK_DESCRIPTOR_TYPE_SAMPLER) {
                VkImageViewCreateInfo view{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};view.image=value.image.image;view.format=value.image.format;
                view.viewType=binding.extra==ZvnTexture_Cube?VK_IMAGE_VIEW_TYPE_CUBE:binding.extra==ZvnTexture_2DArray?VK_IMAGE_VIEW_TYPE_2D_ARRAY:VK_IMAGE_VIEW_TYPE_2D;
                view.subresourceRange.aspectMask=value.image.aspect&VK_IMAGE_ASPECT_DEPTH_BIT?VK_IMAGE_ASPECT_DEPTH_BIT:VK_IMAGE_ASPECT_COLOR_BIT;
                view.subresourceRange.levelCount=value.image.mipCount;view.subresourceRange.layerCount=binding.extra==ZvnTexture_2D?1:value.image.layers;
                if(!Ok(vkCreateImageView(instance.device,&view,nullptr,&images[i].imageView),"texture view")){prepared.result=ZvnResult_BadResource;return false;}
                region->views.push_back(images[i].imageView);images[i].imageLayout=value.layout;
            }
            write.pImageInfo=&images[i];
        }
    }
    vkUpdateDescriptorSets(instance.device,uint32_t(writes.size()),writes.data(),0,nullptr);counters.descriptorsWritten+=writes.size();
    timing.Stage("DescriptorWrite");
    prepared.arguments=arguments;prepared.indices=indices;prepared.sets=std::move(sets);
    prepared.frame=state.currentFrameNumber;prepared.serial=draw->serial;prepared.pipeline=draw->pipeline;
    pipeline.lastFrame=state.currentFrameNumber;
    return true;
}
bool Record(ZvnDraw* draw,Pipeline& pipeline,const PreparedDraw& prepared,bool completion=true) {
    bool diagnose=pipeline.initialRecordCalls<2;if(diagnose)pipeline.initialRecordCalls++;
    InitialTiming timing(diagnose,"Record",draw,pipeline);
    auto refuse=[&](ZvnResult result){if(completion)Refuse(draw,result);else draw->result=result;return false;};
    if(prepared.result!=ZvnResult_Recorded)return refuse(prepared.result);
    unity->EnsureInsideRenderPass();timing.Stage("EnsureInside");
    UnityVulkanRecordingState state{};
    if(!unity->CommandRecordingState(&state,kUnityVulkanGraphicsQueueAccess_DontCare) ||
       !state.commandBuffer || !state.renderPass || state.subPassIndex<0 ||
       state.currentFrameNumber!=prepared.frame || draw->serial!=prepared.serial || draw->pipeline!=prepared.pipeline) {
        Fail("prepared Vulkan draw has no matching current render pass");return refuse(ZvnResult_NoCommandList);
    }
    timing.Stage("RecordingState");
    VkPipeline vkPipeline=Variant(pipeline,state);if(!vkPipeline)return refuse(ZvnResult_NoPipeline);
    timing.Stage("Variant");
    const auto& sets=prepared.sets;const auto& arguments=prepared.arguments;const auto& indices=prepared.indices;
    vkCmdBindPipeline(state.commandBuffer,VK_PIPELINE_BIND_POINT_GRAPHICS,vkPipeline);
    vkCmdBindDescriptorSets(state.commandBuffer,VK_PIPELINE_BIND_POINT_GRAPHICS,pipeline.layout,0,uint32_t(sets.size()),sets.data(),0,nullptr);
    vkCmdBindIndexBuffer(state.commandBuffer,indices.buffer,0,draw->indexFormat==42?VK_INDEX_TYPE_UINT32:VK_INDEX_TYPE_UINT16);
    uint32_t limit=properties.limits.maxDrawIndirectCount;
    for(uint32_t i=0;i<draw->viewportCount;i++) {
        const auto& v=draw->viewports[i];VkViewport viewport{v.x,v.y,v.width,v.height,v.minDepth,v.maxDepth};
        VkRect2D scissor{{v.scissorLeft,v.scissorTop},{uint32_t(v.scissorRight-v.scissorLeft),uint32_t(v.scissorBottom-v.scissorTop)}};
        vkCmdSetViewport(state.commandBuffer,0,1,&viewport);vkCmdSetScissor(state.commandBuffer,0,1,&scissor);
        if(Direct(draw)) {
            // One pipeline/descriptor bind for the packet, then only the few CPU-selected indexed calls.
            // firstInstance chooses the UBO entry; no indirect buffer, GPU selection or argument read is involved.
            for(const auto& command:prepared.directCommands) {
                vkCmdDrawIndexed(state.commandBuffer,command.indexCount,command.instanceCount,command.firstIndex,command.vertexOffset,command.firstInstance);
                counters.drawsRecorded++;counters.commandsRecorded++;
            }
        } else if(IndirectCountMode(draw)) {
            // Experiment: the compacted run drawn as many times as the GPU counted, at most the run's length (and the
            // device's limit). How many commands that covered is the GPU's knowledge alone: not tallied here.
            drawIndexedIndirectCount(state.commandBuffer,arguments.buffer,uint64_t(draw->argumentOffset)+4,
                prepared.count.buffer,prepared.countOffset,std::min(limit,draw->commandCount),24);
            counters.drawsRecorded++;countDrawsRecorded++;
        } else for(uint32_t start=0;start<draw->commandCount;) {
            uint32_t count=std::min(limit,draw->commandCount-start);
            vkCmdDrawIndexedIndirect(state.commandBuffer,arguments.buffer,uint64_t(draw->argumentOffset)+uint64_t(start)*24+4,count,24);
            counters.drawsRecorded++;counters.commandsRecorded+=count;start+=count;
        }
    }
    timing.Stage("BindDraw");
    pipeline.lastFrame=state.currentFrameNumber;if(completion)Consume(draw,ZvnResult_Recorded);else draw->result=ZvnResult_Recorded;return true;
}
void GroupEvent(int id,ZvnDraw* root) {
    const auto key=std::make_pair(root,root->serial);
    if(id==GroupPreparedEvent) {
        auto pending=preparedGroups.find(key);
        if(pending==preparedGroups.end()){Fail("Vulkan group was not prepared");Refuse(root,ZvnResult_BadResource);return;}
        auto prepared=std::move(pending->second);preparedGroups.erase(pending);
        if(prepared.result!=ZvnResult_Recorded){Refuse(root,prepared.result);return;}
        std::vector<uint32_t> offsets;ZvnResult validation=ParseGroup(root,offsets);
        if(validation!=ZvnResult_Recorded || offsets!=prepared.offsets || root->serial!=prepared.serial || currentFrame.load()!=prepared.frame) {
            Refuse(root,validation!=ZvnResult_Recorded?validation:ZvnResult_NoCommandList);return;
        }
        // Validate every child before recording any draw. Resource preparation is already complete, so
        // each Record stays inside the same Unity render pass, including the normal -> Clip pipeline switch.
        for(size_t i=0;i<offsets.size();i++) {
            auto* child=reinterpret_cast<ZvnDraw*>(reinterpret_cast<char*>(root)+offsets[i]);
            validation=ValidateDraw(child);if(validation!=ZvnResult_Recorded){Refuse(root,validation);return;}
            if(child->serial!=prepared.draws[i].serial || child->pipeline!=prepared.draws[i].pipeline) {
                Refuse(root,ZvnResult_BadResource);return;
            }
        }
        for(size_t i=0;i<offsets.size();i++) {
            auto* child=reinterpret_cast<ZvnDraw*>(reinterpret_cast<char*>(root)+offsets[i]);
            if(!Record(child,*pipelines[int(child->pipeline)],prepared.draws[i],false)){Refuse(root,static_cast<ZvnResult>(child->result));return;}
        }
        Consume(root,ZvnResult_Recorded);return;
    }
    PreparedGroup prepared;prepared.serial=root->serial;prepared.frame=currentFrame.load();
    prepared.result=ParseGroup(root,prepared.offsets);
    if(prepared.result==ZvnResult_Recorded) {
        for(uint32_t offset:prepared.offsets) {
            auto* child=reinterpret_cast<ZvnDraw*>(reinterpret_cast<char*>(root)+offset);
            prepared.result=ValidateDraw(child);if(prepared.result!=ZvnResult_Recorded)break;
        }
    }
    if(prepared.result==ZvnResult_Recorded) {
        prepared.draws.resize(prepared.offsets.size());
        for(size_t i=0;i<prepared.offsets.size();i++) {
            auto* child=reinterpret_cast<ZvnDraw*>(reinterpret_cast<char*>(root)+prepared.offsets[i]);
            auto& draw=prepared.draws[i];draw.serial=child->serial;draw.frame=currentFrame.load();
            if(!Prepare(child,*pipelines[int(child->pipeline)],draw)){prepared.result=draw.result;break;}
            if(i==0)prepared.frame=draw.frame;
            else if(draw.frame!=prepared.frame){prepared.result=ZvnResult_NoCommandList;break;}
        }
    }
    // A failed preparation still leaves the root unconsumed until its queued draw or camera sentinel.
    preparedGroups[key]=std::move(prepared);
}
// Separate optional event: the existing Draw / Direct / Group headers and version stay unchanged.
// Its resource-free header uses the existing managed ring ownership and consumption protocol.
void WarmPipeline(ZvnDraw* draw) {
    if(draw->version!=ZVN_CONTRACT_VERSION){Consume(draw,ZvnResult_BadVersion);return;}
    if(draw->totalBytes<sizeof(ZvnDraw) || draw->resourceCount || draw->constantsCount || draw->commandCount ||
       draw->argumentBuffer || draw->indexBuffer || draw->argumentOffset>1){Consume(draw,ZvnResult_BadResource);return;}
    if(!available){Consume(draw,ZvnResult_NotAvailable);return;}
    auto found=pipelines.find(int(draw->pipeline));
    if(found==pipelines.end() || found->second->released){Consume(draw,ZvnResult_NoPipeline);return;}
    if(!unity || !instance.device){Consume(draw,ZvnResult_NoCommandList);return;}
    // Casters and AppSW normally prepare outside and resume with LOAD. Match that render-pass variant.
    // Colour's prepared events stay in URP's existing opaque pass, so their warm headers leave it open.
    if(draw->argumentOffset)unity->EnsureOutsideRenderPass();
    unity->EnsureInsideRenderPass();
    UnityVulkanRecordingState state{};
    if(!unity->CommandRecordingState(&state,kUnityVulkanGraphicsQueueAccess_DontCare) ||
       !state.commandBuffer || !state.renderPass || state.subPassIndex<0){Consume(draw,ZvnResult_NoCommandList);return;}
    Pipeline& pipeline=*found->second;pipeline.warmCalls++;
    VkPipeline variant=Variant(pipeline,state,true);
    if(variant)pipeline.lastFrame=state.currentFrameNumber;
    Consume(draw,variant?ZvnResult_Recorded:ZvnResult_NoPipeline);
}
// Experiment (2026-10-10): zeroes a range of a count buffer -- this frame's count slots, before the frame's compaction
// dispatches, in a pass of its own (never after a draw, which a frame may not have). Resource-free version-1 header:
// argumentBuffer the buffer, argumentOffset the byte offset, commandCount the bytes. Outside the render pass, a
// transfer fill between two barriers of its own: after every earlier reader or writer of the slots, before any
// compute that adds to them or any indirect reader. No wait, no submission.
void ZeroCounts(ZvnDraw* draw) {
    if(draw->version!=ZVN_CONTRACT_VERSION){Consume(draw,ZvnResult_BadVersion);return;}
    if(draw->totalBytes<sizeof(ZvnDraw) || draw->resourceCount || draw->constantsCount || !draw->argumentBuffer || draw->indexBuffer ||
       !draw->commandCount || draw->commandCount%4 || draw->argumentOffset%4){Consume(draw,ZvnResult_BadResource);return;}
    if(!available){Consume(draw,ZvnResult_NotAvailable);return;}
    if(!unity || !instance.device){Consume(draw,ZvnResult_NoCommandList);return;}
    unity->EnsureOutsideRenderPass();
    UnityVulkanBuffer zero{};
    if(!Buffer(draw->argumentBuffer,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_ACCESS_TRANSFER_WRITE_BIT,zero) ||
       uint64_t(draw->argumentOffset)+draw->commandCount>zero.sizeInBytes){Fail("invalid count buffer to zero");Refuse(draw,ZvnResult_BadResource);return;}
    UnityVulkanRecordingState state{};
    if(!unity->CommandRecordingState(&state,kUnityVulkanGraphicsQueueAccess_DontCare) || !state.commandBuffer){Refuse(draw,ZvnResult_NoCommandList);return;}
    uint64_t inFlight=state.currentFrameNumber>state.safeFrameNumber?state.currentFrameNumber-state.safeFrameNumber-1:0;
    if(inFlight>framesInFlightMax)framesInFlightMax=inFlight;
    if(RingReuseUnsafe(state.currentFrameNumber,state.safeFrameNumber)){zeroRefusedInFlight++;Fail("compaction set still in flight (frame - 4 past the safe frame): zero event refused");Refuse(draw,ZvnResult_BadResource);return;}
    VkMemoryBarrier before{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    before.srcAccessMask=VK_ACCESS_INDIRECT_COMMAND_READ_BIT|VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT;before.dstAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;
    vkCmdPipelineBarrier(state.commandBuffer,VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT|VK_PIPELINE_STAGE_VERTEX_SHADER_BIT|VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,1,&before,0,nullptr,0,nullptr);
    vkCmdFillBuffer(state.commandBuffer,zero.buffer,draw->argumentOffset,draw->commandCount,0u);
    VkMemoryBarrier after{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    after.srcAccessMask=VK_ACCESS_TRANSFER_WRITE_BIT;after.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT|VK_ACCESS_INDIRECT_COMMAND_READ_BIT;
    vkCmdPipelineBarrier(state.commandBuffer,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT|VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT,0,1,&after,0,nullptr,0,nullptr);
    currentFrame=state.currentFrameNumber;countSlotsZeroed++;
    NoteZeroed(state.currentFrameNumber,draw->argumentBuffer,draw->argumentOffset,draw->commandCount);
    Consume(draw,ZvnResult_Recorded);
}
// The compute barrier event (2026-10-10): a resource-free header (as the warm event's, no pipeline) recorded between the
// compute dispatches that wrote a selection's entries and the compaction dispatch that reads them. Unity put no barrier
// between two compute dispatches on one buffer on Quest 3S (APK 2's reset against the atomics; the city walk's motion
// selection against its compaction, icW1/icW3 against icW4), so the order is made here: one global memory barrier,
// compute writes made visible to compute, indirect and vertex reads. Outside the render pass; no wait, no submission.
void ComputeBarrier(ZvnDraw* draw) {
    if(draw->version!=ZVN_CONTRACT_VERSION){Consume(draw,ZvnResult_BadVersion);return;}
    if(draw->totalBytes<sizeof(ZvnDraw) || draw->resourceCount || draw->constantsCount || draw->commandCount ||
       draw->argumentBuffer || draw->indexBuffer || draw->argumentOffset){Consume(draw,ZvnResult_BadResource);return;}
    if(!available){Consume(draw,ZvnResult_NotAvailable);return;}
    if(!unity || !instance.device){Consume(draw,ZvnResult_NoCommandList);return;}
    unity->EnsureOutsideRenderPass();
    UnityVulkanRecordingState state{};
    if(!unity->CommandRecordingState(&state,kUnityVulkanGraphicsQueueAccess_DontCare) || !state.commandBuffer){Refuse(draw,ZvnResult_NoCommandList);return;}
    VkMemoryBarrier barrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    barrier.srcAccessMask=VK_ACCESS_SHADER_WRITE_BIT;barrier.dstAccessMask=VK_ACCESS_SHADER_READ_BIT|VK_ACCESS_SHADER_WRITE_BIT|VK_ACCESS_INDIRECT_COMMAND_READ_BIT;
    vkCmdPipelineBarrier(state.commandBuffer,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT|VK_PIPELINE_STAGE_DRAW_INDIRECT_BIT|VK_PIPELINE_STAGE_VERTEX_SHADER_BIT,0,1,&barrier,0,nullptr,0,nullptr);
    computeBarriers++;
    Consume(draw,ZvnResult_Recorded);
}
void UNITY_INTERFACE_API Event(int id,void* data) {
    std::lock_guard<std::mutex> lock(gate);auto* draw=static_cast<ZvnDraw*>(data);if(!draw)return;
    if(id==SentinelEvent){counters.sentinelsRun++;Consume(draw,ZvnResult_Recorded);return;}
    if(id==WarmPipelineEvent){WarmPipeline(draw);return;}
    if(id==ZeroEvent){ZeroCounts(draw);return;}
    if(id==BarrierEvent){ComputeBarrier(draw);return;}
    if(id==GroupPrepareEvent || id==GroupPreparedEvent){GroupEvent(id,draw);return;}
    if(id!=DrawEvent && id!=PrepareEvent && id!=PreparedDrawEvent)return;
    const auto key=std::make_pair(draw,draw->serial);
    PreparedDraw prepared;
    auto p=pipelines.find(int(draw->pipeline));
    ZvnResult invalid=ValidateDraw(draw);
    if(id==PreparedDrawEvent) {
        auto pending=preparedDraws.find(key);
        if(pending==preparedDraws.end()){Fail("Vulkan draw was not prepared");Refuse(draw,ZvnResult_BadResource);return;}
        prepared=std::move(pending->second);preparedDraws.erase(pending);
        if(invalid!=ZvnResult_Recorded){Refuse(draw,invalid);return;}
        Record(draw,*p->second,prepared);return;
    }
    // A prepare callback never consumes the block: a subsequent draw callback still references it,
    // even when preparation failed. The draw reports the error, or the camera sentinel proves discard.
    prepared.serial=draw->serial;prepared.frame=currentFrame.load();prepared.result=invalid;
    if(invalid==ZvnResult_Recorded)Prepare(draw,*p->second,prepared);
    if(id==PrepareEvent){preparedDraws[key]=std::move(prepared);return;}
    if(prepared.result!=ZvnResult_Recorded){Refuse(draw,prepared.result);return;}
    Record(draw,*p->second,prepared);
}
void UNITY_INTERFACE_API Device(UnityGfxDeviceEventType event) {
    std::lock_guard<std::mutex> lock(gate);
    if(event==kUnityGfxDeviceEventInitialize && graphics->GetRenderer()==kUnityGfxRendererVulkan) {
        instance=unity->Instance();if(!instance.device || !instance.physicalDevice)return;vkGetPhysicalDeviceProperties(instance.physicalDevice,&properties);
        available=instance.device && featuresObserved && enabledMulti && enabledFirst;
        if(!available)Fail("Vulkan plugin must preload before device creation; multiDrawIndirect and firstInstance must be enabled");
        // Experiment: the count draw's entry point, through the loader Unity gave; and the subgroup properties, for the record.
        drawIndexedIndirectCount=nullptr;
        auto getDeviceProcAddr=instance.getInstanceProcAddr?reinterpret_cast<PFN_vkGetDeviceProcAddr>(instance.getInstanceProcAddr(instance.instance,"vkGetDeviceProcAddr")):nullptr;
        if(getDeviceProcAddr && indirectCountEnabled) {
            drawIndexedIndirectCount=reinterpret_cast<PFN_vkCmdDrawIndexedIndirectCountKHR>(getDeviceProcAddr(instance.device,"vkCmdDrawIndexedIndirectCountKHR"));
            if(!drawIndexedIndirectCount)drawIndexedIndirectCount=reinterpret_cast<PFN_vkCmdDrawIndexedIndirectCountKHR>(getDeviceProcAddr(instance.device,"vkCmdDrawIndexedIndirectCount"));
        }
        indirectCountReady=available && indirectCountEnabled && drawIndexedIndirectCount!=nullptr;
        VkPhysicalDeviceSubgroupProperties subgroup{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_PROPERTIES};
        auto getProperties2=instance.getInstanceProcAddr?reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(instance.getInstanceProcAddr(instance.instance,"vkGetPhysicalDeviceProperties2")):nullptr;
        if(getProperties2){VkPhysicalDeviceProperties2 properties2{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2};properties2.pNext=&subgroup;getProperties2(instance.physicalDevice,&properties2);}
        {
            char text[256];
            std::snprintf(text,sizeof text,"; countDrawReady=%d entryPoint=%d subgroup(size=%u ops=0x%x stages=0x%x %s)",indirectCountReady,drawIndexedIndirectCount!=nullptr,
                subgroup.subgroupSize,unsigned(subgroup.supportedOperations),unsigned(subgroup.supportedStages),getProperties2?"queried":"properties2 unavailable");
            deviceNote+=text;
            __android_log_print(ANDROID_LOG_INFO,"ZantetsuVpVulkan","%s",text);
        }
        UnityVulkanPluginEventConfig config{kUnityVulkanRenderPass_EnsureInside,kUnityVulkanGraphicsQueueAccess_DontCare,
            kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission|kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState};
        unity->ConfigureEvent(PreparedDrawEvent,&config);
        unity->ConfigureEvent(GroupPreparedEvent,&config);
        UnityVulkanPluginEventConfig warmConfig{kUnityVulkanRenderPass_DontCare,kUnityVulkanGraphicsQueueAccess_DontCare,
            kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission|kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState};
        unity->ConfigureEvent(WarmPipelineEvent,&warmConfig);
        unity->ConfigureEvent(ZeroEvent,&warmConfig);
        unity->ConfigureEvent(BarrierEvent,&warmConfig);
        // The combined path prepares resources before it enters a pass. Entering in the precondition
        // would create an empty pass which Prepare immediately ends again.
        config.renderPassPrecondition=kUnityVulkanRenderPass_DontCare;
        unity->ConfigureEvent(DrawEvent,&config);
        UnityVulkanPluginEventConfig prepareConfig{kUnityVulkanRenderPass_DontCare,kUnityVulkanGraphicsQueueAccess_DontCare,
            kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission};
        unity->ConfigureEvent(PrepareEvent,&prepareConfig);
        unity->ConfigureEvent(GroupPrepareEvent,&prepareConfig);
        config.renderPassPrecondition=kUnityVulkanRenderPass_DontCare;config.flags=kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission;
        unity->ConfigureEvent(SentinelEvent,&config);
        __android_log_print(ANDROID_LOG_INFO,"ZantetsuVpVulkan","ready=%d maxDrawIndirectCount=%u",available,properties.limits.maxDrawIndirectCount);
    }else if(event==kUnityGfxDeviceEventShutdown) {
        available=false;preparedDraws.clear();preparedGroups.clear();
        // Device shutdown follows Unity's rendering drain. No waits or queue submissions on the draw path.
        for(auto& p:pipelines)p.second->Destroy();pipelines.clear();for(auto& r:regions)r->Destroy();regions.clear();
        for(auto* d:held)__atomic_store_n(&d->consumed,1,__ATOMIC_RELEASE);held.clear();instance={};
    }
}
}

extern "C" {
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    graphics=interfaces->Get<IUnityGraphics>();unity=interfaces->Get<IUnityGraphicsVulkanV2>();
    if(!graphics || !unity){Fail("Unity Vulkan V2 interface missing");return;}
    unity->AddInterceptInitialization(Intercept,nullptr,0);
    DrawEvent = graphics->ReserveEventIDRange(9);
    SentinelEvent = DrawEvent + 1;PrepareEvent = DrawEvent + 2;PreparedDrawEvent = DrawEvent + 3;GroupPrepareEvent = DrawEvent + 4;GroupPreparedEvent = DrawEvent + 5;WarmPipelineEvent = DrawEvent + 6;ZeroEvent = DrawEvent + 7;BarrierEvent = DrawEvent + 8;
    graphics->RegisterDeviceEventCallback(Device);
    if(graphics->GetRenderer()==kUnityGfxRendererVulkan)Device(kUnityGfxDeviceEventInitialize);
}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    if(graphics)graphics->UnregisterDeviceEventCallback(Device);
    if(unity)unity->RemoveInterceptInitialization(Intercept);
}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnContractVersion(){return ZVN_CONTRACT_VERSION;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnIsAvailable(){std::lock_guard<std::mutex> lock(gate);return available;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnDescribeState(char* out,int bytes) {
    std::lock_guard<std::mutex> lock(gate);
    return Text(std::string("Vulkan indexed multi-draw; available=")+(available?"true":"false")+"; maxDrawIndirectCount="+std::to_string(properties.limits.maxDrawIndirectCount)
        +"; "+deviceNote+"; countDrawsRecorded="+std::to_string(countDrawsRecorded)+" countSlotsZeroed="+std::to_string(countSlotsZeroed)
        +" countDrawsRefusedUnzeroed="+std::to_string(countDrawsRefusedUnzeroed)+" framesInFlightMax="+std::to_string(framesInFlightMax)
        +" zeroRefusedInFlight="+std::to_string(zeroRefusedInFlight)+" computeBarriers="+std::to_string(computeBarriers)+" countSnapshots="+std::to_string(countSnapshots)+" countCopies="+std::to_string(countCopies)+"; "+error,out,bytes);
}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnLastError(char* out,int bytes){std::lock_guard<std::mutex> lock(gate);return Text(error,out,bytes);}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnConfigureRings(uint32_t count,uint32_t bytes,uint32_t descriptors) {
    std::lock_guard<std::mutex> lock(gate);if(!regions.empty()){Fail("rings already active");return;}
    regionCount=std::max(2u,std::min(count,32u));regionBytes=std::max(8192u,bytes);regionDescriptors=std::max(128u,descriptors);
}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnCreatePipeline(const ZvnPipelineDesc* desc) {
    std::lock_guard<std::mutex> lock(gate);
    if(!available || !desc || desc->version!=ZVN_CONTRACT_VERSION || desc->argumentStride!=24 || !desc->bindingCount || !desc->bindings) {Fail("invalid Vulkan pipeline description or unavailable device");return -1;}
    auto pipeline=std::make_unique<Pipeline>();
    if(!CreatePipeline(*pipeline,*desc)){pipeline->Destroy();return -1;}
    int id=nextPipeline++;pipelines[id]=std::move(pipeline);return id;
}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnReleasePipeline(int id){std::lock_guard<std::mutex> lock(gate);auto p=pipelines.find(id);if(p!=pipelines.end())p->second->released=true;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnDescribePipeline(int id,char* out,int bytes) {
    std::lock_guard<std::mutex> lock(gate);auto p=pipelines.find(id);
    if(p==pipelines.end())return Text("missing pipeline",out,bytes);
    const auto& value=*p->second;
    return Text(std::string(value.desc.name)+" SPIR-V descriptors="+std::to_string(value.slots.size())+
        " variants="+std::to_string(value.variants.size())+" warmCalls="+std::to_string(value.warmCalls)+
        " warmCreates="+std::to_string(value.warmCreates)+" drawVariantHits="+std::to_string(value.drawVariantHits)+
        " drawMissesAfterWarm="+std::to_string(value.drawMissesAfterWarm),out,bytes);
}
UnityRenderingEventAndData UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnGetRenderEventAndDataFunc(){return Event;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnWarmPipelineEventId(){return WarmPipelineEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnZeroEventId(){return ZeroEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnBarrierEventId(){return BarrierEvent;}
// Whether a count draw can be recorded on this device: available, the extension enabled, the entry point found.
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnIndirectCountReady(){std::lock_guard<std::mutex> lock(gate);return indirectCountReady?1:0;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnDrawEventId(){return DrawEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnSentinelEventId(){return SentinelEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnPrepareEventId(){return PrepareEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnPreparedDrawEventId(){return PreparedDrawEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnGroupPrepareEventId(){return GroupPrepareEvent;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnGroupPreparedEventId(){return GroupPreparedEvent;}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnGetCounters(ZvnCounters* out){std::lock_guard<std::mutex> lock(gate);if(out){*out=counters;out->ringRegions=regionCount;out->ringRegionBytes=regionBytes;}}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnFrameFence(uint64_t* completed,uint64_t* next){if(completed)*completed=safeFrame.load();if(next)*next=currentFrame.load();}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnSetLockTiming(int){}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnLockStats(uint64_t* out,int count){if(out && count>0)std::memset(out,0,size_t(count)*8);return 0;}
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnHoldConsumption(int value){std::lock_guard<std::mutex> lock(gate);hold=value!=0;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnReleaseHeld(int count){std::lock_guard<std::mutex> lock(gate);int n=count<0?int(held.size()):std::min(count,int(held.size()));for(int i=0;i<n;i++)__atomic_store_n(&held[i]->consumed,1,__ATOMIC_RELEASE);held.erase(held.begin(),held.begin()+n);return n;}
int UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API ZvnHeldCount(){std::lock_guard<std::mutex> lock(gate);return int(held.size());}
}
