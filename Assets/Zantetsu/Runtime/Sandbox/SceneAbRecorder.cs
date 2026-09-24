#if VP_DIAGNOSTIC_SCENE_AB
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
namespace Zantetsu.Sandbox
{
    internal sealed class SceneAbRecorder : MonoBehaviour
    {
        [Serializable] private struct Sample
        {
            public int frame, phase, vertices, drawnFrames;
            public double driverUpdateUs, driverLateUs, cameraSubmissionUs, previousFrameMainMs;
            public long scopedManagedBytes, previousFrameGcAllocatedBytes, unityAllocated, unityReserved, workingSet, lifetimePeakWorkingSet;
        }
        [Serializable] private sealed class Checkpoint
        {
            public string name, positionHash, indexHash, decodedAttributeHash;
            public int vertices, indices;
        }
        [Serializable] private sealed class Result
        {
            public int stride, vertexCapacity, indexCapacity;
            public bool shaderLegacy, passed, frameGcRecorderValid;
            public string unity, device, fixture;
            public List<Checkpoint> checkpoints = new List<Checkpoint>();
            public Sample[] samples;
        }
        private readonly Sample[] samples = new Sample[4096];
        private readonly Result result = new Result();
        private int count;
        private bool stopped;
        private CutWorldRoot world;
        private CutWorldCameraDrawing drawing;
        private ProfilerRecorder main;
        private ProfilerRecorder frameGc;
        public int Phase;
        public bool CapturesEnabled => stopped;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SelectShaderAbi()
        {
#if VP_DIAGNOSTIC_LEGACY32
            Shader.EnableKeyword("VP_DIAGNOSTIC_LEGACY32");
#else
            Shader.DisableKeyword("VP_DIAGNOSTIC_LEGACY32");
#endif
            Application.runInBackground = true;
        }
        public void Initialize(CutWorldRoot value, bool authoredMegacity)
        {
            world = value; drawing = FindFirstObjectByType<CutWorldCameraDrawing>();
            main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            if (!main.Valid) throw new InvalidOperationException("Scene AB Main recorder unavailable");
            frameGc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            result.frameGcRecorderValid = frameGc.Valid;
            result.stride = VpRenderVertex.Stride; result.vertexCapacity = world.Storage.VertexCapacity; result.indexCapacity = world.Storage.IndexCapacity;
            result.shaderLegacy = Shader.IsKeywordEnabled("VP_DIAGNOSTIC_LEGACY32");
            result.unity = Application.unityVersion; result.device = SystemInfo.graphicsDeviceName;
            result.fixture = authoredMegacity ? "authored-megacity-static16-source" : "dense-synthetic-box";
            if (result.shaderLegacy != (result.stride == 32)) throw new InvalidOperationException("Scene AB CPU/shader ABI mismatch");
        }
        private IEnumerator Start()
        {
            var end = new WaitForEndOfFrame();
            while (!stopped)
            {
                yield return end;
                if (stopped || world == null) continue;
                if (count >= samples.Length) throw new InvalidOperationException("Scene AB sample capacity exceeded");
                var memory = new MemoryCounters(); memory.cb = (uint)Marshal.SizeOf<MemoryCounters>();
                if (!K32GetProcessMemoryInfo(new IntPtr(-1), out memory, memory.cb)) throw new InvalidOperationException("Scene AB memory query failed");
                double scale = 1000000.0 / Stopwatch.Frequency;
                samples[count++] = new Sample { frame = Time.frameCount, phase = Phase, vertices = world.Storage.VertexCount,
                    drawnFrames = drawing.DrawnFrames, driverUpdateUs = SceneAbCounters.UpdateTicks * scale,
                    driverLateUs = SceneAbCounters.LateTicks * scale, cameraSubmissionUs = SceneAbCounters.DrawTicks * scale,
                    scopedManagedBytes = SceneAbCounters.Allocated, previousFrameMainMs = main.LastValue * 1e-6,
                    previousFrameGcAllocatedBytes = frameGc.Valid ? frameGc.LastValue : -1,
                    unityAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(), unityReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                    workingSet = (long)memory.workingSet.ToUInt64(), lifetimePeakWorkingSet = (long)memory.peakWorkingSet.ToUInt64() };
            }
        }
        public void Mark(string name, params LogicalFragmentId[] fragments)
        {
            ulong positions = 14695981039346656037UL, indices = positions, attributes = positions;
            void Mix(ref ulong hash, uint word) { unchecked { for (int k = 0; k < 4; k++) { hash ^= (byte)(word >> (k * 8)); hash *= 1099511628211UL; } } }
            for (int i = 0; i < world.Storage.VertexCount; i++)
            {
                var p = world.Storage.Vertices[i].position;
                Mix(ref positions, Unity.Mathematics.math.asuint(p.x)); Mix(ref positions, Unity.Mathematics.math.asuint(p.y)); Mix(ref positions, Unity.Mathematics.math.asuint(p.z));
                var v = world.Storage.Vertices[i]; var n = v.normal; var uv = v.uv0;
                Mix(ref attributes, Unity.Mathematics.math.asuint(n.x)); Mix(ref attributes, Unity.Mathematics.math.asuint(n.y)); Mix(ref attributes, Unity.Mathematics.math.asuint(n.z));
                Mix(ref attributes, Unity.Mathematics.math.asuint(uv.x)); Mix(ref attributes, Unity.Mathematics.math.asuint(uv.y));
            }
            int indexCount = 0;
            foreach (var fragment in fragments)
            {
                if (!world.Geometry.TryGetGeometry(fragment, out var geometry) || !world.Storage.TryAcquireIndexReadLease(geometry.indexRange, out var lease, out var span)) throw new InvalidOperationException("Scene AB geometry checkpoint unavailable");
                try { Mix(ref indices, (uint)span.Length); for (int i = 0; i < span.Length; i++) Mix(ref indices, span[i]); indexCount += span.Length; }
                finally { world.Storage.TryReleaseIndexReadLease(lease); }
            }
            result.checkpoints.Add(new Checkpoint { name = name, vertices = world.Storage.VertexCount, indices = indexCount, positionHash = positions.ToString("x16"), indexHash = indices.ToString("x16"), decodedAttributeHash = attributes.ToString("x16") });
        }
        public void StopAndSave(string directory)
        {
            stopped = true; main.Dispose(); if (frameGc.Valid) frameGc.Dispose();
            result.samples = new Sample[count]; Array.Copy(samples, result.samples, count);
            result.passed = count > 480 && world.GeometryFaults == 0 && result.checkpoints.Count == 3 && drawing.DrawnFrames > 480;
            if (!result.passed) throw new InvalidOperationException("Scene AB incomplete frame/geometry evidence");
            using (var stream = new FileStream(Path.Combine(directory, "scene-ab.json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(stream)) writer.Write(JsonUtility.ToJson(result, true));
            UnityEngine.Debug.Log("SCENE AB: PASS stride=" + result.stride + " frames=" + count + "; measurement stopped before screenshots");
        }
        private void OnDestroy() { if (main.Valid) main.Dispose(); if (frameGc.Valid) frameGc.Dispose(); }
        [StructLayout(LayoutKind.Sequential)] private struct MemoryCounters
        {
            public uint cb, faults;
            public UIntPtr peakWorkingSet, workingSet, quotaPeakPaged, quotaPaged, quotaPeakNonPaged, quotaNonPaged, pagefile, peakPagefile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, out MemoryCounters counters, uint size);
    }
}
#endif
