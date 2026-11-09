using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Statistics;
using Zantetsu.Quest;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    public sealed class PcvrPlayPrewarmTests
    {
        [SetUp]
        public void SetUp()
        {
            RemovePreparation();
            PlayPreparation.ResetForSession();
            PlayMenu.ReleasePreparation();
        }

        [TearDown]
        public void TearDown()
        {
            RemovePreparation();
            PlayMenu.ReleasePreparation();
            PlayPreparation.ResetForSession();
        }

        private static void RemovePreparation()
        {
            foreach (PcvrPlayPrewarm preparation in Object.FindObjectsByType<PcvrPlayPrewarm>(FindObjectsSortMode.None))
            {
                preparation.Abandon("the test is ending");
                Object.DestroyImmediate(preparation.gameObject);
            }
        }

        [Test]
        public void InstallingBeforeTheFirstFrame_HoldsLoadingAndCutInput_Once()
        {
            bool paused = GamePause.IsPaused;
            PcvrPlayPrewarm preparation = PcvrPlayPrewarm.Install();
            Assert.That(PcvrPlayPrewarm.Install(), Is.SameAs(preparation));
            Assert.That(PlayPreparation.Pending, Is.EqualTo(1));
            Assert.That(PlayPreparation.Begun, Is.EqualTo(1));
            Assert.That(PlayMenu.PreparationHeld, Is.True);
            Assert.That(PlayLoadingIndicator.Loading(true, true, PlayPreparation.Pending), Is.True);
            Assert.That(GamePause.IsPaused, Is.EqualTo(paused));
            Assert.That(preparation.Finished, Is.False);
        }

        [Test]
        public void OtherPreparationsAreWaitedFor_WithoutWaitingForThisPreparationsOwnCount()
        {
            PcvrPlayPrewarm preparation = PcvrPlayPrewarm.Install();
            Assert.That(preparation.OtherPreparationsPending, Is.False);
            PlayPreparation.Begin();
            Assert.That(preparation.OtherPreparationsPending, Is.True);
            PlayPreparation.End();
            Assert.That(preparation.OtherPreparationsPending, Is.False);
        }

        [TestCase(false, false, false, true)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, true, true)]
        public void AnXrDisplayThatHasNotStarted_DoesNotWarmTheDesktopCamera(bool present, bool running, bool stereo, bool ready)
        {
            Assert.That(PcvrPlayPrewarm.XrCameraReady(present, running, stereo), Is.EqualTo(ready));
        }

        [Test]
        public void AReadinessTimeout_FailsAndReleasesItsHoldExactlyOnce()
        {
            PlayPreparation.Begin();
            PcvrPlayPrewarm preparation = PcvrPlayPrewarm.Install();
            IEnumerator run = preparation.Prepare(0.0);
            LogAssert.Expect(LogType.Error, new Regex(@"^PCVR PLAY PREWARM: FAILED: still waiting for .* at the preparation deadline"));
            Assert.That(run.MoveNext(), Is.False);
            Assert.That(preparation.Finished, Is.True);
            Assert.That(preparation.Completed, Is.False);
            Assert.That(preparation.Failure, Does.Contain("preparation deadline"));
            Assert.That(PlayPreparation.Pending, Is.EqualTo(1));
            Assert.That(PlayPreparation.Ended, Is.EqualTo(1));
            Assert.That(PlayMenu.PreparationHeld, Is.False);
            preparation.Abandon("already finished");
            Assert.That(run.MoveNext(), Is.False);
            Assert.That(PlayPreparation.Ended, Is.EqualTo(1));
        }

        [Test]
        public void AbandonmentEndsOnlyItsOwnPreparation_AndCannotBecomeACompletion()
        {
            PlayPreparation.Begin();
            PcvrPlayPrewarm preparation = PcvrPlayPrewarm.Install();
            preparation.Abandon("the world ended");
            preparation.Abandon("the camera ended too");
            Assert.That(PlayPreparation.Pending, Is.EqualTo(1));
            Assert.That(PlayPreparation.Ended, Is.EqualTo(1));
            Assert.That(PlayMenu.PreparationHeld, Is.False);
            Assert.That(preparation.Finished, Is.True);
            Assert.That(preparation.Completed, Is.False);
            Assert.That(preparation.Failure, Is.EqualTo("the world ended"));
            Object.DestroyImmediate(preparation.gameObject);
            Assert.That(PlayPreparation.Ended, Is.EqualTo(1));
        }
    }
}
