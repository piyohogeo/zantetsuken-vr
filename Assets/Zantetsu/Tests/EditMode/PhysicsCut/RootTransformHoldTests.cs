using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.MeshCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// An owner's Root's Transform held from the Root's giving to its letting go (TL, 2026-10-01), its world matrix read at
    /// every placement read as before. Against asking the Root for its Transform at every read (the read before): the same
    /// answer and the same matrix, element by element and exactly, and the same lookup answer -- through a parent's and the
    /// Root's own moves, a rotation, a non-uniform scale, a reparenting, a withdrawal, the Root destroyed from outside and
    /// the owner released (its Transform let go with its Root); with correspondences identity and not. The building-like
    /// snapshot built with the product's lookup, the same item by item. And the same binary's cost, old and new: the lookup
    /// alone and the placement-only Place. Written out where it is a cost.
    /// </summary>
    public class RootTransformHoldTests
    {
        private static (bool ok, Matrix4x4 m) Read(PhysicsFragmentOwner o, bool old)
        {
            PhysicsFragmentOwner.rootTransformReadAgainForTest = old;
            try
            {
                bool ok = o.TryReadGeometryLocalToWorld(out Matrix4x4 m);
                return (ok, m);
            }
            finally
            {
                PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
            }
        }

        private static (VpFragmentPlacementKind kind, Matrix4x4 m) Ask(PhysicsOwnerPlacementLookup lookup, LogicalFragmentId f, bool old)
        {
            PhysicsFragmentOwner.rootTransformReadAgainForTest = old;
            try
            {
                VpFragmentPlacementKind kind = lookup.TryGetGeometryLocalToWorld(f, default, 0f, out Matrix4x4 m);
                return (kind, m);
            }
            finally
            {
                PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
            }
        }

        private static void SameExactly(string what, Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                Assert.That(System.BitConverter.SingleToInt32Bits(a[i]), Is.EqualTo(System.BitConverter.SingleToInt32Bits(b[i])), what + ": element " + i + " (" + a[i].ToString("R") + " / " + b[i].ToString("R") + ")");
            }
        }

        [Test]
        public void HoldingTheRootsTransform_ReadsAsAskingTheRootEveryTime()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            var made = new List<GameObject>();
            using var registry = new PhysicsOwnerRegistry();
            try
            {
                var group = new GameObject("hold group") { hideFlags = HideFlags.HideAndDontSave };
                var other = new GameObject("hold other parent") { hideFlags = HideFlags.HideAndDontSave };
                made.Add(group); made.Add(other);
                Matrix4x4[] correspondences =
                {
                    Matrix4x4.identity,
                    Matrix4x4.TRS(new Vector3(0.3f, -0.2f, 1.7f), Quaternion.Euler(10f, 35f, -20f), Vector3.one),
                    Matrix4x4.TRS(new Vector3(-1f, 2f, 0.5f), Quaternion.Euler(0f, 90f, 0f), new Vector3(2f, 0.5f, 1.25f)),
                };
                var fragments = new List<LogicalFragmentId>();
                var owners = new List<PhysicsFragmentOwner>();
                var roots = new List<GameObject>();
                for (int i = 0; i < 12; i++)
                {
                    var member = new GameObject("hold member " + i) { hideFlags = HideFlags.HideAndDontSave };
                    member.transform.SetParent(group.transform, false);
                    member.transform.localPosition = new Vector3(i, 0.5f * i, -i);
                    made.Add(member);
                    LogicalFragmentId f = ledger.AddFragment();
                    PhysicsFragmentOwner o = PhysicsFragmentOwner.DisplayOnly(member, correspondences[i % correspondences.Length], default);
                    registry.Add(f, o);
                    fragments.Add(f); owners.Add(o); roots.Add(member);
                    Assert.That(o.HoldsRootTransformForTest, Is.True, "held from the Root's giving");
                }

                var lookup = new PhysicsOwnerPlacementLookup(registry);
                int compared = 0;
                void Compare(string when)
                {
                    for (int i = 0; i < owners.Count; i++)
                    {
                        (bool ok, Matrix4x4 m) a = Read(owners[i], true), b = Read(owners[i], false);
                        Assert.That(b.ok, Is.EqualTo(a.ok), when + ", owner " + i + ": the same answer");
                        SameExactly(when + ", owner " + i, b.m, a.m);
                        (VpFragmentPlacementKind kind, Matrix4x4 m) la = Ask(lookup, fragments[i], true), lb = Ask(lookup, fragments[i], false);
                        Assert.That(lb.kind, Is.EqualTo(la.kind), when + ", owner " + i + ": the same lookup answer");
                        SameExactly(when + ", owner " + i + " (lookup)", lb.m, la.m);
                        compared++;
                    }
                }

                Compare("as made");
                group.transform.position += new Vector3(3f, -1f, 2f);
                Compare("the parent moved");
                roots[1].transform.localPosition += new Vector3(0.25f, 0f, -4f);
                Compare("a Root moved");
                group.transform.rotation = Quaternion.Euler(15f, 70f, 5f);
                Compare("the parent turned");
                roots[2].transform.localScale = new Vector3(1.5f, 0.25f, 3f);
                Compare("a Root scaled unevenly");
                roots[3].transform.SetParent(other.transform, true);
                other.transform.position = new Vector3(-5f, 1f, 9f);
                Compare("a Root given another parent, which moved");
                Assert.That(registry.Withdraw(fragments[4]), Is.True);
                Assert.That(Read(owners[4], false).ok, Is.False, "a withdrawn owner is read as nothing");
                Compare("an owner withdrawn");
                Object.DestroyImmediate(roots[5]);
                Assert.That(Read(owners[5], false).ok, Is.False, "an owner whose Root was destroyed from outside is read as nothing");
                Compare("a Root destroyed from outside");
                owners[6].Release();
                Assert.That(owners[6].HoldsRootTransformForTest, Is.False, "let go with its Root");
                Assert.That(Read(owners[6], false).ok, Is.False, "a released owner is read as nothing");
                Compare("an owner released");
                TestContext.Out.WriteLine("reads compared " + compared + " (old and new, the owner's read and the lookup)");
            }
            finally
            {
                PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
                foreach (GameObject g in made) if (g != null) Object.DestroyImmediate(g);
            }
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        private static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        [Test]
        public void TheBuildingLikeSnapshot_TheSame_AndTheSameBinarysCost_AreWrittenOut()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> plain, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike();
            List<VpMultiCutRegistration> registrations = VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(plain);
            var probe = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
            Assert.That(probe.TryBuild(ledger, registrations), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            var stands = new List<LogicalFragmentId>();
            var seen = new HashSet<LogicalFragmentId>();
            for (int r = 0; r < probe.RenderFragmentCount; r++) { LogicalFragmentId f = probe.StandsAsForTest(r).fragment; if (seen.Add(f)) stands.Add(f); }
            var made = new List<GameObject>();
            using var registry = new PhysicsOwnerRegistry();
            try
            {
                var group = new GameObject("hold cost group") { hideFlags = HideFlags.HideAndDontSave };
                made.Add(group);
                var members = new List<Transform>();
                for (int i = 0; i < stands.Count; i++)
                {
                    var member = new GameObject("hold cost member " + i) { hideFlags = HideFlags.HideAndDontSave };
                    member.transform.SetParent(group.transform, false);
                    member.transform.localPosition = new Vector3(i % 13, i / 169, (i / 13) % 13);
                    made.Add(member);
                    members.Add(member.transform);
                    Matrix4x4 correspondence = i % 3 == 0 ? Matrix4x4.identity : Matrix4x4.TRS(new Vector3(0.1f * (i % 7), 0f, 0f), Quaternion.Euler(0f, i % 11, 0f), Vector3.one);
                    registry.Add(stands[i], PhysicsFragmentOwner.DisplayOnly(member.gameObject, correspondence, default));
                }

                var lookup = new PhysicsOwnerPlacementLookup(registry);
                var structure = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
                Assert.That(structure.TryBuild(ledger, registrations, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));

                // The snapshot, old and new, as the members stand and after they move, turn and scale.
                for (int step = 0; step < 3; step++)
                {
                    if (step == 1) group.transform.SetPositionAndRotation(new Vector3(2f, 0f, -3f), Quaternion.Euler(0f, 25f, 0f));
                    if (step == 2) for (int i = 0; i < members.Count; i += 5) members[i].localScale = new Vector3(1.25f, 0.8f, 1f);
                    var contents = new Dictionary<bool, List<string>>();
                    foreach (bool old in new[] { true, false })
                    {
                        var s = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));
                        PhysicsFragmentOwner.rootTransformReadAgainForTest = old;
                        VpMultiCutBuildOutcome o = s.TryBuildPlacementsFrom(structure, ledger, registrations, lookup);
                        PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
                        Assert.That(o, Is.EqualTo(VpMultiCutBuildOutcome.Built));
                        contents[old] = VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger);
                    }

                    Assert.That(contents[false].Count, Is.EqualTo(contents[true].Count), "step " + step + ": as many items");
                    for (int i = 0; i < contents[true].Count; i++) Assert.That(contents[false][i], Is.EqualTo(contents[true][i]), "step " + step + ": item " + i);
                }

                // The same binary's cost, old and new, alternating: the lookup alone over every render fragment's ask, and the Place pass.
                var lookupTimes = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                var placeTimes = new Dictionary<bool, List<double>> { [true] = new List<double>(), [false] = new List<double>() };
                var targets = new Dictionary<bool, VpMultiCutSnapshot> { [true] = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256)), [false] = new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256)) };
                for (int k = 0; k < 20; k++)
                {
                    bool old = (k & 1) == 0;
                    PhysicsFragmentOwner.rootTransformReadAgainForTest = old;
                    long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (int r = 0; r < structure.RenderFragmentCount; r++)
                    {
                        var st = structure.StandsAsForTest(r);
                        lookup.TryGetGeometryLocalToWorld(st.fragment, st.operation, st.side, out _);
                    }

                    double lookupMs = Ms(begin);
                    var before = new VpPlaceCounts();
                    before.CopyFrom(targets[old].PlacementOnlyPlaceCounts);
                    Assert.That(targets[old].TryBuildPlacementsFrom(structure, ledger, registrations, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
                    var d = new VpPlaceCounts();
                    d.AddDifference(targets[old].PlacementOnlyPlaceCounts, before);
                    if (k < 2) continue;
                    lookupTimes[old].Add(lookupMs);
                    placeTimes[old].Add(d.seconds * 1000);
                }

                int n = structure.RenderFragmentCount;
                TestContext.Out.WriteLine("building-like, " + n + " render fragments over display-only owners (a third with the identity correspondence), medians of 9, ms: the lookup alone old "
                    + Median(lookupTimes[true]).ToString("F3") + " new " + Median(lookupTimes[false]).ToString("F3") + " (" + ((Median(lookupTimes[true]) - Median(lookupTimes[false])) * 1000 / n).ToString("F3")
                    + " us an owner less); the placement-only Place old " + Median(placeTimes[true]).ToString("F3") + " new " + Median(placeTimes[false]).ToString("F3"));
            }
            finally
            {
                PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
                foreach (GameObject g in made) if (g != null) Object.DestroyImmediate(g);
            }
        }
    }
}
