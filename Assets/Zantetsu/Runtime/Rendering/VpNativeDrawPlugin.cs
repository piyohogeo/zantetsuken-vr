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
        public const string Library = "ZantetsuVpNative";
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
            }

            failure = s_failure;
            return s_loaded;
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

        /// <summary>
        /// Whether the plugin can run events now: false once the device is gone, after which it touches no block
        /// (so the blocks of events that will never run may be freed).
        /// </summary>
        public static bool IsAvailableNow()
        {
            try
            {
                return s_loaded && ZvnIsAvailable() != 0;
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return false;
            }
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
