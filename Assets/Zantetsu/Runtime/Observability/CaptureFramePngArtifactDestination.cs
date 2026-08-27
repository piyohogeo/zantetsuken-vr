using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable correlation of a capture frame ID with its fully-qualified PNG
    /// and sidecar destination paths. Both files must live in the same
    /// directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All properties are fixed at construction and there are no public setters.
    /// Validation is purely lexical: no directory or file existence check is
    /// performed, no directory is created, no file is written, and no hash is
    /// computed. Paths are normalized with <see cref="Path.GetFullPath"/> and
    /// extensions are compared case-insensitively.
    /// </para>
    /// <para>
    /// The type owns no PNG or sidecar and does not implement
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactDestination
    {
        public CaptureFramePngArtifactDestination(
            long captureFrameId,
            string pngDestinationPath,
            string sidecarDestinationPath)
        {
            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            string pngFullPath = NormalizePath(pngDestinationPath, nameof(pngDestinationPath), ".png");
            string sidecarFullPath = NormalizePath(sidecarDestinationPath, nameof(sidecarDestinationPath), ".json");

            if (string.Equals(pngFullPath, sidecarFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("PNG and sidecar destination paths must not be the same.", nameof(sidecarDestinationPath));
            }

            string pngDirectory = Path.GetDirectoryName(pngFullPath);
            string sidecarDirectory = Path.GetDirectoryName(sidecarFullPath);

            if (!string.Equals(pngDirectory, sidecarDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("PNG and sidecar destination paths must share the same directory.", nameof(sidecarDestinationPath));
            }

            CaptureFrameId = captureFrameId;
            PngDestinationPath = pngFullPath;
            SidecarDestinationPath = sidecarFullPath;
            DirectoryPath = pngDirectory;
        }

        public long CaptureFrameId { get; }

        public string PngDestinationPath { get; }

        public string SidecarDestinationPath { get; }

        public string DirectoryPath { get; }

        private static string NormalizePath(string path, string paramName, string expectedExtension)
        {
            if (path == null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path must not be empty or whitespace.", paramName);
            }

            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("Path must be fully qualified.", paramName);
            }

            string fullPath = Path.GetFullPath(path);

            if (!string.Equals(Path.GetExtension(fullPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Path must end with '" + expectedExtension + "'.", paramName);
            }

            return fullPath;
        }
    }
}
