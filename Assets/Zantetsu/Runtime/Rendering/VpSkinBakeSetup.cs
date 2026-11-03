using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>How a prepared character's display input is baked when its cut is accepted (DESIGN 4.5.15).</summary>
    public enum VpSkinBakeMode
    {
        /// <summary>The product's synchronous CPU bake of the 24-byte input on the main thread (the default).</summary>
        Cpu = 0,

        /// <summary>
        /// GPU first: the fixed pose is baked by a compute dispatch into the display's vertex buffer in the hit's frame,
        /// and the CPU geometry the cut needs is made from the same float input by a worker; the cut is admitted when
        /// that CPU geometry is ready.
        /// </summary>
        Gpu = 1,

        /// <summary>
        /// The float-input bake, synchronously on the main thread, uploaded as the CPU bake is: the functional contrast
        /// of the GPU route (the same arithmetic and packing, without the GPU and without the worker).
        /// </summary>
        Float = 2,
    }

    /// <summary>
    /// The launch's choice of the skin bake (<c>-zantetsuSkinBake cpu|gpu|float</c>; nothing means <see cref="VpSkinBakeMode.Cpu"/>,
    /// the product's path as it was). Read once from the command line; a test sets <see cref="Mode"/> directly.
    /// The choice is a world's at its making, written in its <c>VP SKIN BAKE:</c> line; the GPU and float routes fall
    /// back to the CPU route, request by request, where their inputs or resources are refused, and that is recorded.
    /// </summary>
    public static class VpSkinBakeSetup
    {
        public const string Argument = "-zantetsuSkinBake";

        private static bool s_read;
        private static VpSkinBakeMode s_mode;
        private static string s_unrecognised;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_read = false;
            s_mode = VpSkinBakeMode.Cpu;
            s_unrecognised = null;
        }

        /// <summary>The mode asked for (the command line's, or what a test set).</summary>
        public static VpSkinBakeMode Mode
        {
            get
            {
                Read();
                return s_mode;
            }
            set
            {
                Read();
                s_mode = value;
            }
        }

        /// <summary>A value after the argument that named no mode, kept for the world's line; null when none.</summary>
        public static string Unrecognised
        {
            get
            {
                Read();
                return s_unrecognised;
            }
        }

        /// <summary>The mode's name in the launch line and the records: "cpu", "gpu" or "float".</summary>
        public static string Name(VpSkinBakeMode mode)
        {
            switch (mode)
            {
                case VpSkinBakeMode.Gpu: return "gpu";
                case VpSkinBakeMode.Float: return "float";
                default: return "cpu";
            }
        }

        /// <summary>Parses one value of the argument; false for anything but cpu, gpu or float (case does not matter).</summary>
        public static bool TryParse(string value, out VpSkinBakeMode mode)
        {
            mode = VpSkinBakeMode.Cpu;
            if (string.IsNullOrEmpty(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "cpu": mode = VpSkinBakeMode.Cpu; return true;
                case "gpu": mode = VpSkinBakeMode.Gpu; return true;
                case "float": mode = VpSkinBakeMode.Float; return true;
                default: return false;
            }
        }

        /// <summary>Reads the command line's choice: the last occurrence of the argument with a value wins.</summary>
        public static void ReadArguments(string[] arguments)
        {
            s_read = true;
            s_mode = VpSkinBakeMode.Cpu;
            s_unrecognised = null;
            if (arguments == null) return;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], Argument, StringComparison.Ordinal)) continue;
                string value = i + 1 < arguments.Length ? arguments[i + 1] : null;
                if (TryParse(value, out VpSkinBakeMode mode))
                {
                    s_mode = mode;
                    s_unrecognised = null;
                }
                else
                {
                    s_unrecognised = value ?? "(no value)";
                }
            }
        }

        private static void Read()
        {
            if (s_read) return;
            string[] arguments;
            try { arguments = Environment.GetCommandLineArgs(); }
            catch (Exception) { arguments = null; }
            ReadArguments(arguments);
        }
    }
}
