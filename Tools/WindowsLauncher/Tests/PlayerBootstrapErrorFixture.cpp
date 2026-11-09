#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>

static int WINAPI BootstrapTestMessageBox(HWND, LPCWSTR message, LPCWSTR, UINT)
{
    static DWORD calls = 0;
    ++calls;
    const DWORD size = GetEnvironmentVariableW(L"ZANTETSU_NATIVE_TEST_RESULT", nullptr, 0);
    if (!size) return IDOK;
    std::vector<wchar_t> buffer(size);
    GetEnvironmentVariableW(L"ZANTETSU_NATIVE_TEST_RESULT", buffer.data(), size);
    const std::wstring path = std::wstring(buffer.data()) + L".error";
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
    if (file != INVALID_HANDLE_VALUE)
    {
        DWORD written = 0;
        const DWORD chars = static_cast<DWORD>(lstrlenW(message));
        WriteFile(file, &calls, sizeof(calls), &written, nullptr);
        WriteFile(file, &chars, sizeof(chars), &written, nullptr);
        WriteFile(file, message, chars * sizeof(wchar_t), &written, nullptr);
        CloseHandle(file);
    }
    return IDOK;
}

// Execute the production entry, replacing only its interactive error dialog in this fixture.
#define MessageBoxW BootstrapTestMessageBox
#include "../PlayerBootstrap.cpp"
