using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe class ProvisionalShapeViewTests
    {
        private VpCpuGeometryStorage _storage;
        private SandboxCompoundBody _body;
        private PhysicsShapeSource _meshes;
        private PhysicsOwnerShape _source;
        private readonly List<PhysicsOwnerShape> _sides = new List<PhysicsOwnerShape>();

        [SetUp]
        public void SetUp()
        {
            _storage = new VpCpuGeometryStorage(4096, 8192, 16, 32, 32, Allocator.Persistent);
            _body = SandboxCompoundBody.TryBuild(_storage, 2, 1, new float3(0.25f), 0);
            Assert.That(_body, Is.Not.Null);
            _meshes = PhysicsShapeSource.External();
            _source = PhysicsOwnerShape.Authored(
                _body.Shape.BankOf(0), new[] { _body.Shape.Convex(0), _body.Shape.Convex(1) },
                new[] { _body.Shape.MeshOf(0), _body.Shape.MeshOf(1) }, _meshes, float4x4.identity);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _sides.Count - 1; i >= 0; i--)
            {
                _sides[i].Dispose();
            }

            _sides.Clear();
            _source?.Dispose();
            _body?.Dispose();
            _storage?.Dispose();
        }

        private PhysicsOwnerShape Side(PhysicsOwnerShape source, int[] indices)
        {
            PhysicsOwnerShape shape = PhysicsOwnerShape.ProvisionalSide(source, indices);
            _sides.Add(shape);
            return shape;
        }

        [Test]
        public void ReorderedRepeatedConvexes_KeepTheirInputSnapshotAndMeshEnumeration()
        {
            int[] indices = { 1, 0, 1 };
            PhysicsOwnerShape side = Side(_source, indices);
            indices[0] = 0;
            Assert.That(side.ConvexCount, Is.EqualTo(3));
            Assert.That(side.InputConvexOf(0), Is.EqualTo(1));
            Assert.That(side.InputConvexOf(1), Is.EqualTo(0));
            Assert.That(side.InputConvexOf(2), Is.EqualTo(1));
            Assert.That(side.InputConvexOf(-1), Is.EqualTo(-1));
            Assert.That(side.InputConvexOf(3), Is.EqualTo(-1));
            Assert.That(side.Meshes.Count, Is.EqualTo(3));
            CollectionAssert.AreEqual(
                new[] { _source.MeshOf(1), _source.MeshOf(0), _source.MeshOf(1) }, side.Meshes);
            Assert.That(_source.BankUsers, Is.EqualTo(1), "duplicates do not duplicate bank holds");
            Assert.That(_meshes.Users, Is.EqualTo(2), "nor mesh-source holds");
            for (int i = 0; i < side.ConvexCount; i++)
            {
                int input = side.InputConvexOf(i);
                Assert.That(side.Convex(i), Is.EqualTo(_source.Convex(input)));
                Assert.That((IntPtr)side.BankOf(i).vertices, Is.EqualTo((IntPtr)_source.BankOf(input).vertices));
                side.ConvexBounds(i, out float3 lo, out float3 hi);
                _source.ConvexBounds(input, out float3 expectedLo, out float3 expectedHi);
                Assert.That(lo, Is.EqualTo(expectedLo));
                Assert.That(hi, Is.EqualTo(expectedHi));
            }
        }

        [Test]
        public void NestedSelection_OutlivesBothOwnersWithoutHoldingTheIntermediateBank()
        {
            PhysicsOwnerShape first = Side(_source, new[] { 1, 0, 1 });
            PhysicsOwnerShape second = Side(first, new[] { 2, 1 });
            Mesh expectedFirst = _source.MeshOf(1);
            Mesh expectedSecond = _source.MeshOf(0);
            ConvexBrepRange expectedRange = _source.Convex(1);
            IntPtr expectedBank = (IntPtr)_source.BankOf(1).vertices;
            Assert.That(first.BankUsers, Is.Zero);
            Assert.That(_source.BankUsers, Is.EqualTo(2));
            first.Dispose();
            _source.Dispose();
            Assert.That(first.IsFreed, Is.True);
            Assert.That(_source.MeshHoldsReleased, Is.True);
            Assert.That(_source.IsFreed, Is.False);
            Assert.That(_source.WorkUsers, Is.Zero, "a view does not pretend to be submitted work");
            Assert.That(_meshes.Users, Is.EqualTo(1));
            Assert.That(second.InputConvexOf(0), Is.EqualTo(2), "indices remain relative to the direct input");
            Assert.That(second.InputConvexOf(1), Is.EqualTo(1));
            Assert.That(second.Convex(0), Is.EqualTo(expectedRange));
            Assert.That((IntPtr)second.BankOf(0).vertices, Is.EqualTo(expectedBank));
            Assert.That(second.BlockOwnerOf(0), Is.SameAs(_source));
            CollectionAssert.AreEqual(new[] { expectedFirst, expectedSecond }, second.Meshes);
            Assert.That(second.TryLocalBounds(out _, out _), Is.True);
            second.Dispose();
            Assert.That(_source.IsFreed, Is.True);
            Assert.That(_meshes.Users, Is.Zero);
        }

        [Test]
        public void AViewRetiredWithRunningWork_KeepsItsOwnHoldsUntilCollection()
        {
            PhysicsOwnerShape side = Side(_source, new[] { 1, 0 });
            side.AcquireForWork();
            try
            {
                _source.Dispose();
                side.Dispose();
                Assert.That(side.IsFreed, Is.False);
                Assert.That(side.MeshHoldsReleased, Is.False);
                Assert.That(_source.BankUsers, Is.EqualTo(1));
                Assert.That(_meshes.Users, Is.EqualTo(1));
                Assert.That(side.BankOf(0).vertices[side.Convex(0).vertexBase].x,
                    Is.EqualTo(_body.Shape.BankOf(1).vertices[_body.Shape.Convex(1).vertexBase].x));
            }
            finally
            {
                side.ReleaseFromWork();
            }

            Assert.That(side.IsFreed, Is.True);
            Assert.That(_source.IsFreed, Is.True);
            Assert.That(_meshes.Users, Is.Zero);
        }

        [Test]
        public void BorrowedNativeVertices_RemainTheSameMutableStorage()
        {
            PhysicsOwnerShape side = Side(_source, new[] { 1 });
            float3* vertex = _source.BankOf(1).vertices + _source.Convex(1).vertexBase;
            float3 before = *vertex;
            try
            {
                vertex->x = before.x + 0.125f;
                Assert.That(side.BankOf(0).vertices[side.Convex(0).vertexBase].x, Is.EqualTo(before.x + 0.125f));
            }
            finally
            {
                *vertex = before;
            }
        }

        [Test]
        public void FinalBorrowing_FromAReorderedViewUsesTheSelectedConvex()
        {
            PhysicsOwnerShape view = Side(_source, new[] { 1, 0 });
            var result = new ConvexCutOwnerResult { status = ConvexCutOwnerStatus.Ok };
            var capacity = new ConvexCutOwnerCapacity
            {
                vertices = 1, faceOffsets = 1, faceIndices = 1, edges = 1, scratchBytes = 1,
            };
            using (var products = new PhysicsCutProducts(
                new PhysicsCutArena(in capacity, 1), new ConvexCutOutcome[1], in result,
                float4x4.identity, PhysicsCutCook.DefaultCooking, 1, 0))
            {
                products.Add(true, new PhysicsCutPart(0, true, view.Convex(0), null, default));
                PhysicsOwnerShape final = PhysicsOwnerShape.OfSide(view, products, PhysicsShapeSource.For(products), true);
                _sides.Add(final);
                view.Dispose();
                Assert.That(final.Convex(0), Is.EqualTo(_source.Convex(1)));
                Assert.That(final.MeshOf(0), Is.SameAs(_source.MeshOf(1)));
                Assert.That(final.BlockOwnerOf(0), Is.SameAs(_source));
                Assert.That((IntPtr)final.BankOf(0).vertices, Is.EqualTo((IntPtr)_source.BankOf(1).vertices));
                final.Dispose();
            }
        }

        [Test]
        public void GeneratedSource_CanFreeItsRecordWhileTheViewKeepsItsProducts()
        {
            var result = new ConvexCutOwnerResult { status = ConvexCutOwnerStatus.Ok };
            var capacity = new ConvexCutOwnerCapacity
            {
                vertices = 1, faceOffsets = 1, faceIndices = 1, edges = 1, scratchBytes = 1,
            };
            var products = new PhysicsCutProducts(
                new PhysicsCutArena(in capacity, 1), new ConvexCutOutcome[1], in result,
                float4x4.identity, PhysicsCutCook.DefaultCooking, 1, 0);
            PhysicsOwnerShape generated = null;
            PhysicsOwnerShape view = null;
            Mesh mesh = new Mesh();
            try
            {
                float3 point = new float3(0.1f, 0.2f, 0.3f);
                mesh.vertices = new[] { (Vector3)point };
                mesh.RecalculateBounds();
                products.Bank.vertices[0] = point;
                var range = new ConvexBrepRange { vertexCount = 1 };
                products.Add(true, new PhysicsCutPart(0, false, range, mesh, new float3x2(point, point)));
                var supplier = PhysicsShapeSource.For(products);
                generated = PhysicsOwnerShape.OfSide(_source, products, supplier, true);
                supplier.TakeOwnership();
                view = Side(generated, new[] { 0 });
                IntPtr bank = (IntPtr)generated.BankOf(0).vertices;
                generated.Dispose();
                Assert.That(generated.IsFreed, Is.True, "products banks do not retain the intermediate shape");
                Assert.That(generated.BankUsers, Is.Zero);
                Assert.That(supplier.Users, Is.EqualTo(1), "the view holds the products directly");
                Assert.That(supplier.IsReleased, Is.False);
                Assert.That(view.BlockOwnerOf(0), Is.Null);
                Assert.That((IntPtr)view.BankOf(0).vertices, Is.EqualTo(bank));
                Assert.That(view.BankOf(0).vertices[0], Is.EqualTo(point));
                Assert.That(view.MeshOf(0), Is.SameAs(mesh));
                view.Dispose();
                Assert.That(supplier.IsReleased, Is.True);
            }
            finally
            {
                view?.Dispose();
                generated?.Dispose();
                products.Dispose();
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }
    }
}
