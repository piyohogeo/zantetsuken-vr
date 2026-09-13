using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Puts a finished Run's trace on disk, and reads one back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving writes to a file of its own in the destination's directory and
    /// only moves it into place once the whole of it has been written, flushed
    /// and closed. That move is where a saved trace becomes one: until it
    /// happens there is no file at the destination, and if anything before it
    /// fails there never will be from this call. A destination that already
    /// exists is refused before anything is written - nothing here overwrites,
    /// removes, repairs, retries, or falls back to another name - and the
    /// directory it goes in has to exist already.
    /// </para>
    /// <para>
    /// This is a boundary for finished work inside one process, not a promise
    /// about power loss: the file is flushed and closed before the move, but
    /// nothing here flushes the directory or recovers from a crash.
    /// </para>
    /// <para>
    /// Reading opens the file, reads it once, and closes it again whether that
    /// worked or not. The result of the Run, the history behind it, and the
    /// destination records are handed to all belong to the caller: none of them
    /// is taken over or released here. A file that turns out to be malformed is
    /// refused by the reader as it stands, which means a destination may
    /// already have been handed the records that came before the trouble -
    /// there is nothing here that takes those back.
    /// </para>
    /// </remarks>
    internal static class TracePagedHistoryFileStore
    {
        /// <summary>
        /// Saves one Run's trace and returns how many bytes it holds. The file
        /// appears at <paramref name="destinationPath"/> only if all of it was
        /// written.
        /// </summary>
        internal static long SaveAtomic(string destinationPath, in TracePagedRunResult result)
        {
            if (destinationPath == null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            if (destinationPath.Length == 0 || !Path.IsPathRooted(destinationPath))
            {
                throw new ArgumentException(
                    "The destination must be an absolute path.", nameof(destinationPath));
            }

            string directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(
                    "The directory a saved trace would go in does not exist: " + directory);
            }

            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            {
                throw new IOException("There is already something at " + destinationPath);
            }

            // A file of this call's own, next to the destination so the move
            // stays inside one directory.
            string temporaryPath = Path.Combine(
                directory,
                Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".partial");

            // Made with CreateNew, so this call either made this exact file or
            // did not get one at all - and only a file it made is ever removed.
            FileStream file = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            bool published = false;
            try
            {
                long written = TracePagedHistoryFileWriter.Write(file, result);
                file.Flush();
                file.Dispose();

                // Nothing is overwritten: a destination that appeared in the
                // meantime makes this fail rather than replacing it.
                File.Move(temporaryPath, destinationPath);
                published = true;
                return written;
            }
            finally
            {
                if (!published)
                {
                    file.Dispose();
                    TryRemove(temporaryPath);
                }
            }
        }

        /// <summary>
        /// Reads one saved trace, handing every record to
        /// <paramref name="destination"/>, and returns what its header said.
        /// </summary>
        internal static TracePagedHistoryFileSummary Read(
            string sourcePath, int maxPayloadLength, ITraceRecordDestination destination)
        {
            if (sourcePath == null)
            {
                throw new ArgumentNullException(nameof(sourcePath));
            }

            if (sourcePath.Length == 0 || !Path.IsPathRooted(sourcePath))
            {
                throw new ArgumentException("The source must be an absolute path.", nameof(sourcePath));
            }

            // Opened for reading only, and read straight through: nothing here
            // asks how long the file is, seeks about in it, keeps it, or tries
            // again.
            using (FileStream file = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return TracePagedHistoryFileReader.Read(file, maxPayloadLength, destination);
            }
        }

        /// <summary>
        /// Removes the unfinished file this call made, if it still can. A
        /// failure here is not worth losing the failure that caused it.
        /// </summary>
        private static void TryRemove(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
