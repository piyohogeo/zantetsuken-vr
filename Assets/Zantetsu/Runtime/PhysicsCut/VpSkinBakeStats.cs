using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What the skin bake routes did in this process (DESIGN 4.5.15): every hit's record -- its route, its main-thread
    /// time at the hit (the whole TryCut, the acceptance included, in every route), and for the GPU-first route the frame
    /// its CPU bake was collected in, that collect's main-thread time (the storage's completion, the region's return, the
    /// DAG's registration), the worker's bake time -- the fallbacks by reason, the bakes that failed or adopted nothing,
    /// the most bakes pending at once. Cheap counters and a bounded list of records; read by the world's end line and the
    /// benches. Reset with each world.
    /// </summary>
    public static class VpSkinBakeStats
    {
        public struct Record
        {
            public long requestId;
            public string route;        // "gpu", "float", "cpu"
            public int vertexCount, boneCount;
            public int hitFrame, readyFrame;
            public double hitMainSeconds, readyMainSeconds, workerSeconds, kernelSeconds;
            public int dispatches;
            public string acceptance;
            public string basisOutcome;  // gpu: "pending", "ready", "failed: ...", "dropped: ..."
        }

        public const int RecordCapacity = 4096;
        private static readonly List<Record> s_records = new List<Record>();
        private static readonly Dictionary<long, int> s_recordOfRequest = new Dictionary<long, int>();
        private static readonly Dictionary<string, int> s_fallbacks = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> s_failures = new Dictionary<string, int>();

        public static int Begun { get; private set; }
        public static int Ready { get; private set; }
        public static int BasisFailed { get; private set; }
        public static int Dropped { get; private set; }
        public static int Fallbacks { get; private set; }
        public static int FailedAtBegin { get; private set; }
        public static int FloatSyncs { get; private set; }
        public static int CpuSyncs { get; private set; }
        public static int Pending { get; private set; }
        public static int MaxPending { get; private set; }
        public static int RecordsNotKept { get; private set; }
        public static IReadOnlyList<Record> Records => s_records;
        public static IReadOnlyDictionary<string, int> FallbackReasons => s_fallbacks;
        public static IReadOnlyDictionary<string, int> FailureReasons => s_failures;

        public static void Reset()
        {
            s_records.Clear(); s_recordOfRequest.Clear(); s_fallbacks.Clear(); s_failures.Clear();
            Begun = Ready = BasisFailed = Dropped = Fallbacks = FailedAtBegin = FloatSyncs = CpuSyncs = Pending = MaxPending = RecordsNotKept = 0;
        }

        /// <summary>A GPU-first hit accepted (its Run returned): the record, with the bake pending.</summary>
        internal static void NoteGpuHit(long requestId, int vertexCount, int boneCount, int hitFrame, double hitMainSeconds, int dispatches, ProvisionalCutAcceptance acceptance)
        {
            Begun++; Pending++;
            MaxPending = Math.Max(MaxPending, Pending);
            Add(new Record
            {
                requestId = requestId, route = "gpu", vertexCount = vertexCount, boneCount = boneCount, hitFrame = hitFrame, readyFrame = -1,
                hitMainSeconds = hitMainSeconds, dispatches = dispatches, acceptance = acceptance.ToString(), basisOutcome = "pending",
            });
        }

        /// <summary>The bake of a GPU-first hit was collected: when, the worker's time and the collect's main-thread time.</summary>
        internal static void NoteReady(long requestId, int readyFrame, double workerSeconds, double kernelSeconds, double collectSeconds, int dispatches)
        {
            Pending = Math.Max(0, Pending - 1);
            if (!s_recordOfRequest.TryGetValue(requestId, out int at)) return;
            Record r = s_records[at];
            r.readyFrame = readyFrame; r.workerSeconds = workerSeconds; r.kernelSeconds = kernelSeconds; r.readyMainSeconds = collectSeconds;
            r.dispatches = Math.Max(r.dispatches, dispatches);
            if (r.basisOutcome == "pending") { r.basisOutcome = "ready"; Ready++; }
            s_records[at] = r;
        }

        internal static void NoteBasisFailed(long requestId, string reason)
        {
            BasisFailed++;
            Count(s_failures, Short(reason));
            SetOutcome(requestId, "failed: " + reason);
        }

        internal static void NoteDropped(long requestId, string reason)
        {
            Dropped++;
            SetOutcome(requestId, "dropped: " + reason);
        }

        private static void SetOutcome(long requestId, string outcome)
        {
            if (!s_recordOfRequest.TryGetValue(requestId, out int at)) return;
            Record r = s_records[at];
            if (r.basisOutcome == "pending") r.basisOutcome = outcome;
            s_records[at] = r;
        }

        internal static void NoteFallback(string reason)
        {
            Fallbacks++;
            Count(s_fallbacks, Short(reason));
        }

        internal static void NoteFailedAtBegin(string reason)
        {
            FailedAtBegin++;
            Count(s_failures, "at begin: " + Short(reason));
        }

        internal static void NoteFloatSync() { FloatSyncs++; }

        /// <summary>A synchronous request's record (the CPU route, or the float route): its main-thread time at the hit.</summary>
        internal static void NoteSync(string route, int vertexCount, double hitMainSeconds, double kernelSeconds, ProvisionalCutAcceptance acceptance)
        {
            if (route == "cpu") CpuSyncs++;
            int frame = UnityEngine.Time.frameCount;
            Add(new Record { route = route, vertexCount = vertexCount, hitFrame = frame, readyFrame = frame, hitMainSeconds = hitMainSeconds, kernelSeconds = kernelSeconds, acceptance = acceptance.ToString(), basisOutcome = "" });
        }

        private static void Add(Record record)
        {
            if (s_records.Count >= RecordCapacity) { RecordsNotKept++; return; }
            if (record.requestId != 0) s_recordOfRequest[record.requestId] = s_records.Count;
            s_records.Add(record);
        }

        private static void Count(Dictionary<string, int> into, string key)
        {
            into.TryGetValue(key, out int n);
            into[key] = n + 1;
        }

        private static string Short(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "(no reason)";
            int colon = reason.IndexOf(':');
            return colon > 0 && colon < 40 ? reason.Substring(0, colon) : (reason.Length > 48 ? reason.Substring(0, 48) : reason);
        }

        public static string Describe()
        {
            var t = new StringBuilder();
            t.Append("skin bake: mode ").Append(VpSkinBakeSetup.Name(VpSkinBakeSetup.Mode)).Append("; gpu-first hits ").Append(Begun).Append(", bakes ready ").Append(Ready)
                .Append(", failed ").Append(BasisFailed).Append(", dropped ").Append(Dropped).Append(", failed at begin ").Append(FailedAtBegin).Append(", fallbacks to cpu ").Append(Fallbacks)
                .Append(", float syncs ").Append(FloatSyncs).Append(", cpu syncs ").Append(CpuSyncs)
                .Append(", pending now ").Append(Pending).Append(" (max ").Append(MaxPending).Append("), records ").Append(s_records.Count).Append(RecordsNotKept > 0 ? " (+" + RecordsNotKept + " not kept)" : "");
            if (s_fallbacks.Count > 0) { t.Append("; fallbacks:"); foreach (var kv in s_fallbacks) t.Append(' ').Append(kv.Key).Append('=').Append(kv.Value); }
            if (s_failures.Count > 0) { t.Append("; failures:"); foreach (var kv in s_failures) t.Append(' ').Append(kv.Key).Append('=').Append(kv.Value); }
            return t.ToString();
        }

        /// <summary>The records as CSV lines (a header first), for a bench's file.</summary>
        public static IEnumerable<string> CsvLines()
        {
            yield return "requestId,route,vertexCount,boneCount,hitFrame,readyFrame,hitMainMs,readyMainMs,workerMs,kernelMs,dispatches,acceptance,basisOutcome";
            foreach (Record r in s_records)
            {
                yield return string.Join(",", r.requestId.ToString(CultureInfo.InvariantCulture), r.route, r.vertexCount.ToString(CultureInfo.InvariantCulture), r.boneCount.ToString(CultureInfo.InvariantCulture),
                    r.hitFrame.ToString(CultureInfo.InvariantCulture), r.readyFrame.ToString(CultureInfo.InvariantCulture),
                    (r.hitMainSeconds * 1000.0).ToString("F4", CultureInfo.InvariantCulture), (r.readyMainSeconds * 1000.0).ToString("F4", CultureInfo.InvariantCulture),
                    (r.workerSeconds * 1000.0).ToString("F4", CultureInfo.InvariantCulture), (r.kernelSeconds * 1000.0).ToString("F4", CultureInfo.InvariantCulture),
                    r.dispatches.ToString(CultureInfo.InvariantCulture), r.acceptance ?? "", (r.basisOutcome ?? "").Replace(',', ';'));
            }
        }
    }
}
