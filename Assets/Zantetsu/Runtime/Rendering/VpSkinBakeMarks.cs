using System;
using System.Diagnostics;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Diagnosis only (SKIN-GPU-FIRST-STARTUP-1, 2026-11-02): where the GPU-first route spends main-thread time at a hit, as
    /// monotonic Stopwatch ticks -- the shared input's preparation, a snapshot's allocation, the palette upload, the bake's
    /// dispatch recording. <see cref="Mark"/> is null in the product: each site costs a null check. A probe sets it, keeps
    /// what it hears in memory and writes it after its measurement window. The ticks are Stopwatch's (the same clock the
    /// probe's frame marks use); nothing here says anything about the GPU's execution time.
    /// </summary>
    public static class VpSkinBakeMarks
    {
        public static Action<string, long, long> Mark;

        /// <summary>Exceptions a subscriber throws never leave this (review 2, 2026-11-02): a diagnosis must not change what the product did or thinks it did -- the dispatch it is noted after has been recorded whatever the note does.</summary>
        public static void Note(string name, long began)
        {
            Action<string, long, long> mark = Mark;
            if (mark == null) return;
            try { mark(name, began, Stopwatch.GetTimestamp()); }
            catch (Exception) { }
        }
    }
}
