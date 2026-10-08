// Runs the real SPIR-V descriptor parser on Android without creating a graphics device.
#include "ZantetsuVpVulkan.cpp"
#include <fstream>
#include <iterator>
#include <cassert>
#include <cstddef>
int main(int argc, char** argv) {
    if(argc<2)return 2;
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
    pipelines.erase(100);available=false;
    std::printf("ABI / parser / malformed input / direct ranges / group overlap / paired preparation lifetime: passed\n");
    return 0;
}
