using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Boundary for atomically committing a single Capture Run marker write
    /// operation to durable storage. An implementation returns a receipt only
    /// after the operation is durably committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WriteAtomic"/> treats each call as one synchronous attempt.
    /// A null operation is rejected with
    /// <see cref="ArgumentNullException"/>. The operation is never mutated.
    /// The writer must not retain the operation or its canonical bytes in its
    /// own fields, queues, or caches; temporary references and defensive
    /// copies held only for the duration of the synchronous call are allowed.
    /// The only reference the writer is permitted to keep after
    /// <see cref="WriteAtomic"/> returns is the operation reference held by
    /// the returned receipt; the receipt must not hold the canonical byte
    /// array. A receipt is returned only on success and corresponds to the
    /// exact operation instance passed in; no receipt is returned on
    /// exception. No retry, fallback, or alternate destination is performed.
    /// No logging, registration, draft, or trace state, and no Unity object,
    /// is accessed. Thread selection is the caller's responsibility; this
    /// contract is neither main-thread-only nor worker-only.
    /// </para>
    /// <para>
    /// Atomic success means, in order:
    /// <list type="number">
    /// <item>the temporary path is written as a new entry,</item>
    /// <item>all canonical bytes are written,</item>
    /// <item>the entry data is durably flushed,</item>
    /// <item>the temporary path is atomically renamed to the final path
    /// without overwriting,</item>
    /// <item>the parent directory is durably flushed,</item>
    /// <item>no authoritative completed entry remains at the temporary
    /// path,</item>
    /// <item>a receipt is returned only after all of the above succeed.</item>
    /// </list>
    /// </para>
    /// <para>
    /// If an exception occurs after the rename (for example while flushing the
    /// directory), the final entry may already exist. The caller must not
    /// blindly retry the same operation; a later recovery pass re-examines the
    /// filesystem. This contract itself performs no filesystem work.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunMarkerAtomicWriter
    {
        CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation);
    }
}
