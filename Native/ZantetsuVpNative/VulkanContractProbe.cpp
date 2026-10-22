// Runs the real device negotiation, SPIR-V parser and draw validation without a graphics device.
#include <vulkan/vulkan.h>
static VKAPI_ATTR void VKAPI_CALL ProbeGetFeatures(VkPhysicalDevice, VkPhysicalDeviceFeatures*);
static VKAPI_ATTR void VKAPI_CALL ProbeGetProperties(VkPhysicalDevice, VkPhysicalDeviceProperties*);
static VKAPI_ATTR VkResult VKAPI_CALL ProbeEnumerateExtensions(VkPhysicalDevice, const char*, uint32_t*, VkExtensionProperties*);
#define vkGetPhysicalDeviceFeatures ProbeGetFeatures
#define vkGetPhysicalDeviceProperties ProbeGetProperties
#define vkEnumerateDeviceExtensionProperties ProbeEnumerateExtensions
#include "ZantetsuVpVulkan.cpp"
#undef vkGetPhysicalDeviceFeatures
#undef vkGetPhysicalDeviceProperties
#undef vkEnumerateDeviceExtensionProperties
#include <fstream>
#include <iterator>
#include <cassert>
#include <cstddef>
namespace {
struct DeviceCall {
    std::vector<std::string> extensions;
    VkPhysicalDeviceFeatures core{};
    bool hasVulkan12 = false;
    VkBool32 drawIndirectCount = VK_FALSE;
};
bool probeCountSupported = false, probeRejectCountExtension = false;
std::vector<DeviceCall> deviceCalls;
}
static VKAPI_ATTR void VKAPI_CALL ProbeGetFeatures(VkPhysicalDevice, VkPhysicalDeviceFeatures* result) {
    *result = {};
    result->multiDrawIndirect = result->drawIndirectFirstInstance = result->samplerAnisotropy = VK_TRUE;
}
static VKAPI_ATTR void VKAPI_CALL ProbeGetProperties(VkPhysicalDevice, VkPhysicalDeviceProperties* result) {
    *result = {};
    result->apiVersion = VK_API_VERSION_1_2;
}
static VKAPI_ATTR VkResult VKAPI_CALL ProbeEnumerateExtensions(VkPhysicalDevice, const char* layer,
    uint32_t* count, VkExtensionProperties* result) {
    assert(layer == nullptr);
    if (!result) { *count = probeCountSupported ? 1 : 0; return VK_SUCCESS; }
    if (!probeCountSupported) { *count = 0; return VK_SUCCESS; }
    if (*count == 0) return VK_INCOMPLETE;
    *result = {};
    std::strcpy(result->extensionName, VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME);
    result->specVersion = VK_KHR_DRAW_INDIRECT_COUNT_SPEC_VERSION;
    *count = 1;
    return VK_SUCCESS;
}
static VKAPI_ATTR VkResult VKAPI_CALL ProbeCreateDevice(VkPhysicalDevice physical, const VkDeviceCreateInfo* input,
    const VkAllocationCallbacks* allocator, VkDevice* device) {
    assert(physical != VK_NULL_HANDLE && allocator == nullptr);
    DeviceCall call;
    for (uint32_t i = 0; i < input->enabledExtensionCount; ++i) call.extensions.emplace_back(input->ppEnabledExtensionNames[i]);
    if (input->pEnabledFeatures) call.core = *input->pEnabledFeatures;
    for (auto* item = reinterpret_cast<const VkBaseInStructure*>(input->pNext); item; item = item->pNext) {
        if (item->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2)
            call.core = reinterpret_cast<const VkPhysicalDeviceFeatures2*>(item)->features;
        if (item->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES) {
            call.hasVulkan12 = true;
            call.drawIndirectCount = reinterpret_cast<const VkPhysicalDeviceVulkan12Features*>(item)->drawIndirectCount;
        }
    }
    deviceCalls.push_back(call);
    if (probeRejectCountExtension && std::find(call.extensions.begin(), call.extensions.end(),
        VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME) != call.extensions.end()) return VK_ERROR_EXTENSION_NOT_PRESENT;
    *device = reinterpret_cast<VkDevice>(uintptr_t(2));
    return VK_SUCCESS;
}
static void CheckDeviceNegotiation() {
    struct Case {
        const char* name;
        bool supported;
        int coreCount; // -1: no Vulkan12 feature structure; 0/1: Unity's requested feature value.
        bool unityExtension, rejectAddition, enabled, added;
        std::vector<bool> callHasCountExtension;
    };
    const Case cases[] = {
        {"unsupported: fixed", false, -1, false, false, false, false, {false}},
        {"disabled Vulkan12 feature: fixed", true, 0, false, false, false, false, {false}},
        {"KHR addition", true, -1, false, false, true, true, {true}},
        {"KHR addition rejected: retry fixed", true, -1, false, true, false, false, {true, false}},
        {"core feature without KHR", false, 1, false, false, true, false, {false}},
        {"Unity enabled KHR", true, -1, true, false, true, false, {true}},
    };
    const auto previousCreate = originalCreateDevice;
    originalCreateDevice = ProbeCreateDevice;
    for (const auto& test : cases) {
        probeCountSupported = test.supported;
        probeRejectCountExtension = test.rejectAddition;
        deviceCalls.clear();
        featuresObserved = enabledMulti = enabledFirst = false;
        indirectCountSupported = indirectCountEnabledByUnity = indirectCountEnabled = indirectCountAddedHere = false;
        VkPhysicalDeviceVulkan12Features v12{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES};
        v12.drawIndirectCount = test.coreCount == 1 ? VK_TRUE : VK_FALSE;
        VkPhysicalDeviceFeatures2 core{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2};
        core.features.samplerAnisotropy = VK_TRUE; // Existing unrelated features must survive both attempts.
        core.pNext = test.coreCount < 0 ? nullptr : &v12;
        const char* extensions[] = {VK_KHR_SWAPCHAIN_EXTENSION_NAME, VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME};
        VkDeviceCreateInfo input{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
        input.pNext = &core;
        input.enabledExtensionCount = test.unityExtension ? 2 : 1;
        input.ppEnabledExtensionNames = extensions;
        VkDevice device = VK_NULL_HANDLE;
        assert(CreateDevice(reinterpret_cast<VkPhysicalDevice>(uintptr_t(1)), &input, nullptr, &device) == VK_SUCCESS);
        assert(device != VK_NULL_HANDLE && deviceCalls.size() == test.callHasCountExtension.size());
        for (size_t i = 0; i < deviceCalls.size(); ++i) {
            std::vector<std::string> expected{VK_KHR_SWAPCHAIN_EXTENSION_NAME};
            if (test.callHasCountExtension[i]) expected.emplace_back(VK_KHR_DRAW_INDIRECT_COUNT_EXTENSION_NAME);
            const auto& call = deviceCalls[i];
            assert(call.extensions == expected);
            assert(call.core.multiDrawIndirect && call.core.drawIndirectFirstInstance && call.core.samplerAnisotropy);
            assert(call.hasVulkan12 == (test.coreCount >= 0));
            if (call.hasVulkan12) assert(call.drawIndirectCount == v12.drawIndirectCount);
        }
        assert(featuresObserved && enabledMulti && enabledFirst);
        assert(indirectCountSupported == test.supported && indirectCountEnabledByUnity == test.unityExtension);
        assert(indirectCountEnabled == test.enabled && indirectCountAddedHere == test.added);
        assert(!core.features.multiDrawIndirect && !core.features.drawIndirectFirstInstance); // Never mutate Unity's chain.
        assert(input.enabledExtensionCount == (test.unityExtension ? 2u : 1u));
        std::printf("device negotiation: %s passed\n", test.name);
    }
    originalCreateDevice = previousCreate;
}
int main(int argc, char** argv) {
    CheckDeviceNegotiation(); // With no arguments, the negotiation and ABI checks run; shader inputs are optional.
    for(int i=1;i<argc;i++) {
        std::ifstream stream(argv[i],std::ios::binary);
        std::vector<char> bytes((std::istreambuf_iterator<char>(stream)),std::istreambuf_iterator<char>());
        if(!stream || bytes.size()<20 || bytes.size()%4)return 3;
        auto result=Reflect(bytes.data(),uint32_t(bytes.size()));
        unsigned uniform=0,storage=0,images=0,samplers=0;
        for(auto& item:result) {
            if(item.second==VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER)uniform++;
            if(item.second==VK_DESCRIPTOR_TYPE_STORAGE_BUFFER)storage++;
            if(item.second==VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE || item.second==VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER)images++;
            if(item.second==VK_DESCRIPTOR_TYPE_SAMPLER)samplers++;
        }
        // Direct variants read only vertices plus their inline transform/Clip UBO. Normal variants no longer
        // bind Clip storage: forward/motion have at least vertices, transforms and visibility; casters add bounds.
        if(std::strstr(argv[i],".vs.bin") && (!uniform ||
            (std::strstr(argv[i],"-direct") ? storage!=1 : storage<3)))return 4;
        if(!Reflect(bytes.data(),19).empty())return 5;
        auto damaged=bytes;uint32_t zero=0;std::memcpy(damaged.data()+20,&zero,4);
        if(!Reflect(damaged.data(),uint32_t(damaged.size())).empty())return 6;
        std::printf("%s: uniform=%u storage=%u images=%u samplers=%u\n",argv[i],uniform,storage,images,samplers);
    }
    assert(Range(8,2,4,16) && !Range(8,3,4,16) && !Range(UINT32_MAX,2,4,16));
    // Preparation errors must not release the shared managed block before its queued draw callback.
    available=true;
    pipelines[100]=std::make_unique<Pipeline>();
    ZvnDraw draw{};
    draw.version=ZVN_CONTRACT_VERSION;draw.pipeline=100;draw.serial=7;
    draw.commandCount=1;draw.viewportCount=1;draw.indexFormat=42;
    draw.totalBytes=sizeof draw;draw.resourcesOffset=sizeof draw+1;draw.resourceCount=1;
    auto refused=counters.drawsRefused;
    Event(PrepareEvent,&draw);
    assert(draw.consumed==0 && counters.drawsRefused==refused && preparedDraws.size()==1);
    Event(PreparedDrawEvent,&draw);
    assert(draw.consumed==1 && draw.result==ZvnResult_BadResource && counters.drawsRefused==refused+1 && preparedDraws.empty());
    draw.consumed=0;draw.serial++;
    Event(PreparedDrawEvent,&draw); // A new generation cannot use an earlier generation's prepared descriptors.
    assert(draw.consumed==1 && draw.result==ZvnResult_BadResource && counters.drawsRefused==refused+2);
    draw.consumed=0;draw.serial++;draw.version=0;
    Event(PrepareEvent,&draw);
    assert(draw.consumed==0 && preparedDraws.size()==1);
    Event(PreparedDrawEvent,&draw);
    assert(draw.consumed==1 && draw.result==ZvnResult_BadVersion && preparedDraws.empty());
    draw.consumed=0;draw.serial++;
    Event(PrepareEvent,&draw); // An abandoned preparation is retired by frame safety, without reading the block.
    Retire(currentFrame.load());
    assert(preparedDraws.empty());
    // Version 2 extends the header explicitly: direct draws never overload the indirect argument fields.
    struct DirectBlock { ZvnDraw draw; ZvnDrawExtension extension; ZvnDirectCommand commands[2]; } direct{};
    static_assert(offsetof(DirectBlock,extension)==240 && offsetof(DirectBlock,commands)==256,"direct layout");
    direct.draw.version=ZVN_VULKAN_DRAW_VERSION;direct.draw.pipeline=100;
    direct.draw.totalBytes=sizeof direct;direct.draw.viewportCount=1;direct.draw.indexFormat=42;direct.draw.indexBufferBytes=24;
    direct.extension={ZvnDrawMode_Direct,uint32_t(offsetof(DirectBlock,commands)),1,0};
    direct.commands[0]={4,1,2,0,31};
    direct.draw.argumentOffset=123; // irrelevant for a direct draw, and argumentBuffer remains null.
    assert(ValidateDraw(&direct.draw)==ZvnResult_Recorded);
    direct.commands[0].firstInstance=32;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.commands[0].firstInstance=31;
    direct.commands[0].instanceCount=0;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.commands[0].instanceCount=1;
    direct.commands[0].indexCount=0;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.commands[0].indexCount=4;
    direct.commands[0].firstIndex=UINT32_MAX;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.commands[0].firstIndex=2;
    direct.extension.commandsCount=0;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.extension.commandsCount=1;
    direct.extension.commandsCount=33;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.extension.commandsCount=1;
    direct.extension.commandsOffset=255;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.extension.commandsOffset=256;
    direct.extension.commandsOffset=UINT32_MAX-3;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.extension.commandsOffset=256;
    direct.draw.totalBytes=255;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.draw.totalBytes=sizeof direct;
    direct.draw.version=3;assert(ValidateDraw(&direct.draw)==ZvnResult_BadVersion);direct.draw.version=ZVN_VULKAN_DRAW_VERSION;
    direct.extension.mode=ZvnDrawMode_Group;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.extension.mode=ZvnDrawMode_Direct;
    direct.extension.reserved=1;assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.extension.reserved=0;
    direct.draw.resourcesOffset=241;direct.draw.resourceCount=1;
    assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.draw.resourceCount=0;direct.draw.resourcesOffset=0;
    direct.draw.constantsOffset=241;direct.draw.constantsCount=1;
    assert(ValidateDraw(&direct.draw)==ZvnResult_BadResource);direct.draw.constantsCount=0;direct.draw.constantsOffset=0;

    // Experiment (2026-10-10): the IndirectCount draw is version 2 with one record; refused without the device's
    // enabled entry point, or with a malformed record; never mistaken for a direct draw.
    static_assert(sizeof(ZvnIndirectCount)==32,"indirect count ABI");
    struct CountBlock { ZvnDraw draw; ZvnDrawExtension extension; ZvnIndirectCount record; } counted{};
    static_assert(offsetof(CountBlock,record)==256,"count layout");
    counted.draw.version=ZVN_VULKAN_DRAW_VERSION;counted.draw.pipeline=100;counted.draw.totalBytes=sizeof counted;counted.draw.viewportCount=1;counted.draw.indexFormat=42;
    counted.draw.argumentBuffer=&counted;counted.draw.commandCount=1602;counted.draw.argumentOffset=24*7;counted.draw.indexBuffer=&counted;counted.draw.indexBufferBytes=24;
    counted.extension={ZvnDrawMode_IndirectCount,uint32_t(offsetof(CountBlock,record)),1,0};
    counted.record={&counted,12,1024,&counted,0,0};
    indirectCountReady=false;assert(ValidateDraw(&counted.draw)==ZvnResult_NotAvailable);
    // Ordinary indirect arguments remain valid without the count extension or a count buffer.
    ZvnDraw fixed = counted.draw;fixed.version=ZVN_CONTRACT_VERSION;fixed.totalBytes=sizeof fixed;
    assert(ValidateDraw(&fixed)==ZvnResult_Recorded && !Direct(&fixed) && !IndirectCountMode(&fixed));
    indirectCountReady=true;assert(ValidateDraw(&counted.draw)==ZvnResult_Recorded && !Direct(&counted.draw) && IndirectCountMode(&counted.draw));
    counted.record.zeroBytes=0;assert(ValidateDraw(&counted.draw)==ZvnResult_Recorded);   // a snapshot slot (2026-10-10)
    counted.record.zeroBytes=1022;
    assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.record.zeroBytes=1024;
    counted.record.zeroBuffer=nullptr;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.record.zeroBytes=0;
    assert(ValidateDraw(&counted.draw)==ZvnResult_Recorded);counted.record.zeroBuffer=&counted;counted.record.zeroBytes=1024;
    counted.record.countBuffer=nullptr;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.record.countBuffer=&counted;
    counted.record.countOffset=6;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.record.countOffset=12;
    // The draw from a copy of its count (2026-10-10): a slot named (zeroBuffer, zeroBytes 0), nothing else.
    counted.record.reserved=ZVN_COUNT_DRAW_FROM_COPY;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);   // a fill range is not a slot
    counted.record.zeroBytes=0;assert(ValidateDraw(&counted.draw)==ZvnResult_Recorded);
    counted.record.zeroBuffer=nullptr;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.record.zeroBuffer=&counted;
    counted.record.reserved=2;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);
    counted.record.reserved=0;counted.record.zeroBytes=1024;assert(ValidateDraw(&counted.draw)==ZvnResult_Recorded);
    counted.extension.commandsCount=2;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.extension.commandsCount=1;
    counted.extension.commandsOffset=252;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.extension.commandsOffset=256;
    counted.draw.totalBytes=sizeof counted-1;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.draw.totalBytes=sizeof counted;
    counted.draw.commandCount=0;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.draw.commandCount=1602;
    counted.draw.argumentOffset=25;assert(ValidateDraw(&counted.draw)==ZvnResult_BadResource);counted.draw.argumentOffset=24*7;
    indirectCountReady=false;

    alignas(8) char packet[1024]{};
    auto* root=reinterpret_cast<ZvnDraw*>(packet);
    root->version=ZVN_VULKAN_DRAW_VERSION;root->totalBytes=sizeof packet;root->serial=100;
    auto* groupExtension=reinterpret_cast<ZvnDrawExtension*>(packet+sizeof(ZvnDraw));
    *groupExtension={ZvnDrawMode_Group,256,2,0};
    auto* offsets=reinterpret_cast<uint32_t*>(packet+256);offsets[0]=272;offsets[1]=576;
    auto* first=reinterpret_cast<DirectBlock*>(packet+272);auto* second=reinterpret_cast<DirectBlock*>(packet+576);
    *first=direct;*second=direct;
    std::vector<uint32_t> parsed;
    assert(ParseGroup(root,parsed)==ZvnResult_Recorded && parsed==std::vector<uint32_t>({272,576}));
    offsets[1]=272;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);offsets[1]=576;
    offsets[1]=280;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);offsets[1]=576;
    offsets[1]=1024-239;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);offsets[1]=576;
    first->draw.totalBytes=UINT32_MAX;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);first->draw.totalBytes=sizeof direct;
    first->extension.mode=ZvnDrawMode_Group;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);first->extension.mode=ZvnDrawMode_Direct;
    first->draw.version=3;assert(ParseGroup(root,parsed)==ZvnResult_BadVersion);first->draw.version=ZVN_VULKAN_DRAW_VERSION;
    groupExtension->commandsCount=0;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);groupExtension->commandsCount=2;
    groupExtension->commandsCount=33;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);groupExtension->commandsCount=2;
    groupExtension->commandsOffset=272;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);groupExtension->commandsOffset=256;
    root->totalBytes=255;assert(ParseGroup(root,parsed)==ZvnResult_BadResource);root->totalBytes=sizeof packet;
    root->version=1;assert(ParseGroup(root,parsed)==ZvnResult_BadVersion);root->version=ZVN_VULKAN_DRAW_VERSION;
    // All child metadata is validated before touching a Vulkan device. Failed groups retain the root until
    // the draw callback, and consume only the root, not copied child headers with no independent ownership.
    second->extension.commandsCount=0;
    refused=counters.drawsRefused;
    Event(GroupPrepareEvent,root);
    assert(root->consumed==0 && first->draw.consumed==0 && second->draw.consumed==0 && counters.drawsRefused==refused && preparedGroups.size()==1);
    Event(GroupPreparedEvent,root);
    assert(root->consumed==1 && root->result==ZvnResult_BadResource && counters.drawsRefused==refused+1 && preparedGroups.empty());
    root->consumed=0;root->serial++;
    Event(GroupPreparedEvent,root);
    assert(root->consumed==1 && root->result==ZvnResult_BadResource && counters.drawsRefused==refused+2);
    root->consumed=0;root->serial++;root->version=0;
    Event(GroupPrepareEvent,root);assert(root->consumed==0 && preparedGroups.size()==1);
    Event(GroupPreparedEvent,root);assert(root->consumed==1 && root->result==ZvnResult_BadVersion && preparedGroups.empty());
    root->consumed=0;root->serial++;
    Event(GroupPrepareEvent,root);root->version=UINT32_MAX; // Retirement must not read abandoned managed memory.
    Retire(currentFrame.load());assert(preparedGroups.empty());
    // The warm event keeps version 1 / existing header size. No resources, descriptors or draw commands.
    static_assert(sizeof(ZvnDraw)==240,"warm does not change the draw ABI");
    ZvnDraw warm{};warm.version=ZVN_CONTRACT_VERSION;warm.totalBytes=sizeof warm;warm.pipeline=100;warm.serial=200;
    auto actualDraws=counters.drawsRecorded;refused=counters.drawsRefused;
    Event(WarmPipelineEvent,&warm); // Valid header, but this probe has no Unity graphics device.
    assert(warm.consumed==1 && warm.result==ZvnResult_NoCommandList && pipelines[100]->warmCalls==0);
    warm.consumed=0;warm.serial++;warm.version=0;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_BadVersion);warm.version=ZVN_CONTRACT_VERSION;
    warm.consumed=0;warm.serial++;warm.totalBytes=239;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_BadResource);warm.totalBytes=sizeof warm;
    warm.consumed=0;warm.serial++;warm.commandCount=1;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_BadResource);warm.commandCount=0;
    warm.consumed=0;warm.serial++;warm.argumentOffset=2;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_BadResource);warm.argumentOffset=1;
    warm.consumed=0;warm.serial++;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_NoCommandList);warm.argumentOffset=0;
    warm.consumed=0;warm.serial++;warm.resourceCount=1;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_BadResource);warm.resourceCount=0;
    warm.consumed=0;warm.serial++;warm.pipeline=999;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_NoPipeline);warm.pipeline=100;
    warm.consumed=0;warm.serial++;available=false;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==1 && warm.result==ZvnResult_NotAvailable);available=true;
    assert(counters.drawsRecorded==actualDraws && counters.drawsRefused==refused && preparedDraws.empty() && preparedGroups.empty());
    // The zero event (2026-10-10): a resource-free header naming a buffer range; refused malformed, NoCommandList
    // without a Unity device, never a draw.
    ZvnDraw zero{};zero.version=ZVN_CONTRACT_VERSION;zero.totalBytes=sizeof zero;zero.serial=300;zero.argumentBuffer=&zero;zero.commandCount=1024;
    Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_NoCommandList);
    zero.consumed=0;zero.serial++;zero.commandCount=0;Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_BadResource);zero.commandCount=1024;
    zero.consumed=0;zero.serial++;zero.commandCount=1022;Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_BadResource);zero.commandCount=1024;
    zero.consumed=0;zero.serial++;zero.argumentOffset=2;Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_BadResource);zero.argumentOffset=0;
    zero.consumed=0;zero.serial++;zero.argumentBuffer=nullptr;Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_BadResource);zero.argumentBuffer=&zero;
    zero.consumed=0;zero.serial++;zero.resourceCount=1;Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_BadResource);zero.resourceCount=0;
    zero.consumed=0;zero.serial++;zero.version=0;Event(ZeroEvent,&zero);assert(zero.consumed==1 && zero.result==ZvnResult_BadVersion);zero.version=ZVN_CONTRACT_VERSION;
    // The compute barrier event (2026-10-10): a resource-free header; NoCommandList without a Unity device, refused
    // malformed, never a draw and never a barrier that was not recorded.
    ZvnDraw bar{};bar.version=ZVN_CONTRACT_VERSION;bar.totalBytes=sizeof bar;bar.serial=400;
    Event(BarrierEvent,&bar);assert(bar.consumed==1 && bar.result==ZvnResult_NoCommandList);
    bar.consumed=0;bar.serial++;bar.commandCount=1;Event(BarrierEvent,&bar);assert(bar.consumed==1 && bar.result==ZvnResult_BadResource);bar.commandCount=0;
    bar.consumed=0;bar.serial++;bar.argumentBuffer=&bar;Event(BarrierEvent,&bar);assert(bar.consumed==1 && bar.result==ZvnResult_BadResource);bar.argumentBuffer=nullptr;
    bar.consumed=0;bar.serial++;bar.argumentOffset=1;Event(BarrierEvent,&bar);assert(bar.consumed==1 && bar.result==ZvnResult_BadResource);bar.argumentOffset=0;
    bar.consumed=0;bar.serial++;bar.version=0;Event(BarrierEvent,&bar);assert(bar.consumed==1 && bar.result==ZvnResult_BadVersion);bar.version=ZVN_CONTRACT_VERSION;
    assert(computeBarriers==0);
    assert(counters.drawsRecorded==actualDraws && preparedDraws.empty());
    // A count draw's slot must lie in a range this plugin zeroed in the same frame; a zero event that was refused
    // (the ones above) noted nothing, so nothing is zeroed.
    assert(zeroedRanges.empty() && !CountSlotZeroed(5,&zero,0));
    NoteZeroed(5,&zero,0,1024);
    assert(CountSlotZeroed(5,&zero,0) && CountSlotZeroed(5,&zero,1020) && !CountSlotZeroed(5,&zero,1024) && !CountSlotZeroed(6,&zero,0) && !CountSlotZeroed(5,&warm,0));
    NoteZeroed(20,&zero,0,16);   // far later: the old note is retired, the new one is this frame's
    assert(!CountSlotZeroed(5,&zero,0) && CountSlotZeroed(20,&zero,12) && !CountSlotZeroed(20,&zero,16) && zeroedRanges.size()==1);
    zeroedRanges.clear();
    // The ring's reuse rule (2026-10-10): frame F writes set F mod 4, whose previous writer F-4 must be at or before
    // Unity's safe frame; the first frames (nothing finished yet) are within the ring.
    assert(!RingReuseUnsafe(10,6) && !RingReuseUnsafe(10,7) && RingReuseUnsafe(10,5) && RingReuseUnsafe(100,0) && !RingReuseUnsafe(4,0) && !RingReuseUnsafe(0,0));
    warm.consumed=0;warm.serial++;hold=true;Event(WarmPipelineEvent,&warm);
    assert(warm.consumed==0 && held.size()==1);hold=false;
    __atomic_store_n(&held.back()->consumed,1,__ATOMIC_RELEASE);held.clear();
    pipelines.erase(100);available=false;
    std::printf("ABI / parser / malformed input / direct ranges / group overlap / paired preparation lifetime / warm event: passed\n");
    return 0;
}
