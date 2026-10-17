using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>What an upload is for the stop stages: data of a frame, or the display's initial data (whole writes, geometry transfers, a batch's first whole send).</summary>
    public enum VpUploadKind
    {
        Frame = 0,
        Initial = 1,
    }

    /// <summary>
    /// DIAGNOSIS ONLY (unit 8-C, 2026-10-17): stops the display's GPU work in stages, from launch, to find the least
    /// that still grows the GPU memory at an XR session restart. Each stage keeps the earlier stages stopped:
    /// 1 no draw submission (the display's colour, shadow casters, motion through the same registration, clip list,
    ///   stencil) and the scene's own renderers held off from the first frame (<see cref="SuppressRenderers"/>: the
    ///   inventory still lists them);
    /// 2 no per-frame work OF THE VP DISPLAY either once the registration is over: the display's frame update is not
    ///   entered (no collection, no camera preparation, no cull request), the cull dispatch and its parameter binding are
    ///   not recorded, the clip selection's command writes and the per-frame uploads are not made. The physics step, the
    ///   placement, XR, URP and the readout go on;
    /// 3 no initial uploads either (whole zero writes, geometry transfers, first whole sends, the registration's data),
    ///   the buffers kept at their sizes;
    /// 4 no allocation either: no display buffer is made (<see cref="Buffer"/> gives none), the registration is not
    ///   attempted, and the run goes on to its measurement wait with XR, URP and the readout alone.
    /// The CPU side otherwise runs unchanged: stopping a component would release what must stay. Every decision is
    /// tallied so a run proves its condition. Stage 0 (the default) changes nothing and tallies what it sees.
    /// Set by the Player argument "-zantetsuGpuStop N" (read before anything is made) or by a host that reads a
    /// configuration file at BeforeSceneLoad (QuestStaticDemo). Never a product option.
    /// </summary>
    public static class VpGpuStop
    {
        public const string Argument = "-zantetsuGpuStop";
        public const int None = 0, NoDraws = 1, NoFrameWork = 2, NoInitialUploads = 3, NoAllocations = 4;

        /// <summary>The stage in force (0..4).</summary>
        public static int Stage { get; private set; }

        /// <summary>Told by the host when the display's registration (initial showing) is over: the frame update and the per-frame uploads after it are frame work.</summary>
        public static bool RegistrationDone { get; private set; }

        public static bool SkipDraws => Stage >= NoDraws;
        public static bool SkipFrameWork => Stage >= NoFrameWork;
        /// <summary>The display's frame update (collection, camera preparation, cull request) is not entered: stage 2 once the registration is over.</summary>
        public static bool SkipDisplayUpdate => SkipFrameWork && RegistrationDone;
        public static bool SkipInitialUploads => Stage >= NoInitialUploads;
        /// <summary>No display buffer is made and no registration attempted (stage 4).</summary>
        public static bool WithholdAllocations => Stage >= NoAllocations;

        // The tallies (process-wide, from launch).
        public static long DrawsIssued, DrawsSkipped;
        public static long DisplayUpdatesEntered, DisplayUpdatesSkipped;
        public static long DispatchesIssued, DispatchesSkipped;
        public static long CommandWritesIssued, CommandWritesSkipped;
        public static long FrameUploadsIssued, FrameUploadsSkipped, FrameUploadBytesIssued, FrameUploadBytesSkipped;
        public static long InitialUploadsIssued, InitialUploadsSkipped, InitialUploadBytesIssued, InitialUploadBytesSkipped;
        public static long AllocationsMade, AllocationsWithheld, AllocationBytesMade, AllocationBytesWithheld;

        private static readonly HashSet<Renderer> s_suppressed = new HashSet<Renderer>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Configure()
        {
            Reset();
            Set(Read(Environment.GetCommandLineArgs()));
        }

        /// <summary>The stage a set of arguments asks ("-zantetsuGpuStop N"); 0 when absent or malformed.</summary>
        public static int Read(string[] arguments)
        {
            if (arguments == null) return None;
            int at = Array.IndexOf(arguments, Argument);
            if (at < 0 || at + 1 >= arguments.Length) return None;
            return int.TryParse(arguments[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int stage) && stage >= None && stage <= NoAllocations ? stage : None;
        }

        /// <summary>Sets the stage (clamped to 0..4). A host sets it before the display is made; a later set changes only what follows.</summary>
        public static void Set(int stage)
        {
            Stage = Math.Max(None, Math.Min(NoAllocations, stage));
        }

        /// <summary>The host says the registration is over (the runner, after its last TryShow; at stage 4 at once, as none is attempted).</summary>
        public static void NoteRegistrationDone()
        {
            RegistrationDone = true;
        }

        /// <summary>For tests: stage 0, registration not done, every tally 0, the suppressed renderers given back.</summary>
        public static void Reset()
        {
            Stage = None;
            RegistrationDone = false;
            DrawsIssued = DrawsSkipped = DisplayUpdatesEntered = DisplayUpdatesSkipped = 0;
            DispatchesIssued = DispatchesSkipped = CommandWritesIssued = CommandWritesSkipped = 0;
            FrameUploadsIssued = FrameUploadsSkipped = FrameUploadBytesIssued = FrameUploadBytesSkipped = 0;
            InitialUploadsIssued = InitialUploadsSkipped = InitialUploadBytesIssued = InitialUploadBytesSkipped = 0;
            AllocationsMade = AllocationsWithheld = AllocationBytesMade = AllocationBytesWithheld = 0;
            RestoreSuppressedRenderers();
        }

        // ---- the scene's own renderers (stage 1) ------------------------------------------------------------------

        /// <summary>
        /// Holds the given renderers off from drawing (forceRenderingOff) while remembering them, so that the inventory
        /// still takes them as targets (<see cref="IsSuppressedByStop"/>): what is held and what is listed are kept
        /// apart. Nothing at stage 0. Returns how many were newly held off.
        /// </summary>
        public static int SuppressRenderers(IEnumerable<Renderer> renderers)
        {
            if (!SkipDraws || renderers == null) return 0;
            int held = 0;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer.forceRenderingOff || !s_suppressed.Add(renderer)) continue;
                renderer.forceRenderingOff = true;
                held++;
            }

            return held;
        }

        /// <summary>Every renderer of the loaded scenes, active or not, held off (stage 1); how many.</summary>
        public static int SuppressSceneRenderers()
        {
            if (!SkipDraws) return 0;
            return SuppressRenderers(UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None));
        }

        /// <summary>Whether this renderer's forceRenderingOff is the stop's (and so no reason to leave it out of the inventory).</summary>
        public static bool IsSuppressedByStop(Renderer renderer) => renderer != null && s_suppressed.Contains(renderer);

        public static int SuppressedRendererCount => s_suppressed.Count;

        /// <summary>For tests and a reset: the held renderers draw again.</summary>
        public static void RestoreSuppressedRenderers()
        {
            foreach (Renderer renderer in s_suppressed)
            {
                if (renderer != null) renderer.forceRenderingOff = false;
            }

            s_suppressed.Clear();
        }

        // ---- the decisions, tallied ------------------------------------------------------------------------------

        /// <summary>Whether a draw submission is to be made now; tallied either way.</summary>
        public static bool Draw()
        {
            if (SkipDraws) { DrawsSkipped++; return false; }
            DrawsIssued++;
            return true;
        }

        /// <summary>Whether the display's frame update is to be entered now (collection and what follows it); tallied either way.</summary>
        public static bool DisplayUpdate()
        {
            if (SkipDisplayUpdate) { DisplayUpdatesSkipped++; return false; }
            DisplayUpdatesEntered++;
            return true;
        }

        /// <summary>Whether a compute dispatch (its parameter binding included) is to be recorded now; tallied either way.</summary>
        public static bool Dispatch()
        {
            if (SkipFrameWork) { DispatchesSkipped++; return false; }
            DispatchesIssued++;
            return true;
        }

        /// <summary>Whether a command-buffer data write is to be made now; tallied either way.</summary>
        public static bool CommandWrite()
        {
            if (SkipFrameWork) { CommandWritesSkipped++; return false; }
            CommandWritesIssued++;
            return true;
        }

        /// <summary>
        /// Whether an upload of <paramref name="bytes"/> is to be made now. Initial data is skipped from stage 3; frame
        /// data from stage 2 once the registration is over (during the registration it is the initial data of the bodies
        /// shown, kept to stage 2) and from stage 3 always. Tallied either way.
        /// </summary>
        public static bool Upload(VpUploadKind kind, long bytes)
        {
            bool initial = kind == VpUploadKind.Initial || !RegistrationDone;
            bool skip = initial ? SkipInitialUploads : SkipFrameWork;
            if (initial)
            {
                if (skip) { InitialUploadsSkipped++; InitialUploadBytesSkipped += bytes; } else { InitialUploadsIssued++; InitialUploadBytesIssued += bytes; }
            }
            else
            {
                if (skip) { FrameUploadsSkipped++; FrameUploadBytesSkipped += bytes; } else { FrameUploadsIssued++; FrameUploadBytesIssued += bytes; }
            }

            return !skip;
        }

        /// <summary>A display buffer, or NONE (null) at stage 4: the bytes asked are tallied either way. Callers hold a null as "not made".</summary>
        public static GraphicsBuffer Buffer(GraphicsBuffer.Target target, int count, int stride, string name = null)
        {
            long bytes = (long)count * stride;
            if (WithholdAllocations)
            {
                AllocationsWithheld++;
                AllocationBytesWithheld += bytes;
                return null;
            }

            AllocationsMade++;
            AllocationBytesMade += bytes;
            var buffer = new GraphicsBuffer(target, count, stride);
            if (name != null) buffer.name = name;
            return buffer;
        }

        /// <summary>The same with usage flags.</summary>
        public static GraphicsBuffer Buffer(GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usage, int count, int stride, string name = null)
        {
            long bytes = (long)count * stride;
            if (WithholdAllocations)
            {
                AllocationsWithheld++;
                AllocationBytesWithheld += bytes;
                return null;
            }

            AllocationsMade++;
            AllocationBytesMade += bytes;
            var buffer = new GraphicsBuffer(target, usage, count, stride);
            if (name != null) buffer.name = name;
            return buffer;
        }

        private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>The stage and every tally, for the log.</summary>
        public static string Describe()
        {
            return "gpuStop=" + Stage + " registrationDone=" + RegistrationDone + " renderersHeld=" + s_suppressed.Count
                + " draws=" + DrawsIssued + "/skipped " + DrawsSkipped
                + " displayUpdates=" + DisplayUpdatesEntered + "/skipped " + DisplayUpdatesSkipped
                + " dispatches=" + DispatchesIssued + "/skipped " + DispatchesSkipped
                + " commandWrites=" + CommandWritesIssued + "/skipped " + CommandWritesSkipped
                + " frameUploads=" + FrameUploadsIssued + " (" + Mb(FrameUploadBytesIssued) + " MB)/skipped " + FrameUploadsSkipped + " (" + Mb(FrameUploadBytesSkipped) + " MB)"
                + " initialUploads=" + InitialUploadsIssued + " (" + Mb(InitialUploadBytesIssued) + " MB)/skipped " + InitialUploadsSkipped + " (" + Mb(InitialUploadBytesSkipped) + " MB)"
                + " allocations=" + AllocationsMade + " (" + Mb(AllocationBytesMade) + " MB)/withheld " + AllocationsWithheld + " (" + Mb(AllocationBytesWithheld) + " MB)";
        }
    }
}
