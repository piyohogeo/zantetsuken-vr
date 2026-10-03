using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The cut input gate's two dictionaries with a hash of their own (TL, 2026-10-03): keys that file under one hash are
    /// still told apart as different edges and fans (the same equality); the city walk's real inputs, and each broken in
    /// the two ways a closed surface can be (a face taken out, a face turned over), are judged as the gate judged them
    /// before -- the same verdict, rejection and element (VpCutInputGateBefore).
    /// </summary>
    public sealed class VpCutInputGateHashTests
    {
        private static readonly VpCutInputGate.TopologyPairComparer Comparer = VpCutInputGate.TopologyPairComparer.Instance;

        private static long Edge(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private static long Directed(int a, int b) => ((long)a << 32) | (uint)b;

        // Two keys of four different topology ids below n that the new hash files together, found by a seeded search over
        // random pairs (a 32-bit hash: some 300k keys hold a few collisions).
        private static (int a, int b, int c, int d) Colliding(int n, bool directed)
        {
            var random = new System.Random(directed ? 20261003 : 1003);
            var seen = new Dictionary<int, (int, int)>();
            for (int i = 0; i < 600000; i++)
            {
                int a = random.Next(n), b = random.Next(n);
                if (a == b) continue;
                long key = directed ? Directed(a, b) : Edge(a, b);
                int hash = Comparer.GetHashCode(key);
                if (seen.TryGetValue(hash, out (int x, int y) other) && (directed ? Directed(other.x, other.y) : Edge(other.x, other.y)) != key
                    && other.x != a && other.x != b && other.y != a && other.y != b) return (other.x, other.y, a, b);
                seen[hash] = (a, b);
            }

            return (-1, -1, -1, -1);
        }

        // Closed tetrahedra, one per id quadruple, wound alike; one render vertex per topology id.
        private static (VpRenderVertex[] vertices, uint[] indices, int[] topology, int count) Tetrahedra(int count, params (int a, int b, int c, int d)[] solids)
        {
            var vertices = new VpRenderVertex[count];
            var topology = new int[count];
            for (int i = 0; i < count; i++)
            {
                topology[i] = i;
                var p = new Vector3(i % 97, (i / 97) % 89, i / (97 * 89)) * 0.1f;
                vertices[i] = new VpRenderVertex { position = p, normalX = 0, normalY = 0, u = 0, v = 0 };
            }

            var indices = new List<uint>();
            foreach ((int a, int b, int c, int d) in solids)
            {
                foreach ((int x, int y, int z) in new[] { (a, b, c), (a, c, d), (a, d, b), (b, d, c) })
                {
                    indices.Add((uint)x); indices.Add((uint)y); indices.Add((uint)z);
                }
            }

            return (vertices, indices.ToArray(), topology, count);
        }

        private static void Same(string what, VpRenderVertex[] vertices, uint[] indices, int[] topology, int count)
        {
            var submeshes = new[] { new VpGeometrySubmesh(0, indices.Length, 0) };
            VpCutInputVerdict now = VpCutInputGate.Check(vertices, indices, topology, count, submeshes);
            VpCutInputVerdict before = VpCutInputGateBefore.Check(vertices, indices, topology, count, submeshes);
            TestContext.Out.WriteLine(what + ": now " + now + ", before " + before);
            Assert.That(now.rejection, Is.EqualTo(before.rejection), what);
            Assert.That(now.element, Is.EqualTo(before.element), what + ": the element the verdict names");
        }

        [Test]
        public void KeysFiledUnderOneHash_AreStillDifferentEdgesAndFans()
        {
            const int n = 1 << 20;
            (int a, int b, int c, int d) edge = Colliding(n, false);
            (int a, int b, int c, int d) fan = Colliding(n, true);
            Assert.That(edge.a, Is.GreaterThanOrEqualTo(0), "an edge key collision below " + n + " ids");
            Assert.That(fan.a, Is.GreaterThanOrEqualTo(0), "a directed (fan) key collision below " + n + " ids");
            long e1 = Edge(edge.a, edge.b), e2 = Edge(edge.c, edge.d), f1 = Directed(fan.a, fan.b), f2 = Directed(fan.c, fan.d);
            Assert.That(Comparer.GetHashCode(e1), Is.EqualTo(Comparer.GetHashCode(e2)));
            Assert.That(Comparer.Equals(e1, e2), Is.False, "the same hash, different keys");
            Assert.That(Comparer.GetHashCode(f1), Is.EqualTo(Comparer.GetHashCode(f2)));
            Assert.That(Comparer.Equals(f1, f2), Is.False);
            var d = new Dictionary<long, int>(Comparer) { [e1] = 1, [e2] = 2 };
            Assert.That(d.Count, Is.EqualTo(2));
            Assert.That(d[e1], Is.EqualTo(1));
            Assert.That(d[e2], Is.EqualTo(2));
            TestContext.Out.WriteLine("edge keys (" + edge.a + "," + edge.b + ") and (" + edge.c + "," + edge.d + "), fan keys " + fan.a + "->" + fan.b + " and " + fan.c + "->" + fan.d + ": one hash each");

            // In the gate: each colliding key an edge of its own closed tetrahedron (the fourth corners free ids above n).
            int spare = n;
            var solids = new List<(int, int, int, int)>
            {
                (edge.a, edge.b, spare++, spare++), (edge.c, edge.d, spare++, spare++),
                (fan.a, fan.b, spare++, spare++), (fan.c, fan.d, spare++, spare++),
            };
            // the four solids share no id (a shared vertex between two of them would be two fans: refused, as it should be, below)
            var used = solids.SelectMany(t => new[] { t.Item1, t.Item2, t.Item3, t.Item4 }).ToList();
            if (used.Distinct().Count() != used.Count) solids = new List<(int, int, int, int)> { (edge.a, edge.b, spare++, spare++), (edge.c, edge.d, spare++, spare++) };
            int count = spare;
            var closed = Tetrahedra(count, solids.ToArray());
            var accepted = VpCutInputGate.Check(closed.vertices, closed.indices, closed.topology, closed.count, new[] { new VpGeometrySubmesh(0, closed.indices.Length, 0) });
            Assert.That(accepted.Accepted, Is.True, "closed tetrahedra on the colliding keys: " + accepted);
            Same("closed tetrahedra on the colliding keys", closed.vertices, closed.indices, closed.topology, closed.count);
            // One face of the second solid (the one whose edge collides with the first's) taken out: its edges each one face.
            uint[] open = closed.indices.Take(12).Concat(closed.indices.Skip(15)).ToArray();
            Same("the colliding edge's second solid opened", closed.vertices, open, closed.topology, closed.count);
            Assert.That(VpCutInputGate.Check(closed.vertices, open, closed.topology, closed.count, new[] { new VpGeometrySubmesh(0, open.Length, 0) }).rejection, Is.EqualTo(VpCutInputRejection.EdgeFaceCount));
            // One face of it turned over: an edge traversed twice the same way.
            uint[] turned = (uint[])closed.indices.Clone();
            (turned[13], turned[14]) = (turned[14], turned[13]);
            Same("the colliding edge's second solid with a face turned over", closed.vertices, turned, closed.topology, closed.count);
            // Two solids sharing a vertex: two fans there.
            var shared = Tetrahedra(count, (edge.a, edge.b, n, n + 1), (edge.a, edge.c, n + 2, n + 3));
            Same("two solids sharing a colliding key's vertex", shared.vertices, shared.indices, shared.topology, shared.count);
        }

        [Test]
        public void TheCityWalksInputs_AndEachBrokenTwoWays_AreJudgedAsBefore()
        {
            string[] names = { "mall_001", "government_bilding_003", "barbershop_001", "skyscraper_003", "supermarket_003", "car_018", "tree_017", "bench_001" };
            int compared = 0;
            foreach (string name in names)
            {
                string derived = "Assets/Licensed/WalkCity/InputsDerived/" + name + ".json", original = "Assets/Licensed/WalkCity/Inputs/" + name + ".json";
                string path = File.Exists(derived) ? derived : original;
                if (!File.Exists(path)) continue;
                var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(path));
                VpRenderVertex[] vertices = data.Vertices();
                Same(name, vertices, data.indices, data.topology, data.topologyCount);
                int middle = (data.indices.Length / 3 / 2) * 3;
                uint[] open = data.indices.Take(middle).Concat(data.indices.Skip(middle + 3)).ToArray();
                Same(name + " with a face taken out", vertices, open, data.topology, data.topologyCount);
                uint[] turned = (uint[])data.indices.Clone();
                (turned[middle + 1], turned[middle + 2]) = (turned[middle + 2], turned[middle + 1]);
                Same(name + " with a face turned over", vertices, turned, data.topology, data.topologyCount);
                compared++;
            }

            if (compared == 0) Assert.Ignore("the licensed city walk inputs are not in this checkout");
        }
    }
}
