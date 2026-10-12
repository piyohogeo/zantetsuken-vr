using NUnit.Framework;
using Zantetsu.Core;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The game's pause and its clocks (DESIGN 4.6), on given time: the two causes kept apart, the paused time left
    /// out of the clocks, and a system pause's end taken at the first frame after it.
    /// </summary>
    public sealed class GamePauseTests
    {
        private double _unscaled, _realtime;
        private int _frame;

        private GamePauseState New()
        {
            _unscaled = 10.0;
            _realtime = 100.0;
            _frame = 1;
            return new GamePauseState(() => _unscaled, () => _realtime, () => _frame);
        }

        private void Frame(double seconds)
        {
            _frame++;
            _unscaled += seconds;
            _realtime += seconds;
        }

        [Test]
        public void NeverPaused_TheClocksAreTheGivenOnes()
        {
            GamePauseState state = New();
            Frame(0.5);
            Assert.That(state.IsPaused, Is.False);
            Assert.That(state.EverPaused, Is.False);
            Assert.That(state.UnscaledTime, Is.EqualTo(10.5));
            Assert.That(state.Realtime, Is.EqualTo(100.5));
            Assert.That(state.UnscaledDelta(0.5f), Is.EqualTo(0.5f));
            Assert.That(state.PausedSeconds, Is.Zero);
        }

        [Test]
        public void AManualPause_StandsTheClocksStill_AndTheyGoOnFromWhereTheyStood()
        {
            GamePauseState state = New();
            int paused = 0, resumed = 0;
            state.PausedChanged += p => { if (p) paused++; else resumed++; };
            Frame(1.0);
            state.SetManual(true);
            Assert.That(state.IsPaused && state.Manual && !state.System, Is.True);
            double stoodUnscaled = state.UnscaledTime, stoodRealtime = state.Realtime;
            Assert.That(stoodUnscaled, Is.EqualTo(11.0));
            for (int i = 0; i < 300; i++)
            {
                Frame(1.0 / 90.0);
                Assert.That(state.UnscaledTime, Is.EqualTo(stoodUnscaled), "the clock stands in every paused frame");
                Assert.That(state.UnscaledDelta(1f / 90f), Is.Zero);
            }

            // The real time runs on within a frame too; the paused clock does not.
            _realtime += 0.004;
            Assert.That(state.Realtime, Is.EqualTo(stoodRealtime));
            Assert.That(state.PausedSeconds, Is.EqualTo((300.0 / 90.0) + 0.004).Within(1e-9));

            state.SetManual(false);
            Assert.That(state.IsPaused, Is.False);
            Assert.That(state.UnscaledTime, Is.EqualTo(stoodUnscaled).Within(1e-9), "no jump at the resume");
            Assert.That(state.Realtime, Is.EqualTo(stoodRealtime).Within(1e-9));
            Assert.That(state.UnscaledDelta(1f / 90f), Is.EqualTo(1f / 90f), "the frames went on during the pause: the resume's delta is a frame's");
            Frame(0.25);
            Assert.That(state.UnscaledTime, Is.EqualTo(stoodUnscaled + 0.25).Within(1e-9));
            Assert.That(state.Realtime, Is.EqualTo(stoodRealtime + 0.25).Within(1e-9));
            Assert.That(new[] { paused, resumed, state.ManualPauses, state.SystemPauses }, Is.EqualTo(new[] { 1, 1, 1, 0 }));
        }

        [Test]
        public void TheFrameCount_StandsWhilePaused_AndGoesOnByOneAFrameAfter()
        {
            GamePauseState state = New();
            Assert.That(state.Frame, Is.EqualTo(1));
            Frame(0.01);
            Frame(0.01);
            Assert.That(state.Frame, Is.EqualTo(3), "never paused: the frame's own number");

            // A wait of 600 frames begun here is not used up by a pause of 5000.
            int until = state.Frame + 600;
            state.SetManual(true);
            for (int i = 0; i < 5000; i++)
            {
                Frame(0.01);
                Assert.That(state.Frame, Is.EqualTo(3), "paused, frame " + i);
            }

            Assert.That(state.Frame, Is.LessThan(until));
            state.SetManual(false);
            Assert.That(state.Frame, Is.EqualTo(3), "the frame the pause ended in is the frame it began in");
            for (int i = 1; i <= 599; i++)
            {
                Frame(0.01);
                Assert.That(state.Frame, Is.EqualTo(3 + i));
            }

            Assert.That(state.Frame, Is.LessThan(until), "599 frames of the bound are used");
            Frame(0.01);
            Assert.That(state.Frame, Is.EqualTo(until), "and the 600th ends it");

            // A system pause: the frame that takes its end is the absence's, and counts as none.
            state.SetSystem(true);
            state.SetSystem(false);
            _frame++;
            _unscaled += 30.0;
            _realtime += 30.0;
            Assert.That(state.IsPaused, Is.False);
            Assert.That(state.Frame, Is.EqualTo(until));
            Frame(0.01);
            Assert.That(state.Frame, Is.EqualTo(until + 1));
        }

        [Test]
        public void SettingTheSameValueAgain_ChangesNothing()
        {
            GamePauseState state = New();
            int events = 0, manualEvents = 0;
            state.PausedChanged += _ => events++;
            state.ManualChanged += _ => manualEvents++;
            state.SetManual(false);
            state.SetManual(true);
            state.SetManual(true);
            Frame(1.0);
            state.SetManual(true);
            Assert.That(state.ManualPauses, Is.EqualTo(1));
            state.SetManual(false);
            state.SetManual(false);
            Assert.That(new[] { events, manualEvents }, Is.EqualTo(new[] { 2, 2 }));
        }

        [Test]
        public void ASystemPausesEnd_IsTakenAtTheFirstFrameAfterIt_WhoseDeltaIsNoProgress()
        {
            GamePauseState state = New();
            Frame(1.0);
            state.SetSystem(true);
            double stood = state.UnscaledTime;
            Assert.That(state.System && state.IsPaused && !state.Manual, Is.True);

            // The application says it is back, in the frame that was current when it left: the frame time is still that
            // frame's, so nothing ends yet.
            _realtime += 40.0;
            state.SetSystem(false);
            Assert.That(state.IsPaused, Is.True, "still the old frame");
            Assert.That(state.UnscaledTime, Is.EqualTo(stood));

            // The first frame after: its delta is the whole absence.
            _frame++;
            _unscaled += 40.0;
            Assert.That(state.IsPaused, Is.False);
            Assert.That(state.UnscaledTime, Is.EqualTo(stood).Within(1e-9), "the absence is no part of the clock");
            Assert.That(state.UnscaledDelta(40f), Is.Zero, "nor of this frame's progress");
            Assert.That(state.PausedSeconds, Is.EqualTo(40.0).Within(1e-9));
            Frame(0.011);
            Assert.That(state.UnscaledDelta(0.011f), Is.EqualTo(0.011f));
            Assert.That(state.UnscaledTime, Is.EqualTo(stood + 0.011).Within(1e-9));
            Assert.That(state.SystemPauses, Is.EqualTo(1));
        }

        [Test]
        public void TheHeadsetComingBack_DoesNotEndAManualPause()
        {
            GamePauseState state = New();
            int resumed = 0;
            state.PausedChanged += p => { if (!p) resumed++; };
            Frame(1.0);
            state.SetManual(true);
            double stood = state.UnscaledTime;
            Frame(0.5);
            state.SetSystem(true);
            _realtime += 60.0;
            state.SetSystem(false);
            _frame++;
            _unscaled += 60.0;
            Assert.That(state.System, Is.False, "the system pause ended");
            Assert.That(state.Manual && state.IsPaused, Is.True, "the manual pause did not");
            Assert.That(resumed, Is.Zero);
            Assert.That(state.UnscaledTime, Is.EqualTo(stood));
            Frame(2.0);
            state.SetManual(false);
            Assert.That(state.IsPaused, Is.False);
            Assert.That(state.UnscaledTime, Is.EqualTo(stood).Within(1e-9));
            Assert.That(resumed, Is.EqualTo(1));
        }

        [Test]
        public void AManualPausesEnd_DoesNotEndASystemPause()
        {
            GamePauseState state = New();
            Frame(1.0);
            state.SetSystem(true);
            state.SetManual(true);
            state.SetManual(false);
            Assert.That(state.IsPaused && state.System, Is.True);
        }

        [Test]
        public void ASystemPauseAgainBeforeItsEndWasTaken_NeverEnded()
        {
            GamePauseState state = New();
            int events = 0;
            state.PausedChanged += _ => events++;
            Frame(1.0);
            state.SetSystem(true);
            double stood = state.UnscaledTime;
            state.SetSystem(false);
            state.SetSystem(true);
            _frame++;
            _unscaled += 5.0;
            _realtime += 5.0;
            Assert.That(state.IsPaused, Is.True);
            Assert.That(state.UnscaledTime, Is.EqualTo(stood));
            Assert.That(new[] { events, state.SystemPauses }, Is.EqualTo(new[] { 1, 1 }));
        }

        [Test]
        public void TheStaticPause_HoldsTheTimeScaleAtZero_AndPutsItBack()
        {
            float before = UnityEngine.Time.timeScale;
            try
            {
                GamePause.ResetForTest();
                UnityEngine.Time.timeScale = 0.75f;
                Assert.That(GamePause.EverPaused, Is.False);
                Assert.That(GamePause.Describe(), Is.EqualTo("never paused"));
                int manual = 0;
                void Count(bool _) => manual++;
                GamePause.ManualChanged += Count;
                try
                {
                    GamePause.SetManual(true);
                    Assert.That(GamePause.IsPaused && GamePause.Manual, Is.True);
                    Assert.That(UnityEngine.Time.timeScale, Is.Zero);
                    Assert.That(GamePause.UnscaledDeltaTime, Is.Zero);
                    GamePause.SetManual(false);
                    Assert.That(UnityEngine.Time.timeScale, Is.EqualTo(0.75f), "the value it had before the pause");
                    Assert.That(manual, Is.EqualTo(2));
                    Assert.That(GamePause.Describe(), Does.StartWith("paused 1 time(s) (manual 1, system 0)").And.Contain("NOT a performance sample"));
                }
                finally
                {
                    GamePause.ManualChanged -= Count;
                }
            }
            finally
            {
                GamePause.ResetForTest();
                UnityEngine.Time.timeScale = before;
            }
        }
    }
}
