using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A synthetic building group of 1331 members and more (TL, 2026-09-30: the coexistence run's publications of a
    /// thousand members and more took up to 130 ms on Main). A box with its anchors at the centres of its bottom layer
    /// of cells is cut into an 11 x 11 x 11 lattice by group cuts (the layers first, then the x strips, then the z
    /// cells), and two large cuts of the whole group are then measured: a horizontal cut through a middle layer (its
    /// upper side is dynamic: the masses) and a vertical diagonal cut (both sides anchored: the most crossed members).
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const int LatticeCells = 11;

        /// <summary>A building box whose anchors are the centres of its bottom layer of cells (n x n).</summary>
        private LogicalFragmentId AddLatticeBuilding(CutWorldRoot root, int n)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Lattice Building"));
            actor.transform.position = Vector3.zero;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            float h = 2f / n;
            var anchors = new float3[n * n];
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < n; k++)
                {
                    anchors[i * n + k] = new float3(-1f + (i + 0.5f) * h, -1f + 0.5f * h, -1f + (k + 0.5f) * h);
                }
            }

            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, anchors, true, out LogicalFragmentId fragment);
            if (added) _registered.Add(actor);
            Assert.That(added, Is.True, "the lattice building was taken into the world");
            return fragment;
        }

        private static bool FusionQuiet(CutWorldRoot root)
        {
            BuildingFusion f = root.Fusion;
            return f.PreparationsInFlight == 0 && f.CutsInProgress == 0 && f.GroupCount == 1 && !f.Groups[0].Busy && f.Groups[0].Kinematic && f.HitsPending == 0;
        }

        /// <summary>The members whose colliders' bounds a world plane crosses (the lattice's cells are axis-aligned boxes: their bounds are their solids).</summary>
        private static List<LogicalFragmentId> MembersCrossedBy(CutWorldRoot root, float4 plane)
        {
            var crossed = new List<LogicalFragmentId>();
            foreach (LogicalFragmentId f in root.Fusion.Groups[0].Fragments)
            {
                Bounds b = ColliderBoundsOf(root, f);
                float3 c = b.center, e = b.extents;
                float centre = math.dot(plane.xyz, c) + plane.w;
                float reach = math.abs(plane.x) * e.x + math.abs(plane.y) * e.y + math.abs(plane.z) * e.z;
                if (centre - reach < -1e-4f && centre + reach > 1e-4f) crossed.Add(f);
            }

            return crossed;
        }

        /// <summary>One group cut of the quiet group by a world plane; waits until the group is one resting group again with nothing in progress. Returns the members it crossed.</summary>
        private static IEnumerator LatticeCut(CutWorldRoot root, float4 plane, long slash, List<LogicalFragmentId> crossedOut, float seconds = 90f, long[] exclusions = null, System.Action<List<LogicalFragmentId>> afterPublish = null)
        {
            yield return UntilWithin(() => FusionQuiet(root), seconds, "the group is quiet before slash " + slash);
            List<LogicalFragmentId> crossed = MembersCrossedBy(root, plane);
            Assert.That(crossed.Count, Is.GreaterThan(0), "slash " + slash + " crosses a member");
            int split = root.Fusion.MembersSplit, published = root.Fusion.FinalsPublished, refused = root.Fusion.GroupCutsRefused, cuts = root.Fusion.GroupCuts;
            long pairsBefore = root.Fusion.IgnoredPairs;
            root.Fusion.holdFinals = exclusions != null;   // the copies stand until the exclusions are read
            Assert.That(root.TryAsk(SlashAsk(root, crossed[0], plane, slash, 0f)), Is.True, "slash " + slash + " asked");
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts || root.Fusion.GroupCutsRefused > refused, seconds, "slash " + slash + " published");
            Assert.That(root.Fusion.GroupCutsRefused, Is.EqualTo(refused), "slash " + slash + " not refused");
            Assert.That(root.Fusion.MembersSplit - split, Is.EqualTo(crossed.Count), "slash " + slash + " crossed the members its plane crosses");
            if (exclusions == null) afterPublish?.Invoke(crossed);   // the frame after the publication: its Finals are still running
            if (exclusions != null)
            {
                long expected = AssertExclusions(root, crossed, "slash " + slash, out exclusions[1]);
                exclusions[0] = root.Fusion.IgnoredPairs - pairsBefore;
                Assert.That(exclusions[0], Is.EqualTo(expected), "slash " + slash + ": the pairs set are the needed ones");
                afterPublish?.Invoke(crossed);
                root.Fusion.holdFinals = false;
            }
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, seconds, "slash " + slash + ": the Finals");
            Assert.That(root.Fusion.FinalsPublished - published, Is.EqualTo(crossed.Count), "slash " + slash + ": every Final published");
            crossedOut?.AddRange(crossed);
        }

        private static Bounds GrownBounds(Collider c)
        {
            Bounds b = c.bounds;
            b.Expand(2f * Mathf.Max(0f, c.contactOffset) + 1e-4f);
            return b;
        }

        /// <summary>
        /// The exclusions of a published group cut, read from the physics scene at its publication: every crossed member's
        /// copy ignores every crossed member's colliders (its own and the others', near or apart -- the sides move before
        /// their Finals). Returns the pairs read; <paramref name="apartPairs"/> how many of them stand apart now (their
        /// grown bounds do not meet), for the record.
        /// </summary>
        private static long AssertExclusions(CutWorldRoot root, List<LogicalFragmentId> crossed, string what, out long apartPairs)
        {
            var own = new Dictionary<LogicalFragmentId, List<MeshCollider>>();
            var copies = new Dictionary<LogicalFragmentId, MeshCollider[]>();
            foreach (LogicalFragmentId f in crossed)
            {
                Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsFused, Is.True, what + ": crossed member " + f.value + " is fused");
                own[f] = o.Group.byFragment[f].colliders;
                GameObject shadow = GameObject.Find("Shadow of " + f.value);
                Assert.That(shadow, Is.Not.Null, what + ": member " + f.value + " has its copy");
                copies[f] = shadow.GetComponentsInChildren<MeshCollider>();
                Assert.That(copies[f].Length, Is.EqualTo(own[f].Count), what + ": a copy of every collider");
            }

            long needed = 0;
            apartPairs = 0;
            foreach (LogicalFragmentId a in crossed)
            {
                foreach (MeshCollider copy in copies[a])
                {
                    Bounds c = GrownBounds(copy);
                    foreach (LogicalFragmentId b in crossed)
                    {
                        foreach (MeshCollider o in own[b])
                        {
                            if (!a.Equals(b) && !GrownBounds(o).Intersects(c)) apartPairs++;
                            needed++;
                            bool apart = Physics.GetIgnoreCollision(copy, o) || Physics.GetIgnoreLayerCollision(copy.gameObject.layer, o.gameObject.layer);
                            Assert.That(apart, Is.True, what + ": copy of " + a.value + " and member " + b.value + " must not collide (by a pair or by their layers)");
                        }
                    }
                }
            }

            return needed;
        }

        /// <summary>
        /// The dynamic side of a published cut carries the mass, centre and inertia its members give now, read from their
        /// transforms (a crossed member by the side's share of the box rule): what the fusion summed from its snapshot.
        /// </summary>
        private static void AssertDynamicSideMass(CutWorldRoot root, float4 planeWorld, List<LogicalFragmentId> crossed, string what)
        {
            FusedGroup side = null;
            foreach (FusedGroup g in root.Fusion.Groups) if (!g.Kinematic && g.Body != null && !g.Body.isKinematic) side = g;
            Assert.That(side, Is.Not.Null, what + ": a dynamic side");
            var crossedSet = new HashSet<LogicalFragmentId>(crossed);
            double total = 0.0;
            double3 weighted = 0.0;
            var parts = new List<(double mass, double3 centre, double3x3 inertia)>();
            foreach (LogicalFragmentId f in side.Fragments)
            {
                FusedGroup.Member m = side.byFragment[f];
                Transform at = m.owner.Root.transform;
                double mass; float3 centre, inertia; quaternion axes;
                if (crossedSet.Contains(f))
                {
                    BoxRule(root, f, InShapeFrame(root, f, planeWorld), out ProvisionalBoxMass.Side positive, out _);
                    mass = positive.mass; centre = positive.centerOfMass; inertia = positive.inertia; axes = positive.inertiaRotation;
                }
                else
                {
                    mass = m.mass; centre = m.centreLocal; inertia = m.inertia; axes = m.inertiaRotation;
                }

                double3 c = (float3)at.TransformPoint(centre);
                var r = new double3x3(new float3x3(math.mul((quaternion)at.rotation, axes)));
                parts.Add((mass, c, math.mul(math.mul(r, new double3x3(inertia.x, 0, 0, 0, inertia.y, 0, 0, 0, inertia.z)), math.transpose(r))));
                total += mass;
                weighted += mass * c;
            }

            double3 centreWorld = weighted / total;
            double3x3 expected = default;
            foreach ((double mass, double3 c, double3x3 inertia) in parts)
            {
                double3 d = c - centreWorld;
                expected += inertia + mass * (math.dot(d, d) * double3x3.identity - new double3x3(d.x * d, d.y * d, d.z * d));
            }

            Rigidbody body = side.Body;
            Assert.That(body.mass, Is.EqualTo((float)total).Within(1e-4f * (float)total), what + ": the side's mass");
            Assert.That(math.distance((double3)(float3)body.worldCenterOfMass, centreWorld), Is.LessThan(1e-4), what + ": the side's centre");
            var rb = new double3x3(new float3x3(math.mul((quaternion)body.rotation, (quaternion)body.inertiaTensorRotation)));
            Vector3 i = body.inertiaTensor;
            double3x3 actual = math.mul(math.mul(rb, new double3x3(i.x, 0, 0, 0, i.y, 0, 0, 0, i.z)), math.transpose(rb));
            double scale = expected.c0.x + expected.c1.y + expected.c2.z, worst = 0.0;
            double3x3 diff = actual - expected;
            for (int k = 0; k < 3; k++) worst = math.max(worst, math.cmax(math.abs(diff[k])));
            TestContext.Out.WriteLine(what + ": dynamic side " + side.MemberCount + " members, mass " + body.mass.ToString("F4") + " (expected " + total.ToString("F4") + "), centre off " + math.distance((double3)(float3)body.worldCenterOfMass, centreWorld).ToString("E2") + ", inertia off " + (worst / scale).ToString("E2") + " of its trace");
            Assert.That(worst / scale, Is.LessThan(1e-3), what + ": the side's inertia");
        }

        /// <summary>Every group's colliders belong to its body, its anchored flag is its members', and its number is its members' masses.</summary>
        private static void AssertGroupsWhole(CutWorldRoot root, string when)
        {
            foreach (FusedGroup g in root.Fusion.Groups)
            {
                bool anchored = false;
                double mass = 0.0;
                foreach (LogicalFragmentId f in g.Fragments)
                {
                    FusedGroup.Member m = g.byFragment[f];
                    anchored |= m.fixedByAnchors;
                    mass += m.mass;
                    foreach (MeshCollider c in m.colliders)
                    {
                        Assert.That(c != null && c.enabled && c.attachedRigidbody == g.Body, Is.True, when + ": member " + f.value + "'s collider belongs to its group's body");
                    }
                }

                Assert.That(g.Anchored, Is.EqualTo(anchored), when + ": group " + g.Root.name + " is anchored as its members are");
                if (!g.Busy) Assert.That(g.Mass, Is.EqualTo(mass).Within(1e-6 * math.max(1.0, mass)), when + ": group " + g.Root.name + "'s mass is its members'");
            }
        }

        /// <summary>A frame-by-frame look at the fusion around one group cut: its Main time a frame, the ask and the publication frames, and the physics steps after the publication.</summary>
        private sealed class FusionFrameSampler : MonoBehaviour
        {
            public BuildingFusion fusion;
            private int _hitsAt, _cutsAt;
            private double _lastMain, _askedAt = -1, _publishedAt = -1, _maxFrame, _maxFrameAfter;
            private long _lastStep = -1;
            private readonly List<double> _simulateAfter = new List<double>();

            public void Begin(int hits, int cuts)
            {
                _hitsAt = hits; _cutsAt = cuts;
                _lastMain = fusion.StepSeconds + fusion.RequestSeconds;
                _lastStep = CutPhysicsStep.Clock.StepId;
            }

            private void Update()
            {
                double main = fusion.StepSeconds + fusion.RequestSeconds, frame = main - _lastMain;
                _lastMain = main;
                double now = Time.realtimeSinceStartupAsDouble;
                if (_askedAt < 0 && fusion.Hits.Count > _hitsAt) _askedAt = now;
                if (_publishedAt < 0 && fusion.GroupCuts > _cutsAt) _publishedAt = now;
                if (_askedAt >= 0) _maxFrame = System.Math.Max(_maxFrame, frame);
                if (_publishedAt >= 0) _maxFrameAfter = System.Math.Max(_maxFrameAfter, frame);
                long step = CutPhysicsStep.Clock.StepId;
                if (_publishedAt >= 0 && step != _lastStep && _simulateAfter.Count < 5) _simulateAfter.Add(CutPhysicsStep.LastSimulateSeconds * 1000.0);
                _lastStep = step;
            }

            public string Describe() => "the fusion's Main a frame at most " + (_maxFrame * 1000).ToString("F3") + " ms from the ask (" + (_maxFrameAfter * 1000).ToString("F3") + " from the publication on); ask to publication "
                + (_publishedAt >= 0 && _askedAt >= 0 ? ((_publishedAt - _askedAt) * 1000).ToString("F1") + " ms" : "not seen") + "; the Simulates after the publication ms " + string.Join(" / ", _simulateAfter.ConvertAll(v => v.ToString("F3")));
        }

        private static string MassApplyParts(BuildingFusion f)
        {
            var line = new System.Text.StringBuilder();
            for (int i = 0; i < BuildingFusion.MassApplyPartNames.Length; i++) line.Append(i > 0 ? " " : "").Append(BuildingFusion.MassApplyPartNames[i]).Append(' ').Append((f.MassApplyPartSeconds[i] * 1000).ToString("F3"));
            return line.Append(" (ends ").Append(f.MassApplyEnds).Append(", groups ").Append(f.MassGroupsApplied).Append(", members ").Append(f.MassMembersApplied).Append(", member colliders ").Append(f.MassHullsApplied).Append(')').ToString();
        }

        private static string FusionCostLine(BuildingFusion f)
        {
            return "classify+admit " + (f.ClassifySeconds * 1000).ToString("F3") + " ms (max " + (f.MaxClassifySeconds * 1000).ToString("F3") + "); publish " + (f.PublishSeconds * 1000).ToString("F3") + " ms (max " + (f.MaxPublishSeconds * 1000).ToString("F3") + ", " + f.MaxCutMembers + " members): move " + (f.PublishMoveSeconds * 1000).ToString("F3")
                + ", shadows " + (f.PublishShadowSeconds * 1000).ToString("F3") + ", ignore " + (f.PublishIgnoreSeconds * 1000).ToString("F3")
                + " (" + f.IgnoredPairs + " pairs, most " + f.MaxIgnoredPairs + ", max " + (f.MaxIgnoreSeconds * 1000).ToString("F3") + "), rest+drop " + (f.PublishRestSeconds * 1000).ToString("F3") + ", submit+record " + (f.PublishSubmitSeconds * 1000).ToString("F3")
                + "; heaviest: " + f.MaxPublishBreakdown
                + "; mass " + (f.MassSeconds * 1000).ToString("F3") + " ms (max " + (f.MaxMassSeconds * 1000).ToString("F3") + "): samples " + (f.MassSampleSeconds * 1000).ToString("F3") + " (max " + (f.MaxMassSampleSeconds * 1000).ToString("F3")
                + "), applies " + (f.MassApplySeconds * 1000).ToString("F3") + " (max " + (f.MaxMassApplySeconds * 1000).ToString("F3") + "), waits " + (f.MassWaitSeconds * 1000).ToString("F3") + " (reschedules " + f.MassReschedules + ", " + (f.MassRescheduleWaitSeconds * 1000).ToString("F3")
                + "), apply parts ms " + MassApplyParts(f) + ", heaviest apply " + f.MaxMassApplyBreakdown
                + ", schedules " + f.MassSchedules + " (" + f.MassSamplesGathered + " samples, by retirement " + f.MassSchedulesByRetirement + "); final " + (f.FinalSeconds * 1000).ToString("F3") + " (max " + (f.MaxFinalSeconds * 1000).ToString("F3")
                + "); copies made ahead " + f.ShadowsPrepared + " (dropped " + f.ShadowsPreparedDropped + ", " + (f.ShadowPrepareSeconds * 1000).ToString("F3") + " ms, max " + (f.MaxShadowPrepareSeconds * 1000).ToString("F3") + "); fuse+merge " + (f.FuseSeconds * 1000).ToString("F3") + " (max " + (f.MaxFuseSeconds * 1000).ToString("F3") + "); step max " + (f.MaxStepSeconds * 1000).ToString("F3") + ", over budget " + f.OverrunFrames + " (" + (f.OverrunSeconds * 1000).ToString("F3") + " ms, max " + (f.MaxOverrunSeconds * 1000).ToString("F3") + ")";
        }

        /// <summary>
        /// **A group of 1331 members and more is cut whole, and its costs are measured by part.** The lattice is made by
        /// an ordinary cut and 29 group cuts; then a horizontal cut through a middle layer (the upper side, about 730 members, dynamic: its
        /// masses) and a vertical diagonal cut of about 1450 members (about 250 crossed, both sides anchored) are
        /// published and finished. Every cut crosses exactly the members its plane crosses, every Final is published,
        /// every copy ignores every crossed member's colliders (read from the physics scene at the publication), and afterwards every group's colliders
        /// belong to its body, its anchored flag and its mass are its members'. The costs are written, not judged.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionLarge_ALatticeGroupOfOverAThousandMembers_IsCutWhole_AndItsCostsAreMeasured([Values(2, 0)] int keepMode)
        {
            CutWorldRoot root = NewFusionWorld(perFrame: 4096);
            root.Fusion.keepSideForTest = keepMode;
            TestContext.Out.WriteLine("sides: " + (keepMode == 2 ? "both new (as before)" : "the old Root and Body kept by the side that moves fewer colliders") + "; exclusion by a layer pair");
            int n = LatticeCells;
            float h = 2f / n;
            LogicalFragmentId building = AddLatticeBuilding(root, n);
            yield return null;
            float began = Time.realtimeSinceStartup;
            // The first layer by the ordinary cut (an uncut building is not fused); its two sides rest and fuse.
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, -(-1f + h)), halves);
            yield return UntilWithin(() => FusionQuiet(root) && root.Fusion.Groups[0].MemberCount == 2, 30f, "the first two layers fused");
            long slash = 100;
            for (int j = 2; j < n; j++) yield return LatticeCut(root, new float4(0f, 1f, 0f, -(-1f + j * h)), slash++, null);   // the layers (their upper sides fall to rest)
            for (int i = 1; i < n; i++) yield return LatticeCut(root, new float4(1f, 0f, 0f, -(-1f + i * h)), slash++, null);   // the x strips: n crossed each
            for (int k = 1; k < n; k++) yield return LatticeCut(root, new float4(0f, 0f, 1f, -(-1f + k * h)), slash++, null);   // the z cells: n * n crossed each
            yield return UntilWithin(() => FusionQuiet(root), 90f, "the lattice is one quiet group");
            Assert.That(root.Fusion.Groups[0].MemberCount, Is.EqualTo(n * n * n), "the lattice's members");
            AssertGroupsWhole(root, "the lattice");
            TestContext.Out.WriteLine("lattice of " + root.Fusion.Groups[0].MemberCount + " members made in " + (Time.realtimeSinceStartup - began).ToString("F1") + " s; " + FusionCostLine(root.Fusion));

            // A: a horizontal cut through the middle of layer n / 2: the upper side (dynamic) carries the masses.
            BuildingFusion f = root.Fusion;
            double totalMass = f.Groups[0].Mass;
            double publishBefore = f.PublishSeconds, massBefore = f.MassSeconds, sampleBefore = f.MassSampleSeconds, applyBefore = f.MassApplySeconds, finalBefore = f.FinalSeconds;
            int schedulesBefore = f.MassSchedules, byRetirementBefore = f.MassSchedulesByRetirement;
            var crossedA = new List<LogicalFragmentId>();
            float4 planeA = new float4(0f, 1f, 0f, -(-1f + (n / 2 + 0.5f) * h));
            var exclusionsA = new long[2];
            yield return LatticeCut(root, planeA, slash++, crossedA, 90f, exclusionsA, crossed => AssertDynamicSideMass(root, planeA, crossed, "A at its publication"));
            Assert.That(crossedA.Count, Is.EqualTo(n * n), "A crosses one layer");
            double sum = 0.0;
            foreach (FusedGroup g in f.Groups)
            {
                sum += g.Mass;
                if (!g.Kinematic && g.Body != null) Assert.That(g.Body.mass, Is.EqualTo((float)g.Mass).Within(1e-3f * (float)g.Mass), "A: the dynamic side's body carries its mass");
            }

            Assert.That(sum, Is.EqualTo(totalMass).Within(1e-6 * totalMass), "A: the sides' masses sum to the group's");
            AssertGroupsWhole(root, "after A's Finals");
            TestContext.Out.WriteLine("A (" + crossedA.Count + " crossed, pairs ignored " + exclusionsA[0] + ", apart " + exclusionsA[1] + "): publish " + ((f.PublishSeconds - publishBefore) * 1000).ToString("F3") + " ms, mass " + ((f.MassSeconds - massBefore) * 1000).ToString("F3") + " (samples " + ((f.MassSampleSeconds - sampleBefore) * 1000).ToString("F3")
                + ", applies " + ((f.MassApplySeconds - applyBefore) * 1000).ToString("F3") + ", schedules " + (f.MassSchedules - schedulesBefore) + ", by retirement " + (f.MassSchedulesByRetirement - byRetirementBefore) + "), finals " + ((f.FinalSeconds - finalBefore) * 1000).ToString("F3") + " ms; " + FusionCostLine(f));

            // B: a vertical diagonal cut of the whole group, both sides anchored: the most crossed members.
            yield return UntilWithin(() => FusionQuiet(root), 90f, "the group is one again after A");
            int membersB = f.Groups[0].MemberCount;
            publishBefore = f.PublishSeconds; massBefore = f.MassSeconds; finalBefore = f.FinalSeconds;
            var crossedB = new List<LogicalFragmentId>();
            const float s = 0.70710678f;
            var exclusionsB = new long[2];
            yield return MeasuredLatticeCut(root, new float4(s, 0f, s, -0.05f * s), slash++, "B (even, diagonal)", keepMode, crossedB, exclusionsB, 0);
            AssertGroupsWhole(root, "after B's Finals");
            TestContext.Out.WriteLine("B (" + membersB + " members, " + crossedB.Count + " crossed, pairs ignored " + exclusionsB[0] + ", apart " + exclusionsB[1] + "): publish " + ((f.PublishSeconds - publishBefore) * 1000).ToString("F3") + " ms, mass " + ((f.MassSeconds - massBefore) * 1000).ToString("F3")
                + ", finals " + ((f.FinalSeconds - finalBefore) * 1000).ToString("F3") + " ms; " + FusionCostLine(f));
            Assert.That(f.FinalsFailed, Is.Zero, "no Final failed");

            // C: the whole group's mass applied again and again, its body kinematic (as it rests) and then dynamic: the apply's parts by the group's size.
            yield return UntilWithin(() => FusionQuiet(root), 90f, "the group is one again after B");
            FusedGroup whole = f.Groups[0];
            foreach (bool dynamic in new[] { false, true })
            {
                if (dynamic) whole.Body.isKinematic = false;
                double[] before = (double[])f.MassApplyPartSeconds.Clone();
                double applyAtStart = f.MassApplySeconds;
                int endsBefore = f.MassApplyEnds;
                const int applies = 5;
                for (int k = 0; k < applies; k++)
                {
                    f.ScheduleMassForTest(whole);
                    f.CompleteMassNow();
                }

                var parts = new System.Text.StringBuilder();
                for (int i = 0; i < before.Length; i++) parts.Append(' ').Append(BuildingFusion.MassApplyPartNames[i]).Append(' ').Append(((f.MassApplyPartSeconds[i] - before[i]) * 1000 / applies).ToString("F3"));
                TestContext.Out.WriteLine("C (" + (dynamic ? "dynamic" : "kinematic") + " body): " + whole.MemberCount + " members, " + whole.memberOfCollider.Count + " member colliders; " + (f.MassApplyEnds - endsBefore) + " applies, ms per apply "
                    + ((f.MassApplySeconds - applyAtStart) * 1000 / applies).ToString("F3") + ", parts per apply:" + parts);
                if (dynamic) whole.Body.isKinematic = true;
            }

            // D, E, F: a cut near the bottom (the positive side far the larger), near the top (the negative side the larger),
            // and a diagonal where the members and the colliders to move disagree (the negative side has more members than the
            // positive's uncut ones, but fewer than those and the copies): each side kept as the colliders decide.
            yield return MeasuredLatticeCut(root, new float4(0f, 1f, 0f, -(-1f + 1.5f * h)), slash++, "D (bottom: positive larger)", keepMode, null, null, 1);
            yield return MeasuredLatticeCut(root, new float4(0f, 1f, 0f, -(-1f + 9.5f * h)), slash++, "E (top: negative larger)", keepMode, null, null, -1);
            yield return UntilWithin(() => FusionQuiet(root), 90f, "quiet before F");
            float4 inverted = default;
            bool found = false;
            for (float w = -0.6f; w <= 0.6f && !found; w += 0.013f)
            {
                var plane = new float4(s, 0f, -s, -w * s);
                int crossedCount = MembersCrossedBy(root, plane).Count, up = 0, down = 0;
                foreach (LogicalFragmentId m in root.Fusion.Groups[0].Fragments)
                {
                    Bounds b = ColliderBoundsOf(root, m);
                    float d = math.dot(plane.xyz, (float3)b.center) + plane.w;
                    float reach = math.abs(plane.x) * b.extents.x + math.abs(plane.y) * b.extents.y + math.abs(plane.z) * b.extents.z;
                    if (d - reach >= -1e-4f) up++; else if (d + reach <= 1e-4f) down++;
                }

                if (crossedCount > 0 && up < down && down <= up + crossedCount)
                {
                    inverted = plane;
                    found = true;
                    TestContext.Out.WriteLine("F plane offset " + w.ToString("F3") + ": positive uncut " + up + " + crossed " + crossedCount + " (members " + (up + crossedCount) + "), negative " + down + " (+ copies " + crossedCount + ")");
                }
            }

            Assert.That(found, Is.True, "a diagonal where the members and the colliders disagree");
            yield return MeasuredLatticeCut(root, inverted, slash++, "F (members say positive, colliders say negative)", keepMode, null, null, -1);
            Assert.That(root.Fusion.ExclusionFallbacks, Is.Zero, "no exclusion fell back to pairs (not on the base layer " + root.Fusion.ExclusionFallbacksNotBaseLayer + ", no pair " + root.Fusion.ExclusionFallbacksNoPairs + ")");
            yield return EndWorld(root);
        }

        /// <summary>
        /// One measured group cut of the quiet lattice group: the side kept (when the mode chooses: the expected one), the
        /// bodies made, the members, colliders and copies moved, the publication by part, the fusion's Main a frame at most,
        /// the ask to the publication, the Simulates after it, the Finals and the cleanup; and afterwards the kept body is
        /// still its side's, with its old identity.
        /// </summary>
        private static IEnumerator MeasuredLatticeCut(CutWorldRoot root, float4 plane, long slash, string label, int keepMode, List<LogicalFragmentId> crossedOut, long[] exclusions, int expectedKeep)
        {
            BuildingFusion f = root.Fusion;
            yield return UntilWithin(() => FusionQuiet(root), 90f, label + ": the group is quiet");
            FusedGroup old = f.Groups[0];
            Rigidbody oldBody = old.Body;
            int oldId = oldBody.GetInstanceID(), members = old.MemberCount;
            var sampler = new GameObject("Fusion frame sampler").AddComponent<FusionFrameSampler>();
            sampler.fusion = f;
            double classifyAt = f.ClassifySeconds, prepareAt = f.ShadowPrepareSeconds, releaseAt = f.ExclusionReleaseSeconds, massAt = f.MassSeconds, finalAt = f.FinalSeconds, publishAt = f.PublishSeconds, fuseAt = f.FuseSeconds;
            double moveAt = f.PublishMoveSeconds, shadowAt = f.PublishShadowSeconds, ignoreAt = f.PublishIgnoreSeconds, restAt = f.PublishRestSeconds, submitAt = f.PublishSubmitSeconds;
            long callsAt = f.ExclusionCalls, movedAt = f.MembersMovedAtPublication, collidersAt = f.CollidersMovedAtPublication, copiesAt = f.CopiesMovedAtPublication;
            int bodiesAt = f.BodiesMade, keptPositiveAt = f.SidesKeptPositive, keptNegativeAt = f.SidesKeptNegative, fallbacksAt = f.ExclusionFallbacks;
            sampler.Begin(f.Hits.Count, f.GroupCuts);
            var crossed = crossedOut ?? new List<LogicalFragmentId>();
            // The old body's life (TL, 2026-10-01): the fusion's own trace of publications, merge steps and drops, and at the
            // publication (in its window, when there is one), after the Finals, and before the judgement, who holds the old body.
            f.bodyTraceForTest = new List<string>();
            string HeldBy()
            {
                if (oldBody == null) return "the old body " + oldId + " is destroyed";
                foreach (FusedGroup g in f.Groups)
                {
                    if (g.Body != oldBody) continue;
                    int colliders = 0, onIt = 0;
                    foreach (Collider c in g.Root.GetComponentsInChildren<Collider>(true)) { if (!c.enabled) continue; colliders++; if (c.attachedRigidbody == oldBody) onIt++; }
                    return "the old body " + oldId + " is " + BuildingFusion.DescribeGroupForTest(g) + "'s, enabled colliders under it " + colliders + " of which on the old body " + onIt;
                }

                return "the old body " + oldId + " lives but no group holds it";
            }

            // At the publication: the group holding the old body, and its colliders on it.
            FusedGroup keptGroup = null;
            int keptColliders = 0, keptOnOld = 0;
            bool oldAliveAtPublication = false;
            System.Action<List<LogicalFragmentId>> atPublication = _ =>
            {
                oldAliveAtPublication = oldBody != null && oldBody.GetInstanceID() == oldId;
                foreach (FusedGroup g in f.Groups) if (oldBody != null && g.Body == oldBody) keptGroup = g;
                if (keptGroup != null) foreach (Collider c in keptGroup.Root.GetComponentsInChildren<Collider>(true)) { if (!c.enabled) continue; keptColliders++; if (c.attachedRigidbody == oldBody) keptOnOld++; }
                f.bodyTraceForTest.Add("frame " + Time.frameCount + ": at the publication" + (exclusions != null ? " (Finals held)" : " (the frame after it)") + ": " + HeldBy() + "; kept " + (f.SidesKeptPositive > keptPositiveAt ? "positive" : f.SidesKeptNegative > keptNegativeAt ? "negative" : "neither"));
            };
            yield return LatticeCut(root, plane, slash, crossed, 90f, exclusions, atPublication);
            var keptMembersAfterFinals = new List<LogicalFragmentId>();
            if (keptGroup != null && keptGroup.Root != null) keptMembersAfterFinals.AddRange(keptGroup.Fragments);
            f.bodyTraceForTest.Add("frame " + Time.frameCount + ": every Final completed: " + HeldBy());
            yield return Steps(5);
            f.bodyTraceForTest.Add("frame " + Time.frameCount + ": five steps later, at the judgement: " + HeldBy());
            List<string> trace = f.bodyTraceForTest;
            f.bodyTraceForTest = null;
            foreach (string line in trace) TestContext.Out.WriteLine(label + " body trace: " + line);
            sampler.enabled = false;
            string kept = f.SidesKeptPositive > keptPositiveAt ? "positive" : f.SidesKeptNegative > keptNegativeAt ? "negative" : "neither";
            TestContext.Out.WriteLine(label + ": " + members + " members, crossed " + crossed.Count + "; kept " + kept + ", bodies made " + (f.BodiesMade - bodiesAt) + "; moved members " + (f.MembersMovedAtPublication - movedAt)
                + ", colliders " + (f.CollidersMovedAtPublication - collidersAt) + " (copies " + (f.CopiesMovedAtPublication - copiesAt) + "); exclusion API calls " + (f.ExclusionCalls - callsAt)
                + "; preparation Main ms: judgement and admissions " + ((f.ClassifySeconds - classifyAt) * 1000).ToString("F3") + ", copies made ahead " + ((f.ShadowPrepareSeconds - prepareAt) * 1000).ToString("F3")
                + "; publication " + ((f.PublishSeconds - publishAt) * 1000).ToString("F3") + " (move " + ((f.PublishMoveSeconds - moveAt) * 1000).ToString("F3") + ", copies " + ((f.PublishShadowSeconds - shadowAt) * 1000).ToString("F3")
                + ", exclusion " + ((f.PublishIgnoreSeconds - ignoreAt) * 1000).ToString("F3") + ", rest " + ((f.PublishRestSeconds - restAt) * 1000).ToString("F3") + ", submit+record " + ((f.PublishSubmitSeconds - submitAt) * 1000).ToString("F3")
                + "); cleanup: Finals " + ((f.FinalSeconds - finalAt) * 1000).ToString("F3") + ", exclusion given back " + ((f.ExclusionReleaseSeconds - releaseAt) * 1000).ToString("F3") + ", masses " + ((f.MassSeconds - massAt) * 1000).ToString("F3")
                + ", fuse and merge after " + ((f.FuseSeconds - fuseAt) * 1000).ToString("F3") + "; " + sampler.Describe());
            Object.Destroy(sampler.gameObject);
            Assert.That(f.ExclusionFallbacks, Is.EqualTo(fallbacksAt), label + ": no fallback to pairs");
            if (keepMode == 2)
            {
                Assert.That(f.BodiesMade - bodiesAt, Is.EqualTo(2), label + ": both sides new");
            }
            else
            {
                Assert.That(f.BodiesMade - bodiesAt, Is.EqualTo(1), label + ": one new body");
                // (1) At the publication the kept side is the old body, by its identity, every collider of the side on it.
                Assert.That(oldAliveAtPublication && keptGroup != null, Is.True, label + ": at the publication the old body is the kept side's body");
                Assert.That(keptColliders > 0 && keptOnOld == keptColliders, Is.True, label + ": at the publication every collider of the kept side is on the old body (" + keptOnOld + " of " + keptColliders + ")");
                // (2) After the Finals: the old body is still a side's, or its side was merged into the building's resting group in the
                // ordinary way (both sides anchored: the resting group is the other side) and let go: its group dropped for that merge, the
                // body destroyed, and its members in a live group (their colliders on its body and their masses its: AssertGroupsWhole below).
                bool owned = false;
                foreach (FusedGroup g in f.Groups) owned |= oldBody != null && g.Body == oldBody;
                if (!owned)
                {
                    string id = "group#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(keptGroup) + " ";
                    Assert.That(trace.Exists(l => l.Contains("merge complete: " + id)), Is.True, label + ": the kept side's group was merged whole into the building's resting group");
                    Assert.That(trace.Exists(l => l.Contains("drop (merged into the building's resting group): " + id)), Is.True, label + ": and dropped for that merge (no other drop)");
                    Assert.That(trace.Exists(l => l.Contains("drop (") && l.Contains(id) && !l.Contains("drop (merged into the building's resting group)")), Is.False, label + ": dropped for no other reason");
                    Assert.That(oldBody == null && keptGroup.Root == null && keptGroup.Body == null && !System.Linq.Enumerable.Contains(f.Groups, keptGroup), Is.True, label + ": its group and body let go");
                    foreach (LogicalFragmentId m in keptMembersAfterFinals)
                    {
                        if (!root.Ledger.IsCurrentTarget(m)) continue;
                        Assert.That(root.Owners.TryGet(m, out PhysicsFragmentOwner o) && o.IsFused && System.Linq.Enumerable.Contains(f.Groups, o.Group) && o.Group.Body != null, Is.True, label + ": member " + m.value + " of the merged side is in a live group");
                    }
                }
                if (expectedKeep != 0) Assert.That(kept, Is.EqualTo(expectedKeep > 0 ? "positive" : "negative"), label + ": the side kept moves the fewer colliders");
            }

            AssertGroupsWhole(root, label + " after its Finals");
        }

        /// <summary>
        /// **The check's anchor judgement reads the body that carries the owner.** An uncut anchored building is judged on its
        /// own body (the fixture's is dynamic: not fixed); after its cut the anchored half is fused and has no body of its own (by design) and is judged on its
        /// group's body -- fixed, the body kinematic, every collider attached to it; the unanchored half is not fixed; and
        /// a collider of the anchored half moved onto another body makes the judgement fail until it is back.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionAnchorJudgement_AFusedAnchoredMember_IsJudgedOnItsGroupsBody()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            Assert.That(root.Owners.TryGet(building, out PhysicsFragmentOwner whole), Is.True);
            // The fixture's uncut building keeps the dynamic body it was made with: anchored, but its body is not held, so it is not fixed.
            Assert.That(whole.IsFused, Is.False);
            Assert.That(SandboxPropSlashPlayerCheck.FixedByItsAnchors(whole, out string how), Is.EqualTo(whole.Body.isKinematic), "the uncut building is judged on its own body: " + how);
            StringAssert.StartsWith("own body", how);
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = halves[0].Equals(upper) ? halves[1] : halves[0];
            yield return UntilFused(root, 2, "both halves fused");
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.IsFused && lowerOwner.FixedByAnchors, Is.True, "the lower half is a fused anchored member");
            Assert.That(lowerOwner.Body, Is.Null, "with no body of its own (what the old judgement required)");
            Assert.That(SandboxPropSlashPlayerCheck.FixedByItsAnchors(lowerOwner, out how), Is.True, "judged on its group's body: " + how);
            StringAssert.StartsWith("fused", how);
            Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner) && upperOwner.IsFused, Is.True);
            Assert.That(SandboxPropSlashPlayerCheck.FixedByItsAnchors(upperOwner, out how), Is.False, "the unanchored half is not fixed: " + how);

            // A collider of the anchored half put on another body (a Rigidbody of its own on its frame): the judgement fails.
            MeshCollider moved = lowerOwner.Root.GetComponentInChildren<MeshCollider>();
            var other = moved.gameObject.AddComponent<Rigidbody>();
            other.isKinematic = true;
            Assert.That(moved.attachedRigidbody, Is.EqualTo(other));
            Assert.That(SandboxPropSlashPlayerCheck.FixedByItsAnchors(lowerOwner, out how), Is.False, "a collider on another body fails the judgement: " + how);
            UnityEngine.Object.DestroyImmediate(other);
            Assert.That(SandboxPropSlashPlayerCheck.FixedByItsAnchors(lowerOwner, out how), Is.True, "and passes again once it is back on the group's body: " + how);
            yield return EndWorld(root);
        }
        /// <summary>
        /// **The crowd's plan-cycle windows tell a missing measurement from a stop.** Cycles every second through a long
        /// window: the widest measured gap is a second. The same window open at the run's end with a cycle half a second
        /// before the end: its end is missing, but open only half a second (not a stop). Cycles that stop five seconds
        /// before the end of an open window: the open end is five seconds (a stop, at least). A four-second gap between
        /// cycles inside a closed window is measured as four.
        /// </summary>
        [Test]
        public void CrowdWindows_TellAMissingEndFromAStop()
        {
            var cycles = new List<double>();
            for (int i = 0; i <= 100; i++) cycles.Add(i);
            SandboxPropSlashPlayerCheck.CycleWindows closed = SandboxPropSlashPlayerCheck.AnalyseCycleWindows(new List<(double, double)> { (10.5, 60.5) }, cycles, false);
            Assert.That(closed.widest, Is.EqualTo(1.0).Within(1e-9));
            Assert.That(closed.missingStarts + closed.missingEnds, Is.Zero);
            Assert.That(closed.widestOpenEnd, Is.Zero);

            SandboxPropSlashPlayerCheck.CycleWindows open = SandboxPropSlashPlayerCheck.AnalyseCycleWindows(new List<(double, double)> { (10.5, 100.5) }, cycles, true);
            Assert.That(open.missingEnds, Is.EqualTo(1), "no cycle after a window open at the end: a missing end");
            Assert.That(open.widest, Is.EqualTo(1.0).Within(1e-9), "the cycles inside were measured");
            Assert.That(open.widestOpenEnd, Is.EqualTo(0.5).Within(1e-9), "open half a second: not a stop");
            StringAssert.Contains("open at the run's end", open.lines[0]);

            var stopped = new List<double>();
            for (int i = 0; i <= 95; i++) stopped.Add(i);
            SandboxPropSlashPlayerCheck.CycleWindows stop = SandboxPropSlashPlayerCheck.AnalyseCycleWindows(new List<(double, double)> { (10.5, 100.0) }, stopped, true);
            Assert.That(stop.widestOpenEnd, Is.EqualTo(5.0).Within(1e-9), "cycles stopped five seconds before the end: a stop of five seconds at least");

            var gap = new List<double> { 0, 1, 2, 6, 7, 8 };
            SandboxPropSlashPlayerCheck.CycleWindows inner = SandboxPropSlashPlayerCheck.AnalyseCycleWindows(new List<(double, double)> { (1.5, 7.5) }, gap, false);
            Assert.That(inner.widest, Is.EqualTo(4.0).Within(1e-9), "a four-second gap inside the window");
        }
    }
}
