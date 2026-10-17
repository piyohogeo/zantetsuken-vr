using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// MEASUREMENT (unit 8-C, 2026-10-17): every GPU upload of the display's own buffers, by call site -- how many
    /// calls, how many bytes, the largest single call -- so that the uploads a frame really makes can be told apart
    /// from what the code could make: the whole-buffer paths, the changed-range paths, the registration and growth
    /// paths. The extension methods forward to <see cref="GraphicsBuffer.SetData(System.Array)"/> and its range
    /// forms unchanged and add the count; the site is the file and line of the call. Bytes are the SOURCE elements'
    /// size times their count (what is transferred), never the buffer's stride (a uint array sent into a 20-byte
    /// argument buffer is 4 bytes an element). Counting is on only when the Player was started with
    /// <see cref="Argument"/>, decided at subsystem registration -- before any scene and so before the first upload;
    /// a normal launch counts nothing (the methods forward and return). Reading the tallies is the readout's.
    /// </summary>
    public static class VpUploadMeter
    {
        /// <summary>The Player argument that turns the counting on (the readout's argument too).</summary>
        public const string Argument = "-zantetsuGpuMemory";

        public sealed class Site
        {
            public long Calls, Bytes, MaxBytes, WindowCalls, WindowBytes, WindowMaxBytes;
        }

        private static readonly Dictionary<string, Site> s_sites = new Dictionary<string, Site>();

        /// <summary>Whether uploads are counted: decided before the first upload from the arguments; tests set it.</summary>
        public static bool Enabled { get; private set; }

        public static long TotalCalls { get; private set; }
        public static long TotalBytes { get; private set; }

        public static bool AskedFor(string[] arguments) => arguments != null && Array.IndexOf(arguments, Argument) >= 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Configure()
        {
            Reset();
            Enabled = AskedFor(Environment.GetCommandLineArgs());
        }

        /// <summary>For tests: turns the counting on or off and forgets the tallies.</summary>
        public static void SetEnabledForTest(bool enabled)
        {
            Reset();
            Enabled = enabled;
        }

        public static void Note(string site, long bytes)
        {
            if (!Enabled) return;
            if (!s_sites.TryGetValue(site, out Site s))
            {
                s = new Site();
                s_sites.Add(site, s);
            }

            s.Calls++; s.Bytes += bytes; if (bytes > s.MaxBytes) s.MaxBytes = bytes;
            s.WindowCalls++; s.WindowBytes += bytes; if (bytes > s.WindowMaxBytes) s.WindowMaxBytes = bytes;
            TotalCalls++; TotalBytes += bytes;
        }

        // Every overload: the stop stages (VpGpuStop.Upload) decide whether the upload is made at all -- a frame upload
        // from stage 2 once the registration is over, an initial one from stage 3 -- and the count is of what was made.
        // Without a kind the upload is a frame's; the display's whole writes, transfers and first sends say Initial.

        public static void SetData<T>(this GraphicsBuffer buffer, T[] data, string site, VpUploadKind kind = VpUploadKind.Frame) where T : struct
        {
            long bytes = (long)data.Length * UnsafeUtility.SizeOf<T>();
            if (!VpGpuStop.Upload(kind, bytes)) return;
            buffer.SetData(data);
            if (Enabled) Note(site, bytes);
        }

        public static void SetData<T>(this GraphicsBuffer buffer, T[] data, int managedBufferStartIndex, int graphicsBufferStartIndex, int count, string site, VpUploadKind kind = VpUploadKind.Frame) where T : struct
        {
            long bytes = (long)count * UnsafeUtility.SizeOf<T>();
            if (!VpGpuStop.Upload(kind, bytes)) return;
            buffer.SetData(data, managedBufferStartIndex, graphicsBufferStartIndex, count);
            if (Enabled) Note(site, bytes);
        }

        public static void SetData<T>(this GraphicsBuffer buffer, NativeArray<T> data, string site, VpUploadKind kind = VpUploadKind.Frame) where T : struct
        {
            long bytes = (long)data.Length * UnsafeUtility.SizeOf<T>();
            if (!VpGpuStop.Upload(kind, bytes)) return;
            buffer.SetData(data);
            if (Enabled) Note(site, bytes);
        }

        public static void SetData<T>(this GraphicsBuffer buffer, NativeArray<T> data, int nativeBufferStartIndex, int graphicsBufferStartIndex, int count, string site, VpUploadKind kind = VpUploadKind.Frame) where T : struct
        {
            long bytes = (long)count * UnsafeUtility.SizeOf<T>();
            if (!VpGpuStop.Upload(kind, bytes)) return;
            buffer.SetData(data, nativeBufferStartIndex, graphicsBufferStartIndex, count);
            if (Enabled) Note(site, bytes);
        }

        public static void SetData<T>(this GraphicsBuffer buffer, List<T> data, string site, VpUploadKind kind = VpUploadKind.Frame) where T : struct
        {
            long bytes = (long)data.Count * UnsafeUtility.SizeOf<T>();
            if (!VpGpuStop.Upload(kind, bytes)) return;
            buffer.SetData(data);
            if (Enabled) Note(site, bytes);
        }

        /// <summary>How many sites have been seen.</summary>
        public static int SiteCount => s_sites.Count;

        /// <summary>A site's tallies (null when it never uploaded).</summary>
        public static Site Get(string site) => s_sites.TryGetValue(site, out Site s) ? s : null;

        /// <summary>
        /// The tallies of EVERY site that uploaded since the last snapshot (the window), one line each, largest window
        /// bytes first: "site calls/MB/maxMB (all calls/MB/maxMB)"; the first line is the header with the totals.
        /// Resets every site's window. Nothing is dropped: a site that uploaded once appears in the window it did.
        /// </summary>
        public static List<string> Snapshot()
        {
            var list = new List<KeyValuePair<string, Site>>(s_sites);
            list.Sort((a, b) => b.Value.WindowBytes.CompareTo(a.Value.WindowBytes));
            var lines = new List<string> { "sites=" + list.Count + " totalCalls=" + TotalCalls + " totalMB=" + Mb(TotalBytes) };
            foreach (var pair in list)
            {
                Site s = pair.Value;
                if (s.WindowCalls > 0)
                {
                    lines.Add(pair.Key + " " + s.WindowCalls + "/" + Mb(s.WindowBytes) + "MB/max" + Mb(s.WindowMaxBytes) + "MB (all " + s.Calls + "/" + Mb(s.Bytes) + "MB/max" + Mb(s.MaxBytes) + "MB)");
                }

                s.WindowCalls = 0; s.WindowBytes = 0; s.WindowMaxBytes = 0;
            }

            return lines;
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F3", CultureInfo.InvariantCulture);

        /// <summary>Forgets every tally (the enabled state stays).</summary>
        public static void Reset()
        {
            s_sites.Clear();
            TotalCalls = 0;
            TotalBytes = 0;
        }
    }
}
