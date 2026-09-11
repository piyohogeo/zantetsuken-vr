#include "NvencEncoderSession.h"

#include <cassert>
#include <new>

namespace zantetsu
{
    namespace
    {
        bool IsSameGuid(const GUID& left, const GUID& right)
        {
            return left.Data1 == right.Data1 &&
                left.Data2 == right.Data2 &&
                left.Data3 == right.Data3 &&
                left.Data4[0] == right.Data4[0] &&
                left.Data4[1] == right.Data4[1] &&
                left.Data4[2] == right.Data4[2] &&
                left.Data4[3] == right.Data4[3] &&
                left.Data4[4] == right.Data4[4] &&
                left.Data4[5] == right.Data4[5] &&
                left.Data4[6] == right.Data4[6] &&
                left.Data4[7] == right.Data4[7];
        }

        /// An array sized to what the driver reported, released with the
        /// scope. A refused allocation is a failed observation, not an absent
        /// capability, so the caller checks the pointer.
        template <typename T>
        class ScopedArray
        {
        public:
            explicit ScopedArray(uint32_t count)
                : _values(count == 0 ? nullptr : new (std::nothrow) T[count]())
            {
            }

            ~ScopedArray() { delete[] _values; }

            ScopedArray(const ScopedArray&) = delete;
            ScopedArray& operator=(const ScopedArray&) = delete;

            T* Get() const { return _values; }

        private:
            T* _values;
        };
    }

    NvencEncoderSession::~NvencEncoderSession()
    {
        // Destroying an owner that still holds an encoder or a registered
        // completion event is a contract violation, not a state this handles:
        // the caller unregisters and closes first, and an owner whose
        // unregister or close was refused is kept. Nothing is unregistered,
        // closed, or destroyed implicitly here.
        assert(_completionEvent == nullptr);
        assert(_encoder == nullptr);

        ReleaseDeviceAndDriver();
    }

    void NvencEncoderSession::ReleaseDeviceAndDriver()
    {
        if (_device != nullptr)
        {
            ID3D11Device* device = _device;
            _device = nullptr;
            device->Release();
        }

        _destroyEncoder = nullptr;
        _functionList = nullptr;

        // The driver owner releases its module when it is destroyed; this
        // session's is destroyed with the session itself.
    }

    NvencEncoderSessionOpenStatus NvencEncoderSession::Open(
        const D3D11DeviceBinding& binding)
    {
        // One owner, one attempt - settled before anything is loaded or
        // acquired.
        if (_openAttempted)
        {
            return NvencEncoderSessionOpenStatus::Failed;
        }

        _openAttempted = true;

        const NvencDriverApiLoadStatus loadStatus = _driverApi.Load();
        if (loadStatus == NvencDriverApiLoadStatus::DriverApiUnsupported)
        {
            return NvencEncoderSessionOpenStatus::Unsupported;
        }

        if (loadStatus != NvencDriverApiLoadStatus::Loaded)
        {
            _lastWin32Error = _driverApi.LastWin32Error();
            _lastNvencStatus = _driverApi.LastNvencStatus();
            return NvencEncoderSessionOpenStatus::Failed;
        }

        // The session's own reference to the device, independent of what the
        // graphics lifecycle does next.
        if (!binding.TryAcquireOwned(&_device))
        {
            return NvencEncoderSessionOpenStatus::Unsupported;
        }

        const NV_ENCODE_API_FUNCTION_LIST& functionList = _driverApi.FunctionList();

        // The way to close a session is confirmed before one is opened, so an
        // encoder is never left with no way to destroy it.
        if (functionList.nvEncDestroyEncoder == nullptr ||
            functionList.nvEncOpenEncodeSessionEx == nullptr)
        {
            ReleaseDeviceAndDriver();
            return NvencEncoderSessionOpenStatus::Failed;
        }

        _destroyEncoder = functionList.nvEncDestroyEncoder;
        _functionList = &functionList;

        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS params = {};
        params.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        params.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
        params.device = _device;

        // The struct-version encoding, which is not what
        // NvEncodeAPIGetMaxSupportedVersion reports.
        params.apiVersion = NVENCAPI_VERSION;

        void* encoder = nullptr;
        const NVENCSTATUS status =
            functionList.nvEncOpenEncodeSessionEx(&params, &encoder);

        if (status != NV_ENC_SUCCESS || encoder == nullptr)
        {
            _lastNvencStatus = status;
            ReleaseDeviceAndDriver();

            // Only the driver saying this device cannot encode is an
            // unsupported configuration.
            if (status == NV_ENC_ERR_NO_ENCODE_DEVICE ||
                status == NV_ENC_ERR_UNSUPPORTED_DEVICE)
            {
                return NvencEncoderSessionOpenStatus::Unsupported;
            }

            return NvencEncoderSessionOpenStatus::Failed;
        }

        _encoder = encoder;
        return NvencEncoderSessionOpenStatus::Opened;
    }

