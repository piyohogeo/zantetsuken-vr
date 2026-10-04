using System;
using Unity.Collections;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The zeros a GPU buffer is written whole with, once, when it is made (TL, 2026-10-05): each a temporary native
    /// array, made and given back here so that the count of those alive is one number. Whoever writes whole takes its
    /// arrays inside the protection that gives them back -- a later array that cannot be had, or a write that throws,
    /// leaves none behind. These arrays are not part of any room: they live for the length of one whole write.
    /// </summary>
    public static class VpWholeWrite
    {
        /// <summary>
        /// Called before each array is taken and before each write, with what it is. For tests only, to make that step
        /// throw; null otherwise.
        /// </summary>
        public static Action<string> StepForTest { get; set; }

        /// <summary>How many arrays taken here have not been given back. Zero outside a whole write.</summary>
        public static int LiveArrays { get; private set; }

        /// <summary>How many arrays were ever taken here, and their bytes: what the whole writes of a session cost in temporary memory.</summary>
        public static long ArraysTaken { get; private set; }

        public static long BytesTaken { get; private set; }

        /// <summary>The largest single array taken, in bytes.</summary>
        public static long LargestBytes { get; private set; }

        public static NativeArray<T> Zeros<T>(string what, int length) where T : unmanaged
        {
            StepForTest?.Invoke("take " + what);
            var zeros = new NativeArray<T>(length, Allocator.Persistent);
            LiveArrays++;
            ArraysTaken++;
            long bytes = (long)length * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();
            BytesTaken += bytes;
            if (bytes > LargestBytes)
            {
                LargestBytes = bytes;
            }

            return zeros;
        }

        /// <summary>Gives one back, if it was taken; safe to call for one that never was.</summary>
        public static void Release<T>(ref NativeArray<T> zeros) where T : unmanaged
        {
            if (zeros.IsCreated)
            {
                zeros.Dispose();
                zeros = default;
                LiveArrays--;
            }
        }

        public static void Step(string what)
        {
            StepForTest?.Invoke(what);
        }
    }
}
