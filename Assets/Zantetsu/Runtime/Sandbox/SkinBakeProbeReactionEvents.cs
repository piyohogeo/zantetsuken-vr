using System;
using System.Diagnostics;
using Zantetsu.MeshCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// SKIN-GPU-FIRST-EVAL-1: the probe's record of the product's diagnosis hooks -- the provisional physics publication, the ledger
    /// publication, the DAG commit (by operation) and the deferred bake's collect (by bake request id), each with the frame and the
    /// Stopwatch tick taken inside the hook. Preallocated; a repeated report of the same event is counted, not recorded twice; a full
    /// buffer counts what it could not keep. A request's events are resolved from here at the bench's observations and ONCE MORE before
    /// the rows are written (review 1, P2-4): an event that came after the request's last observation is in the record.
    /// The case verdict is the Commit EVENT (review 2, P2): the bench's observed commit frame stays -1 when the Commit came after the
    /// last observation, and that is not a failure.
    /// </summary>
    internal sealed class SkinBakeProbeReactionEvents
    {
        public enum Kind { Physics = 0, Ledger = 1, Commit = 2, Collect = 3 }

        private struct Event { public CutOperationId operation; public long requestId; public int frame, code; public long tick; }

        private readonly Event[][] _events;
        private readonly int[] _counts = new int[4];

        public int Duplicates { get; private set; }
        public int NotKept { get; private set; }

        public SkinBakeProbeReactionEvents(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _events = new Event[4][];
            for (int k = 0; k < 4; k++) _events[k] = new Event[capacity];
        }

        public int Count(Kind kind) => _counts[(int)kind];

        public void Reset()
        {
            for (int k = 0; k < 4; k++) _counts[k] = 0;
            Duplicates = 0; NotKept = 0;
        }

        /// <summary>A report from a hook: kept once per (kind, operation, request id, code); the tick is taken here, inside the hook.</summary>
        public void Add(Kind kind, CutOperationId operation, long requestId, int frame, int code)
        {
            long tick = Stopwatch.GetTimestamp();
            Event[] into = _events[(int)kind]; int count = _counts[(int)kind];
            for (int i = 0; i < count; i++) if (into[i].requestId == requestId && into[i].code == code && into[i].operation.Equals(operation)) { Duplicates++; return; }
            if (count >= into.Length) { NotKept++; return; }
            into[count] = new Event { operation = operation, requestId = requestId, frame = frame, code = code, tick = tick };
            _counts[(int)kind] = count + 1;
        }

        /// <summary>The event of an operation (physics, ledger or commit), if reported; an unset operation is never resolved.</summary>
        public bool TryResolve(Kind kind, CutOperationId operation, out int frame, out long tick)
        {
            frame = -1; tick = 0;
            if (kind == Kind.Collect || !operation.IsSet) return false;
            Event[] events = _events[(int)kind]; int count = _counts[(int)kind];
            for (int i = 0; i < count; i++) if (events[i].operation.Equals(operation)) { frame = events[i].frame; tick = events[i].tick; return true; }
            return false;
        }

        /// <summary>The collect that ended a bake request, if reported: code = (int)VpSkinBakeCollectOutcome (Completed = the CPU ready; anything else ended it otherwise).</summary>
        public bool TryResolveCollect(long requestId, out int frame, out long tick, out int code)
        {
            frame = -1; tick = 0; code = -1;
            if (requestId == 0) return false;
            Event[] events = _events[(int)Kind.Collect]; int count = _counts[(int)Kind.Collect];
            for (int i = 0; i < count; i++) if (events[i].requestId == requestId) { frame = events[i].frame; tick = events[i].tick; code = events[i].code; return true; }
            return false;
        }

        /// <summary>
        /// The case verdict of a request (review 2, P2): the operation was made and its Commit was REPORTED by the DAG's hook (resolved at an
        /// observation or by the final resolve). The bench's observed commit frame is not the verdict: it stays -1 when the Commit came after
        /// the request's last observation, which is no failure.
        /// </summary>
        public static bool CaseSucceeded(bool operationSet, int committedEventFrame) => operationSet && committedEventFrame >= 0;
    }
}
