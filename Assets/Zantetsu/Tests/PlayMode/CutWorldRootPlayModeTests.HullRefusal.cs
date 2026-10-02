using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.LowLevelPhysics;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A convex PhysX refuses to cook, through the ordinary Final and through a fused member's Final (2026-09-30): the
    /// cook's hull check reads the cooked shape from a collider before the products are handed over, and a refused
    /// convex ends the cut through the existing failure path -- the ordinary driver's abort, the fusion's member Fail --
    /// with nothing of it published, counted once and attributed to its operation and member, its holds, meshes,
    /// shadow and Busy state given back; a valid cut afterwards goes through.
    /// <para>
    /// The refused shape: one produced mesh of the target cut is replaced, after the kernel wrote it and before the bake,
    /// by four vertices of which two coincide and the other three span all three axes (a tilted triangle): PhysX's
    /// cooking refuses it -- "Less than four valid vertices" -- as it refused the Player's convex, in the worker's bake and
    /// again at the collider. The cut, the bake, the check and the recovery are the product's own; only the mesh's
    /// contents are the test's. The license-free box of the fixture is the input.
    /// </para>
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>Four vertices PhysX refuses to cook as a convex (probe test ConvexHullValidityProbePlayModeTests): a tilted triangle and a copy of one corner.</summary>
        private static void MakeRefusedConvex(Mesh mesh)
        {
            mesh.Clear();
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0.15f, 0.1f), new Vector3(0.1f, 0.5f, 0.2f), new Vector3(0.1f, 0.5f, 0.2f) };
            mesh.triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };
            mesh.RecalculateBounds();
        }

        /// <summary>The enabled convex colliders on active objects that have no cooked shape (what must never be published).</summary>
        private static List<string> ShapelessColliders()
        {
            var shapeless = new List<string>();
            foreach (MeshCollider c in Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!c.enabled || !c.convex || c.sharedMesh == null || !c.gameObject.activeInHierarchy) continue;
                if (c.GeometryHolder.Type == GeometryType.Invalid) shapeless.Add(c.transform.root.name + "/" + c.name + " '" + c.sharedMesh.name + "'");
            }

            return shapeless;
        }

        /// <summary>The live meshes a cut of the cook made (its serial is in their names).</summary>
        private static int MeshesOfCut(int serial)
        {
            string prefix = "Zantetsu Physics Cut " + serial + ".";
            int n = 0;
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>()) if (m != null && m.name.StartsWith(prefix)) n++;
            return n;
        }

        /// <summary>The first cut the cook takes up from now on gets its first produced meshes (as many as asked) replaced by a refused convex; its serial is returned through the box.</summary>
        private static void RefuseTheNextCut(CutWorldRoot root, int[] serial, int meshes = 1)
        {
            serial[0] = -1;
            root.Cook.meshOverrideForTest = (request, m, mesh) =>
            {
                if (serial[0] < 0) serial[0] = request.Serial;
                if (request.Serial != serial[0] || m >= meshes) return false;
                MakeRefusedConvex(mesh);
                return true;
            };
        }

        /// <summary>The enabled colliders of a live piece all have a convex shape.</summary>
        private static void AssertCookedShapes(CutWorldRoot root, LogicalFragmentId fragment, string what)
        {
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.Root != null, Is.True, what + ": the piece stands");
            int enabled = 0;
            foreach (MeshCollider c in owner.Root.GetComponentsInChildren<MeshCollider>())
            {
                if (!c.enabled) continue;
                enabled++;
                Assert.That(c.GeometryHolder.Type, Is.EqualTo(GeometryType.ConvexMesh), what + ": collider '" + c.sharedMesh.name + "' has a cooked convex shape");
            }

            Assert.That(enabled, Is.GreaterThan(0), what + ": the piece has enabled colliders");
        }

        /// <summary>
        /// **The ordinary cut: a convex PhysX refuses is not published; the cut fails once, is recovered, and the next cut
        /// goes through.** The first cut's produced mesh is refused: the cook ends it CookFailed with the refused convex on
        /// record; the driver counts it once, attributes it (operation, source, path "driver") and aborts it the ordinary
        /// way -- the source retires, the pair leaves the scene, the cut's meshes are destroyed -- and no enabled collider
        /// is left without a shape. Every cooking error logged names that mesh. A cut of another body afterwards publishes
        /// both sides with cooked shapes, and nothing more is counted.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_TheOrdinaryCut_IsNotPublished_CountedOnce_Recovered_AndTheNextCutGoesThrough()
        {
            LogAssert.ignoreFailingMessages = true;   // PhysX's own errors for the refused convex are expected, and read below
            var audit = new CookingErrorAudit();
            audit.Begin();
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                LogicalFragmentId body = AddBody(root, Vector3.zero);
                LogicalFragmentId other = AddBody(root, new Vector3(6f, 0f, 0f));
                yield return null;
                int failedBefore = root.Driver.FailedCutCount;
                var serial = new int[1];
                RefuseTheNextCut(root, serial);
                Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
                yield return Until(() => root.Driver.FailedCutCount > failedBefore, "the cut failed");
                root.Cook.meshOverrideForTest = null;
                yield return null;
                yield return null;

                TestContext.Out.WriteLine("after the refused cut: " + root.Driver.FailureSummary() + "; " + root.Cook.HullSummary()
                    + "; errors: failed-cooking " + audit.FailedMeshes.Count + " [" + string.Join(", ", audit.FailedMeshes) + "], less-than-four " + audit.CleanupErrors);
                Assert.That(root.Driver.FailedCutCount, Is.EqualTo(failedBefore + 1), "one failed cut");
                Assert.That(root.Driver.HullRejectedCutCount, Is.EqualTo(1), "failed for the refused convex");
                Assert.That(root.Cook.HullRejectedCuts, Is.EqualTo(1), "the cook counted it once");
                Assert.That(root.Cook.AttributedHullRejections, Is.EqualTo(1), "and the driver attributed it once");
                PhysicsCutHullRejection r = root.Cook.HullRejections[0];
                Assert.That(r.request, Is.EqualTo(serial[0]), "the refused cut is the one whose mesh was replaced");
                Assert.That(r.mesh == 0 && r.positive && r.geometry == "Invalid" && r.vertexCount == 4, Is.True, "its first mesh, the positive side, no cooked shape: " + r);
                Assert.That(r.path == "driver" && r.fragment.Equals(body) && r.operation.IsSet, Is.True, "attributed to the source and its operation: " + r);
                Assert.That(root.Ledger.IsCurrentTarget(body), Is.False, "the source retired, as every aborted cut's does");
                Assert.That(ShapelessColliders(), Is.Empty, "no collider without a cooked shape is in the scene");
                Assert.That(MeshesOfCut(serial[0]), Is.Zero, "the failed cut's meshes were destroyed");
                Assert.That(root.Cook.IsDrained, Is.True, "the cook holds nothing of it");
                Assert.That(audit.FailedMeshes.Count, Is.GreaterThanOrEqualTo(1), "PhysX logged its refusal");
                foreach (string name in audit.FailedMeshes) Assert.That(name, Is.EqualTo(r.meshName), "every cooking error names the refused mesh");
                Assert.That(audit.Unaccounted(root.Cook.WasRejected, null), Is.Zero, "no cooking error is unaccounted");

                // The next cut, of another body, with nothing replaced.
                int checksBefore = root.Cook.HullChecks;
                var sides = new LogicalFragmentId[2];
                yield return CutInTwo(root, other, new float4(0f, 1f, 0f, 0f), sides);
                yield return null;
                AssertCookedShapes(root, sides[0], "the positive side");
                AssertCookedShapes(root, sides[1], "the negative side");
                Assert.That(root.Cook.HullChecks, Is.GreaterThan(checksBefore), "its convexes were checked");
                Assert.That(root.Driver.FailedCutCount, Is.EqualTo(failedBefore + 1), "and nothing more failed");
                Assert.That(root.Cook.HullRejectedCuts, Is.EqualTo(1), "nothing more was refused");
                Assert.That(ShapelessColliders(), Is.Empty);
                TestContext.Out.WriteLine("after the next cut: " + root.Cook.HullSummary());
                yield return EndWorld(root);
            }
            finally
            {
                audit.End();
            }
        }

        /// <summary>
        /// **A fused member's Final: a convex PhysX refuses fails that member only, the group is left whole, and the next
        /// group cut goes through.** Two fused halves are cut by a vertical plane that crosses both; the first member cook
        /// taken up has its produced mesh refused. That member fails through the fusion's Fail (its operation aborted, the
        /// member retired, its shadow gone, its input hold given back, its sides' masses redone), attributed once (path
        /// "fusion"); the other member's Final is published with cooked shapes. No group stays Busy, the groups' members
        /// are all live on their bodies with masses summing to the whole less the failed member's, no enabled collider
        /// is without a shape, and every cooking error names the refused mesh. A later cut of a group publishes.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_AFusedMember_FailsAlone_TheGroupIsWhole_AndTheNextGroupCutGoesThrough()
        {
            LogAssert.ignoreFailingMessages = true;
            var audit = new CookingErrorAudit();
            audit.Begin();
            try
            {
                CutWorldRoot root = NewFusionWorld();
                LogicalFragmentId building = AddBuilding(root, Vector3.zero);
                yield return null;
                var halves = new LogicalFragmentId[2];
                yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
                LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
                yield return UntilFused(root, 2, "the halves fused");
                Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));
                double whole = root.Fusion.Groups[0].Mass;
                var shapes = new Dictionary<LogicalFragmentId, PhysicsOwnerShape>();
                var masses = new Dictionary<LogicalFragmentId, double>();
                foreach (LogicalFragmentId f in new[] { upper, lower })
                {
                    Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o), Is.True);
                    shapes[f] = o.Shape;
                    masses[f] = o.Mass;
                }

                int failedBefore = root.Fusion.FinalsFailed, publishedBefore = root.Fusion.FinalsPublished;
                var serial = new int[1];
                RefuseTheNextCut(root, serial);
                Assert.That(root.Fusion.IsSettled, Is.True, "nothing waits before the cut: " + root.Fusion.DescribeUnsettled());
                Assert.That(root.TryAsk(SlashAsk(root, upper, new float4(1f, 0f, 0f, 0f), 1, 0f)), Is.True, "a vertical cut across both members");
                yield return Until(() => root.Fusion.Hits.Count > 0, "the fusion's record of the hit");
                if (root.Fusion.HitsPending > 0)
                {
                    // The hit waits for its outcome (its preparation): the fusion is not settled, and says what waits.
                    Assert.That(root.Fusion.IsSettled, Is.False, "the hit waits for its outcome: " + root.Fusion.DescribeUnsettled());
                    StringAssert.Contains("hits without an outcome 1", root.Fusion.DescribeUnsettled());
                }

                yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation");
                Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1), "the group cut was published");
                if (root.Fusion.CutsInProgress > 0) StringAssert.Contains("crossed members' cuts in progress " + root.Fusion.CutsInProgress, root.Fusion.DescribeUnsettled());
                yield return Until(() => root.Fusion.CutsInProgress == 0, "the Finals");
                root.Cook.meshOverrideForTest = null;
                yield return null;

                WriteFusionRecord(root, "after the refused member");
                foreach (BuildingFusion.MemberOperation op in root.Fusion.MemberOperations) TestContext.Out.WriteLine("  operation " + op.operation.value + " member " + op.source.value + ": " + op.outcome);
                TestContext.Out.WriteLine("  " + root.Cook.HullSummary() + "; errors: failed-cooking " + audit.FailedMeshes.Count + ", less-than-four " + audit.CleanupErrors);
                Assert.That(root.Fusion.FinalsFailed, Is.EqualTo(failedBefore + 1), "one member's Final failed");
                Assert.That(root.Fusion.FinalsHullRefused, Is.EqualTo(1), "for the refused convex");
                Assert.That(root.Fusion.FinalsPublished, Is.EqualTo(publishedBefore + 1), "the other member's Final was published");
                Assert.That(root.Cook.HullRejectedCuts == 1 && root.Cook.AttributedHullRejections == 1, Is.True, "counted and attributed once");
                PhysicsCutHullRejection r = root.Cook.HullRejections[0];
                Assert.That(r.request == serial[0] && r.path == "fusion" && r.operation.IsSet && (r.fragment.Equals(upper) || r.fragment.Equals(lower)), Is.True, "attributed to its member and operation: " + r);
                LogicalFragmentId failed = r.fragment, kept = failed.Equals(upper) ? lower : upper;
                int failedOps = 0;
                foreach (BuildingFusion.MemberOperation op in root.Fusion.MemberOperations)
                {
                    if (!op.source.Equals(failed) || !op.outcome.StartsWith("Failed")) continue;
                    failedOps++;
                    Assert.That(op.operation.value, Is.EqualTo(r.operation.value), "the member's operation is the refused one");
                    Assert.That(op.outcome, Does.Contain("PhysX refused"), op.outcome);
                }

                Assert.That(failedOps, Is.EqualTo(1), "one failed operation of the member");

                // The check's chain (TL, 2026-09-30): the hit names its preparation, and both of that group cut's member
                // operations name it too -- the refused one flagged as PhysX's refusal, apart from the other's success.
                BuildingFusion.HitRecord hit = root.Fusion.Hits[root.Fusion.Hits.Count - 1];
                Assert.That(hit.published && hit.preparation > 0, Is.True, "the hit was published by its preparation: " + hit.outcome);
                int ofPreparation = 0, refusedFlagged = 0;
                foreach (BuildingFusion.MemberOperation op in root.Fusion.MemberOperations)
                {
                    if (op.preparation != hit.preparation) continue;
                    ofPreparation++;
                    if (op.hullRefused) refusedFlagged++;
                    Assert.That(op.hullRefused, Is.EqualTo(op.source.Equals(failed)), "only the refused member's operation is flagged: " + op.outcome);
                }

                Assert.That(ofPreparation, Is.EqualTo(2), "both crossed members' operations belong to the hit's preparation");
                Assert.That(refusedFlagged, Is.EqualTo(1));
                Assert.That(root.Ledger.TryGetOperation(r.operation, out LogicalCutOperation refusedOp) && refusedOp.state == LogicalCutOperationState.Aborted, Is.True, "the refused operation is aborted in the ledger");
                Assert.That(root.Owners.TryGet(failed, out _), Is.False, "and its source has no owner left");
                Assert.That(root.Fusion.IsSettled, Is.True, "nothing waits after the Finals: " + root.Fusion.DescribeUnsettled());
                Assert.That(root.Ledger.IsCurrentTarget(failed), Is.False, "the failed member retired");
                Assert.That(GameObject.Find("Shadow of " + failed.value), Is.Null, "its shadow is gone");
                Assert.That(shapes[failed].WorkUsers, Is.Zero, "its input hold was given back");
                Assert.That(ShapelessColliders(), Is.Empty, "no collider without a cooked shape is in the scene");
                Assert.That(MeshesOfCut(serial[0]), Is.Zero, "the failed cut's meshes were destroyed");
                double sum = 0.0;
                foreach (FusedGroup g in root.Fusion.Groups)
                {
                    Assert.That(g.Busy, Is.False, "no group is left busy");
                    Assert.That(g.Preparing, Is.Zero);
                    sum += g.Mass;
                    foreach (LogicalFragmentId f in g.Fragments)
                    {
                        Assert.That(root.Ledger.IsCurrentTarget(f), Is.True, "member " + f.value + " is live");
                        AssertCookedShapes(root, f, "member " + f.value);
                        Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.Root != null, Is.True);
                        foreach (MeshCollider c in o.Root.GetComponentsInChildren<MeshCollider>()) if (c.enabled) Assert.That(c.attachedRigidbody, Is.EqualTo(g.Body), "member " + f.value + " sits on its group's body");
                    }
                }

                Assert.That(sum, Is.EqualTo(whole - masses[failed]).Within(5e-2 * whole), "the groups carry the whole less the failed member (" + sum.ToString("F3") + " of " + whole.ToString("F3") + ", failed " + masses[failed].ToString("F3") + ")");
                Assert.That(audit.FailedMeshes.Count, Is.GreaterThanOrEqualTo(1), "PhysX logged its refusal");
                foreach (string name in audit.FailedMeshes) Assert.That(name, Is.EqualTo(r.meshName), "every cooking error names the refused mesh");
                Assert.That(audit.Unaccounted(root.Cook.WasRejected, null), Is.Zero);

                // A later cut of a group, with nothing replaced: through a member of the kept one's lineage.
                FusedGroup target = null;
                LogicalFragmentId member = default;
                foreach (FusedGroup g in root.Fusion.Groups)
                {
                    if (g.Busy || g.Preparing > 0 || g.MemberCount == 0) continue;
                    target = g;
                    member = g.Representative;
                    break;
                }

                Assert.That(target, Is.Not.Null, "a group to cut");
                Assert.That(root.Owners.TryGet(member, out PhysicsFragmentOwner mo), Is.True);
                Bounds b = default;
                bool first = true;
                foreach (MeshCollider c in mo.Root.GetComponentsInChildren<MeshCollider>()) if (c.enabled) { if (first) { b = c.bounds; first = false; } else b.Encapsulate(c.bounds); }
                int cutsBefore = root.Fusion.GroupCuts, publishedBeforeNext = root.Fusion.FinalsPublished, failedBeforeNext = root.Fusion.FinalsFailed;
                Assert.That(root.TryAsk(SlashAsk(root, member, new float4(0f, 0f, 1f, -b.center.z), 2, 0f)), Is.True, "a cut through the member's middle");
                yield return null;
                yield return Until(() => root.Fusion.PreparationsInFlight == 0 && root.Fusion.HeldRequestsOf(target.Key) == 0, "the next preparation");
                yield return Until(() => root.Fusion.CutsInProgress == 0, "its Finals");
                WriteFusionRecord(root, "after the next group cut");
                Assert.That(root.Fusion.GroupCuts, Is.GreaterThan(cutsBefore), "the next group cut was published");
                Assert.That(root.Fusion.FinalsPublished, Is.GreaterThan(publishedBeforeNext), "its Finals were published");
                Assert.That(root.Fusion.FinalsFailed, Is.EqualTo(failedBeforeNext), "and none failed");
                Assert.That(root.Cook.HullRejectedCuts, Is.EqualTo(1), "nothing more was refused");
                Assert.That(ShapelessColliders(), Is.Empty);
                yield return EndWorld(root);
            }
            finally
            {
                audit.End();
            }
        }


        /// <summary>
        /// **Two refused convexes of one cut: the cut fails once, each refused mesh is counted and attributed.** Both
        /// produced meshes of the first cut are replaced: every mesh is still checked, both are recorded with the same
        /// operation and source, the driver counts one failed cut, and every cooking error the run logs names one of the
        /// two refused meshes -- none is left unaccounted.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_TwoRefusedConvexesOfOneCut_FailItOnce_AndEachIsCountedAndAttributed()
        {
            LogAssert.ignoreFailingMessages = true;
            var audit = new CookingErrorAudit();
            audit.Begin();
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                LogicalFragmentId body = AddBody(root, Vector3.zero);
                yield return null;
                int failedBefore = root.Driver.FailedCutCount;
                var serial = new int[1];
                RefuseTheNextCut(root, serial, 2);
                Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
                yield return Until(() => root.Driver.FailedCutCount > failedBefore, "the cut failed");
                root.Cook.meshOverrideForTest = null;
                yield return null;
                TestContext.Out.WriteLine(root.Driver.FailureSummary() + "; " + root.Cook.HullSummary() + "; errors " + string.Join(", ", audit.FailedMeshes));
                Assert.That(root.Driver.FailedCutCount, Is.EqualTo(failedBefore + 1), "one failed cut");
                Assert.That(root.Cook.HullRejectedCuts, Is.EqualTo(1), "the cut is counted once");
                Assert.That(root.Cook.HullRejectedMeshes, Is.EqualTo(2), "each refused mesh is counted");
                Assert.That(root.Cook.AttributedHullRejections, Is.EqualTo(1), "attributed once, as a cut");
                Assert.That(root.Cook.HullRejections.Count, Is.EqualTo(2));
                var names = new HashSet<string>();
                foreach (PhysicsCutHullRejection r in root.Cook.HullRejections)
                {
                    Assert.That(r.request == serial[0] && r.path == "driver" && r.fragment.Equals(body), Is.True, "both are the cut's, attributed to its source: " + r);
                    names.Add(r.meshName);
                }

                Assert.That(root.Cook.HullRejections[0].operation.value, Is.EqualTo(root.Cook.HullRejections[1].operation.value), "one operation");
                Assert.That(root.Cook.HullRejections[0].positive && !root.Cook.HullRejections[1].positive, Is.True, "the positive and the negative side of convex 0");
                Assert.That(names.Count, Is.EqualTo(2));
                Assert.That(audit.FailedMeshes.Count, Is.GreaterThanOrEqualTo(2), "PhysX logged both");
                foreach (string name in audit.FailedMeshes) Assert.That(names.Contains(name), Is.True, "every cooking error names one of the refused meshes: " + name);
                Assert.That(audit.Unaccounted(root.Cook.WasRejected, null), Is.Zero, "none is unaccounted");
                Assert.That(ShapelessColliders(), Is.Empty);
                Assert.That(MeshesOfCut(serial[0]), Is.Zero);
                yield return EndWorld(root);
            }
            finally
            {
                audit.End();
            }
        }

        /// <summary>
        /// **The pump's hull checks share the frame's budget and carry over.** The driver's cut is asked, then the cook's
        /// budget for its checks is taken as spent: each frame takes one check only (forced, so the cut is never held for
        /// ever), the rest wait for the next frame; the cut is published after its last check, with cooked shapes.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_ThePumpsChecks_ShareTheFramesBudget_AndCarryOver()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            int framesBefore = root.Cook.HullCheckFrames, forcedBefore = root.Cook.HullChecksForced, deferredBefore = root.Cook.HullChecksDeferred;
            root.Cook.MainRemaining = () => 0.0;   // this frame's budget, as far as the checks go, is spent
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            TestContext.Out.WriteLine(root.Cook.HullSummary());
            Assert.That(root.Cook.HullCheckFrames - framesBefore, Is.GreaterThanOrEqualTo(2), "the checks went over more than one frame");
            Assert.That(root.Cook.MaxFrameHullChecks, Is.EqualTo(1), "one check a frame with the budget spent");
            Assert.That(root.Cook.HullChecksForced - forcedBefore, Is.GreaterThanOrEqualTo(2), "each taken as the frame's one forced check");
            Assert.That(root.Cook.HullChecksDeferred - deferredBefore, Is.GreaterThanOrEqualTo(1), "the next one waited for the next frame");
            AssertCookedShapes(root, sides[0], "the positive side");
            AssertCookedShapes(root, sides[1], "the negative side");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A fused group's hull checks go under the common budget, over frames, and no Final comes before its checks.**
        /// Four fused pieces in a column are cut vertically (four member cooks, two meshes each) with a 0.02 ms budget:
        /// the checks are taken in the fusion's Step, a few a frame, over several frames; at every frame no more Finals
        /// are published than whole checks allow (two checks a member), and every published member has cooked shapes.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_AGroupsChecks_GoUnderTheCommonBudget_OverFrames_BeforeTheirFinals()
        {
            CutWorldRoot root = NewBudgetWorld(0.02);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            var bottom = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 1f, 0f, 0.5f), bottom);
            yield return UntilFused(root, 4, "four fused in a column");
            int checksBefore = root.Fusion.HullChecksInStep, publishedBefore = root.Fusion.FinalsPublished, readsBefore = root.Cook.HullChecks;
            Assert.That(root.TryAsk(SlashAsk(root, top[0], new float4(1f, 0f, 0f, 0f), 1, 12f)), Is.True);
            yield return Until(() => root.Fusion.GroupCuts == 1, "the group cut published");
            var perFrame = new List<int>();
            int last = root.Fusion.HullChecksInStep, sawChecking = 0;
            float deadline = Time.realtimeSinceStartup + 10f;
            while (root.Fusion.CutsInProgress > 0 && Time.realtimeSinceStartup < deadline)
            {
                if (root.Fusion.CutsChecking > 0) sawChecking++;
                yield return null;
                int checks = root.Fusion.HullChecksInStep;
                if (checks != last) { perFrame.Add(checks - last); last = checks; }
                int published = root.Fusion.FinalsPublished - publishedBefore;
                Assert.That(3 * published, Is.LessThanOrEqualTo(checks - checksBefore), "no Final before its two reads and its check's end (" + published + " published, " + (checks - checksBefore) + " units)");
            }

            WriteFusionRecord(root, "after the checks over frames: per frame [" + string.Join(",", perFrame) + "]");
            TestContext.Out.WriteLine(root.Cook.HullSummary() + "; fusion hull " + (root.Fusion.HullSeconds * 1000).ToString("F3") + " ms, max " + (root.Fusion.MaxHullSeconds * 1000).ToString("F3") + " ms");
            Assert.That(root.Fusion.FinalsPublished - publishedBefore, Is.EqualTo(4), "four Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(root.Cook.HullChecks - readsBefore, Is.EqualTo(8), "two mesh reads a member");
            Assert.That(root.Fusion.HullChecksInStep - checksBefore, Is.EqualTo(12), "taken in the Step as units: two reads and the cut's end a member");
            Assert.That(perFrame.Count, Is.GreaterThan(1), "over more than one frame");
            Assert.That(sawChecking, Is.GreaterThan(0), "a cut was seen waiting for its checks");
            Assert.That(root.Fusion.DeferredForBudget, Is.GreaterThan(0), "the budget deferred checks to later frames");
            Assert.That(root.Fusion.HullSeconds, Is.GreaterThan(0.0), "the checks are timed in the Step");
            foreach (FusedGroup g in root.Fusion.Groups) foreach (LogicalFragmentId f in g.Fragments) AssertCookedShapes(root, f, "member " + f.value);
            Assert.That(ShapelessColliders(), Is.Empty);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The world ending in the middle of a group's hull checks publishes nothing of those cuts and gives everything
        /// back.** With a 0.02 ms budget the checks of a four-member group cut go over frames; the world's ending begins
        /// while some are still being checked: those cuts end Abandoned, no further Final is published, their meshes are
        /// destroyed, the input holds are given back, and the cook is empty.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_TheEndingDuringTheChecks_PublishesNothingOfThem_AndGivesEverythingBack()
        {
            CutWorldRoot root = NewBudgetWorld(0.02);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var top = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(0f, 1f, 0f, -0.5f), top);
            var bottom = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 1f, 0f, 0.5f), bottom);
            yield return UntilFused(root, 4, "four fused in a column");
            var shapes = new List<PhysicsOwnerShape>();
            foreach (LogicalFragmentId f in root.Fusion.Groups[0].Fragments) if (root.Owners.TryGet(f, out PhysicsFragmentOwner o)) shapes.Add(o.Shape);
            var serials = new HashSet<int>();
            root.Cook.meshOverrideForTest = (request, m, mesh) => { serials.Add(request.Serial); return false; };   // observes the serials only
            PhysicsCutCook cook = root.Cook;
            BuildingFusion fusion = root.Fusion;
            Assert.That(root.TryAsk(SlashAsk(root, top[0], new float4(1f, 0f, 0f, 0f), 1, 12f)), Is.True);
            yield return UntilWithin(() => fusion.CutsChecking > 0, 5f, "a cut being checked");
            int publishedAtTheEnd = fusion.FinalsPublished, checking = fusion.CutsChecking;
            TestContext.Out.WriteLine("ending with " + checking + " cut(s) being checked, " + publishedAtTheEnd + " Finals published; " + cook.HullSummary());
            root.Shutdown();
            Assert.That(fusion.FinalsPublished, Is.EqualTo(publishedAtTheEnd), "no Final published after the ending began");
            yield return EndWorld(root);
            yield return null;
            Assert.That(fusion.FinalsPublished, Is.EqualTo(publishedAtTheEnd), "nothing of the checked cuts was published");
            Assert.That(cook.CutsChecking, Is.Zero);
            Assert.That(cook.IsDrained, Is.True, "the cook holds nothing");
            foreach (int serial in serials) Assert.That(MeshesOfCut(serial), Is.Zero, "the meshes of cut " + serial + " were destroyed");
            foreach (PhysicsOwnerShape shape in shapes) Assert.That(shape.WorkUsers, Is.Zero, "no work hold remains on a member's shape");
            Assert.That(GameObject.Find("Zantetsu cut hull probe"), Is.Null, "the probe is gone with the cook");
        }


        /// <summary>
        /// **The hull probe is made at the world's build, and the first real check pays only its own read.** A new world
        /// has prepared its probe on a known valid tetrahedron (it read ConvexMesh), the probe collider is disabled and holds
        /// no mesh; no check has run yet. After the first cut, the first check's seconds are recorded apart from the
        /// preparation's.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_TheProbeIsPreparedAtTheBuild_AndTheFirstCheckIsTimedApart()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            PhysicsCutCook cook = root.Cook;
            Assert.That(cook.ProbePreparedGeometry, Is.EqualTo("ConvexMesh"), "the probe read a known valid convex at the build");
            Assert.That(cook.ProbePreparationSeconds, Is.GreaterThan(0.0));
            Assert.That(double.IsNaN(cook.FirstHullCheckSeconds), Is.True, "no check has run yet");
            GameObject probe = null;
            foreach (MeshCollider c in Resources.FindObjectsOfTypeAll<MeshCollider>()) if (c != null && c.gameObject.name == "Zantetsu cut hull probe") probe = c.gameObject;
            Assert.That(probe, Is.Not.Null, "the probe exists after the build");
            MeshCollider probeCollider = probe.GetComponent<MeshCollider>();
            Assert.That(probeCollider.enabled, Is.False, "disabled after its preparation");
            Assert.That(probeCollider.sharedMesh, Is.Null, "and holding no mesh");
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, body, new float4(0f, 1f, 0f, 0f), sides);
            TestContext.Out.WriteLine("probe prepared in " + (cook.ProbePreparationSeconds * 1000).ToString("F3") + " ms, first check " + (cook.FirstHullCheckSeconds * 1000).ToString("F3") + " ms; " + cook.HullSummary());
            Assert.That(double.IsNaN(cook.FirstHullCheckSeconds), Is.False, "the first check was timed");
            Assert.That(probeCollider.enabled || probeCollider.sharedMesh != null, Is.False, "the probe is disabled and empty between checks");
            yield return EndWorld(root);
            yield return null;
            Assert.That(probe == null, Is.True, "the probe is gone with the world");
        }

        /// <summary>
        /// **A mesh whose bake PhysX refused, left unchecked by the world's ending, is accounted as abandoned before its
        /// check: never published, given back.** The cut's negative mesh is replaced by a refused convex; its bake logs
        /// the refusal; the checks are held, so the cut sits in its check when the world ends. The cut ends Abandoned with
        /// its unread meshes recorded as unchecked, nothing of it is published or left alive, and the reconciliation after
        /// the reclaim sorts every cooking error as abandoned unchecked -- none refused, pending or unresolved.
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_AMeshWithABakeErrorLeftUncheckedAtTheEnding_IsAbandonedUnpublishedAndReclaimed()
        {
            LogAssert.ignoreFailingMessages = true;
            var audit = new CookingErrorAudit();
            audit.Begin();
            try
            {
                CutWorldRoot root = NewWorld(out Shader _);
                PhysicsCutCook cook = root.Cook;
                LogicalFragmentId body = AddBody(root, Vector3.zero);
                yield return null;
                int target = -1;
                cook.meshOverrideForTest = (request, m, mesh) =>
                {
                    if (target < 0) target = request.Serial;
                    if (request.Serial != target || m != 1) return false;
                    MakeRefusedConvex(mesh);
                    return true;
                };
                cook.holdHullChecksForTest = true;
                Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
                yield return UntilWithin(() => cook.CutsChecking > 0, 5f, "the cut reached its check");
                yield return UntilWithin(() => audit.FailedMeshes.Count > 0, 5f, "PhysX logged the bake's refusal");
                Assert.That(cook.HullChecks, Is.Zero, "no mesh was read");
                CookingErrorAudit.Reconciliation before = audit.Reconcile(cook.WasRejected, cook.WasAbandonedUnchecked, cook.IsCheckPending);
                TestContext.Out.WriteLine("before the ending: " + before);
                Assert.That(before.pending, Is.GreaterThan(0), "the error is pending while the check waits");
                Assert.That(before.Resolved, Is.False, "and pending is not resolved");
                root.Shutdown();
                yield return EndWorld(root);
                yield return null;
                yield return null;
                CookingErrorAudit.Reconciliation after = audit.Reconcile(cook.WasRejected, cook.WasAbandonedUnchecked, cook.IsCheckPending);
                TestContext.Out.WriteLine("after the reclaim: " + after + "; " + cook.HullSummary());
                Assert.That(cook.AbandonedUncheckedCuts, Is.EqualTo(1), "the cut was abandoned before its check");
                Assert.That(cook.AbandonedUncheckedMeshes, Is.EqualTo(2), "both its meshes unread");
                Assert.That(cook.WasAbandonedUnchecked("Zantetsu Physics Cut " + target + ".1"), Is.True, "the refused mesh is among them");
                Assert.That(cook.HullRejectedCuts, Is.Zero, "nothing was refused by a check");
                Assert.That(after.abandonedUnchecked, Is.EqualTo(after.errors), "every cooking error is an abandoned-unchecked mesh's");
                Assert.That(after.Resolved, Is.True, "nothing pending or unresolved after the reclaim");
                Assert.That(MeshesOfCut(target), Is.Zero, "its meshes were destroyed");
                Assert.That(cook.CutsChecking, Is.Zero);
                Assert.That(ShapelessColliders(), Is.Empty, "no collider without a cooked shape was left");
            }
            finally
            {
                audit.End();
            }
        }

        /// <summary>
        /// **A group cut of thirty-four members is accepted and completed with the ledger's capacity (4096).** A building
        /// cut into 34 slabs rests and fuses into one group; a vertical cut crosses all 34: it is not refused for room, every
        /// member's Final is published with cooked shapes, the ledger held 34 incomplete operations at once, and nothing is
        /// left incomplete. (The small-room case -- FusionCut_ShortLedgerRoom -- keeps its own capacity of 2 and refuses the
        /// whole cut, changing nothing.)
        /// </summary>
        [UnityTest]
        public IEnumerator HullRefusal_AThirtyFourMemberGroupCut_IsAcceptedAndCompleted()
        {
            CutWorldRoot root = NewFusionWorld();
            Assert.That(root.Ledger.Budget.MaxIncompleteCutOperationCount, Is.EqualTo(4096), "the profile's default capacity");
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            LogicalFragmentId rest = building;
            const int slabs = 34;
            for (int k = 1; k < slabs; k++)
            {
                float y = -1f + k * (2f / slabs);
                var sides = new LogicalFragmentId[2];
                yield return CutInTwo(root, rest, new float4(0f, 1f, 0f, -y), sides);
                rest = UpperByRoot(root, sides);
            }

            yield return UntilWithin(() => root.Fusion.GroupCount == 1 && root.Fusion.Groups[0].MemberCount == slabs && root.Fusion.CutsInProgress == 0, 30f, "the 34 slabs rest as one group");
            FusedGroup group = root.Fusion.Groups[0];
            int refusedBefore = root.Fusion.GroupCutsRefused, publishedBefore = root.Fusion.FinalsPublished, splitBefore = root.Fusion.MembersSplit;
            Assert.That(root.TryAsk(SlashAsk(root, group.Representative, new float4(1f, 0f, 0f, 0f), 1, 0f)), Is.True, "a vertical cut through every slab");
            yield return null;
            yield return UntilWithin(() => root.Fusion.PreparationsInFlight == 0, 10f, "the preparation");
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the 34 Finals");
            WriteFusionRecord(root, "after the 34-member cut; ledger peak " + root.Ledger.Budget.PeakIncompleteCutOperationCount);
            Assert.That(root.Fusion.GroupCutsRefused, Is.EqualTo(refusedBefore), "not refused");
            Assert.That(root.Fusion.MembersSplit - splitBefore, Is.EqualTo(slabs), "all 34 crossed");
            Assert.That(root.Fusion.FinalsPublished - publishedBefore, Is.EqualTo(slabs), "and all 34 Finals published");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(root.Ledger.Budget.PeakIncompleteCutOperationCount, Is.GreaterThanOrEqualTo(slabs), "the ledger held the 34 at once");
            Assert.That(root.Ledger.Budget.IncompleteCutOperationCount, Is.Zero, "nothing is left incomplete");
            foreach (FusedGroup g in root.Fusion.Groups) foreach (LogicalFragmentId f in g.Fragments) AssertCookedShapes(root, f, "member " + f.value);
            Assert.That(ShapelessColliders(), Is.Empty);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The check's audit counts what PhysX logs and matches it to the cook's refusals.** The messages PhysX writes
        /// for a refused convex (the pair per cooking attempt) are read by mesh name: those naming a mesh the cook refused
        /// are accounted for, one naming another mesh is not, and a lone "less than four valid vertices" beyond the pairs
        /// is not.
        /// </summary>
        [Test]
        public void HullRefusal_TheCheckAudit_AccountsOnlyForTheCooksRefusals()
        {
            var audit = new CookingErrorAudit();
            const string cleanup = "[Physics.PhysX] ConvexHullLib::cleanupVertices: Less than four valid vertices were found. Provide at least four valid (e.g. each at a different position) vertices.";
            string Failed(string name) => "Failed to create Convex Mesh from source mesh. An internal unspecified error has occurred that could mean the PhysX's implementation of the Quickhull algorithm found the input mesh topologically challenging. Source mesh name: " + name;
            audit.Observe(cleanup, "", LogType.Error);
            audit.Observe(Failed("Zantetsu Physics Cut 7.0"), "", LogType.Error);
            audit.Observe(cleanup, "", LogType.Error);
            audit.Observe(Failed("Zantetsu Physics Cut 7.0"), "", LogType.Error);
            audit.Observe("an unrelated error", "", LogType.Error);
            var refused = new HashSet<string> { "Zantetsu Physics Cut 7.0" };
            var which = new List<string>();
            Assert.That(audit.Unaccounted(refused.Contains, which), Is.Zero, "the pairs of a refused mesh are accounted for");
            audit.Observe(cleanup, "", LogType.Error);
            audit.Observe(Failed("Zantetsu Physics Cut 9.1"), "", LogType.Error);
            Assert.That(audit.Unaccounted(refused.Contains, which), Is.EqualTo(1), "a mesh the cook did not refuse is not: " + string.Join(", ", which));
            which.Clear();
            refused.Add("Zantetsu Physics Cut 9.1");
            audit.Observe(cleanup, "", LogType.Error);
            Assert.That(audit.Unaccounted(refused.Contains, which), Is.EqualTo(1), "a lone 'less than four' beyond the pairs is not: " + string.Join(", ", which));
        }
    }
}
