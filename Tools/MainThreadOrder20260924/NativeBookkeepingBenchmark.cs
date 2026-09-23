// Diagnostic template: install temporarily in Zantetsu.PhysicsCut.PlayModeTests.
// Measures only Main-thread per-cut native bookkeeping allocation and disposal, with collection safety intact.
// It does not measure worker execution, Mesh allocation/application, BakeMesh, or complete Request-to-Final time.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public sealed class NativeBookkeepingBenchmark
    {
        private const int Iterations = 1024;
        private const int Rounds = 8;

        [Test]
        public void Measure()
        {
            string directory = Environment.GetEnvironmentVariable("ORDER_OUTPUT");
            Assert.That(directory, Is.Not.Null.And.Not.Empty, "ORDER_OUTPUT must name this run's directory");
            Assert.That(PhysicsCutBlocks.Fill, Is.EqualTo(-1), "the benchmark requires production uninitialised blocks");
            Directory.CreateDirectory(directory);
            string run = Environment.GetEnvironmentVariable("ORDER_RUN") ?? new DirectoryInfo(directory).Name;
            bool reverse = Environment.GetEnvironmentVariable("ORDER_REVERSE") == "1";
            bool characterFirst = Environment.GetEnvironmentVariable("ORDER_CHARACTER_FIRST") == "1";
            string path = Path.Combine(directory, "native-bookkeeping.csv");
            var csv = new StringBuilder("run,case,mesh_slots,round,order,region,version,ticks,freq,iterations,native_arrays,payload_bytes,cleared_payload_bytes\n");
            WriteEnvironment(directory, reverse, characterFirst);
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));

            foreach (int meshSlots in characterFirst ? new[] { 10, 2 } : new[] { 2, 10 })
            {
                string name = meshSlots == 2 ? "box" : "character";
                AssertClearing(meshSlots);
                // One full batch per version and calibration. Warmup rows are not measurements.
                EmptyBatch();
                BaselineBatch(meshSlots);
                CandidateBatch(meshSlots);
                for (int round = 0; round < Rounds; round++)
                {
                    long emptyTicks = EmptyBatch();
                    Append(csv, run, name, meshSlots, round, -1, "empty_batch", "empty", emptyTicks, 0, 0, 0);
                    for (int order = 0; order < 4; order++)
                    {
                        bool baseline = (order == 0 || order == 3) != reverse;
                        long ticks = baseline ? BaselineBatch(meshSlots) : CandidateBatch(meshSlots);
                        int payload = meshSlots * (baseline ? BaselineBytesPerSlot : UnsafeUtility.SizeOf<PhysicsCutMeshSlot>());
                        int cleared = baseline ? meshSlots * UnsafeUtility.SizeOf<byte>() : payload;
                        Append(csv, run, name, meshSlots, round, order, "allocation_release",
                            baseline ? "baseline" : "candidate", ticks, baseline ? 4 : 1, payload, cleared);
                    }
                    // Persist every complete round, replacing rather than appending a previous run's results.
                    File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
                }
                AssertClearing(meshSlots);
            }
        }

        private static int BaselineBytesPerSlot => 2 * UnsafeUtility.SizeOf<int>()
            + UnsafeUtility.SizeOf<float3x2>() + UnsafeUtility.SizeOf<byte>();

        private static long BaselineBatch(int meshSlots)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Iterations; i++) BaselineIteration(meshSlots);
            return Stopwatch.GetTimestamp() - start;
        }

        private static long CandidateBatch(int meshSlots)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Iterations; i++) CandidateIteration(meshSlots);
            return Stopwatch.GetTimestamp() - start;
        }

        private static long EmptyBatch()
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Iterations; i++) EmptyIteration();
            return Stopwatch.GetTimestamp() - start;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EmptyIteration() { }

        // These flags and this release order are copied from b19ce67a PhysicsCutCook.TryReserve/ReleaseWorkingSet.
        // The unchanged report allocation is excluded on both sides. No array element is touched in this metric.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BaselineIteration(int meshSlots)
        {
            NativeArray<int> ids = default;
            NativeArray<float3x2> bounds = default;
            NativeArray<int> vertexCounts = default;
            NativeArray<byte> bakeDone = default;
            try
            {
                ids = PhysicsCutBlocks.Take<int>(meshSlots);
                bounds = PhysicsCutBlocks.Take<float3x2>(meshSlots);
                vertexCounts = PhysicsCutBlocks.Take<int>(meshSlots);
                bakeDone = new NativeArray<byte>(meshSlots, Allocator.Persistent);
            }
            finally
            {
                if (ids.IsCreated) ids.Dispose();
                if (bounds.IsCreated) bounds.Dispose();
                if (vertexCounts.IsCreated) vertexCounts.Dispose();
                if (bakeDone.IsCreated) bakeDone.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CandidateIteration(int meshSlots)
        {
            NativeArray<PhysicsCutMeshSlot> slots = default;
            try
            {
                // The entire slot is cleared, exactly as in the candidate product; bakeDone starts at zero.
                slots = new NativeArray<PhysicsCutMeshSlot>(meshSlots, Allocator.Persistent);
            }
            finally
            {
                if (slots.IsCreated) slots.Dispose();
            }
        }

        private static void AssertClearing(int meshSlots)
        {
            NativeArray<byte> baseline = default;
            NativeArray<PhysicsCutMeshSlot> candidate = default;
            try
            {
                baseline = new NativeArray<byte>(meshSlots, Allocator.Persistent);
                candidate = new NativeArray<PhysicsCutMeshSlot>(meshSlots, Allocator.Persistent);
                for (int i = 0; i < meshSlots; i++)
                {
                    Assert.That(baseline[i], Is.Zero);
                    Assert.That(candidate[i].bakeDone, Is.Zero);
                }
            }
            finally
            {
                if (baseline.IsCreated) baseline.Dispose();
                if (candidate.IsCreated) candidate.Dispose();
            }
        }

        private static void Append(StringBuilder csv, string run, string name, int meshSlots, int round,
            int order, string region, string version, long ticks, int arrays, int payload, int cleared)
        {
            csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}\n",
                Csv(run), name, meshSlots, round, order, region, version, ticks, Stopwatch.Frequency,
                Iterations, arrays, payload, cleared);
        }

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private static void WriteEnvironment(string directory, bool reverse, bool characterFirst)
        {
            var text = new StringBuilder();
            text.AppendLine("Unity=" + Application.unityVersion);
            text.AppendLine("Editor=" + Application.isEditor + "; platform=" + Application.platform);
            text.AppendLine("Thread=" + Thread.CurrentThread.ManagedThreadId + "; pointerBytes=" + IntPtr.Size);
            text.AppendLine("Metric=Stopwatch elapsed ticks on the calling Main thread; not CPU time or complete cut performance.");
            text.AppendLine("Scope=per-cut native bookkeeping allocation plus release only. No worker, bake, meshes, report, arena, or end-to-end cut.");
            text.AppendLine("Baseline=b19ce67a PhysicsCutCook: ids/bounds/vertexCounts via real PhysicsCutBlocks.Take<T>; bakeDone via cleared NativeArray<byte>.");
            text.AppendLine("Candidate=current PhysicsCutMeshSlot, one cleared NativeArray; all arrays Allocator.Persistent.");
            text.AppendLine("Release=IsCreated then Dispose in finally; finally is inside each measured batch on both versions.");
            text.AppendLine("ORDER_REVERSE=" + (Environment.GetEnvironmentVariable("ORDER_REVERSE") ?? "<unset>")
                + "; ORDER_CHARACTER_FIRST=" + (Environment.GetEnvironmentVariable("ORDER_CHARACTER_FIRST") ?? "<unset>"));
            text.AppendLine("Order=" + (reverse ? "BAAB (candidate,baseline,baseline,candidate)" : "ABBA (baseline,candidate,candidate,baseline)")
                + "; 8 rounds per case, 1024 cuts per batch.");
            text.AppendLine("CaseOrder=" + (characterFirst ? "character,box" : "box,character"));
            text.AppendLine("Warmup=one full batch per case/version and one empty batch; excluded from CSV.");
            text.AppendLine("Calibration=one separate 1024-call empty no-inline batch per round; includes loop/call/Stopwatch overhead; not subtracted.");
            text.AppendLine("CSV=64 measured allocation/release rows plus 16 empty rows; all retained, no outlier removal.");
            text.AppendLine("NativeArrays/payload_bytes/cleared_payload_bytes are per cut, not batch totals; allocator bookkeeping is not counted as payload.");
            text.AppendLine("No forced GC or timing of validation/file I/O. No safety suppression, raw allocation, or pool.");
            text.AppendLine("PhysicsCutBlocks.Fill=" + PhysicsCutBlocks.Fill);
            text.AppendLine("NativeLeakDetection=" + NativeLeakDetection.Mode);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            text.AppendLine("ENABLE_UNITY_COLLECTIONS_CHECKS=true");
#else
            text.AppendLine("ENABLE_UNITY_COLLECTIONS_CHECKS=false");
#endif
            text.AppendFormat(CultureInfo.InvariantCulture,
                "SizeOf: int={0}; float3x2={1}; byte={2}; PhysicsCutMeshSlot={3}; baselinePayloadPerSlot={4}\n",
                UnsafeUtility.SizeOf<int>(), UnsafeUtility.SizeOf<float3x2>(), UnsafeUtility.SizeOf<byte>(),
                UnsafeUtility.SizeOf<PhysicsCutMeshSlot>(), BaselineBytesPerSlot);
            File.WriteAllText(Path.Combine(directory, "native-bookkeeping-environment.txt"), text.ToString(), new UTF8Encoding(false));
        }
    }
}
