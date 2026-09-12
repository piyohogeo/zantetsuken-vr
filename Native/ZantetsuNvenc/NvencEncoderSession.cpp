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
        // surface, a conversion shader, or a source surface binding is a
        // contract violation, not a state
        // this handles: the caller releases and closes first, and an owner
        // whose release or close was refused is kept. Nothing is unregistered,
        // closed, released, or destroyed implicitly here.
        assert(!AnyCompletionEventHeld());
        assert(!AnyOutputBitstreamBufferHeld());
        assert(!AnyInputSurfaceHeld());
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
            ID3D11Texture2D* texture = static_cast<ID3D11Texture2D*>(textures[i]);
            if (texture == nullptr)
            {
                RollBackBoundSourceSurfaces(i);
                return false;
            }

            // A descriptor that is not the accepted one is not a D3D11
            // failure, so no HRESULT is invented for it.
            if (!IsAcceptedSourceTexture(texture))
            {
                RollBackBoundSourceSurfaces(i);
                return false;
            }

            // This session's own reference, taken before anything is made from
            // it and owned from that moment.
            texture->AddRef();
            _sourceSlots[i].texture = texture;

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

        // The completion events and the output buffers go first: an input
        // surface is not taken out from under resources that were prepared on
        // top of it.
        if (AnyCompletionEventHeld() || AnyOutputBitstreamBufferHeld())
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
        // registered against an encoder whose output buffers are already gone.
        if (AnyCompletionEventHeld())
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
        // input surface, conversion shader, or source surface binding -
        // prepared, half-prepared, or half-released. A session left holding only shaders is holding
        // something, and is refused here as well. Refused before the close
        // attempt is spent, so the caller can still close once everything is
        // gone.
        if (AnyCompletionEventHeld() || AnyOutputBitstreamBufferHeld() ||
            AnyInputSurfaceHeld() || AnySourceSurfaceHeld())
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
