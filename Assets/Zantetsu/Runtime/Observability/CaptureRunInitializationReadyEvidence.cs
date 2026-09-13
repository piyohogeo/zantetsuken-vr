namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completion evidence that a Capture Run reached the ready
    /// state, holding the initialization execution receipt it was minted from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Run reaches ready exactly one way: fresh initialization under the
    /// held lock. A Run root that is already occupied is a collision the
    /// caller stops on, so no evidence exists for it. <see cref="IsValid"/>
    /// recomputes the acceptance conditions from the held receipt without
    /// throwing, so a forged nested value yields <c>false</c> rather than an
    /// exception.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing and is not an
    /// <see cref="System.IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationReadyEvidence
    {
        private readonly CaptureRunInitializationExecutionReceipt _freshExecutionReceipt;

        private CaptureRunInitializationReadyEvidence(CaptureRunInitializationExecutionReceipt freshExecutionReceipt)
        {
            _freshExecutionReceipt = freshExecutionReceipt;
        }

        internal static CaptureRunInitializationReadyEvidence FromFresh(CaptureRunInitializationExecutionReceipt receipt)
        {
            if (receipt == null)
            {
                throw new System.ArgumentNullException(nameof(receipt));
            }

            if (!IsValidFresh(receipt))
            {
                throw new System.ArgumentException("Execution receipt must be a valid fresh initialization receipt.", nameof(receipt));
            }

            return new CaptureRunInitializationReadyEvidence(receipt);
        }

        internal CaptureRunInitializationExecutionReceipt FreshExecutionReceipt => _freshExecutionReceipt;

        internal CaptureRunRootLayout RootLayout =>
            _freshExecutionReceipt != null ? _freshExecutionReceipt.RootLayout : null;

        internal long TestRunId =>
            _freshExecutionReceipt != null ? _freshExecutionReceipt.TestRunId : 0;

        internal string RunInitializationId =>
            _freshExecutionReceipt != null ? _freshExecutionReceipt.RunInitializationId : null;

        internal bool IsValid => IsValidFresh(_freshExecutionReceipt);

        private static bool IsValidFresh(CaptureRunInitializationExecutionReceipt receipt)
        {
            return receipt != null
                && receipt.IsValid
                && receipt.RootLayout != null
                && receipt.TestRunId > 0
                && IsLowercaseHex(receipt.RunInitializationId, 32);
        }

        private static bool IsLowercaseHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                if (!digit && !lower)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
