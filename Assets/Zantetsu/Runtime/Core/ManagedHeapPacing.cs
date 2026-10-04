using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Scripting;

namespace Zantetsu.Core
{
    /// <summary>
    /// The adopted automatic collector uses a 1 ms slice in a Player. This is not an upper bound on a GC pause.
    /// The Editor is left unchanged; engine and auto arguments retain the existing comparison settings.
    /// </summary>
    public static class ManagedHeapPacing
    {
        public enum Way { Engine, Automatic }
        public const ulong AdoptedSliceNanoseconds = 1000000UL;
        public const string SliceArgument = "-zantetsuGcSliceMs";
        public const string WayArgument = "-zantetsuGc";

        public static ulong SliceNanosecondsFor(string[] arguments)
        {
            return double.TryParse(ValueOf(arguments, SliceArgument), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double milliseconds) && milliseconds >= 0.05 && milliseconds <= 10.0
                ? (ulong)Math.Round(milliseconds * 1e6)
                : AdoptedSliceNanoseconds;
        }

        public static ulong? SliceToSetFor(string[] arguments)
        {
            switch (WayFor(arguments))
            {
                case Way.Engine: return null;
                default: return SliceNanosecondsFor(arguments);
            }
        }

        public static Way WayFor(string[] arguments)
        {
            switch (ValueOf(arguments, WayArgument))
            {
                case "engine": return Way.Engine;
                default: return Way.Automatic;
            }
        }

        private static string ValueOf(string[] arguments, string name)
        {
            int at = arguments != null ? Array.IndexOf(arguments, name) : -1;
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ApplyAtStart()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            Way s_way = WayFor(arguments);
            string s_started;
            ulong before = GarbageCollector.incrementalTimeSliceNanoseconds;
            if (Application.isEditor)
            {
                s_started = "managed heap: the Editor's collector is left as it is (way " + s_way + " not applied)";
                Debug.Log(s_started);
                return;
            }

            ulong? slice = SliceToSetFor(arguments);
            if (slice.HasValue)
            {
                GarbageCollector.incrementalTimeSliceNanoseconds = slice.Value;
            }

            s_started = "managed heap: way " + s_way + "; incremental collector " + (GarbageCollector.isIncremental ? "on" : "OFF") + ", mode " + GarbageCollector.GCMode
                + ", time slice " + (before / 1e6).ToString("F3", CultureInfo.InvariantCulture) + " ms -> "
                + (GarbageCollector.incrementalTimeSliceNanoseconds / 1e6).ToString("F3", CultureInfo.InvariantCulture) + " ms";
            Debug.Log(s_started);
        }
    }
}
