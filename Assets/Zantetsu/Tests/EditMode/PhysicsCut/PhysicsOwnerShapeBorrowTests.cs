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
    /// <summary>
    /// A side made of a source's convexes borrows them: its convexes address the block they live in, nothing is
    /// copied, and it holds exactly what it reads -- the block's owner for the block, its own mesh source for the
    /// meshes -- for exactly as long as it reads it. The source's mesh holds go back when the source's owner is done
    /// with it, whether or not a side still addresses its block; the block stays until the last side addressing it,
    /// and the last piece of work reading that side, has gone; and a chain of re-cuts holds no ancestor it does not
    /// read. Only the copying path a measurement can still take makes a block of its own.
    /// </summary>
    public unsafe class PhysicsOwnerShapeBorrowTests
    {
        private VpCpuGeometryStorage _storage;
        private SandboxCompoundBody _body;
        private PhysicsShapeSource _meshSource;
        private PhysicsOwnerShape _source;

        [SetUp]
        public void SetUp()
        {
            _storage = new VpCpuGeometryStorage(4096, 8192, 16, 32, 32, Allocator.Persistent);
            _body = SandboxCompoundBody.TryBuild(_storage, 2, 1, new float3(0.25f, 0.25f, 0.25f), 0);
            Assert.That(_body, Is.Not.Null);

            // An authored shape of this test's own, on a mesh source of its own, so that its holds can be counted:
            // the sandbox body's bank and meshes, copied once into a block this shape owns.
            _meshSource = PhysicsShapeSource.External();
            var ranges = new List<ConvexBrepRange> { _body.Shape.Convex(0), _body.Shape.Convex(1) };
            var meshes = new List<Mesh> { _body.Shape.MeshOf(0), _body.Shape.MeshOf(1) };
            _source = PhysicsOwnerShape.Authored(_body.Shape.BankOf(0), ranges, meshes, _meshSource, float4x4.identity);
            Assert.That(_meshSource.Users, Is.EqualTo(1), "the authored shape holds its mesh source once");
        }

        [TearDown]
        public void TearDown()
        {
            _source.Dispose();
            _body.Dispose();
            _storage.Dispose();
        }

        [Test]
        public void AProvisionalSide_AddressesTheSourcesOwnBlock_AndCopiesNothing()
        {
            PhysicsOwnerShape side = PhysicsOwnerShape.ProvisionalSide(_source, new[] { 0, 1 });
            try
            {
                // Nothing was copied and no block was made: each convex's bank pointer and range base are the
                // source's own, which a copy into a block of this side's own could not be.
                for (int c = 0; c < 2; c++)
                {
                    Assert.That((IntPtr)side.BankOf(c).vertices, Is.EqualTo((IntPtr)_source.BankOf(c).vertices), "convex " + c + " reads the source's vertices");
                    Assert.That(side.Convex(c).vertexBase, Is.EqualTo(_source.Convex(c).vertexBase), "at the source's own range");
                    Assert.That(side.MeshOf(c), Is.SameAs(_source.MeshOf(c)), "and names the source's mesh");
                    Assert.That(side.BlockOwnerOf(c), Is.SameAs(_source), "and knows whose block it is");
                }

                Assert.That(_source.BankUsers, Is.EqualTo(1), "the source's block is held by the side");
                Assert.That(_meshSource.Users, Is.EqualTo(2), "and the side holds the meshes it uses, itself");
            }
            finally
            {
                side.Dispose();
            }

            Assert.That(_source.BankUsers, Is.EqualTo(0), "let go when the side is freed");
            Assert.That(_meshSource.Users, Is.EqualTo(1), "and so is the side's mesh hold, once");
        }

        [Test]
        public void ARetiredSource_KeepsItsBlockForTheSidesAddressingIt_ButGivesItsMeshHoldsBackAtOnce()
        {
            PhysicsOwnerShape positive = PhysicsOwnerShape.ProvisionalSide(_source, new[] { 0 });
            PhysicsOwnerShape negative = PhysicsOwnerShape.ProvisionalSide(_source, new[] { 0, 1 });
            Assert.That(_meshSource.Users, Is.EqualTo(3));

            _source.Dispose();
            Assert.That(_source.IsFreed, Is.False, "retired, but two sides still address its block");
            Assert.That(_source.MeshHoldsReleased, Is.True, "its own mesh holds went back with it");
            Assert.That(_meshSource.Users, Is.EqualTo(2), "only the sides' holds remain");
            Assert.That(_source.BankOf(0).vertices != null, Is.True, "the block is still there to read");
            Assert.That(positive.BankOf(0).vertices[positive.Convex(0).vertexBase].x, Is.EqualTo(_body.Shape.BankOf(0).vertices[_body.Shape.Convex(0).vertexBase].x), "and reads what it read");

            positive.Dispose();
            Assert.That(_source.IsFreed, Is.False, "one side still does");
            Assert.That(_meshSource.Users, Is.EqualTo(1));

            negative.Dispose();
            Assert.That(_source.IsFreed, Is.True, "the last side has gone: the block goes");
            Assert.That(_meshSource.Users, Is.EqualTo(0), "every hold went back, each once");
            Assert.That(positive.IsFreed, Is.True);
            Assert.That(negative.IsFreed, Is.True);
        }

        [Test]
        public void ASidesOwnEnd_DoesNotReleaseWhatItsRunningWorkReads()
        {
            PhysicsOwnerShape side = PhysicsOwnerShape.ProvisionalSide(_source, new[] { 1 });
            side.AcquireForWork();

            _source.Dispose();
            side.Dispose();
            Assert.That(side.IsFreed, Is.False, "the side waits for its work");
            Assert.That(side.MeshHoldsReleased, Is.False, "and keeps its mesh hold for it");
            Assert.That(_source.IsFreed, Is.False, "and so the source's block waits for the side");
            Assert.That(_meshSource.Users, Is.EqualTo(1), "the source's own hold went back; the side's stays");

            side.ReleaseFromWork();
            Assert.That(side.IsFreed, Is.True);
            Assert.That(_source.IsFreed, Is.True, "collected, both go");
            Assert.That(_meshSource.Users, Is.EqualTo(0), "and each hold went back once");
        }

        [Test]
        public void AChainOfSides_HoldsTheBlockOwner_AndNoIntermediate()
        {
            PhysicsOwnerShape first = PhysicsOwnerShape.ProvisionalSide(_source, new[] { 0, 1 });
            PhysicsOwnerShape second = PhysicsOwnerShape.ProvisionalSide(first, new[] { 1 });
            Assert.That(second.BlockOwnerOf(0), Is.SameAs(_source), "the grandchild knows the block is the source's");
            Assert.That(_source.BankUsers, Is.EqualTo(2), "the source's block is held by both");
            Assert.That(first.BankUsers, Is.EqualTo(0), "the first side, which only borrowed, is held by no one");

            first.Dispose();
            Assert.That(first.IsFreed, Is.True, "and goes as soon as its owner is done, whatever its descendant does");
            Assert.That(_meshSource.Users, Is.EqualTo(2), "leaving the source's and the grandchild's mesh holds");

            second.Dispose();
            Assert.That(_source.BankUsers, Is.EqualTo(0));
            Assert.That(_meshSource.Users, Is.EqualTo(1));
        }

    }
}
