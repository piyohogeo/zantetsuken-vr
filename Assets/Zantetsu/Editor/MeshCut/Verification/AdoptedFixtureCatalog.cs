using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Zantetsu.Core.Geometry;

namespace Zantetsu.MeshCut.Verification
{
    public enum AdoptedFixtureUse
    {
        RenderCut,
        PhysicsCook,
        Correctness,
    }

    public sealed class AdoptedFixture
    {
        internal AdoptedFixture(string artifactId, string sourceFixtureId, string relativePath,
            string geometryPath, long geometryByteLength, string geometryContentSha256,
            string geometryKind)
        {
            ArtifactId = artifactId;
            SourceFixtureId = sourceFixtureId;
            RelativePath = relativePath;
            m_geometryPath = geometryPath;
            GeometryByteLength = geometryByteLength;
            m_geometryContentSha256 = geometryContentSha256;
            m_geometryKind = geometryKind;
        }

        readonly string m_geometryPath;
        readonly string m_geometryContentSha256;
        readonly string m_geometryKind;
        ZcgDocument m_geometry;

        public string ArtifactId { get; }
        public string SourceFixtureId { get; }
        public string RelativePath { get; }
        public long GeometryByteLength { get; }

        public ZcgDocument Geometry
        {
            get
            {
                if (m_geometry != null)
                    return m_geometry;

                byte[] bytes = File.ReadAllBytes(m_geometryPath);
                if (bytes.LongLength != GeometryByteLength)
                    throw new InvalidDataException("Geometry byte length mismatch: " + ArtifactId);
                if (!string.Equals(ZcgGeometryCodec.ComputeSha256(bytes), m_geometryContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Geometry hash mismatch: " + ArtifactId);

                ZcgDocument geometry = ZcgGeometryCodec.Read(bytes, ZcgDecodeLimits.Phase02);
                if (!string.Equals(geometry.Kind.ToString(), m_geometryKind, StringComparison.Ordinal))
                    throw new InvalidDataException("Geometry kind mismatch: " + ArtifactId);
                m_geometry = geometry;
                return m_geometry;
            }
        }
    }

    /// <summary>
    /// Editor-only reader for the frozen Phase 0.2 handoff. Public Synthetic and local
    /// Licensed indexes use separate validation policies. Only Geometry bindings used by
    /// current harnesses are exposed; generation, receipt, and Structural Slab data are not.
    /// </summary>
    public sealed class AdoptedFixtureCatalog
    {
        public const string RelativeIndexPath =
            "Tools/Phase02/Public/Synthetic/phase02-v1/synthetic-fixture-suite-index.json";

        readonly Dictionary<AdoptedFixtureUse, AdoptedFixture[]> m_byUse;

        AdoptedFixtureCatalog(string datasetId,
            Dictionary<AdoptedFixtureUse, AdoptedFixture[]> byUse)
        {
            DatasetId = datasetId;
            m_byUse = byUse;
        }

        public string DatasetId { get; }

        public IReadOnlyList<AdoptedFixture> Resolve(AdoptedFixtureUse use)
        {
            if (!m_byUse.TryGetValue(use, out AdoptedFixture[] fixtures))
                return Array.Empty<AdoptedFixture>();
            return fixtures;
        }

        public static AdoptedFixtureCatalog Load(string repositoryRoot)
        {
            return Load(repositoryRoot, RelativeIndexPath, ".", 1, "Synthetic", true,
                new[]
                {
                    AdoptedFixtureUse.RenderCut,
                    AdoptedFixtureUse.PhysicsCook,
                    AdoptedFixtureUse.Correctness,
                });
        }

        public static AdoptedFixtureCatalog LoadLicensed(string privateRepositoryRoot)
        {
            return Load(privateRepositoryRoot, "Working/Phase0.2/Adopted/dataset-index.json",
                "payload", 2, "LicensedThirdParty", false,
                new[]
                {
                    AdoptedFixtureUse.RenderCut,
                    AdoptedFixtureUse.PhysicsCook,
                });
        }

        static AdoptedFixtureCatalog Load(string repositoryRoot, string relativeIndexPath,
            string artifactRootRelativePath, int schemaVersion, string provenanceClass,
            bool requireEveryArtifactSelected, AdoptedFixtureUse[] requiredUses)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
                throw new ArgumentException("Repository root is required.", nameof(repositoryRoot));

            string indexPath = Path.GetFullPath(Path.Combine(repositoryRoot,
                relativeIndexPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("The adopted fixture index was not found.", indexPath);
            string fixtureRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(indexPath),
                artifactRootRelativePath.Replace('/', Path.DirectorySeparatorChar)));

            FixtureIndexDto index = JsonUtility.FromJson<FixtureIndexDto>(File.ReadAllText(indexPath));
            if (index == null || index.SchemaVersion != schemaVersion || string.IsNullOrEmpty(index.DatasetId))
                throw new InvalidDataException("The adopted fixture index header is invalid.");

            var artifacts = new Dictionary<string, AdoptedFixture>(StringComparer.Ordinal);
            foreach (GeometryArtifactDto artifact in index.GeometryArtifacts ?? Array.Empty<GeometryArtifactDto>())
            {
                if (artifact == null || string.IsNullOrEmpty(artifact.GeometryArtifactId) ||
                    string.IsNullOrEmpty(artifact.GeometryRelativePath))
                    throw new InvalidDataException("A Geometry artifact entry is incomplete.");
                if (artifact.ProvenanceClass != provenanceClass)
                    throw new InvalidDataException("Unexpected Geometry provenance: " + artifact.GeometryArtifactId);
                if (requireEveryArtifactSelected && artifact.SelectionClass != "Selected")
                    throw new InvalidDataException("The public adopted set must contain only selected Synthetic Geometry.");
                if (artifacts.ContainsKey(artifact.GeometryArtifactId))
                    throw new InvalidDataException("Duplicate Geometry artifact id: " + artifact.GeometryArtifactId);

                string geometryPath = ResolveContainedPath(fixtureRoot, artifact.GeometryRelativePath);
                var fixture = new AdoptedFixture(
                    artifact.GeometryArtifactId, artifact.SourceFixtureId,
                    artifact.GeometryRelativePath, geometryPath, artifact.GeometryByteLength,
                    artifact.GeometryContentSha256, artifact.GeometryKind);
                if (requireEveryArtifactSelected)
                    _ = fixture.Geometry;
                artifacts.Add(artifact.GeometryArtifactId, fixture);
            }

            var lists = new Dictionary<AdoptedFixtureUse, List<AdoptedFixture>>
            {
                { AdoptedFixtureUse.RenderCut, new List<AdoptedFixture>() },
                { AdoptedFixtureUse.PhysicsCook, new List<AdoptedFixture>() },
                { AdoptedFixtureUse.Correctness, new List<AdoptedFixture>() },
            };
            var bindingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (FixtureBindingDto binding in index.FixtureBindings ?? Array.Empty<FixtureBindingDto>())
            {
                if (binding == null || string.IsNullOrEmpty(binding.FixtureBindingId) ||
                    !bindingIds.Add(binding.FixtureBindingId))
                    throw new InvalidDataException("A Fixture binding id is missing or duplicated.");
                if (binding.ArtifactKind != "Geometry")
                    continue;
                if (!TryMapUse(binding.FixtureRole, out AdoptedFixtureUse use))
                    continue;
                if (!artifacts.TryGetValue(binding.ArtifactId, out AdoptedFixture fixture))
                    throw new InvalidDataException("Fixture binding references missing Geometry: " + binding.ArtifactId);
                GeometryArtifactDto artifact = Array.Find(index.GeometryArtifacts,
                    value => value.GeometryArtifactId == binding.ArtifactId);
                if (artifact == null || artifact.SelectionClass != "Selected")
                    throw new InvalidDataException("Fixture binding references unselected Geometry: " + binding.ArtifactId);
                lists[use].Add(fixture);
            }

            var frozen = new Dictionary<AdoptedFixtureUse, AdoptedFixture[]>();
            foreach (KeyValuePair<AdoptedFixtureUse, List<AdoptedFixture>> pair in lists)
            {
                if (Array.IndexOf(requiredUses, pair.Key) >= 0 && pair.Value.Count == 0)
                    throw new InvalidDataException("The adopted set has no Geometry for " + pair.Key + ".");
                frozen.Add(pair.Key, pair.Value.ToArray());
            }
            return new AdoptedFixtureCatalog(index.DatasetId, frozen);
        }

        static bool TryMapUse(string role, out AdoptedFixtureUse use)
        {
            switch (role)
            {
                case "RenderCutInput": use = AdoptedFixtureUse.RenderCut; return true;
                case "PhysicsCookInput": use = AdoptedFixtureUse.PhysicsCook; return true;
                case "PhysicsCorrectnessInput": use = AdoptedFixtureUse.Correctness; return true;
                default: use = default; return false;
            }
        }

        static string ResolveContainedPath(string root, string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Geometry path must be relative.");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(fullRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Geometry path escapes the adopted fixture root.");
            if (!File.Exists(candidate))
                throw new FileNotFoundException("Adopted Geometry was not found.", candidate);
            return candidate;
        }

        [Serializable]
        sealed class FixtureIndexDto
        {
            public int SchemaVersion;
            public string DatasetId;
            public GeometryArtifactDto[] GeometryArtifacts;
            public FixtureBindingDto[] FixtureBindings;
        }

        [Serializable]
        sealed class GeometryArtifactDto
        {
            public string GeometryArtifactId;
            public string SourceFixtureId;
            public string ProvenanceClass;
            public string GeometryKind;
            public string GeometryRelativePath;
            public long GeometryByteLength;
            public string GeometryContentSha256;
            public string SelectionClass;
        }

        [Serializable]
        sealed class FixtureBindingDto
        {
            public string FixtureBindingId;
            public string ArtifactKind;
            public string ArtifactId;
            public string FixtureRole;
        }
    }
}
