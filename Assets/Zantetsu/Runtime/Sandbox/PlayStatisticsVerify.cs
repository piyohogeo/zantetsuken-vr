#if ZANTETSU_STATS_VERIFY
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using Zantetsu.Core;
using Zantetsu.Core.Statistics;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The VERIFICATION build of the play statistics (DESIGN 21.18, "検証専用の構成"): compiled under
    /// <c>ZANTETSU_STATS_VERIFY</c> alone, a symbol no product build defines. It answers two questions about the
    /// statistics' cost in a Release Player, and keeps them apart:
    /// <list type="bullet">
    /// <item><b>Direct.</b> A Stopwatch round the service's whole Update (the time API, the conditions, the counters,
    /// the collection), in the mode the launch asks for; and, beside it, an empty interval of two clock reads, since
    /// the measured interval holds a clock read of its own.</item>
    /// <item><b>ON against OFF.</b> The frame's times read by an observation of this build's own, the same in both
    /// modes -- so its cost (a FrameTimingManager capture and read, the tallies) is in both and is NOT a part of what
    /// the difference shows.</item>
    /// </list>
    /// The consent is the launch's argument, kept in memory: the saved consent is neither read nor written. No send
    /// configuration is loaded and no transport is made in this build, whatever the Player was given. The window is the
    /// same in both modes: it opens a fixed time after play begins by the conditions the statistics use (the world
    /// ready, the start-up preparation over, the head tracked), lasts a fixed time, and then the Player quits by
    /// itself. Records are fixed-size tallies and lines at the window's end; nothing is written in a frame.
    /// </summary>
    public sealed class PlayStatisticsVerify : MonoBehaviour
    {
        public const string Prefix = "PLAY STATISTICS VERIFY: ";

        /// <summary><c>-zantetsuStatsVerify on|off</c>: the statistics of this launch. Without it: off.</summary>
        public const string ModeArgument = "-zantetsuStatsVerify";

        /// <summary><c>-zantetsuStatsVerifySeconds N</c>: the window's length (default 30).</summary>
        public const string SecondsArgument = "-zantetsuStatsVerifySeconds";

        /// <summary><c>-zantetsuStatsVerifySettleSeconds N</c>: from where play begins to the window (default 5).</summary>
        public const string SettleArgument = "-zantetsuStatsVerifySettleSeconds";

        private enum Phase { Preparing, Settling, Measuring, Done }

        public static PlayStatisticsVerify Instance { get; private set; }

        private static bool s_read, s_on, s_modeGiven;
        private static double s_seconds = 30.0, s_settle = 5.0;

        private PlayStatisticsService _service;
        private Phase _phase = Phase.Preparing;
        private readonly PlayStartConditions _start = new PlayStartConditions();
        private readonly FrameTimeSampler _sampler = new FrameTimeSampler();
        private CutWorldRoot _world;
        private float _nextLookup;

        // The preparation, apart from the window.
        private int _firstFrame = -1, _preparingFrames;
        private double _firstSeconds;
        private float _preparingMaxIntervalMs;

        // The window.
        private double _settleUntil, _windowBegan;
        private int _windowFrames, _unfocusedFrames, _pausedFrames, _noMain, _noRender, _noGpu;
        private readonly PlayVerifyTally _intervalMs = new PlayVerifyTally(PlayVerifyTally.MillisecondEdges);
        private readonly PlayVerifyTally _mainMs = new PlayVerifyTally(PlayVerifyTally.MillisecondEdges);
        private readonly PlayVerifyTally _renderMs = new PlayVerifyTally(PlayVerifyTally.MillisecondEdges);
        private readonly PlayVerifyTally _gpuMs = new PlayVerifyTally(PlayVerifyTally.MillisecondEdges);
        private readonly PlayVerifyTally _serviceUs = new PlayVerifyTally(PlayVerifyTally.MicrosecondEdges);
        private readonly PlayVerifyTally _emptyUs = new PlayVerifyTally(PlayVerifyTally.MicrosecondEdges);
        private long _serviceBytes, _serviceBytesMax;
        private int _serviceCallsAllocating;
        private int _gcBefore;
        private long _totalBefore, _monoBefore, _threadBefore;
        private ulong _countedBefore;

        // The service's Update outside the window (the first call is long: it is not a frame's steady cost).
        private long _outsideCalls, _outsideMaxTicks;

        // The allocation counters, tried once: what each shows for a known allocation.
        private static byte[][] s_keep;
        private static long s_selfThread, s_selfTotal, s_selfMono;
        private const int SelfArrays = 64, SelfBytes = 1024;

        private static void ReadArguments()
        {
            if (s_read) return;
            s_read = true;
            string[] a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (string.Equals(a[i], ModeArgument, StringComparison.OrdinalIgnoreCase))
                {
                    s_modeGiven = true;
                    s_on = string.Equals(a[i + 1], "on", StringComparison.OrdinalIgnoreCase);
                }
                else if (string.Equals(a[i], SecondsArgument, StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(a[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0.0)
                {
                    s_seconds = seconds;
                }
                else if (string.Equals(a[i], SettleArgument, StringComparison.OrdinalIgnoreCase)
                         && double.TryParse(a[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double settle) && settle >= 0.0)
                {
                    s_settle = settle;
                }
            }
        }

        /// <summary>
        /// The consent of this launch, in memory: agreed where the argument says on, declined otherwise. The saved
        /// consent (PlayerPrefs) is not read, and nothing the service or the menu does to this store is kept.
        /// </summary>
        public static IPlayStatisticsStore StoreForThisLaunch()
        {
            ReadArguments();
            var store = new MemoryPlayStatisticsStore();
            store.Set(PlayStatisticsConsent.DecisionKey, s_on ? "agreed" : "declined");
            return store;
        }

        public static PlayStatisticsVerify Install(PlayStatisticsService service)
        {
            if (Instance != null) return Instance;
            ReadArguments();
            SelfTestAllocationCounters();
            var host = new GameObject("Zantetsu play statistics verification");
            DontDestroyOnLoad(host);
            PlayStatisticsVerify verify = host.AddComponent<PlayStatisticsVerify>();
            verify._service = service;
            Instance = verify;
            Debug.Log(Prefix + "a VERIFICATION build (ZANTETSU_STATS_VERIFY): statistics " + (s_on ? "ON" : "OFF") + " by " + ModeArgument
                + (s_modeGiven ? "" : " (not given: off)") + ", the saved consent neither read nor written; no send configuration loaded, no transport in this build"
                + "; window " + s_seconds.ToString("F1", CultureInfo.InvariantCulture) + " s, " + s_settle.ToString("F1", CultureInfo.InvariantCulture) + " s after play begins"
                + "; development build " + Debug.isDebugBuild + "; Stopwatch " + System.Diagnostics.Stopwatch.Frequency + " ticks/s, high resolution " + System.Diagnostics.Stopwatch.IsHighResolution
                + "; allocation counters for " + SelfArrays + " arrays of " + SelfBytes + " bytes: this thread's +" + s_selfThread + ", GC.GetTotalMemory +" + s_selfTotal + ", mono used +" + s_selfMono);
            return verify;
        }

        private static void SelfTestAllocationCounters()
        {
            long thread = GC.GetAllocatedBytesForCurrentThread(), total = GC.GetTotalMemory(false), mono = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            var keep = new byte[SelfArrays][];
            for (int i = 0; i < SelfArrays; i++) keep[i] = new byte[SelfBytes];
            s_selfThread = GC.GetAllocatedBytesForCurrentThread() - thread;
            s_selfTotal = GC.GetTotalMemory(false) - total;
            s_selfMono = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - mono;
            s_keep = keep;
        }

        // ---- the service's Update, measured from the service itself -----------------------------------------------------

        private static long s_threadBeforeService;

        /// <summary>Before the service's Update, outside its measured interval: this thread's allocated bytes.</summary>
        public static void ServiceUpdateBegins()
        {
            s_threadBeforeService = GC.GetAllocatedBytesForCurrentThread();
        }

        /// <summary>After the service's Update, with the ticks its whole Update took (one clock read inside them).</summary>
        public static void ServiceUpdateEnded(long ticks)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread() - s_threadBeforeService;
            long a = System.Diagnostics.Stopwatch.GetTimestamp();
            long b = System.Diagnostics.Stopwatch.GetTimestamp();
            PlayStatisticsVerify verify = Instance;
            if (verify == null) return;
            verify.NoteService(ticks, b - a, allocated);
        }

        private void NoteService(long ticks, long emptyTicks, long allocated)
        {
            if (_phase != Phase.Measuring)
            {
                _outsideCalls++;
                if (ticks > _outsideMaxTicks) _outsideMaxTicks = ticks;
                return;
            }

            double perTick = 1e6 / System.Diagnostics.Stopwatch.Frequency;
            _serviceUs.Add(ticks * perTick);
            _emptyUs.Add(emptyTicks * perTick);
            if (allocated > 0)
            {
                _serviceCallsAllocating++;
                _serviceBytes += allocated;
                if (allocated > _serviceBytesMax) _serviceBytesMax = allocated;
            }
        }

        // ---- the frames --------------------------------------------------------------------------------------------------

        private void LateUpdate()
        {
            if (_phase == Phase.Done) return;
            double now = Time.realtimeSinceStartupAsDouble;
            float intervalMs = Time.unscaledDeltaTime * 1000f;
            // The observation common to both modes: its capture and read are made here whatever the statistics do.
            FrameTimeSample sample = _sampler.Sample();

            switch (_phase)
            {
                case Phase.Preparing:
                    if (_firstFrame < 0)
                    {
                        _firstFrame = Time.frameCount;
                        _firstSeconds = now;
                    }

                    _preparingFrames++;
                    if (intervalMs > _preparingMaxIntervalMs) _preparingMaxIntervalMs = intervalMs;
                    if ((_world == null || !_world.IsReady) && Time.unscaledTime >= _nextLookup)
                    {
                        _nextLookup = Time.unscaledTime + 0.5f;
                        CutWorldRoot found = FindFirstObjectByType<CutWorldRoot>();
                        if (found != null && !ReferenceEquals(found, _world))
                        {
                            _world = found;
                            _start.Reset();
                        }
                    }

                    bool worldReady = _world != null && _world.IsReady && !_world.IsEnding;
                    bool active = XRSettings.isDeviceActive;
                    _start.Ask(worldReady, PlayPreparation.Pending, active, active && HeadTracked(), Time.frameCount, now, out bool beganNow);
                    if (beganNow)
                    {
                        Debug.Log(Prefix + "preparation (apart from the window): " + _preparingFrames + " frames and "
                            + (now - _firstSeconds).ToString("F2", CultureInfo.InvariantCulture) + " s from this build's first frame " + _firstFrame + " to where play began, the longest interval "
                            + _preparingMaxIntervalMs.ToString("F1", CultureInfo.InvariantCulture) + " ms; " + _start.Describe()
                            + "; preparations begun " + PlayPreparation.Begun + ", ended " + PlayPreparation.Ended
                            + "; the service's Update so far: " + _outsideCalls + " calls, the longest " + Microseconds(_outsideMaxTicks) + " us");
                        _settleUntil = now + s_settle;
                        _phase = Phase.Settling;
                    }

                    break;

                case Phase.Settling:
                    if (now >= _settleUntil)
                    {
                        BeginWindow(now);
                    }

                    break;

                case Phase.Measuring:
                    _windowFrames++;
                    _intervalMs.Add(intervalMs);
                    if (sample.HasMain) _mainMs.Add(sample.MainMs); else _noMain++;
                    if (sample.HasRender) _renderMs.Add(sample.RenderMs); else _noRender++;
                    if (sample.HasGpu) _gpuMs.Add(sample.GpuMs); else _noGpu++;
                    if (GamePause.IsPaused) _pausedFrames++;
                    if (XRSettings.isDeviceActive && !OpenXRUtility.IsSessionFocused) _unfocusedFrames++;
                    if (now - _windowBegan >= s_seconds)
                    {
                        EndWindow(now);
                    }

                    break;
            }
        }

        private void BeginWindow(double now)
        {
            _windowBegan = now;
            _gcBefore = GC.CollectionCount(0);
            _totalBefore = GC.GetTotalMemory(false);
            _monoBefore = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            _threadBefore = GC.GetAllocatedBytesForCurrentThread();
            _countedBefore = _service != null ? _service.Collector.Gate.Counted : 0UL;
            _phase = Phase.Measuring;
        }

        private void EndWindow(double now)
        {
            _phase = Phase.Done;
            CultureInfo c = CultureInfo.InvariantCulture;
            int gc = GC.CollectionCount(0) - _gcBefore;
            long total = GC.GetTotalMemory(false) - _totalBefore, mono = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - _monoBefore;
            long thread = GC.GetAllocatedBytesForCurrentThread() - _threadBefore;
            ulong counted = (_service != null ? _service.Collector.Gate.Counted : 0UL) - _countedBefore;
            string mode = s_on ? "ON" : "OFF";

            Debug.Log(Prefix + "window [" + mode + "]: " + (now - _windowBegan).ToString("F2", c) + " s, " + _windowFrames + " frames, opened "
                + s_settle.ToString("F1", c) + " s after play began; consent " + (_service != null ? _service.Consent.Decision.ToString() : "(no service)")
                + "; the statistics counted " + counted + " frames in it; paused frames " + _pausedFrames + ", XR session out of focus " + _unfocusedFrames
                + "; this observation's sampler: reads " + _sampler.Reads + " dup " + _sampler.Duplicates + " absent " + _sampler.Absent);
            Debug.Log(Prefix + "frames [" + mode + "] (the observation common to both modes: its own cost is in both and is not in their difference): interval ms: "
                + _intervalMs.Describe("ms") + " | main ms: " + _mainMs.Describe("ms") + " (none " + _noMain + ") | render ms: " + _renderMs.Describe("ms") + " (none " + _noRender
                + ") | gpu ms: " + _gpuMs.Describe("ms") + " (none " + _noGpu + ")");
            Debug.Log(Prefix + "frames [" + mode + "] main ms buckets: " + _mainMs.DescribeBuckets());
            Debug.Log(Prefix + "frames [" + mode + "] interval ms buckets: " + _intervalMs.DescribeBuckets());
            Debug.Log(Prefix + "service update, direct [" + mode + "] (a Stopwatch round the whole Update, one clock read inside): " + _serviceUs.Describe("us")
                + " | buckets us: " + _serviceUs.DescribeBuckets()
                + " | the empty interval (two clock reads, for reference): " + _emptyUs.Describe("us")
                + " | outside the window: " + _outsideCalls + " calls, the longest " + Microseconds(_outsideMaxTicks) + " us");
            Debug.Log(Prefix + "allocation [" + mode + "]: in the service's Update, by this thread's counter: " + _serviceBytes + " bytes in " + _serviceCallsAllocating + " of "
                + _serviceUs.Count + " calls, the most " + _serviceBytesMax + " | the window, whole process: GC collections " + gc + ", GC.GetTotalMemory " + Signed(total)
                + ", mono used " + Signed(mono) + ", this thread's allocated " + Signed(thread)
                + " | what the counters showed for " + (SelfArrays * SelfBytes) + " bytes in " + SelfArrays + " arrays: this thread's +" + s_selfThread + ", GC.GetTotalMemory +" + s_selfTotal
                + ", mono used +" + s_selfMono);
            Debug.Log(Prefix + "done [" + mode + "]: the Player quits by itself");
            Application.Quit();
        }

        private static string Signed(long value) => (value >= 0 ? "+" : "") + value;

        private static string Microseconds(long ticks) => (ticks * 1e6 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture);

        // The head's tracking in this frame, as the service asks it.
        private static bool HeadTracked()
        {
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
#endif