    bool NvencEncoderSession::TryObserveCapabilities(
        NvencEncoderCapabilityObservation* observation)
    {
        if (observation == nullptr)
        {
            return false;
        }

        // Nothing is observed unless there is an open session to observe, and
        // an owner that has already tried to close is finished with the
        // driver.
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // One owner, one observation - settled before any entry point is
        // called, so neither a success nor a failure is repeated against the
        // driver.
        if (_capabilityObservationAttempted)
        {
            return false;
        }

        _capabilityObservationAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;
        if (api.nvEncGetEncodeGUIDCount == nullptr ||
            api.nvEncGetEncodeGUIDs == nullptr ||
            api.nvEncGetEncodeProfileGUIDCount == nullptr ||
            api.nvEncGetEncodeProfileGUIDs == nullptr ||
            api.nvEncGetInputFormatCount == nullptr ||
            api.nvEncGetInputFormats == nullptr ||
            api.nvEncGetEncodeCaps == nullptr)
        {
            return false;
        }

        NvencEncoderCapabilityObservation result = {};

        if (!TryObserveH264Support(result.supportsH264Encode))
        {
            return false;
        }

        // Without H.264 there is nothing further to ask about: the rest of the
        // observation is the false and zero it was observed to be.
        if (!result.supportsH264Encode)
        {
            *observation = result;
            return true;
        }

        if (!TryObserveH264HighProfileSupport(result.supportsH264HighProfile) ||
            !TryObserveNv12InputSupport(result.supportsNv12Input))
        {
            return false;
        }

        int asyncEncodeSupport = 0;
        int maximumWidth = 0;
        int maximumHeight = 0;

        if (!TryQueryCap(NV_ENC_CAPS_ASYNC_ENCODE_SUPPORT, asyncEncodeSupport) ||
            !TryQueryCap(NV_ENC_CAPS_WIDTH_MAX, maximumWidth) ||
            !TryQueryCap(NV_ENC_CAPS_HEIGHT_MAX, maximumHeight))
        {
            return false;
        }

        result.supportsAsyncEncode = asyncEncodeSupport != 0;
        result.maximumEncodeWidth = static_cast<int32_t>(maximumWidth);
        result.maximumEncodeHeight = static_cast<int32_t>(maximumHeight);

        *observation = result;
        return true;
    }

    bool NvencEncoderSession::TryObserveH264Support(bool& supported)
    {
        supported = false;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        uint32_t count = 0;
        NVENCSTATUS status = api.nvEncGetEncodeGUIDCount(_encoder, &count);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        // The array is as large as the driver said it needs to be; no fixed
        // upper bound or per-GPU count is assumed.
        ScopedArray<GUID> guids(count);
        if (guids.Get() == nullptr)
        {
            return false;
        }

        uint32_t written = 0;
        status = api.nvEncGetEncodeGUIDs(_encoder, guids.Get(), count, &written);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        if (written > count)
        {
            return false;
        }

        for (uint32_t i = 0; i < written; ++i)
        {
            if (IsSameGuid(guids.Get()[i], NV_ENC_CODEC_H264_GUID))
            {
                supported = true;
                break;
            }
        }

        return true;
    }

    bool NvencEncoderSession::TryObserveH264HighProfileSupport(bool& supported)
    {
        supported = false;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        uint32_t count = 0;
        NVENCSTATUS status = api.nvEncGetEncodeProfileGUIDCount(
            _encoder, NV_ENC_CODEC_H264_GUID, &count);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        ScopedArray<GUID> guids(count);
        if (guids.Get() == nullptr)
        {
            return false;
        }

        uint32_t written = 0;
        status = api.nvEncGetEncodeProfileGUIDs(
            _encoder, NV_ENC_CODEC_H264_GUID, guids.Get(), count, &written);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        if (written > count)
        {
            return false;
        }

        for (uint32_t i = 0; i < written; ++i)
        {
            if (IsSameGuid(guids.Get()[i], NV_ENC_H264_PROFILE_HIGH_GUID))
            {
                supported = true;
                break;
            }
        }

        return true;
    }

