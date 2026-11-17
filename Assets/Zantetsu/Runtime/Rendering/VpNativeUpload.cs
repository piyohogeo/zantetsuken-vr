using System;
using UnityEngine;
using UnityEngine.Rendering;
using Plugin = Zantetsu.Rendering.VpNativeDrawPlugin;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The Direct3D 12 plugin's own write into a buffer Unity made (DESIGN 4.5.8, D-261), in place of
    /// <see cref="GraphicsBuffer.SetData(System.Array)"/>, for the buffers the plugin's draws bind and the CPU writes
    /// every frame: a batch's instance transforms and clips.
    /// <para>
    /// **Why.** Unity's contract is that a write through its APIs may change a buffer's native pointer, so the route
    /// asks the pointer again after every such write (<see cref="VpNativePointerCache"/>), and that call waits for the
    /// rendering thread. A buffer written by the plugin alone is never written through Unity's APIs: its pointer
    /// stands for the buffer's life, by the same contract, and nothing is asked again.
    /// </para>
    /// <para>
    /// **How.** <see cref="Write"/> hands the bytes to the plugin, which copies them at once; <see cref="Submit"/>
    /// seals what was handed over and issues the plugin's upload event at once (Graphics.ExecuteCommandBuffer), where
    /// SetData would have been called -- so the write stands in the rendering thread's order exactly where SetData's
    /// would. The event copies the bytes into the buffer in the command list Unity is recording, between resource
    /// states asked through Unity. An owner that writes a buffer this way writes it this way **always**: a write
    /// through Unity's API in between would bring back the pointer question, and the order of the two kinds of write
    /// is nothing this relies on.
    /// </para>
    /// <para>
    /// **Where.** The Direct3D 12 library with the upload, loaded; nowhere else (<see cref="Ready"/>). The Player
    /// argument <see cref="UnityUploadsArgument"/> keeps Unity's SetData there too: the comparison's other side, and
    /// the way back.
    /// </para>
    /// </summary>
    public static unsafe class VpNativeUpload
    {
        /// <summary>The Player argument that keeps GraphicsBuffer.SetData for every write (and the pointers asked again after each).</summary>
        public const string UnityUploadsArgument = "-zantetsuVpNativeUnityUploads";

        private static readonly Unity.Profiling.ProfilerMarker SubmitMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.NativeUpload.Submit");
        private static bool? s_asked;
        private static CommandBuffer s_commands;

        /// <summary>Whether the plugin's own write is asked for: true unless the launch said <see cref="UnityUploadsArgument"/>, or a caller (a test) set it.</summary>
        public static bool Asked
        {
            get
            {
                if (!s_asked.HasValue) s_asked = ReadRequest(Environment.GetCommandLineArgs());
                return s_asked.Value;
            }
            set => s_asked = value;
        }

        /// <summary>What <see cref="Asked"/> answers for a set of arguments. For tests of the reading itself.</summary>
        public static bool ReadRequest(string[] arguments)
        {
            return arguments == null || Array.IndexOf(arguments, UnityUploadsArgument) < 0;
        }

        /// <summary>
        /// Whether a buffer made now is to be written by the plugin: asked for, and the loaded library has the upload
        /// (the Direct3D 12 one, on a Direct3D 12 device). Read when the owner is made and kept for the owner's life.
        /// Loads nothing: whoever makes a display the plugin draws has loaded the library before.
        /// </summary>
        public static bool Ready => Asked && Plugin.UploadEventId != 0;

        /// <summary>Observation: writes handed over, their bytes, the events issued, and writes the plugin did not take.</summary>
        public static long Writes { get; private set; }
        public static long Bytes { get; private set; }
        public static long Events { get; private set; }
        public static long Refused { get; private set; }

        /// <summary>
        /// Test control: this many of the next writes are not taken, as when the plugin's device is gone, so that a
        /// test can see what an owner does with a write the plugin did not take. Never set in a product run.
        /// </summary>
        internal static int RefuseNext { get; set; }

        /// <summary>
        /// Hands one write to the plugin: <paramref name="bytes"/> bytes at <paramref name="source"/>, to stand
        /// <paramref name="byteOffset"/> bytes into the buffer of this native pointer. Copied before this returns.
        /// Nothing reaches the buffer before <see cref="Submit"/>. False when the plugin took nothing (its device is
        /// gone): the caller then has no way to write this buffer but Unity's -- and a write through Unity's API may
        /// change the buffer's native pointer, so the caller asks it again before the next write it hands over here.
        /// </summary>
        public static bool Write(IntPtr nativeBuffer, long byteOffset, void* source, long bytes)
        {
            bool refuse = RefuseNext > 0;
            if (refuse) RefuseNext--;
            if (refuse || nativeBuffer == IntPtr.Zero || !Plugin.UploadWrite(nativeBuffer, byteOffset, source, bytes))
            {
                Refused++;
                return false;
            }

            Writes++;
            Bytes += bytes;
            return true;
        }

        /// <summary>
        /// Puts every write handed over since the last call in the rendering thread's order, here: seals them and
        /// issues the upload event. Does nothing when there are none.
        /// </summary>
        /// <remarks>
        /// The event is a Graphics.ExecuteCommandBuffer of its own. Leaving it to the next rendering's start instead
        /// (one event in the commands that rendering submits anyway) was built and measured on 2026-11-11 and is not
        /// adopted: the Release Player's main thread time did not change, and it obliges every other reader of the
        /// buffers to flush first (DESIGN.md 4.5.8).
        /// </remarks>
        public static void Submit()
        {
            using Unity.Profiling.ProfilerMarker.AutoScope marked = SubmitMarker.Auto();
            int frame = FrameForTest ?? Time.frameCount;
            if (frame != s_frame)
            {
                s_frame = frame;
                s_eventsThisFrame = 0;
                s_gapWaitedThisFrame = false;
                NoteWatch(frame);
            }

            if (!s_watchSet) ApplyWatch();

            ulong sequence = Plugin.UploadSeal((ulong)Math.Max(frame, 0));
            if (sequence == 0) return;

            // The measurement's wait (GapArgument): between the frame's first event and its second, both already in
            // the order they will be written in. Nothing else is changed by it.
            if (GapMilliseconds > 0 && s_eventsThisFrame > 0 && !s_gapWaitedThisFrame)
            {
                s_gapWaitedThisFrame = true;
                long until = System.Diagnostics.Stopwatch.GetTimestamp() + GapMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
                GapWaits++;
            }

            if (s_commands == null) s_commands = new CommandBuffer { name = "VP native upload" };
            s_commands.Clear();

            // The data is the batch's number, which the plugin reads as a number and never as an address.
            s_commands.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.UploadEventId, (IntPtr)(long)sequence);
            Graphics.ExecuteCommandBuffer(s_commands);
            Events++;
            s_eventsThisFrame++;
        }

        // ---- the watch on the library's upload pages (2026-11-18) ------------------------------------------------

        /// <summary>
        /// The Player argument that has the library prove what a page reused too early did: the writes of a main frame
        /// are kept through the next, and when a page is taken as free in the main frame it was copied from or the
        /// one after, what those writes left in their buffers is read back and compared with what they were to write.
        /// Without it the library still counts such reuses (and the frame fence value changing within a main frame),
        /// and this side still records them. (2026-11-18: the comparison proved the fault under Link, and the library
        /// now holds a page back until the fence has passed its stamp by a margin; the watch stays to see that fail.)
        /// </summary>
        public const string WatchArgument = "-zantetsuVpNativeUploadWatch";

        /// <summary>
        /// The Player argument of a measurement, followed by whole milliseconds (1..<see cref="GapLimitMilliseconds"/>):
        /// the main thread spins that long between a frame's first upload event and its second, and a batch's
        /// transforms and clips go as an event each so that a frame that writes has two. It widens, on purpose, the
        /// time between two events of one frame -- the condition under which a page stamped with a frame fence value
        /// that is one behind would be reused while its copies are not yet submitted.
        /// </summary>
        public const string GapArgument = "-zantetsuVpNativeUploadGap";

        public const int GapLimitMilliseconds = 100;

        private static bool s_measurementRead, s_watch, s_watchSet, s_reuseAtOnce, s_gapWaitedThisFrame;
        private static int s_gapMilliseconds, s_frame = -1, s_eventsThisFrame;
        private static Plugin.UploadWatch s_watchSeen;
        private static int s_differingNotes, s_reuseNotes, s_changeNotes;

        /// <summary>Whether the library compares (<see cref="WatchArgument"/>, or a caller set it).</summary>
        public static bool Watch
        {
            get { ReadMeasurement(); return s_watch; }
            set { ReadMeasurement(); s_watch = value; ApplyWatch(); }
        }

        // The library outlives a reload of this side in the Editor: its switches are set, never assumed -- at once
        // when it is loaded, else at the first submit (nothing is submitted before it is).
        private static void ApplyWatch()
        {
            ReadMeasurement();
            s_watchSet = Plugin.UploadEventId != 0;
            Plugin.SetUploadWatch(s_watch, s_reuseAtOnce);
        }

        /// <summary>The measurement's wait in milliseconds; 0 (the default) is none. See <see cref="GapArgument"/>.</summary>
        public static int GapMilliseconds
        {
            get { ReadMeasurement(); return s_gapMilliseconds; }
            set { ReadMeasurement(); s_gapMilliseconds = Mathf.Clamp(value, 0, GapLimitMilliseconds); }
        }

        /// <summary>Observation: how many times the measurement's wait was made.</summary>
        public static long GapWaits { get; private set; }

        /// <summary>Observation: how many times this side recorded something the watch saw (a warning each; later ones are thinned).</summary>
        public static long WatchNotes { get; private set; }

        /// <summary>Test control: the library takes every page as free at once -- the fault the watch is there to see. Never set in a product run.</summary>
        internal static bool ReuseAtOnceForTest
        {
            get => s_reuseAtOnce;
            set { s_reuseAtOnce = value; ApplyWatch(); }
        }

        /// <summary>Test control: the frame number <see cref="Submit"/> goes by, in place of Time.frameCount (which stands still within an EditMode test).</summary>
        internal static int? FrameForTest { get; set; }

        private static void ReadMeasurement()
        {
            if (s_measurementRead) return;
            s_measurementRead = true;
            string[] arguments = Environment.GetCommandLineArgs();
            s_watch = Array.IndexOf(arguments, WatchArgument) >= 0;
            s_gapMilliseconds = ReadGap(arguments, out string error);
            if (error != null) Debug.LogWarning("VP native upload: " + error);
        }

        /// <summary>What <see cref="GapMilliseconds"/> starts as for a set of arguments: the whole number after <see cref="GapArgument"/>, 0 without it; an error (and 0) when it is not 1..<see cref="GapLimitMilliseconds"/>.</summary>
        public static int ReadGap(string[] arguments, out string error)
        {
            error = null;
            int value = 0;
            if (arguments == null) return 0;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], GapArgument, StringComparison.Ordinal)) continue;
                string text = i + 1 < arguments.Length ? arguments[i + 1] : null;
                if (text == null || !int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 1 || value > GapLimitMilliseconds)
                {
                    error = GapArgument + " takes whole milliseconds 1.." + GapLimitMilliseconds + ", not '" + (text ?? "") + "'";
                    value = 0;
                }
            }

            return value;
        }

        // Once a frame, at its first submit: what the library's watch has seen since the last look goes into the log
        // at the frame it was noticed in. The counts are read without the library's lock; its account in words (which
        // waits for the lock) is asked only when something is recorded. Each kind is thinned on its own, so that a
        // run in which the fence value changes every frame still records the first page reused and every difference.
        private static void NoteWatch(int frame)
        {
            if (!Plugin.TryReadUploadWatch(out Plugin.UploadWatch now)) return;
            bool differing = now.differing != s_watchSeen.differing;
            bool reuse = now.reuses != s_watchSeen.reuses;
            bool change = now.stampChanges != s_watchSeen.stampChanges;
            s_watchSeen = now;
            bool record = differing ? Due(ref s_differingNotes, 64) : reuse ? Due(ref s_reuseNotes, 32) : change && Due(ref s_changeNotes, 8);
            if (!record) return;
            WatchNotes++;
            Debug.LogWarning("VP native upload watch, frame " + frame + ": "
                + (differing ? "A WRITE'S BYTES DIFFER FROM WHAT WAS HANDED OVER" : reuse ? "a page was reused within a main frame of its last copy" : "the frame fence value changed within a main frame")
                + ": " + now + "; " + Plugin.UploadWatchText());
        }

        // The first `first` of a kind are recorded, then the ones whose number is a power of two.
        private static bool Due(ref int count, int first)
        {
            count++;
            return count <= first || (count & (count - 1)) == 0;
        }

        /// <summary>This side's counts and the plugin's own account, for a record.</summary>
        public static string Describe()
        {
            string text = "plugin uploads " + (Ready ? "on" : Asked ? "off (the library has none)" : "off (" + UnityUploadsArgument + ")")
                + ": writes " + Writes + " (" + Bytes + " bytes), events " + Events + ", not taken " + Refused;
            if (GapMilliseconds > 0) text += ", measurement's wait " + GapMilliseconds + " ms between a frame's first two events (" + GapArgument + "): made " + GapWaits + " times";
            if (Plugin.TryReadUploadInfo(out Plugin.UploadInfo info)) text += ", by the library: " + info;
            if (Plugin.TryReadUploadWatch(out Plugin.UploadWatch watch))
            {
                text += ", page watch (" + (Watch ? WatchArgument : "counts only; " + WatchArgument + " compares") + "): " + watch + " [" + Plugin.UploadWatchText() + "]";
            }

            return text;
        }
    }
}
