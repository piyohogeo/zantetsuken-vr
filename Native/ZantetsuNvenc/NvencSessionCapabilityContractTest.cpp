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
#include <new>

#include <d3d11.h>

#include "D3D11DeviceBinding.h"
#include "NvencEncoderSession.h"

namespace zantetsu
{
    /// The test's own scratch storage. Production never allocates for an
    /// access unit here; this is the caller-provided destination the copy
    /// writes into.
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

namespace
{
    /// The fixed access unit storage this profile uses.
    constexpr uint32_t kAccessUnitCapacity = 16u * 1024u * 1024u;

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

    /// The source texture a Player's capture render target really is, as read
    /// from one with GetDesc: typeless RGBA8, 1280x720, one mip, one slice, no
    /// multisampling, bound as both a render target and a shader resource. The
    /// storage carries no colour interpretation; the session's sRGB view is
    /// what supplies it.
    ID3D11Texture2D* CreateSourceTexture(ID3D11Device* device)
    {
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = 1280;
        desc.Height = 720;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = 0;

        ID3D11Texture2D* texture = nullptr;
        const HRESULT hr = device->CreateTexture2D(&desc, nullptr, &texture);
        if (FAILED(hr) || texture == nullptr)
        {
            std::printf(
                "FAILED: could not create a source texture (hr 0x%08lX)\n",
                static_cast<unsigned long>(hr));
            ++g_failures;
            return nullptr;
        }

        return texture;
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

    Check(
        session.TryInitializeEncoder(request),
        "the encoder initializes with the fixed request");
    Check(session.IsOpen(), "the initialized session is still open");

    // One owner, one initialization.
    Check(
        !session.TryInitializeEncoder(request),
        "a second initialization on the same session is refused");
    Check(session.IsOpen(), "the refused second initialization left the session open");

    // The NV12 input surfaces wait for the sources, and asking too early
    // must not spend the one preparation attempt.
    Check(
        !session.TryPrepareInputSurfaces(),
        "the NV12 input surfaces are not prepared before the sources are bound");

    // Output buffers wait for the input surfaces.
    Check(
        !session.TryPrepareOutputBitstreamBuffers(),
        "output buffers are not prepared before the input surfaces");

    // The fixed set of source RGBA surfaces, created here the way a Player's
    // capture render targets are and owned by this test.
    ID3D11Texture2D* sourceTextures[zantetsu::kSourceSurfaceSlotCount] = {};
    void* sourcePointers[zantetsu::kSourceSurfaceSlotCount] = {};
    bool sourcesCreated = true;
    for (uint32_t i = 0; i < zantetsu::kSourceSurfaceSlotCount; ++i)
    {
        sourceTextures[i] = CreateSourceTexture(device);
        if (sourceTextures[i] == nullptr)
        {
            sourcesCreated = false;
            break;
        }

        // Handed over as the resource the graphics contract promises, not as
        // the 2D interface: the session asks for that itself.
        sourcePointers[i] = static_cast<ID3D11Resource*>(sourceTextures[i]);
    }

    if (!sourcesCreated)
    {
        for (uint32_t i = zantetsu::kSourceSurfaceSlotCount; i > 0; --i)
        {
            if (sourceTextures[i - 1] != nullptr)
            {
                sourceTextures[i - 1]->Release();
            }
        }

        binding.Clear();
        return 1;
    }

    Check(
        session.TryBindSourceSurfaces(
            sourcePointers, zantetsu::kSourceSurfaceSlotCount),
        "the fixed set of source RGBA surfaces binds: a reference and a view each");
    Check(
        !session.TryBindSourceSurfaces(
            sourcePointers, zantetsu::kSourceSurfaceSlotCount),
        "a second source surface binding is refused");
    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with bound source surfaces refuses to close");
    Check(session.IsOpen(), "the refused close left the session open");

    // The fixed conversion pipeline and the fixed set of NV12 input surfaces,
    // owned by the session. Success here is the whole set: three shader
    // objects, and every slot's texture, plane views, and registration.
    Check(
        session.TryPrepareInputSurfaces(),
        "the fixed pipeline and NV12 input surface set prepare: three shaders, then textures, both plane views, and registrations");
    Check(
        !session.TryPrepareInputSurfaces(),
        "a second input surface preparation is refused");
    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with a prepared pipeline and input surfaces refuses to close");
    Check(session.IsOpen(), "the refused close left the session open");

    // The fixed set of output bitstream buffers, owned by the session.
    // The output buffers wait for the conversion commands too.
    Check(
        !session.TryPrepareOutputBitstreamBuffers(),
        "output buffers are not prepared before the conversion commands");

    Check(
        session.TryPrepareConversionCommands(),
        "the fixed set of conversion command slots prepares: a fence and an event each");
    Check(
        !session.TryPrepareConversionCommands(),
        "a second conversion command preparation is refused");
    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with prepared conversion commands refuses to close");
    Check(session.IsOpen(), "the refused close left the session open");

    // The callback's own result and the last Win32 error, written out by
    // every collection below.
    HRESULT callbackHResult = S_OK;
    DWORD collectWin32Error = 0;

    // ---- one command per slot, all eight of them ----
    {
        bool allArmed = true;
        bool allRan = true;
        bool allCollected = true;
        bool allIdleAfterDuplicate = true;
        for (uint32_t i = 0; i < zantetsu::kConversionCommandSlotCount; ++i)
        {
            void* eventData = nullptr;
            if (!session.TryArmConversionCommand(i, i, i, 1, &eventData) ||
                eventData == nullptr)
            {
                allArmed = false;
                break;
            }

            zantetsu::RunConversionCommandFromEventData(eventData);

            // A second callback for the command that is still running must
            // draw nothing.
            zantetsu::RunConversionCommandFromEventData(eventData);

            if (zantetsu::NvencConversionCollectStatus::Completed !=
                session.TryCollectConversionCommand(
                    i, 1, 5000, &callbackHResult, &collectWin32Error))
            {
                allCollected = false;
                break;
            }

            // And one that arrives after the completion was collected must
            // leave the slot idle: a slot that took it would no longer arm.
            zantetsu::RunConversionCommandFromEventData(eventData);

            void* rearmed = nullptr;
            if (!session.TryArmConversionCommand(i, i, i, 2, &rearmed) ||
                rearmed == nullptr)
            {
                allIdleAfterDuplicate = false;
                break;
            }

            if (!session.TryCancelArmedConversionCommand(i, 2))
            {
                allIdleAfterDuplicate = false;
                break;
            }
        }

        Check(allArmed, "every one of the eight conversion command slots arms");
        Check(allRan, "every armed command ran on the calling thread");
        Check(allCollected, "every one of the eight commands completes and collects");
        Check(
            SUCCEEDED(callbackHResult),
            "a collected command reports its own callback's result, which succeeded");
        Check(
            allIdleAfterDuplicate,
            "a callback that arrives after collection leaves its slot idle");
    }

    // ---- one slot, sixteen reuses, distinguished only by generation ----
    {
        bool reuseHeld = true;
        bool staleRefused = true;
        for (uint64_t generation = 3; generation <= 18; ++generation)
        {
            void* eventData = nullptr;
            if (!session.TryArmConversionCommand(
                    0, 0, 0, generation, &eventData) || eventData == nullptr)
            {
                reuseHeld = false;
                break;
            }

            // The generation before this one belongs to a command that is
            // already collected: waiting on it must be refused, not satisfied
            // by an older signal.
            if (zantetsu::NvencConversionCollectStatus::Completed ==
                session.TryCollectConversionCommand(
                    0, generation - 1, 0, &callbackHResult, &collectWin32Error))
            {
                staleRefused = false;
                break;
            }

            zantetsu::RunConversionCommandFromEventData(eventData);

            if (zantetsu::NvencConversionCollectStatus::Completed !=
                session.TryCollectConversionCommand(
                    0, generation, 5000, &callbackHResult, &collectWin32Error))
            {
                reuseHeld = false;
                break;
            }

            // Collected once, and only once.
            if (zantetsu::NvencConversionCollectStatus::Completed ==
                session.TryCollectConversionCommand(
                    0, generation, 0, &callbackHResult, &collectWin32Error))
            {
                reuseHeld = false;
                break;
            }
        }

        Check(reuseHeld, "one sync slot completes sixteen reuses, each collected once");
        Check(staleRefused, "an older generation is refused rather than completed");
    }

    // ---- a collection that gets past the callback and then times out on
    //      the GPU can be repeated for the same generation ----
    {
        // Every slot is loaded first, so the last one's fence has the most
        // chance of still being outstanding when it is collected with no
        // patience at all.
        bool allIssued = true;
        for (uint32_t i = 0; i < zantetsu::kConversionCommandSlotCount; ++i)
        {
            void* eventData = nullptr;
            if (!session.TryArmConversionCommand(i, i, i, 30, &eventData) ||
                eventData == nullptr)
            {
                allIssued = false;
                break;
            }

            zantetsu::RunConversionCommandFromEventData(eventData);
        }

        Check(allIssued, "every slot takes a command before any is collected");

        bool everyImpatientAttemptRecovers = true;
        bool everyNextGenerationWaits = true;
        bool everyTimeoutIsSilent = true;
        bool anyImpatientAttemptTimedOut = false;
        for (uint32_t i = 0; i < zantetsu::kConversionCommandSlotCount; ++i)
        {
            // The callback has already published, so this one waits for the
            // GPU alone - and with no patience at all it may run out of time.
            const bool impatient = zantetsu::NvencConversionCollectStatus::Completed == session.TryCollectConversionCommand(
                i, 30, 0, &callbackHResult, &collectWin32Error);
            if (!impatient)
            {
                anyImpatientAttemptTimedOut = true;

                // A wait that only ran out of time says nothing about the
                // thread's last error.
                if (collectWin32Error != 0)
                {
                    everyTimeoutIsSilent = false;
                    break;
                }

                // The published completion was not consumed, so the very same
                // generation is still collectable.
                if (zantetsu::NvencConversionCollectStatus::Completed != session.TryCollectConversionCommand(
                        i, 30, 5000, &callbackHResult, &collectWin32Error))
                {
                    everyImpatientAttemptRecovers = false;
                    break;
                }
            }

        }

        // The generation after each one starts from nothing: neither the fence
        // event that command was waited on nor the completion its callback
        // published may carry over, so a collection before the next callback
        // has run must simply run out of time.
        for (uint32_t i = 0;
            i < zantetsu::kConversionCommandSlotCount && everyNextGenerationWaits;
            ++i)
        {
            void* nextData = nullptr;
            if (!session.TryArmConversionCommand(i, i, i, 31, &nextData) ||
                nextData == nullptr)
            {
                everyNextGenerationWaits = false;
                break;
            }

            if (zantetsu::NvencConversionCollectStatus::Completed == session.TryCollectConversionCommand(
                    i, 31, 50, &callbackHResult, &collectWin32Error))
            {
                everyNextGenerationWaits = false;
                break;
            }

            if (collectWin32Error != 0)
            {
                everyTimeoutIsSilent = false;
                break;
            }

            zantetsu::RunConversionCommandFromEventData(nextData);

            if (zantetsu::NvencConversionCollectStatus::Completed != session.TryCollectConversionCommand(
                    i, 31, 5000, &callbackHResult, &collectWin32Error))
            {
                everyNextGenerationWaits = false;
                break;
            }
        }

        Check(
            everyImpatientAttemptRecovers,
            "an impatient collection that times out leaves the same generation collectable");
        Check(
            everyNextGenerationWaits,
            "no leftover fence or callback completion carries into the next generation");
        Check(
            everyTimeoutIsSilent,
            "a collection that only ran out of time reports no Win32 error");
        std::printf(
            "  observed: an impatient collection timed out on the GPU: %s\n",
            anyImpatientAttemptTimedOut ? "yes" : "no");
    }

    // ---- what a collection is told apart from ----
    {
        void* eventData = nullptr;
        Check(
            session.TryArmConversionCommand(5, 5, 5, 40, &eventData) &&
                eventData != nullptr,
            "a slot arms for the three-way collection check");

        // Armed, but its callback has not run: not yet, and nothing about the
        // command changed.
        Check(
            session.TryCollectConversionCommand(
                5, 40, 0, &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Pending,
            "a command whose callback has not run is pending");
        Check(
            callbackHResult == S_OK && collectWin32Error == 0,
            "a pending collection invents no HRESULT and no Win32 error");

        zantetsu::RunConversionCommandFromEventData(eventData);

        // The same generation, given time, completes.
        Check(
            session.TryCollectConversionCommand(
                5, 40, 5000, &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Completed,
            "the same generation completes once its callback has run and the GPU caught up");

        // Asking again, or about a generation this slot does not hold, is a
        // caller asking about work that is not there - never "not yet".
        Check(
            session.TryCollectConversionCommand(
                5, 40, 0, &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Failed,
            "collecting a completed command a second time fails rather than pends");
        Check(
            session.TryCollectConversionCommand(
                5, 39, 0, &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Failed,
            "collecting a generation this slot does not hold fails rather than pends");
        Check(
            session.TryCollectConversionCommand(
                zantetsu::kConversionCommandSlotCount, 40, 0,
                &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Failed,
            "collecting a slot index out of range fails rather than pends");
    }

    // ---- what an arming refuses, before it changes anything ----
    {
        void* eventData = nullptr;
        Check(
            !session.TryArmConversionCommand(
                zantetsu::kConversionCommandSlotCount, 0, 0, 100, &eventData),
            "a sync slot index out of range is refused");
        Check(
            !session.TryArmConversionCommand(
                0, zantetsu::kSourceSurfaceSlotCount, 0, 100, &eventData),
            "a source slot index out of range is refused");
        Check(
            !session.TryArmConversionCommand(
                0, 0, zantetsu::kEncodeSampleSlotCount, 100, &eventData),
            "an encode sample slot index out of range is refused");
        Check(
            !session.TryArmConversionCommand(0, 0, 0, 31, &eventData),
            "a generation that is not newer than the slot's last is refused");
    }

    // ---- an armed command that was never issued can be taken back ----
    {
        void* eventData = nullptr;
        Check(
            session.TryArmConversionCommand(1, 2, 3, 100, &eventData) &&
                eventData != nullptr,
            "a command arms with its own source, sample, and sync slots");
        Check(
            !session.TryReleaseConversionCommands(),
            "the conversion commands are not released while one is armed");
        Check(
            session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
            "a session with an armed command refuses to close");
        Check(
            session.TryCancelArmedConversionCommand(1, 100),
            "an armed command whose event was never issued is taken back");
        Check(
            !session.TryCancelArmedConversionCommand(1, 100),
            "a command that is no longer armed is not taken back twice");

        // Once it has run, it is no longer cancellable: it will signal.
        void* runningData = nullptr;
        Check(
            session.TryArmConversionCommand(1, 2, 3, 101, &runningData) &&
                runningData != nullptr,
            "the taken-back slot arms again with a newer generation");
        zantetsu::RunConversionCommandFromEventData(runningData);
        Check(
            !session.TryCancelArmedConversionCommand(1, 101),
            "a command whose callback has started is not taken back");
        Check(
            !session.TryReleaseConversionCommands(),
            "the conversion commands are not released while one is uncollected");
        Check(
            session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
            "a session with an uncollected command refuses to close");
        Check(
            session.TryCollectConversionCommand(
                1, 101, 5000, &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Completed,
            "the started command completes and collects");
    }

    Check(
        session.TryPrepareOutputBitstreamBuffers(),
        "the fixed output bitstream buffer set prepares on the initialized encoder");
    Check(
        !session.TryPrepareOutputBitstreamBuffers(),
        "a second output buffer preparation is refused");
    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with prepared output buffers refuses to close");
    Check(session.IsOpen(), "the refused close left the session open");

    // The fixed set of completion events, owned by the session.
    Check(
        session.TryPrepareCompletionEvents(),
        "the fixed completion event set prepares on the initialized encoder");

    // The events go before the buffers, and the buffers before the surfaces.
    // ---- one whole frame: convert, map, submit, wait, lock, copy, unlock,
    //      unmap ----
    {
        const uint32_t frameSlot = 0;

        // The picture is only meaningful once its conversion has actually
        // written the surface, so that comes first and is collected.
        void* conversionData = nullptr;
        Check(
            session.TryArmConversionCommand(
                frameSlot, frameSlot, frameSlot, 60, &conversionData) &&
                conversionData != nullptr,
            "the frame's conversion arms");
        zantetsu::RunConversionCommandFromEventData(conversionData);
        Check(
            session.TryCollectConversionCommand(
                frameSlot, 60, 5000, &callbackHResult, &collectWin32Error) ==
                zantetsu::NvencConversionCollectStatus::Completed,
            "the frame's conversion completes before anything is encoded");

        NVENCSTATUS submitStatus = NV_ENC_SUCCESS;
        Check(
            session.TrySubmitEncodePicture(frameSlot, 1, &submitStatus) ==
                zantetsu::NvencEncodeSubmitStatus::Submitted,
            "the converted surface maps and one picture submits");
        if (submitStatus != NV_ENC_SUCCESS)
        {
            std::printf(
                "  observed: submit reported NVENCSTATUS %d\n",
                static_cast<int>(submitStatus));
        }

        // One picture per sample generation, and only ever forward.
        NVENCSTATUS secondStatus = NV_ENC_SUCCESS;
        Check(
            session.TrySubmitEncodePicture(frameSlot, 1, &secondStatus) !=
                zantetsu::NvencEncodeSubmitStatus::Submitted,
            "the same sample generation does not submit twice");

        // Nothing the frame is built on may be taken away while the driver
        // still has it.
        Check(
            !session.TryReleaseCompletionEvents(),
            "the completion events are not released while a picture is in flight");
        Check(
            session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
            "a session with a picture in flight refuses to close");

        zantetsu::ScopedArray<uint8_t> accessUnit(kAccessUnitCapacity);
        Check(accessUnit.Get() != nullptr, "the test's access unit storage exists");

        uint32_t validLength = 0;
        NVENCSTATUS outputStatus = NV_ENC_SUCCESS;
        DWORD outputWin32Error = 0;
        const zantetsu::NvencOutputCollectStatus collected =
            session.TryCopyCompletedOutput(
                frameSlot, 1, accessUnit.Get(), kAccessUnitCapacity,
                &validLength, &outputStatus, &outputWin32Error);

        Check(
            collected == zantetsu::NvencOutputCollectStatus::Copied,
            "the completion event is awaited and the access unit copies out");
        if (collected != zantetsu::NvencOutputCollectStatus::Copied)
        {
            std::printf(
                "  observed: output reported NVENCSTATUS %d, win32 error %lu\n",
                static_cast<int>(outputStatus),
                static_cast<unsigned long>(outputWin32Error));
        }

        Check(
            validLength >= 1 && validLength <= kAccessUnitCapacity,
            "the copied access unit has a length inside the storage");

        // Annex-B and nothing more: no parser, no NAL classification.
        const bool startCode = validLength >= 4 &&
            accessUnit.Get()[0] == 0x00 && accessUnit.Get()[1] == 0x00 &&
            (accessUnit.Get()[2] == 0x01 ||
                (accessUnit.Get()[2] == 0x00 && accessUnit.Get()[3] == 0x01));
        Check(startCode, "the copied access unit begins with an Annex-B start code");
        std::printf("  observed: access unit of %u bytes\n", validLength);

        // Collected: the slot is usable again with a newer generation.
        NVENCSTATUS reuseStatus = NV_ENC_SUCCESS;
        Check(
            session.TrySubmitEncodePicture(frameSlot, 2, &reuseStatus) ==
                zantetsu::NvencEncodeSubmitStatus::Submitted,
            "the same slot takes another picture under a newer generation");

        uint32_t reuseLength = 0;
        Check(
            session.TryCopyCompletedOutput(
                frameSlot, 2, accessUnit.Get(), kAccessUnitCapacity,
                &reuseLength, &outputStatus, &outputWin32Error) ==
                zantetsu::NvencOutputCollectStatus::Copied,
            "the reused slot's picture collects as well");
    }

    Check(
        !session.TryReleaseOutputBitstreamBuffers(),
        "the output buffers are not released while completion events are held");
    Check(
        !session.TryReleaseInputSurfaces(),
        "the input surfaces are not released while the other resources are held");

    // A prepared set holds the session open, and refusing the close does not
    // spend the close attempt.
    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with prepared completion events refuses to close");
    Check(session.IsOpen(), "the refused close left the session open");

    // One owner, one preparation.
    Check(
        !session.TryPrepareCompletionEvents(),
        "a second preparation on the same session is refused");

    Check(
        session.TryReleaseCompletionEvents(),
        "the completion event set unregisters and closes");

    // One owner, one release.
    Check(
        !session.TryReleaseCompletionEvents(),
        "a second release on the same session is refused");

    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with prepared output buffers still refuses to close");

    Check(
        session.TryReleaseOutputBitstreamBuffers(),
        "the output bitstream buffer set is destroyed once the events are gone");

    // One owner, one release.
    Check(
        !session.TryReleaseOutputBitstreamBuffers(),
        "a second output buffer release on the same session is refused");

    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with a prepared pipeline and input surfaces still refuses to close");

    Check(
        !session.TryReleaseInputSurfaces(),
        "the input surfaces are not released while the conversion commands are held");
    Check(
        !session.TryReleaseSourceSurfaces(),
        "the source surfaces are not released while the conversion commands are held");

    Check(
        session.TryReleaseConversionCommands(),
        "the conversion command set releases once the buffers and events are gone");
    Check(
        !session.TryReleaseConversionCommands(),
        "a second conversion command release on the same session is refused");

    Check(
        !session.TryReleaseSourceSurfaces(),
        "the source surfaces are not released while the input surfaces are held");

    Check(
        session.TryReleaseInputSurfaces(),
        "the input surface set and then the three shaders release once the rest is gone");

    // One owner, one release.
    Check(
        !session.TryReleaseInputSurfaces(),
        "a second input surface release on the same session is refused");

    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Failed,
        "a session with only its source surfaces left still refuses to close");

    // The refusal above spent nothing, so the release still works.
    Check(
        session.TryReleaseSourceSurfaces(),
        "the source surface set releases its views and references once the rest is gone");
    Check(
        !session.TryReleaseSourceSurfaces(),
        "a second source surface release on the same session is refused");

    Check(
        session.Close() == zantetsu::NvencEncoderSessionCloseStatus::Closed,
        "the session closes once its slot resources, pipeline, and sources are gone");
    Check(!session.IsOpen(), "the closed session holds no encoder");

    // The test's own references, given back in the reverse of the order they
    // were taken. The session released its own with the binding.
    for (uint32_t i = zantetsu::kSourceSurfaceSlotCount; i > 0; --i)
    {
        sourceTextures[i - 1]->Release();
    }

    binding.Clear();

    if (g_failures != 0)
    {
        std::printf("%d check(s) failed\n", g_failures);
        return 1;
    }

    std::printf("NVENC session capability observation contract: PASSED\n");
    return 0;
}