    bool NvencEncoderSession::TryObserveNv12InputSupport(bool& supported)
    {
        supported = false;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        uint32_t count = 0;
        NVENCSTATUS status =
            api.nvEncGetInputFormatCount(_encoder, NV_ENC_CODEC_H264_GUID, &count);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        ScopedArray<NV_ENC_BUFFER_FORMAT> formats(count);
        if (formats.Get() == nullptr)
        {
            return false;
        }

        uint32_t written = 0;
        status = api.nvEncGetInputFormats(
            _encoder, NV_ENC_CODEC_H264_GUID, formats.Get(), count, &written);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        if (written > count)
        {
            return false;
        }

        for (uint32_t i = 0; i < written; ++i)
        {
            if (formats.Get()[i] == NV_ENC_BUFFER_FORMAT_NV12)
            {
                supported = true;
                break;
            }
        }

        return true;
    }

    bool NvencEncoderSession::TryQueryCap(NV_ENC_CAPS cap, int& value)
    {
        value = 0;

        NV_ENC_CAPS_PARAM params = {};
        params.version = NV_ENC_CAPS_PARAM_VER;
        params.capsToQuery = cap;

        const NVENCSTATUS status = _functionList->nvEncGetEncodeCaps(
            _encoder, NV_ENC_CODEC_H264_GUID, &params, &value);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        return true;
    }

    bool NvencEncoderSession::TryInitializeEncoder(
        const NvencEncoderInitializationRequest& request)
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // One owner, one initialization - settled before the driver is called,
        // so neither a success nor a failure is attempted twice.
        if (_initializationAttempted)
        {
            return false;
        }

        _initializationAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;
        if (api.nvEncGetEncodePresetConfigEx == nullptr ||
            api.nvEncInitializeEncoder == nullptr)
        {
            return false;
        }

        // What the driver recommends for this preset and tuning is the base;
        // only the fixed request is written over it, and everything the preset
        // decided that the request does not name is kept.
        NV_ENC_PRESET_CONFIG presetConfig = {};
        presetConfig.version = NV_ENC_PRESET_CONFIG_VER;
        presetConfig.presetCfg.version = NV_ENC_CONFIG_VER;

        NVENCSTATUS status = api.nvEncGetEncodePresetConfigEx(
            _encoder,
            request.encodeGuid,
            request.presetGuid,
            request.tuningInfo,
            &presetConfig);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        NV_ENC_CONFIG config = presetConfig.presetCfg;
        config.version = NV_ENC_CONFIG_VER;
        config.profileGUID = request.profileGuid;
        config.gopLength = request.gopLength;
        config.frameIntervalP = request.frameIntervalP;
        config.frameFieldMode = request.progressiveEncoding != 0
            ? NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME
            : config.frameFieldMode;

        config.rcParams.rateControlMode = request.rateControlMode;
        config.rcParams.constQP.qpIntra = request.qpIntra;
        config.rcParams.constQP.qpInterP = request.qpInterP;
        config.rcParams.constQP.qpInterB = request.qpInterB;

        NV_ENC_CONFIG_H264& h264 = config.encodeCodecConfig.h264Config;
        h264.idrPeriod = request.idrPeriod;
        h264.repeatSPSPPS = request.repeatSequenceAndPictureParameterSets;
        h264.outputAUD = request.outputAccessUnitDelimiter;
        h264.disableSPSPPS = request.disableSequenceAndPictureParameterSets;
        h264.chromaFormatIDC = request.chromaFormatIdc;
        h264.level = request.level;

        NV_ENC_INITIALIZE_PARAMS params = {};
        params.version = NV_ENC_INITIALIZE_PARAMS_VER;
        params.encodeGUID = request.encodeGuid;
        params.presetGUID = request.presetGuid;
        params.tuningInfo = request.tuningInfo;
        params.encodeWidth = request.encodeWidth;
        params.encodeHeight = request.encodeHeight;
        params.maxEncodeWidth = request.maximumEncodeWidth;
        params.maxEncodeHeight = request.maximumEncodeHeight;

        // The display aspect ratio follows the one encoded size; it is not a
        // separate decision.
        params.darWidth = request.encodeWidth;
        params.darHeight = request.encodeHeight;

