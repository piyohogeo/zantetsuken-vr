using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Boundary for provisioning a brand-new Capture Run root directory. An
    /// implementation returns a receipt only after the target run root has been
    /// created and verified as an empty, correctly located directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ProvisionNew"/> treats each call as one synchronous attempt.
    /// A null operation is rejected with
    /// <see cref="ArgumentNullException"/>; the operation and its root layout
    /// are never mutated. The provisioner must not retain the operation or the
    /// root layout in its own fields, queues, or caches; temporary references
    /// held only for the duration of the synchronous call are allowed. Only the
    /// returned receipt may keep the operation reference. No retry, fallback,
    /// delete, repair, or reuse of an already-existing run root is performed,
    /// and an already-existing run root is never treated as success. No
    /// logging, registration, draft, or trace state, and no marker or codec,
    /// is accessed. Thread selection is the caller's responsibility.
    /// </para>
    /// <para>
    /// Provision success means, in order:
    /// <list type="number">
    /// <item>the target is exactly the run root named by the operation,</item>
    /// <item>the trusted base root was already trusted by the caller,</item>
    /// <item>the target run root does not exist when the attempt begins,</item>
    /// <item>no reparse point, symbolic link, or junction on the ancestor or
    /// target path is followed,</item>
    /// <item>the target directory is created new,</item>
    /// <item>the filesystem identity and final path after creation correspond
    /// to the expected run root,</item>
    /// <item>the directory is empty,</item>
    /// <item>a receipt is returned only after every check above succeeds.</item>
    /// </list>
    /// </para>
    /// <para>
    /// If an exception occurs after the directory is created, an empty
    /// directory may remain. The caller must not blindly retry the same
    /// operation; a later recovery pass re-examines the root as an empty or
    /// tmp-only root. An implementation that cannot satisfy these guarantees
    /// must not return success.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunRootProvisioner
    {
        CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation);
    }
}
