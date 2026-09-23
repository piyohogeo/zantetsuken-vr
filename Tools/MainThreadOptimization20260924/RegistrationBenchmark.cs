// Measurement template: temporarily copy into the EditMode test assembly, then remove it after the run.
// Measures only duplicate-registration lookup on Unity's Mono main thread, not a complete geometry commit.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MainThreadAudit.Tests
{
    public class RegistrationBenchmark
    {
        private const int Iterations = 4096;
        private const int Samples = 8;

        private sealed class Fixture : IDisposable
        {
            internal VpCpuGeometryStorage storage;
            internal VpGeometryReferenceTable table;
            internal VpIndexRangeHandle unregistered;
            internal VpIndexRangeHandle duplicate;

            public void Dispose()
            {
                storage?.Dispose();
                storage = null;
            }
        }

        [Test]
        public void Measure()
        {
            string directory = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_OUTPUT");
            string variant = Environment.GetEnvironmentVariable("ZANTETSU_AUDIT_VARIANT");
            Assert.That(string.IsNullOrEmpty(directory), Is.False, "ZANTETSU_AUDIT_OUTPUT is required");
            Assert.That(string.IsNullOrEmpty(variant), Is.False, "ZANTETSU_AUDIT_VARIANT is required");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "registration.csv");

            // Some Unity Mono versions return zero even for a known allocation. Preserve that limitation in the CSV.
            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            var allocationProbe = new byte[4096];
            long allocationProbeDelta = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
            GC.KeepAlive(allocationProbe);

            var csv = new StringBuilder();
            csv.AppendLine("variant,scenario,role,scope,live,slots_used_before_lookup,geometry_capacity,descriptor_capacity,sample,iterations,expected_true,observed_true,ticks,stopwatch_frequency,ns_per_call,allocated_bytes,allocation_counter_responded,allocation_probe_delta");
            MethodInfo lookup = typeof(VpGeometryReferenceTable).GetMethod("IsRegistered", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(lookup, Is.Not.Null);
            RunCase(csv, path, variant, "live16", "representative", 16, 16, lookup, allocationProbeDelta);
            RunCase(csv, path, variant, "live32", "representative", 32, 32, lookup, allocationProbeDelta);
            RunCase(csv, path, variant, "live512", "stress", 512, 512, lookup, allocationProbeDelta);
            RunCase(csv, path, variant, "highwater2048_live16", "stress", 16, 2048, lookup, allocationProbeDelta);
        }

        private static void RunCase(StringBuilder csv, string path, string variant, string scenario, string role,
            int live, int slotsUsed, MethodInfo lookup, long allocationProbeDelta)
        {
            using (Fixture fixture = Build(live, slotsUsed))
            {
                // Reflection and delegate creation occur once, outside every timed batch. The handles have already
                // passed the storage validation that normally precedes this private lookup in CanRegister.
                var registered = (Func<VpIndexRangeHandle, bool>)Delegate.CreateDelegate(
                    typeof(Func<VpIndexRangeHandle, bool>), fixture.table, lookup);
                Record(csv, variant, scenario, role, "unregistered", live, slotsUsed,
                    registered, fixture.unregistered, false, allocationProbeDelta);
                File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
                Record(csv, variant, scenario, role, "late_duplicate", live, slotsUsed,
                    registered, fixture.duplicate, true, allocationProbeDelta);
                File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
            }
        }

        private static Fixture Build(int live, int slotsUsed)
        {
            var fixture = new Fixture();
            try
            {
                fixture.storage = new VpCpuGeometryStorage(16384, 32768, 4096, 8192, 8192, Allocator.Persistent);
                fixture.table = new VpGeometryReferenceTable(fixture.storage, 2048, 2048);
                Mesh quad = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                Assert.That(quad, Is.Not.Null);
                var references = new VpGeometryReference[slotsUsed];
                for (int i = 0; i < slotsUsed; i++)
                {
                    Assert.That(fixture.storage.TryAppend(quad, out VpStoredGeometry geometry), Is.True);
                    Assert.That(fixture.table.TryRegisterGeometry(geometry, out references[i]), Is.True);
                    fixture.duplicate = geometry.indexRange;
                }

                // Keep the last live slots. This puts the duplicate at the old scan's last used slot, including
                // when most earlier registrations have retired and the baseline high-water mark remains 2048.
                for (int i = 0; i < slotsUsed - live; i++)
                {
                    Assert.That(fixture.table.TryRetireGeometry(references[i]), Is.True);
                }

                Assert.That(fixture.storage.TryAppend(quad, out VpStoredGeometry unregistered), Is.True);
                fixture.unregistered = unregistered.indexRange;
                Assert.That(fixture.table.LiveGeometryCount, Is.EqualTo(live));
                Assert.That(fixture.storage.TryGetIndexState(fixture.unregistered, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Published));
                Assert.That(fixture.storage.TryGetIndexState(fixture.duplicate, out state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Published));
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        private static void Record(StringBuilder csv, string variant, string scenario, string role, string scope,
            int live, int slotsUsed, Func<VpIndexRangeHandle, bool> lookup, VpIndexRangeHandle handle,
            bool expected, long allocationProbeDelta)
        {
            int expectedCount = expected ? Iterations : 0;
            for (int sample = -1; sample < Samples; sample++)
            {
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                long begin = Stopwatch.GetTimestamp();
                int observed = 0;
                for (int i = 0; i < Iterations; i++)
                {
                    if (lookup(handle)) observed++;
                }
                long elapsed = Stopwatch.GetTimestamp() - begin;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Assert.That(observed, Is.EqualTo(expectedCount), scenario + "/" + scope);
                if (sample < 0) continue; // Exactly one batch warms this lookup before its eight reported samples.

                double nsPerCall = elapsed * 1e9 / Stopwatch.Frequency / Iterations;
                csv.Append(Csv(variant)).Append(',').Append(scenario).Append(',').Append(role).Append(',').Append(scope).Append(',')
                    .Append(live).Append(',').Append(slotsUsed).Append(",2048,4096,").Append(sample).Append(',')
                    .Append(Iterations).Append(',').Append(expectedCount).Append(',').Append(observed).Append(',')
                    .Append(elapsed).Append(',').Append(Stopwatch.Frequency).Append(',')
                    .Append(nsPerCall.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(allocated).Append(',')
                    .Append(allocationProbeDelta >= 4096 ? "true" : "false").Append(',').Append(allocationProbeDelta).AppendLine();
            }
        }

        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
