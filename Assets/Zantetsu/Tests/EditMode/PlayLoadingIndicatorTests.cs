using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The start-up's "Now loading..." picture (2026-10-18): shown while the cut world is missing or not ready or a
    /// preparation is pending, before the head and below the eyes, following the head; its bright dot goes round on
    /// the clock it is given; hidden and gone once the start-up is over, and never shown again for a later preparation.
    /// </summary>
    public sealed class PlayLoadingIndicatorTests
    {
        private readonly List<Object> _made = new List<Object>();

        [SetUp]
        public void SetUp() => PlayPreparation.ResetForSession();

        [TearDown]
        public void TearDown()
        {
            foreach (PlayLoadingIndicator indicator in Object.FindObjectsByType<PlayLoadingIndicator>(FindObjectsSortMode.None)) Object.DestroyImmediate(indicator.gameObject);
            foreach (Object made in _made) if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
            PlayPreparation.ResetForSession();
        }

        private PlayLoadingIndicator NewIndicator()
        {
            PlayLoadingIndicator indicator = PlayLoadingIndicator.Install();
            _made.Add(indicator.gameObject);
            Assert.That(PlayLoadingIndicator.Install(), Is.SameAs(indicator), "once");
            return indicator;
        }

        [Test]
        public void Loading_WhileTheWorldIsMissingOrNotReady_OrAPreparationIsPending()
        {
            Assert.That(PlayLoadingIndicator.Loading(false, false, 0), Is.True, "no world yet");
            Assert.That(PlayLoadingIndicator.Loading(true, false, 0), Is.True, "the world is not ready");
            Assert.That(PlayLoadingIndicator.Loading(true, true, 1), Is.True, "a preparation is pending");
            Assert.That(PlayLoadingIndicator.Loading(true, true, 0), Is.False, "ready, nothing pending: play");
        }

        [Test]
        public void ThePicture_IsShownBeforeTheHeadBelowTheEyes_FollowsTheHead_AndGoesOnceTheStartUpIsOver_ForGood()
        {
            PlayLoadingIndicator indicator = NewIndicator();
            Vector3 head = new Vector3(2f, 1.6f, 3f);
            Quaternion facing = Quaternion.Euler(20f, 90f, 0f);   // looking east, a little down: the picture is placed by the yaw alone
            indicator.Step(true, false, 0, head, facing, 0.011f, 0f);
            Assert.That(indicator.IsShown, Is.True);
            Assert.That(indicator.View.Visible, Is.True);
            Vector3 expected = head + (Vector3.right * PlayLoadingIndicator.Distance) - (Vector3.up * PlayLoadingIndicator.Below);
            Assert.That(Vector3.Distance(indicator.View.Root.position, expected), Is.LessThan(1e-3f), "1.5 m east of the head, a little below the eyes");
            Assert.That(Vector3.Dot(indicator.View.Root.forward, Vector3.right), Is.GreaterThan(0.999f), "facing away from the head, upright");

            // The head turns to the north: the picture follows, part of the way a frame, and is there within a second.
            Quaternion north = Quaternion.Euler(0f, 0f, 0f);
            indicator.Step(true, false, 0, head, north, 0.1f, 0.1f);
            Vector3 northTarget = head + (Vector3.forward * PlayLoadingIndicator.Distance) - (Vector3.up * PlayLoadingIndicator.Below);
            float afterOne = Vector3.Distance(indicator.View.Root.position, northTarget);
            Assert.That(afterOne, Is.GreaterThan(0.05f).And.LessThan(Vector3.Distance(expected, northTarget)), "on its way, not there yet");
            for (int i = 0; i < 20; i++) indicator.Step(true, false, 0, head, north, 0.1f, 0.2f + (0.1f * i));
            Assert.That(Vector3.Distance(indicator.View.Root.position, northTarget), Is.LessThan(0.01f), "there after two seconds");

            // Ready but a preparation pending: still loading. Then nothing pending: gone, and gone for good.
            PlayPreparation.Begin();
            indicator.Step(true, true, PlayPreparation.Pending, head, north, 0.011f, 2.3f);
            Assert.That(indicator.IsShown && !indicator.IsDone, Is.True, "a preparation pending: still loading");
            PlayPreparation.End();
            indicator.Step(true, true, PlayPreparation.Pending, head, north, 0.011f, 2.4f);
            Assert.That(indicator.IsDone, Is.True);
            Assert.That(indicator.View.IsBuilt, Is.False, "the picture is destroyed");
            PlayPreparation.Begin();
            indicator.Step(true, true, PlayPreparation.Pending, head, north, 0.011f, 2.5f);
            Assert.That(indicator.View.IsBuilt, Is.False, "a later preparation is not a loading");
        }

        [Test]
        public void TheRing_TheBrightDotGoesRound_OnTheClockGiven()
        {
            var view = new PlayLoadingView();
            view.Build();
            try
            {
                view.Animate(0f);
                Assert.That(view.DotAlpha(0), Is.EqualTo(1f).Within(1e-4f), "dot 0 is the bright one at 0 s");
                Assert.That(view.DotAlpha(1), Is.LessThan(0.3f), "the one ahead of it is the dimmest");
                Assert.That(view.DotAlpha(PlayLoadingView.Dots - 1), Is.GreaterThan(view.DotAlpha(PlayLoadingView.Dots - 2)), "the ones behind fade");
                view.Animate(1f / (PlayLoadingView.RevolutionsPerSecond * PlayLoadingView.Dots));
                Assert.That(view.DotAlpha(1), Is.EqualTo(1f).Within(1e-4f), "one dot later the next is bright");
                view.Animate(1f / PlayLoadingView.RevolutionsPerSecond);
                Assert.That(view.DotAlpha(0), Is.EqualTo(1f).Within(1e-4f), "a revolution later dot 0 again");
            }
            finally
            {
                view.Destroy();
            }

            Assert.That(view.IsBuilt, Is.False);
        }

        [Test]
        public void ReadyAtOnce_NothingIsShown()
        {
            PlayLoadingIndicator indicator = NewIndicator();
            indicator.Step(true, true, 0, Vector3.zero, Quaternion.identity, 0.011f, 0f);
            Assert.That(indicator.IsShown, Is.False);
            Assert.That(indicator.IsDone, Is.True);
        }
    }
}
