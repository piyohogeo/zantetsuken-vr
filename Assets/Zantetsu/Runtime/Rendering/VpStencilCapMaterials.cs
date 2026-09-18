using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The materials one stencil colour's three draws are issued with, and the render queues that put them in order.
    /// <para>
    /// **The order is the queue.** DESIGN 5.6 requires that within a colour the stencil byte is set to 128, then every
    /// volume is counted, then every cap is drawn, and that the colours follow one another. The pipeline draws opaque
    /// work in render queue order, so each of the three draws of each colour is given its own queue value in exactly
    /// that sequence: colour 0's initialisation, then its volumes, then its caps, then colour 1's, and so on. That is
    /// why a colour needs materials of its own rather than sharing one — a render queue belongs to a material.
    /// </para>
    /// <para>
    /// **All of them are opaque queues**, at or below <see cref="LastOpaqueQueue"/>, which is what keeps the whole
    /// sequence inside the pipeline's opaque pass and against the camera's own colour, depth and stencil attachment.
    /// This is the arrangement chosen for this fixed-input confirmation; it is not a claim about what other
    /// arrangements could do.
    /// </para>
    /// <para>
    /// **One aggregate batch per camera.** Because the order is nothing but the queue, two sets of these registering
    /// draws for the same camera would sit at the same queue values and their initialisations, volumes and caps would
    /// interleave — a colour's byte set to 128 in the middle of another's counting. This is for a single aggregate
    /// batch per camera. Nothing here reserves queues, arbitrates them, or knows about anyone else's.
    /// </para>
    /// <para>
    /// The materials are made here and destroyed here; a set that cannot be finished destroys what it had made. Fixed
    /// count, main thread only.
    /// </para>
    /// </summary>
    public sealed class VpStencilCapMaterials : IDisposable
    {
        public const string InitShaderName = "Zantetsu/VP Stencil Init";
        public const string VolumeShaderName = "Zantetsu/VP Stencil Volume";
        public const string CapShaderName = "Zantetsu/VP Stencil Cap";

        /// <summary>
        /// The first queue of the sequence. Opaque geometry is 2000; this sits above the ordinary opaque work so that
        /// the bodies and the scene are already drawn when the stencil starts.
        /// </summary>
        public const int FirstQueue = 2450;

        /// <summary>
        /// The last queue that is still drawn as opaque. Above it the pipeline treats work as transparent, which would
        /// take it out of the pass this sequence shares an attachment in.
        /// </summary>
        public const int LastOpaqueQueue = 2500;

        /// <summary>How many queue values one colour uses: the initialisation, the volumes and the caps.</summary>
        public const int QueuesPerColor = 3;

        /// <summary>
        /// How many colours fit between <see cref="FirstQueue"/> and <see cref="LastOpaqueQueue"/> inclusive. A count
        /// above this is refused rather than allowed to run past the opaque range.
        /// </summary>
        public static int MaxColors => (LastOpaqueQueue - FirstQueue + 1) / QueuesPerColor;

        private readonly Material[] _init;
        private readonly Material[] _volume;
        private readonly Material[] _cap;
        private bool _disposed;

        private VpStencilCapMaterials(Material[] init, Material[] volume, Material[] cap)
        {
            _init = init;
            _volume = volume;
            _cap = cap;
        }

        public int ColorCount => _init.Length;

        /// <summary>
        /// Makes one material set per colour from the three shaders, with the queues that order them. Returns false,
        /// having made nothing, when a shader is missing or the count would run the queues past the opaque range. The
        /// comparison is against how many colours fit, not an addition that could carry, and a set that fails partway
        /// destroys what it had already made.
        /// </summary>
        public static bool TryCreate(int colorCount, out VpStencilCapMaterials materials)
        {
            materials = null;
            if (colorCount <= 0 || colorCount > MaxColors)
            {
                return false;
            }

            Shader init = Shader.Find(InitShaderName);
            Shader volume = Shader.Find(VolumeShaderName);
            Shader cap = Shader.Find(CapShaderName);
            if (init == null || volume == null || cap == null)
            {
                return false;
            }

            var inits = new Material[colorCount];
            var volumes = new Material[colorCount];
            var caps = new Material[colorCount];
            VpStencilCapMaterials made;
            try
            {
                for (int c = 0; c < colorCount; c++)
                {
                    int first = FirstQueue + (c * QueuesPerColor);

                    // Each material goes into the array before it is given a name or a queue: an object initialiser
                    // does not assign until it has finished, so a throw partway through one would leave a material
                    // nothing could destroy.
                    inits[c] = new Material(init);
                    inits[c].name = "VP Stencil Init " + c;
                    inits[c].renderQueue = first;

                    volumes[c] = new Material(volume);
                    volumes[c].name = "VP Stencil Volume " + c;
                    volumes[c].renderQueue = first + 1;

                    caps[c] = new Material(cap);
                    caps[c].name = "VP Stencil Cap " + c;
                    caps[c].renderQueue = first + 2;
                }

                made = new VpStencilCapMaterials(inits, volumes, caps);
            }
            catch
            {
                DestroyAll(inits, volumes, caps);
                throw;
            }

            materials = made;
            return true;
        }

        /// <summary>The material that sets this colour's stencil byte to 128, first of the colour's three.</summary>
        public Material Init(int color) => _init[color];

        /// <summary>The material this colour's volumes are counted with, after the initialisation.</summary>
        public Material Volume(int color) => _volume[color];

        /// <summary>The material this colour's caps are drawn with, after every volume of the colour.</summary>
        public Material Cap(int color) => _cap[color];

        /// <summary>The render queue of one colour's one draw, in the order init, volume, cap.</summary>
        public int QueueOf(int color, int stage) => FirstQueue + (color * QueuesPerColor) + stage;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DestroyAll(_init, _volume, _cap);
        }

        private static void DestroyAll(Material[] init, Material[] volume, Material[] cap)
        {
            for (int c = 0; c < init.Length; c++)
            {
                Destroy(init[c]);
                Destroy(volume[c]);
                Destroy(cap[c]);
            }
        }

        private static void Destroy(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(material);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
