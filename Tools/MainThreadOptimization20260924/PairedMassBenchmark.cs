// Temporary EditMode template. Alongside this file, install the 8bae0b1e ProvisionalBoxMass.cs with
// only the type name replaced by BaselineProvisionalBoxMass, plus the existing SandboxCharacterBody helper.
// Both implementations consume the same authored shape. No runtime or existing fixture is modified.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.MainThreadAudit.Tests
{
    public class PairedMassBenchmark
    {
        private const int Iterations = 1024;
        private const int Rounds = 8;
        private static readonly float3 Inertia = new float3(4);
        private static double _sink;

        [Test]
        public void Measure()
        {
            string directory = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_OUTPUT");
            Assert.That(directory, Is.Not.Null.And.Not.Empty);
            Directory.CreateDirectory(directory);
            string run = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_RUN")
                ?? new DirectoryInfo(directory).Name;
            string path = Path.Combine(directory, "paired-mass.csv");
            var csv = new StringBuilder("run,case,round,order,version,ticks,freq,iterations\n");
            var environment = new StringBuilder();
            environment.AppendLine("Unity=" + Application.unityVersion);
            environment.AppendLine("Backend=Editor Mono; Main Stopwatch elapsed time, not CPU time or complete cut cost.");
            environment.AppendLine("Thread=" + Thread.CurrentThread.ManagedThreadId);
            environment.AppendLine("Baseline=8bae0b1e; candidate=current ProvisionalBoxMass; same input per process.");
            environment.AppendLine("Order=ABBA (A baseline, B candidate), 8 rounds, 1024 calls per batch.");
            environment.AppendLine("Warmup=one 1024-call batch per version and case; not saved in CSV.");
            environment.AppendLine("No forced GC; setup/disposal/assertions/CSV writes outside timed batches.");

            foreach (bool character in new[] { false, true })
            {
                using (var fixture = new Fixture())
                {
                    fixture.Initialize(character);
                    string name = character ? "character" : "box";
                    AssertIdentical(fixture.Shape, fixture.Plane, out float3 lo, out float3 hi);
                    environment.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}: lo=({1:R},{2:R},{3:R}), hi=({4:R},{5:R},{6:R}), plane=({7:R},{8:R},{9:R},{10:R}), parentMass=12, sourceInertia=(4,4,4), sourceInertiaRotation=identity\n",
                        name, lo.x, lo.y, lo.z, hi.x, hi.y, hi.z,
                        fixture.Plane.x, fixture.Plane.y, fixture.Plane.z, fixture.Plane.w);
                    File.WriteAllText(Path.Combine(directory, "paired-mass-environment.txt"), environment.ToString(), new UTF8Encoding(false));

                    BaselineBatch(fixture.Shape, fixture.Plane);
                    CandidateBatch(fixture.Shape, fixture.Plane);
                    for (int round = 0; round < Rounds; round++)
                    {
                        for (int order = 0; order < 4; order++)
                        {
                            bool baseline = order == 0 || order == 3;
                            long ticks = baseline ? BaselineBatch(fixture.Shape, fixture.Plane)
                                : CandidateBatch(fixture.Shape, fixture.Plane);
                            csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5},{6},{7}\n",
                                Csv(run), name, round, order, baseline ? "baseline" : "candidate",
                                ticks, Stopwatch.Frequency, Iterations);
                        }
                        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
                    }
                    AssertIdentical(fixture.Shape, fixture.Plane, out _, out _);
                }
            }
        }

        private static long BaselineBatch(PhysicsOwnerShape shape, float4 plane)
        {
            int succeeded = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Iterations; i++)
            {
                bool ok = BaselineProvisionalBoxMass.TryDivide(shape, plane, 12, Inertia, quaternion.identity,
                    out var positive, out var negative, out _);
                succeeded += ok ? 1 : 0;
                _sink = positive.mass + negative.mass;
            }
            long elapsed = Stopwatch.GetTimestamp() - start;
            Assert.That(succeeded, Is.EqualTo(Iterations));
            return elapsed;
        }

        private static long CandidateBatch(PhysicsOwnerShape shape, float4 plane)
        {
            int succeeded = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < Iterations; i++)
            {
                bool ok = ProvisionalBoxMass.TryDivide(shape, plane, 12, Inertia, quaternion.identity,
                    out var positive, out var negative, out _);
                succeeded += ok ? 1 : 0;
                _sink = positive.mass + negative.mass;
            }
            long elapsed = Stopwatch.GetTimestamp() - start;
            Assert.That(succeeded, Is.EqualTo(Iterations));
            return elapsed;
        }

        private static void AssertIdentical(PhysicsOwnerShape shape, float4 plane, out float3 lo, out float3 hi)
        {
            Assert.That(BaselineProvisionalBoxMass.TryBox(shape, out float3 baselineLo, out float3 baselineHi), Is.True);
            Assert.That(ProvisionalBoxMass.TryBox(shape, out lo, out hi), Is.True);
            AssertFloat3(baselineLo, lo, "box low");
            AssertFloat3(baselineHi, hi, "box high");
            Assert.That(BaselineProvisionalBoxMass.TryDivide(shape, plane, 12, Inertia, quaternion.identity,
                out var baselinePositive, out var baselineNegative, out float3 baselineNormal), Is.True);
            Assert.That(ProvisionalBoxMass.TryDivide(shape, plane, 12, Inertia, quaternion.identity,
                out var positive, out var negative, out float3 normal), Is.True);
            // The optimization reuses identical corner-distance expressions; it changes neither the arithmetic
            // order of each expression nor the integration order. Require exact bits, with no tolerance relaxation.
            AssertSide(baselinePositive, positive, "positive");
            AssertSide(baselineNegative, negative, "negative");
            AssertFloat3(baselineNormal, normal, "plane normal");
        }

        private static void AssertSide(BaselineProvisionalBoxMass.Side expected, ProvisionalBoxMass.Side actual, string label)
        {
            Assert.That(BitConverter.DoubleToInt64Bits(actual.mass), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected.mass)), label + " mass bits");
            AssertFloat3(expected.centerOfMass, actual.centerOfMass, label + " COM");
            AssertFloat3(expected.inertia, actual.inertia, label + " inertia");
            Assert.That(math.all(math.asuint(expected.inertiaRotation.value) == math.asuint(actual.inertiaRotation.value)), Is.True, label + " inertia rotation bits");
        }

        private static void AssertFloat3(float3 expected, float3 actual, string label)
        {
            Assert.That(math.all(math.asuint(expected) == math.asuint(actual)), Is.True, label + " bits");
        }

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private sealed class Fixture : IDisposable
        {
            internal PhysicsOwnerShape Shape;
            internal float4 Plane;
            private VpCpuGeometryStorage _storage;
            private IDisposable _body;

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
            }

            public void Dispose()
            {
                try { _body?.Dispose(); }
                finally { _storage?.Dispose(); }
            }
        }
    }
}
