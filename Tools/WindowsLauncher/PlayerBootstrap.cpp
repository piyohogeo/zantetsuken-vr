#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

extern "C"
{
    __declspec(dllexport) DWORD NvOptimusEnablement = 1;
    __declspec(dllexport) int AmdPowerXpressRequestHighPerformance = 1;
    __declspec(dllexport) extern const UINT D3D12SDKVersion = 618;
    __declspec(dllexport) extern const char* D3D12SDKPath = ".\\D3D12\\";
}

namespace
{
    constexpr DWORD ProtocolMagic = 0x3150475a;
    constexpr DWORD RefusedExit = 22;
    constexpr size_t HeaderBytes = 6 * sizeof(uint32_t);
    constexpr size_t MaximumResponseBytes = 128 * 1024;
    constexpr ULONGLONG ResolverTimeoutMs = 15000;

    struct LaunchFailure
    {
        std::wstring message;
    };

    class Handle
    {
    public:
        explicit Handle(HANDLE value = nullptr) : value_(value) {}
        ~Handle() { Close(); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        HANDLE Get() const { return value_; }
        void Close()
        {
            if (value_ && value_ != INVALID_HANDLE_VALUE) CloseHandle(value_);
            value_ = nullptr;
        }
    private:
        HANDLE value_;
    };

    class OwnedResolver
    {
    public:
        explicit OwnedResolver(HANDLE process) : process_(process) {}
        ~OwnedResolver()
        {
            if (WaitForSingleObject(process_.Get(), 0) == WAIT_TIMEOUT)
            {
                TerminateProcess(process_.Get(), RefusedExit);
                WaitForSingleObject(process_.Get(), 5000);
            }
        }
        HANDLE Get() const { return process_.Get(); }
    private:
        Handle process_;
    };

    class AttributeList
    {
    public:
        AttributeList()
        {
            SIZE_T bytes = 0;
            InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
            storage_.resize(bytes);
            list_ = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage_.data());
            if (!InitializeProcThreadAttributeList(list_, 1, 0, &bytes))
                throw LaunchFailure{ L"Could not prepare the graphics resolver's handle list." };
        }
        ~AttributeList() { DeleteProcThreadAttributeList(list_); }
        LPPROC_THREAD_ATTRIBUTE_LIST Get() const { return list_; }
    private:
        std::vector<unsigned char> storage_;
        LPPROC_THREAD_ATTRIBUTE_LIST list_ = nullptr;
    };

    struct GraphicsChoice
    {
        DWORD status;
        std::wstring command;
        std::wstring source;
        std::wstring reason;
        std::wstring error;
    };

    std::wstring ApplicationDirectory()
    {
        std::vector<wchar_t> path(32768);
        const DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
        if (!length || length >= path.size())
            throw LaunchFailure{ L"Could not determine the Player's folder." };
        const std::wstring filename(path.data(), length);
        const size_t slash = filename.find_last_of(L"\\/");
        if (slash == std::wstring::npos)
            throw LaunchFailure{ L"The Player's folder is invalid." };
        return filename.substr(0, slash + 1);
    }

    uint32_t HeaderValue(const std::vector<unsigned char>& response, size_t index)
    {
        uint32_t result;
        std::memcpy(&result, response.data() + index * sizeof(result), sizeof(result));
        return result;
    }

    std::wstring ReadText(const std::vector<unsigned char>& response, size_t& offset, uint32_t chars)
    {
        std::wstring result(chars, L'\0');
        if (chars) std::memcpy(result.data(), response.data() + offset, chars * sizeof(wchar_t));
        offset += chars * sizeof(wchar_t);
        for (size_t index = 0; index < result.size(); ++index)
        {
            const wchar_t character = result[index];
            if (!character) throw LaunchFailure{ L"The graphics resolver returned a NUL character." };
            if (character >= 0xd800 && character <= 0xdbff)
            {
                if (++index == result.size() || result[index] < 0xdc00 || result[index] > 0xdfff)
                    throw LaunchFailure{ L"The graphics resolver returned invalid Unicode." };
            }
            else if (character >= 0xdc00 && character <= 0xdfff)
                throw LaunchFailure{ L"The graphics resolver returned invalid Unicode." };
        }
        return result;
    }

