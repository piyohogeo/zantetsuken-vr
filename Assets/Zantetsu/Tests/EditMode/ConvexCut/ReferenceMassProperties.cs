using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>Test-only reference (double, managed) mass properties by fan triangulation + tetra integration about the vertex centroid.</summary>
    public static class ReferenceMassProperties
    {
        public static MassProperties Compute(ConvexPoly p)
        {
            double3 r = p.VertexCentroid();
            var mp = new MassProperties();
            foreach (var loop in p.F)
                for (int k = 1; k + 1 < loop.Length; k++)
                    AccumulateTetra(ref mp, r, p.V[loop[0]], p.V[loop[k]], p.V[loop[k + 1]]);
            return mp;
        }

        public static void AccumulateTetra(ref MassProperties mp, double3 r, double3 a, double3 b, double3 c)
        {
            double det = math.dot(a - r, math.cross(b - r, c - r));
            double vol = det / 6.0;
            double3 S = r + a + b + c;
            mp.volume += vol;
            mp.firstMoment += vol * (S * 0.25);
            var m = SymmetricMatrix3.Outer(r, r) + SymmetricMatrix3.Outer(a, a) + SymmetricMatrix3.Outer(b, b) + SymmetricMatrix3.Outer(c, c) + SymmetricMatrix3.Outer(S, S);
            mp.secondMoment += m * (det / 120.0);
        }

        public static double Volume(ConvexPoly p) => Compute(p).volume;
    }

    /// <summary>Implementation-independent comparison: support function over fixed direction sets.</summary>
    public static class SupportSignature
    {
        public static readonly double3[] Dirs26 = Build26();
        public static readonly double3[] Dirs42 = Build42();

        static double3[] Build26()
        {
            var l = new List<double3>();
            for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                if (x != 0 || y != 0 || z != 0) l.Add(math.normalize(new double3(x, y, z)));
            return l.ToArray();
        }

        static double3[] Build42()
        {
            var l = new List<double3>(Dirs26);
            double phi = math.PI * (3.0 - math.sqrt(5.0));
            for (int i = 0; i < 16; i++)
            {
                double y = 1 - (i + 0.5) / 8.0;
                double rr = math.sqrt(1 - y * y);
                double t = phi * i;
                l.Add(new double3(math.cos(t) * rr, y, math.sin(t) * rr));
            }
            return l.ToArray();
        }

        public static double Support(ConvexPoly p, double3 d)
        {
            double best = double.MinValue;
            foreach (var v in p.V) best = math.max(best, math.dot(v, d));
            return best;
        }

        /// Max absolute support difference over the direction set.
        public static double MaxSupportDiff(ConvexPoly a, ConvexPoly b, double3[] dirs)
        {
            double m = 0;
            foreach (var d in dirs) m = math.max(m, math.abs(Support(a, d) - Support(b, d)));
            return m;
        }

        /// Max over directions of support(a) - support(b): how far a pokes outside b.
        public static double MaxSupportExcess(ConvexPoly a, ConvexPoly b, double3[] dirs)
        {
            double m = double.MinValue;
            foreach (var d in dirs) m = math.max(m, Support(a, d) - Support(b, d));
            return m;
        }

        public static string Signature(ConvexPoly p, double rel = 1e-6)
        {
            var sb = new StringBuilder();
            double ext = p.MaxExtent();
            double q = ext * rel;
            sb.Append("V").Append(p.V.Length).Append("E").Append(p.EdgeCount).Append("F").Append(p.F.Length);
            foreach (var d in Dirs42) sb.Append('|').Append(Math.Round(Support(p, d) / q).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
