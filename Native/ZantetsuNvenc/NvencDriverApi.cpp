#include "NvencDriverApi.h"

namespace zantetsu
{
    namespace
    {
        typedef NVENCSTATUS(NVENCAPI* PfnNvEncodeApiGetMaxSupportedVersion)(uint32_t*);
        typedef NVENCSTATUS(NVENCAPI* PfnNvEncodeApiCreateInstance)(
            NV_ENCODE_API_FUNCTION_LIST*);

        const wchar_t* const kDriverModuleName = L"nvEncodeAPI64.dll";
    }

    NvencDriverApi::~NvencDriverApi()
    {
        ReleaseModule();
    }

    void NvencDriverApi::ReleaseModule()
    {
        _loaded = false;
        _functionList = {};

        if (_module != nullptr)
        {
            HMODULE module = _module;
            _module = nullptr;
            ::FreeLibrary(module);
        }
    }

    NvencDriverApiLoadStatus NvencDriverApi::Load()
    {
        if (_loaded)
        {
            // One owner, one load.
            return NvencDriverApiLoadStatus::ObservationFailed;
        }

        _maximumSupportedVersion = 0;
        _lastWin32Error = 0;
        _lastNvencStatus = NV_ENC_SUCCESS;

        // The driver's own module, from System32 alone: no ordinary search
        // path, working directory, PATH entry, or module shipped beside this
        // plugin can answer instead.
        _module = ::LoadLibraryExW(
            kDriverModuleName, nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (_module == nullptr)
        {
            _lastWin32Error = ::GetLastError();
            return NvencDriverApiLoadStatus::ObservationFailed;
        }

        PfnNvEncodeApiGetMaxSupportedVersion getMaxSupportedVersion =
            reinterpret_cast<PfnNvEncodeApiGetMaxSupportedVersion>(
                ::GetProcAddress(_module, "NvEncodeAPIGetMaxSupportedVersion"));
        if (getMaxSupportedVersion == nullptr)
        {
            _lastWin32Error = ::GetLastError();
            ReleaseModule();
            return NvencDriverApiLoadStatus::ObservationFailed;
        }

        PfnNvEncodeApiCreateInstance createInstance =
            reinterpret_cast<PfnNvEncodeApiCreateInstance>(
                ::GetProcAddress(_module, "NvEncodeAPICreateInstance"));
        if (createInstance == nullptr)
        {
            _lastWin32Error = ::GetLastError();
            ReleaseModule();
            return NvencDriverApiLoadStatus::ObservationFailed;
        }

        uint32_t maximumSupportedVersion = 0;
        NVENCSTATUS status = getMaxSupportedVersion(&maximumSupportedVersion);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            ReleaseModule();
            return NvencDriverApiLoadStatus::ObservationFailed;
        }

        _maximumSupportedVersion = maximumSupportedVersion;

        // The one thing that makes a driver too old rather than unobservable.
        if (!IsNvencDriverApiVersionSupported(maximumSupportedVersion))
        {
            ReleaseModule();
            return NvencDriverApiLoadStatus::DriverApiUnsupported;
        }

        NV_ENCODE_API_FUNCTION_LIST functionList = {};
        functionList.version = NV_ENCODE_API_FUNCTION_LIST_VER;

        status = createInstance(&functionList);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            ReleaseModule();
            return NvencDriverApiLoadStatus::ObservationFailed;
        }

        // Obtaining the table is the whole proof: which of its pointers must be
        // non-null is settled by whoever first calls one.
        _functionList = functionList;
        _loaded = true;
        return NvencDriverApiLoadStatus::Loaded;
    }
}
