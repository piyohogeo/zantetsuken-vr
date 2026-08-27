using System;
using System.Globalization;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Deterministically maps a <see cref="CaptureFrameRequest"/> to a
    /// <see cref="CaptureFramePngArtifactDestination"/> under a fixed directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The generated basename is
    /// <c>capture-{TestRunId:D20}-{CaptureFrameId:D20}</c> rendered with
    /// <see cref="CultureInfo.InvariantCulture"/>. The zero-padded, fixed-width
    /// decimal fields make the lexicographic order of the generated file names
    /// match the numeric order of the positive IDs.
    /// </para>
    /// <para>
    /// The factory is fully deterministic: the same instance and request always
    /// produce byte-for-byte identical paths. It depends on no current culture,
    /// current time, GUID, Unity frame counter, static mutable state, or
    /// filesystem content. It performs no directory existence check, creates no
    /// directory, writes no file, and reserves or overwrites nothing; collisions
    /// with existing files remain the responsibility of the file store.
    /// </para>
    /// <para>
    /// Holds only the normalized directory string and owns no request,
    /// destination, PNG, or sidecar. It does not implement
    /// <see cref="IDisposable"/> and uses no Unity static API, LINQ, or
    /// reflection. <see cref="Create"/> allocates path strings, which is
    /// acceptable on the persistence cold path.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactDestinationFactory
    {
        public const string FileNamePrefix = "capture-";

        private readonly string _directoryPath;

        public CaptureFramePngArtifactDestinationFactory(string directoryPath)
        {
            if (directoryPath == null)
            {
                throw new ArgumentNullException(nameof(directoryPath));
            }

            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("Directory path must not be empty or whitespace.", nameof(directoryPath));
            }

            if (!Path.IsPathFullyQualified(directoryPath))
            {
                throw new ArgumentException("Directory path must be fully qualified.", nameof(directoryPath));
            }

            _directoryPath = NormalizeDirectory(directoryPath);
        }

        public string DirectoryPath => _directoryPath;

        /// <summary>
        /// Builds the deterministic PNG and sidecar destination for the given
        /// request.
        /// </summary>
        public CaptureFramePngArtifactDestination Create(in CaptureFrameRequest frameRequest)
        {
            if (!frameRequest.IsValid)
            {
                throw new ArgumentException("Frame request must be valid.", nameof(frameRequest));
            }

            long testRunId = frameRequest.TraceContext.TestRunId;
            long captureFrameId = frameRequest.TraceContext.CaptureFrameId;

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameRequest), testRunId, "Test run ID must be greater than zero.");
            }

            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameRequest), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            string basename = FileNamePrefix
                + testRunId.ToString("D20", CultureInfo.InvariantCulture)
                + "-"
                + captureFrameId.ToString("D20", CultureInfo.InvariantCulture);

            string pngPath = Path.Combine(_directoryPath, basename + ".png");
            string sidecarPath = Path.Combine(_directoryPath, basename + ".json");

            return new CaptureFramePngArtifactDestination(captureFrameId, pngPath, sidecarPath);
        }

        private static string NormalizeDirectory(string directoryPath)
        {
            string fullPath = Path.GetFullPath(directoryPath);
            string root = Path.GetPathRoot(fullPath);

            int end = fullPath.Length;
            while (end > 0 && IsDirectorySeparator(fullPath[end - 1]))
            {
                end--;
            }

            if (end == fullPath.Length)
            {
                return fullPath;
            }

            string trimmed = fullPath.Substring(0, end);
            if (root != null && root.Length > 0 && IsDirectorySeparator(root[root.Length - 1])
                && string.Equals(trimmed, root.Substring(0, root.Length - 1), StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return trimmed;
        }

        private static bool IsDirectorySeparator(char c)
        {
            return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
        }
    }
}
