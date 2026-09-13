using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut
{
    /// <summary>Symmetric 3x3 matrix (xx, yy, zz, xy, xz, yz) in double.</summary>
    public struct SymmetricMatrix3
    {
        public double xx, yy, zz, xy, xz, yz;
        public static SymmetricMatrix3 operator +(SymmetricMatrix3 a, SymmetricMatrix3 b) =>
            new SymmetricMatrix3 { xx = a.xx + b.xx, yy = a.yy + b.yy, zz = a.zz + b.zz, xy = a.xy + b.xy, xz = a.xz + b.xz, yz = a.yz + b.yz };
        public static SymmetricMatrix3 operator *(SymmetricMatrix3 a, double k) =>
            new SymmetricMatrix3 { xx = a.xx * k, yy = a.yy * k, zz = a.zz * k, xy = a.xy * k, xz = a.xz * k, yz = a.yz * k };
        public double Trace => xx + yy + zz;
        public double3x3 ToMatrix() => new double3x3(xx, xy, xz, xy, yy, yz, xz, yz, zz);
        public bool IsFinite => math.isfinite(xx) && math.isfinite(yy) && math.isfinite(zz) && math.isfinite(xy) && math.isfinite(xz) && math.isfinite(yz);
        public static SymmetricMatrix3 Outer(double3 a, double3 b) =>
            new SymmetricMatrix3 { xx = a.x * b.x, yy = a.y * b.y, zz = a.z * b.z, xy = a.x * b.y, xz = a.x * b.z, yz = a.y * b.z };
    }

    /// <summary>Unit-density volume integrals of a closed convex: volume, first moment and second moment about the origin.</summary>
    public struct MassProperties
    {
        public double volume;                 // unit-density mass
        public double3 firstMoment;           // integral of x dV
        public SymmetricMatrix3 secondMoment; // integral of x x^T dV
        public double3 CenterOfMass => firstMoment / volume;

        /// <summary>Unit-density inertia tensor about the center of mass.</summary>
        public SymmetricMatrix3 InertiaAboutCom()
        {
            var c = CenterOfMass;
            var cov = secondMoment + SymmetricMatrix3.Outer(c, c) * (-volume); // covariance about the COM
            double tr = cov.Trace;
            return new SymmetricMatrix3 { xx = tr - cov.xx, yy = tr - cov.yy, zz = tr - cov.zz, xy = -cov.xy, xz = -cov.xz, yz = -cov.yz };
        }

        public static MassProperties operator +(MassProperties a, MassProperties b) => new MassProperties
        {
            volume = a.volume + b.volume, firstMoment = a.firstMoment + b.firstMoment, secondMoment = a.secondMoment + b.secondMoment,
        };
    }

    /// <summary>
    /// Fan triangulation + signed tetrahedron integration about the vertex centroid, double accumulation
    /// (probe Phase 4.1: float and double agree within noise; double kept for stability).
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class MassPropertiesKernel
    {
        [BurstCompile]
        public static void ComputeDouble(in BrepBuffer b, ref MassProperties m)
        {
            double3 r = 0;
            for (int i = 0; i < b.V; i++) r += b.v[i];
            r /= b.V;
            double vol = 0; double3 fm = 0;
            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            for (int f = 0; f < b.F; f++)
            {
                int start = b.faceOff[f], L = b.faceOff[f + 1] - start;
                double3 a = b.v[b.faceIdx[start]];
                for (int k = 1; k + 1 < L; k++)
                {
                    double3 p = b.v[b.faceIdx[start + k]], q = b.v[b.faceIdx[start + k + 1]];
                    double det = math.dot(a - r, math.cross(p - r, q - r));
                    double tv = det / 6.0;
                    double3 S = r + a + p + q;
                    vol += tv;
                    fm += tv * (S * 0.25);
                    double k120 = det / 120.0;
                    xx += k120 * (r.x * r.x + a.x * a.x + p.x * p.x + q.x * q.x + S.x * S.x);
                    yy += k120 * (r.y * r.y + a.y * a.y + p.y * p.y + q.y * q.y + S.y * S.y);
                    zz += k120 * (r.z * r.z + a.z * a.z + p.z * p.z + q.z * q.z + S.z * S.z);
                    xy += k120 * (r.x * r.y + a.x * a.y + p.x * p.y + q.x * q.y + S.x * S.y);
                    xz += k120 * (r.x * r.z + a.x * a.z + p.x * p.z + q.x * q.z + S.x * S.z);
                    yz += k120 * (r.y * r.z + a.y * a.z + p.y * p.z + q.y * q.z + S.y * S.z);
                }
            }
            m.volume = vol; m.firstMoment = fm;
            m.secondMoment = new SymmetricMatrix3 { xx = xx, yy = yy, zz = zz, xy = xy, xz = xz, yz = yz };
        }
    }
}