        params.frameRateNum = request.frameRateNumerator;
        params.frameRateDen = request.frameRateDenominator;
        params.enableEncodeAsync = request.enableEncodeAsync;
        params.enablePTD = request.enablePictureTypeDecision;
        params.enableOutputInVidmem = request.enableOutputInVideoMemory;
        params.encodeConfig = &config;

        status = api.nvEncInitializeEncoder(_encoder, &params);
        if (status != NV_ENC_SUCCESS)
        {
            // The session is still open and still the caller's to close.
            _lastNvencStatus = status;
            return false;
        }

        _encoderInitialized = true;
        return true;
    }

    bool NvencEncoderSession::TryRegisterCompletionEvent()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // An event belongs to an encoder that has been initialized, and only
        // one event is ever held.
        if (!_encoderInitialized || _completionEvent != nullptr)
        {
            return false;
        }

        // One owner, one registration - settled before anything is created or
        // registered.
        if (_completionEventRegistrationAttempted)
        {
            return false;
        }

        _completionEventRegistrationAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        // The way to unregister is confirmed before an event is registered, so
        // a registered event is never left with no way to take it back.
        if (api.nvEncRegisterAsyncEvent == nullptr ||
            api.nvEncUnregisterAsyncEvent == nullptr)
        {
            return false;
        }

        // Unnamed, auto-reset, initially non-signalled.
        HANDLE completionEvent = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (completionEvent == nullptr)
        {
            _lastWin32Error = ::GetLastError();
            return false;
        }

        NV_ENC_EVENT_PARAMS params = {};
        params.version = NV_ENC_EVENT_PARAMS_VER;
        params.completionEvent = completionEvent;

        const NVENCSTATUS status = api.nvEncRegisterAsyncEvent(_encoder, &params);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;

            // Nothing was registered, so the event this call created is the
            // only thing to undo; the session stays initialized, open, and
            // closable.
            if (!::CloseHandle(completionEvent))
            {
                _lastWin32Error = ::GetLastError();
            }

            return false;
        }

        _completionEvent = completionEvent;
        return true;
    }

    bool NvencEncoderSession::TryUnregisterCompletionEvent()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        if (_completionEvent == nullptr)
        {
            return false;
        }

        // One owner, one unregistration - settled before the driver is
        // touched.
        if (_completionEventUnregistrationAttempted)
        {
            return false;
        }

        _completionEventUnregistrationAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;
        if (api.nvEncUnregisterAsyncEvent == nullptr)
        {
            return false;
        }

        NV_ENC_EVENT_PARAMS params = {};
        params.version = NV_ENC_EVENT_PARAMS_VER;
        params.completionEvent = _completionEvent;

        const NVENCSTATUS status = api.nvEncUnregisterAsyncEvent(_encoder, &params);
        if (status != NV_ENC_SUCCESS)
        {
            // The driver still knows this event, so it stays this session's.
            _lastNvencStatus = status;
            return false;
        }

        if (!::CloseHandle(_completionEvent))
        {
            // Unregistered but not closed: the handle is still held rather
            // than assumed gone.
            _lastWin32Error = ::GetLastError();
            return false;
        }

        // Cleared only once both steps have succeeded.
        _completionEvent = nullptr;
        return true;
    }

    NvencEncoderSessionCloseStatus NvencEncoderSession::Close()
    {
        // An encoder with a registered event is not destroyed: the event is
        // unregistered first. Refused before the close attempt is spent, so
        // the caller can still close after unregistering.
        if (_completionEvent != nullptr)
        {
            return NvencEncoderSessionCloseStatus::Failed;
        }

        // One owner, one close attempt - settled before the driver is called,
        // so a destroy the driver refused is never issued again.
        if (_closeAttempted)
        {
            return NvencEncoderSessionCloseStatus::Failed;
        }

        if (_encoder == nullptr || _destroyEncoder == nullptr)
        {
            return NvencEncoderSessionCloseStatus::Failed;
        }

        _closeAttempted = true;

        const NVENCSTATUS status = _destroyEncoder(_encoder);
        if (status != NV_ENC_SUCCESS)
        {
            // Nothing is released on a refused destroy, and the attempt is
            // spent: the encoder handle, the device reference, the function
            // table, and the module all stay exactly where they were, and this
            // owner will not call destroy again.
            _lastNvencStatus = status;
            return NvencEncoderSessionCloseStatus::Failed;
        }

        // Cleared only once the driver has accepted the destroy.
        _encoder = nullptr;

        ReleaseDeviceAndDriver();
        return NvencEncoderSessionCloseStatus::Closed;
    }
}
