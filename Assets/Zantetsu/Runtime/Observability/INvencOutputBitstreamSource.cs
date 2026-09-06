using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Injected, instance-scoped bitstream retrieval boundary for one Submitted
    /// Submit-to-Output record. The real implementation owns the NVENC Output
    /// Buffer, Completion Event, and lock/unlock/unmap sequence; this unit only
    /// consumes it through a synchronous call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryCopyCompletedOutput"/> is called synchronously by the
    /// buffer authority with the fixed Access Unit storage. A <c>true</c> return
    /// means the corresponding Completion Event was awaited, the Output Buffer
    /// was locked, the bitstream was copied directly into
    /// <paramref name="destination"/>, and the buffer was unlocked and unmapped,
    /// leaving the native ownership safely resolved.
    /// <paramref name="validLength"/> must then be in
    /// 1..<paramref name="destinationCapacity"/>.
    /// </para>
    /// <para>
    /// A <c>false</c> return is a controllable retrieval failure: the native
    /// ownership is already safely resolved and no valid Access Unit exists.
    /// An exception means the result or ownership is unknown; the exact
    /// exception propagates and the process is poisoned, with no guessed
    /// release or creation of the sample slot, the region, or a completion.
    /// </para>
    /// <para>
    /// <paramref name="destination"/> is used only for the duration of the call
    /// and must never be stored or published.
    /// </para>
    /// </remarks>
    internal interface INvencOutputBitstreamSource
    {
        bool TryCopyCompletedOutput(
            in CaptureFrameWorkToken workToken,
            in NvencEncodeSampleSlotLease sampleSlot,
            byte[] destination,
            int destinationCapacity,
            out int validLength);
    }
}
