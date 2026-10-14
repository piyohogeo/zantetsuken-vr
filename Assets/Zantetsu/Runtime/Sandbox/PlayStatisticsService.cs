using System;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using Zantetsu.Core;
using Zantetsu.Core.Statistics;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The lightweight play statistics of a distribution build, in the game (DESIGN 21.18): the Unity side of
    /// <see cref="PlayStatisticsCollector"/>. Each frame it reads the conditions, the frame times
    /// (<see cref="FrameTimeSampler"/>) and the world's counters -- the ledger, display, storage, driver, collections,
    /// the draw route -- from the values those already keep, and hands them to the collector; it sends by
    /// <see cref="PlayStatisticsSender"/> at the loss of focus. Nothing is read, counted or sent before the player's
    /// agreement; a withdrawal clears what was collected, drops what waits and abandons what is under way. The game's own
    /// clocks and counts run whatever the consent: this reads them and adds nothing to their path.
    /// <para>
    /// **Allocation.** The per-frame path allocates nothing. What allocates does so on a change: finding the world and
    /// the camera drawings when the world is new (and, until drawings are found, once a second), the XR runtime's and
    /// head device's names when XR becomes active, the time-source text when the XR display appears.
    /// </para>
    /// </summary>
    public sealed class PlayStatisticsService : MonoBehaviour
    {
        public const string Prefix = "PLAY STATISTICS: ";

        /// <summary>How often the world and its parts are looked for while they are not at hand, seconds.</summary>
        public const float LookupSeconds = 1f;

        public static PlayStatisticsService Instance { get; private set; }

        public PlayStatistics Statistics { get; private set; }
        public PlayStatisticsCollector Collector { get; private set; }
        public PlayStatisticsConsent Consent { get; private set; }
        public PlayStatisticsSender Sender { get; private set; }
        public FrameTimeSampler Sampler { get; private set; }

        /// <summary>The quit's last offer and its wait (a PC Player's: DESIGN 21.18).</summary>
        public PlayStatisticsQuit Quit { get; private set; }
        public PlayStatisticsConfig Config { get; private set; }

        // The collector's failure sources: the world's counters, the static compaction tally, then one per camera drawing's route.
        private const int SourceWorld = 0, SourceStatic = 1, SourceRoutes = 2, MaxRoutes = PlayStatisticsCollector.MaxSources - SourceRoutes;

        private string _scene = "", _mode = "";
        private bool _focused = true;
        private CutWorldRoot _world;
        private int _worldGeneration;
        private CutWorldCameraDrawing[] _drawings = Array.Empty<CutWorldCameraDrawing>();
        private readonly VpNativeDrawRoute[] _routes = new VpNativeDrawRoute[MaxRoutes];
        private readonly int[] _routeGenerations = new int[MaxRoutes];

        /// <summary>Camera drawings beyond the collector's sources, whose route failures are not counted (0 normally).</summary>
        public int RoutesNotCounted { get; private set; }
        private XRDisplaySubsystem _xr;
        private bool _xrActive, _openXr;
        private string _xrRuntime = PlayEnvironment.NoXr;   // the epoch's condition: the runtime's own name, "none" without XR, null where not obtained
        private readonly System.Collections.Generic.List<XRInputSubsystem> _inputs = new System.Collections.Generic.List<XRInputSubsystem>();

        /// <summary>Where play began for the world at hand (DESIGN 21.18): no frame before it is counted.</summary>
        public PlayStartConditions PlayStart { get; } = new PlayStartConditions();
        private float _nextLookup;
        private readonly System.Collections.Generic.List<XRDisplaySubsystem> _displays = new System.Collections.Generic.List<XRDisplaySubsystem>();

        /// <summary>
        /// Installs the service for a scenario's scene, once; called again for another scene, the one service is retargeted
        /// (the scene and mode it labels frames with, the world it reads), so a scene change does not keep the first labels.
        /// The consent is read; nothing is collected before it is given.
        /// </summary>
        public static PlayStatisticsService Install(SandboxScenarioKind kind, string scene)
        {
            if (Instance != null)
            {
                Instance.Retarget(kind, scene);
                return Instance;
            }
            var host = new GameObject("Zantetsu play statistics");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
#if ZANTETSU_STATS_VERIFY
            // The verification build (DESIGN 21.18): the consent is the launch's argument, kept in memory -- the saved
            // consent is neither read nor written -- and no send configuration is loaded.
            service.Setup(kind, scene, PlayStatisticsVerify.StoreForThisLaunch(), null);
#else
            service.Setup(kind, scene, new PlayerPrefsStatisticsStore(), PlayStatisticsConfig.Load());
#endif
            Instance = service;
            service.HookQuit();
            // The session id is in the log so that what a receiver stored (sessions/<session id>/<revision>.json) can be found
            // from a run's own log: a random id of this launch, nothing of the machine or the player.
            Debug.Log(Prefix + "installed for " + SandboxScenario.Name(kind) + " in " + scene + "; session " + service.Sender.SessionId + "; consent " + service.Consent.Decision
                + "; config " + (service.Config != null ? service.Config.Describe() : "absent (nothing is sent)")
                + "; frame timing " + (FrameTimeSampler.FrameTimingEnabled ? "on" : "OFF in this build"));
#if ZANTETSU_STATS_VERIFY
            PlayStatisticsVerify.Install(service);
#endif
            return service;
        }

        internal void Setup(SandboxScenarioKind kind, string scene, IPlayStatisticsStore store, PlayStatisticsConfig config)
        {
            _scene = scene ?? "";
            _mode = SandboxScenario.Name(kind);
            Config = config;
            Statistics = new PlayStatistics();
            Collector = new PlayStatisticsCollector(Statistics);
            Consent = new PlayStatisticsConsent(store);
            Sampler = new FrameTimeSampler();
#if ZANTETSU_STATS_VERIFY
            IPlayStatisticsTransport transport = null;   // the verification build sends nothing, whatever it was given
#else
            IPlayStatisticsTransport transport = config != null && config.CanSend ? new PlayStatisticsUnityTransport(this, config) : null;
#endif
            string role = config != null && !string.IsNullOrEmpty(config.testerRole) ? config.testerRole : (Debug.isDebugBuild ? "developer" : "external");
            Sender = new PlayStatisticsSender(Statistics, Consent, transport, role);
            Sender.Completed += (snapshot, result) => Debug.Log(Prefix + "send of revision " + snapshot.Revision + " (" + snapshot.Bytes.Length + " bytes): "
                + (result.Success ? "accepted" : "failed") + ", status " + result.StatusCode + (result.TimedOut ? ", timed out" : "") + (result.Error != null ? ", " + result.Error : ""));
            Quit = new PlayStatisticsQuit(Sender);
            Statistics.SessionId = Sender.SessionId;
            Statistics.BuildId = Application.version + "/" + Application.buildGUID;
            ForgetEnvironment();   // nothing of the machine is read for the statistics before the agreement
            if (Consent.Agreed)
            {
                // The saved agreement: the collection starts as if agreed now.
                ReadEnvironment();
                Collector.Agree();
            }
        }

        /// <summary>
        /// The machine's environment, read for the statistics ONCE AN AGREEMENT and never before one (DESIGN 21.18):
        /// the platform, the system, the CPU's and GPU's names, the graphics API and its level as such, and -- apart
        /// from it -- the version of the driver of the device that draws (its PCI ids; null where the system gives
        /// none for that very device). The XR names follow at the first frame after it (<see cref="NoteXrState"/>).
        /// </summary>
        private void ReadEnvironment()
        {
            Statistics.Platform = Application.platform.ToString();
            Statistics.OperatingSystem = SystemInfo.operatingSystem ?? "";
            Statistics.Cpu = SystemInfo.processorType ?? "";
            Statistics.Gpu = SystemInfo.graphicsDeviceName ?? "";
            Statistics.GraphicsApi = SystemInfo.graphicsDeviceVersion ?? "";
            Statistics.GpuDriver = GraphicsDriverVersion.Read(SystemInfo.graphicsDeviceVendorID, SystemInfo.graphicsDeviceID);
            UpdateTimeSource();
            _xrActive = false;   // the XR names are read again at the next frame, as at a start
            _openXr = false;
        }

        /// <summary>No agreement (none yet, or withdrawn): nothing of the environment is kept.</summary>
        private void ForgetEnvironment()
        {
            Statistics.Platform = "";
            Statistics.OperatingSystem = "";
            Statistics.Cpu = "";
            Statistics.Gpu = "";
            Statistics.GraphicsApi = "";
            Statistics.GpuDriver = null;
            Statistics.XrDevice = null;
            Statistics.XrRuntime = null;
            Statistics.XrRuntimeVersion = null;
            Statistics.TimeSource = "";
            _xrActive = false;
            _openXr = false;
            _xrRuntime = PlayEnvironment.NoXr;
        }

        /// <summary>Another scene (or scenario) is the one played now: its labels from here, its world looked for; the frame of the change is a boundary.</summary>
        internal void Retarget(SandboxScenarioKind kind, string scene)
        {
            _scene = scene ?? "";
            _mode = SandboxScenario.Name(kind);
            _world = null;
            _drawings = Array.Empty<CutWorldCameraDrawing>();
            _nextLookup = 0f;
            PlayStart.Reset();
            Collector.Interrupt();
        }

        // ---- the quit of a PC Player (DESIGN 21.18) --------------------------------------------------------------------

        private bool _quitHooked;

        /// <summary>
        /// Whether this process is a desktop Player: where a window closed and <c>Application.Quit</c> both come as
        /// <c>Application.wantsToQuit</c> and the quit can be held a moment. The end of a Quest app is not this (the
        /// system suspends and ends it; nothing here is applied to it), and leaving Play Mode in the Editor asks nothing.
        /// </summary>
        public static bool IsDesktopPlayer =>
            Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.LinuxPlayer || Application.platform == RuntimePlatform.OSXPlayer;

        private void HookQuit()
        {
            if (_quitHooked || !IsDesktopPlayer) return;
            Application.wantsToQuit += OnWantsToQuit;
            _quitHooked = true;
        }

        private bool OnWantsToQuit() => RequestQuit(Time.realtimeSinceStartupAsDouble);

        /// <summary>
        /// A request to quit (the window closed, the quit the game makes itself): true when the quit may go on now. At
        /// the first one the collection stops -- the statistics are final, and the wait below is no part of them -- the
        /// local record is written (apart from any sending, and whether or not anything is sent), and the last
        /// statistics are offered by the one path the sender has. Where a send is then under way the quit is held, at
        /// most <see cref="PlayStatisticsQuit.WaitSeconds"/>, and <see cref="TickQuit"/> ends it; a request made again
        /// while it is held starts nothing, and after it every request passes.
        /// </summary>
        internal bool RequestQuit(double now)
        {
            if (Quit.Finished)
            {
                return true;
            }

            if (Quit.Waiting)
            {
                Quit.Request(now);   // counted, nothing started
                return false;
            }

            Collector.Stop();
            LogRecord("at the quit");
            bool may = Quit.Request(now);
            Debug.Log(Prefix + "quit asked: " + Quit.Describe());
            return may;
        }

        /// <summary>One frame of the held quit: true when the wait has just ended and the quit is to be made now.</summary>
        internal bool TickQuit(double now)
        {
            if (!Quit.Waiting)
            {
                return false;
            }

            if (!Quit.Tick(now))
            {
                return false;
            }

            Debug.Log(Prefix + "quit: " + Quit.Describe());
            return true;
        }

        /// <summary>
        /// The local record of what was counted, in the log of the Player itself (every build; once an event, never a
        /// frame): written whether or not anything is sent, so that the statistics of an agreed run can be read without
        /// a receiver. In the Editor and a Development build the lag and the cost of this service follow it.
        /// </summary>
        private void LogRecord(string when)
        {
            if (!Collector.Agreed)
            {
                Debug.Log(Prefix + "record " + when + ": not agreed, nothing was counted");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log(Prefix + DescribeCost());
#endif
                return;
            }

            Debug.Log(Prefix + "record " + when + ": " + DescribeCounts());
            Debug.Log(Prefix + "record " + when + ": " + PlayStart.Describe() + "; preparations begun " + PlayPreparation.Begun + ", ended " + PlayPreparation.Ended
                + "; environment: graphics API '" + Statistics.GraphicsApi + "', GPU driver " + (Statistics.GpuDriver ?? "not obtained")
                + ", XR runtime " + (Statistics.XrRuntime != null ? "'" + Statistics.XrRuntime + "'" : "not obtained") + " " + (Statistics.XrRuntimeVersion ?? "(version not obtained)") + ", XR device " + (Statistics.XrDevice ?? "not obtained"));
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log(Prefix + Sampler.DescribeLag());   // the verification aid of the attribution bound: Editor/Development only
            Debug.Log(Prefix + DescribeCost());
#endif
        }

        /// <summary>The player agreed (the first-run dialog, or the setting): collection starts from nothing, the counters from here.</summary>
        public void Agree()
        {
            Consent.Agree();
            ReadEnvironment();   // from the agreement on, not before
            Collector.Agree();
            Debug.Log(Prefix + "agreed; collection starts (install id made)");
        }

        /// <summary>The player declined or withdrew: nothing more is collected or sent; what waits is dropped, what is under way abandoned.</summary>
        public void Withdraw()
        {
            Consent.Decline();
            Sender.Withdraw();
            Collector.Withdraw();
            ForgetEnvironment();
            Debug.Log(Prefix + "declined/withdrawn; nothing is collected or sent, the pending snapshot dropped, the send under way abandoned");
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // The cost of this service's own Update (Editor/Development only): what the ON/OFF comparison of the Player check
        // reads. Not a part of the statistics; nothing of it in a Release Player.
        private long _updateTicks, _updateMaxTicks, _updateCalls, _updateCountedCalls;

        /// <summary>One line of the service's own cost so far: calls, mean and max microseconds, and how many calls read the world.</summary>
        public string DescribeCost()
        {
            double perTick = 1e6 / System.Diagnostics.Stopwatch.Frequency;
            return "service update cost: " + _updateCalls + " calls (" + _updateCountedCalls + " read the world), mean "
                + (_updateCalls > 0 ? (_updateTicks * perTick / _updateCalls).ToString("F2") : "-") + " us, max " + (_updateMaxTicks * perTick).ToString("F1") + " us"
                + (Collector.Agreed ? " [agreed]" : " [not agreed: the early return]");
        }
#endif

        private void Update()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            long updateBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                UpdateCore();
            }
            finally
            {
                long took = System.Diagnostics.Stopwatch.GetTimestamp() - updateBegan;
                _updateCalls++;
                _updateTicks += took;
                if (took > _updateMaxTicks) _updateMaxTicks = took;
            }
#elif ZANTETSU_STATS_VERIFY
            // The verification build alone (DESIGN 21.18): the whole Update between two clock reads, the allocation
            // counter read outside them. No product build, Development or Release, compiles this branch.
            PlayStatisticsVerify.ServiceUpdateBegins();
            long verifyBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateCore();
            long verifyTook = System.Diagnostics.Stopwatch.GetTimestamp() - verifyBegan;
            PlayStatisticsVerify.ServiceUpdateEnded(verifyTook);
#else
            UpdateCore();
#endif
        }

        private void UpdateCore()
        {
            if (Quit.Requested)
            {
                // The quit was asked for: nothing more is sampled or counted. While it is held for its send, the wait is
                // looked at once a frame (no thread is blocked); when it ends, the quit is made -- and passes, being finished.
                if (TickQuit(Time.realtimeSinceStartupAsDouble) && Application.isPlaying && !Application.isEditor)
                {
                    Application.Quit();
                }

                return;
            }

            if (!Collector.Agreed)
            {
                return;   // nothing is read or counted for the statistics without the agreement
            }

            float now = Time.unscaledTime;
            bool worldAtHand = _world != null && _world.IsReady;
            if (!worldAtHand || _drawings.Length == 0 || _xr == null || !_xrActive)
            {
                if (now >= _nextLookup)
                {
                    _nextLookup = now + LookupSeconds;
                    LookUp();
                }
            }

            bool worldReady = _world != null && _world.IsReady && !_world.IsEnding;
            bool active = XRSettings.isDeviceActive;
            if (active != _xrActive) NoteXrState(active);

            // Where play begins (DESIGN 21.18): the world ready, the scene's start-up preparation over and, in XR, the
            // head tracked -- a saved agreement or not. Until then nothing is counted; once begun for this world the
            // preparation is not asked again (a long frame of ordinary play is play).
            if (!PlayStart.Began)
            {
                // The head's tracking as it is in this frame (not whether it was ever tracked): a head tracked during the
                // preparation and lost since does not begin the play.
                PlayStart.Ask(worldReady, PlayPreparation.Pending, active, active && HeadTracked(), Time.frameCount, Time.realtimeSinceStartupAsDouble, out bool beganNow);
                if (beganNow)
                {
                    Debug.Log(Prefix + PlayStart.Describe());
                    if (active) Debug.Log(Prefix + DescribeView());
                }
            }

            bool ready = worldReady && PlayStart.Began;
            // Whether the player can play: in XR the session's own focus (the desktop window may be out of focus while the
            // play goes on in the headset, or in focus under the system menu); without XR the window's.
            bool focused = PlayFocus.ForPlay(active, _openXr, active && _openXr && OpenXRUtility.IsSessionFocused, _focused);
            PlayConditions c = ReadConditions(active);
            FrameTimeSample sample = Sampler.Sample();
            Collector.Frame(ready, GamePause.IsPaused, focused, in c, Time.unscaledDeltaTime * 1000f, sample, ready ? _world.Ledger.Revision : 0L);
            if (!ready)
            {
                return;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _updateCountedCalls++;
#endif
            ReadCounters();
        }

        /// <summary>
        /// One line of what was counted, for the log at a focus loss (every build): the frames and epochs, each time
        /// source's taken/missing counts, so that a Player's log says what its FrameTimingManager and XR display gave
        /// without the JSON being sent anywhere.
        /// </summary>
        public string DescribeCounts()
        {
            PlayStatistics s = Statistics;
            var b = new System.Text.StringBuilder(256);
            b.Append("counted: epochs ").Append(s.EpochCount).Append(" (dropped ").Append(s.EpochChangesDropped).Append("), frames outside ").Append(s.FramesOutsideEpochs)
                .Append(", live max ").Append(s.MaxLiveFragments).Append(", cuts ").Append(s.AcceptedCuts).Append(", collected ").Append(s.CollectedFragments)
                .Append("; time source ").Append(s.TimeSource).Append("; sampler reads ").Append(Sampler.Reads).Append(" dup ").Append(Sampler.Duplicates).Append(" absent ").Append(Sampler.Absent);
            for (int i = 0; i < s.EpochCount; i++)
            {
                PlayEpoch e = s.EpochAt(i);
                b.Append(" | epoch ").Append(i).Append(' ').Append(e.Conditions.Scene).Append('/').Append(e.Conditions.DrawRoute).Append('/').Append(e.Conditions.RefreshRateHz.ToString("0.#"))
                    .Append("Hz: frames ").Append(e.Frames).Append(", unattributed ").Append(e.Unattributed).Append(", structure ").Append(e.StructureChangedFrames)
                    .Append(", main ").Append(e.Main.Samples).Append('/').Append(e.MainMissing).Append(" max ").Append(e.Main.MaxMs.ToString("0.#"))
                    .Append(", render ").Append(e.Render.Samples).Append('/').Append(e.RenderMissing)
                    .Append(", gpu ").Append(e.Gpu.Samples).Append('/').Append(e.GpuMissing).Append(" max ").Append(e.Gpu.MaxMs.ToString("0.#"))
                    .Append(", gpuXr ").Append(e.GpuXr.Samples).Append('/').Append(e.GpuXrMissing).Append(" max ").Append(e.GpuXr.MaxMs.ToString("0.#"))
                    .Append(", interval max ").Append(e.Interval.MaxMs.ToString("0.#"));
            }

            return b.ToString();
        }

        // The conditions this frame, from values at hand (no string is made here: the names are cached on change).
        private PlayConditions ReadConditions(bool xrActive)
        {
            return new PlayConditions
            {
                Scene = _scene,
                Mode = _mode,
                DrawRoute = _world != null ? _world.BodyRoute ?? "" : "",
                XrRuntime = _xrRuntime,
                RefreshRateHz = _xr != null && _xr.TryGetDisplayRefreshRate(out float hz) ? hz : 0f,
                RenderScale = xrActive ? XRSettings.eyeTextureResolutionScale : 0f,
                EyeTextureWidth = xrActive ? XRSettings.eyeTextureWidth : 0,
                EyeTextureHeight = xrActive ? XRSettings.eyeTextureHeight : 0,
                MsaaSamples = xrActive ? XRSettings.eyeTextureDesc.msaaSamples : QualitySettings.antiAliasing,
                CutSurfaceDebug = VpCutSurfaceColour.DebugEnabled,
            };
        }

        // The world's counters as they stand: totals with the generation of what keeps them, peaks where a source keeps one.
        private void ReadCounters()
        {
            CutWorldRoot world = _world;
            int g = _worldGeneration;
            Collector.Live(world.Ledger.LiveCutFragmentCount);
            Collector.Total(PlayTotalKind.AcceptedCuts, g, world.Ledger.OperationCount);
            long collected = (world.Groups != null ? world.Groups.FragmentsRetired : 0) + (world.Lifetime != null ? world.Lifetime.Retired : 0)
                + (world.EmptyOwners != null ? world.EmptyOwners.Retired : 0);
            Collector.Total(PlayTotalKind.CollectedFragments, g, collected);
            if (world.Storage != null)
            {
                // The buffers' headroom (DESIGN 21.18), three figures apart: the end in use (holes included, the buffer's own
                // end figure, never a use net of holes), the capacity allocated now, and the fixed limit it may grow to.
                // CPU vertices: the published end, the committed pages, the reservation. CPU indices keep no published end:
                // the committed pages stand as the end, against the reservation (capacity = limit there).
                VpCpuGeometryStorage storage = world.Storage;
                Collector.Capacity(PlayCapacityKind.CpuVertices, g, storage.VertexCount, storage.CommittedVertexCapacity, storage.VertexCapacity, storage.VertexCommitGrowths, storage.RoomRefusalCount);
                Collector.Capacity(PlayCapacityKind.CpuIndices, g, storage.CommittedIndexCapacity, storage.IndexCapacity, storage.IndexCapacity, storage.IndexCommitGrowths, 0);
                Collector.Failure(PlayFailureKind.StorageRoomRefused, SourceWorld, g, storage.RoomRefusalCount);
            }

            if (world.Display != null)
            {
                // The display's buffers grow toward fixed limits: the end taken, the capacity now, the limit.
                VpLogicalCutDisplay display = world.Display;
                Collector.Capacity(PlayCapacityKind.DrawCommandSlots, g, display.DrawCommandEnd, display.CommandCapacity, display.CommandLimit, display.CommandGrowths, 0);
                Collector.Capacity(PlayCapacityKind.DrawInstanceSlots, g, display.DrawInstanceEnd, display.InstanceCapacity, display.InstanceLimit, display.InstanceGrowths, 0);
                Collector.Capacity(PlayCapacityKind.RenderFragments, g, display.RenderFragmentCount, display.RenderFragmentCapacity, display.RenderFragmentLimit, display.FragmentGrowths, 0);
                Collector.Capacity(PlayCapacityKind.GpuVertices, g, display.GpuVertexHighWater, display.GpuVertexCapacity, display.GpuVertexLimit, display.GpuVertexGrowthCount, 0);
                Collector.Capacity(PlayCapacityKind.GpuIndices, g, display.GpuIndexHighWater, display.GpuIndexCapacity, display.GpuIndexLimit, display.GpuIndexGrowthCount, 0);
            }

            if (world.Driver != null)
            {
                Collector.Failure(PlayFailureKind.CutAborted, SourceWorld, g, world.Driver.AbortCount);
                Collector.Failure(PlayFailureKind.CutFailed, SourceWorld, g, world.Driver.FailedCutCount);
                Collector.Failure(PlayFailureKind.CutKernelFailed, SourceWorld, g, world.Driver.KernelFailedCount);
            }

            // Each camera drawing's route is a source of its own (DESIGN 21.18): its identity is the route object at that
            // drawing's index, so a route remade -- the drawing's array unchanged -- is a new generation of that source
            // alone, and the others' counts neither reset nor double. Drawings past the collector's sources are not counted.
            for (int i = 0; i < _drawings.Length && i < MaxRoutes; i++)
            {
                VpNativeDrawRoute route = _drawings[i] != null ? _drawings[i].NativeRoute : null;
                if (!ReferenceEquals(route, _routes[i]))
                {
                    _routes[i] = route;
                    _routeGenerations[i]++;
                }

                if (route == null) continue;
                int source = SourceRoutes + i, rg = _routeGenerations[i];
                Collector.Failure(PlayFailureKind.DrawRefused, source, rg, route.Refused);
                Collector.Failure(PlayFailureKind.RingFull, source, rg, route.RingFull);
                Collector.Failure(PlayFailureKind.ShadowMapFormatRefused, source, rg, route.ShadowMapFormatRefusals);
                Collector.Failure(PlayFailureKind.IndirectCountZeroRefused, source, rg, route.IndirectCountZeroRefused);
            }

            if (_drawings.Length > MaxRoutes) RoutesNotCounted = _drawings.Length - MaxRoutes;
            Collector.Failure(PlayFailureKind.CompactionSlotsShort, SourceStatic, 0, VpCommandCompaction.SlotsShort);   // a static: one generation
            Collector.Failure(PlayFailureKind.TerminationRequested, SourceWorld, g, world.TerminationRequested ? 1 : 0);

            PlaySettings menu = PlayMenu.Instance != null ? PlayMenu.Instance.Settings : null;
            Collector.Settings(menu != null
                ? new PlayUiSettings { Known = true, Volume = menu.Volume, MoveSpeed = menu.MoveSpeed, TurnDegrees = menu.Turn, TurnIsContinuous = menu.TurnIsContinuous, BladeAngleDegrees = menu.BladeAngle }
                : default);
        }

        // The world, its camera drawings and the XR display, looked for while any is not at hand -- never every frame.
        private void LookUp()
        {
            if (_world == null || !_world.IsReady)
            {
                CutWorldRoot found = FindFirstObjectByType<CutWorldRoot>();
                if (found != null && !ReferenceEquals(found, _world))
                {
                    _world = found;
                    _worldGeneration++;
                    _drawings = Array.Empty<CutWorldCameraDrawing>();
                    PlayStart.Reset();   // another world: play has not begun for it
                    Collector.Interrupt();
                }
            }

            if (_drawings.Length == 0 && _world != null)
            {
                _drawings = FindObjectsByType<CutWorldCameraDrawing>(FindObjectsSortMode.None);   // allocates: on a new world, and once a second until found
            }

            if (_xr == null)
            {
                _displays.Clear();
                SubsystemManager.GetSubsystems(_displays);
                _xr = _displays.Count > 0 ? _displays[0] : null;
                if (_xr != null)
                {
                    Sampler.XrDisplay = _xr;
                    UpdateTimeSource();
                }
            }

            if (!_xrActive && XRSettings.isDeviceActive) NoteXrState(true);
        }

        // XR became active or inactive: the names that need a string are read here, once per change. The runtime's own
        // name and version come from the OpenXR runtime (not the engine's loader name); the device's name is recorded only
        // where it is a device's -- the engine's generic "Head Tracking - OpenXR" is "not obtained", and nothing of the
        // headset or its connection is guessed from the runtime's name.
        private void NoteXrState(bool active)
        {
            _xrActive = active;
            string loader = active ? XRSettings.loadedDeviceName ?? "" : "";
            _openXr = active && loader.IndexOf("OpenXR", StringComparison.OrdinalIgnoreCase) >= 0;
            string runtimeName = null, runtimeVersion = null;
            if (_openXr)
            {
                try
                {
                    runtimeName = PlayEnvironment.OrNull(OpenXRRuntime.name);
                    runtimeVersion = PlayEnvironment.OrNull(OpenXRRuntime.version);
                }
                catch (Exception)
                {
                    runtimeName = null;   // not obtained
                    runtimeVersion = null;
                }
            }

            // The runtime's own name, or null where it gave none: never the loader's name in its place.
            _xrRuntime = PlayEnvironment.XrRuntimeName(active, runtimeName);
            Statistics.XrRuntime = _xrRuntime;
            Statistics.XrRuntimeVersion = runtimeVersion;
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            Statistics.XrDevice = active && head.isValid ? PlayEnvironment.XrDeviceName(head.name) : null;
            UpdateTimeSource();
        }

        // Whether the head is tracked now (asked only until play has begun).
        private static bool HeadTracked()
        {
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            return head.isValid && head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
        }

        /// <summary>
        /// One line of where the view stands as play begins (every build, once a world): the tracking origin in effect
        /// (Floor: the head's height is the player's own above the floor; Device: the scene's fixed camera offset stands
        /// in for it), the camera's parent's local height (that offset as applied), and the head's local and world
        /// heights. For telling a view that looks low or high; nothing of it goes into the statistics.
        /// </summary>
        private string DescribeView()
        {
            _inputs.Clear();
            SubsystemManager.GetSubsystems(_inputs);
            string origin = "unknown (no XR input subsystem)";
            if (_inputs.Count > 0)
            {
                origin = _inputs[0].GetTrackingOriginMode() + " (supported: " + _inputs[0].GetSupportedTrackingOriginModes() + ")";
            }

            Camera camera = Camera.main;
            if (camera == null) return "view: tracking origin " + origin + "; no main camera";
            Transform head = camera.transform, offset = head.parent, rig = offset != null ? offset.parent : null;
            return "view: tracking origin " + origin + "; camera offset object '" + (offset != null ? offset.name : "(none)") + "' local y " + (offset != null ? offset.localPosition.y.ToString("F3") : "-")
                + "; rig '" + (rig != null ? rig.name : "(none)") + "' world y " + (rig != null ? rig.position.y.ToString("F3") : "-")
                + "; head local y " + head.localPosition.y.ToString("F3") + ", world y " + head.position.y.ToString("F3");
        }

        private void UpdateTimeSource()
        {
            Statistics.TimeSource = "FrameTimingManager" + (FrameTimeSampler.FrameTimingEnabled ? "" : "(disabled)") + (_xr != null ? "+XRDisplaySubsystem.AppGPUTime" : "");
        }

        private void OnApplicationFocus(bool focus)
        {
            _focused = focus;
            // Without XR the window's focus is the player's: its change breaks the run of frames. In XR it is not (the
            // session's focus is asked every frame), so a click on another window interrupts nothing of the play.
            if (!(_xrActive && _openXr)) Collector.Interrupt();
            if (!focus)
            {
                // A trigger of a send (DESIGN 21.18): the focus lost -- the system menu, another window. The other is the
                // ordinary quit of a PC Player (RequestQuit); there is no timer and no third event.
                if (Quit.Requested) return;   // the quit has taken the last record and made the last offer already
                Debug.Log(Prefix + "focus lost: " + Sender.OnFocusLost());
                LogRecord("at the loss of focus");
            }
        }

        private void OnApplicationPause(bool paused)
        {
            Collector.Interrupt();
        }

        private void OnDestroy()
        {
            if (_quitHooked)
            {
                Application.wantsToQuit -= OnWantsToQuit;
                _quitHooked = false;
            }

            if (Instance == this) Instance = null;
        }

        /// <summary>The first-run dialog's text: what is sent, for what, to where, and for how long (the last not yet decided).</summary>
        public const string ConsentTitle = "Play statistics";

        public static readonly string ConsentBody =
            "May this game send anonymous play statistics?\n"
            + "What: frame time histograms (CPU, GPU), counts of cuts and fragments, buffer sizes,\n"
            + "failure counts, scene and settings, device/OS/GPU model and XR runtime, and random IDs.\n"
            + "Never: your name, device name, hardware IDs, head or hand movement, images.\n"
            + "For: improving performance and stability; statistical analysis only.\n"
            + "To: our Cloudflare Worker (stored in Cloudflare R2).\n"
            + "When: sent when the game loses focus, or when it is closed normally.\n"
            + "Retention: to be decided before release.\n"
            + "You can change this any time in the menu (left menu button).";

        public const string ConsentAgree = "Agree";
        public const string ConsentDecline = "Don't send";
    }

    /// <summary>The consent kept in PlayerPrefs, with the game's other settings.</summary>
    public sealed class PlayerPrefsStatisticsStore : IPlayStatisticsStore
    {
        public bool TryGet(string key, out string value)
        {
            if (!PlayerPrefs.HasKey(key)) { value = null; return false; }
            value = PlayerPrefs.GetString(key);
            return true;
        }

        public void Set(string key, string value) => PlayerPrefs.SetString(key, value);
        public void Delete(string key) => PlayerPrefs.DeleteKey(key);
        public void Save() => PlayerPrefs.Save();
    }
}
