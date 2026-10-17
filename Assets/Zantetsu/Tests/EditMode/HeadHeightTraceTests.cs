using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>The view-height diagnosis's decision of what to record (DESIGN 4.6, unit 6-A): windows, jumps, steady frames, the settled mean.</summary>
    public sealed class HeadHeightTraceTests
    {
        [Test]
        public void ATrigger_OpensAWindow_AndASecondOne_ExtendsIt_WithoutOpeningAgain()
        {
            var t = new HeadHeightTrace();
            Assert.That(t.Trigger(100), Is.True);
            Assert.That(t.InWindow(100 + HeadHeightTrace.WindowFrames), Is.True);
            Assert.That(t.InWindow(101 + HeadHeightTrace.WindowFrames), Is.False);
            Assert.That(t.Trigger(150), Is.False, "extended, not opened");
            Assert.That(t.InWindow(150 + HeadHeightTrace.WindowFrames), Is.True);
        }

        [Test]
        public void InsideAWindow_EveryFrameIsRecorded_OutsideOneInTenSeconds()
        {
            var t = new HeadHeightTrace();
            Assert.That(t.Sample(1, 0.0, true, 1.2f, out string why), Is.True);
            Assert.That(why, Is.EqualTo("steady"), "the first frame is the first steady one");
            Assert.That(t.Sample(2, 0.02, true, 1.2f, out why), Is.False);
            Assert.That(t.Sample(3, HeadHeightTrace.SteadySeconds - 0.01, true, 1.2f, out why), Is.False);
            Assert.That(t.Sample(4, HeadHeightTrace.SteadySeconds + 0.01, true, 1.2f, out why), Is.True);
            Assert.That(why, Is.EqualTo("steady"));

            t.Trigger(10);
            for (int f = 10; f <= 10 + HeadHeightTrace.WindowFrames; f++)
            {
                Assert.That(t.Sample(f, 20.0 + f * 0.02, true, 1.2f, out why), Is.True, "frame " + f);
                Assert.That(why, Is.EqualTo("window"));
            }

            Assert.That(t.Sample(11 + HeadHeightTrace.WindowFrames, 30.0, true, 1.2f, out why), Is.False, "the window is over, and a recorded frame counted as the steady one");
        }

        [Test]
        public void AJump_BetweenTwoTrackedFrames_IsATrigger_AnUntrackedFramesValue_IsNot()
        {
            var t = new HeadHeightTrace();
            t.Sample(1, 0.0, true, 1.20f, out _);
            Assert.That(t.Sample(2, 0.02, true, 1.25f, out string why), Is.False, "5 cm is no jump");
            Assert.That(t.Sample(3, 0.04, true, 1.60f, out why), Is.True);
            Assert.That(why, Is.EqualTo("jump"));
            Assert.That(t.InWindow(3 + HeadHeightTrace.WindowFrames), Is.True, "a jump opens the window");

            var u = new HeadHeightTrace();
            u.Sample(1, 0.0, true, 1.20f, out _);
            Assert.That(u.Sample(2, 0.02, false, 0f, out why), Is.False, "an untracked frame's zero is no jump down");
            Assert.That(u.Sample(3, 0.04, true, 1.20f, out why), Is.False, "nor is the tracked frame after it a jump up");
            Assert.That(u.InWindow(3), Is.False);
        }

        [Test]
        public void TheSettledMean_IsOfTheWindowsLastTrackedFrames_ToldOnce_AtTheFrameAfterTheWindow()
        {
            var t = new HeadHeightTrace();
            t.Trigger(10);
            int end = 10 + HeadHeightTrace.WindowFrames;
            for (int f = 10; f <= end; f++)
            {
                // the first half untracked (the values are no heights), the last 30 frames at 1.25 and the rest at 1.2
                // (5 cm: below the jump, which would extend the window)
                bool tracked = f > 40;
                float y = f > end - HeadHeightTrace.SettledFrames ? 1.25f : 1.2f;
                t.Sample(f, f * 0.02, tracked, y, out _);
                Assert.That(t.Settled(f, out _, out _), Is.False, "not before the window's end, frame " + f);
            }

            Assert.That(t.Settled(end + 1, out float mean, out int count), Is.True);
            Assert.That(count, Is.EqualTo(HeadHeightTrace.SettledFrames));
            Assert.That(mean, Is.EqualTo(1.25f).Within(1e-5f));
            Assert.That(t.Settled(end + 2, out _, out _), Is.False, "told once");
            Assert.That(t.Settled(end + 1, out _, out _), Is.False);
        }

        [Test]
        public void TheDiagnosis_IsAskedFor_ByItsArgumentAlone()
        {
            Assert.That(HeadHeightDiagnosis.AskedFor(new[] { "-zantetsuScenario", "play" }), Is.False, "a normal launch creates nothing");
            Assert.That(HeadHeightDiagnosis.AskedFor(null), Is.False);
            Assert.That(HeadHeightDiagnosis.AskedFor(new[] { "-zantetsuScenario", "play", HeadHeightDiagnosis.Argument }), Is.True);
        }

        [Test]
        public void AWindowWithNoTrackedFrame_SettlesWithACountOfZero()
        {
            var t = new HeadHeightTrace();
            t.Trigger(5);
            int end = 5 + HeadHeightTrace.WindowFrames;
            for (int f = 5; f <= end; f++) t.Sample(f, f * 0.02, false, 0f, out _);
            Assert.That(t.Settled(end + 1, out _, out int count), Is.True);
            Assert.That(count, Is.Zero);
        }
    }
}
