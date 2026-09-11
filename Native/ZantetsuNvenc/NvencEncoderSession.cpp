#include "NvencEncoderSession.h"

namespace zantetsu
{
    NvencEncoderSession::~NvencEncoderSession()
    {
        if (_encoder != nullptr)
        {
            // A session that was never closed, or whose destroy the driver
            // refused, is left exactly as it is: destroying it a second time
            // silently would be a guess, and releasing the device and the
            // module underneath a live encoder would be a worse one.
            return;
        }

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

    NvencEncoderSessionCloseStatus NvencEncoderSession::Close()
    {
        if (_encoder == nullptr || _destroyEncoder == nullptr)
        {
            return NvencEncoderSessionCloseStatus::Failed;
        }

        const NVENCSTATUS status = _destroyEncoder(_encoder);
        if (status != NV_ENC_SUCCESS)
        {
            // Nothing is released on a refused destroy, and nothing is retried
            // here: the encoder handle, the device reference, the function
            // table, and the module all stay exactly where they were.
            _lastNvencStatus = status;
            return NvencEncoderSessionCloseStatus::Failed;
        }

        // Cleared only once the driver has accepted the destroy.
        _encoder = nullptr;

        ReleaseDeviceAndDriver();
        return NvencEncoderSessionCloseStatus::Closed;
    }
}
