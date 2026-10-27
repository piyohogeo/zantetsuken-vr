#include <windows.h>
#include <unknwn.h>
#include "dxcapi.h"
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include <io.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>
int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    HMODULE module = LoadLibraryExW(argv[1], nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!module) return 3;
    auto create = reinterpret_cast<DxcCreateInstanceProc>(GetProcAddress(module, "DxcCreateInstance"));
    if (!create) return 4;
    IDxcCompiler3* compiler = nullptr;
    if (FAILED(create(CLSID_DxcCompiler, IID_PPV_ARGS(&compiler)))) return 5;
    IDxcBlob* inputBlob = nullptr;
    std::vector<char> file;
    if (std::wcscmp(argv[2], L"--control") == 0) {
        std::string name((std::istreambuf_iterator<char>(std::cin)), {});
        int chars = MultiByteToWideChar(CP_UTF8, 0, name.data(), static_cast<int>(name.size()), nullptr, 0);
        std::wstring filename(chars, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, name.data(), static_cast<int>(name.size()), filename.data(), chars);
        filename += L".hlsl";
        const char source[] = "[numthreads(1,1,1)] void main(uint3 id:SV_DispatchThreadID) {}";
        DxcBuffer buffer{source, sizeof(source)-1, DXC_CP_UTF8};
        LPCWSTR args[] = {filename.c_str(), L"-T", L"cs_6_0", L"-E", L"main", L"-Zi", L"-Qembed_debug", L"-Od"};
        IDxcResult* result = nullptr;
        if (FAILED(compiler->Compile(&buffer, args, sizeof(args)/sizeof(args[0]), nullptr, IID_PPV_ARGS(&result)))) return 6;
        HRESULT status;
        if (FAILED(result->GetStatus(&status)) || FAILED(status) || FAILED(result->GetResult(&inputBlob))) return 7;
        result->Release();
    } else {
        std::ifstream stream(std::filesystem::path(argv[2]), std::ios::binary);
        if (!stream) return 8;
        file.assign(std::istreambuf_iterator<char>(stream), {});
    }
    DxcBuffer buffer{inputBlob ? inputBlob->GetBufferPointer() : file.data(), inputBlob ? inputBlob->GetBufferSize() : file.size(), 0};
    IDxcResult* result = nullptr;
    if (FAILED(compiler->Disassemble(&buffer, IID_PPV_ARGS(&result)))) return 9;
    HRESULT status;
    if (FAILED(result->GetStatus(&status)) || FAILED(status)) return 10;
    IDxcBlob* output = nullptr;
    if (FAILED(result->GetResult(&output)) || !output) return 11;
    _setmode(_fileno(stdout), _O_BINARY);
    const auto length = output->GetBufferSize();
    const auto wrote = std::fwrite(output->GetBufferPointer(), 1, length, stdout);
    output->Release();
    result->Release();
    if (inputBlob) inputBlob->Release();
    compiler->Release();
    FreeLibrary(module);
    return wrote == length ? 0 : 12;
}
