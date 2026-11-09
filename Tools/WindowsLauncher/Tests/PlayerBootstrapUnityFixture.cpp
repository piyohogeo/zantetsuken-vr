#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <string>
#include <vector>

namespace
{
    std::wstring Environment(const wchar_t* name)
    {
        const DWORD size = GetEnvironmentVariableW(name, nullptr, 0);
        if (!size) return L"";
        std::vector<wchar_t> value(size);
        GetEnvironmentVariableW(name, value.data(), size);
        return value.data();
    }
    void Number(HANDLE file, DWORD value)
    {
        DWORD written;
        WriteFile(file, &value, sizeof(value), &written, nullptr);
    }
#ifndef ZANTETSU_FIXTURE_NO_ENTRY
    void Text(HANDLE file, const std::wstring& value)
    {
        Number(file, static_cast<DWORD>(value.size()));
        DWORD written;
        WriteFile(file, value.data(), static_cast<DWORD>(value.size() * sizeof(wchar_t)), &written, nullptr);
    }
#endif
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        const std::wstring path = Environment(L"ZANTETSU_NATIVE_TEST_RESULT") + L".loaded";
        HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            Number(file, GetCurrentProcessId());
            CloseHandle(file);
        }
    }
    return TRUE;
}

#ifndef ZANTETSU_FIXTURE_NO_ENTRY
extern "C" __declspec(dllexport) int UnityMain2(HINSTANCE instance, LPCWSTR dataFolder, LPWSTR command, int)
{
    static DWORD calls = 0;
    ++calls;
    const std::wstring result = Environment(L"ZANTETSU_NATIVE_TEST_RESULT");
    HANDLE file = CreateFileW(result.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (file == INVALID_HANDLE_VALUE) return 97;
    std::vector<wchar_t> image(32768);
    GetModuleFileNameW(nullptr, image.data(), static_cast<DWORD>(image.size()));
    Number(file, 0x3154555a);
    Number(file, GetCurrentProcessId());
    Number(file, calls);
    Number(file, instance == GetModuleHandleW(nullptr) ? 1 : 0);
    Number(file, dataFolder == nullptr ? 1 : 0);
    Number(file, GetModuleHandleW(L"clr.dll") == nullptr ? 1 : 0);
    Text(file, image.data());
    Text(file, GetCommandLineW());
    Text(file, command);
    Text(file, Environment(L"ZANTETSU_GRAPHICS_SOURCE"));
    Text(file, Environment(L"ZANTETSU_GRAPHICS_REASON"));
    const auto nvidia = reinterpret_cast<DWORD*>(GetProcAddress(GetModuleHandleW(nullptr), "NvOptimusEnablement"));
    const auto amd = reinterpret_cast<int*>(GetProcAddress(GetModuleHandleW(nullptr), "AmdPowerXpressRequestHighPerformance"));
    const auto sdk = reinterpret_cast<UINT*>(GetProcAddress(GetModuleHandleW(nullptr), "D3D12SDKVersion"));
    const auto sdkPath = reinterpret_cast<const char**>(GetProcAddress(GetModuleHandleW(nullptr), "D3D12SDKPath"));
    Number(file, nvidia ? *nvidia : 0);
    Number(file, amd ? static_cast<DWORD>(*amd) : 0);
    Number(file, sdk ? *sdk : 0);
    std::wstring path;
    if (sdkPath && *sdkPath) for (const char* character = *sdkPath; *character; ++character) path += static_cast<wchar_t>(*character);
    Text(file, path);
    CloseHandle(file);
    return 7;
}
#endif
