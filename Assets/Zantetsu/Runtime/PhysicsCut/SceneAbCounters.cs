#if VP_DIAGNOSTIC_SCENE_AB
using System;
using System.Diagnostics;
using UnityEngine;
namespace Zantetsu.PhysicsCut
{
    // Main-thread wall durations of bounded product scopes. Not all engine Main CPU work.
    public static class SceneAbCounters
    {
        public static int Frame = -1;
        public static long UpdateTicks, LateTicks, DrawTicks;
        public static long Allocated => -1; // IL2CPP GC.GetAllocatedBytesForCurrentThread is not implemented.
        public static Scope Measure(int slot) => new Scope(slot);
        public readonly struct Scope : IDisposable
        {
            private readonly int slot;
            private readonly long ticks;
            public Scope(int slot)
            {
                this.slot = slot;
                if (Frame != Time.frameCount) { Frame = Time.frameCount; UpdateTicks = LateTicks = DrawTicks = 0; }
                ticks = Stopwatch.GetTimestamp();
            }
            public void Dispose()
            {
                long elapsed = Stopwatch.GetTimestamp() - ticks;
                if (slot == 0) UpdateTicks += elapsed; else if (slot == 1) LateTicks += elapsed; else DrawTicks += elapsed;
            }
        }
    }
}
#endif
