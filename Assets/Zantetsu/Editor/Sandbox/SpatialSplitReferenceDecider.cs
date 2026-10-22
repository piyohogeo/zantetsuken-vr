using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>What the reference decider says of one captured input.</summary>
    internal enum ReferenceVerdict
    {
        /// <summary>The elements can be put on two sides, each with a convex and a triangle, no element cut, the margin kept.</summary>
        Separable = 0,

        /// <summary>They cannot: every way of placing the connected components leaves a side without a convex or without a triangle.</summary>
        NotSeparable,

        /// <summary>The time or memory limit was reached before every pair was judged: nothing is said either way.</summary>
        Undecided,

        /// <summary>The input does not hold together (a value not finite, an index out of range, a degenerate convex): nothing is said.</summary>
        InvalidInput,
    }

    /// <summary>One decision, with what it was made of and what it cost.</summary>
    internal sealed class ReferenceDecision
    {
        public ReferenceVerdict verdict;
        public string reason = "";
        public int scale;
        public int convexCount, triangleCount, degenerateTriangles;
        public int componentCount, fullComponents, convexOnlyComponents, triangleOnlyComponents;

        /// <summary>Separable only: the side of each convex and each triangle (true: positive), and the sides' sizes.</summary>
        public bool[] convexPositive, trianglePositive;
        public int positiveConvexes, positiveTriangles, negativeConvexes, negativeTriangles;

        /// <summary>
        /// Pairs: every pair of elements; those the boxes put apart; those an axis (a face's normal) put apart; those joined by two
        /// vertices nearer than 2m; those joined by a vertex inside a convex; those that went to the exact judgement
        /// (whose features are still filtered one by one in double); those joined in all.
        /// </summary>
        public long allPairs, pairsFarByBoxes, pairsFarByAxes, pairsNearByVertices, pairsNearByInside, pairsExact, pairsConnected;

        /// <summary>Whether the double certificates were used at all (false: every pair and feature judged exactly).</summary>
        public bool filters;
        public double bound;

        /// <summary>Feature tests: those decided in double (clearly beyond or clearly within), and those decided exactly.</summary>
        public long featuresDouble, featuresExact;
        public double seconds;
        public long peakManagedBytes;
    }

    /// <summary>
    /// The offline reference decider of the additional split (TL 2026-10-21): whether a captured input -- its convexes
    /// as closed volumes, its triangles as the surfaces they are -- can be put on two sides with no element cut, each
    /// side holding a convex and a triangle, every element of one side at least the capture's margin <c>m</c> from every
    /// element of the other (so that a boundary with the margin on both sides fits between them). It is an existence
    /// question about any boundary at all: the shape of the product's surfaces (one connected graph h = f(u) carried
    /// along an axis, its pieces, its frames) is not asked, and a separable input may need several boundaries or one no
    /// such graph can be. It says nothing of anchors, masses, room or publication.
    /// <para>
    /// **The rule.** Two elements are joined when they intersect, touch, or lie nearer than 2m to each other (at exactly
    /// 2m they are not joined); joined elements must share a side. The connected components of that graph are placed
    /// whole; the input is separable when the components can be dealt to two sides each holding a convex and a triangle.
    /// The placement returned is deterministic in the elements' order (convexes in the shape's order, then triangles in
    /// the index order): a component with both kinds goes to the positive side alone where there are two or more; else
    /// the first convex-only and the first triangle-only component do; the rest is the negative side.
    /// </para>
    /// <para>
    /// **The numbers.** Every saved value (a vertex, a matrix entry, the margin) is a binary float and is read as the
    /// exact rational it is. Each element's vertices are carried to the owner's frame exactly (<c>M·p</c> as sums of
    /// exact products), and every coordinate is scaled by one power of two 2^S chosen so that all of them, and 2m, are
    /// integers; from there every exact test is a sign of an integer polynomial (System.Numerics.BigInteger): a point in
    /// a closed convex volume by its faces' outward planes (orientation from the exact centroid), a segment meeting a
    /// closed polygon (the coplanar case by orientation tests), and a squared distance against (2m)² by cross
    /// multiplication -- point to segment, point to polygon, segment to segment at their interiors. Nothing is rounded
    /// and no tolerance is added in them.
    /// </para>
    /// <para>
    /// **Where double decides.** A test is made in double precision only with a certificate whose error is bounded: with
    /// U = max(1, the largest owner coordinate magnitude, the largest term of a transform), a computed length (a coordinate,
    /// a projection onto a near-unit axis, a plane distance, a point's distance to a point on a feature) is within
    /// E1 = 2^-44 U of the true one, and a side test (a dot of a normal with a cross of differences) within E2 = 2^-40 U²
    /// -- far above what double arithmetic on floats carried to the owner's frame can err by (about 2^-50 U and 2^-46 U²).
    /// Not joined for sure: the two boxes, or the projections of both elements' vertices onto some axis, are apart by more
    /// than 2m + 4E1 (the axes are the faces' normals, each taken from the exact normal; any direction would do, the
    /// certificate is the projection gap itself), or a feature pair's lower bound (a plane distance, a box gap) less 2E1
    /// is above 2m. Joined for sure: two vertices are nearer than 2m − 4E1, a vertex lies inside a volume by more than
    /// 2E1 at every face (the face planes from the exact normals), or a feature pair's upper bound (the distance to a
    /// point on the feature; the plane distance only where the projection is inside the polygon by more than E2) plus
    /// 2E1 is under 2m. Everything else -- a vertex on or near a face, an edge crossing a face, a distance within 4E1 of
    /// 2m, a projection near a polygon's edge -- is judged exactly. Nothing a double normal computed from positions could
    /// get wrong is a certificate: the normals come from the exact ones (TL 2026-10-22, the tiny far triangle). The
    /// filters can be switched off, and then every pair and feature is judged exactly; the pair counts say which path.
    /// </para>
    /// <para>
    /// A triangle of no area is a segment of its two farthest vertices, or a point; it is never dropped. A convex whose
    /// face has no normal, or whose centroid lies on a face's plane, is an invalid input, as is a value that is not
    /// finite or an index the sample does not hold. Past the time limit the verdict is Undecided, never NotSeparable.
    /// </para>
    /// </summary>
    internal static class SpatialSplitReferenceDecider
    {
        public const string Version = "reference-decider 3 (2026-10-22)";

        public const string Numeric =
            "exact: saved floats as dyadic rationals; owner coordinates = M*p exactly; all coordinates scaled by 2^S to integers; "
            + "joined iff intersect, touch, or dist^2 < (2m)^2 strictly (BigInteger sign tests); double decides a test only with a "
            + "certificate whose error bound is E1 = 2^-44*U for lengths and E2 = 2^-40*U^2 for side tests, U = max(1, |coordinate|, "
            + "|transform term|): box or axis-projection gap > 2m + 4E1 (axes from the exact normals), vertices nearer than 2m - 4E1, "
            + "a vertex inside a volume by more than 2E1 at every face, feature lower bounds (plane distance, boxes) - 2E1 > 2m, feature "
            + "upper bounds (points on the features, a projection inside by more than E2) + 2E1 < 2m";

        // ---- exact points -------------------------------------------------------------------------------------------------

        internal struct XPoint
        {
            public BigInteger x, y, z;

            public XPoint(BigInteger x, BigInteger y, BigInteger z)
            {
                this.x = x;
                this.y = y;
                this.z = z;
            }

            public static XPoint operator -(XPoint a, XPoint b) => new XPoint(a.x - b.x, a.y - b.y, a.z - b.z);
            public static XPoint operator +(XPoint a, XPoint b) => new XPoint(a.x + b.x, a.y + b.y, a.z + b.z);
            public static XPoint operator -(XPoint a) => new XPoint(-a.x, -a.y, -a.z);
            public static XPoint operator *(XPoint a, BigInteger k) => new XPoint(a.x * k, a.y * k, a.z * k);
            public static BigInteger Dot(XPoint a, XPoint b) => (a.x * b.x) + (a.y * b.y) + (a.z * b.z);
            public static XPoint Cross(XPoint a, XPoint b) => new XPoint((a.y * b.z) - (a.z * b.y), (a.z * b.x) - (a.x * b.z), (a.x * b.y) - (a.y * b.x));
            public bool IsZero => x.IsZero && y.IsZero && z.IsZero;
            public BigInteger LengthSq => Dot(this, this);
            public bool Same(XPoint o) => x == o.x && y == o.y && z == o.z;
            public double3 ToDouble() => new double3((double)x, (double)y, (double)z);
        }

        /// <summary>One element in the owner's frame: a closed convex volume, or a triangle (a polygon, a segment or a point).</summary>
        internal sealed class XShape
        {
            public XPoint[] V;
            public int[][] Faces = Array.Empty<int[]>();
            public XPoint[] Normals = Array.Empty<XPoint>();   // a solid's outward, a triangle's as computed
            public (int a, int b)[] Edges = Array.Empty<(int, int)>();
            public bool Solid;

            // The double picture: the vertices, each face's unit normal (the exact one's direction) and offset, the box.
            public double3[] D;
            public double3[] DN = Array.Empty<double3>();
            public double[] DO = Array.Empty<double>();
            public double3 boxMin, boxMax;

            // The exact box (at the scale), for the exact-only sweep.
            public XPoint xMin, xMax;
        }

        // The thresholds of one decision.
        private struct Ctx
        {
            public BigInteger t;         // (2m)^2 at the scale
            public double m2, e1, e2;    // 2m, and the bounds on a computed length and on a side test
            public double far, near;     // 2m + 4E1, 2m - 4E1
            public bool filters;         // false: every pair and feature exactly
        }

        // ---- the floats as exact numbers -----------------------------------------------------------------------------------

        // f = mantissa * 2^exponent exactly, the mantissa odd (or zero, with exponent 0).
        private static void Decompose(float f, out long mantissa, out int exponent)
        {
            int bits = BitConverter.SingleToInt32Bits(f);
            int e = (bits >> 23) & 0xFF;
            int m = bits & 0x7FFFFF;
            if (e == 0xFF) throw new ArgumentException("not finite");
            long mant = e == 0 ? m : m | (1 << 23);
            int exp = e == 0 ? -149 : e - 150;
            if (mant == 0)
            {
                mantissa = 0;
                exponent = 0;
                return;
            }

            while ((mant & 1) == 0)
            {
                mant >>= 1;
                exp++;
            }

            mantissa = bits < 0 ? -mant : mant;
            exponent = exp;
        }

        private static int LeastExponent(float f, int least)
        {
            Decompose(f, out long m, out int e);
            return m == 0 ? least : Math.Min(least, e);
        }

        private static BigInteger Scaled(float f, int scale)
        {
            Decompose(f, out long m, out int e);
            if (m == 0) return BigInteger.Zero;
            int shift = scale + e;
            if (shift < 0) throw new InvalidOperationException("the scale does not hold " + f.ToString("R", CultureInfo.InvariantCulture));
            return new BigInteger(m) << shift;
        }

        private static BigInteger ScaledProduct(float f, float g, int scale)
        {
            Decompose(f, out long mf, out int ef);
            Decompose(g, out long mg, out int eg);
            if (mf == 0 || mg == 0) return BigInteger.Zero;
            int shift = scale + ef + eg;
            if (shift < 0) throw new InvalidOperationException("the scale does not hold a product");
            return (new BigInteger(mf) * mg) << shift;
        }

        internal static float Entry(in float4x4 m, int row, int column) => m[column][row];

        // M * p in the owner's frame, exactly, at the scale.
        private static XPoint Owner(float3 p, in float4x4 m, int scale)
        {
            var r = new BigInteger[3];
            for (int i = 0; i < 3; i++)
            {
                r[i] = ScaledProduct(Entry(m, i, 0), p.x, scale) + ScaledProduct(Entry(m, i, 1), p.y, scale)
                    + ScaledProduct(Entry(m, i, 2), p.z, scale) + Scaled(Entry(m, i, 3), scale);
            }

            return new XPoint(r[0], r[1], r[2]);
        }

        internal static double3 OwnerDouble(float3 p, in float4x4 m)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++)
            {
                r[i] = ((double)Entry(m, i, 0) * p.x) + ((double)Entry(m, i, 1) * p.y) + ((double)Entry(m, i, 2) * p.z) + Entry(m, i, 3);
            }

            return new double3(r[0], r[1], r[2]);
        }

        internal static bool Finite(in float4x4 m)
        {
            for (int c = 0; c < 4; c++)
            {
                if (!math.all(math.isfinite(m[c]))) return false;
            }

            return true;
        }

        // ---- exact predicates ---------------------------------------------------------------------------------------------

        private static bool Less(BigInteger lhs, BigInteger rhs, bool orEqual) => orEqual ? lhs <= rhs : lhs < rhs;

        private static int Orient(XPoint n, XPoint p, XPoint q, XPoint r) => XPoint.Dot(n, XPoint.Cross(q - p, r - p)).Sign;

        // Whether the sides of p against every edge of the polygon (a point in its plane, or one given scaled by k > 0
        // against vertices scaled by the same k) agree: inside or on the boundary of a convex polygon.
        private static bool InPolygon(XShape s, int f, XPoint p, BigInteger k)
        {
            int[] loop = s.Faces[f];
            XPoint n = s.Normals[f];
            int positive = 0, negative = 0;
            for (int i = 0; i < loop.Length; i++)
            {
                XPoint vi = s.V[loop[i]], vj = s.V[loop[(i + 1) % loop.Length]];
                int side = XPoint.Dot(n, XPoint.Cross(vj - vi, p - (vi * k))).Sign;
                if (side > 0) positive++;
                else if (side < 0) negative++;
                if (positive > 0 && negative > 0) return false;
            }

            return true;
        }

        private static bool OnSegment(XPoint a, XPoint b, XPoint p)
        {
            XPoint e = b - a;
            XPoint w = p - a;
            if (!XPoint.Cross(e, w).IsZero) return false;
            return XPoint.Dot(w, e).Sign >= 0 && XPoint.Dot(p - b, a - b).Sign >= 0;
        }

        private static bool Inside(XPoint p, XShape q)
        {
            if (q.Solid)
            {
                for (int f = 0; f < q.Faces.Length; f++)
                {
                    if (XPoint.Dot(q.Normals[f], p - q.V[q.Faces[f][0]]).Sign > 0) return false;
                }

                return true;
            }

            if (q.Faces.Length > 0)
            {
                return XPoint.Dot(q.Normals[0], p - q.V[q.Faces[0][0]]).IsZero && InPolygon(q, 0, p, BigInteger.One);
            }

            if (q.Edges.Length > 0)
            {
                return OnSegment(q.V[q.Edges[0].a], q.V[q.Edges[0].b], p);
            }

            return p.Same(q.V[0]);
        }

        private static bool CoplanarSegmentsMeet(XPoint n, XPoint a, XPoint b, XPoint c, XPoint d)
        {
            int o1 = Orient(n, a, b, c), o2 = Orient(n, a, b, d), o3 = Orient(n, c, d, a), o4 = Orient(n, c, d, b);
            if (o1 * o2 < 0 && o3 * o4 < 0) return true;
            if (o1 == 0 && OnSegment(a, b, c)) return true;
            if (o2 == 0 && OnSegment(a, b, d)) return true;
            if (o3 == 0 && OnSegment(c, d, a)) return true;
            if (o4 == 0 && OnSegment(c, d, b)) return true;
            return false;
        }

        // The closed segment ab meets the closed polygon f of s.
        private static bool SegmentMeetsPolygon(XPoint a, XPoint b, XShape s, int f)
        {
            int[] loop = s.Faces[f];
            XPoint n = s.Normals[f];
            XPoint v0 = s.V[loop[0]];
            BigInteger ha = XPoint.Dot(n, a - v0), hb = XPoint.Dot(n, b - v0);
            if ((ha.Sign > 0 && hb.Sign > 0) || (ha.Sign < 0 && hb.Sign < 0)) return false;
            if (ha.IsZero && hb.IsZero)
            {
                if (InPolygon(s, f, a, BigInteger.One) || InPolygon(s, f, b, BigInteger.One)) return true;
                for (int i = 0; i < loop.Length; i++)
                {
                    if (CoplanarSegmentsMeet(n, a, b, s.V[loop[i]], s.V[loop[(i + 1) % loop.Length]])) return true;
                }

                return false;
            }

            // The crossing point x = a + (b - a) * ha / (ha - hb), as X / D with D > 0.
            BigInteger d = ha - hb;
            XPoint x = (a * d) + ((b - a) * ha);
            if (d.Sign < 0)
            {
                d = -d;
                x = -x;
            }

            return InPolygon(s, f, x, d);
        }

        private static bool PointSegmentWithin(XPoint p, XPoint a, XPoint b, BigInteger t, bool orEqual)
        {
            XPoint e = b - a;
            XPoint w = p - a;
            BigInteger l = e.LengthSq;
            if (l.IsZero) return Less(w.LengthSq, t, orEqual);
            BigInteger d = XPoint.Dot(w, e);
            if (d.Sign <= 0) return Less(w.LengthSq, t, orEqual);
            if (d >= l) return Less((p - b).LengthSq, t, orEqual);
            return Less((w.LengthSq * l) - (d * d), t * l, orEqual);
        }

        private static bool PointPolygonWithin(XPoint p, XShape s, int f, BigInteger t, bool orEqual)
        {
            int[] loop = s.Faces[f];
            XPoint n = s.Normals[f];
            XPoint v0 = s.V[loop[0]];
            BigInteger h = XPoint.Dot(n, p - v0);
            BigInteger nn = n.LengthSq;
            // The projection q = p - n * h / nn, as Q / nn.
            XPoint q = (p * nn) - (n * h);
            if (InPolygon(s, f, q, nn)) return Less(h * h, t * nn, orEqual);
            for (int i = 0; i < loop.Length; i++)
            {
                if (PointSegmentWithin(p, s.V[loop[i]], s.V[loop[(i + 1) % loop.Length]], t, orEqual)) return true;
            }

            return false;
        }

        private static bool SegmentsWithin(XPoint a1, XPoint b1, XPoint a2, XPoint b2, BigInteger t, bool orEqual)
        {
            if (PointSegmentWithin(a1, a2, b2, t, orEqual) || PointSegmentWithin(b1, a2, b2, t, orEqual)
                || PointSegmentWithin(a2, a1, b1, t, orEqual) || PointSegmentWithin(b2, a1, b1, t, orEqual))
            {
                return true;
            }

            XPoint e1 = b1 - a1, e2 = b2 - a2;
            XPoint nn = XPoint.Cross(e1, e2);
            BigInteger dd = nn.LengthSq;
            if (dd.IsZero) return false;   // parallel: the nearest points are at ends, judged above
            XPoint r = a1 - a2;
            BigInteger a = e1.LengthSq, b = XPoint.Dot(e1, e2), c = XPoint.Dot(e1, r), e = e2.LengthSq, fr = XPoint.Dot(e2, r);
            BigInteger sNum = (b * fr) - (c * e), tNum = (a * fr) - (b * c);
            if (sNum.Sign <= 0 || sNum >= dd || tNum.Sign <= 0 || tNum >= dd) return false;
            BigInteger q = XPoint.Dot(r, nn);
            return Less(q * q, t * dd, orEqual);
        }

        // ---- the double picture --------------------------------------------------------------------------------------------

        // Every double quantity below is a bound of a certificate, never a decision by itself: a length is used with E1 of
        // slack, a side test with E2, and the bounds are from the structure of the formulas (points on features bound the
        // distance from above; a plane distance or a box gap bounds it from below).

        private static double3 BoxMin(XShape s, int[] loop)
        {
            var lo = new double3(double.PositiveInfinity);
            foreach (int i in loop) lo = math.min(lo, s.D[i]);
            return lo;
        }

        private static double3 BoxMax(XShape s, int[] loop)
        {
            var hi = new double3(double.NegativeInfinity);
            foreach (int i in loop) hi = math.max(hi, s.D[i]);
            return hi;
        }

        private static double BoxGap(double3 lo1, double3 hi1, double3 lo2, double3 hi2)
        {
            double3 g = math.max(math.max(lo2 - hi1, lo1 - hi2), 0.0);
            return math.length(g);
        }

        private static double PointBoxGap(double3 p, double3 lo, double3 hi) => math.length(math.max(math.max(lo - p, p - hi), 0.0));

        // The distance from p to a point of the segment ab (the clamped projection): an upper bound of the true distance.
        private static double PointSegmentD(double3 p, double3 a, double3 b)
        {
            double3 e = b - a, w = p - a;
            double l = math.lengthsq(e);
            if (l <= 0.0) return math.length(w);
            double s = math.clamp(math.dot(w, e) / l, 0.0, 1.0);
            return math.distance(p, a + (e * s));
        }

        // Whether the projection q lies inside the polygon f of s by more than E2 at every edge (the sides all one sign).
        private static bool InsideByMargin(XShape s, int f, double3 q, double e2)
        {
            int[] loop = s.Faces[f];
            double3 n = s.DN[f];
            int positive = 0, negative = 0;
            for (int i = 0; i < loop.Length; i++)
            {
                double3 vi = s.D[loop[i]], vj = s.D[loop[(i + 1) % loop.Length]];
                double side = math.dot(n, math.cross(vj - vi, q - vi));
                if (side > e2) positive++;
                else if (side < -e2) negative++;
                else return false;
            }

            return positive == 0 || negative == 0;
        }

        // Bounds on the distance from p to the closed polygon f of s: a lower one (the plane distance, and the gap to the
        // polygon's box), and an upper one (the plane distance where the projection is inside by E2, else the least
        // distance to a point on an edge). Both before the slack E1.
        private static void PointPolygonBounds(double3 p, XShape s, int f, double e2, out double lower, out double upper)
        {
            int[] loop = s.Faces[f];
            double3 n = s.DN[f];
            double h = math.dot(n, p) - s.DO[f];
            lower = Math.Max(Math.Abs(h), PointBoxGap(p, BoxMin(s, loop), BoxMax(s, loop)));
            double3 q = p - (n * h);
            if (InsideByMargin(s, f, q, e2))
            {
                upper = Math.Abs(h);
                return;
            }

            upper = double.PositiveInfinity;
            for (int i = 0; i < loop.Length; i++) upper = Math.Min(upper, PointSegmentD(p, s.D[loop[i]], s.D[loop[(i + 1) % loop.Length]]));
        }

        // The distance between a point of each segment (the clamped nearest points): an upper bound of the true distance.
        private static double SegmentsD(double3 p1, double3 q1, double3 p2, double3 q2)
        {
            double3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = math.dot(d1, d1), e = math.dot(d2, d2), f = math.dot(d2, r);
            double s, tt;
            if (a <= 0.0 && e <= 0.0) return math.distance(p1, p2);
            if (a <= 0.0)
            {
                s = 0.0;
                tt = math.clamp(f / e, 0.0, 1.0);
            }
            else
            {
                double c = math.dot(d1, r);
                if (e <= 0.0)
                {
                    tt = 0.0;
                    s = math.clamp(-c / a, 0.0, 1.0);
                }
                else
                {
                    double b = math.dot(d1, d2), denom = (a * e) - (b * b);
                    s = denom > 0.0 ? math.clamp(((b * f) - (c * e)) / denom, 0.0, 1.0) : 0.0;
                    tt = ((b * s) + f) / e;
                    if (tt < 0.0)
                    {
                        tt = 0.0;
                        s = math.clamp(-c / a, 0.0, 1.0);
                    }
                    else if (tt > 1.0)
                    {
                        tt = 1.0;
                        s = math.clamp((b - c) / a, 0.0, 1.0);
                    }
                }
            }

            return math.distance(p1 + (d1 * s), p2 + (d2 * tt));
        }

        private static double SegmentsBoxGap(double3 a1, double3 b1, double3 a2, double3 b2) =>
            BoxGap(math.min(a1, b1), math.max(a1, b1), math.min(a2, b2), math.max(a2, b2));

        // The projections of both elements' vertices onto a near-unit axis are apart by more than 2m + 4E1: not joined, for
        // sure, whatever the axis's direction (the projection gap is a lower bound of the distance for any unit axis, and the
        // axis is unit within 2^-50).
        private static bool ApartAlongAxis(double3 axis, XShape p, XShape q, in Ctx c)
        {
            double pLo = double.PositiveInfinity, pHi = double.NegativeInfinity, qLo = double.PositiveInfinity, qHi = double.NegativeInfinity;
            foreach (double3 v in p.D)
            {
                double x = math.dot(axis, v);
                pLo = Math.Min(pLo, x);
                pHi = Math.Max(pHi, x);
            }

            foreach (double3 v in q.D)
            {
                double x = math.dot(axis, v);
                qLo = Math.Min(qLo, x);
                qHi = Math.Max(qHi, x);
            }

            return Math.Max(qLo - pHi, pLo - qHi) > c.far;
        }

        private static bool ApartAlongAFaceAxis(XShape p, XShape q, in Ctx c)
        {
            foreach (double3 axis in p.DN)
            {
                if (ApartAlongAxis(axis, p, q, in c)) return true;
            }

            foreach (double3 axis in q.DN)
            {
                if (ApartAlongAxis(axis, p, q, in c)) return true;
            }

            return false;
        }

        // A vertex of 'other' lies inside the volume by more than 2E1 at every face: they intersect, for sure.
        private static bool AVertexClearlyInside(XShape volume, XShape other, in Ctx c)
        {
            foreach (double3 v in other.D)
            {
                bool inside = true;
                for (int f = 0; f < volume.Faces.Length && inside; f++)
                {
                    inside = math.dot(volume.DN[f], v) - volume.DO[f] < -2.0 * c.e1;
                }

                if (inside) return true;
            }

            return false;
        }

        // Whether a vertex could be inside or on the volume (not clearly outside at any face).
        private static bool MaybeInside(XShape volume, double3 v, in Ctx c)
        {
            for (int f = 0; f < volume.Faces.Length; f++)
            {
                if (math.dot(volume.DN[f], v) - volume.DO[f] > 2.0 * c.e1) return false;
            }

            return true;
        }

        // ---- the pair ----------------------------------------------------------------------------------------------------

        // Whether p and q are joined. With the filters, double decides where a certificate holds; the rest is exact.
        private static bool Joined(XShape p, XShape q, in Ctx c, ReferenceDecision d, out string path)
        {
            if (!c.filters)
            {
                path = "exact";
                return JoinedExact(p, q, c.t);
            }

            if ((p.Solid && AVertexClearlyInside(p, q, in c)) || (q.Solid && AVertexClearlyInside(q, p, in c)))
            {
                path = "inside";
                return true;
            }

            if (ApartAlongAFaceAxis(p, q, in c))
            {
                path = "axes";
                return false;
            }

            path = "exact";
            return IntersectFiltered(p, q, in c, d) || NearFiltered(p, q, in c, d) || NearFiltered(q, p, in c, d) || EdgesNearFiltered(p, q, in c, d);
        }

        /// <summary>The exact rule alone: no double, every feature judged exactly.</summary>
        internal static bool JoinedExact(XShape p, XShape q, BigInteger t)
        {
            if (IntersectExact(p, q)) return true;
            foreach (XPoint v in p.V)
            {
                if (PointShapeWithinExact(v, q, t)) return true;
            }

            foreach (XPoint v in q.V)
            {
                if (PointShapeWithinExact(v, p, t)) return true;
            }

            foreach ((int a, int b) in p.Edges)
            {
                foreach ((int cc, int dd) in q.Edges)
                {
                    if (SegmentsWithin(p.V[a], p.V[b], q.V[cc], q.V[dd], t, false)) return true;
                }
            }

            return false;
        }

        private static bool PointShapeWithinExact(XPoint v, XShape q, BigInteger t)
        {
            if (q.Faces.Length > 0)
            {
                for (int f = 0; f < q.Faces.Length; f++)
                {
                    if (PointPolygonWithin(v, q, f, t, false)) return true;
                }

                return false;
            }

            if (q.Edges.Length > 0) return PointSegmentWithin(v, q.V[q.Edges[0].a], q.V[q.Edges[0].b], t, false);
            return Less((v - q.V[0]).LengthSq, t, false);
        }

        internal static bool IntersectExact(XShape p, XShape q)
        {
            foreach (XPoint v in p.V)
            {
                if (Inside(v, q)) return true;
            }

            foreach (XPoint v in q.V)
            {
                if (Inside(v, p)) return true;
            }

            foreach ((int a, int b) in p.Edges)
            {
                for (int f = 0; f < q.Faces.Length; f++)
                {
                    if (SegmentMeetsPolygon(p.V[a], p.V[b], q, f)) return true;
                }
            }

            foreach ((int a, int b) in q.Edges)
            {
                for (int f = 0; f < p.Faces.Length; f++)
                {
                    if (SegmentMeetsPolygon(q.V[a], q.V[b], p, f)) return true;
                }
            }

            if (p.Faces.Length == 0 || q.Faces.Length == 0)
            {
                foreach ((int a, int b) in p.Edges)
                {
                    foreach ((int cc, int dd) in q.Edges)
                    {
                        if (SegmentsWithin(p.V[a], p.V[b], q.V[cc], q.V[dd], BigInteger.Zero, true)) return true;
                    }
                }
            }

            return false;
        }

        private static bool IntersectFiltered(XShape p, XShape q, in Ctx c, ReferenceDecision d)
        {
            if (VertexInsideFiltered(p, q, in c, d) || VertexInsideFiltered(q, p, in c, d)) return true;
            if (EdgesMeetFacesFiltered(p, q, in c, d) || EdgesMeetFacesFiltered(q, p, in c, d)) return true;
            if (p.Faces.Length == 0 || q.Faces.Length == 0)
            {
                foreach ((int a, int b) in p.Edges)
                {
                    foreach ((int cc, int dd) in q.Edges)
                    {
                        // Apart by their boxes beyond the slack: they cannot touch.
                        if (SegmentsBoxGap(p.D[a], p.D[b], q.D[cc], q.D[dd]) > 2.0 * c.e1)
                        {
                            d.featuresDouble++;
                            continue;
                        }

                        d.featuresExact++;
                        if (SegmentsWithin(p.V[a], p.V[b], q.V[cc], q.V[dd], BigInteger.Zero, true)) return true;
                    }
                }
            }

            return false;
        }

        // A vertex of p inside q (closed), exactly, for the vertices not clearly outside.
        private static bool VertexInsideFiltered(XShape p, XShape q, in Ctx c, ReferenceDecision d)
        {
            double slack = 2.0 * c.e1;
            for (int i = 0; i < p.V.Length; i++)
            {
                double3 v = p.D[i];
                bool maybe;
                if (q.Solid)
                {
                    maybe = MaybeInside(q, v, in c);
                }
                else if (q.Faces.Length > 0)
                {
                    int[] loop = q.Faces[0];
                    maybe = Math.Abs(math.dot(q.DN[0], v) - q.DO[0]) <= slack && PointBoxGap(v, BoxMin(q, loop), BoxMax(q, loop)) <= slack;
                }
                else if (q.Edges.Length > 0)
                {
                    maybe = PointBoxGap(v, math.min(q.D[q.Edges[0].a], q.D[q.Edges[0].b]), math.max(q.D[q.Edges[0].a], q.D[q.Edges[0].b])) <= slack;
                }
                else
                {
                    maybe = math.distance(v, q.D[0]) <= slack;
                }

                if (!maybe)
                {
                    d.featuresDouble++;
                    continue;
                }

                d.featuresExact++;
                if (Inside(p.V[i], q)) return true;
            }

            return false;
        }

        // An edge of p meets a face of q (closed), exactly, for the edges not clearly on one side of the face's plane.
        private static bool EdgesMeetFacesFiltered(XShape p, XShape q, in Ctx c, ReferenceDecision d)
        {
            double slack = 2.0 * c.e1;
            foreach ((int a, int b) in p.Edges)
            {
                for (int f = 0; f < q.Faces.Length; f++)
                {
                    double ha = math.dot(q.DN[f], p.D[a]) - q.DO[f], hb = math.dot(q.DN[f], p.D[b]) - q.DO[f];
                    if ((ha > slack && hb > slack) || (ha < -slack && hb < -slack))
                    {
                        d.featuresDouble++;
                        continue;
                    }

                    d.featuresExact++;
                    if (SegmentMeetsPolygon(p.V[a], p.V[b], q, f)) return true;
                }
            }

            return false;
        }

        // A vertex of p nearer than 2m to q: by the double bounds where they decide, else exactly.
        private static bool NearFiltered(XShape p, XShape q, in Ctx c, ReferenceDecision d)
        {
            double slack = 2.0 * c.e1;
            for (int i = 0; i < p.V.Length; i++)
            {
                double3 v = p.D[i];
                if (q.Faces.Length > 0)
                {
                    for (int f = 0; f < q.Faces.Length; f++)
                    {
                        PointPolygonBounds(v, q, f, c.e2, out double lower, out double upper);
                        if (lower - slack > c.m2)
                        {
                            d.featuresDouble++;
                            continue;
                        }

                        if (upper + slack < c.m2)
                        {
                            d.featuresDouble++;
                            return true;
                        }

                        d.featuresExact++;
                        if (PointPolygonWithin(p.V[i], q, f, c.t, false)) return true;
                    }
                }
                else if (q.Edges.Length > 0)
                {
                    double3 a = q.D[q.Edges[0].a], b = q.D[q.Edges[0].b];
                    double lower = PointBoxGap(v, math.min(a, b), math.max(a, b));
                    if (lower - slack > c.m2) { d.featuresDouble++; continue; }
                    if (PointSegmentD(v, a, b) + slack < c.m2) { d.featuresDouble++; return true; }
                    d.featuresExact++;
                    if (PointSegmentWithin(p.V[i], q.V[q.Edges[0].a], q.V[q.Edges[0].b], c.t, false)) return true;
                }
                else
                {
                    double dist = math.distance(v, q.D[0]);
                    if (dist - slack > c.m2) { d.featuresDouble++; continue; }
                    if (dist + slack < c.m2) { d.featuresDouble++; return true; }
                    d.featuresExact++;
                    if (Less((p.V[i] - q.V[0]).LengthSq, c.t, false)) return true;
                }
            }

            return false;
        }

        // An edge of p nearer than 2m to an edge of q at their interiors (the ends are the vertices' business above).
        private static bool EdgesNearFiltered(XShape p, XShape q, in Ctx c, ReferenceDecision d)
        {
            double slack = 2.0 * c.e1;
            foreach ((int a, int b) in p.Edges)
            {
                foreach ((int cc, int dd) in q.Edges)
                {
                    if (SegmentsBoxGap(p.D[a], p.D[b], q.D[cc], q.D[dd]) - slack > c.m2)
                    {
                        d.featuresDouble++;
                        continue;
                    }

                    if (SegmentsD(p.D[a], p.D[b], q.D[cc], q.D[dd]) + slack < c.m2)
                    {
                        d.featuresDouble++;
                        return true;   // two points of the segments bound the distance from above
                    }

                    d.featuresExact++;
                    if (SegmentsWithin(p.V[a], p.V[b], q.V[cc], q.V[dd], c.t, false)) return true;
                }
            }

            return false;
        }

        // ---- the elements of a sample -----------------------------------------------------------------------------------

        private static XShape Triangle(XPoint a, XPoint b, XPoint c, double3 da, double3 db, double3 dc, out bool degenerate)
        {
            XPoint n = XPoint.Cross(b - a, c - a);
            var s = new XShape();
            degenerate = n.IsZero;
            if (!degenerate)
            {
                s.V = new[] { a, b, c };
                s.D = new[] { da, db, dc };
                s.Faces = new[] { new[] { 0, 1, 2 } };
                s.Normals = new[] { n };
                s.Edges = new[] { (0, 1), (1, 2), (2, 0) };
                return s;
            }

            // No area: the two farthest of its distinct vertices make a segment, or one point.
            XPoint[] pts = { a, b, c };
            double3[] dp = { da, db, dc };
            int bi = 0, bj = 0;
            BigInteger best = BigInteger.MinusOne;
            for (int i = 0; i < 3; i++)
            {
                for (int j = i + 1; j < 3; j++)
                {
                    BigInteger l = (pts[j] - pts[i]).LengthSq;
                    if (l > best)
                    {
                        best = l;
                        bi = i;
                        bj = j;
                    }
                }
            }

            if (best.IsZero)
            {
                s.V = new[] { a };
                s.D = new[] { da };
                return s;
            }

            s.V = new[] { pts[bi], pts[bj] };
            s.D = new[] { dp[bi], dp[bj] };
            s.Edges = new[] { (0, 1) };
            return s;
        }

        private static XShape Convex(SpatialSplitSample.Convex c, in float4x4 shapeToOwner, int scale, out string invalid)
        {
            invalid = null;
            // The counts from the arrays themselves (the range's say the same of a sample read whole).
            int n = c.vertices.Length;
            int faceCount = c.faceOffsets.Length - 1;
            if (n < 4 || faceCount < 4)
            {
                invalid = "a convex of " + n + " vertices and " + faceCount + " faces";
                return null;
            }

            var s = new XShape { Solid = true, V = new XPoint[n], D = new double3[n] };
            var sum = new XPoint(BigInteger.Zero, BigInteger.Zero, BigInteger.Zero);
            for (int i = 0; i < n; i++)
            {
                s.V[i] = Owner(c.vertices[i], in shapeToOwner, scale);
                s.D[i] = OwnerDouble(c.vertices[i], in shapeToOwner);
                sum = sum + s.V[i];
            }

            s.Faces = new int[faceCount][];
            s.Normals = new XPoint[faceCount];
            var edges = new HashSet<(int, int)>();
            for (int f = 0; f < faceCount; f++)
            {
                int from = c.faceOffsets[f], to = c.faceOffsets[f + 1];
                if (to - from < 3 || from < 0 || to > c.faceIndices.Length)
                {
                    invalid = "face " + f + " of " + (to - from) + " vertices";
                    return null;
                }

                var loop = new int[to - from];
                for (int i = 0; i < loop.Length; i++)
                {
                    loop[i] = c.faceIndices[from + i];
                    if (loop[i] < 0 || loop[i] >= n)
                    {
                        invalid = "face " + f + " names vertex " + loop[i] + " of " + n;
                        return null;
                    }
                }

                // Newell's normal (exact: twice the area vector), then outward: the centroid (sum / n) is strictly inside.
                var normal = new XPoint(BigInteger.Zero, BigInteger.Zero, BigInteger.Zero);
                for (int i = 0; i < loop.Length; i++)
                {
                    normal = normal + XPoint.Cross(s.V[loop[i]], s.V[loop[(i + 1) % loop.Length]]);
                    int a = loop[i], b = loop[(i + 1) % loop.Length];
                    if (a != b) edges.Add((Math.Min(a, b), Math.Max(a, b)));
                }

                if (normal.IsZero)
                {
                    invalid = "face " + f + " has no normal (degenerate)";
                    return null;
                }

                int centroidSide = XPoint.Dot(normal, sum - (s.V[loop[0]] * n)).Sign;
                if (centroidSide == 0)
                {
                    invalid = "the centroid lies on the plane of face " + f + " (a flat convex)";
                    return null;
                }

                s.Faces[f] = loop;
                s.Normals[f] = centroidSide > 0 ? -normal : normal;
            }

            s.Edges = new (int, int)[edges.Count];
            int k = 0;
            foreach ((int a, int b) in edges) s.Edges[k++] = (a, b);
            Array.Sort(s.Edges);
            return s;
        }

        // The double picture of a shape: the box, and each face's unit normal from the EXACT normal (its integer components
        // brought under 2^62 by one shift, then converted: within 2^-52 of the direction, whatever the positions' magnitude)
        // with the offset of its first vertex. A normal summed in double from the positions is never used (TL 2026-10-22).
        private static void Prepare(XShape s)
        {
            s.boxMin = new double3(double.PositiveInfinity);
            s.boxMax = new double3(double.NegativeInfinity);
            foreach (double3 d in s.D)
            {
                s.boxMin = math.min(s.boxMin, d);
                s.boxMax = math.max(s.boxMax, d);
            }

            s.xMin = s.V[0];
            s.xMax = s.V[0];
            foreach (XPoint v in s.V)
            {
                s.xMin = new XPoint(BigInteger.Min(s.xMin.x, v.x), BigInteger.Min(s.xMin.y, v.y), BigInteger.Min(s.xMin.z, v.z));
                s.xMax = new XPoint(BigInteger.Max(s.xMax.x, v.x), BigInteger.Max(s.xMax.y, v.y), BigInteger.Max(s.xMax.z, v.z));
            }

            s.DN = new double3[s.Faces.Length];
            s.DO = new double[s.Faces.Length];
            for (int f = 0; f < s.Faces.Length; f++)
            {
                double3 n = math.normalize(ExactDirection(s.Normals[f]));
                s.DN[f] = n;
                s.DO[f] = math.dot(n, s.D[s.Faces[f][0]]);
            }
        }

        private static double3 ExactDirection(XPoint n)
        {
            long bits = Math.Max(Math.Max(Bits(n.x), Bits(n.y)), Bits(n.z));
            int shift = (int)Math.Max(0, bits - 62);
            return new double3((double)(long)(n.x >> shift), (double)(long)(n.y >> shift), (double)(long)(n.z >> shift));
        }

        private static long Bits(BigInteger v) => v.IsZero ? 0 : (long)Math.Ceiling(BigInteger.Log(BigInteger.Abs(v), 2.0)) + 1;

        // ---- the decision ---------------------------------------------------------------------------------------------------

        private sealed class UnionFind
        {
            private readonly int[] _parent;

            public UnionFind(int n)
            {
                _parent = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }

            public int Find(int i)
            {
                while (_parent[i] != i)
                {
                    _parent[i] = _parent[_parent[i]];
                    i = _parent[i];
                }

                return i;
            }

            public void Union(int a, int b)
            {
                a = Find(a);
                b = Find(b);
                if (a != b) _parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }

        /// <summary>
        /// Decides one sample within the time limit (seconds; 0 or less: no limit). With <paramref name="filters"/> false
        /// every pair and every feature is judged exactly (the box sweep included): slow, the reference of the reference.
        /// Any thread; allocates freely.
        /// </summary>
        internal static ReferenceDecision Decide(SpatialSplitSample sample, double secondsLimit, bool filters = true)
        {
            long begin = Stopwatch.GetTimestamp();
            var d = new ReferenceDecision { filters = filters };
            long peak = GC.GetTotalMemory(false);
            try
            {
                DecideInto(sample, secondsLimit, filters, begin, d, ref peak);
            }
            catch (InvalidOperationException e)
            {
                d.verdict = ReferenceVerdict.InvalidInput;
                d.reason = e.Message;
            }
            catch (ArgumentException e)
            {
                d.verdict = ReferenceVerdict.InvalidInput;
                d.reason = e.Message;
            }
            catch (OutOfMemoryException)
            {
                d.verdict = ReferenceVerdict.Undecided;
                d.reason = "out of memory";
            }

            d.seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            d.peakManagedBytes = Math.Max(peak, GC.GetTotalMemory(false));
            return d;
        }

        // Steps 1 to 4 of a decision: the input checked, the scale, the elements (convexes first, then triangles) with their
        // double picture, and the thresholds of the certificates. Throws ArgumentException on an invalid input.
        private static XShape[] Elements(SpatialSplitSample sample, ReferenceDecision d, out Ctx ctx)
        {
            // 1. The input holds together.
            if (sample == null) throw new ArgumentException("no sample");
            if (!math.isfinite(sample.margin) || sample.margin <= 0f) throw new ArgumentException("the margin is " + sample.margin.ToString("R", CultureInfo.InvariantCulture));
            if (!Finite(in sample.geometryToOwner) || !Finite(in sample.shapeToOwner)) throw new ArgumentException("a transform is not finite");
            if (sample.indices.Length % 3 != 0) throw new ArgumentException("the index count " + sample.indices.Length + " is not a multiple of three");
            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            if (sample.vertexStructBytes != vertexBytes) throw new ArgumentException("the vertex is " + sample.vertexStructBytes + " bytes, this build's " + vertexBytes);
            int named = sample.vertexIds.Length;
            if (sample.vertexBytes.Length != named * vertexBytes) throw new ArgumentException("the vertex bytes are not " + named + " vertices of " + vertexBytes + " bytes");
            var positions = new float3[named];
            int positionAt = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            for (int i = 0; i < named; i++)
            {
                int at = (i * vertexBytes) + positionAt;
                positions[i] = new float3(BitConverter.ToSingle(sample.vertexBytes, at), BitConverter.ToSingle(sample.vertexBytes, at + 4), BitConverter.ToSingle(sample.vertexBytes, at + 8));
                if (!math.all(math.isfinite(positions[i]))) throw new ArgumentException("vertex " + sample.vertexIds[i] + " is not finite");
            }

            int triangleCount = sample.indices.Length / 3;
            var corner = new int[sample.indices.Length];
            for (int i = 0; i < sample.indices.Length; i++)
            {
                uint id = sample.indices[i];
                int at = id < (uint)Math.Max(0, sample.vertexViewLength) ? Array.BinarySearch(sample.vertexIds, id) : -1;
                if (at < 0) throw new ArgumentException("index " + i + " names vertex " + id + ", which the sample does not hold");
                corner[i] = at;
            }

            foreach (SpatialSplitSample.Convex c in sample.convexes)
            {
                foreach (float3 v in c.vertices)
                {
                    if (!math.all(math.isfinite(v))) throw new ArgumentException("a convex vertex is not finite");
                }
            }

            // 2. The scale: every owner coordinate and 2m an integer.
            int eGeometry = 0, eShape = 0, eMatrixG = 0, eMatrixS = 0, eTranslationG = 0, eTranslationS = 0;
            foreach (float3 p in positions)
            {
                eGeometry = LeastExponent(p.x, LeastExponent(p.y, LeastExponent(p.z, eGeometry)));
            }

            foreach (SpatialSplitSample.Convex c in sample.convexes)
            {
                foreach (float3 p in c.vertices) eShape = LeastExponent(p.x, LeastExponent(p.y, LeastExponent(p.z, eShape)));
            }

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    eMatrixG = LeastExponent(Entry(in sample.geometryToOwner, r, c), eMatrixG);
                    eMatrixS = LeastExponent(Entry(in sample.shapeToOwner, r, c), eMatrixS);
                }

                eTranslationG = LeastExponent(Entry(in sample.geometryToOwner, r, 3), eTranslationG);
                eTranslationS = LeastExponent(Entry(in sample.shapeToOwner, r, 3), eTranslationS);
            }

            int eMargin = LeastExponent(sample.margin, 0);
            int scale = Math.Max(0, Math.Max(Math.Max(-(eGeometry + eMatrixG), -(eShape + eMatrixS)),
                Math.Max(Math.Max(-eTranslationG, -eTranslationS), -eMargin)));
            d.scale = scale;
            BigInteger twoM = Scaled(sample.margin, scale) * 2;

            // 3. The elements: convexes first, then triangles.
            int convexCount = sample.convexes.Length;
            var elements = new XShape[convexCount + triangleCount];
            for (int c = 0; c < convexCount; c++)
            {
                elements[c] = Convex(sample.convexes[c], in sample.shapeToOwner, scale, out string invalid);
                if (elements[c] == null) throw new ArgumentException("convex " + c + ": " + invalid);
                Prepare(elements[c]);
            }

            var exact = new XPoint[named];
            var dbl = new double3[named];
            for (int i = 0; i < named; i++)
            {
                exact[i] = Owner(positions[i], in sample.geometryToOwner, scale);
                dbl[i] = OwnerDouble(positions[i], in sample.geometryToOwner);
            }

            int degenerate = 0;
            for (int k = 0; k < triangleCount; k++)
            {
                int a = corner[3 * k], b = corner[(3 * k) + 1], c = corner[(3 * k) + 2];
                elements[convexCount + k] = Triangle(exact[a], exact[b], exact[c], dbl[a], dbl[b], dbl[c], out bool flat);
                if (flat) degenerate++;
                Prepare(elements[convexCount + k]);
            }

            d.convexCount = convexCount;
            d.triangleCount = triangleCount;
            d.degenerateTriangles = degenerate;
            int n = elements.Length;
            d.allPairs = (long)n * (n - 1) / 2;

            // 4. The bounds of the double certificates, and the sweep over the boxes.
            double bound = 1.0;
            foreach (XShape s in elements)
            {
                bound = Math.Max(bound, Math.Max(math.cmax(math.abs(s.boxMin)), math.cmax(math.abs(s.boxMax))));
            }

            bound = Math.Max(bound, Math.Max(TermBound(positions, in sample.geometryToOwner), ConvexTermBound(sample, in sample.shapeToOwner)));
            ctx = new Ctx { t = twoM * twoM, m2 = 2.0 * sample.margin };
            ctx.e1 = bound * Math.Pow(2.0, -44);
            ctx.e2 = bound * bound * Math.Pow(2.0, -40);
            ctx.far = ctx.m2 + (4.0 * ctx.e1);
            ctx.near = ctx.m2 - (4.0 * ctx.e1);
            d.bound = ctx.e1;
            return elements;
        }

        private static void DecideInto(SpatialSplitSample sample, double secondsLimit, bool filters, long begin, ReferenceDecision d, ref long peak)
        {
            XShape[] elements = Elements(sample, d, out Ctx ctx);
            ctx.filters = filters;
            int convexCount = sample.convexes.Length;
            int n = elements.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (i, j) => elements[i].boxMin.x.CompareTo(elements[j].boxMin.x));
            var sets = new UnionFind(n);
            long deadline = secondsLimit > 0.0 ? begin + (long)(secondsLimit * Stopwatch.Frequency) : long.MaxValue;
            long judged = 0;
            for (int oi = 0; oi < n; oi++)
            {
                XShape p = elements[order[oi]];
                double reach = p.boxMax.x + ctx.far;
                for (int oj = oi + 1; oj < n; oj++)
                {
                    XShape q = elements[order[oj]];
                    if (filters)
                    {
                        if (q.boxMin.x > reach) break;
                        if (BoxGap(p.boxMin, p.boxMax, q.boxMin, q.boxMax) > ctx.far)
                        {
                            continue;   // far by the boxes (counted with the pairs the sweep never reached)
                        }
                    }

                    judged++;
                    bool joined;
                    if (filters && VerticesNearer(p, q, ctx.near))
                    {
                        joined = true;
                        d.pairsNearByVertices++;
                    }
                    else
                    {
                        if (Stopwatch.GetTimestamp() > deadline)
                        {
                            d.verdict = ReferenceVerdict.Undecided;
                            d.reason = "the time limit of " + secondsLimit.ToString("R", CultureInfo.InvariantCulture) + " s passed with " + judged + " pairs judged";
                            return;
                        }

                        joined = Joined(p, q, in ctx, d, out string path);
                        if (path == "inside") d.pairsNearByInside++;
                        else if (path == "axes") d.pairsFarByAxes++;
                        else d.pairsExact++;
                        if ((judged & 255) == 0) peak = Math.Max(peak, GC.GetTotalMemory(false));
                    }

                    if (joined)
                    {
                        d.pairsConnected++;
                        sets.Union(order[oi], order[oj]);
                    }
                }
            }

            d.pairsFarByBoxes = d.allPairs - judged;

            // 5. The components, and whether they can be dealt to two sides each with a convex and a triangle.
            var componentOf = new Dictionary<int, int>();
            var componentConvexes = new List<int>();
            var componentTriangles = new List<int>();
            var componentIndex = new int[n];
            for (int i = 0; i < n; i++)
            {
                int root = sets.Find(i);
                if (!componentOf.TryGetValue(root, out int k))
                {
                    k = componentConvexes.Count;
                    componentOf[root] = k;   // in the order of the smallest member
                    componentConvexes.Add(0);
                    componentTriangles.Add(0);
                }

                componentIndex[i] = k;
                if (i < convexCount) componentConvexes[k]++;
                else componentTriangles[k]++;
            }

            int components = componentConvexes.Count;
            d.componentCount = components;
            int firstFull = -1, secondFull = -1, firstConvexOnly = -1, firstTriangleOnly = -1;
            for (int k = 0; k < components; k++)
            {
                bool hasC = componentConvexes[k] > 0, hasT = componentTriangles[k] > 0;
                if (hasC && hasT)
                {
                    d.fullComponents++;
                    if (firstFull < 0) firstFull = k;
                    else if (secondFull < 0) secondFull = k;
                }
                else if (hasC)
                {
                    d.convexOnlyComponents++;
                    if (firstConvexOnly < 0) firstConvexOnly = k;
                }
                else
                {
                    d.triangleOnlyComponents++;
                    if (firstTriangleOnly < 0) firstTriangleOnly = k;
                }
            }

            var positive = new HashSet<int>();
            if (d.fullComponents >= 2)
            {
                positive.Add(firstFull);
            }
            else if (d.fullComponents == 1 && d.convexOnlyComponents >= 1 && d.triangleOnlyComponents >= 1)
            {
                positive.Add(firstFull);
            }
            else if (d.fullComponents == 0 && d.convexOnlyComponents >= 2 && d.triangleOnlyComponents >= 2)
            {
                positive.Add(firstConvexOnly);
                positive.Add(firstTriangleOnly);
            }
            else
            {
                d.verdict = ReferenceVerdict.NotSeparable;
                d.reason = "components with both kinds " + d.fullComponents + ", convexes only " + d.convexOnlyComponents + ", triangles only " + d.triangleOnlyComponents
                    + ": no two sides each with a convex and a triangle";
                return;
            }

            d.verdict = ReferenceVerdict.Separable;
            d.convexPositive = new bool[convexCount];
            d.trianglePositive = new bool[n - convexCount];
            for (int i = 0; i < n; i++)
            {
                bool side = positive.Contains(componentIndex[i]);
                if (i < convexCount)
                {
                    d.convexPositive[i] = side;
                    if (side) d.positiveConvexes++; else d.negativeConvexes++;
                }
                else
                {
                    d.trianglePositive[i - convexCount] = side;
                    if (side) d.positiveTriangles++; else d.negativeTriangles++;
                }
            }
        }

        // The largest term of a transform (a matrix entry times a coordinate, or a translation): what the double owner
        // coordinates were summed from, so that their rounding error is bounded by it even where terms cancelled.
        internal static double TermBound(float3[] points, in float4x4 m)
        {
            double entry = 0.0, translation = 0.0, coordinate = 0.0;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) entry = Math.Max(entry, Math.Abs(Entry(in m, r, c)));
                translation = Math.Max(translation, Math.Abs(Entry(in m, r, 3)));
            }

            foreach (float3 p in points) coordinate = Math.Max(coordinate, math.cmax(math.abs(p)));
            return Math.Max(entry * coordinate, translation);
        }

        internal static double ConvexTermBound(SpatialSplitSample sample, in float4x4 m)
        {
            double bound = 0.0;
            foreach (SpatialSplitSample.Convex c in sample.convexes) bound = Math.Max(bound, TermBound(c.vertices, in m));
            return bound;
        }

        /// <summary>What a placement's verification came to: every pair across the sides judged, and whether any is joined.</summary>
        internal sealed class PlacementCheck
        {
            public bool valid;

            /// <summary>The time limit passed before every pair was judged: nothing is said (neither valid nor invalid).</summary>
            public bool unfinished;
            public string reason = "";
            public long pairsAcross, pairsJudgedExactly, pairsJoined, pairsFarByExactBoxes;
            public double seconds;
        }

        // The boxes of two shapes, exactly: apart by 2m or more (the gap's square, in integers at the scale, not under t).
        private static bool ExactBoxesApart(XShape p, XShape q, BigInteger t)
        {
            BigInteger gx = BigInteger.Max(BigInteger.Max(q.xMin.x - p.xMax.x, p.xMin.x - q.xMax.x), BigInteger.Zero);
            BigInteger gy = BigInteger.Max(BigInteger.Max(q.xMin.y - p.xMax.y, p.xMin.y - q.xMax.y), BigInteger.Zero);
            BigInteger gz = BigInteger.Max(BigInteger.Max(q.xMin.z - p.xMax.z, p.xMin.z - q.xMax.z), BigInteger.Zero);
            return (gx * gx) + (gy * gy) + (gz * gz) >= t;
        }

        /// <summary>
        /// Verifies a placement someone else found (a candidate method's): both sides hold a convex and a triangle, and no
        /// pair across the sides is joined -- every such pair judged by this decider's own rule (the double certificates
        /// where they hold, exactly otherwise; <paramref name="filters"/> false: exactly throughout). The elements are
        /// built as in <see cref="Decide"/>; an invalid input is an invalid placement.
        /// </summary>
        internal static PlacementCheck VerifyPlacement(SpatialSplitSample sample, bool[] convexPositive, bool[] trianglePositive, double secondsLimit, bool filters = true)
        {
            long begin = Stopwatch.GetTimestamp();
            var check = new PlacementCheck();
            try
            {
                var d = new ReferenceDecision { filters = filters };
                long peak = 0;
                XShape[] elements = Elements(sample, d, out Ctx ctx);
                ctx.filters = filters;
                int convexCount = sample.convexes.Length;
                if (convexPositive == null || trianglePositive == null || convexPositive.Length != convexCount || trianglePositive.Length != elements.Length - convexCount)
                {
                    check.reason = "the placement does not name every element once";
                    return check;
                }

                int pc = 0, pt = 0, nc = 0, nt = 0;
                foreach (bool side in convexPositive) { if (side) pc++; else nc++; }
                foreach (bool side in trianglePositive) { if (side) pt++; else nt++; }
                if (pc == 0 || pt == 0 || nc == 0 || nt == 0)
                {
                    check.reason = "a side lacks a convex or a triangle (positive " + pc + " convexes, " + pt + " triangles; negative " + nc + ", " + nt + ")";
                    return check;
                }

                bool Side(int i) => i < convexCount ? convexPositive[i] : trianglePositive[i - convexCount];
                int n = elements.Length;
                var order = new int[n];
                for (int i = 0; i < n; i++) order[i] = i;
                Array.Sort(order, (i, j) => elements[i].boxMin.x.CompareTo(elements[j].boxMin.x));
                long deadline = secondsLimit > 0.0 ? begin + (long)(secondsLimit * Stopwatch.Frequency) : long.MaxValue;
                for (int oi = 0; oi < n; oi++)
                {
                    XShape p = elements[order[oi]];
                    bool sideP = Side(order[oi]);
                    double reach = p.boxMax.x + ctx.far;
                    for (int oj = oi + 1; oj < n; oj++)
                    {
                        if (Side(order[oj]) == sideP) continue;
                        XShape q = elements[order[oj]];
                        check.pairsAcross++;
                        if (Stopwatch.GetTimestamp() > deadline)   // before every pair, the skipped ones too (TL review 3)
                        {
                            check.unfinished = true;
                            check.reason = "the time limit passed";
                            return check;
                        }

                        if (filters)
                        {
                            if (q.boxMin.x > reach) break;
                            if (BoxGap(p.boxMin, p.boxMax, q.boxMin, q.boxMax) > ctx.far) continue;
                        }
                        else if (ExactBoxesApart(p, q, ctx.t))
                        {
                            check.pairsFarByExactBoxes++;
                            continue;
                        }

                        bool joined;
                        if (filters && VerticesNearer(p, q, ctx.near)) joined = true;
                        else
                        {
                            joined = Joined(p, q, in ctx, d, out string path);
                            if (path == "exact") check.pairsJudgedExactly++;
                        }

                        if (joined)
                        {
                            check.pairsJoined++;
                            check.reason = "a pair across the sides is joined: elements " + order[oi] + " and " + order[oj];
                            return check;
                        }
                    }
                }

                if (Stopwatch.GetTimestamp() > deadline)   // and right before the conclusion
                {
                    check.unfinished = true;
                    check.reason = "the time limit passed";
                    return check;
                }

                check.valid = true;
                _ = peak;
            }
            catch (ArgumentException e)
            {
                check.reason = "invalid input: " + e.Message;
            }
            catch (InvalidOperationException e)
            {
                check.reason = "invalid input: " + e.Message;
            }
            finally
            {
                check.seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            }

            return check;
        }

        // The least double distance between a vertex of p and one of q is under the bound: they are joined for sure.
        private static bool VerticesNearer(XShape p, XShape q, double under)
        {
            if (under <= 0.0) return false;
            double u2 = under * under;
            foreach (double3 a in p.D)
            {
                foreach (double3 b in q.D)
                {
                    if (math.distancesq(a, b) < u2) return true;
                }
            }

            return false;
        }

        /// <summary>The decision as one JSON line (the identity fields given by the caller).</summary>
        internal static string Json(ReferenceDecision d, string identity, string liveFields)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var line = new StringBuilder(256 + (d.trianglePositive?.Length ?? 0));
            line.Append('{').Append(identity)
                .Append(",\"decider\":").Append(SpatialSplitCaptureSink.Json(Version)).Append(",\"numeric\":").Append(SpatialSplitCaptureSink.Json(Numeric))
                .Append(",\"scale\":").Append(d.scale)
                .Append(",\"verdict\":").Append(SpatialSplitCaptureSink.Json(d.verdict.ToString())).Append(",\"reason\":").Append(SpatialSplitCaptureSink.Json(d.reason))
                .Append(",\"convexes\":").Append(d.convexCount).Append(",\"triangles\":").Append(d.triangleCount).Append(",\"degenerateTriangles\":").Append(d.degenerateTriangles)
                .Append(",\"components\":").Append(d.componentCount).Append(",\"fullComponents\":").Append(d.fullComponents)
                .Append(",\"convexOnlyComponents\":").Append(d.convexOnlyComponents).Append(",\"triangleOnlyComponents\":").Append(d.triangleOnlyComponents)
                .Append(",\"positiveConvexes\":").Append(d.positiveConvexes).Append(",\"positiveTriangles\":").Append(d.positiveTriangles)
                .Append(",\"negativeConvexes\":").Append(d.negativeConvexes).Append(",\"negativeTriangles\":").Append(d.negativeTriangles)
                .Append(",\"convexSides\":").Append(SpatialSplitCaptureSink.Json(Sides(d.convexPositive)))
                .Append(",\"triangleSides\":").Append(SpatialSplitCaptureSink.Json(Sides(d.trianglePositive)))
                .Append(",\"filters\":").Append(d.filters ? "true" : "false").Append(",\"boundE1\":").Append(d.bound.ToString("R", inv))
                .Append(",\"allPairs\":").Append(d.allPairs).Append(",\"pairsFarByBoxes\":").Append(d.pairsFarByBoxes).Append(",\"pairsFarByAxes\":").Append(d.pairsFarByAxes)
                .Append(",\"pairsNearByVertices\":").Append(d.pairsNearByVertices).Append(",\"pairsNearByInside\":").Append(d.pairsNearByInside)
                .Append(",\"pairsExact\":").Append(d.pairsExact).Append(",\"pairsConnected\":").Append(d.pairsConnected)
                .Append(",\"featuresDouble\":").Append(d.featuresDouble).Append(",\"featuresExact\":").Append(d.featuresExact)
                .Append(",\"seconds\":").Append(d.seconds.ToString("R", inv)).Append(",\"peakManagedBytes\":").Append(d.peakManagedBytes);
            if (!string.IsNullOrEmpty(liveFields)) line.Append(',').Append(liveFields);
            return line.Append('}').ToString();
        }

        private static string Sides(bool[] positive)
        {
            if (positive == null) return "";
            var text = new StringBuilder(positive.Length);
            foreach (bool p in positive) text.Append(p ? '+' : '-');
            return text.ToString();
        }
    }
}
