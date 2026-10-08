// Runs the real SPIR-V descriptor parser on Android without creating a graphics device.
#include "ZantetsuVpVulkan.cpp"
#include <fstream>
#include <iterator>
#include <cassert>
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
        // Forward/motion vertex stages require four storage buffers; casters require the fifth bounds buffer.
        if(std::strstr(argv[i],".vs.bin") && (storage<4 || !uniform))return 4;
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
    pipelines.erase(100);available=false;
    std::printf("ABI / parser / malformed input / event ranges / paired preparation lifetime: passed\n");
    return 0;
}
