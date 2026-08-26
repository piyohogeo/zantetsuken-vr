using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Atomically publishes and verifies a version 1 trace run bundle: a
    /// directory containing <c>bundle.index</c>, <c>manifest.json</c> and
    /// <c>trace.bin</c>.
    /// </summary>
    public static class TraceRunBundleStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        /// <summary>
        /// Writes a complete bundle into <paramref name="bundleDirectoryPath"/>,
        /// publishing it atomically by renaming a temporary directory into
        /// place. An existing destination is never overwritten.
        /// </summary>
        public static void SaveAtomic(string bundleDirectoryPath, TraceCaptureSnapshot snapshot, TraceRunManifest manifest)
        {
            ValidatePath(bundleDirectoryPath, out string finalPath);

            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (snapshot.EventCount != manifest.EventCount ||
                snapshot.TriggerHistoryCount != manifest.TriggerHistoryCount ||
                snapshot.CapturedPostRollCount != manifest.CapturedPostRollCount ||
                snapshot.WasHistoryOverwrittenAtTrigger != manifest.WasHistoryOverwrittenAtTrigger)
            {
                throw new ArgumentException("Snapshot and manifest metadata do not match.");
            }

            if ((long)snapshot.EventCount != (long)snapshot.TriggerHistoryCount + (long)snapshot.CapturedPostRollCount)
            {
                throw new InvalidOperationException("Snapshot event counts are inconsistent.");
            }

            long expectedTraceLength = CheckedTraceLength(snapshot.EventCount);

            string parent = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException("Bundle parent directory does not exist: " + parent);
            }

            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                throw new IOException("A file or directory already exists at the bundle path: " + finalPath);
            }

            string tempDir = Path.Combine(parent, GenerateTempDirName());
            Directory.CreateDirectory(tempDir);

            try
            {
                TraceEvent[] events = new TraceEvent[snapshot.EventCount];
                snapshot.CopyEventsTo(events, 0);

                string tracePath = Path.Combine(tempDir, TraceRunBundleFormat.TraceFileName);
                TraceBinaryFileStore.SaveAtomic(tracePath, events, 0, events.Length);

                byte[] manifestBytes = TraceRunManifestCodec.SerializeCanonical(manifest);
                string manifestPath = Path.Combine(tempDir, TraceRunBundleFormat.ManifestFileName);
                WriteFile(manifestPath, manifestBytes);

                string traceHash;
                long traceLength;
                using (FileStream traceStream = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    traceLength = traceStream.Length;
                    if (traceLength != expectedTraceLength)
                    {
                        throw new InvalidOperationException("Serialized trace length does not match the expected length.");
                    }

                    traceHash = ComputeSha256HexFromStream(traceStream);
                }

                string manifestHash = ComputeSha256Hex(manifestBytes);
                long manifestLength = manifestBytes.Length;

                byte[] indexBytes = BuildIndexBytes(manifestLength, manifestHash, traceLength, traceHash);
                string indexPath = Path.Combine(tempDir, TraceRunBundleFormat.IndexFileName);
                WriteFile(indexPath, indexBytes);

                Directory.Move(tempDir, finalPath);
            }
            catch
            {
                TryDeleteDirectory(tempDir);
                throw;
            }
        }

        /// <summary>
        /// Verifies and loads a version 1 bundle, returning its manifest,
        /// snapshot and content hashes.
        /// </summary>
        public static TraceRunBundle Load(string bundleDirectoryPath, int maximumEventCount)
        {
            ValidatePath(bundleDirectoryPath, out string finalPath);

            if (maximumEventCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEventCount), maximumEventCount, "Maximum event count must not be negative.");
            }

            if (!Directory.Exists(finalPath))
            {
                throw new DirectoryNotFoundException("Bundle directory does not exist: " + finalPath);
            }

            VerifyOnlyThreeFiles(finalPath);

            string indexPath = Path.Combine(finalPath, TraceRunBundleFormat.IndexFileName);
            IndexInfo index = ParseIndex(ReadIndexFile(indexPath));

            string manifestPath = Path.Combine(finalPath, TraceRunBundleFormat.ManifestFileName);
            byte[] manifestBytes = ReadFileBounded(manifestPath, index.ManifestLength, TraceRunManifestCodec.MaximumCanonicalByteCount);
            string manifestHash = ComputeSha256Hex(manifestBytes);

            if (manifestHash != index.ManifestHash)
            {
                throw new InvalidDataException("manifest.json SHA-256 does not match the index.");
            }

            TraceRunManifest manifest = TraceRunManifestCodec.DeserializeCanonical(manifestBytes);

            if (manifest.EventCount > maximumEventCount)
            {
                throw new InvalidDataException("Bundle event count exceeds the maximum allowed.");
            }

            long expectedTraceLength = CheckedTraceLength(manifest.EventCount);
            if (index.TraceLength != expectedTraceLength)
            {
                throw new InvalidDataException("trace.bin length does not match the manifest event count.");
            }

            string tracePath = Path.Combine(finalPath, TraceRunBundleFormat.TraceFileName);
            TraceEvent[] events = ReadTraceFile(tracePath, index.TraceHash, expectedTraceLength, manifest.EventCount);

            TraceCaptureSnapshot snapshot = new TraceCaptureSnapshot(
                events,
                manifest.TriggerHistoryCount,
                manifest.CapturedPostRollCount,
                manifest.WasHistoryOverwrittenAtTrigger);

            return new TraceRunBundle(manifest, snapshot, manifestHash, index.TraceHash);
        }

        private static TraceEvent[] ReadTraceFile(string tracePath, string expectedHash, long expectedLength, int expectedEventCount)
        {
            using (FileStream stream = new FileStream(tracePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long actualLength = stream.Length;
                if (actualLength != expectedLength)
                {
                    throw new InvalidDataException("trace.bin length does not match the expected length.");
                }

                string actualHash = ComputeSha256HexFromStream(stream);

                if (actualHash != expectedHash)
                {
                    throw new InvalidDataException("trace.bin SHA-256 does not match the index.");
                }

                stream.Position = 0;
                return TraceBinaryCodec.Read(stream, expectedEventCount);
            }
        }

        private static long CheckedTraceLength(int eventCount)
        {
            try
            {
                return checked(TraceBinaryFormat.HeaderSize + (long)eventCount * TraceBinaryFormat.EventRecordSize);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("Trace length computation overflow.");
            }
        }

        private static byte[] BuildIndexBytes(long manifestLength, string manifestHash, long traceLength, string traceHash)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append(TraceRunBundleFormat.IndexHeaderLine).Append('\n');
            sb.Append(TraceRunBundleFormat.ManifestFileName).Append(' ')
              .Append(manifestLength.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(manifestHash).Append('\n');
            sb.Append(TraceRunBundleFormat.TraceFileName).Append(' ')
              .Append(traceLength.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(traceHash).Append('\n');

            byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());
            if (bytes.Length > TraceRunBundleFormat.MaximumIndexByteCount)
            {
                throw new InvalidOperationException("Bundle index exceeds the maximum byte count.");
            }

            return bytes;
        }

        private static void WriteFile(string path, byte[] bytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }

        private static IndexInfo ParseIndex(byte[] indexBytes)
        {
            if (indexBytes.Length == 0)
            {
                throw new InvalidDataException("Bundle index is empty.");
            }

            if (indexBytes.Length > TraceRunBundleFormat.MaximumIndexByteCount)
            {
                throw new InvalidDataException("Bundle index exceeds the maximum byte count.");
            }

            if (HasUtf8Bom(indexBytes))
            {
                throw new InvalidDataException("Bundle index must not have a UTF-8 BOM.");
            }

            string text;
            try
            {
                text = Utf8Strict.GetString(indexBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Bundle index is not valid UTF-8.", ex);
            }

            if (text.IndexOf('\r') >= 0)
            {
                throw new InvalidDataException("Bundle index must use LF line endings.");
            }

            string[] lines = text.Split('\n');
            if (lines.Length != 4 || lines[3].Length != 0)
            {
                throw new InvalidDataException("Bundle index must contain exactly three lines.");
            }

            if (lines[0] != TraceRunBundleFormat.IndexHeaderLine)
            {
                throw new InvalidDataException("Unsupported bundle index header.");
            }

            ParseFileLine(lines[1], TraceRunBundleFormat.ManifestFileName, out long manifestLength, out string manifestHash);
            ParseFileLine(lines[2], TraceRunBundleFormat.TraceFileName, out long traceLength, out string traceHash);

            return new IndexInfo(manifestLength, manifestHash, traceLength, traceHash);
        }

        private static void ParseFileLine(string line, string expectedName, out long length, out string hash)
        {
            string[] parts = line.Split(' ');
            if (parts.Length != 3)
            {
                throw new InvalidDataException("Bundle index line has an invalid format.");
            }

            if (parts[0] != expectedName)
            {
                throw new InvalidDataException("Bundle index file name mismatch.");
            }

            if (!TryParseDecimalLength(parts[1], out length))
            {
                throw new InvalidDataException("Bundle index byte length is invalid.");
            }

            hash = parts[2];
            if (!IsLowercaseHex64(hash))
            {
                throw new InvalidDataException("Bundle index SHA-256 is invalid.");
            }
        }

        private static bool TryParseDecimalLength(string s, out long length)
        {
            length = 0;
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            if (s.Length > 1 && s[0] == '0')
            {
                return false; // no leading zeros
            }

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out length) && length >= 0;
        }

        private static void VerifyOnlyThreeFiles(string directory)
        {
            if (Directory.GetDirectories(directory).Length != 0)
            {
                throw new InvalidDataException("Bundle must not contain subdirectories.");
            }

            string[] files = Directory.GetFiles(directory);
            if (files.Length != 3)
            {
                throw new InvalidDataException("Bundle must contain exactly three files.");
            }

            bool hasIndex = false;
            bool hasManifest = false;
            bool hasTrace = false;
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name == TraceRunBundleFormat.IndexFileName)
                {
                    hasIndex = true;
                }
                else if (name == TraceRunBundleFormat.ManifestFileName)
                {
                    hasManifest = true;
                }
                else if (name == TraceRunBundleFormat.TraceFileName)
                {
                    hasTrace = true;
                }
                else
                {
                    throw new InvalidDataException("Unexpected file in bundle: " + name);
                }
            }

            if (!hasIndex || !hasManifest || !hasTrace)
            {
                throw new InvalidDataException("Bundle is missing a required file.");
            }
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(bytes));
            }
        }

        private static string ComputeSha256HexFromStream(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(stream));
            }
        }

        private static byte[] ReadIndexFile(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = stream.Length;
                if (length <= 0 || length > TraceRunBundleFormat.MaximumIndexByteCount)
                {
                    throw new InvalidDataException("Bundle index length is outside the allowed range.");
                }

                byte[] bytes = new byte[(int)length];
                ReadExactly(stream, bytes, 0, bytes.Length);
                return bytes;
            }
        }

        private static byte[] ReadFileBounded(string path, long expectedLength, long maxBytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = stream.Length;
                if (length != expectedLength)
                {
                    throw new InvalidDataException("File length does not match the index.");
                }

                if (length <= 0 || length > maxBytes)
                {
                    throw new InvalidDataException("File length is outside the allowed range.");
                }

                byte[] bytes = new byte[(int)length];
                ReadExactly(stream, bytes, 0, bytes.Length);
                return bytes;
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                {
                    throw new InvalidDataException("Unexpected end of file.");
                }

                total += read;
            }
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static bool IsLowercaseHex64(string s)
        {
            if (s == null || s.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0x0F];
            }

            return new string(chars);
        }

        private static string GenerateTempDirName()
        {
            return "zantetsu.bundle." + Guid.NewGuid().ToString("N") + ".tmp";
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Best-effort cleanup: preserve the original exception.
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

        private struct IndexInfo
        {
            public readonly long ManifestLength;
            public readonly string ManifestHash;
            public readonly long TraceLength;
            public readonly string TraceHash;

            public IndexInfo(long manifestLength, string manifestHash, long traceLength, string traceHash)
            {
                ManifestLength = manifestLength;
                ManifestHash = manifestHash;
                TraceLength = traceLength;
                TraceHash = traceHash;
            }
        }
    }
}
