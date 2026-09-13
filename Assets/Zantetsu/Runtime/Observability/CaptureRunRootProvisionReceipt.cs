using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable token returned by a Capture Run root provisioner after a new
    /// run root has been created and verified empty. It records which
    /// provisioner issued it and which provision operation was performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsValid"/> is computed from whether both references are
    /// non-null; no validity flag is stored. The receipt holds no copied path
    /// or ID value. What root was created is read from
    /// <see cref="Operation"/>; the receipt does not restate it.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunRootProvisionReceipt
    {
        private readonly ICaptureRunRootProvisioner _issuedBy;
        private readonly CaptureRunRootProvisionOperation _operation;

        internal CaptureRunRootProvisionReceipt(
            ICaptureRunRootProvisioner issuedBy,
            CaptureRunRootProvisionOperation operation)
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

        internal ICaptureRunRootProvisioner IssuedBy => _issuedBy;

        internal CaptureRunRootProvisionOperation Operation => _operation;

        internal bool IsValid => _issuedBy != null && _operation != null;
    }
}
