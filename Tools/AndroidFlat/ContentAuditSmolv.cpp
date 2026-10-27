#include "smolv.h"
#include <cstring>
extern "C" __declspec(dllexport) size_t audit_smolv_size(const void* data, size_t size) {
    return smolv::GetDecodedBufferSize(data, size);
}
extern "C" __declspec(dllexport) int audit_smolv_decode(const void* data, size_t size, void* output, size_t output_size) {
    return smolv::Decode(data, size, output, output_size) ? 1 : 0;
}
extern "C" __declspec(dllexport) size_t audit_smolv_encode(const void* data, size_t size, void* output, size_t output_size) {
    smolv::ByteArray encoded;
    if (!smolv::Encode(data, size, encoded)) return 0;
    if (output && output_size >= encoded.size()) std::memcpy(output, encoded.data(), encoded.size());
    return encoded.size();
}
