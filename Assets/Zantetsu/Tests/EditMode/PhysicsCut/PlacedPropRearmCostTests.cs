using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// What a placed prop's re-arming costs (DESIGN 7.10: a prop given back its instance after its pieces were
    /// collected is made a cut target again, and its convexes are prepared and cooked again, since the ones it had
    /// went to its cut's owner). On Quest 3S one re-arming took 2.9 ms on the main thread (2026-10-10). The cost is
    /// split here, in the Editor, over every prop input of the licensed city: the brep bank built from the input, the
    /// prepared physics input made from it (the convex meshes baked by PhysX, the shape, the mesh frames), and the
    /// same input made from meshes kept baked (<see cref="VpBakedConvexMeshes"/>, the crowd's way) -- many samples of
    /// each in one run, the median and the largest. A record, not a verdict; ignored where the licensed inputs are absent.
    /// </summary>
    public class PlacedPropRearmCostTests
    {
        private const string InputFolder = "Assets/Licensed/WalkCity/Inputs";
        private const int Samples = 12;

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static double Median(List<double> v)
        {
            var s = new List<double>(v);
            s.Sort();
            return s[s.Count / 2];
        }

        [Test]
        public void APropsRearming_ItsBankItsCookedInputAndTheSameFromKeptMeshes_AreMeasuredForTheRecord()
        {
            if (!Directory.Exists(InputFolder)) Assert.Ignore("the licensed city walk inputs are not in this checkout: " + InputFolder);
            var props = new List<(string name, PlacedCuttableInput data)>();
            foreach (string path in Directory.GetFiles(InputFolder, "*.json").OrderBy(p => p))
            {
                var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(path));
                if (data != null && data.isCuttable && !data.isBuilding && data.hulls != null && data.hulls.Length > 0)
                {
                    props.Add((Path.GetFileNameWithoutExtension(path), data));
                }
            }

            Assert.That(props.Count, Is.GreaterThan(0), "the layout: prop inputs");
            var lines = new List<(double cooked, string line)>();
            double bankAll = 0, cookedAll = 0, keptAll = 0;
            foreach ((string name, PlacedCuttableInput data) in props)
            {
                var bank = new List<double>();
                var cooked = new List<double>();
                var kept = new List<double>();
                int convexes = data.hulls.Length, vertices = data.hulls.Sum(h => h.vertices.Length);
                for (int i = 0; i < Samples + 2; i++)
                {
                    // Exactly what PlacedCuttableCandidate.TryRearm does: the bank built from the input, then the input made from it.
                    var natives = new List<IDisposable>();
                    VpPreparedPhysicsInput input = null, fromKept = null;
                    VpBakedConvexMeshes baked = null;
                    try
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        ConvexBrepBank bank0 = PlacedCuttableRegistration.BuildBank(data.hulls, natives, out List<ConvexBrepRange> ranges);
                        long t1 = Stopwatch.GetTimestamp();
                        input = new VpPreparedPhysicsInput(bank0, ranges);
                        long t2 = Stopwatch.GetTimestamp();

                        // The crowd's way: the meshes baked once and kept, the next input made from them without a bake.
                        baked = new VpBakedConvexMeshes(bank0, ranges);
                        long t3 = Stopwatch.GetTimestamp();
                        fromKept = new VpPreparedPhysicsInput(bank0, ranges, baked);
                        long t4 = Stopwatch.GetTimestamp();
                        if (i >= 2)
                        {
                            bank.Add(Ms(t1 - t0));
                            cooked.Add(Ms(t2 - t1));
                            kept.Add(Ms(t4 - t3));
                        }
                    }
                    finally
                    {
                        fromKept?.Dispose();
                        input?.Dispose();
                        baked?.Dispose();
                        foreach (IDisposable n in natives) n.Dispose();
                    }
                }

                bankAll += Median(bank);
                cookedAll += Median(cooked);
                keptAll += Median(kept);
                lines.Add((Median(cooked), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0,-22} convexes {1,2} vertices {2,4} | bank median {3,6:F3} max {4,6:F3} | cooked input median {5,6:F3} max {6,6:F3} | from kept meshes median {7,6:F3} max {8,6:F3} ms",
                    name, convexes, vertices, Median(bank), bank.Max(), Median(cooked), cooked.Max(), Median(kept), kept.Max())));
            }

            lines.Sort((a, b) => b.cooked.CompareTo(a.cooked));   // the costliest cooked input first
            TestContext.Out.WriteLine("prop re-arming in the Editor (" + Samples + " samples a prop after 2 warm-ups; the Editor's figures, not a device's):");
            foreach ((double _, string l) in lines) TestContext.Out.WriteLine("  " + l);
            TestContext.Out.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "sums of the medians over {0} props: bank {1:F3} ms, cooked input {2:F3} ms, from kept meshes {3:F3} ms",
                props.Count, bankAll, cookedAll, keptAll));
            Assert.That(props.Count, Is.GreaterThan(0));
        }
    }
}
