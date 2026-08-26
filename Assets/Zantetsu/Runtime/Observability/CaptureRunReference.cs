using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable run-scoped reference values shared by every capture frame
    /// record in a run: test run, test case, build, scene, random seed, capture
    /// profile, and the run manifest content SHA-256. Build, scene, and hash
    /// strings are fixed here once and never copied per frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is the source of truth for run information referenced by a
    /// <c>CaptureFrameRecord</c>. The manifest itself is not retained; only the
    /// needed values are copied and fixed at construction.
    /// </para>
    /// <para>
    /// The run manifest content hash is computed exactly once, in the
    /// constructor, and validated against
    /// <see cref="TraceRunManifestCodec.ComputeContentSha256(TraceRunManifest)"/>.
    /// It is never recomputed by later property access.
    /// </para>
    /// </remarks>
    public sealed class CaptureRunReference
    {
        public CaptureRunReference(
            TraceRunManifest manifest,
            long testCaseId,
            int captureProfileId,
            string runManifestContentSha256)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (testCaseId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testCaseId), testCaseId, "Test case ID must be greater than zero.");
            }

            if (captureProfileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureProfileId), captureProfileId, "Capture profile ID must be greater than zero.");
            }

            if (runManifestContentSha256 == null)
            {
                throw new ArgumentNullException(nameof(runManifestContentSha256));
            }

            string normalizedHash = NormalizeContentSha256(runManifestContentSha256);

            string computedHash = TraceRunManifestCodec.ComputeContentSha256(manifest);
            if (!string.Equals(normalizedHash, computedHash, StringComparison.Ordinal))
            {
                throw new ArgumentException("Run manifest content SHA-256 does not match the manifest.", nameof(runManifestContentSha256));
            }

            TestRunId = manifest.TestRunId;
            TestCaseId = testCaseId;
            BuildId = manifest.BuildId;
            SceneId = manifest.SceneId;
            RandomSeed = manifest.RandomSeed;
            CaptureProfileId = captureProfileId;
            RunManifestContentSha256 = normalizedHash;
        }

        public long TestRunId { get; }

        public long TestCaseId { get; }

        public string BuildId { get; }

        public string SceneId { get; }

        public long RandomSeed { get; }

        public int CaptureProfileId { get; }

        public string RunManifestContentSha256 { get; }

        private static string NormalizeContentSha256(string hash)
        {
            if (hash.Length != 64)
            {
                throw new ArgumentException("Run manifest content SHA-256 must be exactly 64 hexadecimal characters.", nameof(hash));
            }

            for (int i = 0; i < hash.Length; i++)
            {
                char c = hash[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    throw new ArgumentException("Run manifest content SHA-256 must be ASCII hexadecimal.", nameof(hash));
                }
            }

            return hash.ToLowerInvariant();
        }
    }
}