    GraphicsChoice ParseChoice(const std::vector<unsigned char>& response, DWORD exitCode)
    {
        if (response.size() < HeaderBytes || HeaderValue(response, 0) != ProtocolMagic)
            throw LaunchFailure{ L"The graphics resolver returned an invalid response header." };
        const DWORD status = HeaderValue(response, 1);
        const uint32_t commandChars = HeaderValue(response, 2);
        const uint32_t sourceChars = HeaderValue(response, 3);
        const uint32_t reasonChars = HeaderValue(response, 4);
        const uint32_t errorChars = HeaderValue(response, 5);
        if ((status != 0 && status != RefusedExit) || status != exitCode)
            throw LaunchFailure{ L"The graphics resolver's result and exit code do not agree." };
        if (commandChars > 32766 || sourceChars > 32 || reasonChars > 2048 || errorChars > 4096)
            throw LaunchFailure{ L"The graphics resolver returned an overlong field." };
        const uint64_t characters = static_cast<uint64_t>(commandChars) + sourceChars + reasonChars + errorChars;
        if (response.size() != HeaderBytes + characters * sizeof(wchar_t))
            throw LaunchFailure{ L"The graphics resolver returned a truncated or extra payload." };
        size_t offset = HeaderBytes;
        GraphicsChoice choice{ status, ReadText(response, offset, commandChars), ReadText(response, offset, sourceChars),
            ReadText(response, offset, reasonChars), ReadText(response, offset, errorChars) };
        if (status == 0)
        {
            if (choice.command.empty() || !choice.error.empty() || (choice.source != L"auto" && choice.source != L"saved" && choice.source != L"command-line"))
                throw LaunchFailure{ L"The graphics resolver returned an invalid successful result." };
        }
        else if (!choice.command.empty() || !choice.source.empty() || !choice.reason.empty())
            throw LaunchFailure{ L"The graphics resolver returned an invalid failure result." };
        return choice;
    }

    std::vector<unsigned char> ReadResolver(HANDLE pipe, HANDLE process, ULONGLONG started)
    {
        std::vector<unsigned char> response;
        unsigned char buffer[8192];
        for (;;)
        {
            if (GetTickCount64() - started >= ResolverTimeoutMs)
                throw LaunchFailure{ L"The graphics resolver did not finish within 15 seconds." };
            const DWORD wait = WaitForSingleObject(process, 0);
            if (wait != WAIT_OBJECT_0 && wait != WAIT_TIMEOUT)
                throw LaunchFailure{ L"Could not observe the graphics resolver's completion." };
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
            {
                if (GetLastError() != ERROR_BROKEN_PIPE)
                    throw LaunchFailure{ L"Could not read the graphics resolver's response." };
                available = 0;
            }
            if (available)
            {
                const DWORD requested = available < sizeof(buffer) ? available : static_cast<DWORD>(sizeof(buffer));
                DWORD read = 0;
                if (!ReadFile(pipe, buffer, requested, &read, nullptr) || !read)
                    throw LaunchFailure{ L"Could not read the graphics resolver's response." };
                if (response.size() + read > MaximumResponseBytes)
                    throw LaunchFailure{ L"The graphics resolver returned too much data." };
                response.insert(response.end(), buffer, buffer + read);
                continue;
            }
            if (wait == WAIT_OBJECT_0) return response;
            Sleep(5);
        }
    }

