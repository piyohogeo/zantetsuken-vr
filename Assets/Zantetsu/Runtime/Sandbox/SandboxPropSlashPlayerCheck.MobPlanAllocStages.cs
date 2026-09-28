using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    // Diagnosis only (the MobPlanSlash unit, re-preparation allocation; "-zantetsuAllocStages"): the main thread's managed
    // allocation of each frame split by where in the frame it happened. Probes at fixed execution orders read the thread's
    // allocated bytes (and, beside it, the GC heap in use and the collection count):
    //   a  frame start (Update -31000) .. before the crowd (Update -76): early Updates, the driver's Update
    //   b  the crowd's Update (-75): re-preparation, refill, the plan's collection
    //   c  -74 .. Update +1: poses, the level of detail, the NPCs, the katana's hits and cut requests
    //   d  Update +1 .. LateUpdate 399: the other Updates, the world's LateUpdate (collection, lifetime, vertex room)
    //   e  the check's LateUpdate (400)
    //   f  LateUpdate 401 .. Application.onBeforeRender: the physics step after the LateUpdates
    //   g  onBeforeRender .. the next frame's start: rendering and the driver's after-rendering turn
    // Kept in preallocated arrays and written once at the end (mobplan-alloc.csv), so the probes add no allocation of their
    // own to what they read. Nothing else is changed.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed class AllocStages
        {
            internal const int Probes = 7, Capacity = 24000;
            internal readonly long[,] bytes = new long[Capacity, Probes];
            internal readonly long[,] heap = new long[Capacity, Probes];
            internal readonly int[,] collections = new int[Capacity, Probes];
            internal readonly int[] frameOf = new int[Capacity];
            internal int firstFrame = -1, rows;
            internal bool threadCounter = true;
            internal readonly long[] mobPlanFrame = new long[Capacity];
            internal readonly int[] mobPlanFrameCollections = new int[Capacity];

            // The heap's change over the check's MobPlan part of this frame (inside stage e).
            internal void NoteMobPlanFrame(long delta, int collected)
            {
                int row = Time.frameCount - firstFrame;
                if (firstFrame < 0 || row < 0 || row >= Capacity) return;
                mobPlanFrame[row] = delta;
                mobPlanFrameCollections[row] = collected;
            }

            internal void Read(int probe)
            {
                int frame = Time.frameCount;
                if (firstFrame < 0) firstFrame = frame;
                int row = frame - firstFrame;
                if (row < 0 || row >= Capacity) return;
                frameOf[row] = frame;
                rows = Math.Max(rows, row + 1);
                long b = -1;
                if (threadCounter)
                {
                    try { b = GC.GetAllocatedBytesForCurrentThread(); }
                    catch (Exception) { threadCounter = false; }
                }

                bytes[row, probe] = b;
                heap[row, probe] = GC.GetTotalMemory(false);
                collections[row, probe] = GC.CollectionCount(0);
            }
        }

        private static AllocStages s_allocStages;

        [DefaultExecutionOrder(-31000)] private sealed class AllocProbeFrameStart : MonoBehaviour { private void Update() => s_allocStages?.Read(0); }
        [DefaultExecutionOrder(-76)] private sealed class AllocProbeBeforeCrowd : MonoBehaviour { private void Update() => s_allocStages?.Read(1); }
        [DefaultExecutionOrder(-74)] private sealed class AllocProbeAfterCrowd : MonoBehaviour { private void Update() => s_allocStages?.Read(2); }
        [DefaultExecutionOrder(1)] private sealed class AllocProbeAfterUpdates : MonoBehaviour { private void Update() => s_allocStages?.Read(3); }
        [DefaultExecutionOrder(399)] private sealed class AllocProbeBeforeCheck : MonoBehaviour { private void LateUpdate() => s_allocStages?.Read(4); }
        [DefaultExecutionOrder(401)] private sealed class AllocProbeAfterCheck : MonoBehaviour { private void LateUpdate() => s_allocStages?.Read(5); }

        private sealed partial class Walk
        {
            private GameObject _mpAllocProbes;
            private UnityEngine.Events.UnityAction _mpAllocRender;

            private void MobPlanAllocStagesBegin()
            {
                if (!Environment.GetCommandLineArgs().Contains("-zantetsuAllocStages")) return;
                s_allocStages = new AllocStages();
                _mpAllocProbes = new GameObject("Check Allocation Probes");
                _mpAllocProbes.AddComponent<AllocProbeFrameStart>();
                _mpAllocProbes.AddComponent<AllocProbeBeforeCrowd>();
                _mpAllocProbes.AddComponent<AllocProbeAfterCrowd>();
                _mpAllocProbes.AddComponent<AllocProbeAfterUpdates>();
                _mpAllocProbes.AddComponent<AllocProbeBeforeCheck>();
                _mpAllocProbes.AddComponent<AllocProbeAfterCheck>();
                _mpAllocRender = () => s_allocStages?.Read(6);
                Application.onBeforeRender += _mpAllocRender;
                string line = "mobplan allocation stages: probes on (thread counter " + GC.GetAllocatedBytesForCurrentThread() + ")";
                Log(line);
                MobPlanRecord(line);
            }

            // Once, at the end: per frame the bytes of each stage (thread counter; -1 where it could not be read), the heap's
            // change in each stage and the collections in each stage.
            private void MobPlanAllocStagesEnd()
            {
                AllocStages s = s_allocStages;
                if (s == null) return;
                s_allocStages = null;
                if (_mpAllocRender != null) Application.onBeforeRender -= _mpAllocRender;
                _mpAllocRender = null;
                if (_mpAllocProbes != null) UnityEngine.Object.Destroy(_mpAllocProbes);
                _mpAllocProbes = null;
                var text = new StringBuilder("frame,a,b,c,d,e,f,g,heapA,heapB,heapC,heapD,heapE,heapF,heapG,gcA,gcB,gcC,gcD,gcE,gcF,gcG,heapMobPlanFrame,gcMobPlanFrame\n");
                for (int row = 0; row + 1 < s.rows; row++)
                {
                    // Stage g runs from this frame's onBeforeRender to the next frame's start.
                    int[] from = { 0, 1, 2, 3, 4, 5, 6 };
                    long Delta(long[,] v, int k) => k < 6 ? v[row, k + 1] - v[row, k] : v[row + 1, 0] - v[row, 6];
                    int Count(int k) => k < 6 ? s.collections[row, k + 1] - s.collections[row, k] : s.collections[row + 1, 0] - s.collections[row, 6];
                    text.Append(s.frameOf[row]);
                    foreach (int k in from) text.Append(',').Append(s.threadCounter ? Delta(s.bytes, k) : -1);
                    foreach (int k in from) text.Append(',').Append(Delta(s.heap, k));
                    foreach (int k in from) text.Append(',').Append(Count(k));
                    text.Append(',').Append(s.mobPlanFrame[row]).Append(',').Append(s.mobPlanFrameCollections[row]);
                    text.Append('\n');
                }

                File.WriteAllText(Path.Combine(directory, "mobplan-alloc.csv"), text.ToString());
                string line = "mobplan allocation stages: " + s.rows + " frames written (thread counter " + (s.threadCounter ? "read" : "not available") + ")";
                Log(line);
                MobPlanRecord(line);
            }
        }
    }
}
