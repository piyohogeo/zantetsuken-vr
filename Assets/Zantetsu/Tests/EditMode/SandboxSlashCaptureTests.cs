using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The slash capture, checked on short known inputs before any device is
    /// used: that it saves where it says and reads back; that the current
    /// method recomputed from the saved input and the saved conditions matches
    /// what was recorded beside it, including from a katana that already had a
    /// history and live waves when the capture started; that a save which
    /// cannot be written is reported as a failure; that a second run never
    /// writes into the first; that a conditions change ends the capture at the
    /// boundary; and that a start which would discard unsaved rows is refused.
    /// <para>
    /// The cross-check compares the columns of <c>current.csv</c> other than
    /// the update time and the candidate guide's own origin and direction; see
    /// <see cref="Key"/> for what that does and does not establish. Nothing
    /// here is a device recording, and nothing here settles whether the span
    /// axis proposal is better.
    /// </para>
    /// </summary>
    public class SandboxSlashCaptureTests
    {
        private const double SampleInterval = 0.011;

        // 0.12 m per 11 ms is about 10.9 m/s, past the 3.5 m/s floor, and the
        // chord over three steps is 0.36 m, past the 0.35 m latch chord.
        private static readonly Vector3 Step = new Vector3(0f, -0.12f, 0f);

        private readonly List<GameObject> made = new List<GameObject>();
        private readonly List<string> savedDirectories = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = made.Count - 1; i >= 0; i--)
            {
                if (made[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(made[i]);
                }
            }

            made.Clear();

            foreach (string directory in savedDirectories)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch (IOException)
                {
                    // A directory left behind is untidy, not a failed test.
                }
            }

            savedDirectories.Clear();
        }

        [Test]
        public void Save_WritesTheThreeKindsAndReadsBack()
        {
            SandboxSlashCapture capture = CaptureOneSweep(TestRoot("readback"), out int updates, out _);

            Assert.That(capture.TrySave(out string directory, out string failure), Is.True, failure);
            savedDirectories.Add(directory);

            Assert.That(File.Exists(Path.Combine(directory, "input.csv")), Is.True, "the raw input");
            Assert.That(File.Exists(Path.Combine(directory, "conditions.txt")), Is.True, "what it ran under");
            Assert.That(File.Exists(Path.Combine(directory, "current.csv")), Is.True, "what the current method made");
            Assert.That(
                Directory.Exists(Path.Combine(directory, "comparison")), Is.True,
                "a later comparison's results are kept apart from the run");

            string[] input = File.ReadAllLines(Path.Combine(directory, "input.csv"));
            Assert.That(input.Length, Is.EqualTo(updates + 1), "one header and one row per update");
            Assert.That(input[0], Does.Contain("gripX").And.Contain("rotW").And.Contain("accepted"));

            int rejected = 0;
            for (int i = 1; i < input.Length; i++)
            {
                if (input[i].Split(',')[16] == "0")
                {
                    rejected++;
                }
            }

            Assert.That(rejected, Is.GreaterThan(0), "the untracked sample is kept as rejected input");

            string conditions = File.ReadAllText(Path.Combine(directory, "conditions.txt"));
            Assert.That(conditions, Does.Contain("metres"), "the units are written down");
            Assert.That(conditions, Does.Contain("commit: "), "the revision is written down");
            Assert.That(conditions, Does.Contain("uncommitted changes: "));
            Assert.That(conditions, Does.Contain("capture ended by: Stopped"));
            Assert.That(conditions, Does.Contain("ending detail: "), "the reason is its own line, not the note");

            string[] current = File.ReadAllLines(Path.Combine(directory, "current.csv"));
            Assert.That(current.Length, Is.GreaterThan(1), "the latched wave was observed at least once");
            Assert.That(current[0], Does.Contain("acceptedSpan").And.Contain("rawSpan").And.Contain("denominator"));
        }

        [Test]
        public void Save_ReadsItsOwnConditionsBack()
        {
            SandboxRightHandKatana katana = NewKatana();
            Assert.That(katana.TrySetLatchChordMetres(0.28f), Is.True);
            Assert.That(katana.TrySetSpanCaptureTimeoutSeconds(0.18f), Is.True);
            Assert.That(katana.TrySetMinimumSpeed(4.25f), Is.True);

            SandboxSlashCapture capture = CaptureInto(katana, TestRoot("conditions"), out _);
            Assert.That(capture.TrySave(out string directory, out string failure), Is.True, failure);
            savedDirectories.Add(directory);

            Assert.That(
                SandboxSlashCapture.TryReadConditions(
                    Path.Combine(directory, "conditions.txt"), out SandboxSlashCapture.Conditions read,
                    out string readFailure),
                Is.True, readFailure);

            Assert.That(read.LatchChordMetres, Is.EqualTo(0.28f));
            Assert.That(read.SpanCaptureTimeoutSeconds, Is.EqualTo(0.18f));
            Assert.That(read.MinimumSpeed, Is.EqualTo(4.25f));
            Assert.That(read.WaveSpeed, Is.EqualTo(SandboxSlashWaveStore.WaveSpeed));
            Assert.That(read.WaveLifetimeSeconds, Is.EqualTo(SandboxSlashWaveStore.WaveLifetimeSeconds));
            Assert.That(read.WaveCapacity, Is.EqualTo(SandboxSlashWaveStore.Capacity));
            Assert.That(read.BladeLength, Is.EqualTo(katana.CaptureConditions.BladeLength));
        }

        [Test]
        public void Save_HoldsEnoughToRecomputeTheCurrentMethod_AtDefaultTuning()
        {
            SandboxSlashCapture capture = CaptureOneSweep(TestRoot("recompute"), out _, out _);
            Assert.That(capture.TrySave(out string directory, out string failure), Is.True, failure);
            savedDirectories.Add(directory);
            AssertRecomputes(directory);
        }

        [Test]
        public void Save_HoldsEnoughToRecomputeTheCurrentMethod_AtNonDefaultTuning()
        {
            SandboxRightHandKatana katana = NewKatana();

            // Away from every default the replay would otherwise fall back on.
            Assert.That(katana.TrySetMinimumSpeed(4.25f), Is.True);
            Assert.That(katana.TrySetMinimumDisplacement(0.11f), Is.True);
            Assert.That(katana.TrySetMinimumEdgeLeadScore(0.08f), Is.True);
            Assert.That(katana.TrySetReturnStrokeEdgeLeadScore(-0.22f), Is.True);
            Assert.That(katana.TrySetLatchChordMetres(0.28f), Is.True);
            Assert.That(katana.TrySetSpanCaptureTimeoutSeconds(0.18f), Is.True);
            Assert.That(katana.TrySetBeginBladeAxisViewDotMinimum(0.25f), Is.True);

            SandboxSlashCapture capture = CaptureInto(katana, TestRoot("recompute-tuned"), out _);
            Assert.That(capture.TrySave(out string directory, out string failure), Is.True, failure);
            savedDirectories.Add(directory);
            AssertRecomputes(directory);
        }

        [Test]
        public void Save_WhenStartedFromAKatanaThatAlreadyHasHistoryAndWaves_StillRecomputes()
        {
            SandboxRightHandKatana katana = NewKatana();
            SandboxSlashPoseRecorder recorder = NewRecorder(katana);

            // A swing before the capture: a stroke, a latched wave and a pose
            // history, none of it saved anywhere.
            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            for (int i = 0; i < 20; i++)
            {
                katana.TryRecordSample(Tracked(i + 1, time, position));
                time += SampleInterval;
                position += i < 6 ? Step : Vector3.zero;
            }

            Assert.That(katana.WaveCount, Is.GreaterThan(0), "there is state to be discarded");
            Assert.That(katana.AcceptedSampleCount, Is.GreaterThan(0));

            recorder.CaptureRunId = "midway";
            recorder.CaptureForTests.Root = TestRoot("midway");
            Assert.That(recorder.BeginCapture(), Is.True, recorder.LastSaveMessage);

            // The start put the calculation back to a known state.
            Assert.That(katana.WaveCount, Is.EqualTo(0), "waves on screen are cleared by the start");
            Assert.That(katana.AcceptedSampleCount, Is.EqualTo(0), "the stroke in progress is dropped");
            Assert.That(katana.RecordedPoseCount, Is.EqualTo(0), "the pose history is dropped");

            // Now a fresh swing, captured. Its times continue from before, so
            // a replay that secretly kept the old history would not match.
            position = new Vector3(0f, 1.4f, 0.3f);
            Sweep(katana, ref time, ref position);

            recorder.EndCapture();
            Assert.That(recorder.SaveCapture(), Is.True, recorder.LastSaveMessage);
            string directory = DirectoryFrom(recorder.LastSaveMessage);
            savedDirectories.Add(directory);
            AssertRecomputes(directory);
        }

        [Test]
        public void Save_WhenTheRootCannotBeWritten_SaysSo()
        {
            string file = Path.Combine(
                Path.GetTempPath(), "zantetsu-slash-capture-not-a-directory-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(file, "not a directory");
            try
            {
                SandboxSlashCapture capture = CaptureOneSweep(Path.Combine(file, "SlashSpan"), out _, out _);

                Assert.That(capture.TrySave(out string directory, out string failure), Is.False);
                Assert.That(directory, Is.Empty, "a failed save names no directory");
                Assert.That(failure, Is.Not.Empty, "the operator is told why");
                Assert.That(capture.HasUnsavedRows, Is.True, "a failed save leaves the rows unsaved");
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void Save_WhenNothingWasCaptured_SaysSoRatherThanWritingAnEmptyRun()
        {
            var capture = new SandboxSlashCapture { Root = TestRoot("empty") };
            capture.Begin("empty", NewKatana().CaptureConditions);
            capture.Stop();

            Assert.That(capture.TrySave(out string directory, out string failure), Is.False);
            Assert.That(directory, Is.Empty);
            Assert.That(failure, Does.Contain("nothing was captured"));
        }

        [Test]
        public void Save_Twice_NeverWritesIntoTheEarlierRun()
        {
            string root = TestRoot("norepeat");
            SandboxSlashCapture first = CaptureOneSweep(root, out _, out _);
            Assert.That(first.TrySave(out string firstDirectory, out string firstFailure), Is.True, firstFailure);
            savedDirectories.Add(firstDirectory);

            string[] firstInput = File.ReadAllLines(Path.Combine(firstDirectory, "input.csv"));

            SandboxSlashCapture second = CaptureOneSweep(root, out _, out _);
            Assert.That(second.TrySave(out string secondDirectory, out string secondFailure), Is.True, secondFailure);
            savedDirectories.Add(secondDirectory);

            Assert.That(secondDirectory, Is.Not.EqualTo(firstDirectory), "a second run gets a directory of its own");
            Assert.That(Directory.Exists(firstDirectory), Is.True, "the first run is still there");
            Assert.That(
                File.ReadAllLines(Path.Combine(firstDirectory, "input.csv")), Is.EqualTo(firstInput),
                "the first run's input is unchanged");
        }

        [Test]
        public void Capture_WhenFull_StopsAndSaysSoInsteadOfDroppingOldInput()
        {
            SandboxRightHandKatana katana = NewKatana();

            // A low bound, so the stop-when-full behaviour is exercised without
            // capturing the half a million updates a real capture allows.
            var capture = new SandboxSlashCapture { Root = TestRoot("full"), MaximumUpdates = 64 };
            capture.Begin("full", katana.CaptureConditions);
            katana.Capture = capture;

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            for (int i = 0; i < capture.MaximumUpdates + 1; i++)
            {
                katana.TryRecordSample(Tracked(i + 1, time, position));
                time += SampleInterval;
            }

            Assert.That(capture.EndedBy, Is.EqualTo(SandboxSlashCapture.Ending.BufferFull));
            Assert.That(capture.IsCapturing, Is.False);
            Assert.That(
                capture.UpdateCount, Is.EqualTo(capture.MaximumUpdates),
                "the earlier updates are all still there; capturing simply stopped");
            Assert.That(capture.EndingDetail, Does.Contain("truncated"));

            Assert.That(capture.TrySave(out string directory, out string failure), Is.True, failure);
            savedDirectories.Add(directory);
            string conditions = File.ReadAllText(Path.Combine(directory, "conditions.txt"));
            Assert.That(conditions, Does.Contain("capture ended by: BufferFull"));
            Assert.That(conditions, Does.Contain("ending detail: "));
        }

        [Test]
        public void Capture_WhenConditionsChange_EndsAtTheBoundaryAndKeepsWhatCameBefore()
        {
            SandboxRightHandKatana katana = NewKatana();
            var capture = new SandboxSlashCapture { Root = TestRoot("tuning") };
            capture.Begin("tuning", katana.CaptureConditions);
            katana.Capture = capture;

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            for (int i = 0; i < 4; i++)
            {
                katana.TryRecordSample(Tracked(i + 1, time, position));
                time += SampleInterval;
                position += Step;
            }

            int before = capture.UpdateCount;
            Assert.That(before, Is.EqualTo(4));

            Assert.That(katana.TrySetLatchChordMetres(0.2f), Is.True);
            katana.TryRecordSample(Tracked(9, time, position));

            Assert.That(capture.EndedBy, Is.EqualTo(SandboxSlashCapture.Ending.ConditionsChanged));
            Assert.That(
                capture.UpdateCount, Is.EqualTo(before),
                "the first update under the changed conditions is not kept");
            Assert.That(capture.EndingDetail, Does.Contain("changed"));
            Assert.That(capture.Note, Does.Not.Contain("changed"), "the reason is not smuggled into the note");

            // A later update does not restart it either.
            katana.TryRecordSample(Tracked(10, time + SampleInterval, position));
            Assert.That(capture.UpdateCount, Is.EqualTo(before));

            capture.Note = "the operator's own text";
            Assert.That(capture.TrySave(out string directory, out string failure), Is.True, failure);
            savedDirectories.Add(directory);
            string conditions = File.ReadAllText(Path.Combine(directory, "conditions.txt"));
            Assert.That(conditions, Does.Contain("capture ended by: ConditionsChanged"));
            Assert.That(conditions, Does.Contain("ending detail: the tuning or geometry changed"));
            Assert.That(conditions, Does.Contain("note: the operator's own text"), "the note survives the save");
            Assert.That(
                File.ReadAllLines(Path.Combine(directory, "input.csv")).Length, Is.EqualTo(before + 1),
                "what came before the change is all there");
        }

        [Test]
        public void BeginCapture_WhileCapturing_IsRefused()
        {
            SandboxRightHandKatana katana = NewKatana();
            SandboxSlashPoseRecorder recorder = NewRecorder(katana);
            recorder.CaptureForTests.Root = TestRoot("refuse-capturing");
            Assert.That(recorder.BeginCapture(), Is.True, recorder.LastSaveMessage);

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            Sweep(katana, ref time, ref position);
            int captured = recorder.CaptureForTests.UpdateCount;
            Assert.That(captured, Is.GreaterThan(0));

            Assert.That(recorder.BeginCapture(), Is.False, "a start while capturing is refused");
            Assert.That(recorder.LastSaveMessage, Does.Contain("already capturing"));
            Assert.That(recorder.CaptureForTests.UpdateCount, Is.EqualTo(captured), "nothing was discarded");
        }

        [Test]
        public void BeginCapture_WithUnsavedRows_IsRefusedUntilTheyAreSaved()
        {
            SandboxRightHandKatana katana = NewKatana();
            SandboxSlashPoseRecorder recorder = NewRecorder(katana);
            recorder.CaptureForTests.Root = TestRoot("refuse-unsaved");
            Assert.That(recorder.BeginCapture(), Is.True, recorder.LastSaveMessage);

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            Sweep(katana, ref time, ref position);
            recorder.EndCapture();

            int captured = recorder.CaptureForTests.UpdateCount;
            Assert.That(recorder.CaptureForTests.HasUnsavedRows, Is.True);
            Assert.That(recorder.BeginCapture(), Is.False, "a start after End but before Save is refused");
            Assert.That(recorder.LastSaveMessage, Does.Contain("not saved yet"));
            Assert.That(recorder.CaptureForTests.UpdateCount, Is.EqualTo(captured), "nothing was discarded");

            Assert.That(recorder.SaveCapture(), Is.True, recorder.LastSaveMessage);
            savedDirectories.Add(DirectoryFrom(recorder.LastSaveMessage));
            Assert.That(recorder.CaptureForTests.HasUnsavedRows, Is.False);
            Assert.That(recorder.BeginCapture(), Is.True, "once saved, the next capture can start");
        }

        [Test]
        public void AutoCapture_StartsWhenIdleAndOnlyAgainAfterASuccessfulSave()
        {
            SandboxRightHandKatana katana = NewKatana();
            SandboxSlashPoseRecorder recorder = NewRecorder(katana);
            recorder.AutoCaptureWhenIdle = true;
            recorder.CaptureForTests.Root = TestRoot("auto");

            Assert.That(recorder.TryAutoBeginCapture(), Is.True, "idle, so it arms itself");
            Assert.That(recorder.CaptureForTests.IsCapturing, Is.True);
            Assert.That(recorder.TryAutoBeginCapture(), Is.False, "already capturing, so it does not restart");

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            Sweep(katana, ref time, ref position);
            int captured = recorder.CaptureForTests.UpdateCount;
            Assert.That(captured, Is.GreaterThan(0));

            recorder.EndCapture();
            Assert.That(
                recorder.TryAutoBeginCapture(), Is.False,
                "rows are waiting to be saved, so it must not start and reset over them");
            Assert.That(recorder.CaptureForTests.UpdateCount, Is.EqualTo(captured));

            Assert.That(recorder.SaveCapture(), Is.True, recorder.LastSaveMessage);
            savedDirectories.Add(DirectoryFrom(recorder.LastSaveMessage));

            string savedMessage = recorder.LastSaveMessage;
            Assert.That(recorder.TryAutoBeginCapture(), Is.True, "saved, so the next run arms itself");
            Assert.That(recorder.CaptureForTests.UpdateCount, Is.EqualTo(0), "the new capture starts empty");
            Assert.That(recorder.LastSaveMessage, Is.EqualTo(savedMessage), "auto-start preserves the save outcome");

            recorder.AutoCaptureWhenIdle = false;
            recorder.EndCapture();
            Assert.That(recorder.TryAutoBeginCapture(), Is.False, "turned off, it stays off");
        }

        [Test]
        public void ARefusedCandidate_StillReportsItsActualRawSpan()
        {
            var store = new SandboxSlashWaveStore();
            Assert.That(store.TryLatch(0, new Plane(Vector3.forward, 0), Vector3.zero,
                Vector3.up, Vector3.right, Vector3.up, 1, 0.25f), Is.True);
            store.Advance(0.01, 1, true, Vector3.down, Vector3.right);
            Assert.That(store.TryGetWaveCandidate(0, out _, out bool evaluated, out _, out _, out _,
                out float r, out float q, out float denominator, out bool finite,
                out bool usable, out bool widened), Is.True);
            Assert.That(evaluated && finite, Is.True);
            Assert.That(r, Is.EqualTo(-1f).Within(1e-6f));
            Assert.That(q, Is.GreaterThan(0f));
            Assert.That(denominator, Is.EqualTo(-1f));
            Assert.That(usable || widened, Is.False);
            store.TryGetWave(0, out _, out _, out _, out _, out _, out float accepted,
                out _, out _, out _, out _);
            Assert.That(accepted, Is.EqualTo(1f));
        }

        [Test]
        public void GrowingCapture_PreservesInputBeyondItsInitialAllocation()
        {
            var katana = NewKatana();
            var recorder = NewRecorder(katana);
            recorder.CaptureForTests.Root = TestRoot("growth");
            recorder.BeginCapture();
            for (int i = 0; i < 5000; i++)
                katana.TryRecordSample(Tracked(i + 1, i * SampleInterval, Vector3.up));
            Assert.That(recorder.CaptureForTests.UpdateCount, Is.EqualTo(5000));
            Assert.That(recorder.CaptureForTests.IsCapturing, Is.True);
            recorder.CaptureRunId = "named-after-capture";
            recorder.CaptureNote = "observed after removing the headset";
            Assert.That(recorder.SaveCapture(), Is.True, recorder.LastSaveMessage);
            string directory = DirectoryFrom(recorder.LastSaveMessage);
            savedDirectories.Add(directory);
            Assert.That(Path.GetFileName(directory), Does.Contain("named-after-capture"));
            var rows = File.ReadAllLines(Path.Combine(directory, "input.csv"));
            Assert.That(rows.Length, Is.EqualTo(5001));
            Assert.That(rows[1], Does.StartWith("0,1,"));
            Assert.That(rows[5000], Does.StartWith("4999,5000,"));
            Assert.That(File.ReadAllText(Path.Combine(directory, "conditions.txt")),
                Does.Contain("observed after removing the headset"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResetOrDisable_EndsCaptureBeforeLosingCalculationState(bool disable)
        {
            var katana = NewKatana();
            var recorder = NewRecorder(katana);
            Assert.That(recorder.BeginCapture(), Is.True);
            katana.TryRecordSample(Tracked(1, 0, Vector3.up));
            int count = recorder.CaptureForTests.UpdateCount;
            if (disable)
            {
                katana.enabled = false;
                // EditMode does not dispatch this Play Mode lifecycle callback.
                // Exercise its body explicitly, not an engine lifecycle claim.
                InvokeDisableBody(katana);
            }
            else katana.LiveInputEnabled = false;
            Assert.That(recorder.CaptureForTests.IsCapturing, Is.False);
            Assert.That(recorder.CaptureForTests.HasUnsavedRows, Is.True);
            Assert.That(katana.Capture, Is.Null);
            katana.TryRecordSample(Tracked(2, 0.02, Vector3.up));
            Assert.That(recorder.CaptureForTests.UpdateCount, Is.EqualTo(count));
            Assert.That(recorder.TryAutoBeginCapture(), Is.False);
        }

        [Test]
        public void DisablingRecorder_StopsCaptureAndKeepsUnsavedRows()
        {
            var katana = NewKatana();
            var recorder = NewRecorder(katana);
            recorder.BeginCapture();
            katana.TryRecordSample(Tracked(1, 0, Vector3.up));
            recorder.enabled = false;
            InvokeDisableBody(recorder); // Explicit lifecycle-body check in EditMode.
            Assert.That(katana.Capture, Is.Null);
            Assert.That(recorder.CaptureForTests.IsCapturing, Is.False);
            Assert.That(recorder.CaptureForTests.HasUnsavedRows, Is.True);
        }

        private static void InvokeDisableBody(MonoBehaviour target)
        {
            var callback = target.GetType().GetMethod("OnDisable",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(callback, Is.Not.Null);
            callback.Invoke(target, null);
        }

        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Feeds a saved run's input into a fresh katana, under the run's own
        /// saved conditions, and requires every wave observation to match.
        /// </summary>
        private void AssertRecomputes(string directory)
        {
            Assert.That(
                SandboxSlashCapture.TryReadConditions(
                    Path.Combine(directory, "conditions.txt"), out SandboxSlashCapture.Conditions saved,
                    out string readFailure),
                Is.True, readFailure);

            SandboxRightHandKatana replayed = NewKatana();
            Assert.That(
                replayed.TryApplyCaptureConditions(saved, out string mismatch), Is.True,
                "the saved conditions could not be applied: " + mismatch);

            string[] input = File.ReadAllLines(Path.Combine(directory, "input.csv"));
            string[] recorded = File.ReadAllLines(Path.Combine(directory, "current.csv"));

            var seen = new List<string>();
            for (int i = 1; i < input.Length; i++)
            {
                string[] f = input[i].Split(',');
                var sample = new BladePoseSample(
                    long.Parse(f[1], CultureInfo.InvariantCulture),
                    double.Parse(f[2], CultureInfo.InvariantCulture),
                    new Vector3(Parse(f[3]), Parse(f[4]), Parse(f[5])),
                    new Quaternion(Parse(f[6]), Parse(f[7]), Parse(f[8]), Parse(f[9])),
                    (BladeTrackingState)int.Parse(f[10], CultureInfo.InvariantCulture));
                Vector3 view = new Vector3(Parse(f[13]), Parse(f[14]), Parse(f[15]));

                Assert.That(
                    replayed.TryRecordSample(sample, view), Is.EqualTo(f[16] == "1"),
                    "update " + f[0] + ": the saved input has to be accepted or refused as it was");

                for (int w = 0; w < replayed.WaveCount; w++)
                {
                    seen.Add(KeyFromKatana(f[0], w, replayed));
                }
            }

            var expected = new List<string>();
            for (int i = 1; i < recorded.Length; i++)
            {
                expected.Add(KeyFromRow(recorded[i].Split(',')));
            }

            Assert.That(expected, Is.Not.Empty, "there is something to check against");
            Assert.That(seen, Is.EqualTo(expected), "the recomputation matches what was recorded");
        }

        /// <summary>
        /// The columns compared: every column of <c>current.csv</c> other than
        /// the update time, which is the input row's own, and the candidate
        /// guide's origin and direction.
        /// <para>
        /// The guide itself is therefore NOT compared directly. What is
        /// compared is <c>rawSpan</c>, <c>q</c> and the signed denominator,
        /// which are computed from it -- and agreement there is agreement about
        /// those terms, not proof that the two guides were the same vector. Two
        /// different guides could in principle give the same terms.
        /// </para>
        /// </summary>
        private static string Key(
            string update, int wave, double latchedAt, Plane plane, Vector3 origin, Vector3 travel, Vector3 span,
            float acceptedSpan, Vector3 previousStart, Vector3 previousEnd, Vector3 currentStart, Vector3 currentEnd,
            bool spanClosed, double spanClosedAt, Vector3 frozenOrigin, Vector3 frozenDirection,
            bool candidateEvaluated, bool candidateFromFrozenGuide, float rawSpan, float q, float denominator,
            bool termsFinite, bool usable, bool widened)
        {
            return string.Join(
                "|",
                update,
                wave.ToString(CultureInfo.InvariantCulture),
                Round(latchedAt),
                Round(plane.normal),
                Round(plane.distance),
                Round(origin),
                Round(travel),
                Round(span),
                Round(acceptedSpan),
                Round(previousStart),
                Round(previousEnd),
                Round(currentStart),
                Round(currentEnd),
                spanClosed ? "closed" : "open",
                Round(spanClosedAt),
                Round(frozenOrigin),
                Round(frozenDirection),
                candidateEvaluated ? "1" : "0",
                candidateFromFrozenGuide ? "1" : "0",
                Fine(rawSpan),
                Fine(q),
                Fine(denominator),
                termsFinite ? "1" : "0",
                usable ? "1" : "0",
                widened ? "1" : "0");
        }

        private static string KeyFromKatana(string update, int wave, SandboxRightHandKatana katana)
        {
            Assert.That(
                katana.TryGetWave(wave, out double latchedAt, out Plane plane, out Vector3 origin,
                    out Vector3 travel, out Vector3 span, out float acceptedSpan, out Vector3 previousStart,
                    out Vector3 previousEnd, out Vector3 currentStart, out Vector3 currentEnd),
                Is.True);
            bool closed = katana.TryGetWaveSpanClose(
                wave, out double spanClosedAt, out Vector3 frozenOrigin, out Vector3 frozenDirection);
            bool hasCandidate = katana.TryGetWaveCandidate(
                wave, out _, out bool evaluated, out bool fromFrozen, out _, out _, out float rawSpan, out float q,
                out float denominator, out bool termsFinite, out bool usable, out bool widened);

            return Key(
                update, wave, latchedAt, plane, origin, travel, span, acceptedSpan, previousStart, previousEnd,
                currentStart, currentEnd, closed, closed ? spanClosedAt : double.NaN,
                closed ? frozenOrigin : Vector3.zero, closed ? frozenDirection : Vector3.zero,
                hasCandidate && evaluated, hasCandidate && fromFrozen, hasCandidate ? rawSpan : 0f,
                hasCandidate ? q : 0f, hasCandidate ? denominator : 0f, hasCandidate && termsFinite,
                hasCandidate && usable, hasCandidate && widened);
        }

        private static string KeyFromRow(string[] f)
        {
            var plane = new Plane(new Vector3(Parse(f[4]), Parse(f[5]), Parse(f[6])), 0f)
            {
                distance = Parse(f[7]),
            };
            return Key(
                f[0],
                int.Parse(f[2], CultureInfo.InvariantCulture),
                double.Parse(f[3], CultureInfo.InvariantCulture),
                plane,
                new Vector3(Parse(f[8]), Parse(f[9]), Parse(f[10])),
                new Vector3(Parse(f[11]), Parse(f[12]), Parse(f[13])),
                new Vector3(Parse(f[14]), Parse(f[15]), Parse(f[16])),
                Parse(f[17]),
                new Vector3(Parse(f[18]), Parse(f[19]), Parse(f[20])),
                new Vector3(Parse(f[21]), Parse(f[22]), Parse(f[23])),
                new Vector3(Parse(f[24]), Parse(f[25]), Parse(f[26])),
                new Vector3(Parse(f[27]), Parse(f[28]), Parse(f[29])),
                f[30] == "1",
                double.Parse(f[31], CultureInfo.InvariantCulture),
                new Vector3(Parse(f[32]), Parse(f[33]), Parse(f[34])),
                new Vector3(Parse(f[35]), Parse(f[36]), Parse(f[37])),
                f[38] == "1",
                f[39] == "1",
                Parse(f[46]),
                Parse(f[47]),
                Parse(f[48]),
                f[49] == "1",
                f[50] == "1",
                f[51] == "1");
        }

        private SandboxSlashCapture CaptureOneSweep(string root, out int updates, out SandboxRightHandKatana katana)
        {
            katana = NewKatana();
            return CaptureInto(katana, root, out updates);
        }

        private SandboxSlashCapture CaptureInto(SandboxRightHandKatana katana, string root, out int updates)
        {
            var capture = new SandboxSlashCapture { Root = root };
            capture.Begin("known", katana.CaptureConditions);
            capture.Note = "a short known input, no device involved";
            katana.Capture = capture;

            double time = 0.0;
            Vector3 position = new Vector3(0f, 1.4f, 0.3f);
            Sweep(katana, ref time, ref position);

            capture.Stop();
            katana.Capture = null;
            updates = capture.UpdateCount;
            Assert.That(updates, Is.GreaterThan(0), "something was captured");
            return capture;
        }

        // Six accepted steps, one untracked sample, six more, then long enough
        // for the span to close: the capture holds refused input, two strokes
        // and a frozen guide.
        private static void Sweep(SandboxRightHandKatana katana, ref double time, ref Vector3 position)
        {
            long frame = 1;
            for (int i = 0; i < 6; i++)
            {
                katana.TryRecordSample(Tracked(frame++, time, position));
                time += SampleInterval;
                position += Step;
            }

            katana.TryRecordSample(
                new BladePoseSample(frame++, time, Vector3.zero, Quaternion.identity, BladeTrackingState.None));
            time += SampleInterval;
            position = new Vector3(0f, 1.4f, 0.3f);

            for (int i = 0; i < 6; i++)
            {
                katana.TryRecordSample(Tracked(frame++, time, position));
                time += SampleInterval;
                position += Step;
            }

            for (int i = 0; i < 40; i++)
            {
                katana.TryRecordSample(Tracked(frame++, time, position));
                time += SampleInterval;
            }
        }

        private SandboxRightHandKatana NewKatana()
        {
            var rig = new GameObject("Slash Capture Rig");
            var blade = new GameObject("Katana");
            made.Add(rig);
            made.Add(blade);
            SandboxRightHandKatana katana = rig.AddComponent<SandboxRightHandKatana>();
            katana.Katana = blade.transform;
            return katana;
        }

        private SandboxSlashPoseRecorder NewRecorder(SandboxRightHandKatana katana)
        {
            var host = new GameObject("Slash Capture Recorder");
            made.Add(host);
            SandboxSlashPoseRecorder recorder = host.AddComponent<SandboxSlashPoseRecorder>();
            var serialized = new SerializedObject(recorder);
            SerializedProperty property = serialized.FindProperty("katana");
            Assert.That(property, Is.Not.Null, "the recorder's katana field is gone");
            property.objectReferenceValue = katana;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return recorder;
        }

        private static string DirectoryFrom(string saveMessage)
        {
            const string Marker = " to ";
            int at = saveMessage.IndexOf(Marker, StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThan(0), "the save message names the directory: " + saveMessage);
            return saveMessage.Substring(at + Marker.Length);
        }

        private static BladePoseSample Tracked(long frameId, double time, Vector3 position)
        {
            return new BladePoseSample(
                frameId, time, position, Quaternion.identity,
                BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static string TestRoot(string name)
        {
            return Path.Combine(
                Path.GetTempPath(), "zantetsu-slash-capture-tests", name + "-" + Guid.NewGuid().ToString("N"));
        }

        private static float Parse(string text)
        {
            return float.Parse(text, CultureInfo.InvariantCulture);
        }

        // Compared at a tolerance rather than bit for bit: the file holds
        // round-tripping decimals, but a recomputation still goes through the
        // same float arithmetic, and a difference below these is not one the
        // comparison would act on.
        private static string Round(float value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string Round(double value)
        {
            return value.ToString("F4", CultureInfo.InvariantCulture);
        }

        private static string Round(Vector3 value)
        {
            return Round(value.x) + ";" + Round(value.y) + ";" + Round(value.z);
        }

        // The intersection terms need more digits: the near-parallel threshold
        // on the denominator is 1e-3, so four decimals would flatten the very
        // values the comparison turns on.
        private static string Fine(float value)
        {
            return value.ToString("F7", CultureInfo.InvariantCulture);
        }
    }
}
