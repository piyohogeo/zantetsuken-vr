using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The corrections of a fused group's cut publication (2026-09-29): a cut refused as a whole for any crossed member's
    /// refusal (the ledger's room, or one member's own), the interval's mass, centre and inertia by the Provisional box
    /// rule (an oblique asymmetric cut; a rotating group whose sides inherit v + w x r), the interval read while the
    /// Finals are held (the sides apart: display, colliders and mass properties correspond; a held piece waits for a
    /// busy group; the shadow's colliders stop in the Final's or the failure's own call), and the check's ending with
    /// fused members through the check's own ending code.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutOperationId AdmittedOne(CutWorldRoot root, LogicalFragmentId source)
        {
            List<CutOperationId> admitted = AdmittedFor(root, new[] { source });
            Assert.That(admitted.Count, Is.EqualTo(1), "the crossed member's cut was admitted");
            return admitted[0];
        }

        private static int OperationCount(CutWorldRoot root)
        {
            int n = 0;
            for (int i = 0; root.Ledger.TryGetOperationAtAdmission(i, out _); i++) n++;
            return n;
        }

        /// <summary>A world plane carried into a member's own shape frame (the frame the ask's plane is in).</summary>
        private static float4 InShapeFrame(CutWorldRoot root, LogicalFragmentId member, float4 planeWorld)
        {
            Assert.That(root.Owners.TryGet(member, out PhysicsFragmentOwner owner), Is.True);
            float4x4 shapeToWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
            return BuildingFusion.TransformPlane(math.inverse(shapeToWorld), planeWorld);
        }

        /// <summary>The Provisional box rule's answer for a member and a plane in its shape frame, as the fusion computes it.</summary>
        private static void BoxRule(CutWorldRoot root, LogicalFragmentId member, float4 planeLocal, out ProvisionalBoxMass.Side positive, out ProvisionalBoxMass.Side negative)
        {
            Assert.That(root.Owners.TryGet(member, out PhysicsFragmentOwner owner) && owner.IsFused, Is.True);
            FusedGroup.Member m = owner.Group.byFragment[member];
            Assert.That(ProvisionalBoxMass.TryDivide(owner.Shape, planeLocal, m.mass, m.inertia, m.inertiaRotation, out positive, out negative, out _), Is.True, "the box rule divides member " + member.value);
        }

        private static List<MeshCollider> ShadowCollidersOf(LogicalFragmentId member)
        {
            GameObject shadow = GameObject.Find("Shadow of " + member.value);
            Assert.That(shadow, Is.Not.Null, "the negative side carries the shadow of " + member.value);
            return new List<MeshCollider>(shadow.GetComponentsInChildren<MeshCollider>(true));
        }

        private static Vector3 Sorted(Vector3 v)
        {
            var a = new[] { v.x, v.y, v.z };
            Array.Sort(a);
            return new Vector3(a[0], a[1], a[2]);
        }

        private GameObject AddKinematicPlatform()
        {
            GameObject platform = Track(new GameObject("Kinematic Platform"));
            var pbox = platform.AddComponent<BoxCollider>();
            pbox.center = new Vector3(0f, -1.5f, 0f);
            pbox.size = new Vector3(6f, 1f, 6f);
            var mover = platform.AddComponent<Rigidbody>();
            mover.isKinematic = true;
            return platform;
        }

        /// <summary>
        /// **One crossed member's own refusal refuses the whole cut, with the group and the ledger unchanged.** Three fused
        /// pieces in a column; the middle one (in the group's member order) is put under a cut of its own directly in the
        /// ledger; a vertical plane crossing all three is asked: the first member passes the judgement, the second is
        /// refused, and nothing is admitted for any of them -- no operation, no side, no shadow, the group as it was.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_AMemberRefusedMidWay_RefusesTheWholeCut_AndChangesNothing()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var quarters = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), quarters);
            yield return UntilFused(root, 3, "three fused in a column");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));
            FusedGroup group = root.Fusion.Groups[0];
            var members = new List<LogicalFragmentId>(group.Fragments);
            Assert.That(members.Count, Is.EqualTo(3));
            GameObject groupRoot = group.Root;
            Rigidbody groupBody = group.Body;
            Vector3 groupAt = groupRoot.transform.position;

            // The middle member is under a cut of its own (the ledger's own refusal: SourceActive), the others are free.
            Assert.That(root.Ledger.Admit(members[1], new float4(0f, 1f, 0f, 0f), true, out CutOperationId held), Is.EqualTo(LogicalCutAdmission.Admitted));
            int operationsBefore = OperationCount(root);
            int incompleteBefore = root.Ledger.Budget.IncompleteCutOperationCount;
            int refusedBefore = root.Fusion.GroupCutsRefused, membersRefusedBefore = root.Fusion.MembersRefused;

            ProvisionalCutAsk ask = Ask(members[0], InShapeFrame(root, members[0], new float4(1f, 0f, 0f, 0f)));   // the world plane x = 0 crosses the column
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            yield return null;
            WriteFusionRecord(root, "after the refused cut");
            Assert.That(root.Fusion.GroupCutsRefused, Is.EqualTo(refusedBefore + 1), "refused as a whole");
            Assert.That(root.Fusion.MembersRefused, Is.EqualTo(membersRefusedBefore + 1), "for one member's own refusal");
            Assert.That(root.Fusion.GroupCuts, Is.Zero, "no group cut");
            string last = root.Fusion.Events[root.Fusion.Events.Count - 1];
            TestContext.Out.WriteLine(last);
            Assert.That(last, Does.Contain("member " + members[1].value + " is under a cut already"), "the refusal names the middle member");
            Assert.That(OperationCount(root), Is.EqualTo(operationsBefore), "no operation admitted for any member");
            Assert.That(root.Ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incompleteBefore), "the budget as it was");
            Assert.That(root.Ledger.TryGetActiveOperation(members[0], out _), Is.False, "the first member, judged before the refusal, has no operation");
            Assert.That(root.Ledger.TryGetActiveOperation(members[2], out _), Is.False, "nor the third");
            Assert.That(root.Ledger.TryGetActiveOperation(members[1], out CutOperationId still) && still.Equals(held), Is.True, "the middle keeps only its own");
            Assert.That(root.Fusion.GroupCount == 1 && ReferenceEquals(root.Fusion.Groups[0], group), Is.True, "the same group");
            Assert.That(group.MemberCount, Is.EqualTo(3));
            Assert.That(ReferenceEquals(group.Root, groupRoot) && ReferenceEquals(group.Body, groupBody) && group.Body.isKinematic, Is.True, "with its root and body, still held");
            Assert.That((group.Root.transform.position - groupAt).magnitude, Is.LessThan(1e-4f));
            Assert.That(group.Busy, Is.False);
            foreach (LogicalFragmentId f in members)
            {
                Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsFused && ReferenceEquals(o.Group, group), Is.True, "member " + f.value + " stands in the group");
                Assert.That(root.Ledger.IsCurrentTarget(f), Is.True, "and is live");
                foreach (MeshCollider c in group.byFragment[f].colliders) Assert.That(c.attachedRigidbody, Is.EqualTo(groupBody), "its colliders belong to the group's body");
                Assert.That(GameObject.Find("Shadow of " + f.value), Is.Null, "no shadow");
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **A cut whose crossed members would not all fit the ledger's room is refused as a whole, with nothing
        /// changed.** With room for two incomplete cuts, three fused pieces in a column are crossed by one vertical plane:
        /// the group cut is refused, no operation is admitted, the group stands as it was; a plane crossing one member is
        /// then accepted.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_ShortLedgerRoom_RefusesTheWholeCut_AndChangesNothing()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile =>
            {
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingWorldEnabled", false);
                SetPrivate(profile, "buildingFusionEnabled", true);
                SetPrivate(profile, "maxIncompleteCuts", 2);
            });
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var quarters = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), quarters);
            yield return UntilFused(root, 3, "three fused in a column");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));
            int operationsBefore = OperationCount(root);
            int reCutsBefore = root.Fusion.GroupCuts;

            // A vertical plane in the lower half's frame crosses all three; the ledger's room is two.
            ProvisionalCutAsk ask = Ask(lower, new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            yield return null;
            WriteFusionRecord(root, "after the refused cut");
            Assert.That(root.Fusion.GroupCutsRefused, Is.EqualTo(1), "refused as a whole");
            Assert.That(root.Fusion.MembersRefused, Is.Zero, "by the room, not by a member");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(reCutsBefore), "no group cut");
            Assert.That(OperationCount(root), Is.EqualTo(operationsBefore), "no operation admitted");
            Assert.That(root.Fusion.GroupCount == 1 && root.Fusion.Groups[0].MemberCount == 3, Is.True, "the group stands as it was");
            Assert.That(root.Fusion.Groups[0].Body.isKinematic, Is.True);

            // A plane crossing one member fits: horizontal through the top quarter's own centre (a vertical one would cross the column).
            float y = ColliderBoundsOf(root, quarters[0]).center.y;
            ProvisionalCutAsk one = Ask(quarters[0], InShapeFrame(root, quarters[0], new float4(0f, 1f, 0f, -y)));
            Assert.That(root.TryAsk(in one), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (one)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(reCutsBefore + 1), "a cut within the room is accepted");
            yield return Until(() => root.Fusion.FinalsPublished >= 1 || root.Fusion.FinalsFailed > 0, "its Final");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An oblique, asymmetric cut shares a crossed member's mass, centre and inertia by the Provisional box rule.**
        /// Two fused halves on a platform; the plane x + y = 1.3 (world) cuts a corner prism off the upper half (a
        /// 0.7 x 0.7 right triangle of its 2 x 1 section: 12.25 % of it) and misses the lower. Before the Final (held),
        /// the positive side's body carries exactly the rule's positive part -- mass, centre through the member's Root,
        /// and the rule's inertia -- and the negative side the lower half plus the rule's negative part; a linear share
        /// along the normal would have given the corner more than twice its mass.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_AnObliqueAsymmetricCut_SharesMassByTheProvisionalBoxRule()
        {
            CutWorldRoot root = NewFusionWorld(-12f);
            AddKinematicPlatform();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            yield return UntilFused(root, 2, "the halves fused on the platform");
            double whole = root.Fusion.Groups[0].Mass;
            Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner), Is.True);
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            float upperMass = upperOwner.Group.byFragment[upper].mass, lowerMass = lowerOwner.Group.byFragment[lower].mass;

            float s = 1f / math.sqrt(2f);
            var planeWorld = new float4(s, s, 0f, -1.3f * s);   // n.x + d = 0: (x + y) / sqrt 2 = 1.3 / sqrt 2
            float4 planeLocal = InShapeFrame(root, upper, planeWorld);
            BoxRule(root, upper, planeLocal, out ProvisionalBoxMass.Side expectedPositive, out ProvisionalBoxMass.Side expectedNegative);
            TestContext.Out.WriteLine("box rule: positive " + expectedPositive.mass.ToString("F4") + " kg at " + ((Vector3)expectedPositive.centerOfMass).ToString("F3") + " inertia " + ((Vector3)expectedPositive.inertia).ToString("F3")
                + "; negative " + expectedNegative.mass.ToString("F4") + " kg at " + ((Vector3)expectedNegative.centerOfMass).ToString("F3"));
            Assert.That(expectedPositive.mass + expectedNegative.mass, Is.EqualTo(upperMass).Within(1e-4), "the rule's parts sum to the member");
            Assert.That(expectedPositive.mass, Is.EqualTo(0.1225 * upperMass).Within(0.02 * upperMass), "the corner prism's share by volume (independent expectation)");

            root.Fusion.holdFinals = true;
            ProvisionalCutAsk ask = Ask(upper, planeLocal);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1), "the group was cut");
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(1), "only the upper half is crossed");
            Assert.That(root.Fusion.FinalsPublished, Is.Zero, "the Finals are held");
            Assert.That(root.Owners.TryGet(upper, out upperOwner), Is.True);
            Assert.That(root.Owners.TryGet(lower, out lowerOwner), Is.True);
            FusedGroup positive = upperOwner.Group, negative = lowerOwner.Group;
            Assert.That(positive.Root.name, Does.Contain("+"));
            Assert.That(negative.Root.name, Does.Contain("-"));
            Assert.That(positive.Body.isKinematic || negative.Body.isKinematic, Is.False, "both sides are dynamic (no anchor)");
            WriteFusionRecord(root, "interval: positive " + positive.Body.mass.ToString("F4") + " kg, negative " + negative.Body.mass.ToString("F4") + " kg");

            // Mass: the positive side is the rule's positive part alone; the negative side the lower half plus the rule's negative part.
            Assert.That(positive.Body.mass, Is.EqualTo(expectedPositive.mass).Within(1e-3), "the positive side's mass is the rule's positive part");
            Assert.That(positive.Mass, Is.EqualTo(expectedPositive.mass).Within(1e-3));
            Assert.That(negative.Body.mass, Is.EqualTo(lowerMass + expectedNegative.mass).Within(1e-3), "the negative side's mass is the lower half plus the rule's negative part");
            Assert.That(positive.Mass + negative.Mass, Is.EqualTo(whole).Within(1e-3), "the sides sum to the whole");
            // Centre: the rule's centre, in the member Root's frame, placed by the Root (positive) and by the shadow (negative).
            Vector3 expectedPositiveCentre = upperOwner.Root.transform.TransformPoint(expectedPositive.centerOfMass);
            Assert.That((positive.Body.worldCenterOfMass - expectedPositiveCentre).magnitude, Is.LessThan(1e-3f), "the positive side's centre is the rule's, through the member's Root");
            GameObject shadow = GameObject.Find("Shadow of " + upper.value);
            Assert.That(shadow, Is.Not.Null);
            Vector3 lowerCentre = lowerOwner.Root.transform.TransformPoint(lowerOwner.Group.byFragment[lower].centreLocal);
            Vector3 expectedNegativeCentre = (lowerCentre * lowerMass + shadow.transform.TransformPoint(expectedNegative.centerOfMass) * (float)expectedNegative.mass) / (lowerMass + (float)expectedNegative.mass);
            Assert.That((negative.Body.worldCenterOfMass - expectedNegativeCentre).magnitude, Is.LessThan(1e-3f), "the negative side's centre weighs the lower half and the rule's negative part through the shadow");
            // Inertia: one contribution on the positive side, so its principal moments are the rule's.
            Vector3 got = Sorted(positive.Body.inertiaTensor), want = Sorted(expectedPositive.inertia);
            for (int i = 0; i < 3; i++) Assert.That(got[i], Is.EqualTo(want[i]).Within(1e-3f * math.max(1f, want[i])), "the positive side's principal moment " + i + " is the rule's");

            yield return Until(() => root.Fusion.AllCooksOver, "the cook");
            Assert.That(root.Fusion.FinalsPublished, Is.Zero, "still held after the cook");
            root.Fusion.holdFinals = false;
            root.Fusion.CollectFinals();
            Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(1), "the Final, collected on release");
            double after = 0.0; foreach (FusedGroup g in root.Fusion.Groups) after += g.Mass;
            Assert.That(after, Is.EqualTo(whole).Within(1e-3), "mass conserved across the Final");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A rotating group's sides inherit v + w x r.** Two fused halves on a platform are cut vertically and the
        /// platform is taken away in the same frame, so the two dynamic sides never rest and fall as dynamic groups
        /// (a resting group that loses its platform is not released within 20 s: not this test's subject). A falling
        /// side is given an angular velocity about z and cut by a vertical plane through its centre; its two sides keep
        /// the angular velocity, and their linear velocities differ by w x (c+ - c-) -- about 1 m/s here, which a fall
        /// alone (the same gravity on both) cannot make.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_ARotatingGroup_PassesItsAngularMotionToTheSides()
        {
            CutWorldRoot root = NewFusionWorld(-12f);
            GameObject platform = AddKinematicPlatform();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            yield return UntilFused(root, 2, "the halves fused on the platform");
            LogicalFragmentId first = root.Fusion.Groups[0].Representative;
            ProvisionalCutAsk split = Ask(first, InShapeFrame(root, first, new float4(1f, 0f, 0f, 0f)));
            Assert.That(root.TryAsk(in split), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (split)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1), "the resting group was cut into two dynamic sides");
            UnityEngine.Object.DestroyImmediate(platform);
            _objects.Remove(platform);
            yield return Until(() => root.Fusion.FinalsPublished == 2 || root.Fusion.FinalsFailed > 0, "the first cut's Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            FusedGroup falling = null;
            yield return UntilWithin(() =>
            {
                falling = null;
                foreach (FusedGroup g in root.Fusion.Groups) if (g.Body != null && !g.Body.isKinematic && g.MemberCount == 2 && g.Body.linearVelocity.y < -1f) { falling = g; break; }
                return falling != null;
            }, RestSettleSeconds, "a side with two members is falling");

            // No separation impulse of any rule: what the sides start with is the inheritance alone.
            root.Fusion.SeparationStrength = null;
            root.Fusion.DefaultSeparationImpulse = 0f;
            root.Fusion.holdFinals = true;
            int cutsBefore = root.Fusion.GroupCuts;
            falling.Body.angularVelocity = new Vector3(0f, 0f, 2f);
            Vector3 omega = falling.Body.angularVelocity;
            Vector3 centre = falling.Body.worldCenterOfMass;
            LogicalFragmentId member = falling.Representative;
            ProvisionalCutAsk ask = Ask(member, InShapeFrame(root, member, new float4(1f, 0f, 0f, -centre.x)));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(cutsBefore + 1), "the falling side was cut");
            FusedGroup positive = null, negative = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (!g.Busy) continue; if (g.Root.name.Contains("+")) positive = g; else negative = g; }
            Assert.That(positive != null && negative != null && !positive.Body.isKinematic && !negative.Body.isKinematic, Is.True, "two dynamic sides of the rotating group");
            Vector3 dv = positive.Body.linearVelocity - negative.Body.linearVelocity;
            Vector3 dc = positive.Body.worldCenterOfMass - negative.Body.worldCenterOfMass;
            Vector3 expected = Vector3.Cross(omega, dc);
            TestContext.Out.WriteLine("omega " + omega.ToString("F3") + "; sides' angular " + positive.Body.angularVelocity.ToString("F3") + " / " + negative.Body.angularVelocity.ToString("F3")
                + "; dc " + dc.ToString("F3") + "; dv " + dv.ToString("F3") + " expected w x dc " + expected.ToString("F3"));
            Assert.That((positive.Body.angularVelocity - omega).magnitude, Is.LessThan(0.1f), "the positive side keeps the angular velocity");
            Assert.That((negative.Body.angularVelocity - omega).magnitude, Is.LessThan(0.1f), "the negative side keeps the angular velocity");
            Assert.That(expected.magnitude, Is.GreaterThan(0.3f), "the sides' centres are apart enough for w x r to show");
            Assert.That((dv - expected).magnitude, Is.LessThan(0.1f), "the sides' linear velocities differ by w x (c+ - c-)");
            yield return Until(() => root.Fusion.AllCooksOver, "the cooks");
            root.Fusion.holdFinals = false;
            root.Fusion.CollectFinals();
            Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(4), "the second cut's Finals on release");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Before the Final, with the sides apart, the display, the colliders and the mass properties correspond; the
        /// Final retires the shadow's colliders in its own call.** An anchored building's two fused halves are cut by a
        /// vertical plane with the Finals held: the free side slides off the anchored one. A second later, for each
        /// crossed member, the placement by side answers its Root (positive) and its shadow (negative), apart; its own
        /// colliders belong to the positive side's body and the shadow's copies to the negative side's; the dynamic
        /// side's mass and centre are the box rule's positive parts and the anchored side's number the negative parts.
        /// The Finals are then collected in one call: in that same call the shadows' colliders are disabled, the
        /// replaced members answer nothing, and the mass is conserved.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_BeforeTheFinal_SidesApart_DisplayCollidersAndMassCorrespond_ThenTheFinalRetiresTheShadowAtOnce()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            yield return UntilFused(root, 2, "both fused (the anchored one as well)");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));
            double whole = root.Fusion.Groups[0].Mass;
            var crossed = new[] { upper, lower };
            var planeLocal = new Dictionary<LogicalFragmentId, float4>();
            var rule = new Dictionary<LogicalFragmentId, (ProvisionalBoxMass.Side positive, ProvisionalBoxMass.Side negative)>();
            foreach (LogicalFragmentId m in crossed)
            {
                planeLocal[m] = InShapeFrame(root, m, new float4(1f, 0f, 0f, 0f));
                BoxRule(root, m, planeLocal[m], out ProvisionalBoxMass.Side p, out ProvisionalBoxMass.Side n);
                rule[m] = (p, n);
            }

            root.Fusion.holdFinals = true;
            ProvisionalCutAsk ask = Ask(upper, planeLocal[upper]);
            ask.positiveSeparationImpulse = 12f;
            ask.negativeSeparationImpulse = 12f;
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(2), "both members are crossed");
            Assert.That(root.Fusion.SidesKinematic == 1 && root.Fusion.SidesDynamic == 1, Is.True, "one side keeps the anchor, the other is free");
            var ops = new Dictionary<LogicalFragmentId, CutOperationId>();
            var shadowColliders = new Dictionary<LogicalFragmentId, List<MeshCollider>>();
            foreach (LogicalFragmentId m in crossed)
            {
                ops[m] = AdmittedOne(root, m);
                shadowColliders[m] = ShadowCollidersOf(m);
            }

            yield return PhysicsSeconds(1.0);
            yield return Until(() => root.Fusion.AllCooksOver, "the cooks");
            Assert.That(root.Fusion.FinalsPublished, Is.Zero, "the Finals are held: the interval is read with certainty");
            FusedGroup positive = null, negative = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Kinematic) negative = g; else positive = g; }
            Assert.That(positive != null && negative != null, Is.True, "a dynamic side and the anchored side");
            Assert.That(positive.Root.name, Does.Contain("+"), "the free side is the positive one (the anchor is at x < 0)");
            double expectedPositiveMass = 0.0, expectedNegativeMass = 0.0;
            Vector3 weighted = Vector3.zero;
            foreach (LogicalFragmentId m in crossed)
            {
                Assert.That(root.Owners.TryGet(m, out PhysicsFragmentOwner mo) && mo.IsFused, Is.True);
                // The display: the positive side placed by the member's Root, the negative by its shadow, and they are apart.
                VpFragmentPlacementKind pk = root.Placement.TryGetGeometryLocalToWorld(m, ops[m], 1f, out Matrix4x4 positiveAt);
                VpFragmentPlacementKind nk = root.Placement.TryGetGeometryLocalToWorld(m, ops[m], -1f, out Matrix4x4 negativeAt);
                Assert.That(pk == VpFragmentPlacementKind.Following && nk == VpFragmentPlacementKind.Following, Is.True, "both sides of " + m.value + " are placed before the Final");
                GameObject shadow = GameObject.Find("Shadow of " + m.value);
                Assert.That(shadow, Is.Not.Null);
                AssertSame(mo.Root.transform.localToWorldMatrix * mo.GeometryLocalToOwner.Value, positiveAt, "the positive side's placement of " + m.value + " is its Root's");
                AssertSame(shadow.transform.localToWorldMatrix * mo.GeometryLocalToOwner.Value, negativeAt, "the negative side's placement of " + m.value + " is its shadow's");
                float apart = (positiveAt.GetPosition() - negativeAt.GetPosition()).magnitude;
                TestContext.Out.WriteLine("member " + m.value + ": sides apart by " + apart.ToString("F3") + " m");
                Assert.That(apart, Is.GreaterThan(0.1f), "the sides of " + m.value + " are apart");
                // The colliders: the member's own on the positive side's body, the shadow's copies on the negative side's.
                FusedGroup.Member member = mo.Group.byFragment[m];
                Assert.That(ReferenceEquals(mo.Group, positive), Is.True, "the crossed member itself stands on the positive side");
                Assert.That(member.colliders.Count, Is.GreaterThan(0));
                foreach (MeshCollider c in member.colliders) Assert.That(c.enabled && c.attachedRigidbody == positive.Body, Is.True, "the member's collider belongs to the positive side's body");
                Assert.That(shadowColliders[m].Count, Is.EqualTo(member.colliders.Count), "the shadow copies every collider");
                for (int i = 0; i < shadowColliders[m].Count; i++)
                {
                    MeshCollider copy = shadowColliders[m][i];
                    Assert.That(copy.enabled && copy.attachedRigidbody == negative.Body, Is.True, "the shadow's collider belongs to the negative side's body");
                    Assert.That(copy.sharedMesh, Is.EqualTo(member.colliders[i].sharedMesh), "with the same convex");
                }

                expectedPositiveMass += rule[m].positive.mass;
                expectedNegativeMass += rule[m].negative.mass;
                weighted += mo.Root.transform.TransformPoint(rule[m].positive.centerOfMass) * (float)rule[m].positive.mass;
            }

            // The mass properties: the dynamic side's body carries the rule's positive parts (mass and centre); the anchored side keeps the number.
            WriteFusionRecord(root, "interval: positive " + positive.Body.mass.ToString("F4") + " kg, negative " + negative.Mass.ToString("F4") + " kg");
            Assert.That(positive.Body.mass, Is.EqualTo(expectedPositiveMass).Within(1e-3), "the free side's mass is the rule's positive parts");
            Assert.That(positive.Mass, Is.EqualTo(expectedPositiveMass).Within(1e-3));
            Assert.That(negative.Mass, Is.EqualTo(expectedNegativeMass).Within(1e-3), "the anchored side's mass is the rule's negative parts");
            Assert.That(positive.Mass + negative.Mass, Is.EqualTo(whole).Within(1e-3), "the sides sum to the whole");
            Assert.That((positive.Body.worldCenterOfMass - weighted / (float)expectedPositiveMass).magnitude, Is.LessThan(1e-2f), "the free side's centre is the rule's, through the members' Roots");

            // The Finals, in one call: the shadows' colliders stop in that call, the replaced members answer nothing.
            root.Fusion.holdFinals = false;
            root.Fusion.CollectFinals();
            Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(2), "both Finals collected in the call");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            foreach (LogicalFragmentId m in crossed)
            {
                foreach (MeshCollider copy in shadowColliders[m])
                {
                    Assert.That(copy != null, Is.True, "the shadow's object is still there in this frame (its destruction is the frame's end)");
                    Assert.That(copy.enabled, Is.False, "and its collider is disabled in the Final's own call");
                }

                Assert.That(root.Owners.TryGet(m, out _), Is.False, "the replaced member is retired");
                Assert.That(root.Placement.TryGetGeometryLocalToWorld(m, ops[m], -1f, out _), Is.EqualTo(VpFragmentPlacementKind.Missing), "and answers nothing");
            }

            foreach (FusedGroup g in root.Fusion.Groups) Assert.That(g.Busy, Is.False, "no side is busy");
            double after = 0.0; foreach (FusedGroup g in root.Fusion.Groups) after += g.Mass;
            Assert.That(after, Is.EqualTo(whole).Within(1e-3), "mass conserved across the Finals");
            yield return null;
            foreach (LogicalFragmentId m in crossed) Assert.That(GameObject.Find("Shadow of " + m.value), Is.Null, "the shadow is gone by the next frame");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A held piece of the building is not fused into its group while the group is being cut, and is once the cut
        /// ends; a held side group waits the same way.** The upper half is kept from fusing (test hook) so the anchored
        /// lower half is the group alone. A first cut frees a side that slides off and its Final comes; at once a second
        /// cut of the anchored group is made with the Finals held, so the group stays busy. The upper half, let go by
        /// the hook, comes to rest with its own body and is deferred (counted); the first cut's free side comes to rest
        /// and its merge is deferred (counted, it stays a group of its own); the Finals are released, and then the piece
        /// fuses and the side merges.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_AHeldPiece_IsNotFusedIntoABusyGroup_AndIsAfterTheRelease()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            root.Fusion.holdFusionHook = f => f.Equals(upper);
            yield return UntilFused(root, 1, "the lower half fused alone");
            yield return null;
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.IsFused, Is.True, "the lower half is the group");
            Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner) && !upperOwner.IsFused && upperOwner.Body != null, Is.True, "the upper half keeps its own body");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));

            // The first cut: vertical; the x > 0 side is free and slides off; its Final comes on its own.
            ProvisionalCutAsk first = Ask(lower, InShapeFrame(root, lower, new float4(1f, 0f, 0f, 0f)));
            first.positiveSeparationImpulse = 12f;
            first.negativeSeparationImpulse = 12f;
            Assert.That(root.TryAsk(in first), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (first)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            yield return Until(() => root.Fusion.FinalsPublished == 1 || root.Fusion.FinalsFailed > 0, "the first Final");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            FusedGroup main = null, freeSide = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Kinematic) main = g; else freeSide = g; }
            Assert.That(main != null && freeSide != null && !main.Busy, Is.True, "the anchored group is free again and the free side is on its way");
            Assert.That(main.MemberCount, Is.EqualTo(1), "the anchored group holds the lower half's anchored child");

            // The second cut, at once, with the Finals held: horizontal through the anchored child; the group stays busy.
            root.Fusion.holdFinals = true;
            LogicalFragmentId child = main.Representative;
            ProvisionalCutAsk second = Ask(child, InShapeFrame(root, child, new float4(0f, 1f, 0f, 0.5f)));   // the world plane y = -0.5; the anchor (y -0.9) keeps the bottom
            Assert.That(root.TryAsk(in second), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (second)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(2));
            main = null; foreach (FusedGroup g in root.Fusion.Groups) if (g.Kinematic && g.Busy) main = g;
            Assert.That(main, Is.Not.Null, "the anchored side of the second cut is the building's group, busy");
            Assert.That(freeSide.Root != null && !freeSide.Busy, Is.True, "the first cut's free side stands, not busy");
            int deferredBefore = root.Fusion.FusionsDeferred, mergesBefore = root.Fusion.MergesDeferred;
            root.Fusion.holdFusionHook = null;
            yield return UntilWithin(() => root.Fusion.FusionsDeferred > deferredBefore, RestSettleSeconds, "the upper half's fusion deferred");
            Assert.That(root.Owners.TryGet(upper, out upperOwner) && !upperOwner.IsFused && upperOwner.Body != null, Is.True, "the upper half still has its own body");
            yield return UntilWithin(() => root.Fusion.MergesDeferred > mergesBefore, RestSettleSeconds, "the free side's merge deferred once it rests");
            WriteFusionRecord(root, "while busy: fusions deferred " + (root.Fusion.FusionsDeferred - deferredBefore) + ", merges deferred " + (root.Fusion.MergesDeferred - mergesBefore));
            Assert.That(freeSide.Root != null && freeSide.Kinematic && freeSide.MemberCount > 0, Is.True, "the held side waits as a group of its own");
            Assert.That(main.Busy, Is.True);
            yield return Until(() => root.Fusion.AllCooksOver, "the cook");
            Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(1), "the second Final is held");

            root.Fusion.holdFinals = false;
            root.Fusion.CollectFinals();
            Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(2), "the second Final on release");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(main.Busy, Is.False, "the group is free");
            yield return UntilWithin(() => root.Owners.TryGet(upper, out PhysicsFragmentOwner o) && o.IsFused, RestSettleSeconds, "the upper half fused after the release");
            yield return UntilWithin(() => root.Fusion.GroupCount == 1, RestSettleSeconds, "the sides merged after the release");
            WriteFusionRecord(root, "after the release");
            Assert.That(root.Fusion.GroupsMerged, Is.GreaterThanOrEqualTo(1));
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A crossed member whose cook fails is reclaimed alone, its shadow's colliders stopped in the failure's own
        /// call.** The failure hook refuses the products of one member's cook (collected on release): the member is
        /// retired (its operation aborted, its colliders and its shadow's disabled at once, the shadow gone by the next
        /// frame), the sides stand with their other members, the group is free again, and the ending gives everything back.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionCut_AFailedCook_RetiresThatMemberAlone_AndItsShadowAtOnce()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            yield return UntilFused(root, 2, "two fused");
            root.Fusion.failProductsHook = f => f.Equals(upper);
            root.Fusion.holdFinals = true;
            ProvisionalCutAsk ask = Ask(upper, InShapeFrame(root, upper, new float4(0f, 1f, 0f, -0.5f)));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            CutOperationId op = AdmittedOne(root, upper);
            List<MeshCollider> shadowColliders = ShadowCollidersOf(upper);
            Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner) && upperOwner.IsFused, Is.True);
            var own = new List<MeshCollider>(upperOwner.Group.byFragment[upper].colliders);
            yield return Until(() => root.Fusion.AllCooksOver, "the cook's end");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero, "held");

            root.Fusion.holdFinals = false;
            root.Fusion.CollectFinals();
            WriteFusionRecord(root, "after the failed cook");
            Assert.That(root.Fusion.FinalsFailed, Is.EqualTo(1), "the failure path ran in the call");
            Assert.That(root.Fusion.FinalsPublished, Is.Zero);
            foreach (MeshCollider c in shadowColliders) Assert.That(c != null && !c.enabled, Is.True, "the shadow's collider is disabled in the failure's own call");
            foreach (MeshCollider c in own) Assert.That(c == null || !c.enabled, Is.True, "the member's own collider answers no more");
            Assert.That(root.Owners.TryGet(upper, out _), Is.False, "the member is retired");
            Assert.That(root.Ledger.IsCurrentTarget(upper), Is.False, "and not a target any more");
            Assert.That(root.Placement.TryGetGeometryLocalToWorld(upper, op, -1f, out _), Is.EqualTo(VpFragmentPlacementKind.Missing));
            foreach (FusedGroup g in root.Fusion.Groups) Assert.That(g.Busy, Is.False, "no group is busy");
            yield return null;
            Assert.That(GameObject.Find("Shadow of " + upper.value), Is.Null, "its shadow is gone");
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.IsFused && lowerOwner.Root != null, Is.True, "the other member stands");
            Assert.That(IsHitTarget(root, lower), Is.True);
            root.Fusion.failProductsHook = null;
            yield return EndWorld(root);
            yield return null;
            Assert.That(root.Fusion, Is.Null);
            Assert.That(GameObject.Find("Fused Group 1") == null, Is.True, "no group object remains");
        }
    }
}
