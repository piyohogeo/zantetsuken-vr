using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The real building college_001 (the private one-anchor input, anchor index 0, the floor at its base) as one hull
    /// group under the building rest (TL, 2026-09-30): ten cuts by different Slashes, the sides resting and fused back
    /// by the deadline and by the next Slash both; the colliders, hulls, bodies and groups counted at their most and
    /// after the aggregation; every collider's body checked; no member collider; the fusion's expansion in metres, the
    /// gap filled and the penetration before and after each exchange; and the Main cost by side. Ignored where the
    /// licensed input is absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string CollegeInputPath = "Assets/Licensed/BuildingSlashE2E/InputsWound/college_001-anchor0.json";

        /// <summary>One update of a wave in a plane through <paramref name="centre"/> with the normal given, spanning 40 m along u and v, travelling along u.</summary>
        private static SlashSweep Wide(long slash, Vector3 normal, Vector3 centre, Vector3 u, Vector3 v)
        {
            const float half = 20f;
            return new SlashSweep(slash, 0.0, false, new Plane(normal, centre), u, v,
                centre - u * half - v * half, centre - u * half + v * half, centre + u * half - v * half, centre + u * half + v * half);
        }

        private static int CollidersUnder(Transform root)
        {
            return root.GetComponentsInChildren<Collider>(true).Length;
        }

        /// <summary>
        /// **The Player's large penetration, reconstructed as far as the saved information allows: the one-anchor building
        /// whose bulk is the free side.** The Player's first cut left the anchor on a 630 kg slab and freed the 9370 kg
        /// bulk; two free pieces united in the air fused into a hull that penetrated something by 2.31 m. Here a cut
        /// through the building beside its anchor frees the bulk; a second, level cut of the bulk frees its top, which
        /// is pushed off to land apart. Under this unit's rule no free group is united: each rests and is held first;
        /// each union's candidate hull is judged against every opponent (the floor, the other groups) before adoption,
        /// the comparison recorded by opponent, and a candidate that would add a penetration keeps the hulls as they are.
        /// </summary>
        [UnityTest]
        public IEnumerator CollegeHull_BulkFreedBesideTheAnchor_FusionCandidatesJudgedByOpponent()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            float baseY = float.MaxValue;
            foreach (Vector3 v in data.hulls[0].vertices) baseY = Mathf.Min(baseY, v.y);
            CutWorldRoot root = NewHullWorld(0.9f);
            BuildingHullFusion h = root.Hulls;
            GameObject instance = TrackActor(new GameObject("college_001 instance"));
            instance.transform.position = new Vector3(0f, -1f - baseY, 0f);
            PlacedCuttableRegistration made = PlacedCuttableRegistration.RegisterHull(root, data, instance.transform, new Renderer[0], new Collider[0], 10000f, SideMaterial);
            _actors.Add(made.Actor);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Vector3 anchor = made.Actor.transform.TransformPoint(data.anchors[0]);
            // The cut beside the anchor: the slab with the anchor stays, the bulk (most of the mass) is free.
            Evaluate(detector, Wide(1, Vector3.right, new Vector3(anchor.x - 2f, anchor.y + 10f, anchor.z), Vector3.up, Vector3.forward), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilCutsEnd(root, 30f, "the cut beside the anchor");
            HullGroup bulk = FreeSide(h);
            Assert.That(bulk, Is.Not.Null.And.Property("Mass").GreaterThan(5000.0), "the bulk is the free side (" + (bulk != null ? bulk.Mass : 0) + " kg)");
            // The bulk cut level while free; its top pushed off, to land apart.
            float3 c = CentreOf(bulk);
            Evaluate(detector, Wide(2, Vector3.up, new Vector3(c.x, c.y + 3f, c.z), Vector3.right, Vector3.forward), 1, 2);
            Assert.That(detector.HitCount, Is.GreaterThanOrEqualTo(1), "the bulk is cut level");
            yield return UntilCutsEnd(root, 30f, "the level cut");
            foreach (HullGroup g in h.Groups)
            {
                if (g.Kinematic || g.Body == null) continue;
                if (CentreOf(g).y > c.y + 3f) g.Body.linearVelocity = new Vector3(-6f, 0f, 0f);   // the top, off to the side
            }

            yield return UntilWithin(() => h.AllGroupsAtRest && h.CutsInProgress == 0, 90f, "every piece at rest (held or anchored)");
            yield return UntilWithin(() => h.IsSettled, 90f, "the aggregation decided: fused, or one hull not achieved");
            WriteHullRecord(root, "after the aggregation of the resting pieces");
            TestContext.Out.WriteLine(RestRecord(root));
            TestContext.Out.WriteLine("comparison: unions " + h.Unions + ", fusions " + h.Fusions + ", not achieved " + h.HullsNotAchieved + " (rejected new penetration max " + h.MaxRejectedNewPenetration.ToString("F3") + " m), adopted: before " + h.MaxPenetrationBeforeExchange.ToString("F4") + " after " + h.MaxFusionPenetration.ToString("F4") + " added " + h.MaxNewPenetration.ToString("F4") + " m; filled " + h.OverhangVolume.ToString("F2") + " m3");
            Assert.That(h.Unions, Is.GreaterThanOrEqualTo(1), "the resting pieces were united");
            Assert.That(h.AggregationsEnded, Is.EqualTo(h.AggregationsBegun), "every aggregation was fused or recorded as one hull not achieved (aggregations " + h.AggregationsBegun + " of " + h.Unions + " unions)");
            Assert.That(h.FusionsGivenUp, Is.Zero);
            Assert.That(h.MaxNewPenetration, Is.LessThanOrEqualTo((float)h.Settings.maxNewPenetrationMetres + 1e-4f), "no adopted hull added a penetration past the settings");
            foreach (string n in h.NotAchieved) Assert.That(n, Does.Contain("would penetrate").And.Contain(" by "), "a rejected candidate names its opponent: " + n);
            Assert.That(h.CutsFailed, Is.Zero);
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator CollegeHull_TenCutsRestAndRefuse_KeepOneHullPerGroup()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            Assert.That(data.hulls.Length == 1 && data.anchors.Length == 1 && data.isBuilding, Is.True, "college_001 with one convex and one anchor");
            float baseY = float.MaxValue;
            foreach (Vector3 v in data.hulls[0].vertices) baseY = Mathf.Min(baseY, v.y);

            CutWorldRoot root = NewHullWorld(0.9f);
            BuildingHullFusion h = root.Hulls;
            // The instance stands so that the convex's base is on the floor's top (y = -1); the floor is the fixture's.
            GameObject instance = TrackActor(new GameObject("college_001 instance"));
            instance.transform.position = new Vector3(0f, -1f - baseY, 0f);
            PlacedCuttableRegistration made = PlacedCuttableRegistration.RegisterHull(root, data, instance.transform, new Renderer[0], new Collider[0], 10000f, SideMaterial);   // the fixture's display binds this source material index
            _actors.Add(made.Actor);
            TestContext.Out.WriteLine("registered: " + made.Description);
            HullGroup building = made.Group;
            Assert.That(building.Anchored && building.Kinematic && building.Body.isKinematic, Is.True, "fixed by its one anchor");
            Assert.That(root.Rest.TrackedAnchored, Is.EqualTo(1), "the rest's ground");
            Assert.That(CollidersUnder(made.Actor.transform), Is.EqualTo(1), "one collider: the hull's; none of the instance's");
            Assert.That(instance.GetComponentsInChildren<Renderer>(true).Length, Is.Zero, "nothing drawn twice");
            AssertDisplayConsistent(root, "registered");
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;

            var simulates = new List<double>();
            int successes = 0, attempts = 0, notCounted = 0;
            long slash = 100;
            while (successes < 10 && attempts < 24)
            {
                attempts++;
                // A next-Slash round: the sides must be at rest (all kinematic) before the deadline, so that the next
                // Slash brings the fusion on; a deadline round waits for the fusion first.
                if (successes % 2 == 1)
                {
                    bool allKinematic() { foreach (HullGroup g in h.Groups) if (!g.Kinematic) return false; return h.CutsInProgress == 0; }
                    yield return UntilWithin(allKinematic, 30f, "attempt " + attempts + ": every side at rest");
                }
                else
                {
                    yield return UntilOneGroup(root, "attempt " + attempts + ": fused by the deadline", 60f);
                }

                HullGroup g0 = h.Groups[0];
                g0.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                float3 size = hi - lo;
                float3 c = math.transform((float4x4)g0.Root.transform.localToWorldMatrix, (lo + hi) * 0.5f);
                int kind = attempts % 3;
                float off = (attempts % 5 - 2) * 0.12f;   // -0.24 .. +0.24 of the size
                Vector3 normal = kind == 0 ? Vector3.right : kind == 1 ? Vector3.up : Vector3.forward;
                Vector3 centre = (Vector3)c + normal * (off * (kind == 0 ? size.x : kind == 1 ? size.y : size.z));
                Vector3 u = kind == 1 ? Vector3.right : Vector3.up, v = kind == 2 ? Vector3.right : Vector3.forward;
                slash++;
                Evaluate(detector, Wide(slash, normal, centre, u, v), slash);
                if (detector.HitCount == 0) { TestContext.Out.WriteLine("attempt " + attempts + ": the sweep missed; not counted"); notCounted++; continue; }
                int cuts = h.GroupCuts, hits = h.Hits.Count;
                // Held behind a fusion or prepared at once: answered when it is published or refused, the cut over.
                yield return UntilHull(root, () => !h.Hits[hits - 1].IsPending && h.CutsInProgress == 0, 90f, "attempt " + attempts + ": the hit answered");
                if (h.GroupCuts == cuts) { TestContext.Out.WriteLine("attempt " + attempts + ": " + h.Hits[hits - 1].outcome + "; not counted"); notCounted++; continue; }
                successes++;
                yield return Steps(1);
                simulates.Add(CutPhysicsStep.LastSimulateSeconds * 1000.0);
                int sceneColliders = 0;
                foreach (Collider col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None)) if (col.gameObject != _hullFloor) sceneColliders++;
                Assert.That(sceneColliders, Is.EqualTo(h.GroupCount), "attempt " + attempts + ": one collider a group (" + h.GroupCount + " groups)");
                if (h.GroupCount > 2) { WriteHullRecord(root, "attempt " + attempts + ": more than two groups stand after the cut"); TestContext.Out.WriteLine(RestRecord(root)); }
                foreach (HullGroup g in h.Groups) Assert.That(g.VertexCount, Is.LessThanOrEqualTo(128), "attempt " + attempts + ": a side's hull within the limit");
            }

            yield return UntilOneGroup(root, "the last pair fused", 60f);
            yield return Steps(1);
            WriteHullRecord(root, "after ten cuts");
            TestContext.Out.WriteLine(RestRecord(root));
            TestContext.Out.WriteLine("Simulate after each publication (ms): " + string.Join(" / ", simulates.ConvertAll(x => x.ToString("F3"))));
            TestContext.Out.WriteLine("Main ms: physics " + (h.MainPhysicsSeconds * 1000).ToString("F3") + ", display " + (h.MainDisplaySeconds * 1000).ToString("F3") + " (worker scan " + (h.FusionWorkerSeconds * 1000).ToString("F3") + ", worker bake " + (h.FusionBakeSeconds * 1000).ToString("F3") + " apart); rest support/rest " + (root.Rest.SupportSeconds * 1000).ToString("F3") + "/" + (root.Rest.SleepSeconds * 1000).ToString("F3"));
            foreach (string o in h.OverrunUnits) TestContext.Out.WriteLine("  overrun: " + o);
            Assert.That(successes, Is.EqualTo(10), "ten cuts published (attempts " + attempts + ", not counted " + notCounted + ")");
            Assert.That(h.GroupCuts, Is.GreaterThanOrEqualTo(10), "ten or more cuts (a sweep may cut both groups)");
            Assert.That(h.Fusions, Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(h.Unions), "fusions after unions");
            Assert.That(h.UnionsByDeadline, Is.GreaterThanOrEqualTo(1), "fused by the deadline");
            Assert.That(h.UnionsByNextSlash, Is.GreaterThanOrEqualTo(1), "and by the next Slash");
            Assert.That(h.CutsFailed, Is.Zero);

            // The physics after the aggregation: one group, one body, one hull, one collider; every collider on a group's body; no member collider.
            HullGroup whole = h.Groups[0];
            Assert.That(h.GroupCount == 1 && whole.HullCount == 1 && h.LiveColliders == 1, Is.True, "one group, one hull, one collider");
            // A sweep across the building can cut both of its groups at once, and a class judged apart stands apart while it is cut again: the most groups at once is recorded (the count's growth is an open item apart), one hull a group always.
            TestContext.Out.WriteLine("most groups at once " + h.MaxGroups + " (hulls " + h.MaxLiveHulls + ")");
            Assert.That(h.MaxLiveHulls, Is.EqualTo(h.MaxGroups), "one hull a group, always");
            // The registration's actor may be gone (a union keeps the anchored or heavier side's Root): the audit reads the scene.
            int colliders = 0, bodies = 0;
            foreach (Collider col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (col.gameObject == _hullFloor) continue;
                colliders++;
                Assert.That(col.attachedRigidbody == whole.Body, Is.True, "collider " + col.name + " is on the group's body");
                Assert.That(col == whole.Collider, Is.True, "and is the hull's");
            }

            foreach (Rigidbody body in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)) { bodies++; Assert.That(body == whole.Body, Is.True, "body " + body.name + " is the group's"); }
            Assert.That(colliders, Is.EqualTo(1), "one collider in the scene besides the floor");
            Assert.That(bodies, Is.EqualTo(1), "one body in the scene");
            TestContext.Out.WriteLine("physics after the aggregation: groups " + h.GroupCount + ", bodies " + bodies + ", hulls " + whole.HullCount + ", colliders " + colliders + " (most at once: groups " + h.MaxGroups + ", hulls " + h.MaxLiveHulls + "); members " + whole.MemberCount);
            foreach (LogicalFragmentId f in whole.Fragments)
            {
                Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsDisplayOnly, Is.True, "member " + f.value + " is display-only");
                Assert.That(o.Root.GetComponentsInChildren<Collider>(true).Length, Is.Zero, "member " + f.value + " has no collider");
            }

            Assert.That(whole.MemberCount, Is.GreaterThanOrEqualTo(11), "the members grew with the cuts: " + whole.MemberCount);
            Assert.That(whole.Mass, Is.EqualTo(10000.0).Within(1.0), "the mass is kept");
            Assert.That(whole.Anchored && whole.Kinematic, Is.True, "fixed by its anchor at the end");
            Assert.That(root.Rest.TrackedAnchored == 1 && root.Rest.TrackedDynamic == 0, Is.True, "the rest follows the one anchored group");
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero);
            Assert.That(h.IsSettled && h.DisplayOperationsOpen == 0, Is.True, h.DescribeUnsettled());
            AssertDisplayConsistent(root, "after ten cuts");
            TestContext.Out.WriteLine("shape: fusion envelope expansion max " + h.MaxFusionExpansion.ToString("E2") + " m (face approximation), gap filled " + h.OverhangVolume.ToString("F3") + " m^3 (max " + h.MaxOverhangVolume.ToString("F3") + "), penetration before exchange max " + h.MaxPenetrationBeforeExchange.ToString("F4") + " m, after max " + h.MaxFusionPenetration.ToString("F4") + " m, added max " + h.MaxNewPenetration.ToString("F4") + " m");
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero);
        }
    }
}
