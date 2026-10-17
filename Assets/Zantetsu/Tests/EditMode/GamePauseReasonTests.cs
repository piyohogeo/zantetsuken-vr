using System.Collections.Generic;
using NUnit.Framework;
using Zantetsu.Core;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The pause's reasons (DESIGN 4.6, D-241): manual, application and XR focus held apart; the game paused while
    /// any holds and resumed only when none does, whatever the order of their ends, however often a notification
    /// repeats, and never for a frame between an end and a new interruption. And the focus decision itself.
    /// </summary>
    public sealed class GamePauseReasonTests
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

        private void Frame(double seconds = 0.011)
        {
            _frame++;
            _unscaled += seconds;
            _realtime += seconds;
        }

        [Test]
        public void TheReasons_AreHeldApart_AndTheGameResumes_OnlyWhenTheLastOneEnds_InAnyOrder()
        {
            GamePauseState s = New();
            var changes = new List<bool>();
            var system = new List<bool>();
            s.PausedChanged += changes.Add;
            s.SystemChanged += system.Add;

            s.SetFocusLost(true);
            Assert.That(s.Reasons, Is.EqualTo(GamePauseReason.Focus));
            Assert.That(s.IsPaused && s.System && !s.Manual, Is.True);
            s.SetSystem(true);
            Assert.That(s.Reasons, Is.EqualTo(GamePauseReason.Focus | GamePauseReason.Application));
            s.SetManual(true);
            Assert.That(s.Reasons, Is.EqualTo(GamePauseReason.Focus | GamePauseReason.Application | GamePauseReason.Manual));
            Assert.That(changes, Is.EqualTo(new[] { true }), "paused once, at the first reason");
            Assert.That(system, Is.EqualTo(new[] { true }));
            Assert.That(s.SystemPauses, Is.EqualTo(1));

            // The ends in another order than the beginnings: the focus back first, then the manual, then the application.
            s.SetFocusLost(false);
            Frame();
            Assert.That(s.IsPaused, Is.True, "the application and the manual still hold");
            Assert.That(s.Reasons, Is.EqualTo(GamePauseReason.Application | GamePauseReason.Manual));
            s.SetManual(false);
            Assert.That(s.IsPaused && s.System, Is.True, "the application still holds");
            s.SetSystem(false);
            Assert.That(s.IsPaused, Is.True, "a system end is taken at the next frame");
            Frame();
            Assert.That(s.IsPaused, Is.False);
            Assert.That(s.Reasons, Is.EqualTo(GamePauseReason.None));
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
            Assert.That(system, Is.EqualTo(new[] { true, false }));
            Assert.That(s.UnscaledDelta(0.011f), Is.Zero, "the frame that ended it has no progress");
            Frame();
            Assert.That(s.UnscaledDelta(0.011f), Is.EqualTo(0.011f));
        }

        [Test]
        public void ARepeatedNotification_ChangesNothing_AndOneReasonsEnd_DoesNotEndAnother()
        {
            GamePauseState s = New();
            var changes = new List<bool>();
            s.PausedChanged += changes.Add;
            s.SetFocusLost(true);
            s.SetFocusLost(true);
            s.SetSystem(true);
            s.SetSystem(true);
            Assert.That(s.SystemPauses, Is.EqualTo(1));
            Assert.That(changes.Count, Is.EqualTo(1));
            // The application's end told twice, the focus still lost: paused on, frame after frame.
            s.SetSystem(false);
            s.SetSystem(false);
            Frame(); Frame();
            Assert.That(s.IsPaused && s.System, Is.True);
            Assert.That(s.Reasons, Is.EqualTo(GamePauseReason.Focus));
            // The focus back ends it, at the next frame.
            s.SetFocusLost(false);
            Assert.That(s.IsPaused, Is.True);
            Frame();
            Assert.That(s.IsPaused, Is.False);
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
            // A manual pause's end never ends a system reason, and a system end never ends the manual.
            s.SetManual(true);
            s.SetFocusLost(true);
            s.SetManual(false);
            Assert.That(s.IsPaused && s.System, Is.True);
            s.SetManual(true);
            s.SetFocusLost(false);
            Frame();
            Assert.That(s.IsPaused && s.Manual && !s.System, Is.True);
        }

        [Test]
        public void AnInterruptionAgain_WhileAnEndWaitsForItsFrame_NeverResumes_NotEvenForAFrame()
        {
            GamePauseState s = New();
            var changes = new List<bool>();
            s.PausedChanged += changes.Add;
            s.SetFocusLost(true);
            s.SetFocusLost(false);          // the end waits for the next frame
            s.SetSystem(true);              // another reason comes in the same frame
            Frame();
            Assert.That(s.IsPaused, Is.True, "the application holds: no resumption at the frame the focus's end would have been taken");
            Assert.That(changes, Is.EqualTo(new[] { true }));
            s.SetSystem(false);
            s.SetFocusLost(true);           // lost again before the frame that would have ended the application's
            Frame(); Frame();
            Assert.That(s.IsPaused, Is.True);
            Assert.That(changes, Is.EqualTo(new[] { true }), "never resumed");
            s.SetFocusLost(false);
            s.SetFocusLost(true);           // an end and a new loss within one frame: it never ended
            Frame();
            Assert.That(s.IsPaused, Is.True);
            Assert.That(changes.Count, Is.EqualTo(1));
            s.SetFocusLost(false);
            Frame();
            Assert.That(s.IsPaused, Is.False);
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
            Assert.That(s.PausedSeconds, Is.EqualTo(_realtime - 100.0).Within(1e-9), "one pause, the whole time");
        }

        [Test]
        public void TheFocusLostAgain_InTheFrameAfterItCameBack_BeforeThatFramesRefresh_ResumesNothing_AndHoldsOn()
        {
            GamePauseState s = New();
            var changes = new List<bool>();
            var system = new List<bool>();
            s.PausedChanged += changes.Add;
            s.SystemChanged += system.Add;
            s.SetFocusLost(true);
            s.SetFocusLost(false);          // frame N: the end waits for the next frame
            Frame();                        // frame N+1: the observer runs before the frame's refresh...
            s.SetFocusLost(true);           // ...and finds the focus lost again
            s.Refresh();                    // the frame's refresh (the listener)
            Assert.That(s.IsPaused && s.System, Is.True, "held on");
            Assert.That(changes, Is.EqualTo(new[] { true }), "no resumption event, no new pause event");
            Assert.That(system, Is.EqualTo(new[] { true }));
            Frame(); Frame();
            Assert.That(s.IsPaused, Is.True);
            s.SetFocusLost(false);
            Frame();
            s.Refresh();
            Assert.That(s.IsPaused, Is.False);
            Assert.That(changes, Is.EqualTo(new[] { true, false }));
        }

        [Test]
        public void AnotherReasonHeld_InTheFrameAfterOneReasonsEnd_ResumesNothing_AndTellsNothing_InEitherDirection()
        {
            foreach (bool applicationFirst in new[] { true, false })
            {
                GamePauseState s = New();
                var changes = new List<bool>();
                var system = new List<bool>();
                s.PausedChanged += changes.Add;
                s.SystemChanged += system.Add;
                GamePauseReason first = applicationFirst ? GamePauseReason.Application : GamePauseReason.Focus;
                GamePauseReason second = applicationFirst ? GamePauseReason.Focus : GamePauseReason.Application;
                s.SetSystemReason(first, true);
                s.SetSystemReason(first, false);      // frame N: the end waits
                Frame();                              // frame N+1, before the frame's refresh:
                s.SetSystemReason(second, true);      // the other reason comes
                s.Refresh();                          // the frame's refresh takes the first's end
                Assert.That(s.IsPaused && s.System, Is.True, "held on by the second reason (" + second + ")");
                Assert.That(s.Reasons, Is.EqualTo(second));
                Assert.That(changes, Is.EqualTo(new[] { true }), "no resumption, no new pause: " + first + " -> " + second);
                Assert.That(system, Is.EqualTo(new[] { true }));
                Assert.That(s.SystemPauses, Is.EqualTo(1), "one system pause, from one reason to the other");
                s.SetSystemReason(second, false);
                Frame();
                s.Refresh();
                Assert.That(s.IsPaused, Is.False);
                Assert.That(changes, Is.EqualTo(new[] { true, false }));
                Assert.That(system, Is.EqualTo(new[] { true, false }));
            }
        }

        [Test]
        public void TheFocusObserver_RunsBeforeThePausesOwnListener_InEveryFrame()
        {
            System.Type listener = typeof(GamePause).GetNestedType("Listener", System.Reflection.BindingFlags.NonPublic);
            Assert.That(listener, Is.Not.Null);
            var listenerOrder = (UnityEngine.DefaultExecutionOrder)System.Attribute.GetCustomAttribute(listener, typeof(UnityEngine.DefaultExecutionOrder));
            var observerOrder = (UnityEngine.DefaultExecutionOrder)System.Attribute.GetCustomAttribute(typeof(Zantetsu.Sandbox.XrFocusPause), typeof(UnityEngine.DefaultExecutionOrder));
            Assert.That(listenerOrder.order, Is.EqualTo(GamePause.ListenerExecutionOrder));
            Assert.That(observerOrder.order, Is.LessThan(listenerOrder.order), "the focus is observed before the frame's first refresh takes a waiting end");
            var menuOrder = (UnityEngine.DefaultExecutionOrder)System.Attribute.GetCustomAttribute(typeof(Zantetsu.Sandbox.PlayMenu), typeof(UnityEngine.DefaultExecutionOrder));
            Assert.That(observerOrder.order, Is.LessThan(menuOrder.order), "and before the menu reads the frame");
        }

        [Test]
        public void TheSystemChanged_IsTold_WhileTheManualPauseHoldsToo()
        {
            GamePauseState s = New();
            var system = new List<bool>();
            var changes = new List<bool>();
            s.SystemChanged += system.Add;
            s.PausedChanged += changes.Add;
            s.SetManual(true);
            s.SetFocusLost(true);
            Assert.That(system, Is.EqualTo(new[] { true }), "the interruption is told though the game was paused already");
            Assert.That(changes, Is.EqualTo(new[] { true }), "progress changed once");
            s.SetFocusLost(false);
            Frame();
            s.Refresh();   // the frame's beginning, as the listener does it
            Assert.That(system, Is.EqualTo(new[] { true, false }));
            Assert.That(changes, Is.EqualTo(new[] { true }), "still paused by the menu");
            Assert.That(s.Manual && !s.System && s.IsPaused, Is.True);
        }

        [Test]
        public void TheStaticPause_TakesTheFocusAsAReason_AndTellsTheSystemChange()
        {
            float before = UnityEngine.Time.timeScale;
            try
            {
                GamePause.ResetForTest();
                var system = new List<bool>();
                void Note(bool paused) => system.Add(paused);
                GamePause.SystemChanged += Note;
                try
                {
                    GamePause.SetFocusLost(true);
                    Assert.That(GamePause.IsPaused && GamePause.System, Is.True);
                    Assert.That(GamePause.Reasons, Is.EqualTo(GamePauseReason.Focus));
                    Assert.That(UnityEngine.Time.timeScale, Is.Zero);
                    Assert.That(system, Is.EqualTo(new[] { true }));
                }
                finally
                {
                    GamePause.SystemChanged -= Note;
                }
            }
            finally
            {
                GamePause.ResetForTest();
                UnityEngine.Time.timeScale = before;
            }
        }

        /// <summary>Runs one scenario at the static entry (the Player's), with the pause reset before and after and the time scale restored.</summary>
        private static void AtTheStaticEntry(System.Action<List<bool>, List<bool>> scenario)
        {
            float before = UnityEngine.Time.timeScale;
            try
            {
                GamePause.ResetForTest();
                var paused = new List<bool>();
                var system = new List<bool>();
                void NotePaused(bool p) => paused.Add(p);
                void NoteSystem(bool p) => system.Add(p);
                GamePause.PausedChanged += NotePaused;
                GamePause.SystemChanged += NoteSystem;
                try
                {
                    scenario(paused, system);
                }
                finally
                {
                    GamePause.PausedChanged -= NotePaused;
                    GamePause.SystemChanged -= NoteSystem;
                }
            }
            finally
            {
                GamePause.ResetForTest();
                UnityEngine.Time.timeScale = before;
            }
        }

        [Test]
        public void AtTheStaticEntry_TheFocusLostAgain_InTheFrameAfterItCameBack_ResumesNothing_AndIsOneChangedRequest()
        {
            AtTheStaticEntry((paused, system) =>
            {
                Assert.That(GamePause.SetFocusLost(true), Is.True);
                Assert.That(GamePause.SetFocusLost(false), Is.True);
                GamePause.BeginNextFrameForTest();          // the next frame began; its refresh has not run
                Assert.That(GamePause.SetFocusLost(true), Is.True, "held again: the waiting end is cancelled");
                Assert.That(GamePause.IsPaused && GamePause.System, Is.True);
                Assert.That(GamePause.Reasons, Is.EqualTo(GamePauseReason.Focus));
                Assert.That(paused, Is.EqualTo(new[] { true }), "no resumption, no second pause");
                Assert.That(system, Is.EqualTo(new[] { true }));
                Assert.That(UnityEngine.Time.timeScale, Is.Zero);
                GamePause.TakeSystemEndsForTest();          // nothing waits any more
                Assert.That(GamePause.IsPaused, Is.True);
            });
        }

        [Test]
        public void AtTheStaticEntry_AnotherReason_InTheFrameAfterOnesEnd_ResumesNothing_AndTheLastEnd_Resumes()
        {
            AtTheStaticEntry((paused, system) =>
            {
                Assert.That(GamePause.SetFocusLost(true), Is.True);
                Assert.That(GamePause.SetFocusLost(false), Is.True);
                GamePause.BeginNextFrameForTest();
                Assert.That(GamePause.SetSystem(true), Is.True);
                Assert.That(GamePause.IsPaused, Is.True);
                Assert.That(GamePause.Reasons, Is.EqualTo(GamePauseReason.Application), "the focus's end was taken, the application holds");
                Assert.That(paused, Is.EqualTo(new[] { true }));
                Assert.That(system, Is.EqualTo(new[] { true }));

                Assert.That(GamePause.SetSystem(false), Is.True);
                GamePause.TakeSystemEndsForTest();
                Assert.That(GamePause.IsPaused, Is.False);
                Assert.That(paused, Is.EqualTo(new[] { true, false }));
                Assert.That(system, Is.EqualTo(new[] { true, false }));
            });
        }

        [Test]
        public void AtTheStaticEntry_ARepeatedHoldOrRelease_AndTheReleaseOfAReasonNotHeld_ChangeNoRequest()
        {
            AtTheStaticEntry((paused, system) =>
            {
                Assert.That(GamePause.SetSystem(false), Is.False, "not held: nothing to release");
                Assert.That(GamePause.SetFocusLost(true), Is.True);
                Assert.That(GamePause.SetFocusLost(true), Is.False, "a repeated notification");
                Assert.That(GamePause.SetFocusLost(false), Is.True);
                Assert.That(GamePause.SetFocusLost(false), Is.False, "released already: its end waits");
                Assert.That(GamePause.IsPaused, Is.True, "the end is taken at the next frame");
                GamePause.TakeSystemEndsForTest();
                Assert.That(GamePause.IsPaused, Is.False);
                Assert.That(GamePause.SetFocusLost(false), Is.False);
                Assert.That(paused, Is.EqualTo(new[] { true, false }));
            });
        }

        [Test]
        public void AtTheStaticEntry_AHoldWhileTheEndWaits_InTheSameFrame_IsAChangedRequest_AndCancelsTheEnd()
        {
            AtTheStaticEntry((paused, system) =>
            {
                Assert.That(GamePause.SetFocusLost(true), Is.True);
                Assert.That(GamePause.SetFocusLost(false), Is.True);
                Assert.That(GamePause.SetFocusLost(true), Is.True, "the cancel is a request change (its line is logged)");
                GamePause.TakeSystemEndsForTest();
                Assert.That(GamePause.IsPaused, Is.True, "no end waited any more");
                Assert.That(GamePause.Reasons, Is.EqualTo(GamePauseReason.Focus));
                Assert.That(paused, Is.EqualTo(new[] { true }));
            });
        }

        [Test]
        public void TheFocusDecision_NeverBeforeXr_NeverByTheWindow_AndXrGoneInactive_IsAPause()
        {
            Assert.That(PlayFocus.PauseByFocus(xrActive: false, xrEverActive: false, openXr: false, xrSessionFocused: false), Is.False, "before XR: never (a PC without XR, the start-up)");
            Assert.That(PlayFocus.PauseByFocus(true, true, true, true), Is.False, "focused");
            Assert.That(PlayFocus.PauseByFocus(true, true, true, false), Is.True, "the session not focused: the system UI");
            Assert.That(PlayFocus.PauseByFocus(false, true, false, false), Is.True, "XR inactive after it was active: a pause, not the window's focus resuming the game");
            Assert.That(PlayFocus.PauseByFocus(true, true, false, false), Is.False, "another loader whose focus is not known: not paused by focus");
            Assert.That(PlayFocus.ForPlay(true, true, false, true), Is.False, "the window in focus under the system menu is not the player's focus (the statistics' reading, unchanged)");
        }

        [Test]
        public void ThePhysicsStep_StandsWhileAnyReasonHolds_AndAnExplicitPause_OutlivesTheOthersEnds()
        {
            Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.PauseAggregate(false, false, false), Is.False);
            Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.PauseAggregate(true, false, false), Is.True);
            Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.PauseAggregate(false, true, false), Is.True);
            Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.PauseAggregate(false, false, true), Is.True);
            float before = UnityEngine.Time.timeScale;
            try
            {
                GamePause.ResetForTest();
                Zantetsu.PhysicsCut.CutPhysicsStep.SetPaused(false);
                Zantetsu.PhysicsCut.CutPhysicsStep.FollowGamePause();
                Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.IsClockPaused, Is.False);
                GamePause.SetFocusLost(true);
                Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.IsClockPaused, Is.True, "the system pause alone pauses the step (not only the time scale)");
                Zantetsu.PhysicsCut.CutPhysicsStep.SetPaused(true);
                GamePause.SetFocusLost(false);
                GamePause.TakeSystemEndsForTest();   // the next frame takes the end: the game resumes
                Assert.That(GamePause.IsPaused, Is.False);
                Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.IsClockPaused, Is.True, "the explicit pause is not ended by the game's resumption");
                Zantetsu.PhysicsCut.CutPhysicsStep.SetPaused(false);
                Assert.That(Zantetsu.PhysicsCut.CutPhysicsStep.IsClockPaused, Is.False);
            }
            finally
            {
                Zantetsu.PhysicsCut.CutPhysicsStep.SetPaused(false);
                GamePause.ResetForTest();
                UnityEngine.Time.timeScale = before;
            }
        }
    }
}
