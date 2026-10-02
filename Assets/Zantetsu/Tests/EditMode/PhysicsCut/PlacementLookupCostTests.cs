using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.MeshCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The product's placement lookup taken apart on fixed inputs (TL, 2026-10-01): display-only owners as a building's
    /// members are registered (each a child of its group's root), answered by PhysicsOwnerPlacementLookup. Its whole call and
    /// its parts, each part timed as a loop over every owner (never per call): the two tables (the Provisional pairs first,
    /// then the owners), the lookup's own checks of an owner (withdrawn, Root, correspondence) and the same checks repeated
    /// by the owner's read, the Root's transform, its world matrix and the product with the correspondence -- with nothing
    /// moved, and with the group's root moved before every pass. Then the building-like snapshot's phased Place with this
    /// lookup as its provider: the query block beside the lookup alone, the rest being the call's own side. Written out,
    /// not judged.
    /// </summary>
    public class PlacementLookupCostTests
    {
        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private sealed class Members : IDisposable
        {
            public readonly PhysicsOwnerRegistry registry = new PhysicsOwnerRegistry();
            public readonly List<LogicalFragmentId> fragments = new List<LogicalFragmentId>();
            public readonly List<PhysicsFragmentOwner> owners = new List<PhysicsFragmentOwner>();
            public readonly List<GameObject> made = new List<GameObject>();
            public GameObject group;

            public void Dispose()
            {
                registry.Dispose();
                foreach (GameObject g in made) UnityEngine.Object.DestroyImmediate(g);
            }
        }

        // A building's group root with its display members under it, each a display-only owner of one fragment.
        private static Members MakeMembers(IReadOnlyList<LogicalFragmentId> fragments)
        {
            var m = new Members();
            m.group = new GameObject("lookup cost group") { hideFlags = HideFlags.HideAndDontSave };
            m.made.Add(m.group);
            for (int i = 0; i < fragments.Count; i++)
            {
                var member = new GameObject("member " + i) { hideFlags = HideFlags.HideAndDontSave };
                member.transform.SetParent(m.group.transform, false);
                member.transform.localPosition = new Vector3(i % 13, i / 169, (i / 13) % 13);
                m.made.Add(member);
                PhysicsFragmentOwner owner = PhysicsFragmentOwner.DisplayOnly(member, Matrix4x4.identity, default);
                m.registry.Add(fragments[i], owner);
                m.fragments.Add(fragments[i]);
                m.owners.Add(owner);
            }

            return m;
        }

        [Test]
        public void TheLookup_ItsPartsAndItsCaller_AreWrittenOut()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            var ids = new List<LogicalFragmentId>();
            for (int i = 0; i < 2029; i++) ids.Add(ledger.AddFragment());
            using (Members m = MakeMembers(ids))
            {
                var lookup = new PhysicsOwnerPlacementLookup(m.registry);
                int n = m.owners.Count;
                var roots = new GameObject[n];
                var transforms = new Transform[n];
                for (int i = 0; i < n; i++) { roots[i] = m.owners[i].Root; transforms[i] = roots[i].transform; }
                Matrix4x4 sink = Matrix4x4.identity;
                int truths = 0;
                foreach (bool moved in new[] { false, true })
                {
                    var parts = new Dictionary<string, List<double>>();
                    void Time(string part, Action loop)
                    {
                        if (!parts.ContainsKey(part)) parts[part] = new List<double>();
                        if (moved) m.group.transform.position += new Vector3(0.001f, 0f, 0f);   // every member's world matrix stale again
                        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        loop();
                        parts[part].Add(Ms(begin));
                    }

                    for (int k = 0; k < 10; k++)
                    {
                        Time("the lookup, whole", () => { for (int i = 0; i < n; i++) if (lookup.TryGetGeometryLocalToWorld(m.fragments[i], default, 0f, out Matrix4x4 g) == VpFragmentPlacementKind.Following) sink = g; });
                        Time("the two tables", () => { for (int i = 0; i < n; i++) { if (!m.registry.TryGetProvisionalOf(m.fragments[i], out _) && m.registry.TryGet(m.fragments[i], out PhysicsFragmentOwner o) && o != null) truths++; } });
                        Time("one set of checks (withdrawn, Root, correspondence)", () => { for (int i = 0; i < n; i++) { PhysicsFragmentOwner o = m.owners[i]; if (!(o.IsWithdrawn || o.Root == null || o.GeometryLocalToOwner == null)) truths++; } });
                        Time("  of which Root == null (Unity's check)", () => { for (int i = 0; i < n; i++) if (m.owners[i].Root == null) truths++; });
                        Time("  of which the correspondence == null", () => { for (int i = 0; i < n; i++) if (m.owners[i].GeometryLocalToOwner == null) truths++; });
                        Time("the owner's read, whole (its checks, transform, matrix, product)", () => { for (int i = 0; i < n; i++) if (m.owners[i].TryReadGeometryLocalToWorld(out Matrix4x4 g)) sink = g; });
                        Time("Root.transform", () => { for (int i = 0; i < n; i++) if (roots[i].transform == null) truths++; });
                        Time("localToWorldMatrix", () => { for (int i = 0; i < n; i++) sink = transforms[i].localToWorldMatrix; });
                        Time("the product with the correspondence (its value read from the owner)", () => { Matrix4x4 w = transforms[0].localToWorldMatrix; for (int i = 0; i < n; i++) sink = w * m.owners[i].GeometryLocalToOwner.Value; });
                        Time("  of which the correspondence's value read", () => { for (int i = 0; i < n; i++) sink = m.owners[i].GeometryLocalToOwner.Value; });
                    }

                    TestContext.Out.WriteLine((moved ? "the group's root moved before every pass" : "nothing moved") + " (" + n + " owners, medians of 9 passes after one, ms):");
                    foreach (KeyValuePair<string, List<double>> p in parts)
                    {
                        p.Value.RemoveAt(0);
                        TestContext.Out.WriteLine("  " + p.Key + ": " + Median(p.Value).ToString("F3") + " (" + (Median(p.Value) * 1000.0 / n).ToString("F3") + " us an owner)");
                    }
                }

                TestContext.Out.WriteLine("(" + truths + sink.m00 + ")");
            }
        }

        [Test]
        public void ThePlacePassesQueryBlock_WithTheProductsLookup_IsWrittenOut()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> plain, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            List<VpMultiCutRegistration> registrations = VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(plain);
            var probe = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
            Assert.That(probe.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            var stands = new List<LogicalFragmentId>();
            var seen = new HashSet<LogicalFragmentId>();
            for (int r = 0; r < probe.RenderFragmentCount; r++) { LogicalFragmentId f = probe.StandsAsForTest(r).fragment; if (seen.Add(f)) stands.Add(f); }
            using (Members m = MakeMembers(stands))
            {
                var lookup = new PhysicsOwnerPlacementLookup(m.registry);
                var structure = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
                Assert.That(structure.TryBuild(ledger, registrations, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                var target = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
                var block = new List<double>();
                var check = new List<double>();
                var rest = new List<double>();
                var alone = new List<double>();
                try
                {
                    VpMultiCutSnapshot.PlacePhasedDiagnosis = true;
                    for (int k = 0; k < 10; k++)
                    {
                        var before = new VpPlaceCounts();
                        before.CopyFrom(target.PlacementOnlyPlaceCounts);
                        Assert.That(target.TryBuildPlacementsFrom(structure, ledger, registrations, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        var d = new VpPlaceCounts();
                        d.AddDifference(target.PlacementOnlyPlaceCounts, before);
                        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        for (int r = 0; r < structure.RenderFragmentCount; r++)
                        {
                            var s = structure.StandsAsForTest(r);
                            lookup.TryGetGeometryLocalToWorld(s.fragment, s.operation, s.side, out _);
                        }

                        double lookupAlone = Ms(begin);
                        if (k == 0) continue;
                        block.Add(d.providerSeconds * 1000); check.Add(d.checkSeconds * 1000); rest.Add(d.restSeconds * 1000); alone.Add(lookupAlone);
                    }
                }
                finally
                {
                    VpMultiCutSnapshot.PlacePhasedDiagnosis = false;
                }

                int n = structure.RenderFragmentCount;
                TestContext.Out.WriteLine("building-like, " + n + " render fragments, the product's lookup over display-only owners (medians of 9, ms): the query block " + Median(block).ToString("F3")
                    + " (" + (Median(block) * 1000 / n).ToString("F3") + " us a render fragment) = the lookup alone " + Median(alone).ToString("F3") + " (" + (Median(alone) * 1000 / n).ToString("F3")
                    + " us) + the call's own side " + (Median(block) - Median(alone)).ToString("F3") + "; checks " + Median(check).ToString("F3") + ", the rest " + Median(rest).ToString("F3"));
            }
        }
    }
}
