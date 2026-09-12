#include "NvencEncoderSession.h"

#include <cassert>
#include <new>

// The fixed conversion shaders, compiled by this build and embedded as byte
// code. There is no run-time compile and no d3dcompiler dependency: these
// headers are generated into the build directory before this file is compiled.
#include "NvencRgbaToNv12V1_ChromaPs.h"
#include "NvencRgbaToNv12V1_LumaPs.h"
#include "NvencRgbaToNv12V1_Vs.h"

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
        // Destroying an owner that still holds an encoder, a completion-event
        // handle or registration, an output bitstream buffer, an input
        // surface, a conversion shader, a conversion command, or a source
        // surface binding is a contract violation, not a state
        // this handles: the caller releases and closes first, and an owner
        // whose release or close was refused is kept. Nothing is unregistered,
        // closed, released, or destroyed implicitly here.
        assert(!AnyEncodeSampleInFlight());
        assert(!AnyCompletionEventHeld());
        assert(!AnyOutputBitstreamBufferHeld());
        assert(!AnyInputSurfaceHeld());
        assert(!AnyConversionCommandHeld());
        assert(!AnyConversionCommandBusy());
        assert(!AnySourceSurfaceHeld());
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

    bool NvencEncoderSession::AnyCompletionEventHeld() const
    {
        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            if (_slots[i].completionEvent != nullptr ||
                _slots[i].completionEventRegistered)
            {
                return true;
            }
        }

        return false;
    }

    /// Unregisters one slot if the driver has it, then closes its handle. Each
    /// fact is cleared only once the step that undoes it has succeeded, so a
    /// refusal leaves the slot exactly as truthful as it was.
    bool NvencEncoderSession::TryReleaseCompletionEventSlot(EncodeSampleSlot& slot)
    {
        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        if (slot.completionEventRegistered)
        {
            if (api.nvEncUnregisterAsyncEvent == nullptr)
            {
                return false;
            }

            NV_ENC_EVENT_PARAMS params = {};
            params.version = NV_ENC_EVENT_PARAMS_VER;
            params.completionEvent = slot.completionEvent;

            const NVENCSTATUS status = api.nvEncUnregisterAsyncEvent(_encoder, &params);
            if (status != NV_ENC_SUCCESS)
            {
                _lastNvencStatus = status;
                return false;
            }

            slot.completionEventRegistered = false;
        }

        if (slot.completionEvent != nullptr)
        {
            if (!::CloseHandle(slot.completionEvent))
            {
                _lastWin32Error = ::GetLastError();
                return false;
            }

            slot.completionEvent = nullptr;
        }

        return true;
    }

    bool NvencEncoderSession::AnyOutputBitstreamBufferHeld() const
    {
        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            if (_slots[i].outputBitstreamBuffer != nullptr)
            {
                return true;
            }
        }

        return false;
    }

    /// Destroys one slot's output buffer, clearing it only once the driver has
    /// accepted.
    bool NvencEncoderSession::TryDestroyOutputBitstreamBuffer(EncodeSampleSlot& slot)
    {
        if (slot.outputBitstreamBuffer == nullptr)
        {
            return true;
        }

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;
        if (api.nvEncDestroyBitstreamBuffer == nullptr)
        {
            return false;
        }

        const NVENCSTATUS status =
            api.nvEncDestroyBitstreamBuffer(_encoder, slot.outputBitstreamBuffer);
        if (status != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = status;
            return false;
        }

        slot.outputBitstreamBuffer = nullptr;
        return true;
    }

    /// Unwinds the buffers this preparation made, in reverse. A destroy that is
    /// itself refused stops the unwinding: that buffer and the ones before it
    /// stay held rather than being released on an assumption.
    void NvencEncoderSession::RollBackPreparedOutputBitstreamBuffers(uint32_t count)
    {
        for (uint32_t i = count; i > 0; --i)
        {
            if (!TryDestroyOutputBitstreamBuffer(_slots[i - 1]))
            {
                return;
            }
        }
    }

    /// Whether this is the one source texture shape this session accepts. The
    /// descriptor was established by reading a real Player's render texture,
    /// not by mapping a Unity format name onto a DXGI one, and nothing else is
    /// accepted in its place: there is no compatibility table and no fallback.
    bool NvencEncoderSession::IsAcceptedSourceTexture(ID3D11Texture2D* texture) const
    {
        // The exact device this session is open on. A texture from another
        // device is not this session's to read, whatever it looks like.
        ID3D11Device* owningDevice = nullptr;
        texture->GetDevice(&owningDevice);
        if (owningDevice == nullptr)
        {
            return false;
        }

        const bool sameDevice = owningDevice == _device;
        owningDevice->Release();

        if (!sameDevice)
        {
            return false;
        }

        D3D11_TEXTURE2D_DESC desc = {};
        texture->GetDesc(&desc);

        // RGBA8 at the one size, with nothing the fixed conversion cannot
        // read: one mip, one slice, no multisampling, and readable as a shader
        // resource. The storage format is typeless - that is what a Player's
        // capture render target really reports - so it carries no colour
        // interpretation of its own, and the sRGB view created below is what
        // makes the read decode.
        return desc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS &&
            desc.Width == kInputSurfaceWidth &&
            desc.Height == kInputSurfaceHeight &&
            desc.MipLevels == 1 &&
            desc.ArraySize == 1 &&
            desc.SampleDesc.Count == 1 &&
            desc.SampleDesc.Quality == 0 &&
            (desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) != 0;
    }

    /// Gives one source surface back in the reverse of the order it was taken:
    /// the view, then the reference. A COM release cannot be refused, so this
    /// reports nothing.
    void NvencEncoderSession::ReleaseSourceSurfaceSlot(SourceSurfaceSlot& slot)
    {
        if (slot.shaderResourceView != nullptr)
        {
            ID3D11ShaderResourceView* view = slot.shaderResourceView;
            slot.shaderResourceView = nullptr;
            view->Release();
        }

        if (slot.texture != nullptr)
        {
            ID3D11Texture2D* texture = slot.texture;
            slot.texture = nullptr;

            // Only this session's own reference. The texture itself stays the
            // caller's, and is neither destroyed nor handed back.
            texture->Release();
        }
    }

    /// Unwinds the surfaces this binding took, in reverse. Nothing here can be
    /// refused, so unlike the registered NV12 surfaces it always completes.
    void NvencEncoderSession::RollBackBoundSourceSurfaces(uint32_t count)
    {
        for (uint32_t i = count; i > 0; --i)
        {
            ReleaseSourceSurfaceSlot(_sourceSlots[i - 1]);
        }
    }

    bool NvencEncoderSession::AnySourceSurfaceHeld() const
    {
        for (uint32_t i = 0; i < kSourceSurfaceSlotCount; ++i)
        {
            if (_sourceSlots[i].texture != nullptr ||
                _sourceSlots[i].shaderResourceView != nullptr)
            {
                return true;
            }
        }

        return false;
    }

    bool NvencEncoderSession::AreSourceSurfacesFullyBound() const
    {
        for (uint32_t i = 0; i < kSourceSurfaceSlotCount; ++i)
        {
            if (_sourceSlots[i].texture == nullptr ||
                _sourceSlots[i].shaderResourceView == nullptr)
            {
                return false;
            }
        }

        return true;
    }

    bool NvencEncoderSession::TryBindSourceSurfaces(
        void* const* textures, uint32_t count)
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        if (textures == nullptr || count != kSourceSurfaceSlotCount)
        {
            return false;
        }

        // The sources are the first thing bound after the encoder exists:
        // nothing may be prepared on top of them yet, and nothing may be held.
        if (!_encoderInitialized ||
            AnySourceSurfaceHeld() ||
            AnyInputSurfaceHeld() ||
            AnyOutputBitstreamBufferHeld() ||
            AnyCompletionEventHeld())
        {
            return false;
        }

        // One owner, one binding - settled before D3D11 is touched.
        if (_sourceSurfacesBindAttempted)
        {
            return false;
        }

        _sourceSurfacesBindAttempted = true;

        for (uint32_t i = 0; i < kSourceSurfaceSlotCount; ++i)
        {
            // What the caller hands over is a resource, which is all the
            // graphics contract promises. The 2D interface is asked for
            // through the resource itself rather than assumed from the
            // address, and the reference that query returns is this session's
            // own - owned from the moment it exists, so nothing is AddRef'd a
            // second time.
            ID3D11Resource* resource = static_cast<ID3D11Resource*>(textures[i]);
            if (resource == nullptr)
            {
                RollBackBoundSourceSurfaces(i);
                return false;
            }

            ID3D11Texture2D* texture = nullptr;
            const HRESULT queryHr = resource->QueryInterface(
                __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&texture));
            if (FAILED(queryHr) || texture == nullptr)
            {
                _lastHResult = queryHr;
                RollBackBoundSourceSurfaces(i);
                return false;
            }

            _sourceSlots[i].texture = texture;

            // A descriptor that is not the accepted one is not a D3D11
            // failure, so no HRESULT is invented for it. Everything from here
            // on asks the 2D interface, never the address that came in.
            if (!IsAcceptedSourceTexture(texture))
            {
                RollBackBoundSourceSurfaces(i + 1);
                return false;
            }

            // The explicit typed view the shader reads through. Its sRGB
            // format is what decodes to linear on read; nothing beyond the
            // format, the dimension, and the one mip is set.
            D3D11_SHADER_RESOURCE_VIEW_DESC viewDesc = {};
            viewDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            viewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            viewDesc.Texture2D.MostDetailedMip = 0;
            viewDesc.Texture2D.MipLevels = 1;

            ID3D11ShaderResourceView* view = nullptr;
            const HRESULT hr =
                _device->CreateShaderResourceView(texture, &viewDesc, &view);
            if (FAILED(hr) || view == nullptr)
            {
                _lastHResult = hr;
                RollBackBoundSourceSurfaces(i + 1);
                return false;
            }

            _sourceSlots[i].shaderResourceView = view;
        }

        return AreSourceSurfacesFullyBound();
    }

    bool NvencEncoderSession::TryReleaseSourceSurfaces()
    {
        if (_encoder == nullptr || _closeAttempted)
        {
            return false;
        }

        if (!AnySourceSurfaceHeld())
        {
            return false;
        }

        // Everything prepared on top of the sources goes first. Checked before
        // this release's one attempt is spent, so the caller can still release
        // them once the rest is gone.
        if (AnyInputSurfaceHeld() ||
            AnyConversionCommandHeld() ||
            AnyOutputBitstreamBufferHeld() ||
            AnyCompletionEventHeld())
        {
            return false;
        }

        if (_sourceSurfacesReleaseAttempted)
        {
            return false;
        }

        _sourceSurfacesReleaseAttempted = true;

        RollBackBoundSourceSurfaces(kSourceSurfaceSlotCount);
        return true;
    }

    void RunConversionCommandFromEventData(void* eventData)
    {
        if (eventData == nullptr)
        {
            return;
        }

        ConversionCommandEventDataV1& data =
            *static_cast<ConversionCommandEventDataV1*>(eventData);
        if (data.session == nullptr)
        {
            return;
        }

        data.session->RunConversionCommand(data);
    }

    bool NvencEncoderSession::TryReleaseConversionCommandSlot(
        ConversionCommandSlot& slot)
    {
        // A close that the OS refuses leaves this session owning the handle.
        // Nothing after it is released either, so the fence cannot outlive the
        // event that names its completion.
        if (slot.callbackEvent != nullptr)
        {
            if (!::CloseHandle(slot.callbackEvent))
            {
                _lastWin32Error = ::GetLastError();
                return false;
            }

            slot.callbackEvent = nullptr;
        }

        if (slot.completionEvent != nullptr)
        {
            if (!::CloseHandle(slot.completionEvent))
            {
                _lastWin32Error = ::GetLastError();
                return false;
            }

            slot.completionEvent = nullptr;
        }

        if (slot.fence != nullptr)
        {
            ID3D11Fence* fence = slot.fence;
            slot.fence = nullptr;
            fence->Release();
        }

        return true;
    }

    /// Unwinds what this preparation took, in reverse. A refused close stops
    /// the unwinding there: that slot and the ones before it stay held, and so
    /// do the device interfaces.
    bool NvencEncoderSession::RollBackPreparedConversionCommands(uint32_t count)
    {
        for (uint32_t i = count; i > 0; --i)
        {
            if (!TryReleaseConversionCommandSlot(_conversionSlots[i - 1]))
            {
                return false;
            }
        }

        return true;
    }

    /// The device and context interfaces, given back in the reverse of the
    /// order they were taken.
    void NvencEncoderSession::ReleaseConversionDeviceInterfaces()
    {
        if (_context4 != nullptr)
        {
            ID3D11DeviceContext4* context4 = _context4;
            _context4 = nullptr;
            context4->Release();
        }

        if (_immediateContext != nullptr)
        {
            ID3D11DeviceContext* context = _immediateContext;
            _immediateContext = nullptr;
            context->Release();
        }

        if (_device5 != nullptr)
        {
            ID3D11Device5* device5 = _device5;
            _device5 = nullptr;
            device5->Release();
        }
    }

    bool NvencEncoderSession::AnyConversionCommandHeld() const
    {
        if (_device5 != nullptr || _immediateContext != nullptr || _context4 != nullptr)
        {
            return true;
        }

        for (uint32_t i = 0; i < kConversionCommandSlotCount; ++i)
        {
            if (_conversionSlots[i].fence != nullptr ||
                _conversionSlots[i].completionEvent != nullptr ||
                _conversionSlots[i].callbackEvent != nullptr)
            {
                return true;
            }
        }

        return false;
    }

    bool NvencEncoderSession::AreConversionCommandsPrepared() const
    {
        if (_device5 == nullptr || _immediateContext == nullptr || _context4 == nullptr)
        {
            return false;
        }

        for (uint32_t i = 0; i < kConversionCommandSlotCount; ++i)
        {
            if (_conversionSlots[i].fence == nullptr ||
                _conversionSlots[i].completionEvent == nullptr ||
                _conversionSlots[i].callbackEvent == nullptr)
            {
                return false;
            }
        }

        return true;
    }

    bool NvencEncoderSession::AnyConversionCommandBusy() const
    {
        for (uint32_t i = 0; i < kConversionCommandSlotCount; ++i)
        {
            const LONG state = ::InterlockedCompareExchange(
                const_cast<volatile LONG*>(&_conversionSlots[i].state),
                static_cast<LONG>(ConversionCommandState::Idle),
                static_cast<LONG>(ConversionCommandState::Idle));
            if (state != static_cast<LONG>(ConversionCommandState::Idle))
            {
                return true;
            }
        }

        return false;
    }

    bool NvencEncoderSession::TryPrepareConversionCommands()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // The conversion commands come after everything they draw from and
        // into, and before everything that is prepared on top of them.
        if (!_encoderInitialized ||
            !AreSourceSurfacesFullyBound() ||
            !AreInputSurfacesFullyPrepared() ||
            AnyConversionCommandHeld() ||
            AnyOutputBitstreamBufferHeld() ||
            AnyCompletionEventHeld())
        {
            return false;
        }

        // One owner, one preparation - settled before D3D11 is touched.
        if (_conversionCommandsPrepareAttempted)
        {
            return false;
        }

        _conversionCommandsPrepareAttempted = true;

        // The exact device, and the exact immediate context that device hands
        // out. Both are asked for their newer interface rather than assumed to
        // have it, and each is owned from the moment it exists.
        const HRESULT deviceHr = _device->QueryInterface(
            __uuidof(ID3D11Device5), reinterpret_cast<void**>(&_device5));
        if (FAILED(deviceHr) || _device5 == nullptr)
        {
            _lastHResult = deviceHr;
            ReleaseConversionDeviceInterfaces();
            return false;
        }

        _device->GetImmediateContext(&_immediateContext);
        if (_immediateContext == nullptr)
        {
            _lastHResult = E_FAIL;
            ReleaseConversionDeviceInterfaces();
            return false;
        }

        const HRESULT contextHr = _immediateContext->QueryInterface(
            __uuidof(ID3D11DeviceContext4), reinterpret_cast<void**>(&_context4));
        if (FAILED(contextHr) || _context4 == nullptr)
        {
            _lastHResult = contextHr;
            ReleaseConversionDeviceInterfaces();
            return false;
        }

        for (uint32_t i = 0; i < kConversionCommandSlotCount; ++i)
        {
            ID3D11Fence* fence = nullptr;
            const HRESULT fenceHr = _device5->CreateFence(
                0, D3D11_FENCE_FLAG_NONE, __uuidof(ID3D11Fence),
                reinterpret_cast<void**>(&fence));
            if (FAILED(fenceHr) || fence == nullptr)
            {
                _lastHResult = fenceHr;
                if (RollBackPreparedConversionCommands(i))
                {
                    ReleaseConversionDeviceInterfaces();
                }

                return false;
            }

            _conversionSlots[i].fence = fence;

            // Auto-reset and unsignalled: one waiter is released per
            // completion, and a stale signal never satisfies the next wait.
            HANDLE completionEvent = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (completionEvent == nullptr)
            {
                _lastWin32Error = ::GetLastError();
                if (RollBackPreparedConversionCommands(i + 1))
                {
                    ReleaseConversionDeviceInterfaces();
                }

                return false;
            }

            _conversionSlots[i].completionEvent = completionEvent;

            // The other half of the completion: the callback says on this one
            // that it has finished and published what it did. Manual-reset, so
            // the fact stays true until the command is collected - a
            // collection that then times out on the GPU can be repeated.
            HANDLE callbackEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (callbackEvent == nullptr)
            {
                _lastWin32Error = ::GetLastError();
                if (RollBackPreparedConversionCommands(i + 1))
                {
                    ReleaseConversionDeviceInterfaces();
                }

                return false;
            }

            _conversionSlots[i].callbackEvent = callbackEvent;
            _conversionSlots[i].lastGeneration = 0;
            _conversionSlots[i].lastHResult = S_OK;
            _conversionSlots[i].lastWin32Error = 0;
            _conversionSlots[i].fenceEventRegistered = false;
            _conversionSlots[i].state =
                static_cast<LONG>(ConversionCommandState::Idle);
        }

        return AreConversionCommandsPrepared();
    }

    bool NvencEncoderSession::TryReleaseConversionCommands()
    {
        if (_encoder == nullptr || _closeAttempted)
        {
            return false;
        }

        if (!AnyConversionCommandHeld())
        {
            return false;
        }

        // Everything prepared on top of the commands goes first, and no
        // command may be in flight. Both are checked before this release's one
        // attempt is spent.
        if (AnyOutputBitstreamBufferHeld() || AnyCompletionEventHeld())
        {
            return false;
        }

        if (AnyEncodeSampleInFlight())
        {
            return false;
        }

        if (AnyConversionCommandBusy())
        {
            return false;
        }

        if (_conversionCommandsReleaseAttempted)
        {
            return false;
        }

        _conversionCommandsReleaseAttempted = true;

        if (!RollBackPreparedConversionCommands(kConversionCommandSlotCount))
        {
            // Stopped at a handle the OS refused to close: that slot and the
            // ones before it stay with the session, and so do the device
            // interfaces.
            return false;
        }

        ReleaseConversionDeviceInterfaces();
        return true;
    }

    bool NvencEncoderSession::TryArmConversionCommand(
        uint32_t syncSlotIndex,
        uint32_t sourceSlotIndex,
        uint32_t sampleSlotIndex,
        uint64_t generation,
        void** eventData)
    {
        if (eventData == nullptr)
        {
            return false;
        }

        *eventData = nullptr;

        if (_encoder == nullptr || _closeAttempted)
        {
            return false;
        }

        if (syncSlotIndex >= kConversionCommandSlotCount ||
            sourceSlotIndex >= kSourceSurfaceSlotCount ||
            sampleSlotIndex >= kEncodeSampleSlotCount)
        {
            return false;
        }

        // Everything this command will touch has to be completely prepared.
        if (!AreConversionCommandsPrepared() ||
            !AreSourceSurfacesFullyBound() ||
            !AreInputSurfacesFullyPrepared())
        {
            return false;
        }

        ConversionCommandSlot& slot = _conversionSlots[syncSlotIndex];

        // A generation is a lease: it only ever moves forward, and it is the
        // fence value this command signals.
        if (generation <= slot.lastGeneration)
        {
            return false;
        }

        // Idle is the only state a command is armed from. Taken atomically, so
        // a slot cannot be armed twice.
        const LONG previous = ::InterlockedCompareExchange(
            &slot.state,
            static_cast<LONG>(ConversionCommandState::Armed),
            static_cast<LONG>(ConversionCommandState::Idle));
        if (previous != static_cast<LONG>(ConversionCommandState::Idle))
        {
            return false;
        }

        slot.lastGeneration = generation;
        slot.lastHResult = S_OK;
        slot.lastWin32Error = 0;
        slot.fenceEventRegistered = false;
        slot.eventData.session = this;
        slot.eventData.syncSlotIndex = syncSlotIndex;
        slot.eventData.sourceSlotIndex = sourceSlotIndex;
        slot.eventData.sampleSlotIndex = sampleSlotIndex;
        slot.eventData.reserved = 0;
        slot.eventData.generation = generation;

        *eventData = &slot.eventData;
        return true;
    }

    bool NvencEncoderSession::TryCancelArmedConversionCommand(
        uint32_t syncSlotIndex, uint64_t generation)
    {
        if (syncSlotIndex >= kConversionCommandSlotCount)
        {
            return false;
        }

        ConversionCommandSlot& slot = _conversionSlots[syncSlotIndex];
        if (slot.lastGeneration != generation)
        {
            return false;
        }

        // Only a command whose callback has not started can be taken back. One
        // that is already running will signal, so it is kept rather than
        // guessed about.
        const LONG previous = ::InterlockedCompareExchange(
            &slot.state,
            static_cast<LONG>(ConversionCommandState::Idle),
            static_cast<LONG>(ConversionCommandState::Armed));
        return previous == static_cast<LONG>(ConversionCommandState::Armed);
    }

    void NvencEncoderSession::RunConversionCommand(ConversionCommandEventDataV1& data)
    {
        if (data.syncSlotIndex >= kConversionCommandSlotCount)
        {
            return;
        }

        ConversionCommandSlot& slot = _conversionSlots[data.syncSlotIndex];

        // Armed to running, once. A second callback for the same command finds
        // it no longer armed and draws nothing.
        const LONG previous = ::InterlockedCompareExchange(
            &slot.state,
            static_cast<LONG>(ConversionCommandState::Running),
            static_cast<LONG>(ConversionCommandState::Armed));
        if (previous != static_cast<LONG>(ConversionCommandState::Armed))
        {
            return;
        }

        ID3D11DeviceContext* context = _immediateContext;
        if (context == nullptr || _context4 == nullptr)
        {
            // Nothing was drawn and nothing will signal, so the waiter is told
            // on the callback's own edge instead. Everything this callback has
            // to say is written before the state that says it is finished.
            slot.lastHResult = E_FAIL;

            if (!::SetEvent(slot.callbackEvent))
            {
                // Nobody will be woken, so what the OS said is the only thing
                // a waiter will ever have to go on.
                slot.lastWin32Error = ::GetLastError();
            }

            ::InterlockedExchange(
                &slot.state,
                static_cast<LONG>(ConversionCommandState::AwaitingCollection));
            return;
        }

        const uint32_t sourceSlot = data.sourceSlotIndex;
        const uint32_t sampleSlot = data.sampleSlotIndex;

        // ---- what this callback is about to change ----
        D3D11_PRIMITIVE_TOPOLOGY savedTopology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
        context->IAGetPrimitiveTopology(&savedTopology);

        ID3D11InputLayout* savedInputLayout = nullptr;
        context->IAGetInputLayout(&savedInputLayout);

        ID3D11VertexShader* savedVertexShader = nullptr;
        context->VSGetShader(&savedVertexShader, nullptr, nullptr);

        ID3D11PixelShader* savedPixelShader = nullptr;
        context->PSGetShader(&savedPixelShader, nullptr, nullptr);

        ID3D11GeometryShader* savedGeometryShader = nullptr;
        context->GSGetShader(&savedGeometryShader, nullptr, nullptr);

        ID3D11HullShader* savedHullShader = nullptr;
        context->HSGetShader(&savedHullShader, nullptr, nullptr);

        ID3D11DomainShader* savedDomainShader = nullptr;
        context->DSGetShader(&savedDomainShader, nullptr, nullptr);

        ID3D11ShaderResourceView* savedShaderResource = nullptr;
        context->PSGetShaderResources(0, 1, &savedShaderResource);

        UINT savedViewportCount =
            D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        D3D11_VIEWPORT savedViewports[
            D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] = {};
        context->RSGetViewports(&savedViewportCount, savedViewports);

        ID3D11RasterizerState* savedRasterizer = nullptr;
        context->RSGetState(&savedRasterizer);

        ID3D11RenderTargetView* savedTargets[
            D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
        ID3D11DepthStencilView* savedDepthStencil = nullptr;
        context->OMGetRenderTargets(
            D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, savedTargets, &savedDepthStencil);

        ID3D11BlendState* savedBlend = nullptr;
        FLOAT savedBlendFactor[4] = {};
        UINT savedSampleMask = 0;
        context->OMGetBlendState(&savedBlend, savedBlendFactor, &savedSampleMask);

        ID3D11DepthStencilState* savedDepthStencilState = nullptr;
        UINT savedStencilReference = 0;
        context->OMGetDepthStencilState(
            &savedDepthStencilState, &savedStencilReference);

        // ---- the one fixed state this conversion draws with ----
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->IASetInputLayout(nullptr);
        context->VSSetShader(_conversionVertexShader, nullptr, 0);

        // Nothing between the vertex and pixel stages: a geometry, hull, or
        // domain shader left bound would change what is drawn.
        context->GSSetShader(nullptr, nullptr, 0);
        context->HSSetShader(nullptr, nullptr, 0);
        context->DSSetShader(nullptr, nullptr, 0);

        // The default states: no blending, no depth or stencil, solid fill,
        // no scissor.
        context->OMSetBlendState(nullptr, nullptr, 0xFFFFFFFFu);
        context->OMSetDepthStencilState(nullptr, 0);
        context->RSSetState(nullptr);

        ID3D11ShaderResourceView* sourceView =
            _sourceSlots[sourceSlot].shaderResourceView;
        context->PSSetShaderResources(0, 1, &sourceView);

        // The Y plane, one output pixel per source pixel.
        ID3D11RenderTargetView* lumaTarget =
            _slots[sampleSlot].inputLumaRenderTargetView;
        context->OMSetRenderTargets(1, &lumaTarget, nullptr);

        D3D11_VIEWPORT lumaViewport = {};
        lumaViewport.TopLeftX = 0.0f;
        lumaViewport.TopLeftY = 0.0f;
        lumaViewport.Width = static_cast<FLOAT>(kInputSurfaceWidth);
        lumaViewport.Height = static_cast<FLOAT>(kInputSurfaceHeight);
        lumaViewport.MinDepth = 0.0f;
        lumaViewport.MaxDepth = 1.0f;
        context->RSSetViewports(1, &lumaViewport);

        context->PSSetShader(_conversionLumaPixelShader, nullptr, 0);
        context->Draw(3, 0);

        // The UV plane, one output pixel per 2x2 source block.
        ID3D11RenderTargetView* chromaTarget =
            _slots[sampleSlot].inputChromaRenderTargetView;
        context->OMSetRenderTargets(1, &chromaTarget, nullptr);

        D3D11_VIEWPORT chromaViewport = lumaViewport;
        chromaViewport.Width = static_cast<FLOAT>(kInputSurfaceWidth / 2);
        chromaViewport.Height = static_cast<FLOAT>(kInputSurfaceHeight / 2);
        context->RSSetViewports(1, &chromaViewport);

        context->PSSetShader(_conversionChromaPixelShader, nullptr, 0);
        context->Draw(3, 0);

        // ---- give the pipeline back exactly as it was ----
        ID3D11ShaderResourceView* noShaderResource = nullptr;
        context->PSSetShaderResources(0, 1, &noShaderResource);
        context->OMSetRenderTargets(
            D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, savedTargets, savedDepthStencil);
        context->RSSetViewports(savedViewportCount, savedViewports);
        context->RSSetState(savedRasterizer);
        context->OMSetBlendState(savedBlend, savedBlendFactor, savedSampleMask);
        context->OMSetDepthStencilState(savedDepthStencilState, savedStencilReference);
        context->DSSetShader(savedDomainShader, nullptr, 0);
        context->HSSetShader(savedHullShader, nullptr, 0);
        context->GSSetShader(savedGeometryShader, nullptr, 0);
        context->PSSetShader(savedPixelShader, nullptr, 0);
        context->VSSetShader(savedVertexShader, nullptr, 0);
        context->IASetInputLayout(savedInputLayout);
        context->IASetPrimitiveTopology(savedTopology);
        context->PSSetShaderResources(0, 1, &savedShaderResource);

        // The signal goes after both passes, so a completed fence means the
        // whole conversion is done and the source can be used again.
        const HRESULT signalHr = _context4->Signal(slot.fence, data.generation);

        // A getter hands back a reference; every one of them goes back here.
        if (savedInputLayout != nullptr) { savedInputLayout->Release(); }
        if (savedVertexShader != nullptr) { savedVertexShader->Release(); }
        if (savedPixelShader != nullptr) { savedPixelShader->Release(); }
        if (savedGeometryShader != nullptr) { savedGeometryShader->Release(); }
        if (savedHullShader != nullptr) { savedHullShader->Release(); }
        if (savedDomainShader != nullptr) { savedDomainShader->Release(); }
        if (savedShaderResource != nullptr) { savedShaderResource->Release(); }
        if (savedRasterizer != nullptr) { savedRasterizer->Release(); }
        for (UINT i = 0; i < D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT; ++i)
        {
            if (savedTargets[i] != nullptr) { savedTargets[i]->Release(); }
        }
        if (savedDepthStencil != nullptr) { savedDepthStencil->Release(); }
        if (savedBlend != nullptr) { savedBlend->Release(); }
        if (savedDepthStencilState != nullptr) { savedDepthStencilState->Release(); }

        // A failure is recorded for the waiter, never thrown and never
        // retried here. The order matters: everything this callback has to say
        // is written, the waiter is woken, and the state that says the command
        // is collectable is published last - so a worker that sees that state
        // sees a callback with nothing left to write, and one woken by the
        // event that finds the state not yet published simply comes back.
        slot.lastHResult = signalHr;

        if (!::SetEvent(slot.callbackEvent))
        {
            slot.lastWin32Error = ::GetLastError();
        }

        ::InterlockedExchange(
            &slot.state,
            static_cast<LONG>(ConversionCommandState::AwaitingCollection));
    }

    namespace
    {
        /// What a wait that did not succeed should report. A timeout leaves the
        /// thread's last error undefined, so it is never read there; what is
        /// reported instead is whatever the callback itself recorded, which is
        /// zero unless it failed to wake anyone. Only a failed wait has a
        /// meaningful last error.
        DWORD DescribeWaitFailure(DWORD waitResult, DWORD recordedError)
        {
            if (waitResult == WAIT_FAILED)
            {
                return ::GetLastError();
            }

            return recordedError;
        }
    }

    NvencConversionCollectStatus NvencEncoderSession::TryCollectConversionCommand(
        uint32_t syncSlotIndex,
        uint64_t generation,
        uint32_t timeoutMilliseconds,
        HRESULT* callbackHResult,
        DWORD* win32Error)
    {
        if (callbackHResult != nullptr)
        {
            *callbackHResult = S_OK;
        }

        if (win32Error != nullptr)
        {
            *win32Error = 0;
        }

        if (syncSlotIndex >= kConversionCommandSlotCount)
        {
            return NvencConversionCollectStatus::Failed;
        }

        ConversionCommandSlot& slot = _conversionSlots[syncSlotIndex];
        if (slot.fence == nullptr || slot.completionEvent == nullptr ||
            slot.callbackEvent == nullptr)
        {
            return NvencConversionCollectStatus::Failed;
        }

        // Exactly one outstanding command is collectable: this slot's current
        // generation. An older one, or a slot with nothing outstanding, is a
        // caller asking about work that is not there - a broken contract, not a
        // completion still to come.
        if (slot.lastGeneration != generation)
        {
            return NvencConversionCollectStatus::Failed;
        }

        const LONG state = ::InterlockedCompareExchange(
            &slot.state,
            static_cast<LONG>(ConversionCommandState::Idle),
            static_cast<LONG>(ConversionCommandState::Idle));
        if (state == static_cast<LONG>(ConversionCommandState::Idle))
        {
            return NvencConversionCollectStatus::Failed;
        }

        // One deadline for the whole collection, so waiting for the callback
        // and then for the GPU cannot add up to twice the timeout.
        const ULONGLONG deadline =
            ::GetTickCount64() + static_cast<ULONGLONG>(timeoutMilliseconds);

        // The callback first: it is what publishes the result, and it is the
        // only edge a callback that never reached its Signal arrives on. A
        // command that has already published one is not waited for again, so a
        // second collection after a fence timeout goes straight on.
        if (state != static_cast<LONG>(ConversionCommandState::AwaitingCollection))
        {
            const DWORD callbackWait =
                ::WaitForSingleObject(slot.callbackEvent, timeoutMilliseconds);
            if (callbackWait != WAIT_OBJECT_0)
            {
                const DWORD error = DescribeWaitFailure(callbackWait, slot.lastWin32Error);
                if (error != 0)
                {
                    _lastWin32Error = error;
                }

                if (win32Error != nullptr)
                {
                    *win32Error = error;
                }

                // A wait that ran out of time is the command still to come; a
                // wait that failed is not.
                return callbackWait == WAIT_TIMEOUT
                    ? NvencConversionCollectStatus::Pending
                    : NvencConversionCollectStatus::Failed;
            }

            // Woken, but the callback has not published yet: this command is
            // left for the next collection rather than spun on here.
            const LONG published = ::InterlockedCompareExchange(
                &slot.state,
                static_cast<LONG>(ConversionCommandState::AwaitingCollection),
                static_cast<LONG>(ConversionCommandState::AwaitingCollection));
            if (published != static_cast<LONG>(ConversionCommandState::AwaitingCollection))
            {
                return NvencConversionCollectStatus::Pending;
            }
        }

        // What the callback recorded, whether or not the GPU was ever asked to
        // do anything.
        const HRESULT callbackResult = slot.lastHResult;
        if (callbackHResult != nullptr)
        {
            *callbackHResult = callbackResult;
        }

        if (FAILED(callbackResult))
        {
            // Nothing signalled, so there is no fence to wait on. The command
            // stays outstanding rather than being returned to idle, and what
            // went wrong is not something waiting longer will fix.
            return NvencConversionCollectStatus::Failed;
        }

        if (slot.fence->GetCompletedValue() < generation)
        {
            // Registered once for this command. A second collection after a
            // timeout waits on the registration the first one made rather than
            // stacking another.
            if (!slot.fenceEventRegistered)
            {
                const HRESULT hr =
                    slot.fence->SetEventOnCompletion(generation, slot.completionEvent);
                if (FAILED(hr))
                {
                    if (callbackHResult != nullptr)
                    {
                        *callbackHResult = hr;
                    }

                    return NvencConversionCollectStatus::Failed;
                }

                slot.fenceEventRegistered = true;
            }

            const ULONGLONG now = ::GetTickCount64();
            const DWORD remaining = now >= deadline
                ? 0u
                : static_cast<DWORD>(deadline - now);

            const DWORD fenceWait =
                ::WaitForSingleObject(slot.completionEvent, remaining);
            if (fenceWait != WAIT_OBJECT_0)
            {
                const DWORD error = DescribeWaitFailure(fenceWait, slot.lastWin32Error);
                if (error != 0)
                {
                    _lastWin32Error = error;
                }

                if (win32Error != nullptr)
                {
                    *win32Error = error;
                }

                // The callback's result stays published and the slot stays
                // outstanding, so the same generation can be collected again.
                return fenceWait == WAIT_TIMEOUT
                    ? NvencConversionCollectStatus::Pending
                    : NvencConversionCollectStatus::Failed;
            }
        }

        if (slot.fence->GetCompletedValue() < generation)
        {
            // Woken without the value having been reached: still to come.
            return NvencConversionCollectStatus::Pending;
        }

        // Both completions are consumed here, before the slot is idle again:
        // the next command on this slot must produce its own. A reset the OS
        // refuses leaves the slot outstanding rather than idle with an event
        // that would complete the next command for nothing.
        //
        // The fence event goes first, with the registration that named it, and
        // the callback event after, because the callback event is what a
        // collector waits on to decide there is anything to do at all.
        if (!::ResetEvent(slot.completionEvent))
        {
            const DWORD error = ::GetLastError();
            _lastWin32Error = error;
            if (win32Error != nullptr)
            {
                *win32Error = error;
            }

            return NvencConversionCollectStatus::Failed;
        }

        slot.fenceEventRegistered = false;

        if (!::ResetEvent(slot.callbackEvent))
        {
            const DWORD error = ::GetLastError();
            _lastWin32Error = error;
            if (win32Error != nullptr)
            {
                *win32Error = error;
            }

            return NvencConversionCollectStatus::Failed;
        }

        ::InterlockedExchange(
            &slot.state, static_cast<LONG>(ConversionCommandState::Idle));
        return NvencConversionCollectStatus::Completed;
    }

    bool NvencEncoderSession::AreCompletionEventsPrepared() const
    {
        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            if (_slots[i].completionEvent == nullptr ||
                !_slots[i].completionEventRegistered)
            {
                return false;
            }
        }

        return true;
    }

    bool NvencEncoderSession::AreOutputBitstreamBuffersPrepared() const
    {
        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            if (_slots[i].outputBitstreamBuffer == nullptr)
            {
                return false;
            }
        }

        return true;
    }

    bool NvencEncoderSession::AnyEncodeSampleInFlight() const
    {
        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            if (_slots[i].mappedInputResource != nullptr ||
                _slots[i].submitted ||
                _slots[i].bitstreamLocked)
            {
                return true;
            }
        }

        return false;
    }

    NvencEncodeSubmitStatus NvencEncoderSession::TrySubmitEncodePicture(
        uint32_t sampleSlotIndex, uint64_t generation, NVENCSTATUS* lastStatus)
    {
        if (lastStatus != nullptr)
        {
            *lastStatus = NV_ENC_SUCCESS;
        }

        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr ||
            !_encoderInitialized)
        {
            return NvencEncodeSubmitStatus::Failed;
        }

        if (sampleSlotIndex >= kEncodeSampleSlotCount || generation == 0)
        {
            return NvencEncodeSubmitStatus::Failed;
        }

        // Everything a picture is built from has to be completely there.
        if (!AreInputSurfacesFullyPrepared() ||
            !AreOutputBitstreamBuffersPrepared() ||
            !AreCompletionEventsPrepared())
        {
            return NvencEncodeSubmitStatus::Failed;
        }

        EncodeSampleSlot& slot = _slots[sampleSlotIndex];

        // One frame at a time in a slot, and only ever forward.
        if (slot.mappedInputResource != nullptr || slot.submitted ||
            slot.bitstreamLocked)
        {
            return NvencEncodeSubmitStatus::Failed;
        }

        if (generation <= slot.lastSampleGeneration)
        {
            return NvencEncodeSubmitStatus::Failed;
        }

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        // The way back is confirmed before anything is mapped, so a mapped
        // input is never left with no way to unmap it.
        if (api.nvEncMapInputResource == nullptr ||
            api.nvEncUnmapInputResource == nullptr ||
            api.nvEncEncodePicture == nullptr)
        {
            return NvencEncodeSubmitStatus::Failed;
        }

        // The generation is spent here, before the driver is touched: one
        // attempt per sample generation, whatever that attempt comes to. A map
        // the driver refuses does not hand this generation back.
        slot.lastSampleGeneration = generation;

        // This generation has had no output collected yet.
        slot.outputCollectionAttempted = false;

        NV_ENC_MAP_INPUT_RESOURCE mapResource = {};
        mapResource.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
        mapResource.registeredResource = slot.registeredInputResource;

        const NVENCSTATUS mapStatus = api.nvEncMapInputResource(_encoder, &mapResource);
        if (mapStatus != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = mapStatus;
            if (lastStatus != nullptr)
            {
                *lastStatus = mapStatus;
            }

            // A refused map that gave nothing back changed no ownership, so the
            // caller can report an ordinary submit failure. A refused map that
            // left a handle, or a device that is gone, did not.
            if (mapResource.mappedResource != nullptr ||
                mapStatus == NV_ENC_ERR_DEVICE_NOT_EXIST)
            {
                return NvencEncodeSubmitStatus::Failed;
            }

            return NvencEncodeSubmitStatus::NotSubmitted;
        }

        if (mapResource.mappedResource == nullptr)
        {
            // Success without a handle is not something to reason about.
            return NvencEncodeSubmitStatus::Failed;
        }

        // Owned from the moment it exists: this session must unmap it.
        slot.mappedInputResource = mapResource.mappedResource;
        slot.mappedBufferFormat = mapResource.mappedBufferFmt;

        // The fixed picture. The encoder makes no picture type decision for
        // this profile, so the type, the display order, and the reference flag
        // are stated here - this is where the project's "every frame is an IDR"
        // becomes an explicit IDR picture rather than a force flag, which is
        // for the picture-type-decision path. The sequence and picture
        // parameter sets come from the initialization's repeat setting; nothing
        // is asked for per picture.
        NV_ENC_PIC_PARAMS picture = {};
        picture.version = NV_ENC_PIC_PARAMS_VER;
        picture.inputWidth = kInputSurfaceWidth;
        picture.inputHeight = kInputSurfaceHeight;
        picture.inputPitch = 0;
        picture.inputBuffer = slot.mappedInputResource;
        picture.bufferFmt = slot.mappedBufferFormat;
        picture.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
        picture.pictureType = NV_ENC_PIC_TYPE_IDR;
        picture.outputBitstream = slot.outputBitstreamBuffer;
        picture.completionEvent = slot.completionEvent;
        picture.inputTimeStamp = 0;
        picture.inputDuration = 0;
        picture.encodePicFlags = 0;
        picture.codecPicParams.h264PicParams.displayPOCSyntax = 0;
        picture.codecPicParams.h264PicParams.refPicFlag = 1;

        const NVENCSTATUS encodeStatus = api.nvEncEncodePicture(_encoder, &picture);
        if (encodeStatus != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = encodeStatus;
            if (lastStatus != nullptr)
            {
                *lastStatus = encodeStatus;
            }

            // The input is mapped and whether the encoder took it cannot be
            // established, so it stays mapped and this session keeps it. That
            // includes needing more input: this profile has no delay to drain,
            // so it is not an ordinary "not submitted" either.
            return NvencEncodeSubmitStatus::Failed;
        }

        slot.submitted = true;
        return NvencEncodeSubmitStatus::Submitted;
    }

    NvencOutputCollectStatus NvencEncoderSession::TryCopyCompletedOutput(
        uint32_t sampleSlotIndex,
        uint64_t generation,
        uint8_t* destination,
        uint32_t destinationCapacity,
        uint32_t* validLength,
        NVENCSTATUS* lastStatus,
        DWORD* win32Error)
    {
        if (validLength != nullptr)
        {
            *validLength = 0;
        }

        if (lastStatus != nullptr)
        {
            *lastStatus = NV_ENC_SUCCESS;
        }

        if (win32Error != nullptr)
        {
            *win32Error = 0;
        }

        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return NvencOutputCollectStatus::Failed;
        }

        if (sampleSlotIndex >= kEncodeSampleSlotCount || destination == nullptr ||
            destinationCapacity == 0)
        {
            return NvencOutputCollectStatus::Failed;
        }

        EncodeSampleSlot& slot = _slots[sampleSlotIndex];

        // Exactly the submitted picture this caller names, and no other.
        if (!slot.submitted || slot.bitstreamLocked ||
            slot.mappedInputResource == nullptr ||
            slot.lastSampleGeneration != generation)
        {
            return NvencOutputCollectStatus::Failed;
        }

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;
        if (api.nvEncLockBitstream == nullptr ||
            api.nvEncUnlockBitstream == nullptr ||
            api.nvEncUnmapInputResource == nullptr)
        {
            return NvencOutputCollectStatus::Failed;
        }

        // One collection per submitted picture, spent before the event is
        // waited on. A collection that failed after consuming the completion
        // event is never repeated: a second attempt would wait forever on an
        // event nothing will signal again, so it touches neither the OS nor the
        // driver.
        if (slot.outputCollectionAttempted)
        {
            return NvencOutputCollectStatus::Failed;
        }

        slot.outputCollectionAttempted = true;

        // The encoder signals this event when the picture is done. Waiting is
        // the whole point of this call, which is why only the output worker
        // makes it.
        const DWORD waited = ::WaitForSingleObject(slot.completionEvent, INFINITE);
        if (waited != WAIT_OBJECT_0)
        {
            if (waited == WAIT_FAILED)
            {
                const DWORD error = ::GetLastError();
                _lastWin32Error = error;
                if (win32Error != nullptr)
                {
                    *win32Error = error;
                }
            }

            return NvencOutputCollectStatus::Failed;
        }

        NV_ENC_LOCK_BITSTREAM lockBitstream = {};
        lockBitstream.version = NV_ENC_LOCK_BITSTREAM_VER;
        lockBitstream.outputBitstream = slot.outputBitstreamBuffer;
        lockBitstream.doNotWait = 0;

        const NVENCSTATUS lockStatus = api.nvEncLockBitstream(_encoder, &lockBitstream);
        if (lockStatus != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = lockStatus;
            if (lastStatus != nullptr)
            {
                *lastStatus = lockStatus;
            }

            return NvencOutputCollectStatus::Failed;
        }

        // Held from the moment the driver gave it, so the unlock below is owed
        // whatever happens to the bytes.
        slot.bitstreamLocked = true;

        const uint8_t* bitstream =
            static_cast<const uint8_t*>(lockBitstream.bitstreamBufferPtr);
        const uint32_t length = lockBitstream.bitstreamSizeInBytes;

        // Whether there is anything usable to copy is decided before the copy,
        // and does not change what is owed.
        const bool usable =
            bitstream != nullptr && length >= 1 && length <= destinationCapacity;

        if (usable)
        {
            for (uint32_t i = 0; i < length; ++i)
            {
                destination[i] = bitstream[i];
            }
        }

        const NVENCSTATUS unlockStatus =
            api.nvEncUnlockBitstream(_encoder, slot.outputBitstreamBuffer);
        if (unlockStatus != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = unlockStatus;
            if (lastStatus != nullptr)
            {
                *lastStatus = unlockStatus;
            }

            // Still locked, still mapped, still submitted: this slot is not
            // usable again and nothing is guessed about it.
            return NvencOutputCollectStatus::Failed;
        }

        slot.bitstreamLocked = false;

        const NVENCSTATUS unmapStatus =
            api.nvEncUnmapInputResource(_encoder, slot.mappedInputResource);
        if (unmapStatus != NV_ENC_SUCCESS)
        {
            _lastNvencStatus = unmapStatus;
            if (lastStatus != nullptr)
            {
                *lastStatus = unmapStatus;
            }

            // The lock is back but the map is not, so the slot stays held.
            return NvencOutputCollectStatus::Failed;
        }

        slot.mappedInputResource = nullptr;
        slot.mappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;
        slot.submitted = false;

        if (!usable)
        {
            // Everything was given back safely; there was simply nothing to
            // hand on. The slot is usable again.
            return NvencOutputCollectStatus::Rejected;
        }

        if (validLength != nullptr)
        {
            *validLength = length;
        }

        return NvencOutputCollectStatus::Copied;
    }

    bool NvencEncoderSession::AnyInputSurfaceSlotResourceHeld() const
    {
        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            const EncodeSampleSlot& slot = _slots[i];
            if (slot.inputTexture != nullptr ||
                slot.inputLumaRenderTargetView != nullptr ||
                slot.inputChromaRenderTargetView != nullptr ||
                slot.registeredInputResource != nullptr)
            {
                return true;
            }
        }

        return false;
    }

    bool NvencEncoderSession::AnyInputSurfaceHeld() const
    {
        return AnyInputSurfaceSlotResourceHeld() ||
            _conversionVertexShader != nullptr ||
            _conversionLumaPixelShader != nullptr ||
            _conversionChromaPixelShader != nullptr;
    }

    bool NvencEncoderSession::AreInputSurfacesFullyPrepared() const
    {
        if (_conversionVertexShader == nullptr ||
            _conversionLumaPixelShader == nullptr ||
            _conversionChromaPixelShader == nullptr)
        {
            return false;
        }

        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            const EncodeSampleSlot& slot = _slots[i];
            if (slot.inputTexture == nullptr ||
                slot.inputLumaRenderTargetView == nullptr ||
                slot.inputChromaRenderTargetView == nullptr ||
                slot.registeredInputResource == nullptr)
            {
                return false;
            }
        }

        return true;
    }

    /// Creates the three shader objects from the byte code this build compiled,
    /// in the order the pipeline uses them. Each is owned from the moment it
    /// exists; a refused creation keeps the real HRESULT and gives back what it
    /// already took, in reverse.
    bool NvencEncoderSession::TryCreateConversionShaders()
    {
        ID3D11VertexShader* vertexShader = nullptr;
        const HRESULT vertexHr = _device->CreateVertexShader(
            kZantetsuNvencRgbaToNv12VertexShaderV1,
            sizeof(kZantetsuNvencRgbaToNv12VertexShaderV1),
            nullptr,
            &vertexShader);
        if (FAILED(vertexHr) || vertexShader == nullptr)
        {
            _lastHResult = vertexHr;
            return false;
        }

        _conversionVertexShader = vertexShader;

        ID3D11PixelShader* lumaShader = nullptr;
        const HRESULT lumaHr = _device->CreatePixelShader(
            kZantetsuNvencRgbaToNv12LumaPixelShaderV1,
            sizeof(kZantetsuNvencRgbaToNv12LumaPixelShaderV1),
            nullptr,
            &lumaShader);
        if (FAILED(lumaHr) || lumaShader == nullptr)
        {
            _lastHResult = lumaHr;
            ReleaseConversionShaders();
            return false;
        }

        _conversionLumaPixelShader = lumaShader;

        ID3D11PixelShader* chromaShader = nullptr;
        const HRESULT chromaHr = _device->CreatePixelShader(
            kZantetsuNvencRgbaToNv12ChromaPixelShaderV1,
            sizeof(kZantetsuNvencRgbaToNv12ChromaPixelShaderV1),
            nullptr,
            &chromaShader);
        if (FAILED(chromaHr) || chromaShader == nullptr)
        {
            _lastHResult = chromaHr;
            ReleaseConversionShaders();
            return false;
        }

        _conversionChromaPixelShader = chromaShader;
        return true;
    }

    /// Gives the three back in the reverse of the order they were taken. Each
    /// reference is cleared before it is released, and a COM release reports
    /// nothing worth checking.
    void NvencEncoderSession::ReleaseConversionShaders()
    {
        if (_conversionChromaPixelShader != nullptr)
        {
            ID3D11PixelShader* shader = _conversionChromaPixelShader;
            _conversionChromaPixelShader = nullptr;
            shader->Release();
        }

        if (_conversionLumaPixelShader != nullptr)
        {
            ID3D11PixelShader* shader = _conversionLumaPixelShader;
            _conversionLumaPixelShader = nullptr;
            shader->Release();
        }

        if (_conversionVertexShader != nullptr)
        {
            ID3D11VertexShader* shader = _conversionVertexShader;
            _conversionVertexShader = nullptr;
            shader->Release();
        }
    }

    /// Unregisters one slot's input surface if the driver has it, then releases
    /// the two plane views and the texture, in the reverse of the order they
    /// were taken. Each fact is cleared only once the step that undoes it has
    /// succeeded; a COM release reports nothing worth checking, so its count is
    /// never read as evidence.
    bool NvencEncoderSession::TryReleaseInputSurfaceSlot(EncodeSampleSlot& slot)
    {
        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        if (slot.registeredInputResource != nullptr)
        {
            if (api.nvEncUnregisterResource == nullptr)
            {
                return false;
            }

            const NVENCSTATUS status =
                api.nvEncUnregisterResource(_encoder, slot.registeredInputResource);
            if (status != NV_ENC_SUCCESS)
            {
                _lastNvencStatus = status;
                return false;
            }

            slot.registeredInputResource = nullptr;
        }

        if (slot.inputChromaRenderTargetView != nullptr)
        {
            ID3D11RenderTargetView* view = slot.inputChromaRenderTargetView;
            slot.inputChromaRenderTargetView = nullptr;
            view->Release();
        }

        if (slot.inputLumaRenderTargetView != nullptr)
        {
            ID3D11RenderTargetView* view = slot.inputLumaRenderTargetView;
            slot.inputLumaRenderTargetView = nullptr;
            view->Release();
        }

        if (slot.inputTexture != nullptr)
        {
            ID3D11Texture2D* texture = slot.inputTexture;
            slot.inputTexture = nullptr;
            texture->Release();
        }

        return true;
    }

    /// Unwinds what this preparation took, in reverse: the slots first, then
    /// the conversion shaders. An unregister that is itself refused stops the
    /// unwinding - that slot and the ones before it stay held rather than
    /// being released on an assumption, and the shaders stay with them, since
    /// a surface the driver still has registered is not left without the
    /// pipeline that was prepared alongside it.
    void NvencEncoderSession::RollBackPreparedInputSurfaces(uint32_t count)
    {
        for (uint32_t i = count; i > 0; --i)
        {
            if (!TryReleaseInputSurfaceSlot(_slots[i - 1]))
            {
                return;
            }
        }

        if (!AnyInputSurfaceSlotResourceHeld())
        {
            ReleaseConversionShaders();
        }
    }

    bool NvencEncoderSession::TryPrepareInputSurfaces()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // Input surfaces are the first slot resource: the encoder must be
        // initialized, nothing else may be prepared yet, and nothing may be
        // held.
        if (!_encoderInitialized ||
            AnyCompletionEventHeld() ||
            AnyOutputBitstreamBufferHeld() ||
            AnyInputSurfaceHeld())
        {
            return false;
        }

        // The sources the conversion will read come first. Checked before this
        // preparation's one attempt is spent, so the caller can still prepare
        // once they are bound.
        if (!AreSourceSurfacesFullyBound())
        {
            return false;
        }

        // One owner, one preparation - settled before D3D11 or NVENC is
        // touched.
        if (_inputSurfacesPrepareAttempted)
        {
            return false;
        }

        _inputSurfacesPrepareAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        // The way to unregister is confirmed before anything is registered, so
        // a registered resource is never left with no way to take it back.
        if (api.nvEncRegisterResource == nullptr ||
            api.nvEncUnregisterResource == nullptr)
        {
            return false;
        }

        // The conversion pipeline comes before the surfaces it will write to.
        // Creating a shader object touches no surface and issues no GPU work,
        // so a refusal here leaves nothing else to give back.
        if (!TryCreateConversionShaders())
        {
            return false;
        }

        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            // The fixed NV12 destination. Its one bind flag is what the plane
            // shader needs to write Y and UV; the views themselves belong to
            // the conversion unit, not here.
            D3D11_TEXTURE2D_DESC textureDesc = {};
            textureDesc.Width = kInputSurfaceWidth;
            textureDesc.Height = kInputSurfaceHeight;
            textureDesc.MipLevels = 1;
            textureDesc.ArraySize = 1;
            textureDesc.Format = DXGI_FORMAT_NV12;
            textureDesc.SampleDesc.Count = 1;
            textureDesc.SampleDesc.Quality = 0;
            textureDesc.Usage = D3D11_USAGE_DEFAULT;
            textureDesc.BindFlags = D3D11_BIND_RENDER_TARGET;
            textureDesc.CPUAccessFlags = 0;
            textureDesc.MiscFlags = 0;

            ID3D11Texture2D* texture = nullptr;
            const HRESULT hr = _device->CreateTexture2D(&textureDesc, nullptr, &texture);
            if (FAILED(hr) || texture == nullptr)
            {
                _lastHResult = hr;
                RollBackPreparedInputSurfaces(i);
                return false;
            }

            // Owned from the moment it exists, viewed and registered or
            // not.
            _slots[i].inputTexture = texture;

            // The two plane views the conversion will draw through. Nothing
            // beyond the format, the dimension, and the mip slice is set, and
            // no GPU command is issued to make them.
            D3D11_RENDER_TARGET_VIEW_DESC lumaViewDesc = {};
            lumaViewDesc.Format = DXGI_FORMAT_R8_UNORM;
            lumaViewDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            lumaViewDesc.Texture2D.MipSlice = 0;

            ID3D11RenderTargetView* lumaView = nullptr;
            const HRESULT lumaHr =
                _device->CreateRenderTargetView(texture, &lumaViewDesc, &lumaView);
            if (FAILED(lumaHr) || lumaView == nullptr)
            {
                _lastHResult = lumaHr;
                RollBackPreparedInputSurfaces(i + 1);
                return false;
            }

            _slots[i].inputLumaRenderTargetView = lumaView;

            D3D11_RENDER_TARGET_VIEW_DESC chromaViewDesc = {};
            chromaViewDesc.Format = DXGI_FORMAT_R8G8_UNORM;
            chromaViewDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            chromaViewDesc.Texture2D.MipSlice = 0;

            ID3D11RenderTargetView* chromaView = nullptr;
            const HRESULT chromaHr =
                _device->CreateRenderTargetView(texture, &chromaViewDesc, &chromaView);
            if (FAILED(chromaHr) || chromaView == nullptr)
            {
                _lastHResult = chromaHr;
                RollBackPreparedInputSurfaces(i + 1);
                return false;
            }

            _slots[i].inputChromaRenderTargetView = chromaView;

            NV_ENC_REGISTER_RESOURCE registerResource = {};
            registerResource.version = NV_ENC_REGISTER_RESOURCE_VER;
            registerResource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
            registerResource.width = kInputSurfaceWidth;
            registerResource.height = kInputSurfaceHeight;
            registerResource.resourceToRegister = texture;
            registerResource.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
            registerResource.bufferUsage = NV_ENC_INPUT_IMAGE;

            const NVENCSTATUS status = api.nvEncRegisterResource(_encoder, &registerResource);
            if (status != NV_ENC_SUCCESS || registerResource.registeredResource == nullptr)
            {
                _lastNvencStatus = status;
                RollBackPreparedInputSurfaces(i + 1);
                return false;
            }

            _slots[i].registeredInputResource = registerResource.registeredResource;
        }

        return AreInputSurfacesFullyPrepared();
    }

    bool NvencEncoderSession::TryReleaseInputSurfaces()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        if (!AnyInputSurfaceHeld())
        {
            return false;
        }

        // The completion events, the output buffers, and the conversion
        // commands go first: an input surface is not taken out from under
        // resources that were prepared on top of it. Nor is one taken out from
        // under a frame the driver still has.
        if (AnyCompletionEventHeld() || AnyOutputBitstreamBufferHeld() ||
            AnyConversionCommandHeld() || AnyEncodeSampleInFlight())
        {
            return false;
        }

        // One owner, one release - settled before the driver is touched.
        if (_inputSurfacesReleaseAttempted)
        {
            return false;
        }

        _inputSurfacesReleaseAttempted = true;

        for (uint32_t i = kEncodeSampleSlotCount; i > 0; --i)
        {
            if (!TryReleaseInputSurfaceSlot(_slots[i - 1]))
            {
                // Stopped here: this slot and everything before it stay with
                // the session, and so does the conversion pipeline.
                return false;
            }
        }

        // Every slot is gone, so the pipeline that was prepared before them
        // goes now, in the reverse of the order it was created.
        ReleaseConversionShaders();
        return true;
    }

    bool NvencEncoderSession::TryPrepareOutputBitstreamBuffers()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // Buffers belong to an encoder that has been initialized and whose
        // input surfaces are already prepared, and the set is prepared only
        // while nothing is held.
        if (!_encoderInitialized || AnyOutputBitstreamBufferHeld())
        {
            return false;
        }

        if (!AreInputSurfacesFullyPrepared())
        {
            return false;
        }

        // The conversion commands come before the buffers too. Checked before
        // this preparation's one attempt is spent.
        if (!AreConversionCommandsPrepared())
        {
            return false;
        }

        // One owner, one preparation - settled before anything is created.
        if (_outputBitstreamBuffersPrepareAttempted)
        {
            return false;
        }

        _outputBitstreamBuffersPrepareAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        // The way to destroy is confirmed before anything is created, so a
        // buffer is never left with no way to give it back.
        if (api.nvEncCreateBitstreamBuffer == nullptr ||
            api.nvEncDestroyBitstreamBuffer == nullptr)
        {
            return false;
        }

        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            // The ordinary system-memory output buffer: the deprecated size
            // and heap fields are left at zero rather than turned into
            // settings of this project's own.
            NV_ENC_CREATE_BITSTREAM_BUFFER createBitstreamBuffer = {};
            createBitstreamBuffer.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;

            const NVENCSTATUS status =
                api.nvEncCreateBitstreamBuffer(_encoder, &createBitstreamBuffer);
            if (status != NV_ENC_SUCCESS ||
                createBitstreamBuffer.bitstreamBuffer == nullptr)
            {
                _lastNvencStatus = status;
                RollBackPreparedOutputBitstreamBuffers(i);
                return false;
            }

            // Held from the moment it exists.
            _slots[i].outputBitstreamBuffer = createBitstreamBuffer.bitstreamBuffer;
        }

        return true;
    }

    bool NvencEncoderSession::TryReleaseOutputBitstreamBuffers()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        if (!AnyOutputBitstreamBufferHeld())
        {
            return false;
        }

        // The completion events go first: a slot's event is not left
        // registered against an encoder whose output buffers are already gone,
        // and neither is a frame the driver will still write into one.
        if (AnyCompletionEventHeld() || AnyEncodeSampleInFlight())
        {
            return false;
        }

        // One owner, one release - settled before the driver is touched.
        if (_outputBitstreamBuffersReleaseAttempted)
        {
            return false;
        }

        _outputBitstreamBuffersReleaseAttempted = true;

        for (uint32_t i = kEncodeSampleSlotCount; i > 0; --i)
        {
            if (!TryDestroyOutputBitstreamBuffer(_slots[i - 1]))
            {
                // Stopped here: this buffer and everything before it stay with
                // the session.
                return false;
            }
        }

        return true;
    }

    bool NvencEncoderSession::TryPrepareCompletionEvents()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        // Events belong to an encoder that has been initialized, and the set
        // is prepared only while nothing is held.
        if (!_encoderInitialized || AnyCompletionEventHeld())
        {
            return false;
        }

        // One owner, one preparation - settled before anything is created or
        // registered.
        if (_completionEventsPrepareAttempted)
        {
            return false;
        }

        _completionEventsPrepareAttempted = true;

        const NV_ENCODE_API_FUNCTION_LIST& api = *_functionList;

        // The way to unregister is confirmed before anything is registered, so
        // a registered event is never left with no way to take it back.
        if (api.nvEncRegisterAsyncEvent == nullptr ||
            api.nvEncUnregisterAsyncEvent == nullptr)
        {
            return false;
        }

        for (uint32_t i = 0; i < kEncodeSampleSlotCount; ++i)
        {
            // Unnamed, auto-reset, initially non-signalled.
            HANDLE completionEvent = ::CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (completionEvent == nullptr)
            {
                _lastWin32Error = ::GetLastError();
                RollBackPreparedCompletionEvents(i);
                return false;
            }

            // Owned from the moment it exists, registered or not.
            _slots[i].completionEvent = completionEvent;

            NV_ENC_EVENT_PARAMS params = {};
            params.version = NV_ENC_EVENT_PARAMS_VER;
            params.completionEvent = completionEvent;

            const NVENCSTATUS status = api.nvEncRegisterAsyncEvent(_encoder, &params);
            if (status != NV_ENC_SUCCESS)
            {
                _lastNvencStatus = status;
                RollBackPreparedCompletionEvents(i + 1);
                return false;
            }

            _slots[i].completionEventRegistered = true;
        }

        return true;
    }

    /// Unwinds the slots this preparation took, in reverse. A step that itself
    /// fails stops the unwinding: that slot and the ones before it stay held
    /// rather than being released on the assumption that they are fine.
    void NvencEncoderSession::RollBackPreparedCompletionEvents(uint32_t count)
    {
        for (uint32_t i = count; i > 0; --i)
        {
            if (!TryReleaseCompletionEventSlot(_slots[i - 1]))
            {
                return;
            }
        }
    }

    bool NvencEncoderSession::TryReleaseCompletionEvents()
    {
        if (_encoder == nullptr || _closeAttempted || _functionList == nullptr)
        {
            return false;
        }

        if (!AnyCompletionEventHeld())
        {
            return false;
        }

        // A frame the driver still has - a mapped input, an accepted picture,
        // a locked bitstream - is not torn down from underneath. Checked
        // before this release's one attempt is spent.
        if (AnyEncodeSampleInFlight())
        {
            return false;
        }

        // One owner, one release - settled before the driver is touched.
        if (_completionEventsReleaseAttempted)
        {
            return false;
        }

        _completionEventsReleaseAttempted = true;

        for (uint32_t i = kEncodeSampleSlotCount; i > 0; --i)
        {
            if (!TryReleaseCompletionEventSlot(_slots[i - 1]))
            {
                // Stopped here: this slot and everything before it stay with
                // the session.
                return false;
            }
        }

        return true;
    }

    NvencEncoderSessionCloseStatus NvencEncoderSession::Close()
    {
        // An encoder is not destroyed while this session still holds any
        // completion-event handle or registration, output bitstream buffer,
        // input surface, conversion shader, conversion command, or source
        // surface binding - prepared, half-prepared, half-released, or still in
        // flight. A session left holding only shaders is holding
        // something, and is refused here as well. Refused before the close
        // attempt is spent, so the caller can still close once everything is
        // gone.
        if (AnyCompletionEventHeld() || AnyOutputBitstreamBufferHeld() ||
            AnyConversionCommandHeld() || AnyConversionCommandBusy() ||
            AnyInputSurfaceHeld() || AnySourceSurfaceHeld() ||
            AnyEncodeSampleInFlight())
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
