using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Debug = UnityEngine.Debug;

namespace Zantetsu.Sandbox
{
    // Explicit diagnostic only. Both layouts run in one binary on the Main thread. This compares
    // SetData submission, NOT two product worlds, GPU execution, or float-vs-packed cutting math.
    internal static class CompactVertexUploadComparison
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Legacy32 { public Vector3 position, normal; public Vector2 uv; }
        [Serializable] private sealed class Result
        {
            public string unity, graphics, device;
            public int samples = 61, warmup = 10, orderSeed;
            public bool passed;
            public List<Row> rows = new List<Row>();
        }
        [Serializable] private sealed class Row
        {
            public int stage, copies, sourceStart, sourceCount, committedVertices, count, capacity;
            public string range;
            public long cpuCapacity16, cpuCapacity32, gpuLogicalCapacity16, gpuLogicalCapacity32;
            public long managed16, managed32;
            public double median16Us, median32Us, p95_16Us, p95_32Us;
            public double[] samples16Us, samples32Us;
            public bool readbackPassed;
        }

        internal static IEnumerator RunGuarded(string directory, Action<bool> completed)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(Run(directory));
            try
            {
                while (stack.Count > 0)
                {
                    bool next = false; object current = null; Exception failure = null;
                    try { next = stack.Peek().MoveNext(); if (next) current = stack.Peek().Current; }
                    catch (Exception e) { failure = e; }
                    if (failure != null) { Debug.LogException(failure); completed(false); yield break; }
                    if (!next) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    if (current is IEnumerator nested) stack.Push(nested);
                    else yield return current;
                }
                completed(true);
            }
            finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
        }

        private static IEnumerator Run(string directory)
        {
            Require(UnsafeUtility.SizeOf<VpRenderVertex>() == 16 && UnsafeUtility.SizeOf<Legacy32>() == 32, "ABI");
            string output = Path.Combine(directory, "upload-comparison.json");
            Require(!File.Exists(output), "Refusing to overwrite comparison evidence");
            var data = Resources.Load<TextAsset>("Static16Migration/Megacity");
            Require(data != null, "Private verified Megacity Static16 fixture missing");
            var result = new Result { unity = Application.unityVersion, graphics = SystemInfo.graphicsDeviceType.ToString(), device = SystemInfo.graphicsDeviceName };
            int.TryParse(Environment.GetEnvironmentVariable("VP_UPLOAD_ORDER_SEED"), out result.orderSeed);
            using (var storage = new VpCpuGeometryStorage(65536, 262144, 64, 128, 128, Allocator.Persistent))
            {
                VpStoredGeometry geometry;
                using (var stream = new MemoryStream(data.bytes, false))
                    Require(VpStatic16File.TryAppendCuttable(stream, storage, out geometry, out var failure), failure);
                for (int stage = 0; stage <= 3; stage++)
                {
                    int previous = storage.VertexCount;
                    if (stage > 0)
                    {
                        Require(storage.TryGetPublishedExtent(geometry, out _, out _, out var bounds), "Extent");
                        int axis = stage - 1;
                        float3 normal = axis == 0 ? new float3(1, 0, 0) : axis == 1 ? new float3(0, 1, 0) : new float3(0, 0, 1);
                        Require(VpStorageCutInput.TryAcquire(storage, geometry, out var input), "Acquire cut input");
                        VpStorageCutResult cut;
                        using (input) Require(VpStorageCut.TryExecute(storage, input, new float4(normal, -bounds.center[axis] - .137f * bounds.extents[axis]), default, out cut), "Execute cut");
                        Require(cut.status == VpStorageCutStatus.Ok && cut.kernel.executedManaged == 0 && cut.kernel.openContourCount == 0, "Burst closed cut");
                        geometry = cut.positive.geometry;
                        Require(storage.VertexCount > previous, "Nonempty append");
                    }
                    foreach (int copies in new[] { 1, 16, 64 })
                    {
                        yield return Measure(storage, stage, copies, 0, storage.VertexCount, "all-committed", result);
                        if (stage > 0) yield return Measure(storage, stage, copies, previous, storage.VertexCount - previous, "new-tail", result);
                    }
                }
            }
            result.passed = result.rows.Count == 21;
            Require(result.passed, "All comparison cells completed");
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(result, true));
            Debug.Log("UPLOAD COMPARISON: PASS rows=" + result.rows.Count + " output=" + output);
        }

        private static IEnumerator Measure(VpCpuGeometryStorage storage, int stage, int copies, int start, int sourceCount, string range, Result result)
        {
            int count = checked(sourceCount * copies), capacity = math.ceilpow2(count + 16);
            var row = new Row { stage = stage, copies = copies, sourceStart = start, sourceCount = sourceCount, committedVertices = storage.VertexCount,
                count = count, capacity = capacity, range = range, cpuCapacity16 = (long)capacity * 16, cpuCapacity32 = (long)capacity * 32,
                gpuLogicalCapacity16 = (long)capacity * 16, gpuLogicalCapacity32 = (long)capacity * 32,
                samples16Us = new double[result.samples], samples32Us = new double[result.samples] };
            using (var packedOwner = new NativeArray<VpRenderVertex>(capacity, Allocator.Persistent))
            using (var legacyOwner = new NativeArray<Legacy32>(capacity, Allocator.Persistent))
            using (var gpu16 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16))
            using (var gpu32 = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 32))
            {
                // Writable NativeArray views; the using owners alone dispose the allocations.
                var packed = packedOwner; var legacy = legacyOwner;
                // Initialize buffer guards before filling source data. Packing/decoding/allocation is outside timing.
                gpu16.SetData(packed); gpu32.SetData(legacy);
                for (int i = 0; i < count; i++)
                {
                    var vertex = storage.Vertices[start + i % sourceCount];
                    Require(vertex.HasValidAttributes, "Valid fixture attributes");
                    packed[i] = vertex;
                    legacy[i] = new Legacy32 { position = vertex.position, normal = vertex.normal, uv = vertex.uv0 };
                }
                // Identical data, API, destination offset, element count/capacity. Neither buffer is drawn.
                // A yield per pair prevents a single tight loop being mistaken for ordinary frame throughput.
                for (int sample = -result.warmup; sample < result.samples; sample++)
                {
                    for (int order = 0; order < 2; order++)
                    {
                        bool use16 = ((sample + result.warmup + order + result.orderSeed) & 1) == 0;
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long ticks = Stopwatch.GetTimestamp();
                        if (use16) gpu16.SetData(packed, 0, 8, count); else gpu32.SetData(legacy, 0, 8, count);
                        double elapsed = (Stopwatch.GetTimestamp() - ticks) * 1000000.0 / Stopwatch.Frequency;
                        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                        if (sample >= 0)
                        {
                            if (use16) { row.samples16Us[sample] = elapsed; row.managed16 += allocated; }
                            else { row.samples32Us[sample] = elapsed; row.managed32 += allocated; }
                        }
                    }
                    yield return null;
                }
                // Synchronous readback and verification are outside every sample, including the untouched guards.
                var actual16 = new VpRenderVertex[capacity]; var actual32 = new Legacy32[capacity];
                gpu16.GetData(actual16); gpu32.GetData(actual32);
                for (int i = 0; i < capacity; i++)
                {
                    bool inside = i >= 8 && i < count + 8;
                    var a = inside ? packed[i - 8] : default;
                    var b = inside ? legacy[i - 8] : default;
                    Require(actual16[i].position.Equals(a.position) && actual16[i].normalX == a.normalX && actual16[i].normalY == a.normalY && actual16[i].u == a.u && actual16[i].v == a.v, "16B readback/guard");
                    Require(actual32[i].position.Equals(b.position) && actual32[i].normal.Equals(b.normal) && actual32[i].uv.Equals(b.uv), "32B readback/guard");
                }
                row.readbackPassed = true;
            }
            var sorted16 = (double[])row.samples16Us.Clone(); var sorted32 = (double[])row.samples32Us.Clone();
            Array.Sort(sorted16); Array.Sort(sorted32);
            row.median16Us = sorted16[30]; row.median32Us = sorted32[30];
            row.p95_16Us = sorted16[57]; row.p95_32Us = sorted32[57]; // nearest-rank ceil(.95*61)-1
            result.rows.Add(row);
            Debug.Log($"UPLOAD COMPARISON: stage={stage} range={range} copies={copies} count={count} capacity={capacity} median16Us={row.median16Us:F3} median32Us={row.median32Us:F3} readback=PASS");
        }
        private static void Require(bool valid, string message)
        { if (!valid) throw new InvalidOperationException("UPLOAD COMPARISON FAILED: " + message); }
    }
}
