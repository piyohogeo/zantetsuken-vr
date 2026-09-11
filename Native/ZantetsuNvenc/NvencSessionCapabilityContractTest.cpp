// Contract test for the retained session's capability observation.
//
// It opens a real D3D11 device on the default hardware adapter, publishes it
// through the same binding the plugin uses, opens one encoder session on it,
// observes that session's capabilities once, initializes its encoder with the
// fixed request once, and closes the session. It needs
// an NVIDIA driver and an encode-capable default adapter, so it is run only
// when asked for.
//
// The observed values themselves are not pinned: what is checked is that the
// enumeration and the capability queries complete, that a second observation is
// refused, and that the session still closes afterwards. If the default adapter
// simply cannot encode, that is reported and not treated as a failure - no
// adapter is enumerated or selected to find one that can.
//
// Exit code 0 means every check held. Any failure prints what it was and
// returns non-zero.

#include <cstdio>

#include <d3d11.h>

#include "D3D11DeviceBinding.h"
#include "NvencEncoderSession.h"

namespace
{
    int g_failures = 0;

    void Check(bool condition, const char* what)
    {
        if (!condition)
        {
            std::printf("FAILED: %s\n", what);
            ++g_failures;
            return;
        }

        std::printf("  ok: %s\n", what);
    }

    ID3D11Device* CreateHardwareDevice()
    {
        ID3D11Device* device = nullptr;
        D3D_FEATURE_LEVEL featureLevel = D3D_FEATURE_LEVEL_11_0;

        const HRESULT hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            0,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &device,
            &featureLevel,
            nullptr);

        if (FAILED(hr) || device == nullptr)
        {
            std::printf(
                "FAILED: could not create a hardware D3D11 device (hr 0x%08lX)\n",
                static_cast<unsigned long>(hr));
            ++g_failures;
            return nullptr;
        }

        return device;
    }
}

int main()
{
    std::printf("NVENC session capability observation contract\n");

    ID3D11Device* device = CreateHardwareDevice();
    if (device == nullptr)
    {
        return 1;
    }

    zantetsu::D3D11DeviceBinding binding;
    binding.PublishBorrowed(device);
    device->Release();

    zantetsu::NvencEncoderSession session;
    const zantetsu::NvencEncoderSessionOpenStatus openStatus = session.Open(binding);

    if (openStatus == zantetsu::NvencEncoderSessionOpenStatus::Unsupported)
    {
        // Nothing is enumerated or chosen to find an adapter that could.
        std::printf(
            "  skipped: the default hardware adapter cannot open an encoder session\n");
        binding.Clear();
        return 0;
    }

    if (openStatus != zantetsu::NvencEncoderSessionOpenStatus::Opened)
    {
        std::printf("FAILED: the encoder session could not be opened (NVENCSTATUS %d)\n",
            static_cast<int>(session.LastNvencStatus()));
        binding.Clear();
        return 1;
    }

    Check(session.IsOpen(), "the session is open");

    zantetsu::NvencEncoderCapabilityObservation observation = {};
    Check(
        session.TryObserveCapabilities(&observation),
        "the encoder capabilities were observed");

    std::printf(
        "  observed: h264 %d, high %d, nv12 %d, async %d, maximum %dx%d\n",
        observation.supportsH264Encode ? 1 : 0,
        observation.supportsH264HighProfile ? 1 : 0,
        observation.supportsNv12Input ? 1 : 0,
        observation.supportsAsyncEncode ? 1 : 0,
        observation.maximumEncodeWidth,
        observation.maximumEncodeHeight);

    // An encoder that reports H.264 must have answered the rest of the
    // queries; one that does not is a completed observation of nothing.
    if (observation.supportsH264Encode)
    {
        Check(
            observation.maximumEncodeWidth > 0 && observation.maximumEncodeHeight > 0,
            "an H.264 encoder reported usable maximum dimensions");
    }
    else
    {
        Check(
            observation.maximumEncodeWidth == 0 && observation.maximumEncodeHeight == 0,
            "an encoder without H.264 reported no dimensions");
    }

    // One owner, one observation.
    zantetsu::NvencEncoderCapabilityObservation again = {};
    Check(
        !session.TryObserveCapabilities(&again),
        "a second observation on the same session is refused");
    Check(session.IsOpen(), "the refused second observation left the session open");

    // The fixed request, mapped the way the plugin's export maps it.
    zantetsu::NvencEncoderInitializationRequest request = {};
    request.encodeGuid = NV_ENC_CODEC_H264_GUID;
    request.presetGuid = NV_ENC_PRESET_P1_GUID;
    request.profileGuid = NV_ENC_H264_PROFILE_HIGH_GUID;
    request.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;
    request.rateControlMode = NV_ENC_PARAMS_RC_CONSTQP;
    request.encodeWidth = 1280;
    request.encodeHeight = 720;
    request.maximumEncodeWidth = 1280;
    request.maximumEncodeHeight = 720;
    request.frameRateNumerator = 30;
    request.frameRateDenominator = 1;
    request.enablePictureTypeDecision = 0;
    request.gopLength = 1;
    request.idrPeriod = 1;
    request.frameIntervalP = 1;
    request.qpIntra = 28;
    request.qpInterP = 28;
    request.qpInterB = 28;
    request.repeatSequenceAndPictureParameterSets = 1;
    request.outputAccessUnitDelimiter = 0;
    request.disableSequenceAndPictureParameterSets = 0;
    request.chromaFormatIdc = 1;
    request.level = NV_ENC_LEVEL_AUTOSELECT;
    request.progressiveEncoding = 1;
    request.enableEncodeAsync = 1;
    request.enableOutputInVideoMemory = 0;

    Check(!session.IsEncoderInitialized(), "the encoder is not initialized yet");
    Check(
        session.TryInitializeEncoder(request),
        "the encoder initializes with the fixed request");
    Check(session.IsEncoderInitialized(), "the encoder reports itself initialized");
    Check(session.IsOpen(), "the initialized session is still open");

    // One owner, one initialization.
    Check(
        !session.TryInitializeEncoder(request),
        "a second initialization on the same session is refused");
    Check(session.IsOpen(), "the refused second initialization left the session open");

    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Closed,
        "the initialized session closes");
    Check(!session.IsOpen(), "the closed session holds no encoder");

    binding.Clear();

    if (g_failures != 0)
    {
        std::printf("%d check(s) failed\n", g_failures);
        return 1;
    }

    std::printf("NVENC session capability observation contract: PASSED\n");
    return 0;
}
