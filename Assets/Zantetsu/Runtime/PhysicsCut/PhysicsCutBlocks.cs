using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// How one cut's working blocks are taken.
    /// <para>
    /// **The product takes them as they come.** A block is taken uninitialised where every byte that is ever read from
    /// it is written first -- the classification scan writes each of its arrays over its whole used range, the kernel
    /// and the clip write their own sentinels into the scratch before reading any of it, and the numbers the job
    /// reports through are written for every slot by the job itself. Clearing such a block is writing every byte twice.
    /// </para>
    /// <para>
    /// **What reads a zero as a meaning is not taken this way**, and is not here: the job's report, whose
    /// <c>ranToEnd</c> tells a run that did not happen from one that did, and the bake's <c>done</c>, whose zero says
    /// that element never came back. Those stay cleared where they are taken.
    /// </para>
    /// <para>
    /// **<see cref="Fill"/> is for tests.** Set to 0 it gives back what clearing used to give, and set to anything else
    /// it gives back a block that is nothing like zero, so that a read of something not yet written shows itself
    /// instead of passing as an empty value. The product never sets it.
    /// </para>
    /// </summary>
    internal static unsafe class PhysicsCutBlocks
    {
        /// <summary>Negative: as it comes, which is the product. 0-255: every byte of a taken block is set to this.</summary>
        internal static int Fill = -1;

        /// <summary>Takes one block of <paramref name="length"/> elements, and fills it only if a test asked.</summary>
        internal static NativeArray<T> Take<T>(int length)
            where T : struct
        {
            var block = new NativeArray<T>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int fill = Fill;
            if (fill >= 0 && length > 0)
            {
                UnsafeUtility.MemSet(block.GetUnsafePtr(), (byte)fill, (long)length * UnsafeUtility.SizeOf<T>());
            }

            return block;
        }
    }
}
