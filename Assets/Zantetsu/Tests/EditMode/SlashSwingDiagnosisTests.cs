using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The swing readout (<see cref="SlashSwingDiagnosis"/>) through the katana: a swing of the blade, and what its
    /// record says of why it latched or did not -- the sweep that latches, the slow one the gate turns away, the short
    /// one that never reaches the latch chord, the one tracking lost in the middle, the one after a slow return (a stroke
    /// of its own since D-224). One line per ended swing; the menu's text lists the newest first.
    /// </summary>
    public sealed class SlashSwingDiagnosisTests
    {
        private const double Interval = 0.011;

        // 0.12 m per 11 ms along the edge: about 10.9 m/s, past the 3.5 m/s floor; the chord over three steps is 0.36 m.
        private static readonly Vector3 Step = new Vector3(0f, -0.12f, 0f);

        // 0.02 m per 11 ms: 1.8 m/s -- a swing to the readout (over its 1.0 m/s floor), under the gate's 3.5 m/s.
        private static readonly Vector3 SlowStep = new Vector3(0f, -0.02f, 0f);

        private readonly List<GameObject> _made = new List<GameObject>();
        private long _frame;
        private double _time;
        private Vector3 _position;

        [SetUp]
        public void SetUp()
        {
            _frame = 0;
            _time = 10.0;
            _position = new Vector3(0f, 1.4f, 0.3f);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _made.Count - 1; i >= 0; i--)
            {
                if (_made[i] != null)
                {
                    Object.DestroyImmediate(_made[i]);
                }
            }

            _made.Clear();
        }

        [Test]
        public void ALatchingSweep_IsOneSwing_ReadAsLatched_OnceTheBladeIsStill()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int i = 0; i < 8; i++) Feed(katana, Step);
            Assert.That(katana.WaveCount, Is.EqualTo(1), "the layout: the sweep latched");
            Assert.That(katana.Swings.Swinging, Is.True);
            Assert.That(katana.Swings.Count, Is.Zero, "not ended while the blade moves");

            Quiet(katana);
            Assert.That(katana.Swings.Swinging, Is.False);
            Assert.That(katana.Swings.Count, Is.EqualTo(1));
            SlashSwingDiagnosis.Swing s = katana.Swings.At(0);
            Assert.That(s.Number, Is.EqualTo(1));
            Assert.That(s.Latched, Is.EqualTo(1));
            Assert.That(s.LastSlashId, Is.EqualTo(katana.SlashIdAt(0)));
            Assert.That(s.Chord, Is.GreaterThanOrEqualTo(0.35f));
            Assert.That(s.PeakSpeed, Is.GreaterThan(3.5f));
            Assert.That(s.Untracked, Is.Zero);
            Assert.That(SlashSwingDiagnosis.Verdict(in s), Does.StartWith("latched (slash " + s.LastSlashId + ")"));
            Assert.That(katana.Swings.Describe(4), Does.StartWith("1: latched"));
        }

        [Test]
        public void ASlowSweep_IsASwing_ReadAsUnderTheGatesSpeed()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int i = 0; i < 12; i++) Feed(katana, SlowStep);
            Quiet(katana);
            Assert.That(katana.WaveCount, Is.Zero);
            Assert.That(katana.Swings.Count, Is.EqualTo(1));
            SlashSwingDiagnosis.Swing s = katana.Swings.At(0);
            Assert.That(s.Accepted, Is.Zero);
            Assert.That(s.GateReasons[(int)BladeEdgeGateReason.SpeedBelowMinimum], Is.GreaterThan(0));
            Assert.That(s.PeakSpeed, Is.InRange(1.5f, 2.1f));
            Assert.That(SlashSwingDiagnosis.Verdict(in s), Does.StartWith("no latch: speed under the gate (peak 1.8 m/s)"));
        }

        [Test]
        public void AShortFastSweep_IsReadAsAStrokeShortOfTheLatchChord()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int i = 0; i < 5; i++) Feed(katana, Step);
            Assert.That(katana.AcceptedSampleCount, Is.EqualTo(2), "the layout: a stroke under way, short of the chord");
            Quiet(katana);
            Assert.That(katana.WaveCount, Is.Zero);
            SlashSwingDiagnosis.Swing s = katana.Swings.At(0);
            Assert.That(s.Accepted, Is.GreaterThanOrEqualTo(2));
            Assert.That(s.Chord, Is.EqualTo(0.12f).Within(0.005f));
            Assert.That(s.SpentCarried, Is.Zero);
            Assert.That(SlashSwingDiagnosis.Verdict(in s), Does.StartWith("no latch: stroke short (chord 0.12 m"));
        }

        [Test]
        public void TrackingLostInTheMiddle_IsReadAsTheStrokeReset()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int i = 0; i < 5; i++) Feed(katana, Step);
            Feed(katana, Step, tracked: false);
            Assert.That(katana.AcceptedSampleCount, Is.Zero, "the layout: the unusable pose reset the stroke");
            for (int i = 0; i < 3; i++) Feed(katana, Step);
            Quiet(katana);
            Assert.That(katana.WaveCount, Is.Zero);
            Assert.That(katana.Swings.Count, Is.EqualTo(1), "one swing: the gap was a sample long");
            SlashSwingDiagnosis.Swing s = katana.Swings.At(0);
            Assert.That(s.Untracked, Is.EqualTo(1));
            Assert.That(s.ResetPoseUnusable, Is.EqualTo(1));
            Assert.That(SlashSwingDiagnosis.Verdict(in s), Does.StartWith("no latch: tracking lost on 1 sample(s)"));
        }

        [Test]
        public void ASweepAfterASlowReturn_LatchesAgain_TheQuietHavingEndedTheStroke()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int i = 0; i < 8; i++) Feed(katana, Step);
            Assert.That(katana.WaveCount, Is.EqualTo(1), "the layout: the first swing latched");
            Quiet(katana);
            Assert.That(katana.AcceptedSampleCount, Is.Zero, "the quiet ended the latched stroke (D-224)");
            // Brought back slowly: 1.8 m/s, spine first -- a swing to the readout, turned away by the gate for its speed.
            for (int i = 0; i < 40; i++) Feed(katana, -SlowStep);
            Quiet(katana);
            // The next swing, as the first: a wave of its own (read before the quiet that ends the swing's record: the
            // first wave's 1.5 s lifetime runs out during it).
            for (int i = 0; i < 8; i++) Feed(katana, Step);
            Assert.That(katana.WaveCount, Is.EqualTo(2), "the second fast swing gave a wave of its own");
            Quiet(katana);
            Assert.That(katana.Swings.Count, Is.EqualTo(3));
            SlashSwingDiagnosis.Swing first = katana.Swings.At(2), back = katana.Swings.At(1), second = katana.Swings.At(0);
            Assert.That(first.Latched, Is.EqualTo(1));
            Assert.That(first.ResetQuiet, Is.EqualTo(1), "the quiet after the first swing ended its stroke, inside that swing's record");
            Assert.That(SlashSwingDiagnosis.Verdict(in back), Does.StartWith("no latch: speed under the gate"), "the slow return");
            Assert.That(back.SpentCarried, Is.Zero);
            Assert.That(second.Number, Is.EqualTo(3));
            Assert.That(second.Latched, Is.EqualTo(1));
            Assert.That(second.SpentCarried, Is.Zero);
            Assert.That(SlashSwingDiagnosis.Verdict(in second), Does.StartWith("latched (slash "));
            Assert.That(SlashSwingDiagnosis.Line(in first), Does.Contain(", quiet 1"));
        }

        [Test]
        public void EachEndedSwing_IsOneLine_AndTheReadoutListsTheNewestFirst()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int i = 0; i < 8; i++) Feed(katana, Step);
            Quiet(katana);
            for (int i = 0; i < 12; i++) Feed(katana, SlowStep);
            Quiet(katana);
            Assert.That(katana.Swings.Count, Is.EqualTo(2));
            Assert.That(katana.Swings.Ended, Is.EqualTo(2));
            string[] lines = katana.Swings.Describe(4).Split('\n');
            Assert.That(lines.Length, Is.EqualTo(2));
            Assert.That(lines[0], Does.StartWith("2: no latch"));
            Assert.That(lines[1], Does.StartWith("1: latched"));
            Assert.That(katana.Swings.Describe(1).Split('\n').Length, Is.EqualTo(1), "as many lines as asked");
            string line = SlashSwingDiagnosis.Line(katana.Swings.At(1));
            Assert.That(line, Does.StartWith("swing 1 at 10.").And.Contain("gate: accepted ").And.Contain("latch: latched 1").And.Contain("-> latched (slash "));
        }

        [Test]
        public void TheKept_HoldTheLastEight_NewestFirst()
        {
            SandboxRightHandKatana katana = NewKatana();
            for (int n = 0; n < SlashSwingDiagnosis.Kept + 2; n++)
            {
                for (int i = 0; i < 6; i++) Feed(katana, SlowStep);
                Quiet(katana);
            }

            Assert.That(katana.Swings.Ended, Is.EqualTo(SlashSwingDiagnosis.Kept + 2));
            Assert.That(katana.Swings.Count, Is.EqualTo(SlashSwingDiagnosis.Kept));
            Assert.That(katana.Swings.At(0).Number, Is.EqualTo(SlashSwingDiagnosis.Kept + 2));
            Assert.That(katana.Swings.At(SlashSwingDiagnosis.Kept - 1).Number, Is.EqualTo(3));
            Assert.That(katana.Swings.At(SlashSwingDiagnosis.Kept).Number, Is.Zero, "outside the kept: nothing");
        }

        private void Feed(SandboxRightHandKatana katana, Vector3 step, bool tracked = true)
        {
            _frame++;
            _time += Interval;
            _position += step;
            katana.TryRecordSample(new BladePoseSample(
                _frame, _time, _position, Quaternion.identity,
                tracked ? BladeTrackingState.Position | BladeTrackingState.Rotation : BladeTrackingState.None));
        }

        /// <summary>The blade still for 330 ms: longer than the readout's gap, so the swing under way ends.</summary>
        private void Quiet(SandboxRightHandKatana katana, int samples = 30)
        {
            for (int i = 0; i < samples; i++) Feed(katana, Vector3.zero);
        }

        private SandboxRightHandKatana NewKatana()
        {
            var rig = new GameObject("Swing Diagnosis Rig");
            var blade = new GameObject("Katana");
            _made.Add(rig);
            _made.Add(blade);
            SandboxRightHandKatana katana = rig.AddComponent<SandboxRightHandKatana>();
            katana.Katana = blade.transform;
            return katana;
        }
    }
}
