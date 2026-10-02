using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The small trial of excluding a group cut's copies from its crossed members by one layer pair (TL, 2026-09-30),
    /// against the pairwise exclusion, without the fusion: 252 "crossed" convexes on one body (A), their copies on another
    /// (B) standing where they stand, an uncut neighbour above each crossed one on A's body (U) and below each copy on B's
    /// body (V), overlapping slightly. The relation both must keep: no A-B contact; B-U and A-V contacts stay. Measured: the
    /// setting (calls and seconds), the next Simulates, and the contact pairs the simulation reports (Physics.ContactEvent),
    /// with the enter/exit notices across the change.
    /// </summary>
    public sealed class LayerPairExclusionProbePlayModeTests
    {
        private const int Crossed = 252;
        private const int LayerA = 20, LayerB = 21;   // unnamed user layers, not 30/31 (diagnostics) nor 8 (Player)
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly Dictionary<int, char> _kind = new Dictionary<int, char>();
        private readonly Dictionary<string, int> _pairs = new Dictionary<string, int>();
        private int _enters, _exits;
        private Mesh _cube;
        private bool[] _savedA, _savedB;

        [SetUp]
        public void SetUp()
        {
            _cube = new Mesh();
            float h = 0.51f;
            _cube.vertices = new[]
            {
                new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(-h, h, -h),
                new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h),
            };
            _cube.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
            _savedA = new bool[32];
            _savedB = new bool[32];
            for (int l = 0; l < 32; l++) { _savedA[l] = Physics.GetIgnoreLayerCollision(LayerA, l); _savedB[l] = Physics.GetIgnoreLayerCollision(LayerB, l); }
            Physics.ContactEvent += OnContacts;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.ContactEvent -= OnContacts;
            foreach (GameObject g in _made) if (g != null) Object.DestroyImmediate(g);
            _made.Clear();
            for (int l = 0; l < 32; l++) { Physics.IgnoreLayerCollision(LayerA, l, _savedA[l]); Physics.IgnoreLayerCollision(LayerB, l, _savedB[l]); }
            Object.DestroyImmediate(_cube);
        }

        private void OnContacts(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly headers)
        {
            for (int h = 0; h < headers.Length; h++)
            {
                ContactPairHeader header = headers[h];
                for (int p = 0; p < header.PairCount; p++)
                {
                    ref readonly ContactPair pair = ref header.GetContactPair(p);
                    if (!_kind.TryGetValue(pair.ColliderInstanceID, out char a) || !_kind.TryGetValue(pair.OtherColliderInstanceID, out char b)) continue;
                    string key = a < b ? a + "-" + b : b + "-" + a;
                    if (pair.IsCollisionEnter) _enters++;
                    if (pair.IsCollisionExit) { _exits++; key += " exit"; }
                    _pairs.TryGetValue(key, out int n);
                    _pairs[key] = n + 1;
                }
            }
        }

        private (Rigidbody a, Rigidbody b, List<MeshCollider> crossed, List<MeshCollider> copies) Build()
        {
            Rigidbody Body(string name)
            {
                var go = new GameObject(name);
                _made.Add(go);
                var body = go.AddComponent<Rigidbody>();
                body.useGravity = false;
                body.isKinematic = false;
                return body;
            }

            MeshCollider Convex(Rigidbody body, Vector3 at, char kind)
            {
                var go = new GameObject("Convex mesh frame");
                go.transform.SetParent(body.transform, false);
                go.transform.localPosition = at;
                var c = go.AddComponent<MeshCollider>();
                c.convex = true;
                c.providesContacts = true;
                c.sharedMesh = _cube;
                _kind[c.GetInstanceID()] = kind;
                return c;
            }

            Rigidbody a = Body("A side"), b = Body("B side");
            a.isKinematic = true;   // the crossed side held (a resting group); the copies' side free, so that contacts are reported between them
            var crossed = new List<MeshCollider>();
            var copies = new List<MeshCollider>();
            for (int i = 0; i < Crossed; i++)
            {
                var at = new Vector3(i % 16, 0f, i / 16);
                crossed.Add(Convex(a, at, 'A'));
                copies.Add(Convex(b, at, 'B'));
                Convex(a, at + Vector3.up, 'U');
                Convex(b, at + Vector3.down, 'V');
            }

            return (a, b, crossed, copies);
        }

        private double Simulate(int steps)
        {
            var w = Stopwatch.StartNew();
            for (int s = 0; s < steps; s++) Physics.Simulate(1f / 90f);
            return w.Elapsed.TotalMilliseconds;
        }

        private string Contacts()
        {
            var keys = new List<string>(_pairs.Keys);
            keys.Sort();
            var line = new System.Text.StringBuilder();
            foreach (string k in keys) line.Append(k).Append(' ').Append(_pairs[k]).Append("; ");
            return line.Append("enters ").Append(_enters).Append(", exits ").Append(_exits).ToString();
        }

        [Test]
        public void TheCopiesExcludedByOneLayerPair_KeepTheRelationOfThePairwiseExclusion_AndTheCostIsMeasured([Values(false, true)] bool byLayers)
        {
            var matrix = Stopwatch.StartNew();
            for (int l = 0; l < 32; l++)
            {
                bool ignoreDefault = Physics.GetIgnoreLayerCollision(0, l);
                Physics.IgnoreLayerCollision(LayerA, l, ignoreDefault);
                Physics.IgnoreLayerCollision(LayerB, l, ignoreDefault);
            }

            Physics.IgnoreLayerCollision(LayerA, LayerA, Physics.GetIgnoreLayerCollision(0, 0));
            Physics.IgnoreLayerCollision(LayerB, LayerB, Physics.GetIgnoreLayerCollision(0, 0));
            Physics.IgnoreLayerCollision(LayerA, LayerB, true);
            double matrixMs = matrix.Elapsed.TotalMilliseconds;

            (Rigidbody a, Rigidbody b, List<MeshCollider> crossed, List<MeshCollider> copies) = Build();
            double warm = Simulate(1);   // the contacts before the exclusion (every A-B pair touches)
            string before = Contacts();
            b.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);   // back where it stood (the overlap pushed it)
            b.linearVelocity = Vector3.zero;
            b.angularVelocity = Vector3.zero;
            _pairs.Clear(); _enters = 0; _exits = 0;

            var set = Stopwatch.StartNew();
            long calls = 0;
            if (byLayers)
            {
                foreach (MeshCollider c in crossed) { c.gameObject.layer = LayerA; calls++; }
                foreach (MeshCollider c in copies) { c.gameObject.layer = LayerB; calls++; }
            }
            else
            {
                foreach (MeshCollider copy in copies) foreach (MeshCollider own in crossed) { Physics.IgnoreCollision(copy, own, true); calls++; }
            }

            double setMs = set.Elapsed.TotalMilliseconds;
            double first = Simulate(1), next = Simulate(4);
            string after = Contacts();
            bool touchingAfter = _pairs.ContainsKey("A-B");
            // Moved and turned apart and back into each other: the relation holds whatever the sides do.
            b.transform.SetPositionAndRotation(new Vector3(0.3f, 0.2f, 0.4f), Quaternion.Euler(0f, 17f, 5f));
            b.linearVelocity = Vector3.zero;
            _pairs.Clear();
            double moved = Simulate(3);
            string afterMove = Contacts();

            TestContext.Out.WriteLine((byLayers ? "layer pair" : "pairwise") + ": matrix set " + matrixMs.ToString("F3") + " ms; exclusion set " + setMs.ToString("F3") + " ms in " + calls + " calls; Simulate before "
                + warm.ToString("F3") + " ms a step, the first after " + first.ToString("F3") + ", the next four " + (next / 4).ToString("F3") + " a step, after the move " + (moved / 3).ToString("F3")
                + "\n  contacts before: " + before + "\n  contacts after: " + after + "\n  after the move: " + afterMove);

            Assert.That(before, Does.Contain("A-B"), "before the exclusion the crossed and their copies touch");
            foreach (string k in _pairs.Keys) Assert.That(k.StartsWith("A-B"), Is.False, "no A-B contact after the move: " + k);
            Assert.That(touchingAfter, Is.False, "no A-B contact after the exclusion (only its exits): " + after);
            Assert.That(after, Does.Contain("B-U"), "the copies still touch the uncut neighbours on the other body");
            Assert.That(after, Does.Contain("A-V"), "and the crossed their neighbours on the copies' body");
            Assert.That(afterMove, Does.Contain("B-U").Or.Contain("A-V"), "the sides moved and turned still meet by the other pairs");
        }
    }
}
