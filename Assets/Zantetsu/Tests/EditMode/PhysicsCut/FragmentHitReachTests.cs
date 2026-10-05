using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The arithmetic of the fragments' reach (2026-10-05; DESIGN 19.1.7, D-193; <see cref="FragmentHitReach"/>), by
    /// itself: the reach of a box as placed in its owner holds every point of that box, wherever the box's centre is,
    /// however the placement turns, scales or shears it -- and half the box's diagonal would not; a sweep's box is
    /// said to be beyond the reach only when it is, by more than the margin, and never when the numbers cannot be
    /// vouched for. (The owner's own part -- its scale and its parents' -- needs Transforms: the PlayMode cases.)
    /// </summary>
    public sealed class FragmentHitReachTests
    {
        private static float4x4 Affine(System.Random random, float offset, float scale, bool shear)
        {
            float N(float span) => (float)(random.NextDouble() * 2.0 - 1.0) * span;
            quaternion q = quaternion.Euler(N(3.2f), N(3.2f), N(3.2f));
            float3 s = new float3(1f + (float)random.NextDouble() * scale, 1f + (float)random.NextDouble() * scale, 1f + (float)random.NextDouble() * scale);
            if (random.Next(4) == 0) s.x = -s.x;   // a mirror
            float4x4 m = float4x4.TRS(new float3(N(offset), N(offset), N(offset)), q, s);
            if (shear)
            {
                // a second turn and scale on top: the axes are no longer at right angles
                m = math.mul(float4x4.TRS(new float3(N(offset), N(offset), N(offset)), quaternion.Euler(N(3.2f), N(3.2f), N(3.2f)), new float3(1f, 1f + (float)random.NextDouble() * scale, 1f)), m);
            }

            return m;
        }

        private static double Length(double3 v) => math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

        private static double3 Carry(in float4x4 m, double3 p) =>
            new double3(m.c0.xyz) * p.x + new double3(m.c1.xyz) * p.y + new double3(m.c2.xyz) * p.z + new double3(m.c3.xyz);

        [Test]
        public void TheOwnerReach_HoldsEveryPointOfTheBoxAsPlaced_AndHalfTheDiagonalWouldNot()
        {
            var random = new System.Random(20261005);
            int cases = 0, diagonalTooSmall = 0;
            double loosest = 1.0;
            for (int i = 0; i < 4000; i++)
            {
                float N(float span) => (float)(random.NextDouble() * 2.0 - 1.0) * span;
                // boxes of every kind: about the origin, far off it, thin, a point, large
                float3 centre = i % 3 == 0 ? float3.zero : new float3(N(40f), N(40f), N(40f));
                float size = i % 7 == 0 ? 0f : i % 5 == 0 ? 60f : 2f;
                float3 half = new float3((float)random.NextDouble() * size, (float)random.NextDouble() * size, i % 11 == 0 ? 0f : (float)random.NextDouble() * size);
                float3 lo = centre - half, hi = centre + half;
                float4x4 placement = i % 4 == 0 ? float4x4.identity : Affine(random, i % 6 == 0 ? 500f : 8f, i % 2 == 0 ? 0f : 3f, i % 9 == 0);
                Assert.That(FragmentHitReach.TryOwnerReach(lo, hi, in placement, out float reach), Is.True, "case " + i);

                double farthest = 0.0;
                for (int corner = 0; corner < 8; corner++)
                {
                    var p = new double3((corner & 1) == 0 ? lo.x : hi.x, (corner & 2) == 0 ? lo.y : hi.y, (corner & 4) == 0 ? lo.z : hi.z);
                    farthest = math.max(farthest, Length(Carry(in placement, p)));
                }

                for (int k = 0; k < 24; k++)
                {
                    var p = new double3(math.lerp(lo.x, hi.x, (float)random.NextDouble()), math.lerp(lo.y, hi.y, (float)random.NextDouble()), math.lerp(lo.z, hi.z, (float)random.NextDouble()));
                    double d = Length(Carry(in placement, p));
                    if (d > reach) Assert.Fail("case " + i + ": a point of the box is " + d + " from the owner's origin, the reach is " + reach);
                }

                if (farthest > reach) Assert.Fail("case " + i + ": a corner is " + farthest + " from the owner's origin, the reach is " + reach);
                // ... and not loose: within a part in ten thousand of the farthest corner, and what the carrying can be off by
                float3 far = math.max(math.abs(lo), math.abs(hi));
                double terms = math.cmax(math.abs(placement.c0.xyz) * far.x + math.abs(placement.c1.xyz) * far.y + math.abs(placement.c2.xyz) * far.z + math.abs(placement.c3.xyz));
                Assert.That(reach, Is.LessThanOrEqualTo(farthest * 1.0001 + 1e-5 * terms + 1e-30), "case " + i + ": the reach is the farthest corner's distance, not more");
                if (farthest > 1e-3) loosest = math.max(loosest, reach / farthest);

                // half the diagonal, as carried by the largest scale, is the box's size and says nothing of where it is
                double halfDiagonal = Length(new double3(hi - lo)) * 0.5;
                if (halfDiagonal < farthest * 0.999) diagonalTooSmall++;
                cases++;
            }

            TestContext.Out.WriteLine("cases " + cases + "; the reach over the farthest corner at most x" + loosest.ToString("F6") + "; cases where half the box's diagonal is less than the farthest corner's distance: " + diagonalTooSmall);
            Assert.That(diagonalTooSmall, Is.GreaterThan(cases / 2), "half the diagonal would not have held the box in most of these");
        }

        [Test]
        public void TheOwnerReach_IsNone_ForABoxOrAPlacementThatCannotBeVouchedFor()
        {
            float4x4 identity = float4x4.identity;
            Assert.That(FragmentHitReach.TryOwnerReach(new float3(-1f), new float3(1f), in identity, out float reach), Is.True);
            Assert.That(reach, Is.EqualTo(math.sqrt(3f)).Within(1e-4f));
            Assert.That(FragmentHitReach.TryOwnerReach(new float3(float.NaN, 0f, 0f), new float3(1f), in identity, out reach), Is.False, "a box that is not finite");
            Assert.That(reach, Is.LessThan(0f));
            Assert.That(FragmentHitReach.TryOwnerReach(new float3(1f, 0f, 0f), new float3(0f, 1f, 1f), in identity, out reach), Is.False, "a box inside out");
            float4x4 broken = identity;
            broken.c3.x = float.PositiveInfinity;
            Assert.That(FragmentHitReach.TryOwnerReach(new float3(-1f), new float3(1f), in broken, out reach), Is.False, "a placement that is not finite");
            float4x4 projective = identity;
            projective.c0.w = 0.1f;
            Assert.That(FragmentHitReach.TryOwnerReach(new float3(-1f), new float3(1f), in projective, out reach), Is.False, "a placement that is not affine");
            float4x4 huge = float4x4.Scale(1e30f);
            Assert.That(FragmentHitReach.TryOwnerReach(new float3(-1e10f), new float3(1e10f), in huge, out reach), Is.False, "a reach that overflows");
            Assert.That(reach, Is.LessThan(0f));
        }

        [Test]
        public void ASweepIsBeyond_OnlyWhenItCertainlyIs_AndTouchingIsNot()
        {
            var random = new System.Random(5);
            int beyond = 0, within = 0, inTheMargin = 0;
            for (int i = 0; i < 20000; i++)
            {
                float N(float span) => (float)(random.NextDouble() * 2.0 - 1.0) * span;
                float where = i % 4 == 0 ? 2000f : 60f;
                float3 p = new float3(N(where), N(where), N(where));
                float reach = i % 9 == 0 ? 0f : (float)random.NextDouble() * (i % 5 == 0 ? 40f : 3f);
                float3 c = p + new float3(N(1f), N(1f), N(1f)) * (reach + (float)random.NextDouble() * (i % 3 == 0 ? 30f : 2f * reach + 1f));
                float3 half = new float3((float)random.NextDouble() * 3f, i % 6 == 0 ? 0f : (float)random.NextDouble() * 3f, (float)random.NextDouble() * 3f);
                float3 lo = c - half, hi = c + half;
                if (i % 13 == 0)
                {
                    // touching exactly: the box's face at the reach along one axis
                    lo = p + new float3(reach, -1f, -1f);
                    hi = p + new float3(reach + 2f, 1f, 1f);
                }

                float boxReach = math.cmax(math.max(math.abs(lo), math.abs(hi)));
                bool said = FragmentHitReach.IsBeyond(p, reach, lo, hi, boxReach);
                var gap = math.max(math.max(new double3(lo) - new double3(p), new double3(p) - new double3(hi)), 0.0);
                double distance = Length(gap);
                double margin = 1e-4 * math.max(boxReach, math.cmax(math.abs(p)) + reach) + 1e-4;
                if (said)
                {
                    beyond++;
                    if (distance <= reach + margin * 0.99) Assert.Fail("case " + i + ": said beyond at distance " + distance + " with reach " + reach + " and margin " + margin);
                }
                else
                {
                    within++;
                    if (distance > reach + margin * 1.01) Assert.Fail("case " + i + ": not said beyond at distance " + distance + " with reach " + reach + " and margin " + margin);
                    if (distance > reach) inTheMargin++;
                }

                if (distance <= reach) Assert.That(said, Is.False, "case " + i + ": within the reach, or touching it");
            }

            TestContext.Out.WriteLine("beyond " + beyond + ", not beyond " + within + " (of them outside the reach but inside the margin " + inTheMargin + ")");
            Assert.That(beyond, Is.GreaterThan(3000));
            Assert.That(within, Is.GreaterThan(3000));
        }

        [Test]
        public void NothingIsBeyond_WhenTheNumbersCannotBeVouchedFor()
        {
            float3 lo = new float3(100f, 0f, 0f), hi = new float3(101f, 1f, 1f);
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, 1f, lo, hi, 101f), Is.True, "the ordinary case: far beyond");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, FragmentHitReach.None, lo, hi, 101f), Is.False, "no reach");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, float.NaN, lo, hi, 101f), Is.False, "a reach that is not a number");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, float.PositiveInfinity, lo, hi, 101f), Is.False, "an infinite reach");
            Assert.That(FragmentHitReach.IsBeyond(new float3(float.NaN, 0f, 0f), 1f, lo, hi, 101f), Is.False, "a position that is not a number");
            Assert.That(FragmentHitReach.IsBeyond(new float3(float.PositiveInfinity, 0f, 0f), 1f, lo, hi, 101f), Is.False, "an infinite position");
            Assert.That(FragmentHitReach.IsBeyond(new float3(-1e30f, 0f, 0f), 1f, lo, hi, 101f), Is.False, "a distance whose square overflows");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, 1e25f, new float3(3e38f, 0f, 0f), new float3(3.1e38f, 1f, 1f), 3.1e38f), Is.False, "a reach whose square overflows");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, 1f, new float3(1f, -1f, -1f), new float3(2f, 1f, 1f), 2f), Is.False, "touching");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, 1f, new float3(1.00005f, -1f, -1f), new float3(2f, 1f, 1f), 2f), Is.False, "outside by less than the margin");
            Assert.That(FragmentHitReach.IsBeyond(float3.zero, 1f, new float3(1.001f, -1f, -1f), new float3(2f, 1f, 1f), 2f), Is.True, "outside by more than the margin");
        }
    }
}
