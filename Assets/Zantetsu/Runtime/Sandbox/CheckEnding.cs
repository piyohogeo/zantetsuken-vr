using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The ending a check run goes through (2026-09-29): its summary parts, each guarded so that one part's failure is
    /// recorded as a failure of the run and stops nothing else, then the world's reclaim on the ordinary frames. The
    /// Player check's Walk ends through this; a test ends a world of its own through the same code, so that what the
    /// test exercises is what the check does.
    /// </summary>
    internal sealed class CheckEnding
    {
        /// <summary>One named part of the ending: a summary, a trace's close, a last log line.</summary>
        public readonly struct Part
        {
            public readonly string what;
            public readonly Action run;

            public Part(string what, Action run)
            {
                this.what = what;
                this.run = run;
            }
        }

        private readonly Action<string> _log;
        private readonly List<string> _failedParts = new List<string>();

        public CheckEnding(Action<string> log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>The failures this ending counted: parts that threw, and a world that did not end within its cap.</summary>
        public int Failures { get; private set; }

        /// <summary>The names of the parts that threw, in order.</summary>
        public IReadOnlyList<string> FailedParts => _failedParts;

        /// <summary>Whether <see cref="Run"/> saw the world released.</summary>
        public bool WorldReleased { get; private set; }

        /// <summary>
        /// Runs one part so that its failure is logged as FAILED and counted, but stops nothing else: the later parts, the
        /// saving of what is already gathered, the ending and the reclaim go on. True when the part ran through.
        /// </summary>
        public bool Guarded(string what, Action part)
        {
            if (TryRun(what, part, _log))
            {
                return true;
            }

            Failures++;
            _failedParts.Add(what);
            return false;
        }

        /// <summary>The guard itself, for a caller that keeps its own count: the FAILED line names the part, the exception and where it was thrown.</summary>
        public static bool TryRun(string what, Action part, Action<string> log)
        {
            try
            {
                part();
                return true;
            }
            catch (Exception e)
            {
                log("FAILED: summary part '" + what + "' threw " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace);
                return false;
            }
        }

        /// <summary>
        /// The ending: every part in order, each guarded, then the world asked to end and carried on the ordinary frames
        /// until it is released or the cap passes. A world that does not end within the cap is a failure, logged as one.
        /// What comes after (the code, the timeline, the held log's release) is the caller's, so that a caller without a
        /// timeline ends the same way up to there.
        /// </summary>
        public IEnumerator Run(CutWorldRoot world, IReadOnlyList<Part> parts, float reclaimCapSeconds)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            foreach (Part part in parts)
            {
                Guarded(part.what, part.run);
            }

            float endBy = Time.realtimeSinceStartup + reclaimCapSeconds;
            world.Shutdown();
            while (!world.IsReleased && Time.realtimeSinceStartup < endBy)
            {
                yield return null;
                world.Shutdown();
            }

            WorldReleased = world.IsReleased;
            if (!WorldReleased)
            {
                Failures++;
            }

            _log((WorldReleased ? "ok: " : "FAILED: ") + "the world ended the ordinary way and gave everything back");
        }

        /// <summary>The run's code: 0 for none failed, 18 for a replay that began before the world was ready, 14 for any failure.</summary>
        public static int CodeOf(int failures, bool replayBeforeReady) => failures == 0 ? (replayBeforeReady ? 18 : 0) : 14;

        /// <summary>The code <see cref="Complete"/> settled on, or -1 before it ran.</summary>
        public int Code { get; private set; } = -1;

        /// <summary>Whether <see cref="Complete"/> reached its <c>done</c>.</summary>
        public bool Done { get; private set; }

        /// <summary>
        /// The completion, after the world's reclaim: every closing step in order (the records' close, the timeline's
        /// save, a view target's release), each guarded so that one step's failure is recorded as FAILED and counted and
        /// the later steps still run; then the code, from the failures the caller counts plus those of the steps
        /// (<paramref name="codeOf"/> gets the steps' count); the code's line; the held log's release, guarded as well;
        /// and <paramref name="done"/> with the code, reached whatever the steps did -- the run's completion notice is
        /// never lost to a closing failure.
        /// </summary>
        public void Complete(IReadOnlyList<Part> steps, Action releaseLog, Func<int, int> codeOf, Action<int> done)
        {
            int before = Failures;
            int code = 14;
            try
            {
                foreach (Part step in steps)
                {
                    Guarded(step.what, step.run);
                }

                code = codeOf(Failures - before);
                _log("finished with code " + code);
            }
            finally
            {
                Code = code;
                Guarded("log release", releaseLog);
                Done = true;
                done?.Invoke(code);
            }
        }
    }
}
