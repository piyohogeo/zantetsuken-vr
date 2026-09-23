// Installed into the EditMode test assembly by run_experiments.py, never into the product.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.MainThreadAudit.Tests
{
    public class PhysicsMainBenchmark
    {
        private const int Samples = 8;
        private static readonly float3 Inertia = new float3(4);
        private readonly StringBuilder _csv = new StringBuilder();
        private bool _allocationCounterWorks;
        private string _variant;
        private static object _probe;
        private static double _sink;

        [Test]
        public void Measure()
        {
            string directory = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_OUTPUT");
            Assert.That(directory, Is.Not.Null.And.Not.Empty, "run with the experiment runner");
            _variant = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_VARIANT");
            long before = GC.GetAllocatedBytesForCurrentThread();
            _probe = new byte[4096];
            long probeBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _allocationCounterWorks = probeBytes >= 4096;
            _csv.AppendLine("variant,case,region,sample,iterations,elapsed_ticks,allocated_bytes,counter_responded,frequency");
            File.WriteAllText(Path.Combine(directory, "physics-environment.txt"),
                "Unity=" + Application.unityVersion + "\nBackend=Editor Mono\nThread="
                + Thread.CurrentThread.ManagedThreadId + "\nStopwatchFrequency=" + Stopwatch.Frequency
                + "\nAllocationProbeDelta=" + probeBytes + "\nInput=box(0.25,0.25,0.25), m_8 intake"
                + "\nNo forced GC; no render loop in measured regions; actors inactive.\n");
            foreach (bool character in new[] { false, true })
            {
                using (var fixture = new Fixture())
                {
                    fixture.Initialize(character);
                    string name = character ? "character" : "box";
                    MeasureShapes(fixture, name);
                    MeasureMass(fixture, name);
                    fixture.Cook(); // Work preparation/completion is outside every measured interval.
                    MeasureBuildAndColliders(fixture, name);
                }
            }
            File.WriteAllText(Path.Combine(directory, "physics.csv"), _csv.ToString(), new UTF8Encoding(false));
        }

        private void Record(string name, string region, int sample, int iterations, long ticks, long allocated)
        {
            _csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7},{8}\n",
                _variant, name, region, sample, iterations, ticks,
                _allocationCounterWorks ? allocated : -1, _allocationCounterWorks, Stopwatch.Frequency);
        }

        private void MeasureShapes(Fixture f, string name)
        {
            int[] positive = Indices(f.Classification, true), negative = Indices(f.Classification, false);
            const int iterations = 256;
            // Both allocation and release are inside: moving work from creation to disposal cannot win this metric.
            for (int sample = -1; sample < Samples; sample++)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                {
                    PhysicsOwnerShape p = null, n = null;
                    try
                    {
                        p = PhysicsOwnerShape.ProvisionalSide(f.Shape, positive);
                        n = PhysicsOwnerShape.ProvisionalSide(f.Shape, negative);
                        _sink = p.ConvexCount + n.ConvexCount;
                    }
                    finally { n?.Dispose(); p?.Dispose(); }
                }
                long elapsed = Stopwatch.GetTimestamp() - start;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                if (sample >= 0) Record(name, "shape_pair_create_dispose", sample, iterations, elapsed, allocated);
            }
        }

        private void MeasureMass(Fixture f, string name)
        {
            const int iterations = 1024;
            for (int sample = -1; sample < Samples; sample++)
            {
                int success = 0;
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                {
                    bool ok = ProvisionalBoxMass.TryDivide(f.Shape, f.Plane, 12, Inertia, quaternion.identity,
                        out var positive, out var negative, out _);
                    success += ok ? 1 : 0;
                    _sink = positive.mass + negative.mass;
                }
                long elapsed = Stopwatch.GetTimestamp() - start;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                Assert.That(success, Is.EqualTo(iterations));
                if (sample >= 0) Record(name, "box_mass_divide", sample, iterations, elapsed, allocated);
            }
        }

        private void MeasureBuildAndColliders(Fixture f, string name)
        {
            var input = new ProvisionalOwnerBuildInput
            {
                sourceShape = f.Shape, sides = f.Classification.Sides, planeLocal = f.Plane,
                placement = PhysicsOwnerPlacement.Identity, sourceMotion = default,
                anchors = default, parentMass = 12, sourceInertia = Inertia,
                sourceInertiaRotation = quaternion.identity, cooking = PhysicsCutCook.DefaultCooking,
                name = "Main audit",
            };
            // Four warm-up pairs, then 64 one-shot builds/preparations/adoptions. Destruction is out of these spans.
            for (int sample = -4; sample < 64; sample++)
            {
                ProvisionalOwnerCandidate pair = null;
                PreparedSideColliders p = null, n = null;
                try
                {
                    long bytes = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    bool built = ProvisionalOwnerBuilder.TryBuild(in input, out pair, out var outcome);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    Assert.That(built && outcome == PhysicsOwnerBuildOutcome.Ok, Is.True);
                    Assert.That(pair.Positive.Colliders.Count, Is.EqualTo(name == "character" ? 15 : 1));
                    Assert.That(pair.Negative.Colliders.Count, Is.EqualTo(name == "character" ? 9 : 1));
                    if (sample >= 0) Record(name, "provisional_build", sample, 1, elapsed, allocated);

                    bytes = GC.GetAllocatedBytesForCurrentThread();
                    start = Stopwatch.GetTimestamp();
                    p = PhysicsOwnerBuilder.PrepareFinalColliders(f.Products, f.Shape.Meshes, pair.Positive,
                        pair.PositiveShape, quaternion.identity, float3.zero);
                    n = PhysicsOwnerBuilder.PrepareFinalColliders(f.Products, f.Shape.Meshes, pair.Negative,
                        pair.NegativeShape, quaternion.identity, float3.zero);
                    elapsed = Stopwatch.GetTimestamp() - start;
                    allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    Assert.That(p.KeptCount + n.KeptCount, Is.EqualTo(name == "character" ? 14 : 0));
                    Assert.That(p.MadeCount + n.MadeCount, Is.EqualTo(name == "character" ? 10 : 2));
                    if (sample >= 0) Record(name, "prepare_colliders", sample, 1, elapsed, allocated);

                    bytes = GC.GetAllocatedBytesForCurrentThread();
                    start = Stopwatch.GetTimestamp();
                    p.Adopt(quaternion.identity, float3.zero);
                    n.Adopt(quaternion.identity, float3.zero);
                    elapsed = Stopwatch.GetTimestamp() - start;
                    allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                    Assert.That(p.IsAdopted && n.IsAdopted, Is.True);
                    if (sample >= 0) Record(name, "adopt_colliders", sample, 1, elapsed, allocated);
                }
                finally
                {
                    n?.Withdraw(); p?.Withdraw(); pair?.Dispose();
                }
            }
        }

        private static int[] Indices(PhysicsCutClassification classification, bool positive)
        {
            var into = new List<int>();
            for (int c = 0; c < classification.ConvexCount; c++)
            {
                var side = classification.Sides[c];
                if (side == Zantetsu.ConvexCut.ConvexSide.Split
                    || (positive && side != Zantetsu.ConvexCut.ConvexSide.Negative)
                    || (!positive && side == Zantetsu.ConvexCut.ConvexSide.Negative)) into.Add(c);
            }
            return into.ToArray();
        }

        private sealed class Fixture : IDisposable
        {
            internal PhysicsOwnerShape Shape;
            internal float4 Plane;
            internal PhysicsCutClassification Classification;
            internal PhysicsCutProducts Products;
            private VpCpuGeometryStorage _storage;
            private IDisposable _body;
            private PhysicsCutCook _cook;
            private SharedWorkDispatcher _dispatcher;
            private WorkerPoolExecutor _geometry, _background;
            private static readonly List<Fixture> Unconfirmed = new List<Fixture>();

            internal void Initialize(bool character)
            {
                _storage = new VpCpuGeometryStorage(262144, 1048576, 2048, 8192, 8192, Allocator.Persistent);
                if (character)
                {
                    string root = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_INTAKE")
                        ?? @"C:\log\zantetsuken-vr\Phase41ActPlayer\intake\m8";
                    var body = SandboxCharacterBody.TryBuild(_storage, Path.Combine(root, "m_8.hulls.json"),
                        Path.Combine(root, "m_8.render.json"), 0);
                    _body = body; Shape = body?.Shape; Plane = new float4(0, 0, 1, -0.9f);
                }
                else
                {
                    var body = SandboxCompoundBody.TryBuild(_storage, 1, 1, new float3(0.25f), 0);
                    _body = body; Shape = body?.Shape; Plane = new float4(0, 1, 0, 0);
                }
                Assert.That(Shape, Is.Not.Null);
                Assert.That(PhysicsCutClassification.TryClassify(Shape, Plane, 1e-4f, 12, 128, out Classification), Is.True);
            }

            internal void Cook()
            {
                _geometry = WorkerPoolExecutor.GeometryPool(2);
                _background = WorkerPoolExecutor.BackgroundPool(2);
                _dispatcher = new SharedWorkDispatcher(8, 2, 32, new UnityJobWorkExecutor(4), _geometry, _background);
                _cook = new PhysicsCutCook(_dispatcher, 1);
                var input = Classification.Input;
                PhysicsCutRequest request = _cook.Submit(in input, Shape.LocalToOwner);
                var watch = Stopwatch.StartNew();
                int frame = 0;
                while (!request.IsOver && watch.ElapsedMilliseconds < 30000)
                {
                    _dispatcher.BeginFrame(++frame); _dispatcher.Dispatch(); _cook.Pump(); Thread.Sleep(1);
                }
                Assert.That(request.IsOver && request.Outcome == PhysicsCutOutcomeKind.Ok, Is.True, "untimed cook");
                Products = request.Products;
            }

            public void Dispose()
            {
                if (_dispatcher != null)
                {
                    _cook?.Dispose();
                    bool stopped = _dispatcher.Shutdown(30000).workersStopped;
                    _cook?.Pump();
                    if (!stopped || (_cook != null && !_cook.IsDrained))
                    {
                        Unconfirmed.Add(this);
                        throw new InvalidOperationException("workers not confirmed stopped; fixture retained");
                    }
                }
                _geometry?.Dispose(); _background?.Dispose();
                Products?.Dispose(); Classification?.Dispose(); _body?.Dispose(); _storage?.Dispose();
            }
        }
    }
}
