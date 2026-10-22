#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The managed side of the Direct3D 12 native draw plugin (Native/ZantetsuVpNative; DESIGN 4.5.8): the contract's
    /// structures mirrored field for field from ZantetsuVpNative.h, the entry points, and the small tables that turn
    /// Unity's formats and sampler settings into the plugin's. <see cref="TryLoad"/> is the one way in: a Player
    /// without the library, a library of another contract version, or a graphics device the plugin cannot draw on
    /// (anything but Direct3D 12) is a failure with its reason -- the caller records it and does not draw through the
    /// plugin; nothing falls back on its own.
    /// </summary>
    public static unsafe class VpNativeDrawPlugin
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        public const string Library = "ZantetsuVpVulkan";
#else
        public const string Library = "ZantetsuVpNative";
#endif
        public static bool Vulkan => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan;
        public const int ContractVersion = 1;
        public const int NameLength = 64;
        public const int MaxViewports = 4;

        /// <summary>The argument entry the plugin's command signature reads: the command number, then the five indexed arguments.</summary>
        public const int ArgumentStride = 24;

        public enum BindingKind : uint
        {
            ConstantBuffer = 0,
            CommandConstant = 1,
            Buffer = 2,
            Texture = 3,
            Sampler = 4,
        }

        public enum Stage : uint
        {
            Vertex = 0,
            Pixel = 1,
        }

        public enum TextureKind : uint
        {
            Tex2D = 0,
            Cube = 1,
            Tex2DArray = 2,
        }

        public enum SamplerKind : uint
        {
            PointClamp = 0,
            LinearClamp = 1,
            LinearWrap = 2,
            TrilinearClamp = 3,
            TrilinearWrap = 4,
            CompareLinearClampLessEqual = 5,
            CompareLinearClampGreaterEqual = 6,
        }

        public enum CullMode : uint
        {
            None = 1,
            Front = 2,
            Back = 3,
        }

        public enum Compare : uint
        {
            Never = 1,
            Less = 2,
            Equal = 3,
            LessEqual = 4,
            Greater = 5,
            NotEqual = 6,
            GreaterEqual = 7,
            Always = 8,
        }

        public enum ResourceKind : uint
        {
            RawBuffer = 0,
            StructuredBuffer = 1,
            Texture = 2,
            RenderBuffer = 3,
        }

        public enum Result : uint
        {
            Recorded = 0,
            NotAvailable = 1,
            BadVersion = 2,
            NoPipeline = 3,
            NoCommandList = 4,
            RingFull = 5,
            BadResource = 6,
            BadConstants = 7,
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Binding
        {
            public uint kind;
            public uint stage;
            public uint shaderRegister;
            public uint extra;
            public uint extra2;
            public fixed byte name[NameLength];

            public void SetName(string value)
            {
                fixed (byte* p = name)
                {
                    WriteName(p, value);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PipelineDesc
        {
            public uint version;
            public void* vertexBytecode;
            public uint vertexBytecodeSize;
            public void* pixelBytecode;
            public uint pixelBytecodeSize;
            public Binding* bindings;
            public uint bindingCount;
            public uint renderTargetFormat;
            public uint depthFormat;
            public uint sampleCount;
            public uint cullMode;
            public uint frontCounterClockwise;
            public uint depthFunc;
            public uint depthWrite;
            public uint colourWrite;
            public int depthBias;
            public float slopeScaledDepthBias;
            public float depthBiasClamp;
            public uint argumentStride;
            public fixed byte name[NameLength];
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Resource
        {
            public uint binding;
            public uint kind;
            public void* resource;
            public uint firstElement;
            public uint elementCount;
            public uint stride;
            public uint format;
            public uint dimension;
            public uint mipLevels;
            public uint arraySize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Constants
        {
            public uint binding;
            public uint offset;
            public uint size;
            public void* resource;
            public uint resourceOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Viewport
        {
            public float x, y, width, height, minDepth, maxDepth;
            public int scissorLeft, scissorTop, scissorRight, scissorBottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Draw
        {
            public uint version;
            public uint pipeline;
            public void* argumentBuffer;
            public uint argumentOffset;
            public uint commandCount;
            public void* indexBuffer;
            public uint indexBufferBytes;
            public uint indexFormat;
            public uint viewportCount;
            public fixed byte viewports[MaxViewports * 40];
            public uint resourceCount;
            public uint resourcesOffset;
            public uint constantsCount;
            public uint constantsOffset;
            public uint totalBytes;
            public uint serial;
            public volatile uint result;
            public volatile uint consumed;
        }

        public const uint ExtendedDrawVersion = 2;
        public enum DrawMode : uint { Indirect = 0, Direct = 1, Group = 2, IndirectCount = 3 }
        [StructLayout(LayoutKind.Sequential)]
        public struct DrawExtension
        {
            public uint mode, commandsOffset, commandsCount, reserved;
        }
        /// <summary>
        /// Experiment (2026-10-10): the one record of a <see cref="DrawMode.IndirectCount"/> draw -- the buffer the GPU
        /// counted the compacted commands into, and where; and a buffer range the plugin zeroes in this draw's
        /// preparation (the counts the previous frame's draw read), null for none. The draw's commandCount is then the
        /// most the count may be.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct IndirectCountRecord
        {
            public void* countBuffer;
            public uint countOffset;
            public uint zeroBytes;
            public void* zeroBuffer;
            public uint zeroOffset;
            public uint reserved;   // 0, or CountDrawFromCopy
        }

        /// <summary>
        /// <see cref="IndirectCountRecord.reserved"/>: the draw reads its count from the 4-byte slot the record names
        /// (zeroBuffer, zeroOffset; zeroBytes 0), into which the plugin copies it by a transfer in the draw's preparation
        /// (ZVN_COUNT_DRAW_FROM_COPY). See <see cref="VpCommandCompaction.DrawFromCopy"/>.
        /// </summary>
        public const uint CountDrawFromCopy = 1u;
        [StructLayout(LayoutKind.Sequential)]
        public struct DirectCommand
        {
            public uint indexCount, instanceCount, firstIndex;
            public int vertexOffset;
            public uint firstInstance;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Counters
        {
            public uint version;
            public ulong drawsRecorded;
            public ulong commandsRecorded;
            public ulong drawsRefused;
            public uint lastRefusal;
            public uint lastRefusalSerial;
            public ulong constantBytesUploaded;
            public ulong descriptorsWritten;
            public uint ringRegions;
            public uint ringRegionBytes;
            public ulong sentinelsRun;
            public ulong eventsHeld;

            public override string ToString()
            {
                return "draws recorded " + drawsRecorded + " (commands " + commandsRecorded + "), refused " + drawsRefused
                    + (drawsRefused > 0 ? " (last " + (Result)lastRefusal + " serial " + lastRefusalSerial + ")" : "")
                    + ", constant bytes " + constantBytesUploaded + ", descriptors " + descriptorsWritten
                    + ", rings " + ringRegions + " x " + ringRegionBytes + " B, sentinels run " + sentinelsRun
                    + (eventsHeld > 0 ? ", events held by the test control " + eventsHeld : "");
            }
        }

        public const uint DxgiR32Uint = 42;
        public const uint DxgiR16Uint = 57;

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnContractVersion();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnIsAvailable();

        // Optional Vulkan capability observation; available even when native drawing is not.
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnReadClipDistanceInfo(uint* supported, uint* enabled);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnDescribeState(byte* output, int outputSize);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnLastError(byte* output, int outputSize);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern void ZvnConfigureRings(uint regions, uint regionBytes, uint regionDescriptors);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnCreatePipeline(PipelineDesc* desc);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern void ZvnReleasePipeline(int pipeline);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnDescribePipeline(int pipeline, byte* output, int outputSize);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr ZvnGetRenderEventAndDataFunc();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnDrawEventId();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnSentinelEventId();

#if UNITY_ANDROID && !UNITY_EDITOR
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnPrepareEventId();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnPreparedDrawEventId();
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnGroupPrepareEventId();
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnGroupPreparedEventId();
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnWarmPipelineEventId();
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnZeroEventId();
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnBarrierEventId();
#endif

        // Both libraries: whether a count draw can be recorded on this device (absent in older ones).
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnIndirectCountReady();

#if !(UNITY_ANDROID && !UNITY_EDITOR)
        // The Direct3D 12 library: the device's wave operations and the count draws recorded (absent in older ones).
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnIndirectCountInfo(ulong* output, int count);
#endif

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern void ZvnGetCounters(Counters* output);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern void ZvnFrameFence(ulong* completed, ulong* next);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern void ZvnHoldConsumption(int hold);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnReleaseHeld(int count);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnHeldCount();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern int ZvnLockStats(ulong* output, int count);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        private static extern void ZvnSetLockTiming(int on);

        /// <summary>
        /// The launch option of the detailed observation (the Main P95 investigation, 2026-10-08): with it, the plugin
        /// times its mutex (<see cref="ReadLockStats"/>), the state query is timed as the main thread sees it and the
        /// keeper breaks its end-of-rendering work into parts (<see cref="VpNativeDrawRelease.TryGetFrameWork"/>).
        /// Without it -- the default -- none of that runs: the plugin takes its mutex as it did before the observation
        /// existed, the query is made alone, and the keeper reads no clock and keeps nothing.
        /// </summary>
        public const string DetailTimingArgument = "-zantetsuNativeDrawTiming";

        /// <summary>Whether the detailed observation is on (off unless the launch asked, or a test did).</summary>
        public static bool DetailTiming { get; private set; }

        private static bool s_detailArgumentRead;

        /// <summary>
        /// Reads the launch option once (the first caller: the plugin's load or the keeper's subscription, whichever
        /// comes first); the option only ever turns the observation on.
        /// </summary>
        public static void ReadDetailTimingArgument()
        {
            if (s_detailArgumentRead)
            {
                return;
            }

            s_detailArgumentRead = true;
            if (Array.IndexOf(Environment.GetCommandLineArgs(), DetailTimingArgument) >= 0) SetDetailTiming(true);
        }

        /// <summary>
        /// Turns the detailed observation on or off, here and in the plugin when it is loaded (when it is not yet, the
        /// load applies it). What was counted while it was on stays: the sources are cumulative.
        /// </summary>
        public static void SetDetailTiming(bool on)
        {
            DetailTiming = on;
            if (!s_loaded)
            {
                return;
            }

            try
            {
                ZvnSetLockTiming(on ? 1 : 0);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                // A library without the switch has no timing either: nothing to turn.
            }
        }

        private static bool s_checked;
        private static bool s_loaded;
        private static string s_failure;
        private static IntPtr s_eventFunction;
        private static int s_eventId;

        /// <summary>
        /// Whether the plugin is loaded, of this contract, and able to draw on this device. Asked once; the answer is
        /// kept, as is the reason when it is no.
        /// </summary>
        public static bool TryLoad(out string failure)
        {
            if (!s_checked)
            {
                s_checked = true;
                try
                {
                    int version = ZvnContractVersion();
                    if (version != ContractVersion)
                    {
                        s_failure = "the native library is of contract version " + version + "; this managed side is " + ContractVersion;
                    }
                    else if (ZvnIsAvailable() == 0)
                    {
                        s_failure = "the native library cannot draw on this device: " + DescribeState();
                    }
                    else
                    {
                        s_eventFunction = ZvnGetRenderEventAndDataFunc();
                        s_eventId = ZvnDrawEventId();
                        s_sentinelEventId = ZvnSentinelEventId();
#if UNITY_ANDROID && !UNITY_EDITOR
                        s_prepareEventId = ZvnPrepareEventId();
                        s_preparedDrawEventId = ZvnPreparedDrawEventId();
                        s_groupPrepareEventId = ZvnGroupPrepareEventId();
                        s_groupPreparedEventId = ZvnGroupPreparedEventId();
                        // Optional export: libraries without warmup retain the existing contract-1 draw path.
                        try { s_warmPipelineEventId = ZvnWarmPipelineEventId(); }
                        catch (EntryPointNotFoundException) { s_warmPipelineEventId = 0; }
                        // Experiment (2026-10-10): the count slots' zero event and the count draw's readiness; absent in older libraries.
                        try { s_zeroEventId = ZvnZeroEventId(); s_indirectCountReady = ZvnIndirectCountReady() != 0; }
                        catch (EntryPointNotFoundException) { s_zeroEventId = 0; s_indirectCountReady = false; }
                        // The compute barrier event before a compaction (2026-10-10); absent in older libraries, and then no compaction.
                        try { s_barrierEventId = ZvnBarrierEventId(); }
                        catch (EntryPointNotFoundException) { s_barrierEventId = 0; }
#else
                        // The Direct3D 12 library's count draw (DESIGN 4.5.11); a library built before it has none.
                        try { s_indirectCountReady = ZvnIndirectCountReady() != 0; }
                        catch (EntryPointNotFoundException) { s_indirectCountReady = false; }
#endif
                        s_loaded = s_eventFunction != IntPtr.Zero;
                        if (!s_loaded)
                        {
                            s_failure = "the native library gave no render event function";
                        }
                    }
                }
                catch (DllNotFoundException e)
                {
                    s_failure = "the native library " + Library + " is not in this Player: " + e.Message;
                }
                catch (EntryPointNotFoundException e)
                {
                    s_failure = "the native library " + Library + " lacks an entry point of this contract: " + e.Message;
                }
                catch (BadImageFormatException e)
                {
                    s_failure = "the native library " + Library + " is not loadable here: " + e.Message;
                }

                // The detailed observation: on when the launch (or a test, before the load) asked for it, else off. The
                // plugin is told either way: in the Editor the library outlives a reload of the managed side, and its
                // switch must not be left where an earlier session put it.
                ReadDetailTimingArgument();
                if (s_loaded) SetDetailTiming(DetailTiming);
            }

            failure = s_failure;
            return s_loaded;
        }

        /// <summary>
        /// Observes the Vulkan device's ClipDistance support and actual enablement, independently of native drawing.
        /// False (both outputs false) when not Vulkan, device creation was not observed, or the optional export is absent.
        /// Does not cache an early unknown result or call TryLoad: Unity's VP3C route needs the same observation.
        /// </summary>
        public static bool TryReadClipDistanceInfo(out bool supported, out bool enabled)
        {
            supported = enabled = false;
            if (!Vulkan) return false;
            try
            {
                uint deviceSupported = 0, deviceEnabled = 0;
                if (ZvnReadClipDistanceInfo(&deviceSupported, &deviceEnabled) == 0) return false;
                supported = deviceSupported != 0;
                enabled = deviceEnabled != 0;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return false;
            }
        }

        /// <summary>The plugin's state in words; "(not loaded)" with the reason when it is not.</summary>
        public static string DescribeState()
        {
            try
            {
                return Text(ZvnDescribeState);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return "(not loaded: " + e.Message + ")";
            }
        }

        public static string LastError()
        {
            return Text(ZvnLastError);
        }

        public static IntPtr RenderEventFunction => s_eventFunction;

        public static int DrawEventId => s_eventId;

        /// <summary>The sentinel event: the plugin marks its block consumed and does nothing else (see VpNativeDrawRelease).</summary>
        public static int SentinelEventId => s_sentinelEventId;

        private static int s_sentinelEventId;
        private static int s_prepareEventId, s_preparedDrawEventId, s_groupPrepareEventId, s_groupPreparedEventId, s_warmPipelineEventId, s_zeroEventId, s_barrierEventId;
        private static bool s_indirectCountReady;
        public static int WarmPipelineEventId => s_warmPipelineEventId;
        /// <summary>Experiment (2026-10-10): the event that zeroes a count buffer range before a frame's compaction; 0 when the library has none.</summary>
        public static int ZeroEventId => s_zeroEventId;
        /// <summary>
        /// The event that records the plugin's compute barrier between a selection's dispatches and the compaction that
        /// reads their entries (2026-10-10: Unity's order of two compute dispatches on one buffer is not a contract on
        /// Quest 3S); 0 when the library has none, and then nothing compacts.
        /// </summary>
        public static int BarrierEventId => s_barrierEventId;
        /// <summary>
        /// Whether the loaded library can record a count draw on this device; false until loaded. Vulkan: the
        /// extension enabled and its entry point found. Direct3D 12: wherever the library draws (ExecuteIndirect's
        /// count buffer), and false for a library built before the count draw.
        /// </summary>
        public static bool IndirectCountReady => s_loaded && s_indirectCountReady;
        public static int GroupPrepareEventId => s_groupPrepareEventId;
        public static int GroupPreparedEventId => s_groupPreparedEventId;
        public static int PrepareEventId => s_prepareEventId;
        public static int PreparedDrawEventId => s_preparedDrawEventId;
        /// <summary>Optional diagnostic geometry for the rare native Clip path; off by default.</summary>
        public static bool VulkanDirectClipProbe { get; set; }
        /// <summary>Quest comparison switch. Set before creating a native draw route.</summary>
        public static bool VulkanColourPreparation { get; set; } = true;
        // Independent comparison switches for investigating AppSW regressions; production defaults stay enabled.
        public static bool VulkanNoClipColourShaders { get; set; } = true;
        public static bool VulkanNoClipMotionShaders { get; set; } = true;
        public static bool VulkanNoClipShadowShaders { get; set; } = true;
        public static bool VulkanShareGpuConstants { get; set; } = true;
        /// <summary>
        /// The Player argument that asks for the command compaction. On the Vulkan route the compaction is the
        /// default (DESIGN 4.5.10, D-217; since 2026-10-10), so the argument is accepted and changes nothing there.
        /// On the Direct3D 12 route the fixed draw is the default (DESIGN 4.5.11, D-219), and this argument is what
        /// asks for the compaction.
        /// </summary>
        public const string CompactCommandsArgument = "-zantetsuVpCompactCommands";

        /// <summary>
        /// The Player argument that keeps the fixed-count draw: the way back from the command compaction on the
        /// Vulkan route (DESIGN 4.5.10, D-217), and the Direct3D 12 route's default said aloud (DESIGN 4.5.11,
        /// D-219). It wins over <see cref="CompactCommandsArgument"/> on both.
        /// </summary>
        public const string FixedCommandsArgument = "-zantetsuVpFixedCommands";

        private static bool? s_indirectCountAsked;

        /// <summary>
        /// Whether the Vulkan route is asked to compact each draw unit's argument entries on the GPU
        /// (VpCommandCompaction) and draw them by vkCmdDrawIndexedIndirectCount (DESIGN 4.5.10, D-217): true unless
        /// the Player was started with <see cref="FixedCommandsArgument"/>, or a launch's configuration set it.
        /// Asking is not taking: the route takes it only where <see cref="VpCommandCompaction.TrySelect"/> finds every
        /// condition, and draws fixed otherwise. No other device's route reads it.
        /// </summary>
        public static bool VulkanIndirectCount
        {
            get
            {
                if (!s_indirectCountAsked.HasValue) s_indirectCountAsked = ReadIndirectCountRequest(Environment.GetCommandLineArgs());
                return s_indirectCountAsked.Value;
            }
            set => s_indirectCountAsked = value;
        }

        /// <summary>What <see cref="VulkanIndirectCount"/> answers for a set of arguments. For tests of the reading itself.</summary>
        public static bool ReadIndirectCountRequest(string[] arguments)
        {
            return arguments == null || Array.IndexOf(arguments, FixedCommandsArgument) < 0;
        }

        private static bool? s_direct3D12IndirectCountAsked;

        /// <summary>
        /// Whether the Direct3D 12 route is asked to compact each draw unit's argument entries on the GPU and draw
        /// them by ExecuteIndirect with a count buffer (DESIGN 4.5.11): false -- the fixed draw is that route's
        /// default (D-219) -- unless the Player was started with <see cref="CompactCommandsArgument"/> and without
        /// <see cref="FixedCommandsArgument"/>, or a test set it. Asking is not taking, as for the Vulkan route:
        /// the route takes it only where <see cref="VpCommandCompaction.TrySelect"/> finds every condition, and
        /// draws fixed otherwise, saying why.
        /// </summary>
        public static bool Direct3D12IndirectCount
        {
            get
            {
                if (!s_direct3D12IndirectCountAsked.HasValue) s_direct3D12IndirectCountAsked = ReadDirect3D12IndirectCountRequest(Environment.GetCommandLineArgs());
                return s_direct3D12IndirectCountAsked.Value;
            }
            set => s_direct3D12IndirectCountAsked = value;
        }

        /// <summary>What <see cref="Direct3D12IndirectCount"/> answers for a set of arguments. For tests of the reading itself.</summary>
        public static bool ReadDirect3D12IndirectCountRequest(string[] arguments)
        {
            return arguments != null && Array.IndexOf(arguments, CompactCommandsArgument) >= 0 && Array.IndexOf(arguments, FixedCommandsArgument) < 0;
        }

        /// <summary>What this device's route is asked: the Vulkan route's request on a Vulkan device, the Direct3D 12 route's elsewhere.</summary>
        public static bool IndirectCountAsked => Vulkan ? VulkanIndirectCount : Direct3D12IndirectCount;

        /// <summary>
        /// The Direct3D 12 library's account of the device's wave operations (D3D12_FEATURE_D3D12_OPTIONS1) and of the
        /// count draws it recorded: ExecuteIndirect calls with a count buffer, and the most commands those may have
        /// drawn (the sum of their MaxCommandCount). False where the library is not loaded, is the Vulkan one, or was
        /// built before the count draw.
        /// </summary>
        public static bool TryReadIndirectCountInfo(out bool waveOperations, out uint waveLanesLeast, out uint waveLanesMost, out ulong countDraws, out ulong countDrawMostCommands)
        {
            waveOperations = false;
            waveLanesLeast = waveLanesMost = 0;
            countDraws = countDrawMostCommands = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            return false;
#else
            if (!s_loaded || Vulkan) return false;
            try
            {
                ulong* values = stackalloc ulong[5];
                if (ZvnIndirectCountInfo(values, 5) != 5) return false;
                waveOperations = values[0] != 0;
                waveLanesLeast = (uint)values[1];
                waveLanesMost = (uint)values[2];
                countDraws = values[3];
                countDrawMostCommands = values[4];
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return false;
            }
#endif
        }

        /// <summary>
        /// Whether the plugin can run events now: false once the device is gone, after which it touches no block
        /// (so the blocks of events that will never run may be freed).
        /// </summary>
        public static bool IsAvailableNow()
        {
            if (!s_loaded)
            {
                return false;
            }

            if (!DetailTiming)
            {
                // The default: the query alone, as it was before the observation existed.
                try
                {
                    return ZvnIsAvailable() != 0;
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
                {
                    return false;
                }
            }

            // Observation (TL, 2026-10-08; on by DetailTimingArgument only): the whole call as the main thread sees it --
            // the P/Invoke, the plugin's wait for its mutex, its moment under it -- counted and timed (Stopwatch ticks; no
            // allocation). The plugin's own share of the wait and the hold is in LockStats.
#if ZANTETSU_OBSERVE
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            try
            {
                return ZvnIsAvailable() != 0;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return false;
            }
            finally
            {
                AvailabilityCalls++;
#if ZANTETSU_OBSERVE
                long took = System.Diagnostics.Stopwatch.GetTimestamp() - began;
                AvailabilityTicks += took;
                if (took > AvailabilityMaxTicks) AvailabilityMaxTicks = took;
#endif
            }
        }

        /// <summary>
        /// Observation: <see cref="IsAvailableNow"/> calls, their time in Stopwatch ticks summed, and the longest one
        /// (cumulative; a reader takes differences). They advance only while <see cref="DetailTiming"/> is on.
        /// </summary>
        public static long AvailabilityCalls { get; private set; }
        public static long AvailabilityTicks { get; private set; }
        public static long AvailabilityMaxTicks { get; private set; }

        /// <summary>
        /// The plugin's mutex statistics (2026-10-08), cumulative, read without the mutex: the main thread's state
        /// queries (calls, contended, wait and hold in QPC ticks, sums and maxima) and the rendering thread's draw
        /// events (the same), the QPC frequency, and where the last acquisition of each was made -- the thread's id,
        /// the QPC stamp, and the logical processor the thread was on at that instant (an instant's observation: it
        /// says nothing of where the thread ran before or after). They advance only while the plugin's timing is on
        /// (<see cref="timingOn"/>); zeros when the plugin is not loaded.
        /// </summary>
        public struct LockStats
        {
            public ulong availabilityCalls, availabilityContended, availabilityWaitTicks, availabilityWaitMaxTicks, availabilityHeldTicks, availabilityHeldMaxTicks;
            public ulong drawEvents, drawContended, drawWaitTicks, drawWaitMaxTicks, drawHeldTicks, drawHeldMaxTicks;
            public ulong renderThreadId, qpcFrequency;
            public ulong availabilityThreadId, availabilityLastQpc, availabilityLastProcessor, drawLastQpc, drawLastProcessor;
            public bool timingOn;

            public double Ms(ulong ticks) => qpcFrequency > 0 ? ticks * 1000.0 / qpcFrequency : 0.0;
        }

        private const int LockStatValues = 20;

        public static LockStats ReadLockStats()
        {
            var stats = new LockStats();
            if (!s_loaded)
            {
                return stats;
            }

            ulong* values = stackalloc ulong[LockStatValues];
            int n;
            try
            {
                n = ZvnLockStats(values, LockStatValues);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return stats;
            }

            if (n < LockStatValues)
            {
                return stats;
            }

            stats.availabilityCalls = values[0]; stats.availabilityContended = values[1]; stats.availabilityWaitTicks = values[2]; stats.availabilityWaitMaxTicks = values[3];
            stats.availabilityHeldTicks = values[4]; stats.availabilityHeldMaxTicks = values[5];
            stats.drawEvents = values[6]; stats.drawContended = values[7]; stats.drawWaitTicks = values[8]; stats.drawWaitMaxTicks = values[9];
            stats.drawHeldTicks = values[10]; stats.drawHeldMaxTicks = values[11];
            stats.renderThreadId = values[12]; stats.qpcFrequency = values[13];
            stats.availabilityThreadId = values[14]; stats.availabilityLastQpc = values[15]; stats.availabilityLastProcessor = values[16];
            stats.drawLastQpc = values[17]; stats.drawLastProcessor = values[18];
            stats.timingOn = values[19] != 0;
            return stats;
        }

        /// <summary>Before the first draw: regions (frames in flight the rings can hold) and bytes and descriptors per region.</summary>
        public static void ConfigureRings(int regions, int regionBytes, int regionDescriptors)
        {
            ZvnConfigureRings((uint)regions, (uint)regionBytes, (uint)regionDescriptors);
        }

        public static int CreatePipeline(PipelineDesc* desc)
        {
            return ZvnCreatePipeline(desc);
        }

        public static void ReleasePipeline(int pipeline)
        {
            ZvnReleasePipeline(pipeline);
        }

        public static string DescribePipeline(int pipeline)
        {
            byte* buffer = stackalloc byte[4096];
            int n = ZvnDescribePipeline(pipeline, buffer, 4096);
            return Encoding.ASCII.GetString(buffer, Math.Min(n, 4095));
        }

        public static Counters ReadCounters()
        {
            Counters counters;
            ZvnGetCounters(&counters);
            return counters;
        }

        public static void FrameFence(out ulong completed, out ulong next)
        {
            ulong c, n;
            ZvnFrameFence(&c, &n);
            completed = c;
            next = n;
        }

        /// <summary>
        /// Test control (2026-10-08): while held, the plugin's events do their work but withhold their blocks'
        /// consumed marks, so that the "submitted, not consumed" state the managed lifetimes guard exists on a device
        /// whose rendering is not threaded (the Editor), or is widened on one that is. Never on in a product run.
        /// </summary>
        public static void HoldConsumptionForTest(bool hold)
        {
            if (s_loaded) ZvnHoldConsumption(hold ? 1 : 0);
        }

        /// <summary>Gives the withheld consumed marks of the first <paramref name="count"/> held events (all when negative), in the order they ran; how many were given.</summary>
        public static int ReleaseHeldForTest(int count = -1)
        {
            return s_loaded ? ZvnReleaseHeld(count) : 0;
        }

        /// <summary>How many events ran whose consumed mark is withheld.</summary>
        public static int HeldCountForTest()
        {
            return s_loaded ? ZvnHeldCount() : 0;
        }

        public static void WriteName(byte* destination, string value)
        {
            int n = 0;
            if (value != null)
            {
                for (int i = 0; i < value.Length && n < NameLength - 1; i++)
                {
                    char c = value[i];
                    destination[n++] = c < 128 ? (byte)c : (byte)'?';
                }
            }

            for (; n < NameLength; n++)
            {
                destination[n] = 0;
            }
        }

        private delegate int TextFunction(byte* output, int outputSize);

        private static string Text(TextFunction function)
        {
            byte* buffer = stackalloc byte[2048];
            int n = function(buffer, 2048);
            return Encoding.ASCII.GetString(buffer, Math.Max(0, Math.Min(n, 2047)));
        }

        // ---- formats ----------------------------------------------------------------------------------------------

        /// <summary>The DXGI format of a render target view or a sampled view of a colour texture; 0 when not known here.</summary>
        public static uint DxgiFormat(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.R8G8B8A8_SRGB: return 29;
                case GraphicsFormat.R8G8B8A8_UNorm: return 28;
                case GraphicsFormat.B8G8R8A8_SRGB: return 91;
                case GraphicsFormat.B8G8R8A8_UNorm: return 87;
                case GraphicsFormat.R16G16B16A16_SFloat: return 10;
                case GraphicsFormat.R16G16B16A16_UNorm: return 11;
                case GraphicsFormat.R32G32B32A32_SFloat: return 2;
                case GraphicsFormat.A2B10G10R10_UNormPack32: return 24;
                case GraphicsFormat.B10G11R11_UFloatPack32: return 26;
                case GraphicsFormat.R8_UNorm: return 61;
                case GraphicsFormat.R8G8_UNorm: return 49;
                case GraphicsFormat.R16_UNorm: return 56;
                case GraphicsFormat.R16_SFloat: return 54;
                case GraphicsFormat.R16G16_SFloat: return 34;
                case GraphicsFormat.R32_SFloat: return 41;
                case GraphicsFormat.R32_UInt: return 42;
                case GraphicsFormat.RGBA_DXT1_SRGB: return 72;
                case GraphicsFormat.RGBA_DXT1_UNorm: return 71;
                case GraphicsFormat.RGBA_DXT3_SRGB: return 75;
                case GraphicsFormat.RGBA_DXT3_UNorm: return 74;
                case GraphicsFormat.RGBA_DXT5_SRGB: return 78;
                case GraphicsFormat.RGBA_DXT5_UNorm: return 77;
                case GraphicsFormat.R_BC4_UNorm: return 80;
                case GraphicsFormat.RG_BC5_UNorm: return 83;
                case GraphicsFormat.RGB_BC6H_UFloat: return 95;
                case GraphicsFormat.RGB_BC6H_SFloat: return 96;
                case GraphicsFormat.RGBA_BC7_SRGB: return 99;
                case GraphicsFormat.RGBA_BC7_UNorm: return 98;
                default: return 0;
            }
        }

        /// <summary>The DXGI format of a depth stencil view; 0 when not known here.</summary>
        public static uint DxgiDepthFormat(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.D32_SFloat: return 40;
                case GraphicsFormat.D24_UNorm_S8_UInt: return 45;
                case GraphicsFormat.D32_SFloat_S8_UInt: return 20;
                case GraphicsFormat.D16_UNorm: return 55;
                default: return 0;
            }
        }

        /// <summary>The DXGI format a depth texture is sampled through (its depth plane); 0 when not known here.</summary>
        public static uint DxgiDepthSampledFormat(GraphicsFormat format)
        {
            switch (format)
            {
                case GraphicsFormat.D32_SFloat: return 41;            // R32_FLOAT
                case GraphicsFormat.D24_UNorm_S8_UInt: return 46;     // R24_UNORM_X8_TYPELESS
                case GraphicsFormat.D32_SFloat_S8_UInt: return 21;    // R32_FLOAT_X8X24_TYPELESS
                case GraphicsFormat.D16_UNorm: return 56;             // R16_UNORM
                default: return 0;
            }
        }

        /// <summary>The static sampler a texture is sampled with, from its own filter, wrap and anisotropy settings.</summary>
        public static SamplerKind SamplerOf(Texture texture, out int anisotropy)
        {
            anisotropy = texture != null ? Mathf.Max(1, texture.anisoLevel) : 1;
            if (texture == null)
            {
                return SamplerKind.LinearClamp;
            }

            bool wrap = texture.wrapMode == TextureWrapMode.Repeat || texture.wrapMode == TextureWrapMode.Mirror || texture.wrapMode == TextureWrapMode.MirrorOnce;
            switch (texture.filterMode)
            {
                case FilterMode.Point:
                    anisotropy = 1;
                    return SamplerKind.PointClamp;
                case FilterMode.Trilinear:
                    return wrap ? SamplerKind.TrilinearWrap : SamplerKind.TrilinearClamp;
                default:
                    return wrap ? SamplerKind.LinearWrap : SamplerKind.LinearClamp;
            }
        }
    }
}
