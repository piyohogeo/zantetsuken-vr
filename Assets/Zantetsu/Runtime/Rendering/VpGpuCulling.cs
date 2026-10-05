using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One draw command as the GPU selection reads it (VpInstanceCull.compute): the index range it draws, the run of
    /// instances it draws it at, and the bounds of that range in the geometry's own frame. Made on the CPU from a
    /// <see cref="VpIndirectCommand"/> and sent whenever the commands are; the selection makes the indirect arguments
    /// from it, so in a batch that selects on the GPU this record takes the place of the two argument buffers' upload.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpCullCommand
    {
        /// <summary>The size of one record on the GPU, in bytes.</summary>
        public const int Stride = 40;

        public uint indexCount;
        public uint startIndex;
        public uint startInstance;
        public uint instanceCount;
        public Vector3 centre;
        public Vector3 extents;
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

        /// <summary>The compute shader's name under a Resources folder.</summary>
        public const string ShaderResource = "VpInstanceCull";

        /// <summary>The shader keyword of the variants that read the selection's list.</summary>
        public const string Keyword = "VP_GPU_CULLED";

        private static bool s_argumentsRead;
        private static bool s_requested = true;
        private static bool s_sliceSelection = true;

        private VpGpuCullSetup(ComputeShader shader, int kernel, int viewCapacity)
        {
            Shader = shader;
            Kernel = kernel;
            ViewCapacity = viewCapacity;
        }

        public ComputeShader Shader { get; }

        public int Kernel { get; }

        public int ViewCapacity { get; }

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
            Read(Environment.GetCommandLineArgs(), out s_requested, out s_sliceSelection);
        }

        /// <summary>
        /// The setup for <paramref name="viewCapacity"/> views. False, with the reason, when the device has no compute
        /// shaders, the compute shader is not in the build, or its kernel is not found.
        /// </summary>
        public static bool TryCreate(int viewCapacity, out VpGpuCullSetup setup, out string failure)
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

            if (!shader.HasKernel("CullCommands"))
            {
                failure = "the compute shader '" + ShaderResource + "' has no kernel CullCommands";
                return false;
            }

            setup = new VpGpuCullSetup(shader, shader.FindKernel("CullCommands"), viewCapacity);
            return true;
        }
    }
}
