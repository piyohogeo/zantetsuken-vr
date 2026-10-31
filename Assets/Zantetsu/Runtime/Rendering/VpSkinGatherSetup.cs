using System;

namespace Zantetsu.Rendering
{
    /// <summary>How the skin draw gathers its bone palettes each frame (DESIGN 4.5.12, SKIN-BENCH-3).</summary>
    public enum VpSkinGatherMode
    {
        /// <summary>On the main thread, within <see cref="VpSkinDisplay.UpdateFrame"/>, as before: the comparison method and the default.</summary>
        Sync = 0,

        /// <summary>
        /// By Burst jobs over TransformAccessArrays, scheduled at the end of the late update once every pose of the frame
        /// is written, completed and published before the frame's first skin draw uploads.
        /// </summary>
        Worker = 1,
    }

    /// <summary>
    /// The launch's choice of the palette gathering (<see cref="VpSkinGatherMode"/>), read once from the Player's
    /// arguments (<see cref="Argument"/> followed by <c>sync</c> or <c>worker</c>); a test or a configuration sets it.
    /// What a display actually did is its own to say (<see cref="VpSkinDisplay.GatherModeActual"/>).
    /// </summary>
    public static class VpSkinGatherSetup
    {
        public const string Argument = "-zantetsuSkinGather";

        /// <summary>
        /// The Player argument of the worker's threshold (SKIN-BENCH-3, TL review): a frame with fewer dirty individuals than
        /// this is gathered on the main thread even under <see cref="VpSkinGatherMode.Worker"/> (the jobs' fixed cost outweighs
        /// the gather below some count: PC 8 updated individuals cost more by the jobs, 64 less). 0 (the default): the jobs
        /// whenever anything is dirty.
        /// </summary>
        public const string WorkerFromArgument = "-zantetsuSkinGatherWorkerFrom";

        private static bool s_read;
        private static VpSkinGatherMode s_mode;
        private static int s_workerFrom;

        /// <summary>Dirty individuals from which the frame goes to the jobs (0: always). Read once; a configuration or a test sets it.</summary>
        public static int WorkerFrom
        {
            get
            {
                Read();
                return s_workerFrom;
            }
            set
            {
                Read();
                s_workerFrom = Math.Max(0, value);
            }
        }

        /// <summary>The threshold a set of arguments asks (<see cref="WorkerFromArgument"/> followed by a whole number 0..100000); 0 when absent or not a number.</summary>
        public static int ReadWorkerFrom(string[] arguments, out string error)
        {
            error = null;
            int value = 0;
            if (arguments == null) return 0;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], WorkerFromArgument, StringComparison.Ordinal)) continue;
                string text = i + 1 < arguments.Length ? arguments[i + 1] : null;
                if (text == null || !int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 0 || value > 100000)
                {
                    error = WorkerFromArgument + " takes a whole number 0..100000, not '" + (text ?? "") + "'";
                    value = 0;
                }
            }

            return value;
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_read = false;
            s_mode = VpSkinGatherMode.Sync;
            s_workerFrom = 0;
        }

        public static VpSkinGatherMode Mode
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

        private static void Read()
        {
            if (s_read) return;
            s_read = true;
            s_mode = ReadMode(Environment.GetCommandLineArgs(), out _);
            s_workerFrom = ReadWorkerFrom(Environment.GetCommandLineArgs(), out _);
        }

        /// <summary>
        /// The mode a set of arguments asks: <see cref="Argument"/> followed by <c>sync</c> or <c>worker</c> (case
        /// insensitive); absent, or followed by anything else, Sync -- with <paramref name="error"/> naming a value that
        /// was not understood (nothing else is changed by it).
        /// </summary>
        public static VpSkinGatherMode ReadMode(string[] arguments, out string error)
        {
            error = null;
            VpSkinGatherMode mode = VpSkinGatherMode.Sync;
            if (arguments == null) return mode;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], Argument, StringComparison.Ordinal)) continue;
                string value = i + 1 < arguments.Length ? arguments[i + 1] : null;
                if (!TryParse(value, out mode)) { error = Argument + " takes sync or worker, not '" + (value ?? "") + "'"; mode = VpSkinGatherMode.Sync; }
            }

            return mode;
        }

        public static bool TryParse(string value, out VpSkinGatherMode mode)
        {
            mode = VpSkinGatherMode.Sync;
            if (string.Equals(value, "sync", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "worker", StringComparison.OrdinalIgnoreCase)) { mode = VpSkinGatherMode.Worker; return true; }
            return false;
        }

        public static string Name(VpSkinGatherMode mode) => mode == VpSkinGatherMode.Worker ? "worker" : "sync";
    }
}
