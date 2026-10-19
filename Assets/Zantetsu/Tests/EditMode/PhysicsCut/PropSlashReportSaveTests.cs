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
        private static string Format(params object[] source)
        {
            var rows = Array.CreateInstance(Row, source.Length);
            for (int i = 0; i < source.Length; i++) rows.SetValue(source[i], i);
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
        public void WorkerTimeline_PreservesNativeCounterDeltasAcrossAnUnknownRow()
        {
            object first = Activator.CreateInstance(Row, true);
            object unknown = Activator.CreateInstance(Row, true);
            object last = Activator.CreateInstance(Row, true);
            string[] fields = { "nPointersAsked", "nPointersKept", "nBodyIssues", "nCasterIssues", "nNotDrawn", "nBuffersRetired" };
            long[] before = { 10, 4, 20, 30, 40, 50 }, after = { 15, 8, 29, 33, 45, 56 };
            Set(first, "nativeKnown", true); Set(last, "nativeKnown", true);
            foreach (object row in new[] { first, unknown, last }) Set(row, "markers", new long[] { 42 });
            for (int i = 0; i < fields.Length; i++) { Set(first, fields[i], before[i]); Set(last, fields[i], after[i]); }
            Set(last, "nBlocksInFlight", 4);
            object frozen = Row.GetMethod("FrozenCopy", Instance).Invoke(last, null);
            Set(last, "nBodyIssues", 999L);
            string result = null;
            var write = new CheckReportWrite(() => result = Format(first, unknown, frozen));
            write.Wait(); write.ThrowIfFailed();
            string[] lines = result.Split('\n');
            string[] columns = lines[0].Split(',');
            int start = Array.IndexOf(columns, "nPointersAsked");
            Assert.That(start, Is.GreaterThan(0));
            for (int row = 1; row <= 3; row++) Assert.That(lines[row].Split(',').Length, Is.EqualTo(columns.Length));
            foreach (int row in new[] { 1, 2 })
                for (int i = 0; i < 7; i++) Assert.That(lines[row].Split(',')[start + i], Is.Empty);
            Assert.That(string.Join(",", lines[3].Split(','), start, 7), Is.EqualTo("5,4,9,3,5,6,4"));
        }

        [Test]
        public void Timeline_WritesTheMotionPassCountsAsDifferences_AndTheAppSwSectionsCallsAsTheyStand()
        {
            object first = Activator.CreateInstance(Row, true);
            object unknown = Activator.CreateInstance(Row, true);
            object last = Activator.CreateInstance(Row, true);
            Set(first, "nativeKnown", true); Set(last, "nativeKnown", true);
            foreach (object row in new[] { first, unknown, last }) Set(row, "markers", new long[] { 42 });
            Set(first, "nMotionIssues", 100L); Set(first, "nClipMotionTaken", 20L); Set(first, "nClipMotionKept", 10L);
            Set(last, "nMotionIssues", 107L); Set(last, "nClipMotionTaken", 26L); Set(last, "nClipMotionKept", 12L);
            Set(last, "appSwCullCalls", 1L); Set(last, "appSwRecordCalls", 1L); Set(last, "appSwNativeCullCalls", 3L); Set(last, "appSwMotionClipKeepCalls", 0L);
            object frozen = Row.GetMethod("FrozenCopy", Instance).Invoke(last, null);
            Set(last, "appSwNativeCullCalls", 999L);
            string[] lines = Format(first, unknown, frozen).Split('\n');
            string[] columns = lines[0].Split(',');
            int motion = Array.IndexOf(columns, "nMotionIssues"), calls = Array.IndexOf(columns, "appSwCullCalls");
            Assert.That(motion, Is.EqualTo(Array.IndexOf(columns, "nBlocksInFlight") + 1), "after the plugin's own counts");
            Assert.That(string.Join(",", columns, calls, 4), Is.EqualTo("appSwCullCalls,appSwRecordCalls,appSwNativeCullCalls,appSwMotionClipKeepCalls"));
            Assert.That(columns[columns.Length - 1], Is.EqualTo("fixture-marker"), "the markers stay last");
            for (int row = 1; row <= 3; row++) Assert.That(lines[row].Split(',').Length, Is.EqualTo(columns.Length));
            Assert.That(string.Join(",", lines[1].Split(','), motion, 3), Is.EqualTo(",,"), "the first row has no row before");
            Assert.That(string.Join(",", lines[1].Split(','), calls, 4), Is.EqualTo("-1,-1,-1,-1"), "a row never completed: not known");
            Assert.That(string.Join(",", lines[3].Split(','), motion, 3), Is.EqualTo("7,6,2"), "the difference from the last known row");
            Assert.That(string.Join(",", lines[3].Split(','), calls, 4), Is.EqualTo("1,1,3,0"), "as frozen at the save");
        }

        [Test]
        public void TheTimeline_RecordsTheFourAppSwSections_InTheirOrder()
        {
            var fixedMarkers = (Array)Walk.GetField("TimelineMarkers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var names = new System.Collections.Generic.List<string>();
            foreach (object marker in fixedMarkers) names.Add((string)marker.GetType().GetField("Item2").GetValue(marker));
            // This assembly does not reference the URP one: the sections' class is read by its name.
            Type sections = Type.GetType("Zantetsu.Rendering.Urp.VpAppSwMarkers, Zantetsu.Rendering.Urp", true);
            var expected = (string[])sections.GetField("Names").GetValue(null);
            Assert.That(expected, Is.EqualTo(new[] { "Zantetsu.AppSwDistance.UnityCull", "Zantetsu.AppSwDistance.Record",
                "Zantetsu.AppSwDistance.NativeCullRecord", "Zantetsu.AppSwDistance.MotionClipKeep" }), "the existing UnityCull marker's name is kept");
            int at = names.IndexOf(expected[0]);
            Assert.That(at, Is.GreaterThanOrEqualTo(0));
            Assert.That(names.GetRange(at, 4), Is.EqualTo(expected));
            Assert.That(sections.GetMethod("Ensure").Invoke(null, null), Is.EqualTo(4), "the markers exist");
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
