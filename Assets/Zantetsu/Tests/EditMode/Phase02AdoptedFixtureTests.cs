using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
using Zantetsu.MeshCut.Verification;

namespace Zantetsu.Core.Tests
{
    public sealed class Phase02AdoptedFixtureTests
    {
        AdoptedFixtureCatalog m_catalog;

        [OneTimeSetUp]
        public void LoadCatalog()
        {
            string repositoryRoot = Directory.GetParent(Application.dataPath).FullName;
            m_catalog = AdoptedFixtureCatalog.Load(repositoryRoot);
        }

        [Test]
        public void AdoptedSet_ResolvesTheThreeCurrentGeometryUses()
        {
            Assert.That(m_catalog.DatasetId, Is.EqualTo("synthetic-phase02-v1"));
            Assert.That(m_catalog.Resolve(AdoptedFixtureUse.RenderCut).Count, Is.EqualTo(8));
            Assert.That(m_catalog.Resolve(AdoptedFixtureUse.PhysicsCook).Count, Is.EqualTo(4));
            Assert.That(m_catalog.Resolve(AdoptedFixtureUse.Correctness).Count, Is.EqualTo(12));
            Assert.That(m_catalog.Resolve(AdoptedFixtureUse.RenderCut).All(
                fixture => fixture.Geometry.Kind == ZcgGeometryKind.TriangleMesh), Is.True);
            Assert.That(m_catalog.Resolve(AdoptedFixtureUse.PhysicsCook).All(
                fixture => fixture.Geometry.Kind == ZcgGeometryKind.ConvexSet), Is.True);
        }

        [Test]
        public void RenderCutInput_RunsThroughTheCurrentMeshCutHarness()
        {
            AdoptedFixture fixture = m_catalog.Resolve(AdoptedFixtureUse.RenderCut)
                .Single(value => value.SourceFixtureId == "center-cut");
            SyntheticMesh mesh = ToSyntheticMesh(fixture.Geometry);
            using (var harness = new MeshCutHarness(8 << 20))
            {
                CutGeometry geometry = harness.Place(mesh, fixture.ArtifactId, 17, 31);
                var bounds = geometry.Bounds();
                float3 center = (float3)(0.5 * (bounds.min + bounds.max));
                CutRun run = harness.Cut(geometry,
                    SyntheticGeometry.Plane(new float3(1, 0, 0), center));
                Assert.That(run.Result.status, Is.EqualTo(MeshCutStatus.Ok));
                Assert.That(MeshCutVerifier.Verify(run).Passed, Is.True);
            }
        }

