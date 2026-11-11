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
        public static void Submit()
        {
            ulong sequence = Plugin.UploadSeal();
            if (sequence == 0) return;
            if (s_commands == null) s_commands = new CommandBuffer { name = "VP native upload" };
            s_commands.Clear();

            // The data is the batch's number, which the plugin reads as a number and never as an address.
            s_commands.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.UploadEventId, (IntPtr)(long)sequence);
            Graphics.ExecuteCommandBuffer(s_commands);
            Events++;
        }

        /// <summary>This side's counts and the plugin's own account, for a record.</summary>
        public static string Describe()
        {
            string text = "plugin uploads " + (Ready ? "on" : Asked ? "off (the library has none)" : "off (" + UnityUploadsArgument + ")")
                + ": writes " + Writes + " (" + Bytes + " bytes), events " + Events + ", not taken " + Refused;
            return Plugin.TryReadUploadInfo(out Plugin.UploadInfo info) ? text + ", by the library: " + info : text;
        }
    }
}