    GraphicsChoice ResolveGraphics(const std::wstring& directory, const wchar_t* userCommand)
    {
        const ULONGLONG started = GetTickCount64();
        SECURITY_ATTRIBUTES inherited{ sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE };
        HANDLE readPipe = nullptr, writePipe = nullptr;
        if (!CreatePipe(&readPipe, &writePipe, &inherited, 0))
            throw LaunchFailure{ L"Could not create the graphics resolver's response pipe." };
        Handle read(readPipe), write(writePipe);
        if (!SetHandleInformation(read.Get(), HANDLE_FLAG_INHERIT, 0))
            throw LaunchFailure{ L"Could not isolate the graphics resolver's response pipe." };
        Handle nullDevice(CreateFileW(L"NUL", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, &inherited, OPEN_EXISTING, 0, nullptr));
        if (nullDevice.Get() == INVALID_HANDLE_VALUE)
            throw LaunchFailure{ L"Could not prepare the graphics resolver's standard handles." };
        HANDLE inheritedHandles[] = { write.Get(), nullDevice.Get() };
        AttributeList attributes;
        if (!UpdateProcThreadAttribute(attributes.Get(), 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
            inheritedHandles, sizeof(inheritedHandles), nullptr, nullptr))
            throw LaunchFailure{ L"Could not isolate the graphics resolver's inherited handles." };
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = nullDevice.Get();
        startup.StartupInfo.hStdError = nullDevice.Get();
        startup.StartupInfo.hStdOutput = write.Get();
        startup.lpAttributeList = attributes.Get();
        const std::wstring helper = directory + L"Zantetsu.Graphics.exe";
        std::wstring arguments = L"\"" + helper + L"\" " + userCommand;
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(helper.c_str(), arguments.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
            nullptr, directory.c_str(), &startup.StartupInfo, &process))
            throw LaunchFailure{ L"Could not start Zantetsu.Graphics.exe from the Player's folder." };
        OwnedResolver resolver(process.hProcess);
        Handle thread(process.hThread);
        write.Close();
        nullDevice.Close();
        const auto response = ReadResolver(read.Get(), resolver.Get(), started);
        DWORD exitCode = 0;
        if (!GetExitCodeProcess(resolver.Get(), &exitCode))
            throw LaunchFailure{ L"Could not read the graphics resolver's exit code." };
        return ParseChoice(response, exitCode);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR userCommand, int show)
{
    try
    {
        const std::wstring directory = ApplicationDirectory();
        GraphicsChoice choice = ResolveGraphics(directory, userCommand);
        if (choice.status != 0)
            throw LaunchFailure{ choice.error.empty() ? L"The graphics resolver rejected this launch." : choice.error };
        if (!SetEnvironmentVariableW(L"ZANTETSU_GRAPHICS_SOURCE", choice.source.c_str())
            || !SetEnvironmentVariableW(L"ZANTETSU_GRAPHICS_REASON", choice.reason.c_str()))
            throw LaunchFailure{ L"Could not record the selected graphics settings." };
        HMODULE unity = LoadLibraryExW((directory + L"UnityPlayer.dll").c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!unity) throw LaunchFailure{ L"Could not load UnityPlayer.dll from the Player's folder." };
        using UnityEntry = int (*)(HINSTANCE, LPCWSTR, LPWSTR, int);
        const auto entry = reinterpret_cast<UnityEntry>(GetProcAddress(unity, "UnityMain2"));
        if (!entry)
        {
            FreeLibrary(unity);
            throw LaunchFailure{ L"UnityPlayer.dll does not provide the required UnityMain2 entry." };
        }
        // Keep the engine loaded until process shutdown, as Unity's standard imported entry does.
        return entry(instance, nullptr, choice.command.data(), show);
    }
    catch (const LaunchFailure& failure)
    {
        MessageBoxW(nullptr, failure.message.c_str(), L"WORLD CUTTER - Launch error", MB_OK | MB_ICONERROR);
    }
    catch (...)
    {
        MessageBoxW(nullptr, L"Could not prepare the graphics settings.", L"WORLD CUTTER - Launch error", MB_OK | MB_ICONERROR);
    }
    return RefusedExit;
}