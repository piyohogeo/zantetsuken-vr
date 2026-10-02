using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.PhysicsCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The candidate the builder refused three times in hi1-play (2026-09-30, CollegeHull_TenCutsRestAndRefuse, the
    /// class [8, 11]: two resting sides of college_001, 102 hull vertices): the recorded points, in their recorded order,
    /// built directly with the vertex limit 128 -- no real time, physics or budget. The refusal was "a face polygon is not
    /// convex (2.67E-002 m at a corner of face 10 with 8 vertices)". A later 10-cut run passing on another input does not
    /// count as this one resolved.
    /// </summary>
    public class ConvexHullBuilderRefusedCandidateTests
    {
        internal static readonly float3[] k_points4 =
        {
            new float3(12.7686653f, 15.4713669f, 8.233125f),
            new float3(12.1622782f, 4.92377043f, -2.30529022f),
            new float3(12.1315908f, 4.92376947f, -7.53401f),
            new float3(12.6790934f, 15.6067114f, -8.218933f),
            new float3(11.3902855f, 4.923772f, 7.86215448f),
            new float3(12.7690706f, 15.5324316f, 8.328605f),
            new float3(12.7676382f, 16.423317f, 8.367775f),
            new float3(-12.5662632f, 16.4302f, 8.367806f),
            new float3(-12.76889f, 16.42642f, 8.367638f),
            new float3(-12.7688446f, 16.1750031f, 8.356584f),
            new float3(-5.464858f, 4.92377472f, 7.861974f),
            new float3(12.1624842f, 4.92377138f, 7.59578037f),
            new float3(12.7691422f, 15.4796429f, 8.323893f),
            new float3(12.6789637f, 15.6630354f, -8.223625f),
            new float3(11.5496283f, 4.923769f, -7.742363f),
            new float3(7.819318f, 18.5360222f, 7.353965f),
            new float3(12.6179991f, 16.590519f, 8.287509f),
            new float3(12.7331934f, 16.4893684f, 2.04599237f),
            new float3(12.6775446f, 16.4227333f, -8.226695f),
            new float3(12.6455917f, 16.4359665f, -8.200696f),
            new float3(10.1972361f, 17.4574928f, -5.3521595f),
            new float3(5.297205f, 19.5019913f, 0.356415629f),
            new float3(12.6773987f, 16.4118328f, -8.257184f),
            new float3(12.7671728f, 16.5276833f, 8.31766f),
            new float3(-1.78715992f, 21.02841f, 6.15221453f),
            new float3(-1.759932f, 21.0282726f, 6.15851545f),
            new float3(-0.215413675f, 21.0467987f, 6.149417f),
            new float3(-0.06412698f, 21.1365337f, 4.92023659f),
            new float3(-1.496821f, 21.12272f, 4.88157463f),
            new float3(-1.520681f, 21.1221428f, 4.885794f),
            new float3(1.58513927f, 21.0260963f, 6.15912f),
            new float3(1.66458428f, 21.0265484f, 6.1412f),
            new float3(1.40101624f, 21.1230659f, 4.88272238f),
            new float3(-12.7688866f, 16.431797f, 8.367066f),
            new float3(1.76346326f, 20.986681f, 6.178023f),
            new float3(-1.88392246f, 20.9893627f, 6.177215f),
            new float3(-11.8674021f, 16.9534359f, 8.116471f),
            new float3(-12.7684717f, 16.5863361f, 8.292861f),
            new float3(-1.59307194f, 21.09167f, 4.79486f),
            new float3(-12.6781149f, 16.435833f, -8.206449f),
            new float3(-12.7683582f, 16.5889931f, 8.27229f),
            new float3(-12.6780138f, 15.568059f, -8.219971f),
            new float3(-12.6781015f, 15.569582f, -8.218275f),
            new float3(-12.678092f, 15.5696812f, -8.220041f),
            new float3(6.12262774f, 16.4170532f, -8.257658f),
            new float3(12.6457243f, 16.415659f, -8.257357f),
            new float3(-12.182004f, 4.92377424f, 1.27074075f),
            new float3(-12.6908741f, 13.9927626f, 8.245893f),
            new float3(-12.7686939f, 15.4894447f, 8.321541f),
            new float3(-12.6659288f, 15.3332119f, -8.209776f),
            new float3(-12.130374f, 4.92377329f, -7.743234f),
            new float3(-12.18036f, 4.92377472f, 7.78793669f),
            new float3(-11.5394669f, 16.41723f, -8.258317f),
            new float3(-12.6778393f, 16.41776f, -8.256914f),
            new float3(12.1622925f, 4.92377853f, -2.30528927f),
            new float3(11.8795767f, 0.00607119128f, -7.21871758f),
            new float3(12.13161f, 4.923778f, -7.534009f),
            new float3(-2.160485f, 0.005761261f, 7.645763f),
            new float3(6.03000736f, 0.00123134092f, 7.64565945f),
            new float3(6.601168f, 0.00116491818f, 7.645663f),
            new float3(10.9568663f, 1.58891976f, 7.71552372f),
            new float3(11.39029f, 4.92377853f, 7.86215448f),
            new float3(-5.464853f, 4.92377853f, 7.8619585f),
            new float3(-2.41038966f, 0.2186157f, 7.655119f),
            new float3(10.918189f, -0.0128650842f, 7.58662367f),
            new float3(11.8732805f, -0.09000177f, 7.25031137f),
            new float3(11.8742371f, -0.08926525f, 7.25004673f),
            new float3(11.8744068f, -0.08890062f, 7.2500205f),
            new float3(12.16249f, 4.923778f, 7.59578133f),
            new float3(-11.1604586f, -0.09324722f, 7.191491f),
            new float3(6.128773f, -0.0957330242f, 7.22219324f),
            new float3(6.202428f, -0.0957213044f, 7.22242165f),
            new float3(-2.344882f, 0.00567807257f, 7.644956f),
            new float3(-11.5704584f, -0.0127400281f, 7.542296f),
            new float3(-11.9012728f, -0.03435498f, 7.44705057f),
            new float3(-11.8981609f, -0.09071592f, 7.20077848f),
            new float3(11.8743868f, -0.08924527f, 7.24959f),
            new float3(11.8741064f, -0.09411321f, 7.229353f),
            new float3(11.8738356f, -0.0939215f, -6.93072367f),
            new float3(11.8738337f, -0.09387245f, -7.194104f),
            new float3(11.8739738f, -0.09142386f, -7.21268368f),
            new float3(11.5362549f, -0.093798086f, -7.333398f),
            new float3(11.8737164f, -0.09386899f, -7.21257448f),
            new float3(11.8696508f, -0.09462354f, -3.15764046f),
            new float3(-11.8981819f, -0.093031235f, 6.599971f),
            new float3(-11.8997593f, -0.09190863f, 0.5729856f),
            new float3(-11.8947325f, -0.0911303759f, -3.60999656f),
            new float3(-11.8741255f, -0.0904623345f, -7.21312428f),
            new float3(11.1036482f, 0.669673741f, -7.551237f),
            new float3(11.1066866f, 0.7114804f, -7.5535965f),
            new float3(11.5496483f, 4.92377758f, -7.7423625f),
            new float3(11.8694859f, -0.09481964f, 7.22927332f),
            new float3(11.87328f, -0.09423964f, 7.23180771f),
            new float3(-12.0754f, 3.024143f, -0.190330908f),
            new float3(-11.9369879f, 1.16537631f, -7.574808f),
            new float3(-12.1819925f, 4.923778f, 1.27071929f),
            new float3(-12.1303539f, 4.923778f, -7.74325562f),
            new float3(11.0892649f, 0.6719779f, -7.55182648f),
            new float3(-11.8831472f, 0.0251871683f, 7.540787f),
            new float3(-11.9047108f, 0.027123183f, 7.53965235f),
            new float3(-11.9135466f, 0.184088856f, 7.548575f),
            new float3(-12.1803551f, 4.92377758f, 7.787915f),
        };

        /// <summary>The second refused candidate (hm3-play, 2026-09-30, the class [6, 8], after the exact triangle hull): 80 points, recorded in order; refused three times for "a face polygon is not convex (1.61E-002 m at a corner of face 23 with 5 vertices)". Kept for the diagnosis.</summary>
        internal static readonly float3[] k_points5 =
        {
            new float3(12.7672691f, 16.426733f, 8.364858f),
            new float3(-3.06419849f, 16.4266281f, 8.364858f),
            new float3(-3.06419849f, 0.5052872f, 7.66252232f),
            new float3(-2.73604727f, 1.21180083E-05f, 7.640233f),
            new float3(2.73630428f, 1.125592E-05f, 7.640231f),
            new float3(12.7668715f, 15.4441023f, 8.321511f),
            new float3(12.7670155f, 15.4452133f, 8.32156f),
            new float3(12.7670383f, 15.4456186f, 8.321578f),
            new float3(1.72975016f, 20.9808216f, 6.17886639f),
            new float3(-1.7296164f, 20.9808064f, 6.17886257f),
            new float3(-3.064199f, 20.44104f, 6.437945f),
            new float3(12.7670355f, 16.5167942f, 8.321629f),
            new float3(12.7662029f, 15.4311514f, 8.224148f),
            new float3(11.8742266f, -0.0895222f, -7.18977165f),
            new float3(11.8741026f, -0.08952255f, -7.21129036f),
            new float3(12.6778307f, 15.5558653f, -8.197632f),
            new float3(1.69666541f, 20.9808254f, 4.57114363f),
            new float3(-1.6965847f, 20.98081f, 4.57106829f),
            new float3(-3.06419945f, 0.04516828f, 7.638947f),
            new float3(-2.73645782f, 1.10750216E-05f, 7.64022827f),
            new float3(-3.06419945f, -0.00319162756f, 7.62623835f),
            new float3(2.73661637f, 1.04631727E-05f, 7.640228f),
            new float3(-3.06419849f, -0.08928448f, 7.25017643f),
            new float3(11.8742113f, -0.08928508f, 7.250183f),
            new float3(11.8740835f, -0.08952258f, -7.21322155f),
            new float3(11.8750505f, -0.08928509f, 7.249894f),
            new float3(-3.06419849f, -0.0895219743f, -7.21315432f),
            new float3(11.1599751f, 1.16063392f, 7.607302f),
            new float3(12.7670317f, 15.4455042f, 8.320351f),
            new float3(-3.064199f, 20.4122066f, 2.98069358f),
            new float3(10.86182f, 17.1700764f, -6.08728838f),
            new float3(8.938919f, 16.4023685f, -8.234553f),
            new float3(-3.06419945f, 16.4023762f, -8.23465252f),
            new float3(12.6778708f, 16.4150162f, -8.19554f),
            new float3(11.1598759f, 1.1603533f, -7.5705266f),
            new float3(11.15997f, 1.16169167f, -7.57060146f),
            new float3(12.6748857f, 15.5277033f, -8.196419f),
            new float3(12.6777725f, 15.5547409f, -8.197584f),
            new float3(-3.06419849f, 9.7743845f, -7.9459157f),
            new float3(12.6748991f, 16.4010887f, -8.234467f),
            new float3(-3.06419945f, 1.22200787f, -7.516877f),
            new float3(-3.06419754f, 0.388705552f, -7.349868f),
            new float3(12.6778612f, 15.5573721f, -8.197697f),
            new float3(12.67766f, 16.4010925f, -8.234453f),
            new float3(-3.06417942f, 16.4265919f, 8.365128f),
            new float3(-12.7673912f, 16.4265518f, 8.365122f),
            new float3(-12.7672405f, 15.7736588f, 8.33631f),
            new float3(-12.7671f, 15.4454288f, 8.321827f),
            new float3(-3.06422f, 0.5052618f, 7.66254759f),
            new float3(-3.06416821f, 20.4410324f, 6.438276f),
            new float3(-12.7671022f, 16.5167274f, 8.321838f),
            new float3(-12.7671595f, 16.5154247f, 8.322464f),
            new float3(-11.8743305f, -0.0892805159f, 7.25018358f),
            new float3(-11.16007f, 1.16063309f, 7.60733128f),
            new float3(-11.8743877f, -0.08830276f, 7.250251f),
            new float3(-3.064222f, 0.0451432727f, 7.63896561f),
            new float3(-3.064222f, -0.00321644149f, 7.626256f),
            new float3(-3.06422114f, -0.08930353f, 7.25019264f),
            new float3(-11.8744278f, -0.0892965049f, -7.213104f),
            new float3(-3.06421256f, -0.08931952f, -7.213138f),
            new float3(-12.67779f, 15.5558891f, -8.197326f),
            new float3(-11.8744621f, -0.088709414f, -7.213202f),
            new float3(-12.7670736f, 15.4448919f, 8.317062f),
            new float3(-12.7027454f, 15.5250435f, -3.58303452f),
            new float3(-12.6777678f, 16.4154739f, -8.198526f),
            new float3(-3.06416655f, 20.4122524f, 2.98102379f),
            new float3(-3.06417036f, 16.4025936f, -8.234384f),
            new float3(-12.6775827f, 16.4026241f, -8.234468f),
            new float3(-12.6777725f, 16.41528f, -8.19907f),
            new float3(-3.06418681f, 9.774598f, -7.94574833f),
            new float3(-12.67778f, 15.5956383f, -8.19932652f),
            new float3(-12.6775837f, 16.4013462f, -8.234413f),
            new float3(-11.874444f, -0.0890193f, -7.213183f),
            new float3(-11.0438414f, 1.25682306f, -7.486748f),
            new float3(-3.06421f, 1.22221494f, -7.516841f),
            new float3(-3.06421018f, 0.388910085f, -7.349844f),
            new float3(-12.6777906f, 15.5561752f, -8.197347f),
            new float3(-12.6907511f, 15.5402746f, -5.801738f),
            new float3(-12.6981459f, 15.5308943f, -4.43387365f),
            new float3(-12.700943f, 16.4412022f, -3.91683936f),
        };

        /// <summary>
        /// **The 80 points build within the limit and pass the independent verification** on their float output: every
        /// input point inside, planar faces, faces convex within their planes (no reflex corner, one full turn), a closed
        /// surface, the expansion within the merge tolerance, the vertices within 128. Before the fix a cut point of the
        /// intersection lay 2.3 cm past the kept end of its edge (a kept end 1.3e-10 m outside the plane): a notch of 1.61 cm.
        /// </summary>
        [Test]
        public void RefusedCandidate80_BuildsWithinTheLimit_AndVerifies()
        {
            bool built = ConvexHullBuilder.TryBuild(new List<float3>(k_points5), k_limit, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report);
            Assert.That(built, Is.True, report.reason);
            HullChecks.Measure(k_points5, v, offsets, indices, out double outside, out double nonConvex, out double nonPlanar, out double reflex, out double turning);
            TestContext.Out.WriteLine("refused candidate 80: " + v.Length + " v " + (offsets.Length - 1) + " f, decimated " + report.decimated + ", expansion " + report.expansion.ToString("E2") + " m; measured here: outside " + outside.ToString("E2") + ", non-convex " + nonConvex.ToString("E2") + ", non-planar " + nonPlanar.ToString("E2") + ", reflex " + reflex.ToString("E2") + " m, turning off by " + turning.ToString("E2") + " rad");
            HullChecks.AssertHull(k_points5, v, offsets, indices, report, "refused candidate 80", k_limit);
        }

        /// <summary>The diagnosis of the 80 points: every stage written out, with the vertex limit. It asserts only the input's identity.</summary>
        [Test]
        public void RefusedCandidate80_Stages()
        {
            Assert.That(k_points5.Length, Is.EqualTo(80));
            var trace = new List<string>();
            ConvexHullBuilder.traceForTest = trace;
            bool built;
            HullBuildReport report;
            try { built = ConvexHullBuilder.TryBuild(new List<float3>(k_points5), k_limit, out _, out _, out _, out report); }
            finally { ConvexHullBuilder.traceForTest = null; }
            TestContext.Out.WriteLine("==== 80 points, limit " + k_limit + ": built " + built + "; reason " + (report.reason ?? "none") + "; triangles " + report.triangles + ", merges " + report.merges + ", decimated " + report.decimated + ", expansion " + report.expansion.ToString("E2") + ", extent " + report.extent.ToString("R"));
            foreach (string line in trace) TestContext.Out.WriteLine(line);
        }

        private const int k_limit = 128;

        /// <summary>The diagnosis: every stage of the build written out (the triangle hull, the merges, each intersection, each decimation step, the double result and its float output), with and without the vertex limit. It asserts nothing but the input's identity.</summary>
        [Test]
        public void RefusedCandidate102_Stages()
        {
            Assert.That(k_points4.Length, Is.EqualTo(102));
            foreach (int limit in new[] { k_limit, 0 })
            {
                var trace = new List<string>();
                ConvexHullBuilder.traceForTest = trace;
                bool built;
                HullBuildReport report;
                float3[] v;
                try { built = ConvexHullBuilder.TryBuild(new List<float3>(k_points4), limit, out v, out _, out _, out report); }
                finally { ConvexHullBuilder.traceForTest = null; }
                TestContext.Out.WriteLine("==== limit " + limit + ": built " + built + (built ? ", " + v.Length + " vertices" : "") + "; reason " + (report.reason ?? "none") + "; triangles " + report.triangles + ", merges " + report.merges + ", decimated " + report.decimated + ", expansion " + report.expansion.ToString("E2") + ", extent " + report.extent.ToString("R"));
                foreach (string line in trace) TestContext.Out.WriteLine(line);
            }
        }

        /// <summary>
        /// **The triangle hull is convex** on every saved college input (the refused 102, the 52, the 123, the 131): no input
        /// point stands outside any of its triangles past the numeric tolerance. Before the exact orientation predicate the
        /// 102 points' triangle hull had 34 triangles with a point outside, the worst by 11.7 m.
        /// </summary>
        [Test]
        public void SavedInputs_TriangleHullIsConvex()
        {
            var inputs = new (string name, float3[] points)[] { ("refused 102", k_points4), ("refused 80", k_points5), ("college 52", ConvexHullBuilderCollegeReplayTests.k_points), ("college 123", ConvexHullBuilderCollegeReplayTests.k_points2), ("college 131", ConvexHullBuilderCollegeReplayTests.k_points3) };
            foreach ((string name, float3[] points) in inputs)
            {
                double worst = ConvexHullBuilder.TriangleHullWorstOutsideForTest(points, out int triangles);
                double numeric = ConvexHullBuilder.NumericTolerance * HullChecks.Extent(points);
                TestContext.Out.WriteLine(name + ": " + triangles + " triangles, an input point outside a triangle by " + worst.ToString("E2") + " m (numeric " + numeric.ToString("E2") + ")");
                Assert.That(worst, Is.LessThanOrEqualTo(numeric), name + ": the triangle hull is convex");
            }
        }

        /// <summary>**The fixed input builds within the limit and passes the independent verification** on its float output: every input point inside, planar faces, convex faces within their planes, a closed surface, the expansion within the merge tolerance, the vertices within 128.</summary>
        [Test]
        public void RefusedCandidate102_BuildsWithinTheLimit_AndVerifies()
        {
            bool built = ConvexHullBuilder.TryBuild(new List<float3>(k_points4), k_limit, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report);
            Assert.That(built, Is.True, report.reason);
            HullChecks.Measure(k_points4, v, offsets, indices, out double outside, out double nonConvex, out double nonPlanar, out double reflex);
            TestContext.Out.WriteLine("refused candidate 102: " + v.Length + " v " + (offsets.Length - 1) + " f, decimated " + report.decimated + ", expansion " + report.expansion.ToString("E2") + " m; measured here: outside " + outside.ToString("E2") + ", non-convex " + nonConvex.ToString("E2") + ", non-planar " + nonPlanar.ToString("E2") + ", reflex " + reflex.ToString("E2") + " m");
            HullChecks.AssertHull(k_points4, v, offsets, indices, report, "refused candidate 102", k_limit);
        }

        /// <summary>
        /// **The independent in-plane convexity sees a face that walks out along an edge and back** (the refused candidate's
        /// defect: a loop whose vertex order ran past its neighbour and returned). A cube whose edge 7-5 carries an extra
        /// vertex beyond 5 on its line, in both faces that share the edge: the surface is still closed (Euler 2), and the
        /// face polygons are measured with a reflex corner of about 0.9 m -- the check is not blind to it.
        /// </summary>
        [Test]
        public void InPlaneConvexity_SeesAFaceWalkingOutAlongAnEdgeAndBack()
        {
            var v = new[]
            {
                new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(-1f, 1f, -1f), new float3(1f, 1f, -1f),
                new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(-1f, 1f, 1f), new float3(1f, 1f, 1f),
                new float3(1f, -2f, 1f),   // 8: beyond 5 on the line 7-5
            };
            // Outward faces: +x with 7 -> 8 -> 5, +z with 5 -> 8 -> 7 (the shared edges paired), the others the cube's.
            int[][] faces = { new[] { 1, 3, 7, 8, 5 }, new[] { 0, 4, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 0, 1, 5, 4 }, new[] { 4, 5, 8, 7, 6 }, new[] { 0, 2, 3, 1 } };
            var offsets = new List<int> { 0 };
            var indices = new List<int>();
            foreach (int[] f in faces) { indices.AddRange(f); offsets.Add(indices.Count); }
            HullChecks.Measure(v, v, offsets.ToArray(), indices.ToArray(), out _, out _, out _, out double reflex, out double turning);
            TestContext.Out.WriteLine("a face walking out along an edge and back: reflex " + reflex.ToString("E2") + " m, turning off by " + turning.ToString("E2") + " rad");
            Assert.That(reflex, Is.GreaterThan(0.5), "the reflex corner is measured");
        }
    }
}
