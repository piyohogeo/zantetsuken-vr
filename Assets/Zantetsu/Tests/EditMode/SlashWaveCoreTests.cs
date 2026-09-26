using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The product SlashWave Core (Phase 4.50, T-035), through its one entrance: fixed device poses in, waves, SlashIds
    /// and sweeps out. The sandbox katana tests exercise the same core through the sandbox's device path; these are the
    /// core's own rules.
    /// </summary>
    public class SlashWaveCoreTests
    {
        private const double Interval = 0.011;

        // 0.12 m per 11 ms along the edge: about 10.9 m/s, past the 3.5 m/s floor and under the 20 m/s ceiling.
        private static readonly Vector3 EdgeStep = new Vector3(0f, -0.12f, 0f);

        private sealed class Feed
        {
            public readonly SlashWaveCore Core = new SlashWaveCore(
                new SlashBlade(new Pose(new Vector3(0f, 0f, 0.02f), Quaternion.Euler(-15f, 0f, 0f)), 0.9f));

            public long Frame;
            public double Time;
            public Vector3 Position = new Vector3(0f, 1.4f, 0.3f);

            public Quaternion Upright => Quaternion.Inverse(Core.Blade.GripToKatana.rotation);

            public SlashInputOutcome Step(Vector3 step)
            {
                Frame++;
                Time += Interval;
                Position += step;
                return At(Time);
            }

            public SlashInputOutcome At(double time)
            {
                Time = time;
                return Core.Update(
                    new BladePoseSample(Frame, Time, Position, Upright, BladeTrackingState.Position | BladeTrackingState.Rotation),
                    Vector3.zero, null, out _);
            }

            /// <summary>A forward sweep until this stroke latches or <paramref name="max"/> samples pass.</summary>
            public bool SweepUntilLatch(int max = 12)
            {
                int before = Core.WaveCount;
                long last = LastId();
                for (int i = 0; i < max; i++)
                {
                    Step(EdgeStep);
                    if (LastId() != last)
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>Sweeps back until the stroke re-arms (the return half ends it).</summary>
            public void ReArm()
            {
                for (int i = 0; i < 20 && Core.AcceptedSampleCount > 0; i++)
                {
                    Step(-EdgeStep);
                }

                Assert.That(Core.AcceptedSampleCount, Is.Zero, "the stroke re-armed");
            }

            public long LastId()
            {
                long max = 0;
                for (int i = 0; i < Core.WaveCount; i++)
                {
                    max = System.Math.Max(max, Core.SlashIdAt(i));
                }

                return max;
            }

            public int IndexOf(long slashId)
            {
                for (int i = 0; i < Core.WaveCount; i++)
                {
                    if (Core.SlashIdAt(i) == slashId) return i;
                }

                return -1;
            }
        }

        private struct Fixed
        {
            public double LatchedAt;
            public Plane Plane;
            public Vector3 Origin, Travel, Span;
        }

        private static Fixed Read(SlashWaveCore core, int index)
        {
            Assert.That(core.TryGetWave(index, out double at, out Plane plane, out Vector3 origin, out Vector3 travel,
                out Vector3 span, out _, out _, out _, out _, out _), Is.True);
            return new Fixed { LatchedAt = at, Plane = plane, Origin = origin, Travel = travel, Span = span };
        }

        private static void AssertSame(Fixed a, Fixed b, string what)
        {
            Assert.That(a.LatchedAt, Is.EqualTo(b.LatchedAt), what);
            Assert.That(a.Plane.normal, Is.EqualTo(b.Plane.normal), what);
            Assert.That(a.Plane.distance, Is.EqualTo(b.Plane.distance), what);
            Assert.That(a.Origin, Is.EqualTo(b.Origin), what);
            Assert.That(a.Travel, Is.EqualTo(b.Travel), what);
            Assert.That(a.Span, Is.EqualTo(b.Span), what);
        }

        /// <summary>The sample index (1-based, from a re-armed rest) at which a fresh stroke of this shape latches.</summary>
        private static int LatchIndexOfAFreshStroke()
        {
            var dry = new Feed();
            dry.Step(Vector3.zero);
            for (int i = 1; i <= 12; i++)
            {
                dry.Step(EdgeStep);
                if (dry.Core.WaveCount > 0)
                {
                    return i;
                }
            }

            Assert.Fail("the stroke shape does not latch");
            return -1;
        }

        [Test]
        public void TheCapacityRules_HoldAsOneSequence()
        {
            var f = new Feed();
            Assert.That(SlashWaveCore.Capacity, Is.EqualTo(4));

            // 1. Up to the capacity: each stroke publishes one wave, each with its own SlashId.
            var ids = new List<long>();
            for (int n = 0; n < SlashWaveCore.Capacity; n++)
            {
                Assert.That(f.SweepUntilLatch(), Is.True, "stroke " + n + " latched");
                ids.Add(f.LastId());
                f.ReArm();

                // Spaced out, so each wave has its own expiry, all four inside one lifetime.
                if (n < SlashWaveCore.Capacity - 1)
                {
                    f.At(f.Time + 0.3);
                }
            }

            Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity));
            Assert.That(ids, Is.Unique);
            var before = new Fixed[SlashWaveCore.Capacity];
            for (int i = 0; i < SlashWaveCore.Capacity; i++) before[i] = Read(f.Core, f.IndexOf(ids[i]));

            // 2. Full: a ready stroke finds no slot just before publishing. Only the new wave is not published; the
            //    live waves are the same waves, with the same fixed values.
            Assert.That(f.SweepUntilLatch(), Is.False, "a full core publishes nothing");
            Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity));
            Assert.That(f.Core.AcceptedSampleCount, Is.GreaterThan(0), "the refused stroke is still the stroke under way");
            for (int i = 0; i < SlashWaveCore.Capacity; i++)
            {
                AssertSame(Read(f.Core, f.IndexOf(ids[i])), before[i], "live wave " + ids[i] + " unchanged by the refusal");
            }

            // 3. The oldest expires and gives its slot back; the refused stroke is not latched later for it.
            Fixed oldest = before[0];
            f.At(oldest.LatchedAt + SlashWaveFlight.Adopted.LifetimeSeconds + 0.001);
            Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity - 1), "the oldest expired");
            Assert.That(f.IndexOf(ids[0]), Is.EqualTo(-1));
            for (int i = 0; i < 6; i++) f.Step(EdgeStep);
            Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity - 1), "the refused stroke does not fire late");
            Assert.That(f.LastId(), Is.EqualTo(ids[SlashWaveCore.Capacity - 1]));

            // 4. Fill the freed slot with a new stroke, then arrange a re-armed new stroke to latch in the very update
            //    the next oldest expires: expiry comes first, so that update's latch uses the slot it gave back.
            f.ReArm();
            Assert.That(f.SweepUntilLatch(), Is.True);
            long filler = f.LastId();
            f.ReArm();
            Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity));
            double nextExpiry = before[1].LatchedAt + SlashWaveFlight.Adopted.LifetimeSeconds;
            int k = LatchIndexOfAFreshStroke();
            f.At(nextExpiry - (k * Interval) + 1e-9);
            for (int i = 1; i < k; i++)
            {
                f.Step(EdgeStep);
                Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity), "full until the expiry");
            }

            f.Step(EdgeStep);
            Assert.That(f.Time, Is.GreaterThanOrEqualTo(nextExpiry), "this update is at the expiry");
            Assert.That(f.IndexOf(ids[1]), Is.EqualTo(-1), "the wave expired in this update");
            long newest = f.LastId();
            Assert.That(newest, Is.GreaterThan(filler), "and this update's latch used the slot it gave back");
            Assert.That(f.Core.WaveCount, Is.EqualTo(SlashWaveCore.Capacity));
        }

        [Test]
        public void EachWave_KeepsItsSlashId_ThroughExpiryAndSlotReuse()
        {
            var f = new Feed();
            var latched = new Dictionary<long, double>();
            for (int n = 0; n < 3; n++)
            {
                Assert.That(f.SweepUntilLatch(), Is.True);
                long id = f.LastId();
                latched[id] = Read(f.Core, f.IndexOf(id)).LatchedAt;
                f.ReArm();
            }

            // The first expires; the others move down a slot. Each still answers with its own id.
            double firstExpiry = latched[1] + SlashWaveFlight.Adopted.LifetimeSeconds;
            f.At(firstExpiry + 0.001);
            Assert.That(f.IndexOf(1), Is.EqualTo(-1));
            foreach (KeyValuePair<long, double> wave in latched)
            {
                if (wave.Key == 1) continue;
                Assert.That(Read(f.Core, f.IndexOf(wave.Key)).LatchedAt, Is.EqualTo(wave.Value), "id " + wave.Key + " is its own wave");
            }

            // A new wave takes a reused slot with a new id, never an old one.
            Assert.That(f.SweepUntilLatch(), Is.True);
            Assert.That(f.LastId(), Is.EqualTo(4));
            Assert.That(f.IndexOf(1), Is.EqualTo(-1));

            // A reset ends every wave; ids are still not reused.
            f.Core.Reset();
            f.Step(Vector3.zero);
            Assert.That(f.SweepUntilLatch(), Is.True);
            Assert.That(f.LastId(), Is.EqualTo(5));
        }

        [Test]
        public void TheLatchUpdate_GivesOneDegenerateSweep_AndTheNextUpdatesGiveTheirOwn()
        {
            var f = new Feed();
            Assert.That(f.SweepUntilLatch(), Is.True);
            long id = f.LastId();
            Assert.That(f.Core.SweepCount, Is.EqualTo(1), "one sweep in the latch update");
            SlashSweep latch = f.Core.SweepAt(0);
            Assert.That(latch.SlashId, Is.EqualTo(id));
            Assert.That(latch.IsLatch, Is.True);
            Assert.That(latch.At, Is.EqualTo(f.Time), "at this update's input time");
            Assert.That(latch.PreviousA, Is.EqualTo(latch.CurrentA), "degenerate: the initial segment twice");
            Assert.That(latch.PreviousB, Is.EqualTo(latch.CurrentB));
            Assert.That(f.Core.TryGetWaveCandidate(0, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _),
                Is.False, "no span candidate is evaluated in the latch update");

            f.Step(EdgeStep);
            Assert.That(f.Core.SweepCount, Is.EqualTo(1));
            SlashSweep next = f.Core.SweepAt(0);
            Assert.That(next.IsLatch, Is.False);
            Assert.That(next.SlashId, Is.EqualTo(id));
            Assert.That(next.PreviousA, Is.EqualTo(latch.CurrentA), "the previous segment is the one it had");
            Assert.That(next.PreviousB, Is.EqualTo(latch.CurrentB));
            Assert.That(Vector3.Dot(next.CurrentA - next.PreviousA, next.TravelAxis), Is.GreaterThan(0f), "it moved on");
            Assert.That(next.SourceSlashPlane.normal, Is.EqualTo(latch.SourceSlashPlane.normal), "fixed plane and axes");
            Assert.That(next.TravelAxis, Is.EqualTo(latch.TravelAxis));
            Assert.That(next.SpanAxis, Is.EqualTo(latch.SpanAxis));
            Assert.That(f.Core.TryGetWaveCandidate(0, out double at, out bool evaluated, out _, out _, out _, out _, out _, out _, out _, out _, out _),
                Is.True, "from the next update on");
            Assert.That(at, Is.EqualTo(f.Time));

            // The accepted span never shrinks, update after update, whatever the candidates were.
            float span = next.CurrentB == next.CurrentA ? 0f : Vector3.Distance(next.CurrentA, next.CurrentB);
            bool closedSeen = false;
            for (int i = 0; i < 40; i++)
            {
                f.Step(i % 3 == 0 ? -EdgeStep : EdgeStep * 0.3f);
                if (f.Core.WaveCount == 0) break;
                Assert.That(f.Core.TryGetWave(0, out _, out _, out _, out _, out _, out float accepted, out _, out _, out _, out _), Is.True);
                Assert.That(accepted, Is.GreaterThanOrEqualTo(span - 1e-6f), "never narrower");
                span = accepted;
                if (!closedSeen && f.Core.TryGetWaveSpanClose(0, out double closedAt, out _, out _))
                {
                    closedSeen = true;
                    Assert.That(closedAt, Is.EqualTo(f.Time), "closed in this update");
                    Assert.That(f.Core.TryGetWaveCandidate(0, out _, out _, out bool frozen, out _, out _, out _, out _, out _, out _, out _, out _), Is.True);
                    Assert.That(frozen, Is.False, "the closing update keeps its live candidate");
                    f.Step(Vector3.zero);
                    Assert.That(f.Core.TryGetWaveCandidate(0, out _, out _, out bool frozenNext, out _, out _, out _, out _, out _, out _, out _, out _), Is.True);
                    Assert.That(frozenNext, Is.True, "the frozen guide from the next update");
                }
            }

            Assert.That(closedSeen, Is.True, "the span closed within the run");

            // Expired: in the update it expires in, the wave gives no sweep.
            double expiry = latch.At + SlashWaveFlight.Adopted.LifetimeSeconds;
            f.At(expiry);
            Assert.That(f.Core.WaveCount, Is.Zero);
            Assert.That(f.Core.SweepCount, Is.Zero, "no segment or sweep for an expired wave");
        }

        [Test]
        public void AFlyingWave_KeepsTheMethodAndFlightItLatchedWith_WhileTheNextTakesTheNewOnes()
        {
            var f = new Feed();
            Assert.That(f.SweepUntilLatch(), Is.True);
            long first = f.LastId();
            Assert.That(f.Core.TryGetWaveMethod(0, out SlashWaveFlight flight, out ISlashSpanCandidateEstimator candidate, out ISlashSpanCloseEstimator close), Is.True);
            Fixed firstFixed = Read(f.Core, 0);

            // The development UI changes everything afterwards.
            var newCandidate = new GuideRaySpanCandidate(0.5f);
            var newClose = new CaptureTimeoutSpanClose(0.05f);
            f.Core.SpanCandidateEstimator = newCandidate;
            f.Core.SpanCloseEstimator = newClose;
            f.Core.Flight = new SlashWaveFlight(30f, 1.0f);
            f.ReArm();

            Assert.That(f.Core.TryGetWaveMethod(f.IndexOf(first), out SlashWaveFlight stillFlight, out ISlashSpanCandidateEstimator stillCandidate, out ISlashSpanCloseEstimator stillClose), Is.True);
            Assert.That(stillFlight.Speed, Is.EqualTo(flight.Speed), "its speed");
            Assert.That(stillFlight.LifetimeSeconds, Is.EqualTo(flight.LifetimeSeconds), "its lifetime");
            Assert.That(stillCandidate, Is.SameAs(candidate), "its candidate estimator");
            Assert.That(stillClose, Is.SameAs(close), "its close estimator");
            AssertSame(Read(f.Core, f.IndexOf(first)), firstFixed, "its fixed values");

            // It still flies at its own speed.
            Assert.That(f.Core.TryGetWave(f.IndexOf(first), out double latchedAt, out _, out Vector3 origin, out Vector3 travel,
                out _, out _, out _, out _, out Vector3 a, out _), Is.True);
            Assert.That(Vector3.Dot(a - origin, travel), Is.EqualTo((float)(flight.Speed * (f.Time - latchedAt))).Within(1e-3f));

            Assert.That(f.SweepUntilLatch(), Is.True);
            long second = f.LastId();
            Assert.That(f.Core.TryGetWaveMethod(f.IndexOf(second), out SlashWaveFlight newFlight, out ISlashSpanCandidateEstimator c2, out ISlashSpanCloseEstimator k2), Is.True);
            Assert.That(newFlight.Speed, Is.EqualTo(30f));
            Assert.That(c2, Is.SameAs(newCandidate));
            Assert.That(k2, Is.SameAs(newClose));

            // The first still ends at its own lifetime, not the new one.
            f.At(latchedAt + 1.0 + 0.001);
            Assert.That(f.IndexOf(first), Is.Not.EqualTo(-1), "not ended by the new, shorter lifetime");
            f.At(latchedAt + SlashWaveFlight.Adopted.LifetimeSeconds);
            Assert.That(f.IndexOf(first), Is.EqualTo(-1));
        }

        // A close timeout as long as the lifetime, or longer: the wave is published, flies with its span open, never
        // closes, and in the update it expires in gives no sweep and gives its slot back (DESIGN 19.1.1).
        [TestCase(0.1f, 0.25f, TestName = "ACloseTimeoutLongerThanTheLifetime_LatchesAndExpiresOpen")]
        [TestCase(0.25f, 0.25f, TestName = "ACloseTimeoutAsLongAsTheLifetime_LatchesAndExpiresOpen")]
        public void ACloseTimeoutNotShorterThanTheLifetime_LatchesAndExpiresOpen(float lifetime, float timeout)
        {
            var f = new Feed();
            f.Core.Flight = new SlashWaveFlight(SlashWaveFlight.Adopted.Speed, lifetime);
            f.Core.SpanCloseEstimator = new CaptureTimeoutSpanClose(timeout);
            Assert.That(f.SweepUntilLatch(), Is.True, "published");
            long id = f.LastId();
            double latchedAt = Read(f.Core, f.IndexOf(id)).LatchedAt;

            int flyingUpdates = 0;
            while (true)
            {
                f.Step(Vector3.zero);
                int index = f.IndexOf(id);
                if (index < 0)
                {
                    break;
                }

                flyingUpdates++;
                Assert.That(f.Core.TryGetWaveSpanClose(index, out _, out _, out _), Is.False, "the span stays open");
                Assert.That(f.Core.TryGetWaveCandidate(index, out _, out bool evaluated, out bool frozen, out _, out _, out _, out _, out _, out _, out _, out _),
                    Is.True);
                Assert.That(evaluated && !frozen, Is.True, "evaluated with the live guide");
            }

            Assert.That(flyingUpdates, Is.GreaterThan(0), "it flew");
            Assert.That(f.Time, Is.GreaterThanOrEqualTo(latchedAt + lifetime), "it ended at its expiry");
            Assert.That(f.Core.WaveCount, Is.Zero, "its slot is back");
            Assert.That(f.Core.SweepCount, Is.Zero, "and the expiry update gives no sweep for it");
        }

        [Test]
        public void TheAdoptedTimeoutAndLifetime_StillCloseBeforeTheExpiry()
        {
            var f = new Feed();
            Assert.That(f.Core.Flight.LifetimeSeconds, Is.EqualTo(1.5f));
            Assert.That(((CaptureTimeoutSpanClose)f.Core.SpanCloseEstimator).TimeoutSeconds, Is.EqualTo(0.25f));
            Assert.That(f.SweepUntilLatch(), Is.True);
            double latchedAt = Read(f.Core, 0).LatchedAt;
            double closedAt = double.NaN;
            for (int i = 0; i < 40 && double.IsNaN(closedAt); i++)
            {
                f.Step(Vector3.zero);
                if (f.Core.TryGetWaveSpanClose(0, out double at, out _, out _)) closedAt = at;
            }

            Assert.That(closedAt - latchedAt, Is.GreaterThanOrEqualTo(0.25).And.LessThan(0.25 + 0.012), "closed at the first update past 0.25 s");
            Assert.That(f.Core.WaveCount, Is.EqualTo(1), "and still flying");
        }

        [Test]
        public void ChangingTheLatchEstimator_ReachesTheStrokeNotYetLatched()
        {
            var f = new Feed();
            f.Core.LatchEstimator = new EmitterChordLatch(100f);
            for (int i = 0; i < 6; i++) f.Step(EdgeStep);
            Assert.That(f.Core.WaveCount, Is.Zero, "a latch distance this long is not reached");
            Assert.That(f.Core.AcceptedSampleCount, Is.GreaterThan(1), "the stroke is under way");

            f.Core.LatchEstimator = new EmitterChordLatch(0.35f);
            f.Step(EdgeStep);
            Assert.That(f.Core.WaveCount, Is.EqualTo(1), "the same stroke latches under the estimator it is judged by now");
        }

        [Test]
        public void TheKatanaCarriesNoCollider_SoItPushesNothing()
        {
            // T-040: the katana is not a physical body; the waves are the only thing a slash puts out.
            try
            {
                Scene scene = EditorSceneManager.OpenScene("Assets/Scenes/Sandbox.unity", OpenSceneMode.Single);
                int roots = 0;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name != "Katana" && root.name != "Sandbox Katana Rig" && root.name != "Slash Wave VFX")
                    {
                        continue;
                    }

                    roots++;
                    Assert.That(root.GetComponentsInChildren<Collider>(true), Is.Empty, root.name + " carries no collider");
                    Assert.That(root.GetComponentsInChildren<Rigidbody>(true), Is.Empty, root.name + " carries no body");
                }

                Assert.That(roots, Is.EqualTo(3));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }
    }
}
