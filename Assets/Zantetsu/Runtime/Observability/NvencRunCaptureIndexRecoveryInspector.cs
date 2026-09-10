using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous, read-only production Phase 0.11 NVENC Capture
    /// Index recovery inspector. It serializes the authoritative plan once,
    /// observes <c>capture.index.tmp</c> and then <c>capture.index</c> directly
    /// under the final Run root, and returns exactly one snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both documents are always observed, in the restart order the design
    /// fixes: the temporary first, the final second. A matching final index is
    /// not the end of the question, because a foreign temporary beside it is
    /// still a collision.
    /// </para>
    /// <para>
    /// The paths are fixed basenames under
    /// <see cref="CaptureRunRootLayout.FinalRunRoot"/>, opened through the
    /// existing no-follow opener; no Win32 or NT entry point is duplicated
    /// here, nothing re-resolves a path with <c>File.Exists</c> or a directory
    /// listing, and the staging root, <c>publication.plan</c>, the chunk, and
    /// the legacy plan temporary are never touched.
    /// </para>
    /// <para>
    /// A status means exactly what
    /// <see cref="NvencRunCaptureIndexObservationStatus"/> says it means. Only
    /// a confirmed missing entry is Absent, and only an ordinary file that was
    /// opened, read to its end within the limit, and failed to decode is
    /// Invalid. A reparse point or other non-file kind, a path escaping the Run
    /// root, an open or read failure, and a platform without no-follow support
    /// all leave this method by an exception with no snapshot at all: a later
    /// commit is allowed to delete a temporary called Invalid, so an unusable
    /// observation must never be recorded as unusable content.
    /// </para>
    /// <para>
    /// Each document is read once through a buffer that grows only as far as
    /// the bytes actually observed, never seeking, never asking the stream for
    /// its length, never re-opening, and never reading past one byte over the
    /// limit. Neither the raw bytes nor a decoded plan outlives the call; the
    /// expected canonical bytes exist only for the comparison inside it. The
    /// stream and its handle are released exactly once on every path.
    /// </para>
    /// <para>
    /// This type classifies nothing, deletes, renames, replaces, and commits
    /// nothing, runs no CaptureComplete, releases no lock, holds no state
    /// between calls, owns no thread, queue, or task, and is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryInspector
        : INvencRunCaptureIndexRecoveryInspector
    {
        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string CaptureIndexName = "capture.index";

        private const int InitialReadByteCount = 64 * 1024;

        private readonly ICaptureArtifactNoFollowOpener _opener;

        internal NvencRunCaptureIndexRecoveryInspector()
            : this(CaptureArtifactNoFollowOpen.Create())
        {
        }

        internal NvencRunCaptureIndexRecoveryInspector(ICaptureArtifactNoFollowOpener opener)
        {
            _opener = opener ?? throw new ArgumentNullException(nameof(opener));
        }

        public NvencRunCaptureIndexRecoveryInspectionSnapshot Inspect(
            NvencRunCaptureIndexRecoveryInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!_opener.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Phase 0.11 NVENC Capture Index recovery inspection requires no-follow file support on this platform.");
            }

            // The authoritative plan is serialized exactly once, and the bytes
            // live only for the two comparisons below.
            byte[] expected = CapturePublicationPlanCodec.SerializeCanonical(
                operation.AuthoritativePlan);

            string finalRunRoot = operation.RootLayout.FinalRunRoot;

            NvencRunCaptureIndexObservationStatus temporaryStatus = Observe(
                finalRunRoot, CaptureIndexTemporaryName, expected);
            NvencRunCaptureIndexObservationStatus finalStatus = Observe(
                finalRunRoot, CaptureIndexName, expected);

            return new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                operation, finalStatus, temporaryStatus);
        }

        private NvencRunCaptureIndexObservationStatus Observe(
            string finalRunRoot,
            string basename,
            byte[] expected)
        {
            CaptureArtifactNoFollowOpenResult opened = _opener.TryOpen(finalRunRoot, basename);

            switch (opened.Status)
            {
                case CaptureArtifactNoFollowOpenStatus.Opened:
                    break;

                case CaptureArtifactNoFollowOpenStatus.Absent:
                    return NvencRunCaptureIndexObservationStatus.Absent;

                case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                    throw new IOException(
                        basename + " is a reparse point or is not an ordinary file.");

                case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                    throw new IOException(basename + " escapes the final Run root.");

                case CaptureArtifactNoFollowOpenStatus.IoFailure:
                    throw new IOException("Failed to open " + basename + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow open is not supported on this platform.");
            }

            try
            {
                // One byte past the limit is read so the limit itself can be
                // observed; nothing is read after that, and the buffer grows
                // only as far as the bytes actually observed.
                const int Limit = CapturePublicationPlanCodec.MaximumCanonicalByteCount;
                byte[] buffer = new byte[InitialReadByteCount];
                int count = 0;
                while (count <= Limit)
                {
                    if (count == buffer.Length)
                    {
                        Array.Resize(
                            ref buffer,
                            buffer.Length <= (Limit + 1) / 2 ? buffer.Length * 2 : Limit + 1);
                    }

                    int read = opened.Stream.Read(buffer, count, buffer.Length - count);
                    if (read == 0)
                    {
                        break;
                    }

                    count = checked(count + read);
                }

                if (count > Limit)
                {
                    // Too large to be a canonical document at all, and never
                    // decoded.
                    return NvencRunCaptureIndexObservationStatus.LimitExceeded;
                }

                if (count == 0)
                {
                    return NvencRunCaptureIndexObservationStatus.Invalid;
                }

                byte[] canonical = new byte[count];
                Array.Copy(buffer, canonical, count);

                try
                {
                    // The codec's own canonical decision is the authority; the
                    // decoded plan itself is deliberately discarded.
                    CapturePublicationPlanCodec.DeserializeCanonical(canonical);
                }
                catch (ArgumentException)
                {
                    return NvencRunCaptureIndexObservationStatus.Invalid;
                }
                catch (InvalidOperationException)
                {
                    return NvencRunCaptureIndexObservationStatus.Invalid;
                }

                // The whole comparison is these bytes against the once-built
                // expected bytes: no second plan validator, no semantic
                // comparer.
                return HasSameBytes(canonical, expected)
                    ? NvencRunCaptureIndexObservationStatus.MatchesAuthoritative
                    : NvencRunCaptureIndexObservationStatus.CanonicalMismatch;
            }
            finally
            {
                opened.Close();
            }
        }

        private static bool HasSameBytes(byte[] observed, byte[] expected)
        {
            if (observed.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < observed.Length; i++)
            {
                if (observed[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
