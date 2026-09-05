namespace Zantetsu.Observability
{
    /// <summary>
    /// Instance-scoped encode-picture submit boundary. A <c>true</c> result
    /// means the submit is known to have succeeded; <c>false</c> means a known,
    /// controllable submit failure that maps to
    /// <see cref="NvencFailedBeforeSubmitReason.NvencSubmitFailed"/>. An
    /// exception means the submit result or ownership cannot be determined
    /// safely, so the caller poisons the process and must not fabricate a
    /// success or failure record. The implementation performs no retry,
    /// fallback, synchronous encode, or completion wait.
    /// </summary>
    internal interface INvencEncodePictureSubmitter
    {
        bool TrySubmit(in NvencEncodePictureSubmitOperation operation);
    }
}
