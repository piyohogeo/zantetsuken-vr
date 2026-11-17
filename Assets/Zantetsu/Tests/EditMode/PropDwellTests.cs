using System;
using NUnit.Framework;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Core.Tests
{
    /// <summary>PROP-OBSTACLE-1: the local-stay measurement on made-up motions -- standing, a turn, spinning, swinging, a small circle, a goal change, a retirement.</summary>
    public sealed class PropDwellTests
    {
        private const float Dt = 1f / 72f;

        /// <summary>Feeds `seconds` of a motion given as functions of the time since `from`.</summary>
        private static void Feed(DwellTracker tracker, int id, double from, double seconds, Func<double, float> x, Func<double, float> z, Func<double, float> yaw, float obstacle = float.PositiveInfinity, int clip = 1, bool standing = true)
        {
            int frames = (int)Math.Round(seconds / Dt);
            for (int i = 0; i <= frames; i++)
            {
                double t = i * (double)Dt;
                tracker.Frame(from + t, id, x(t), z(t), yaw(t), obstacle, 1, clip, standing);
            }
        }

        private static DwellTracker Tracker() => new DwellTracker(new DwellSettings(), 0.35f);

        [Test]
        public void Standing_IsOneStillStay_AndAShortStopIsNone()
        {
            DwellTracker tracker = Tracker();
            Feed(tracker, 1, 10.0, 5.0, t => 3f, t => 4f, t => 20f);
            Feed(tracker, 2, 10.0, 2.5, t => 8f, t => 4f, t => 20f);
            tracker.Finish();
            Assert.That(tracker.Stays.Count, Is.EqualTo(1), "under 3 s is no stay");
            DwellInterval s = tracker.Stays[0];
            Assert.That(s.Id == 1 && s.EndedBy == "section-end" && s.Kind(tracker.Settings) == "still", Is.True, s.Kind(tracker.Settings));
            Assert.That(s.Seconds, Is.EqualTo(5.0).Within(0.02));
            Assert.That(s.AbsYaw < 0.01f && s.PathMeters < 0.001f && s.RotatingWindows == 0 && s.StationaryClipSeconds > 4.9, Is.True);
            Assert.That(s.Near, Is.False, "no obstacle near");
        }

        [Test]
        public void ATurnThenAWait_IsNotRotatingOn_AndATurnThenAWalkIsNoStay()
        {
            DwellTracker tracker = Tracker();
            // 90 degrees in the first second, then 5 s waiting
            Feed(tracker, 1, 0.0, 6.0, t => 0f, t => 0f, t => (float)Math.Min(90.0, t * 90.0));
            // the same turn, then walking away at 0.9 m/s
            Feed(tracker, 2, 0.0, 6.0, t => 5f, t => (float)(t < 1.0 ? 0.0 : (t - 1.0) * 0.9), t => (float)Math.Min(90.0, t * 90.0), standing: false);
            tracker.Finish();
            Assert.That(tracker.Stays.Count, Is.EqualTo(1));
            DwellInterval s = tracker.Stays[0];
            Assert.That(s.Id, Is.EqualTo(1));
            Assert.That(s.AbsYaw, Is.EqualTo(90f).Within(1f));
            Assert.That(s.RotatingWindows, Is.EqualTo(1), "one second of turning");
            Assert.That(s.Kind(tracker.Settings), Is.EqualTo("turn-then-wait"));
        }

        [Test]
        public void SpinningOnTheSpot_IsRotatingOnOneWay_WithTheWholeTurnCounted()
        {
            DwellTracker tracker = Tracker();
            // 90 degrees a second for 8 s: twice round; the yaw is given wrapped, as a pose has it
            Feed(tracker, 1, 0.0, 8.0, t => 0f, t => 0f, t => Pose2.WrapDeg((float)(t * 90.0)));
            tracker.Finish();
            DwellInterval s = tracker.Stays[0];
            Assert.That(s.AbsYaw, Is.EqualTo(720f).Within(2f), "the wrap at 180 degrees adds nothing");
            Assert.That(s.SignedYaw, Is.EqualTo(720f).Within(2f));
            Assert.That(s.RotatingWindows, Is.EqualTo(8));
            Assert.That(s.LongestRotatingRun, Is.EqualTo(8));
            Assert.That(s.YawRate, Is.EqualTo(90f).Within(1f));
            Assert.That(s.Kind(tracker.Settings), Is.EqualTo("rotating-on (one-way)"));
        }

        [Test]
        public void SwingingLeftAndRight_IsRotatingOnBackAndForth()
        {
            DwellTracker tracker = Tracker();
            // +-60 degrees with a 2 s period: 240 degrees of absolute yaw every period, none in sum
            Feed(tracker, 1, 0.0, 10.0, t => 0f, t => 0f, t => (float)(60.0 * Math.Sin(Math.PI * t)));
            tracker.Finish();
            DwellInterval s = tracker.Stays[0];
            Assert.That(s.AbsYaw, Is.EqualTo(1200f).Within(15f));
            Assert.That(Math.Abs(s.SignedYaw), Is.LessThan(5f));
            Assert.That(s.Kind(tracker.Settings), Is.EqualTo("rotating-on (back-and-forth)"));
        }

        [Test]
        public void TurningWithASmallMovementOfTheRoot_AndASmallCircle_AreStays_AndAWiderCircleIsNot()
        {
            DwellTracker tracker = Tracker();
            // spinning while the root drifts 0.1 m about: no speed condition leaves it out
            Feed(tracker, 1, 0.0, 6.0, t => (float)(0.1 * Math.Sin(3.0 * t)), t => (float)(0.1 * Math.Cos(2.0 * t) - 0.1), t => Pose2.WrapDeg((float)(t * 120.0)), standing: false);
            // a circle of 0.2 m radius walked at 0.5 m/s, facing along it: never farther than 0.4 m from where it began
            double w = 0.5 / 0.2;
            Feed(tracker, 2, 0.0, 8.0, t => (float)(0.2 * Math.Cos(w * t) - 0.2), t => (float)(0.2 * Math.Sin(w * t)), t => Pose2.WrapDeg((float)(-w * t * 180.0 / Math.PI)), standing: false);
            // a circle of 0.6 m radius at 0.9 m/s: out of the radius within a second and a half, every time
            double w2 = 0.9 / 0.6;
            Feed(tracker, 3, 0.0, 12.0, t => (float)(0.6 * Math.Cos(w2 * t) - 0.6), t => (float)(0.6 * Math.Sin(w2 * t)), t => Pose2.WrapDeg((float)(-w2 * t * 180.0 / Math.PI)), standing: false);
            tracker.Finish();
            DwellInterval drift = tracker.Stays.Find(s => s.Id == 1), circle = tracker.Stays.Find(s => s.Id == 2);
            Assert.That(drift != null && circle != null, Is.True);
            Assert.That(drift.PathMeters > 0.5f && drift.MaxDistance <= 0.5f && drift.Kind(tracker.Settings) == "rotating-on (one-way)", Is.True, "a turn with a small movement");
            Assert.That(circle.PathMeters, Is.EqualTo(4.0f).Within(0.1f), "8 s at 0.5 m/s, all inside the radius");
            Assert.That(circle.MaxDistance, Is.EqualTo(0.4f).Within(0.01f));
            Assert.That(circle.Kind(tracker.Settings), Is.EqualTo("rotating-on (one-way)"));
            Assert.That(circle.SignedYaw, Is.LessThan(-1000f), "anticlockwise");
            Assert.That(tracker.Stays.Exists(s => s.Id == 3), Is.False, "a circle wider than the radius is walking, not a stay (the first values' limit)");
        }

        [Test]
        public void AGoalChangeStaysInsideTheStay_AndProgressIsNotMixedAcrossGoals()
        {
            DwellTracker tracker = Tracker();
            // standing 10 m from the goal; at 4 s the goal becomes one 30 m away; at 7 s it is "reached" somewhere else -- the NPC never moves
            for (int i = 0; i <= 720; i++)
            {
                double t = i * (double)Dt;
                tracker.Frame(t, 1, 0f, 0f, 0f, 1.0f, 3, 1, true);
                if (i == 0) tracker.Goal(t, 1, 10f, 0f, null);
                if (i == 288) tracker.Goal(t, 1, 0f, 30f, "changed");
                if (i == 504) tracker.Goal(t, 1, 0.2f, 0f, "reached");
            }

            tracker.Finish();
            Assert.That(tracker.Stays.Count, Is.EqualTo(1), "a goal that changes does not end the stay");
            DwellInterval s = tracker.Stays[0];
            Assert.That(s.GoalEvents.Count, Is.EqualTo(2));
            Assert.That(s.GoalEvents[0].what == "changed" && s.GoalEvents[1].what == "reached", Is.True);
            Assert.That(s.ProgressMeters, Is.EqualTo(0f).Within(1e-3f), "10 m to one goal and 30 m to the next are not a step of 20 m");
            Assert.That(s.GoalUnreachedSeconds, Is.EqualTo(7.0).Within(0.05), "not at its goal until the last one, 0.2 m away");
            Assert.That(s.Near, Is.True, "1 m from the sections");
            Assert.That(s.FirstMapVersion == 3 && s.MapVersions == 1, Is.True);
        }

        [Test]
        public void ARetirementEndsTheStay_AndLeavingTheRadiusEndsItAtThatFrame()
        {
            DwellTracker tracker = Tracker();
            Feed(tracker, 1, 0.0, 6.0, t => 0f, t => 0f, t => 0f);
            tracker.Retire(1);
            Feed(tracker, 2, 0.0, 2.0, t => 0f, t => 0f, t => 0f);
            tracker.Retire(2);
            // 4 s standing, then walking off at 0.9 m/s for 3 s
            Feed(tracker, 3, 0.0, 7.0, t => 0f, t => (float)(t < 4.0 ? 0.0 : (t - 4.0) * 0.9), t => 0f);
            tracker.Finish();
            Assert.That(tracker.Stays.Count, Is.EqualTo(2));
            Assert.That(tracker.Stays[0].Id == 1 && tracker.Stays[0].EndedBy == "retired", Is.True);
            DwellInterval left = tracker.Stays[1];
            Assert.That(left.Id == 3 && left.EndedBy == "left", Is.True);
            Assert.That(left.Seconds, Is.EqualTo(4.0 + 0.5 / 0.9).Within(0.05), "to the last frame inside the radius");
            Assert.That(left.MaxDistance, Is.LessThanOrEqualTo(0.5f));
        }
    }
}
