using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable token returned by a Capture Run marker atomic writer after a
    /// durable commit. It records which writer issued it and which write
    /// operation was committed; it is not an on-disk certificate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsValid"/> is computed from whether both references are
    /// non-null; no validity flag is stored. The receipt holds no handle, no
    /// canonical byte array, no hash, and no copied path value; forwarding
    /// properties read straight from the operation.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunMarkerWriteReceipt
    {
        private readonly ICaptureRunMarkerAtomicWriter _issuedBy;
        private readonly CaptureRunMarkerWriteOperation _operation;

        internal CaptureRunMarkerWriteReceipt(
            ICaptureRunMarkerAtomicWriter issuedBy,
            CaptureRunMarkerWriteOperation operation)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunMarkerAtomicWriter IssuedBy => _issuedBy;

        internal CaptureRunMarkerWriteOperation Operation => _operation;

        internal bool IsValid => _issuedBy != null && _operation != null;

        internal CaptureRunRootRole RootRole => _operation.RootRole;

        internal CaptureRunMarkerKind MarkerKind => _operation.MarkerKind;

        internal string TemporaryPath => _operation.TemporaryPath;

        internal string FinalPath => _operation.FinalPath;

        internal int ByteCount => _operation.ByteCount;
    }
}
