using System;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A single acquired Capture Run OS lock: the exclusively opened lock file
    /// handle plus the verified <c>.locks</c> and trusted base directory
    /// handles that pin the namespace against a parent-directory swap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live OS file handle is the source of truth for ownership; the lock
    /// file's existence or content is never treated as ownership evidence.
    /// Disposal closes the file handle first (releasing exclusivity), then the
    /// <c>.locks</c> directory handle, then the base directory handle. It is
    /// idempotent and never deletes, truncates, or writes the lock file.
    /// </para>
    /// <para>
    /// The directory handles are opened without delete sharing, so while this
    /// handle is alive the base and <c>.locks</c> directories cannot be
    /// renamed or deleted, pinning the resolved lock identity.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunLockOsHandle : ICaptureRunLockHandle
    {
        private readonly string _lockPath;
        private SafeFileHandle _fileHandle;
        private SafeFileHandle _locksDirectoryHandle;
        private SafeFileHandle _baseDirectoryHandle;

        internal CaptureRunLockOsHandle(
            string lockPath,
            SafeFileHandle fileHandle,
            SafeFileHandle locksDirectoryHandle,
            SafeFileHandle baseDirectoryHandle)
        {
            if (lockPath == null)
            {
                throw new ArgumentNullException(nameof(lockPath));
            }

            if (fileHandle == null)
            {
                throw new ArgumentNullException(nameof(fileHandle));
            }

            if (locksDirectoryHandle == null)
            {
                throw new ArgumentNullException(nameof(locksDirectoryHandle));
            }

            if (baseDirectoryHandle == null)
            {
                throw new ArgumentNullException(nameof(baseDirectoryHandle));
            }

            _lockPath = lockPath;
            _fileHandle = fileHandle;
            _locksDirectoryHandle = locksDirectoryHandle;
            _baseDirectoryHandle = baseDirectoryHandle;
        }

        public string LockPath => _lockPath;

        /// <summary>
        /// Reports the live file handle's current state. It is <c>true</c>
        /// while the exclusive file handle is open and becomes <c>false</c>
        /// once disposal has closed it.
        /// </summary>
        public bool IsCreated
        {
            get
            {
                SafeFileHandle handle = _fileHandle;
                return handle != null && !handle.IsInvalid;
            }
        }

        public void Dispose()
        {
            CloseHandle(ref _fileHandle);
            CloseHandle(ref _locksDirectoryHandle);
            CloseHandle(ref _baseDirectoryHandle);
        }

        private static void CloseHandle(ref SafeFileHandle handle)
        {
            SafeFileHandle current = handle;
            handle = null;
            if (current != null)
            {
                current.Dispose();
            }
        }
    }
}