        [Test]
        public void PhysicsCookInput_LoadsAndCooksAsConvex()
        {
            AdoptedFixture fixture = m_catalog.Resolve(AdoptedFixtureUse.PhysicsCook)
                .Single(value => value.SourceFixtureId == "convex-cook-v004");
            ZcgHull hull = fixture.Geometry.Hulls.Single();
            var vertices = hull.Positions.Select(position =>
                new Vector3(position.X, position.Y, position.Z)).ToArray();
            var triangles = new List<int>();
            foreach (uint[] face in hull.Faces)
                for (int i = 1; i + 1 < face.Length; i++)
                {
                    triangles.Add(checked((int)face[0]));
                    triangles.Add(checked((int)face[i]));
                    triangles.Add(checked((int)face[i + 1]));
                }

            var mesh = new Mesh { name = fixture.ArtifactId, hideFlags = HideFlags.HideAndDontSave };
            var owner = new GameObject("Phase02AdoptedCookProbe") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                mesh.vertices = vertices;
                mesh.triangles = triangles.ToArray();
                mesh.RecalculateBounds();
                const MeshColliderCookingOptions options =
                    MeshColliderCookingOptions.CookForFasterSimulation |
                    MeshColliderCookingOptions.EnableMeshCleaning |
                    MeshColliderCookingOptions.WeldColocatedVertices |
                    MeshColliderCookingOptions.UseFastMidphase;
                Assert.DoesNotThrow(() => Physics.BakeMesh(mesh.GetEntityId(), true, options));
                MeshCollider collider = owner.AddComponent<MeshCollider>();
                collider.convex = true;
                collider.cookingOptions = options;
                collider.sharedMesh = mesh;
                Assert.That(collider.sharedMesh, Is.SameAs(mesh));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void LicensedAdoptedSet_WhenPresent_ResolvesAndRunsCurrentUses()
        {
            string repositoryRoot = Directory.GetParent(Application.dataPath).FullName;
            string repositoriesRoot = Directory.GetParent(repositoryRoot).FullName;
            string privateRepositoryRoot = Path.Combine(repositoriesRoot, "zantetsuken-assets-private");
            string indexPath = Path.Combine(privateRepositoryRoot,
                "Working", "Phase0.2", "Adopted", "dataset-index.json");
            if (!File.Exists(indexPath))
                Assert.Ignore("The optional sibling private repository is not available.");

            AdoptedFixtureCatalog licensed = AdoptedFixtureCatalog.LoadLicensed(privateRepositoryRoot);
            Assert.That(licensed.Resolve(AdoptedFixtureUse.RenderCut), Is.Not.Empty);
            Assert.That(licensed.Resolve(AdoptedFixtureUse.PhysicsCook), Is.Not.Empty);
            Assert.That(licensed.Resolve(AdoptedFixtureUse.Correctness), Is.Empty);

            AdoptedFixture renderFixture = licensed.Resolve(AdoptedFixtureUse.RenderCut)
                .OrderBy(value => value.GeometryByteLength).First();
            Assert.That(renderFixture.Geometry.Kind, Is.EqualTo(ZcgGeometryKind.TriangleMesh));
            SyntheticMesh mesh = ToSyntheticMesh(renderFixture.Geometry);
            using (var harness = new MeshCutHarness(8 << 20))
            {
                CutGeometry geometry = harness.Place(mesh, renderFixture.ArtifactId, 19, 37);
                var bounds = geometry.Bounds();
                float3 center = (float3)(0.5 * (bounds.min + bounds.max));
                CutRun run = harness.Cut(geometry,
                    SyntheticGeometry.Plane(new float3(1, 0, 0), center));
                Assert.That(run.Result.status, Is.Not.EqualTo(MeshCutStatus.InvalidInput));
            }

            AdoptedFixture cookFixture = licensed.Resolve(AdoptedFixtureUse.PhysicsCook)
                .OrderBy(value => value.GeometryByteLength).First();
            Assert.That(cookFixture.Geometry.Kind, Is.EqualTo(ZcgGeometryKind.ConvexSet));
            ZcgHull hull = cookFixture.Geometry.Hulls.First();
            var vertices = hull.Positions.Select(position =>
                new Vector3(position.X, position.Y, position.Z)).ToArray();
            var triangles = new List<int>();
            foreach (uint[] face in hull.Faces)
                for (int i = 1; i + 1 < face.Length; i++)
                {
                    triangles.Add(checked((int)face[0]));
                    triangles.Add(checked((int)face[i]));
                    triangles.Add(checked((int)face[i + 1]));
                }

            var cookMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                cookMesh.vertices = vertices;
                cookMesh.triangles = triangles.ToArray();
                Assert.DoesNotThrow(() => Physics.BakeMesh(cookMesh.GetEntityId(), true));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cookMesh);
            }
        }

        static SyntheticMesh ToSyntheticMesh(ZcgDocument document)
        {
            if (document.Kind != ZcgGeometryKind.TriangleMesh)
                throw new ArgumentException("A RenderCutInput must be a TriangleMesh.", nameof(document));
            var mesh = new SyntheticMesh
            {
                Vertices = new RenderVertex[document.Positions.Length],
                Indices = new uint[document.Triangles.Length * 3],
                TopologyOfVertex = new int[document.Positions.Length],
                TopologyVertexCount = document.Positions.Length,
            };
            for (int i = 0; i < document.Positions.Length; i++)
            {
                ZcgPosition position = document.Positions[i];
                mesh.Vertices[i] = new RenderVertex
                {
                    position = new float3(position.X, position.Y, position.Z),
                    normal = new float3(0, 1, 0),
                    uv0 = float2.zero,
                    tangent = new float4(1, 0, 0, 1),
                };
                mesh.TopologyOfVertex[i] = i;
            }
            for (int i = 0; i < document.Triangles.Length; i++)
            {
                ZcgTriangle triangle = document.Triangles[i];
                mesh.Indices[i * 3] = triangle.I0;
                mesh.Indices[i * 3 + 1] = triangle.I1;
                mesh.Indices[i * 3 + 2] = triangle.I2;
            }
            mesh.SubmeshIndexCounts.Add(mesh.Indices.Length);
            return mesh;
        }
    }
}
