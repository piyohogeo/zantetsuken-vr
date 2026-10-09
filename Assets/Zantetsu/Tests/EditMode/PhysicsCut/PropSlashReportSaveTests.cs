using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    public class PropSlashReportSaveTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Walk = typeof(SandboxPropSlashPlayerCheck).GetNestedType("Walk", BindingFlags.NonPublic);
        private static readonly Type Row = Walk.GetNestedType("FrameRow", BindingFlags.NonPublic);
        private static void Set(object row, string name, object value) => Row.GetField(name, Instance).SetValue(row, value);
        private static string Format(object row)
        {
            var rows = Array.CreateInstance(Row, 1);
            rows.SetValue(row, 0);
            return (string)Walk.GetMethod("FormatTimeline", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { rows, new[] { "fixture-marker" }, true });
        }

        [Test]
        public void TimelineSerializer_PreservesTheInvariantCsvFixture_AndFreezesTheMutableTail()
        {
            object row = Activator.CreateInstance(Row, true);
            Set(row, "frame", 7); Set(row, "phase", "replay"); Set(row, "real", 1.25); Set(row, "delta", 0.025f);
            Set(row, "fed", 3); Set(row, "recorded", 0.5); Set(row, "stepId", 4L); Set(row, "unsimulated", 0.125);
            Set(row, "sweeps", 2); Set(row, "waves", 1); Set(row, "hits", 5);
            var markers = new long[] { 42 }; Set(row, "markers", markers);
            var validate = new VpValidateCounts { structural = 4, indexSeconds = 0.003 }; Set(row, "validate", validate);
            var structural = new VpPlaceCounts { passes = 2 }; Set(row, "placeStructural", structural);
            var placement = new VpPlaceCounts { passes = 3 }; Set(row, "placePlacementOnly", placement);
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string expected = Format(row);
                string[] lines = expected.Split('\n');
                Assert.That(lines[0], Does.StartWith("frame,phase,real,delta,fed,recorded,realMinusRecorded,stepId,unsimulated,sweeps,waves,hits,picturesAsked,"));
                Assert.That(lines[0], Does.EndWith(",fixture-marker"));
                Assert.That(lines[1], Does.StartWith("7,replay,1.25,0.025,3,0.5,0.75,4,0.125,2,1,5,0,"));
                Assert.That(lines[1], Does.EndWith(",42"));
                object frozen = Row.GetMethod("FrozenCopy", Instance).Invoke(row, null);
                Set(row, "notes", 99); markers[0] = 999; validate.structural = 999; structural.passes = 999; placement.passes = 999;
                Assert.That(Format(row), Is.Not.EqualTo(expected));
                string workerResult = null;
                var write = new CheckReportWrite(() => workerResult = Format(frozen));
                write.Wait(); write.ThrowIfFailed();
                Assert.That(workerResult, Is.EqualTo(expected), "the worker sees the values at the save boundary, including the tail's reference-backed counts");
            }
            finally { CultureInfo.CurrentCulture = before; }
        }

        [Test]
        public void FailedWorkerWrite_CompletesBeforeTheExistingClosingGuard_AndDoneStillFollowsLogRelease()
        {
            using var gate = new ManualResetEventSlim(false);
            var write = new CheckReportWrite(() => { gate.Wait(); throw new IOException("fixture disk error"); });
            Assert.That(write.Done, Is.False);
            gate.Set(); write.Wait();
            Assert.That(write.Done, Is.True);
            var ending = new CheckEnding(_ => { });
            bool released = false, notified = false;
            ending.Complete(new[] { new CheckEnding.Part("timeline", write.ThrowIfFailed) }, () => released = true,
                failures => failures == 0 ? 0 : 14, code => { Assert.That(released, Is.True); Assert.That(code, Is.EqualTo(14)); notified = true; });
            Assert.That(ending.Failures, Is.EqualTo(1));
            Assert.That(ending.FailedParts, Is.EqualTo(new[] { "timeline" }));
            Assert.That(ending.Done && notified, Is.True);
        }
    }
}
