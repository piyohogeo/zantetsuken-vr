using System;
using System.IO;

namespace Zantetsu.Trace
{
    /// <summary>
    /// Publishes finalized trace binaries atomically. Data is written to a
    /// unique temporary file in the destination directory and then renamed
    /// into place; an existing destination is never overwritten.
    /// </summary>
    public static class TraceBinaryFileStore
    {
        /// <summary>
        /// Writes <paramref name="count"/> events starting at
        /// <paramref name="sourceIndex"/> to <paramref name="path"/>, publishing
        /// the file atomically. The destination's parent directory must already
        /// exist and an existing destination is never overwritten.
        /// </summary>
        public static void SaveAtomic(string path, TraceEvent[] events, int sourceIndex, int count)
        {
            ValidatePath(path, out string finalPath);

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (sourceIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sourceIndex), sourceIndex, "Source index must not be negative.");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count must not be negative.");
            }

            if ((long)sourceIndex + (long)count > (long)events.Length)
            {
                throw new ArgumentException("Source index plus count exceeds the bounds of the events array.", nameof(count));
            }

            string directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The destination directory does not exist: " + directory);
            }

            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                throw new IOException("A file or directory already exists at the destination path: " + finalPath);
            }

            string tempPath = Path.Combine(directory, GenerateTempFileName());

            try
            {
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    TraceBinaryCodec.Write(stream, events, sourceIndex, count);
                    stream.Flush(flushToDisk: true);
                }
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            try
            {
                File.Move(tempPath, finalPath);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        /// <summary>
        /// Loads the events stored at <paramref name="path"/>. The file is opened
        /// read-only and the decoded event order is preserved.
        /// </summary>
        public static TraceEvent[] Load(string path, int maximumEventCount)
        {
            ValidatePath(path, out string finalPath);

            if (maximumEventCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEventCount), maximumEventCount, "Maximum event count must not be negative.");
            }

            using (FileStream stream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return TraceBinaryCodec.Read(stream, maximumEventCount);
            }
        }

        private static void ValidatePath(string path, out string fullPath)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path must not be empty or whitespace.", nameof(path));
            }

            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("Path must be a fully qualified absolute path.", nameof(path));
            }

            fullPath = Path.GetFullPath(path);
        }

        private static string GenerateTempFileName()
        {
            return "zantetsu." + Guid.NewGuid().ToString("N") + ".tmp";
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup: preserve the original exception.
            }
        }
    }
}
