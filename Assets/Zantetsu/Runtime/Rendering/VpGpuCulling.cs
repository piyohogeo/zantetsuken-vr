using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One draw command as the GPU selection reads it (VpInstanceCull.compute): the index range it draws, the run of
    /// instances it draws it at, and the bounds of that range in the geometry's own frame. Made on the CPU from a
    /// <see cref="VpIndirectCommand"/> and sent whenever the commands are; the selection makes the indirect arguments
    /// from it, so in a batch that selects on the GPU this record takes the place of the two argument buffers' upload.
    /// <para>
    /// <see cref="flags"/> stands where the bounds' centre is padded to four components: a draw's shader that reads
    /// the record for its bounds declares a float there and does not read it.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpCullCommand
    {
        /// <summary>The size of one record on the GPU, in bytes.</summary>
        public const int Stride = 48;

        /// <summary>
        /// <see cref="flags"/>: the command is drawn by its owner's clip route (<see cref="VpIndirectCommand.clipRoute"/>,
        /// DESIGN 4.5.9). The selection goes through none of its instances and writes its arguments with a count of
        /// zero, for the body and for the casters alike.
        /// </summary>
        public const uint ClipRouteFlag = 1u;

        public uint indexCount;
        public uint startIndex;
        public uint startInstance;
        public uint instanceCount;
        public Vector3 centre;
        public uint flags;
        public Vector3 extents;
        public float pad1;
    }

    /// <summary>
    /// What one view asks of the GPU selection (DESIGN 4.5.7): the frustum planes of the eyes the body is drawn for, and
    /// the culling planes of the shadow splits the casters are drawn into. Planes hold an inward normal in xyz and a
    /// distance in w; a point is inside when <c>dot(n, x) + d &gt;= 0</c>. The body keeps what may intersect either eye,
    /// the casters what may intersect any split. An eye count of zero keeps every instance for the body, and a split
    /// count of zero keeps every instance as a caster: that is how a view with nothing known about it is asked for.
    /// <para>
    /// The arrays are made once and filled each time, so asking allocates nothing.
    /// </para>
    /// </summary>
    public sealed class VpCullConditions
    {
        public const int EyeCapacity = 2;
        public const int EyePlaneCount = 6;
        public const int SplitCapacity = 4;
        public const int SplitPlaneCapacity = 10;

        /// <summary>The planes of eye e are [e * <see cref="EyePlaneCount"/>, (e + 1) * EyePlaneCount).</summary>
        public readonly Vector4[] eyePlanes = new Vector4[EyeCapacity * EyePlaneCount];

        /// <summary>The planes of split s start at s * <see cref="SplitPlaneCapacity"/>.</summary>
        public readonly Vector4[] shadowPlanes = new Vector4[SplitCapacity * SplitPlaneCapacity];

        /// <summary>How many planes each split holds, one component per split.</summary>
        public Vector4 shadowPlaneCounts;

        public int eyeCount;
        public int shadowSplitCount;

        /// <summary>Asks for nothing to be removed: every instance is drawn and every instance casts.</summary>
        public void KeepEverything()
        {
            eyeCount = 0;
            shadowSplitCount = 0;
            shadowPlaneCounts = Vector4.zero;
        }

        /// <summary>
        /// Sets eye <paramref name="eye"/>'s six planes from its view and projection, the projection being the
        /// camera's own (<see cref="Camera.projectionMatrix"/>'s convention, clip z in [-w, w]), not the device's.
        /// </summary>
        public void SetEye(int eye, Matrix4x4 view, Matrix4x4 projection)
        {
            if (eye < 0 || eye >= EyeCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(eye));
            }

            Matrix4x4 m = projection * view;
            Vector4 x = m.GetRow(0);
            Vector4 y = m.GetRow(1);
            Vector4 z = m.GetRow(2);
            Vector4 w = m.GetRow(3);
            int first = eye * EyePlaneCount;
            eyePlanes[first + 0] = Normalised(w + x);
            eyePlanes[first + 1] = Normalised(w - x);
            eyePlanes[first + 2] = Normalised(w + y);
            eyePlanes[first + 3] = Normalised(w - y);
            eyePlanes[first + 4] = Normalised(w + z);
            eyePlanes[first + 5] = Normalised(w - z);
        }

        /// <summary>Sets plane <paramref name="index"/> of split <paramref name="split"/>.</summary>
        public void SetShadowPlane(int split, int index, Plane plane)
        {
            if (split < 0 || split >= SplitCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(split));
            }

            if (index < 0 || index >= SplitPlaneCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            Vector3 normal = plane.normal;
            shadowPlanes[split * SplitPlaneCapacity + index] = new Vector4(normal.x, normal.y, normal.z, plane.distance);
        }

        // A plane with a normal of no length removes nothing: it is left as it is, and the test's radius and distance
        // are then both of that same scale.
        private static Vector4 Normalised(Vector4 plane)
        {
            float length = Mathf.Sqrt(plane.x * plane.x + plane.y * plane.y + plane.z * plane.z);
            return length > 0f ? plane / length : plane;
        }
    }

    /// <summary>
    /// What draws through a batch that selects on the GPU, as whoever puts the selection into the frame sees it: asked,
    /// while a camera's frame is being built, to issue that camera's selection into a command buffer that will be
    /// executed before the camera's shadow maps and colour are drawn.
    /// </summary>
    public interface IVpGpuCullTarget
    {
        /// <summary>
        /// Gives an ordinary display this camera's single-slice shadow condition before its draws are registered.
        /// True allows its Vulkan VS to omit slice judgement. Skin displays retain their own bounds and slice policy.
        /// </summary>
        void SetSingleShadowSlice(Camera camera, bool singleSlice);

        /// <summary>
        /// Issues the selection for the draws registered for <paramref name="camera"/> in this frame. False, issuing
        /// nothing, when no draw was registered for that camera in this frame.
        /// </summary>
        bool IssueCull(UnityEngine.Rendering.CommandBuffer commands, Camera camera, VpCullConditions conditions);
    }

    /// <summary>
    /// What a <see cref="VpIndexedIndirectDrawBatch"/> needs to select its instances on the GPU (DESIGN 4.5.7): the
    /// compute shader, and how many views it keeps results for. A view is one camera's: its lists and its arguments are
    /// written only by that camera's dispatch and read only by that camera's draws, so no camera's selection is
    /// another's.
    /// <para>
    /// **The default, settled at start-up and never switched (D-198).** A world draws its bodies through the GPU
    /// selection unless the Player was started with <see cref="Vp3Argument"/>, which is the way back to VP Stage 3.
    /// <see cref="Requested"/> is that choice; a world reads it once, when it makes its display. A device or a pipeline
    /// that cannot select is told by <see cref="TryCreate"/>'s failure: the world then draws as VP Stage 3 and says so
    /// in the log.
    /// </para>
    /// </summary>
    public sealed class VpGpuCullSetup
    {
        /// <summary>
        /// The Player argument that draws the bodies as VP Stage 3, with no GPU selection: the way back from the
        /// default. It wins over <see cref="Argument"/> when both are given.
        /// </summary>
        public const string Vp3Argument = "-zantetsuVp3";

        /// <summary>
        /// The Player argument that asked for the GPU selection while VP Stage 3 was the default. The selection is the
        /// default now, so it changes nothing; it is still accepted, so that a launcher written before does what it did.
        /// </summary>
        public const string Argument = "-zantetsuVp3cGpuCull";

        /// <summary>
        /// The Player argument that leaves the casters' per-slice selection out of the shadow caster's vertex stage,
        /// for comparing with and without it. It changes nothing about which casters the compute pass keeps.
        /// </summary>
        public const string NoSliceSelectionArgument = "-zantetsuVp3cNoSliceSelection";

        /// <summary>
        /// The Player argument that turns the per-slice selection on for the skin draw's casters on Vulkan (SKIN-BENCH-3),
        /// where the ordinary (16-byte) casters keep it off: their judgement reads the command's bounds by a number the
        /// Vulkan multi-draw cannot give, the skin draw's reads the instance's own bounds. Off unless given; nothing
        /// without <see cref="ShadowSliceSelection"/>.
        /// </summary>
        public const string Skin24SliceSelectionArgument = "-zantetsuSkin24SliceSelection";

        /// <summary>Explicitly selects each shadow cascade in compute on the native route.</summary>
        public const string ComputeShadowSlicesArgument = "-zantetsuVp3cComputeShadowSlices";
        /// <summary>Requests the shadow vertex stage's cascade judgement, including a single Vulkan shadow slice.</summary>
        public const string VertexShadowSlicesArgument = "-zantetsuVp3cVertexShadowSlices";
        public const string ComputeShadowSlicesKeyword = "VP_COMPUTE_SHADOW_SLICES";

        /// <summary>The compute shader's name under a Resources folder.</summary>
        public const string ShaderResource = "VpInstanceCull";

        /// <summary>The shader keyword of the variants that read the selection's list.</summary>
        public const string Keyword = "VP_GPU_CULLED";

        /// <summary>
        /// Start-up argument: clipped instances stay in the selection's own draws, which then evaluate the clip
        /// record for every instance, as before the separation (DESIGN 4.5.9). For comparing the two in one build.
        /// </summary>
        public const string ClipTogetherArgument = "-zantetsuVp3cClipTogether";

        /// <summary>The variant of an ordinary instance's draw: no clip buffer, no clip arithmetic, no clip output.</summary>
        public const string NoClipKeyword = "VP_NO_CLIP";

        /// <summary>
        /// The variant of the clip list's shadow draw by Unity's own call (DESIGN 4.5.9): the vertex stage passes over
        /// an entry whose bounds lie outside the slice being rendered, as the selection's casters do, reading the
        /// entry's bounds by the entry's own number. It changes no output, so it is the vertex stage's alone.
        /// </summary>
        public const string ClipListSliceKeyword = "VP_CLIP_LIST_SLICES";

        private static bool s_argumentsRead;
        private static bool s_requested = true;
        private static bool s_sliceSelection = true;
        private static bool s_skin24SliceSelectionOnVulkan;
        private static bool s_separateClip = true;
        private static bool s_computeShadowSlices;
        private static bool s_vertexShadowSlices;

        /// <summary>The argument entry Unity's own indirect draw reads: GraphicsBuffer.IndirectDrawIndexedArgs.</summary>
        public const int UnityArgumentStride = 20;

        /// <summary>
        /// The argument entry the Direct3D 12 plugin's command signature reads (DESIGN 4.5.8): the command's number,
        /// then the five indexed arguments. Unity's own draw cannot read these entries.
        /// </summary>
        public const int NativeArgumentStride = 24;

        private VpGpuCullSetup(ComputeShader shader, int kernel, int viewCapacity, bool nativeArguments, bool instanceBounds)
        {
            Shader = shader;
            Kernel = kernel;
            ViewCapacity = viewCapacity;
            NativeArguments = nativeArguments;
            InstanceBounds = instanceBounds;
            _separateClipRequested = SeparateClipRequested;
        }

        /// <summary>The kernel of a selection by each instance's own bounds (<see cref="InstanceBounds"/>).</summary>
        public const string InstanceBoundsKernel = "CullInstanceBounds";

        /// <summary>
        /// Whether the selection judges each instance by bounds of its own, read from the batch's instance bounds
        /// buffer (<see cref="VpIndexedIndirectDrawBatch.InstanceBounds"/>; the skin draw's, written on the GPU from
        /// the instance's pose, DESIGN 4.5.12), rather than by its command's bounds carried by its transform. Settled
        /// when the setup is made.
        /// </summary>
        public bool InstanceBounds { get; }

        public ComputeShader Shader { get; }

        public int Kernel { get; }

        public int ViewCapacity { get; }

        /// <summary>
        /// Whether the selection writes the plugin's argument entries (<see cref="NativeArgumentStride"/>) rather than
        /// Unity's. Settled when the setup is made: a batch made with it draws through the plugin alone.
        /// </summary>
        public bool NativeArguments { get; }

        /// <summary>
        /// Whether a display made with this setup draws its clipped instances apart from the selection (DESIGN 4.5.9):
        /// <see cref="SeparateClipRequested"/> as it stood when the setup was made, fixed for the setup's life.
        /// </summary>
        public bool SeparateClip => _separateClipRequested && ClipSeparationUnavailable == null;

        private readonly bool _separateClipRequested;

        /// <summary>
        /// Why whatever will draw a display made with this setup cannot draw clipped instances apart (DESIGN 4.5.9),
        /// or null when it can. Set, before the display is made, by whoever knows the route: a plugin route whose
        /// variants for it are not in the build. A display made with it then draws as before the separation -- every
        /// instance through the selection, with its clip record, by materials that evaluate it -- and says why.
        /// </summary>
        public string ClipSeparationUnavailable { get; set; }

        /// <summary>Bytes per argument entry the selection writes.</summary>
        public int ArgumentStride => NativeArguments ? NativeArgumentStride : UnityArgumentStride;

        /// <summary>Bytes into an entry at which the five indexed arguments begin.</summary>
        public int ArgumentOffset => NativeArguments ? 4 : 0;

        /// <summary>
        /// Whether a world is to draw its bodies through the GPU selection: true unless the Player's arguments say
        /// VP Stage 3 (<see cref="Vp3Argument"/>), or as a test set it.
        /// </summary>
        public static bool Requested
        {
            get
            {
                ReadArguments();
                return s_requested;
            }
            set
            {
                ReadArguments();
                s_requested = value;
            }
        }

        /// <summary>
        /// Whether the shadow caster's vertex stage passes over an instance that lies outside the slice being
        /// rendered. On unless the Player was started with <see cref="NoSliceSelectionArgument"/>.
        /// </summary>
        public static bool ShadowSliceSelection
        {
            get
            {
                ReadArguments();
                return s_sliceSelection;
            }
            set
            {
                ReadArguments();
                s_sliceSelection = value;
            }
        }

        /// <summary>
        /// Whether the native route selects each shadow cascade in compute. On by default on Direct3D 12 when
        /// slice selection is enabled. <see cref="VertexShadowSlicesArgument"/> selects the vertex judgement.
        /// </summary>
        public static bool ComputeShadowSlices
        {
            get
            {
                ReadArguments();
                return s_computeShadowSlices && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 && s_sliceSelection;
            }
            set { ReadArguments(); s_computeShadowSlices = value; }
        }

        /// <summary>Whether the launch explicitly retains the vertex stage's cascade judgement.</summary>
        public static bool VertexShadowSlicesRequested
        {
            get { ReadArguments(); return s_vertexShadowSlices; }
        }

        public static bool ReadComputeShadowSlices(string[] arguments)
        {
            return ReadComputeShadowSlices(arguments, SystemInfo.graphicsDeviceType);
        }

        public static bool ReadComputeShadowSlices(string[] arguments, GraphicsDeviceType device)
        {
            return device == GraphicsDeviceType.Direct3D12
                && (arguments == null || (Array.IndexOf(arguments, VertexShadowSlicesArgument) < 0
                    && Array.IndexOf(arguments, NoSliceSelectionArgument) < 0));
        }

        /// <summary>
        /// Whether the setups made from now on draw clipped instances apart (the default), read once from the command
        /// line (<see cref="ClipTogetherArgument"/> turns it off); a test sets it and puts it back.
        /// </summary>
        public static bool SeparateClipRequested
        {
            get
            {
                ReadArguments();
                return s_separateClip;
            }
            set
            {
                ReadArguments();
                s_separateClip = value;
            }
        }

        /// <summary>Whether <paramref name="arguments"/> leave the separation on: every case but <see cref="ClipTogetherArgument"/> given.</summary>
        public static bool ReadSeparateClip(string[] arguments)
        {
            return arguments == null || Array.IndexOf(arguments, ClipTogetherArgument) < 0;
        }

        /// <summary>
        /// Whether the skin draw's casters on Vulkan pass over the instances outside the slice being rendered
        /// (<see cref="Skin24SliceSelectionArgument"/>; SKIN-BENCH-3). The ordinary casters on Vulkan never do; on
        /// Direct3D 12 both follow <see cref="ShadowSliceSelection"/>. A test sets it and puts it back.
        /// </summary>
        public static bool Skin24SliceSelectionOnVulkan
        {
            get
            {
                ReadArguments();
                return s_skin24SliceSelectionOnVulkan;
            }
            set
            {
                ReadArguments();
                s_skin24SliceSelectionOnVulkan = value;
            }
        }

        /// <summary>Whether <paramref name="arguments"/> ask for the skin casters' slice selection on Vulkan.</summary>
        public static bool ReadSkin24SliceSelection(string[] arguments)
        {
            return arguments != null && Array.IndexOf(arguments, Skin24SliceSelectionArgument) >= 0;
        }

        /// <summary>
        /// The slice selection a caster issue's constants carry (SKIN-BENCH-3): 1 when the vertex stage passes over
        /// instances outside the slice. An ordinary (16-byte) caster follows the global switch, except on Vulkan where
        /// it is always 0 (its judgement would read command-zero bounds); the skin draw's caster follows the switch,
        /// and on Vulkan only with <paramref name="skin24OnVulkan"/> too.
        /// </summary>
        public static float CasterSliceSelectionFor(bool skinIssue, bool vulkan, bool sliceSelection, bool skin24OnVulkan)
        {
            if (!sliceSelection) return 0f;
            if (!vulkan) return 1f;
            return skinIssue && skin24OnVulkan ? 1f : 0f;
        }

        /// <summary>
        /// The context of a caster issue's shared GPU constant blocks (the plugin's route, SKIN-BENCH-3): the split and the
        /// slice selection its constants carry, so that two issues of one split with different selections (an ordinary run
        /// at 0, a skin run at 1 on Vulkan) never share a block.
        /// </summary>
        public static int CasterConstantContext(int split, float sliceSelection) => split * 2 + (sliceSelection > 0.5f ? 1 : 0);

        /// <summary>What the two properties answer for a set of arguments. For tests of the reading itself.</summary>
        public static void Read(string[] arguments, out bool requested, out bool sliceSelection)
        {
            requested = true;
            sliceSelection = true;
            if (arguments == null)
            {
                return;
            }

            for (int i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], Vp3Argument, StringComparison.Ordinal))
                {
                    requested = false;
                }
                else if (string.Equals(arguments[i], NoSliceSelectionArgument, StringComparison.Ordinal))
                {
                    sliceSelection = false;
                }
            }
        }

        private static void ReadArguments()
        {
            if (s_argumentsRead)
            {
                return;
            }

            s_argumentsRead = true;
            string[] arguments = Environment.GetCommandLineArgs();
            Read(arguments, out s_requested, out s_sliceSelection);
            s_separateClip = ReadSeparateClip(arguments);
            s_skin24SliceSelectionOnVulkan = ReadSkin24SliceSelection(arguments);
            s_computeShadowSlices = ReadComputeShadowSlices(arguments);
            s_vertexShadowSlices = Array.IndexOf(arguments, VertexShadowSlicesArgument) >= 0;
        }

        /// <summary>
        /// The setup for <paramref name="viewCapacity"/> views. False, with the reason, when the device has no compute
        /// shaders, the compute shader is not in the build, or its kernel is not found.
        /// </summary>
        public static bool TryCreate(int viewCapacity, out VpGpuCullSetup setup, out string failure)
        {
            return TryCreate(viewCapacity, false, out setup, out failure);
        }

        /// <summary>
        /// The same setup writing the plugin's argument entries when <paramref name="nativeArguments"/> is true (see
        /// <see cref="NativeArguments"/>).
        /// </summary>
        public static bool TryCreate(int viewCapacity, bool nativeArguments, out VpGpuCullSetup setup, out string failure)
        {
            return TryCreate(viewCapacity, nativeArguments, false, out setup, out failure);
        }

        /// <summary>The same setup selecting by each instance's own bounds when <paramref name="instanceBounds"/> is true (see <see cref="InstanceBounds"/>).</summary>
        public static bool TryCreate(int viewCapacity, bool nativeArguments, bool instanceBounds, out VpGpuCullSetup setup, out string failure)
        {
            setup = null;
            failure = null;
            if (viewCapacity <= 0)
            {
                failure = "no view to select for (view capacity " + viewCapacity + ")";
                return false;
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                failure = "the device has no compute shaders";
                return false;
            }

            ComputeShader shader = Resources.Load<ComputeShader>(ShaderResource);
            if (shader == null)
            {
                failure = "the compute shader '" + ShaderResource + "' is not in the build";
                return false;
            }

            string kernelName = instanceBounds ? InstanceBoundsKernel : "CullCommands";
            if (!shader.HasKernel(kernelName))
            {
                failure = "the compute shader '" + ShaderResource + "' has no kernel " + kernelName;
                return false;
            }

            setup = new VpGpuCullSetup(shader, shader.FindKernel(kernelName), viewCapacity, nativeArguments, instanceBounds);
            return true;
        }
    }
}
