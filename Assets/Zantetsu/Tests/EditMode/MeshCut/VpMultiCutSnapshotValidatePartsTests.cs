using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The validation's parts on a fixed input shaped like the coexistence run's building (TL, 2026-10-01): one lineage
    /// cut by 36 hits, each splitting up to 40 + hit of its current leaves (every leaf while fewer), the leaves registered,
    /// each geometry reflecting its whole chain -- some 1,900 registrations under as many operations, the deepest
    /// leaf some 36 cuts down -- plus 120 shallow registrations (a crowd's pieces). The structural validation's and the
    /// placement-only validation's parts are measured by VpMultiCutSnapshotFinitenessTests on this input.
    /// </summary>
    public class VpMultiCutSnapshotValidatePartsTests
    {
        private static float4 Plane(int k) => new float4(0f, 1f, 0f, -0.05f * (k % 10 + 1));

        internal static (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, int deepest) BuildingLike(int hits = 36, int shallow = 120)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64));
            var leaves = new List<(LogicalFragmentId fragment, List<VpClipBoundary> chain)> { (ledger.AddFragment(), new List<VpClipBoundary>()) };
            int k = 0;
            for (int hit = 1; hit <= hits; hit++)
            {
                int cross = System.Math.Min(leaves.Count, 40 + hit);
                int stride = System.Math.Max(1, leaves.Count / cross);
                var next = new List<(LogicalFragmentId, List<VpClipBoundary>)>(leaves.Count + cross);
                int crossed = 0;
                for (int i = 0; i < leaves.Count; i++)
                {
                    (LogicalFragmentId f, List<VpClipBoundary> chain) = leaves[i];
                    if (crossed < cross && i % stride == 0)
                    {
                        Assert.That(ledger.Admit(f, Plane(k++), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
                        Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                        Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
                        Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
                        next.Add((positive, new List<VpClipBoundary>(chain) { new VpClipBoundary(new VpCapFace(ledger, cut), 1f) }));
                        next.Add((negative, new List<VpClipBoundary>(chain) { new VpClipBoundary(new VpCapFace(ledger, cut), -1f) }));
                        crossed++;
                    }
                    else
                    {
                        next.Add((f, chain));
                    }
                }

                leaves = next;
            }

            var registrations = new List<VpMultiCutRegistration>(leaves.Count + shallow);
            int deepest = 0, r = 0;
            foreach ((LogicalFragmentId f, List<VpClipBoundary> chain) in leaves)
            {
                deepest = System.Math.Max(deepest, chain.Count);
                registrations.Add(new VpMultiCutRegistration(f, new Bounds(Vector3.zero, Vector3.one * 2f), Matrix4x4.Translate(new Vector3(0.01f * r++, 0f, 0f)), Matrix4x4.identity, chain.ToArray(), 0.001f));
            }

            for (int s = 0; s < shallow; s++)
            {
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(ledger.Admit(root, Plane(k++), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
                Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
                Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
                registrations.Add(new VpMultiCutRegistration(positive, new Bounds(Vector3.zero, Vector3.one), Matrix4x4.Translate(new Vector3(0f, 5f + s, 0f)), Matrix4x4.identity, new[] { new VpClipBoundary(new VpCapFace(ledger, cut), 1f) }, 0.001f));
            }

            return (ledger, registrations, deepest);
        }
    }
}
