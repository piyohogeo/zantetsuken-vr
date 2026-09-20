using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The principal-axis conversion on its own (<see cref="PrincipalInertia"/>): a symmetric inertia in, three
    /// moments and a rotation out, so that the rotation carries the diagonal back to the matrix it came from.
    /// <para>
    /// The inputs here are built the other way round — from moments and a rotation chosen in advance — so that the
    /// entries really are off the diagonal and the Jacobi rotations are what is being checked, not a diagonal matrix
    /// that comes back unchanged. Which axis each moment lands on, and which way an axis points, is not asked of it.
    /// </para>
    /// </summary>
    public class PrincipalInertiaTests
    {
        /// <summary>
        /// A rotation built in double, from three angles. The axes are made here rather than taken from a
        /// <see cref="quaternion"/> because that is single precision: an input matrix rounded to float would have
        /// principal moments of its own, a ten-millionth away from the ones it was built from, and the moments this
        /// conversion returns would be judged against the wrong numbers.
        /// </summary>
        private static double3x3 Axes(double x, double y, double z)
        {
            var rx = new double3x3(
                1.0, 0.0, 0.0,
                0.0, math.cos(x), -math.sin(x),
                0.0, math.sin(x), math.cos(x));
            var ry = new double3x3(
                math.cos(y), 0.0, math.sin(y),
                0.0, 1.0, 0.0,
                -math.sin(y), 0.0, math.cos(y));
            var rz = new double3x3(
                math.cos(z), -math.sin(z), 0.0,
                math.sin(z), math.cos(z), 0.0,
                0.0, 0.0, 1.0);
            return math.mul(rz, math.mul(ry, rx));
        }

        /// <summary>A symmetric matrix with the given principal moments about the given axes.</summary>
        private static SymmetricMatrix3 From(double3 moments, double3x3 r)
        {
            var diagonal = new double3x3(moments.x, 0.0, 0.0, 0.0, moments.y, 0.0, 0.0, 0.0, moments.z);
            double3x3 m = math.mul(math.mul(r, diagonal), math.transpose(r));
            return new SymmetricMatrix3
            {
                xx = m.c0.x, yy = m.c1.y, zz = m.c2.z, xy = m.c1.x, xz = m.c2.x, yz = m.c2.y,
            };
        }

        private static void AssertRebuilds(in SymmetricMatrix3 expected, double3 moments, quaternion rotation, double tolerance)
        {
            var q = new double3x3(new float3x3(rotation));
            var diagonal = new double3x3(moments.x, 0.0, 0.0, 0.0, moments.y, 0.0, 0.0, 0.0, moments.z);
            double3x3 rebuilt = math.mul(math.mul(q, diagonal), math.transpose(q));
            double3x3 original = expected.ToMatrix();
            for (int c = 0; c < 3; c++)
            {
                for (int r = 0; r < 3; r++)
                {
                    Assert.That(rebuilt[c][r], Is.EqualTo(original[c][r]).Within(tolerance), "entry " + c + "," + r);
                }
            }
        }

        private static double3 Sorted(double3 v)
        {
            double a = math.min(v.x, math.min(v.y, v.z));
            double c = math.max(v.x, math.max(v.y, v.z));
            return new double3(a, (v.x + v.y + v.z) - a - c, c);
        }

        /// <summary>
        /// Three different moments about axes that are not the frame's own: the conversion gives those moments back,
        /// and its rotation carries them to the matrix it was given.
        /// </summary>
        [Test]
        public void AnInertiaWithOffDiagonalEntries_IsCarriedBackByItsOwnRotation()
        {
            var moments = new double3(1.0, 2.5, 4.0);
            SymmetricMatrix3 inertia = From(moments, Axes(0.4, -0.9, 1.3));
            Assert.That(
                math.abs(inertia.xy) + math.abs(inertia.xz) + math.abs(inertia.yz), Is.GreaterThan(0.1),
                "the input really is off the diagonal");

            Assert.That(PrincipalInertia.TryDiagonalize(in inertia, out double3 found, out quaternion rotation), Is.True);
            Assert.That(
                Sorted(found).x, Is.EqualTo(Sorted(moments).x).Within(1e-9), "the smallest moment");
            Assert.That(
                Sorted(found).y, Is.EqualTo(Sorted(moments).y).Within(1e-9), "the middle one");
            Assert.That(
                Sorted(found).z, Is.EqualTo(Sorted(moments).z).Within(1e-9), "the largest");
            AssertRebuilds(in inertia, found, rotation, 1e-5);
        }

        /// <summary>
        /// A second one, with moments closer together and a different rotation, since how far apart they are is what
        /// the sweeps work on.
        /// </summary>
        [Test]
        public void AnInertiaWithNearlyEqualMoments_IsCarriedBackToo()
        {
            var moments = new double3(3.0, 3.05, 7.0);
            SymmetricMatrix3 inertia = From(moments, Axes(-1.1, 0.6, 2.2));
            Assert.That(
                math.abs(inertia.xy) + math.abs(inertia.xz) + math.abs(inertia.yz), Is.GreaterThan(0.1),
                "the input really is off the diagonal");

            Assert.That(PrincipalInertia.TryDiagonalize(in inertia, out double3 found, out quaternion rotation), Is.True);
            Assert.That(Sorted(found).x, Is.EqualTo(Sorted(moments).x).Within(1e-9));
            Assert.That(Sorted(found).y, Is.EqualTo(Sorted(moments).y).Within(1e-9));
            Assert.That(Sorted(found).z, Is.EqualTo(Sorted(moments).z).Within(1e-9));
            AssertRebuilds(in inertia, found, rotation, 1e-5);
        }

        /// <summary>The small control: an inertia that is the same about every axis comes back as it is.</summary>
        [Test]
        public void AnIsotropicInertia_ComesBackAsItIs()
        {
            var inertia = new SymmetricMatrix3 { xx = 2.0, yy = 2.0, zz = 2.0 };
            Assert.That(PrincipalInertia.TryDiagonalize(in inertia, out double3 found, out quaternion rotation), Is.True);
            Assert.That(found.x, Is.EqualTo(2.0).Within(1e-12));
            Assert.That(found.y, Is.EqualTo(2.0).Within(1e-12));
            Assert.That(found.z, Is.EqualTo(2.0).Within(1e-12));
            Assert.That(math.all(math.isfinite(rotation.value)), Is.True, "with a rotation the solver can take");
            AssertRebuilds(in inertia, found, rotation, 1e-9);
        }
    }
}
