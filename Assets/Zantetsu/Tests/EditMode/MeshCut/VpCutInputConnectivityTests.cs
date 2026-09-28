using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The input gate with a connectivity its owner has verified (<see cref="VpCutInputConnectivity"/>): only the edge and
    /// fan checks of indices and a topology identical, element by element, to a verified one are not made again; every
    /// check of an input's own values is.
    /// </summary>
    public class VpCutInputConnectivityTests
    {
        private sealed class Input
        {
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] Topology;
            public int TopologyCount;
            public VpGeometrySubmesh[] Submeshes;

            public VpCutInputVerdict Check(VpCutInputConnectivity verified) =>
                VpCutInputGate.Check(Vertices, Indices, Topology, TopologyCount, Submeshes, verified);

            public Input Copy() => new Input
            {
                Vertices = (VpRenderVertex[])Vertices.Clone(), Indices = (uint[])Indices.Clone(), Topology = (int[])Topology.Clone(),
                TopologyCount = TopologyCount, Submeshes = (VpGeometrySubmesh[])Submeshes.Clone(),
            };
        }

        private static Input Box()
        {
            SyntheticMesh mesh = SyntheticGeometry.Box(2, new float3(1, 1, 1), float3.zero).Finish(new LogicalMeshBuilder.AttributeOptions());
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            return new Input
            {
                Vertices = mesh.Vertices, Indices = mesh.Indices, Topology = mesh.TopologyOfVertex,
                TopologyCount = mesh.TopologyVertexCount, Submeshes = submeshes,
            };
        }

        // The same box with one triangle's winding turned: its three edges are then traversed in the same direction as
        // their other faces, which only the edge check refuses.
        private static Input TurnedTriangle(Input input)
        {
            Input turned = input.Copy();
            (turned.Indices[1], turned.Indices[2]) = (turned.Indices[2], turned.Indices[1]);
            return turned;
        }

        [Test]
        public void AVerifiedConnectivity_IsKeptOnce_AndAnIdenticalInputReusesIt()
        {
            var verified = new VpCutInputConnectivity();
            Input first = Box();
            Assert.That(first.Check(verified).Accepted, Is.True);
            Assert.That(verified.Count, Is.EqualTo(1));
            Assert.That(verified.Kept, Is.EqualTo(1));
            Assert.That(verified.Reused, Is.Zero, "the first input is checked in full");

            Input second = first.Copy();
            Assert.That(second.Check(verified).Accepted, Is.True);
            Assert.That(verified.Reused, Is.EqualTo(1), "a second input with the same arrays reuses it");
            Assert.That(verified.Count, Is.EqualTo(1), "and nothing more is kept");
        }

        [Test]
        public void AnInputsOwnValues_AreCheckedEveryTime_EvenWithTheSameConnectivity()
        {
            var verified = new VpCutInputConnectivity();
            Input first = Box();
            Assert.That(first.Check(verified).Accepted, Is.True);

            Input notFinite = first.Copy();
            notFinite.Vertices[notFinite.Indices[0]].position = new Vector3(float.NaN, 0f, 0f);
            Assert.That(notFinite.Check(verified).rejection, Is.EqualTo(VpCutInputRejection.NonFinite));

            Input moved = first.Copy();
            int topology = moved.Topology[moved.Indices[0]];
            int other = -1;
            for (int v = 0; v < moved.Vertices.Length && other < 0; v++)
            {
                if (v != moved.Indices[0] && moved.Topology[v] == topology) other = v;
            }

            if (other >= 0)
            {
                moved.Vertices[other].position += new Vector3(0.25f, 0f, 0f);
                Assert.That(moved.Check(verified).rejection, Is.EqualTo(VpCutInputRejection.PositionMismatch),
                    "two render vertices of one topology vertex at different positions");
            }

            Input badReference = first.Copy();
            badReference.Indices[0] = (uint)badReference.Vertices.Length;
            Assert.That(badReference.Check(verified).rejection, Is.EqualTo(VpCutInputRejection.InvalidReference));
            Assert.That(verified.Reused, Is.Zero, "none of these reached the connectivity");
        }

        [Test]
        public void ADifferentConnectivity_IsCheckedInFull_AndARefusedOneIsNotKept()
        {
            var verified = new VpCutInputConnectivity();
            Input box = Box();
            Assert.That(box.Check(verified).Accepted, Is.True);

            Input turned = TurnedTriangle(box);
            Assert.That(turned.Check(null).rejection, Is.EqualTo(VpCutInputRejection.EdgeSameDirection), "the shape itself is refused");
            Assert.That(turned.Check(verified).rejection, Is.EqualTo(VpCutInputRejection.EdgeSameDirection), "and so with a kept connectivity");
            Assert.That(verified.Reused, Is.Zero);
            Assert.That(verified.Count, Is.EqualTo(1), "a refused connectivity is not kept");

            Input fewerTopology = box.Copy();
            fewerTopology.TopologyCount += 1;
            Assert.That(fewerTopology.Check(verified).Accepted, Is.True, "an unused extra topology id is allowed");
            Assert.That(verified.Reused, Is.Zero, "but a different count is a different connectivity");
        }

        [Test]
        public void TheKeptArraysAreCopies_SoAChangedCallerArrayIsNotTakenForTheVerifiedOne()
        {
            var verified = new VpCutInputConnectivity();
            Input box = Box();
            Assert.That(box.Check(verified).Accepted, Is.True);

            (box.Indices[1], box.Indices[2]) = (box.Indices[2], box.Indices[1]);
            Assert.That(box.Check(verified).rejection, Is.EqualTo(VpCutInputRejection.EdgeSameDirection),
                "the caller's own array, changed after it was verified, is checked again");
        }

        [Test]
        public void ClearingLetsEverythingGo_AndTheKeptCountIsBounded()
        {
            var verified = new VpCutInputConnectivity();
            for (int i = 0; i < VpCutInputConnectivity.Capacity + 3; i++)
            {
                Input box = Box();
                box.TopologyCount += i;
                Assert.That(box.Check(verified).Accepted, Is.True);
            }

            Assert.That(verified.Count, Is.EqualTo(VpCutInputConnectivity.Capacity), "no more than its capacity is kept");
            verified.Clear();
            Assert.That(verified.Count, Is.Zero);
            Assert.That(Box().Check(verified).Accepted, Is.True);
            Assert.That(verified.Reused, Is.Zero, "after Clear an input is checked in full again");
        }
    }
}
