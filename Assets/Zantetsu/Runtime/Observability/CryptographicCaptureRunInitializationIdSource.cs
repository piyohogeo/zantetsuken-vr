namespace Zantetsu.Observability
{
    /// <summary>
    /// Cryptographic initialization ID source that delegates directly to the
    /// existing generator, without re-implementing entropy generation or hex
    /// encoding. Holds no fields, no mutable static state, is not disposable,
    /// and performs no filesystem work.
    /// </summary>
    internal sealed class CryptographicCaptureRunInitializationIdSource : ICaptureRunInitializationIdSource
    {
        public string Create()
        {
            return CaptureRunInitializationIdGenerator.Create();
        }
    }
}
