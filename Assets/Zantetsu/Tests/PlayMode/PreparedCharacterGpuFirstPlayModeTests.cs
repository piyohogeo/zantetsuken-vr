using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The GPU-first route of a prepared character's cut (DESIGN 4.5.15, SKIN-GPU-FIRST-2), through the product's own hit,
    /// acceptance and update: the hit is accepted, published and shown in its own frame exactly as the CPU route's is -- the
    /// pose drawn from the GPU bake, the children clipped from it -- and only the geometry of the cuts below waits, in the cut
    /// DAG, for the CPU bake the world runs. The bake is held back deliberately in most cases here (VpSkinBakePending.HoldForTest),
    /// so that what goes on without it can be seen: the Operation, the physics publication, the clip display, a cut of a published
    /// child, the character's withdrawal and its handle's end; then released, so that the geometry completes and commits.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        private SkinnedMeshRenderer RendererOf(GameObject root) => root.GetComponentInChildren<SkinnedMeshRenderer>(true);

        private bool _holdBake;

        // The world with the GPU-first route, or the case is inconclusive where this device cannot make the route (no compute).
        private IEnumerator GpuFirstWorld(Action<CutWorldProfile> room = null, bool holdBake = true)
        {
            VpSkinBakeSetup.Mode = VpSkinBakeMode.Gpu;
            _holdBake = holdBake;
            VpSkinBakePending.HoldForTest = () => _holdBake;
            yield return ReadyCharacter(room ?? SmallRoom);
            if (coldWorld.SkinBake == null || coldWorld.SkinBake.Bake == null)
            {
                Assert.Ignore("the GPU-first route cannot be made here: " + coldWorld.SkinBakeReason);
            }

            Assert.That(coldWorld.SkinBakePending, Is.Not.Null, "the world owns the deferred bakes");
        }

        [TearDown]
        public void GpuFirstTearDown()
        {
            VpSkinBakeSetup.Mode = VpSkinBakeMode.Cpu;
            VpSkinBakePending.HoldForTest = null;
            VpSkinBakeSnapshot.FailKernelForTest = false;
            VpSkinBakeSnapshot.InvalidateOutputForTest = false;
            VpSkinBakeMarks.Mark = null;
            VpSkinBakeWarm.ThrowAfterDispatchForTest = null;
            LogicalCutLedger.PublishedForDiagnosis = null;
            CutDag.CommittedForDiagnosis = null;
            VpSkinBakePending.CollectedForDiagnosis = null;
        }

        // The display's state in words, for a failure's message: whether it stopped, and why.
        private string DisplayState()
        {
            VpLogicalCutDisplay d = coldWorld.Display;
            return "display halted " + d.IsHalted + " (reason " + d.HaltReason + ", invalid input " + d.HaltInvalidInput + ", room failure " + (d.LastRoomFailure ?? "none") + "); gpu regions " + d.GpuRegionCount
                + ", lost " + d.GpuRegionsLost + ", retired " + d.GpuRegionsRetired + "; storage pending " + coldWorld.Storage.DeferredPendingCount
                + "; pending " + (coldWorld.SkinBakePending != null ? coldWorld.SkinBakePending.Describe() : "none") + "; dag pending bases " + coldWorld.Geometry.PendingBasisCount + "; stats " + VpSkinBakeStats.Describe();
        }

        private void AssertNotHalted(string when)
        {
            Assert.That(coldWorld.Display.IsHalted, Is.False, "the display stopped " + when + ": " + DisplayState());
        }

        // The lowest skinned z of a storage geometry (the pose a GPU-first hit was baked at), read from the storage's vertices.
        private float LowestZOf(VpStoredGeometry geometry)
        {
            Assert.That(coldWorld.Storage.TryGetCommittedVertices(geometry.vertexStart, geometry.vertexCount, out NativeArray<VpRenderVertex> skinned), Is.True, "the geometry's vertices are in the storage");
            float lowest = float.PositiveInfinity;
            for (int v = 0; v < skinned.Length; v++) lowest = Mathf.Min(lowest, skinned[v].position.z);
            return lowest;
        }

        private IEnumerator UntilCommittedOrExplain(CutOperationId operation, int frames = 600)
        {
            for (int i = 0; i < frames && coldWorld.Geometry.StageOf(operation) != CutGeometryStage.Committed; i++) yield return null;
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.Committed), "the cut " + operation + " committed; " + DisplayState());
        }

        private IEnumerator UntilPublished(CutOperationId operation, int frames = 300)
        {
            for (int i = 0; i < frames && !IsPublished(operation); i++) yield return null;
            Assert.That(IsPublished(operation), Is.True, "the operation " + operation + " was published (its final physics); " + DisplayState());
        }

        private bool IsPublished(CutOperationId operation)
        {
            return coldWorld.Ledger.TryGetOperation(operation, out LogicalCutOperation record)
                && (record.state == LogicalCutOperationState.Published || record.state == LogicalCutOperationState.Completed);
        }

        private (LogicalFragmentId positive, LogicalFragmentId negative) ChildrenOf(CutOperationId operation)
        {
            Assert.That(coldWorld.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True, "the operation is the ledger's");
            return (record.positive, record.negative);
        }

        // A hit of the character at the bone's shape (z 5..6), under the GPU-first route: accepted at once, with its Operation.
        private List<SlashHitConfirmed> GpuFirstHit(SlashHitDetector detector, VpPreparedCharacterCut handle, int level = 1, float near = 2f, float far = 8f)
        {
            var hits = Evaluate(detector, CharacterLevel(level, 0.3f, near, far), level);
            Assert.That(hits.Count, Is.EqualTo(1), "one character under the sweep");
            Assert.That(handle.DeferredFallbackReason, Is.Null, "the GPU-first route was taken (not: " + handle.DeferredFallbackReason + "); " + DisplayState());
            Assert.That(hits[0].Operation.IsSet, Is.True, "accepted at the hit, as the CPU route's hit is: " + hits[0].Acceptance + "; " + DisplayState());
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published).Or.EqualTo(ProvisionalCutAcceptance.Pending), "the ordinary acceptance");
            return hits;
        }

        /// <summary>
        /// **The hit is accepted, published and shown in its own frame; only the geometry waits for the CPU bake.** With the
        /// bake held: the Operation is set at the hit, the renderer stops drawing, the display draws the GPU region, the
        /// provisional physics (the pair) is published in the hit's own frame and the display clips the source by the pending
        /// sides before the ledger publishes; the ledger then publishes the two children and the display clips them from the
        /// parent's body, the cut's geometry stays in
        /// WaitingForBasis, no kernel reads the pending vertices, the character is withdrawn and its handle ended as any
        /// character's. Released: the bake completes the storage, the DAG registers the base geometry, the cut commits, the
        /// geometry is the hit's pose (z 5) though the bone moved since, the snapshot went back.
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_TheHitIsAcceptedAndPublishedAtOnce_OnlyTheGeometryWaitsForTheBake()
        {
            yield return GpuFirstWorld();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            Assert.That(handle.TryPrepareSharedSkinInput(out string refusal), Is.True, refusal);
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            SkinnedMeshRenderer renderer = RendererOf(root);
            Assert.That(renderer.enabled, Is.True, "L:renderer.enabled before");
            int begun = VpSkinBakeStats.Begun, ready = VpSkinBakeStats.Ready;

            bone.localPosition = new Vector3(0, 0, 5);
            int hitFrame = Time.frameCount;
            var hits = GpuFirstHit(detector, handle);
            CutOperationId operation = hits[0].Operation;
            LogicalFragmentId source = hits[0].Fragment;
            VpStoredGeometry reserved = handle.GpuFirstGeometry;
            long requestId = handle.GpuFirstRequestId;
            Assert.That(requestId, Is.Not.Zero, "the hit's bake has a request id");
            Assert.That(VpSkinBakeStats.Begun - begun, Is.EqualTo(1));
            Assert.That(coldWorld.SkinBakePending.Count, Is.EqualTo(1), "one bake begun (held): " + coldWorld.SkinBakePending.Describe());
            Assert.That(coldWorld.SkinBakePending.HeldCount, Is.EqualTo(1), "held out of the dispatcher by the test");
            Assert.That(coldWorld.Display.GpuRegionCount, Is.EqualTo(1), "one region is the GPU bake's");
            Assert.That(coldWorld.Display.GpuRegionDispatches, Is.GreaterThanOrEqualTo(1));
            Assert.That(coldWorld.Storage.DeferredPendingCount, Is.EqualTo(1));
            Assert.That(coldWorld.Storage.IsDeferredPending(reserved), Is.True, "the reserved geometry is pending");
            Assert.That(coldWorld.Geometry.IsBasisPending(source), Is.True, "the DAG waits for the base geometry");
            Assert.That(coldWorld.Geometry.TryGetGeometry(source, out _), Is.False, "no cut input while the CPU geometry is pending");
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.WaitingForBasis), "the cut's geometry waits; nothing is offered to a kernel");
            Assert.That(coldWorld.Owners.TryGet(source, out _), Is.True, "the source's physics owner is registered at the hit");
            Assert.That(handle.IsHeld, Is.False, "L:IsHeld");

            // Review 4 (P2-1): the provisional physics -- the pair in the scene -- is published in the hit's own frame (the transaction's
            // frame; the ledger's publication, which issues the children, is later), and the display clips the source by the pending
            // sides before the ledger publishes. Both are read directly, with the bake held.
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published), "one body, a fresh world, the whole budget: the pair is published at the hit");
            ProvisionalCutTransaction transaction = coldWorld.Driver.TransactionOf(operation);
            Assert.That(transaction, Is.Not.Null, "the driver holds the cut's transaction");
            Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "the pair is in the scene");
            Assert.That(transaction.PublishedFrame, Is.EqualTo(hitFrame), "the provisional physics was published in the hit's frame");
            Assert.That(transaction.Pair, Is.Not.Null, "the published pair");
            Assert.That(coldWorld.Owners.TryGetProvisional(operation, out _), Is.True, "the registry holds the provisional pair");
            Assert.That(coldWorld.Ledger.TryGetOperation(operation, out LogicalCutOperation pendingRecord), Is.True);
            Assert.That(pendingRecord.state, Is.Not.EqualTo(LogicalCutOperationState.Published), "the ledger has not published the operation yet (" + pendingRecord.state + "): its publication is a later frame's");
            int clipSeenFrame = coldWorld.Display.StateOf(source) == LogicalCutDisplayState.ProvisionalSplit ? Time.frameCount : -1;
            for (int i = 0; i < 3 && clipSeenFrame < 0; i++)
            {
                yield return null;
                if (coldWorld.Display.StateOf(source) == LogicalCutDisplayState.ProvisionalSplit) clipSeenFrame = Time.frameCount;
            }
            Assert.That(clipSeenFrame, Is.GreaterThanOrEqualTo(0), "the source is drawn split by the pending sides while the bake is held: " + DisplayState());
            Assert.That(clipSeenFrame - hitFrame, Is.LessThanOrEqualTo(1), "the clip is seen at the latest the frame after the hit (a collection later in the hit's frame is seen the frame after); seen +" + (clipSeenFrame - hitFrame));
            // Whether the ledger had published by then is read, not asserted: in the Editor both are seen at +1 (the hit frame's later
            // steps are seen the frame after); the Players see the ledger's publication at +2 and the clip no later than it.
            bool ledgerPublishedWhenClipSeen = IsPublished(operation);
            Debug.Log("GpuFirst T1: provisional physics at +0 (transaction frame " + transaction.PublishedFrame + "), clip seen at +" + (clipSeenFrame - hitFrame) + ", ledger published by then: " + ledgerPublishedWhenClipSeen);

            // The ledger publishes the children, the display clips them from the parent's body, the character leaves; all while the bake is held.
            yield return UntilPublished(operation);
            int publishedFrame = Time.frameCount;
            Assert.That(clipSeenFrame, Is.LessThanOrEqualTo(publishedFrame), "the clip display was seen no later than the ledger's publication");
            (LogicalFragmentId positive, LogicalFragmentId negative) = ChildrenOf(operation);
            Assert.That(positive.IsSet && negative.IsSet, Is.True, "two children published");
            Assert.That(coldWorld.Owners.TryGet(positive, out _) || coldWorld.Owners.TryGet(negative, out _), Is.True, "the children's provisional physics is published");
            for (int i = 0; i < 10 && coldWorld.Display.StateOf(positive) != LogicalCutDisplayState.ProvisionalSplit && coldWorld.Display.StateOf(negative) != LogicalCutDisplayState.ProvisionalSplit; i++) yield return null;
            Assert.That(coldWorld.Display.StateOf(positive) == LogicalCutDisplayState.ProvisionalSplit || coldWorld.Display.StateOf(negative) == LogicalCutDisplayState.ProvisionalSplit, Is.True,
                "the children are drawn clipped from the parent's GPU region (" + coldWorld.Display.StateOf(positive) + " / " + coldWorld.Display.StateOf(negative) + "); " + DisplayState());
            Assert.That(renderer.enabled && root.activeInHierarchy, Is.False, "the renderer no longer draws (withdrawn, or its drawing stopped): the display draws the pose and the children");
            Assert.That(root.activeInHierarchy, Is.False, "withdrawn at the publication, as any character");
            Assert.That(handle.IsDisposed, Is.True, "the handle ended with its cut, as any handle");
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.WaitingForBasis), "still waiting for its basis, nothing else: " + DisplayState());
            Assert.That(coldWorld.SkinBakePending.Count, Is.EqualTo(1), "the bake lives on after the handle ended: " + coldWorld.SkinBakePending.Describe());
            Assert.That(coldWorld.Storage.IsDeferredPending(reserved), Is.True, "still pending");
            AssertNotHalted("while the bake was held");

            // PLANE-1: the pieces move while the bake is held -- the provisional physics is live, not waiting for the geometry.
            Rigidbody watched = null;
            if (coldWorld.Owners.TryGet(positive, out PhysicsFragmentOwner watchedOwner) && watchedOwner.Body != null) watched = watchedOwner.Body;
            else if (coldWorld.Owners.TryGet(negative, out watchedOwner) && watchedOwner.Body != null) watched = watchedOwner.Body;
            Assert.That(watched, Is.Not.Null, "a child's body to watch while the bake is held");
            Vector3 watchedStart = watched.position; Quaternion watchedStartRotation = watched.rotation;
            // The project steps physics by script (CutPhysicsStep); this cold world has no stepping component, so the test steps it.
            for (int i = 0; i < 10; i++) { if (Physics.simulationMode == SimulationMode.Script) Physics.Simulate(1f / 72f); yield return null; }
            Assert.That(coldWorld.SkinBakePending.HeldCount, Is.EqualTo(1), "still held while the pieces moved");
            float watchedMoved = Vector3.Distance(watchedStart, watched.position), watchedTurned = Quaternion.Angle(watchedStartRotation, watched.rotation);
            Debug.Log("GpuFirst T1: while the bake was held the child's body moved " + watchedMoved.ToString("F4") + " m and turned " + watchedTurned.ToString("F2") + " deg over 10 frames (physics " + Physics.simulationMode + ", stepped by the test at 1/72 s where Script)");
            Assert.That(watchedMoved > 1e-4f || watchedTurned > 0.01f, Is.True, "the provisional pieces move while the CPU bake is held (moved " + watchedMoved + " m, turned " + watchedTurned + " deg)");
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.WaitingForBasis), "and the geometry still waits");

            // Released: the bake runs, the storage completes, the base geometry is registered, the cut commits.
            bone.localPosition = new Vector3(0, 0, 9);   // the bone moved on (it is withdrawn anyway): the geometry keeps the hit's pose
            _holdBake = false;
            yield return UntilCommittedOrExplain(operation);
            Assert.That(VpSkinBakeStats.Ready - ready, Is.EqualTo(1), "one bake collected: " + VpSkinBakeStats.Describe());
            Assert.That(coldWorld.SkinBakePending.Completed, Is.EqualTo(1), coldWorld.SkinBakePending.Describe());
            Assert.That(coldWorld.SkinBakePending.Count, Is.Zero);
            Assert.That(coldWorld.Geometry.IsBasisPending(source), Is.False);
            Assert.That(coldWorld.Storage.IsDeferredPending(reserved), Is.False, "the reserved geometry was completed");
            Assert.That(reserved.cutInputAccepted, Is.True, "the completed geometry is a cut input");
            Assert.That(LowestZOf(reserved), Is.GreaterThanOrEqualTo(4.99f), "skinned at the hit's pose (z 5), not the bone's later place (z 9)");
            Assert.That(coldWorld.Display.GpuRegionCount, Is.Zero, "the region is the storage's again, or retired with the parent's body");
            Assert.That(coldWorld.Storage.DeferredPendingCount, Is.Zero);
            VpSkinBakeStats.Record record = VpSkinBakeStats.Records.Last(r => r.requestId == requestId);
            Assert.That(record.hitFrame, Is.EqualTo(hitFrame));
            Assert.That(record.readyFrame, Is.GreaterThanOrEqualTo(publishedFrame), "the bake was collected after the publication (it was held until then)");
            Assert.That(record.hitMainSeconds, Is.GreaterThan(0));
            Assert.That(record.basisOutcome, Is.EqualTo("ready"));
            for (int i = 0; i < 30 && coldWorld.SkinBake.Rented > 0; i++) yield return null;
            Assert.That(coldWorld.SkinBake.Rented, Is.Zero, "the snapshot went back");
            AssertNotHalted("at the commit");
        }

        /// <summary>
        /// **The provisional publication is reported to the diagnosis hook once, with the transaction's frame, and the report
        /// outlives the transaction** (MOTION-CAPTURE-1 review 3, 2026-11-02). A probe that observes once a frame misses a
        /// publication whose transaction is published, handed over and removed between two observations; the hook's report is
        /// what it reads then. The hook is diagnosis only: null in the product, and nothing of the cut depends on it.
        /// </summary>
        [UnityTest]
        public IEnumerator PublishedForDiagnosis_ReportsThePublicationFrameOnce_AndTheReportOutlivesTheTransaction()
        {
            yield return GpuFirstWorld(holdBake: false);
            var reports = new List<(CutOperationId operation, int frame)>();
            ProvisionalCutDriver.PublishedForDiagnosis = (operation, frame) => reports.Add((operation, frame));
            try
            {
                var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
                yield return null;
                Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
                Assert.That(handle.TryPrepareSharedSkinInput(out string refusal), Is.True, refusal);
                coldWorld.Driver.RemainingMainSeconds = () => 1;
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(handle);
                bone.localPosition = new Vector3(0, 0, 5);
                int hitFrame = Time.frameCount;
                var hits = GpuFirstHit(detector, handle);
                CutOperationId operation = hits[0].Operation;
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published), "one body, a fresh world, the whole budget: published at the hit");

                // Reported once, at the publication, with the frame the transaction itself records.
                Assert.That(reports.Count(r => r.operation.Equals(operation)), Is.EqualTo(1), "the publication is reported once");
                int reported = reports.First(r => r.operation.Equals(operation)).frame;
                ProvisionalCutTransaction transaction = coldWorld.Driver.TransactionOf(operation);
                Assert.That(transaction, Is.Not.Null, "the transaction is live right after the hit");
                Assert.That(reported, Is.EqualTo(transaction.PublishedFrame), "the report carries the transaction's publication frame");
                Assert.That(reported, Is.EqualTo(hitFrame), "which is the hit's frame here");

                // Through the commit: the handover removes the transaction; the report is still there, unchanged.
                yield return UntilCommittedOrExplain(operation);
                for (int i = 0; i < 10 && coldWorld.Driver.TransactionOf(operation) != null; i++) yield return null;
                Assert.That(coldWorld.Driver.TransactionOf(operation), Is.Null, "the transaction is removed after the final handover: a probe observing now sees no PublishedFrame");
                Assert.That(reports.Count(r => r.operation.Equals(operation)), Is.EqualTo(1), "still reported exactly once");
                Assert.That(reports.First(r => r.operation.Equals(operation)).frame, Is.EqualTo(hitFrame), "the report outlives the transaction with the same frame");
                AssertNotHalted("at the commit");
            }
            finally
            {
                ProvisionalCutDriver.PublishedForDiagnosis = null;
            }
        }

        /// <summary>
        /// **The ledger's publication and the DAG's commit are each reported once, with the operation (SKIN-GPU-FIRST-EVAL-1).** The
        /// publication report comes with the children issued; the commit report with the geometry stage Committed; neither repeats.
        /// </summary>
        [UnityTest]
        public IEnumerator LedgerAndDagHooksForDiagnosis_ReportThePublicationAndTheCommitOnce_WithTheOperation()
        {
            yield return GpuFirstWorld(holdBake: false);
            // The order of the two hooks is read by a sequence number: in the Editor the ledger's publication and the geometry commit can land in the same frame.
            int sequence = 0;
            var published = new List<(CutOperationId operation, int frame, bool childrenIssued, int at)>();
            var committed = new List<(CutOperationId operation, int frame, CutGeometryStage stage, int at)>();
            LogicalCutLedger.PublishedForDiagnosis = o => published.Add((o, Time.frameCount, ChildrenOf(o).positive.IsSet && ChildrenOf(o).negative.IsSet, sequence++));
            CutDag.CommittedForDiagnosis = o => committed.Add((o, Time.frameCount, coldWorld.Geometry.StageOf(o), sequence++));
            try
            {
                var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
                yield return null;
                Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
                Assert.That(handle.TryPrepareSharedSkinInput(out string refusal), Is.True, refusal);
                coldWorld.Driver.RemainingMainSeconds = () => 1;
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(handle);
                bone.localPosition = new Vector3(0, 0, 5);
                int hitFrame = Time.frameCount;
                var hits = GpuFirstHit(detector, handle);
                CutOperationId operation = hits[0].Operation;

                yield return UntilPublished(operation);
                Assert.That(published.Count(r => r.operation.Equals(operation)), Is.EqualTo(1), "the ledger's publication is reported once");
                (CutOperationId _, int publishedFrame, bool childrenIssued, int publishedAt) = published.First(r => r.operation.Equals(operation));
                Assert.That(childrenIssued, Is.True, "reported after the children were issued (a subscriber can read them)");
                Assert.That(publishedFrame, Is.GreaterThanOrEqualTo(hitFrame), "at or after the hit");

                yield return UntilCommittedOrExplain(operation);
                Assert.That(committed.Count(r => r.operation.Equals(operation)), Is.EqualTo(1), "the commit is reported once");
                Assert.That(committed.First(r => r.operation.Equals(operation)).stage, Is.EqualTo(CutGeometryStage.Committed), "reported with the stage already Committed");
                Assert.That(committed.First(r => r.operation.Equals(operation)).at, Is.GreaterThan(publishedAt), "the commit's report comes after the publication's (the same frame is allowed)");
                Assert.That(committed.First(r => r.operation.Equals(operation)).frame, Is.GreaterThanOrEqualTo(publishedFrame), "the commit is not before the publication");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.That(published.Count(r => r.operation.Equals(operation)), Is.EqualTo(1), "still once");
                Assert.That(committed.Count(r => r.operation.Equals(operation)), Is.EqualTo(1), "still once");
                AssertNotHalted("at the commit");
            }
            finally
            {
                LogicalCutLedger.PublishedForDiagnosis = null;
                CutDag.CommittedForDiagnosis = null;
            }
        }

        /// <summary>**A completed deferred bake is reported once as Completed, with the hit's request id (the CPU Ready).**</summary>
        [UnityTest]
        public IEnumerator CollectedForDiagnosis_ReportsACompletedBakeOnce_WithTheRequestId()
        {
            yield return GpuFirstWorld(holdBake: false);
            var collected = new List<(long requestId, VpSkinBakeCollectOutcome outcome, int frame)>();
            VpSkinBakePending.CollectedForDiagnosis = (id, outcome) => collected.Add((id, outcome, Time.frameCount));
            try
            {
                var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
                yield return null;
                Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
                Assert.That(handle.TryPrepareSharedSkinInput(out string refusal), Is.True, refusal);
                coldWorld.Driver.RemainingMainSeconds = () => 1;
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(handle);
                bone.localPosition = new Vector3(0, 0, 5);
                var hits = GpuFirstHit(detector, handle);
                CutOperationId operation = hits[0].Operation;
                long requestId = handle.GpuFirstRequestId;
                Assert.That(requestId, Is.Not.Zero, "the hit's bake has a request id");
                Assert.That(collected.Count(c => c.requestId == requestId), Is.EqualTo(0), "nothing reported at the hit (the bake is pending)");

                yield return UntilCommittedOrExplain(operation);
                Assert.That(collected.Count(c => c.requestId == requestId), Is.EqualTo(1), "the collect is reported once: " + string.Join(" | ", collected.Select(c => c.requestId + " " + c.outcome)));
                Assert.That(collected.First(c => c.requestId == requestId).outcome, Is.EqualTo(VpSkinBakeCollectOutcome.Completed), "as Completed (the basis published)");
                VpSkinBakeStats.Record record = VpSkinBakeStats.Records.First(r => r.requestId == requestId);
                Assert.That(record.basisOutcome, Is.EqualTo("ready"), "the stats agree");
                Assert.That(collected.First(c => c.requestId == requestId).frame, Is.EqualTo(record.readyFrame), "reported in the frame the stats call the ready frame");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.That(collected.Count(c => c.requestId == requestId), Is.EqualTo(1), "still once");
                AssertNotHalted("at the commit");
            }
            finally { VpSkinBakePending.CollectedForDiagnosis = null; }
        }

        /// <summary>**A failed deferred bake is reported once as Failed, never as Completed: the record keeps the CPU Ready apart from a failure.**</summary>
        [UnityTest]
        public IEnumerator CollectedForDiagnosis_ReportsAFailedBakeAsFailed_NeverAsCompleted()
        {
            yield return GpuFirstWorld(holdBake: false);
            var collected = new List<(long requestId, VpSkinBakeCollectOutcome outcome)>();
            VpSkinBakePending.CollectedForDiagnosis = (id, outcome) => collected.Add((id, outcome));
            try
            {
                var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
                yield return null;
                Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
                coldWorld.Driver.RemainingMainSeconds = () => 1;
                var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
                detector.AddCharacter(handle);
                VpSkinBakeSnapshot.FailKernelForTest = true;
                LogTestFailureExpected();
                coldTerminationExpected = true;   // the geometry failure is the common termination, as in GpuFirst_AFailedBake
                bone.localPosition = new Vector3(0, 0, 5);
                var hits = GpuFirstHit(detector, handle);
                long requestId = handle.GpuFirstRequestId;
                Assert.That(requestId, Is.Not.Zero, "the hit's bake has a request id");

                for (int i = 0; i < 300 && coldWorld.SkinBakePending.Failed == 0; i++) yield return null;
                Assert.That(coldWorld.SkinBakePending.Failed, Is.EqualTo(1), "the bake failed: " + coldWorld.SkinBakePending.Describe());
                Assert.That(collected.Count(c => c.requestId == requestId), Is.EqualTo(1), "reported once: " + string.Join(" | ", collected.Select(c => c.requestId + " " + c.outcome)));
                Assert.That(collected.First(c => c.requestId == requestId).outcome, Is.EqualTo(VpSkinBakeCollectOutcome.Failed), "as Failed");
                Assert.That(collected.Any(c => c.requestId == requestId && c.outcome == VpSkinBakeCollectOutcome.Completed), Is.False, "never as Completed");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.That(collected.Count(c => c.requestId == requestId), Is.EqualTo(1), "still once");
            }
            finally
            {
                VpSkinBakePending.CollectedForDiagnosis = null;
                VpSkinBakeSnapshot.FailKernelForTest = false;
            }
        }

        // ---- SKIN-GPU-FIRST-STARTUP-1: the start-up warm of the GPU bake (VpSkinBakeWarm) and its lifetime rules.
        private IEnumerator WarmModel(System.Action<VpPreparedCharacterCut, VpSkinModel> got)
        {
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            Assert.That(handle.TryPrepareSharedSkinInput(out string refusal), Is.True, refusal);
            VpSkinModel model = handle.SkinModelForWarm;
            Assert.That(model, Is.Not.Null, "the prepared character's skin model is readable for the warm");
            got(handle, model);
        }

        /// <summary>**The warm completes by the readback and retires everything it held: no snapshot of the pool, the model's hold released.**</summary>
        [UnityTest]
        public IEnumerator BakeWarm_CompletesByTheReadback_AndRetiresItsResources()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpSkinModel model = null;
            yield return WarmModel((h, m) => model = m);
            int holds = model.HoldCount; long rents = coldWorld.SkinBake.Rents; int pending = VpSkinBakeWarm.Pending;
            var warm = new VpSkinBakeWarm(coldWorld.SkinBake, model);
            yield return warm.Run();
            Assert.That(warm.Completed, Is.True, string.Join(" | ", warm.Report));
            for (int i = 0; i < 60 && !warm.Retired; i++) yield return null;
            Assert.That(warm.Retired, Is.True, "the readback's callback retired the palette, the output and the hold");
            Assert.That(model.HoldCount, Is.EqualTo(holds), "the model's hold was released with the retirement");
            Assert.That(coldWorld.SkinBake.Rents, Is.EqualTo(rents), "no snapshot of the pool was rented by the warm");
            Assert.That(VpSkinBakeWarm.Pending, Is.EqualTo(pending), "no warm left pending");
            Assert.That(warm.OutputBytes, Is.EqualTo((long)model.VertexCount * 16));
            AssertNotHalted("after the bake warm");
        }

        /// <summary>**Abandoned before the GPU is done: finished once as a failure, the resources held until the readback, then retired.**</summary>
        [UnityTest]
        public IEnumerator BakeWarm_AbandonedBeforeCompletion_FailsOnce_AndRetiresWhenTheReadbackReturns()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpSkinModel model = null;
            yield return WarmModel((h, m) => model = m);
            int holds = model.HoldCount;
            var warm = new VpSkinBakeWarm(coldWorld.SkinBake, model);
            IEnumerator run = warm.Run();
            Assert.That(run.MoveNext(), Is.True, "issued, waiting for the readback");
            Assert.That(model.HoldCount, Is.EqualTo(holds + 1), "the model is held while the GPU may read its input");
            Assert.That(warm.Retired, Is.False, "nothing is freed before the readback");
            warm.Abandon("the test");
            Assert.That(warm.Finished, Is.True); Assert.That(warm.Completed, Is.False);
            Assert.That(warm.Failure, Does.StartWith("abandoned"));
            Assert.That(run.MoveNext(), Is.False, "the coroutine ends at once after the abandon");
            warm.Abandon("again");
            Assert.That(warm.Failure, Does.Contain("the test"), "finished once: a later abandon changes nothing");
            for (int i = 0; i < 120 && !warm.Retired; i++) yield return null;
            Assert.That(warm.Retired, Is.True, "the readback retired the resources after the abandon");
            Assert.That(model.HoldCount, Is.EqualTo(holds));
            AssertNotHalted("after the abandoned bake warm");
        }

        /// <summary>**No resources, no model: a failure without an exception, nothing held.**</summary>
        [UnityTest]
        public IEnumerator BakeWarm_WithoutResourcesOrModel_FailsWithoutThrowing()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpSkinModel model = null;
            yield return WarmModel((h, m) => model = m);
            int pending = VpSkinBakeWarm.Pending;
            var noResources = new VpSkinBakeWarm(null, model);
            yield return noResources.Run();
            Assert.That(noResources.Finished && !noResources.Completed, Is.True);
            Assert.That(noResources.Failure, Does.Contain("no skin bake resources"));
            Assert.That(noResources.Retired, Is.True);
            var noModel = new VpSkinBakeWarm(coldWorld.SkinBake, null);
            yield return noModel.Run();
            Assert.That(noModel.Failure, Does.Contain("no skin model"));
            Assert.That(noModel.Retired, Is.True);
            Assert.That(VpSkinBakeWarm.Pending, Is.EqualTo(pending));
            var twice = new VpSkinBakeWarm(coldWorld.SkinBake, model);
            yield return twice.Run();
            Assert.That(twice.Completed, Is.True, string.Join(" | ", twice.Report));
            IEnumerator again = twice.Run();
            Assert.That(again.MoveNext(), Is.False, "a second Run ends at once");
            Assert.That(twice.Completed, Is.True, "and does not undo the completion");
            for (int i = 0; i < 60 && !twice.Retired; i++) yield return null;
            Assert.That(twice.Retired, Is.True);
        }

        /// <summary>**The character ends while the GPU is pending: the model lives on under the warm's hold until the readback, then goes.**</summary>
        [UnityTest]
        public IEnumerator BakeWarm_TheOwnerEndsWhileTheGpuIsPending_TheModelLivesUntilTheReadback()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpPreparedCharacterCut handle = null; VpSkinModel model = null;
            yield return WarmModel((h, m) => { handle = h; model = m; });
            var warm = new VpSkinBakeWarm(coldWorld.SkinBake, model);
            IEnumerator run = warm.Run();
            Assert.That(run.MoveNext(), Is.True, "issued, waiting for the readback");
            handle.Dispose();   // the owner ends: the model's owner is done, but the warm's hold stands
            Assert.That(model.IsDisposed, Is.False, "the model is not freed while the warm holds it for the GPU");
            while (run.MoveNext()) yield return run.Current;
            Assert.That(warm.Finished, Is.True, string.Join(" | ", warm.Report));
            for (int i = 0; i < 60 && !warm.Retired; i++) yield return null;
            Assert.That(warm.Retired, Is.True);
            Assert.That(model.IsDisposed, Is.True, "freed once the warm's hold went and the owner was done");
            AssertNotHalted("after the owner ended under a pending warm");
        }

        /// <summary>**A diagnosis hook that throws changes nothing: the dispatch stays issued, the warm completes and retires.**</summary>
        [UnityTest]
        public IEnumerator BakeWarm_ADiagnosisHookThatThrows_NeverReachesTheWarm_AndItCompletes()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpSkinModel model = null;
            yield return WarmModel((h, m) => model = m);
            int holds = model.HoldCount; int pending = VpSkinBakeWarm.Pending; int notes = 0;
            VpSkinBakeMarks.Mark = (name, began, ended) => { notes++; throw new InvalidOperationException("a diagnosis that throws"); };
            try
            {
                var warm = new VpSkinBakeWarm(coldWorld.SkinBake, model);
                yield return warm.Run();
                Assert.That(notes, Is.GreaterThan(0), "the hook was called (and threw)");
                Assert.That(warm.Completed, Is.True, "the hook's exception never reached the warm: " + string.Join(" | ", warm.Report));
                for (int i = 0; i < 60 && !warm.Retired; i++) yield return null;
                Assert.That(warm.Retired, Is.True);
                Assert.That(model.HoldCount, Is.EqualTo(holds));
                Assert.That(VpSkinBakeWarm.Pending, Is.EqualTo(pending));
            }
            finally { VpSkinBakeMarks.Mark = null; }
        }

        /// <summary>
        /// **An exception after the dispatch was recorded: a failure once, and the resources KEPT (never freed under the dispatch).**
        /// The clean-up retires them by a readback of the dispatched output (a GPU completion), requested in the finally so that a failing
        /// assert still hands the recovery to the completion callback -- never by a frame count, never unconditionally.
        /// </summary>
        [UnityTest]
        public IEnumerator BakeWarm_AnExceptionAfterTheDispatch_FailsOnce_AndKeepsTheResources()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpSkinModel model = null;
            yield return WarmModel((h, m) => model = m);
            int holds = model.HoldCount; int pending = VpSkinBakeWarm.Pending;
            VpSkinBakeWarm.ThrowAfterDispatchForTest = () => "injected after the dispatch";
            var warm = new VpSkinBakeWarm(coldWorld.SkinBake, model);
            bool recovery = false;
            try
            {
                yield return warm.Run();
                Assert.That(warm.Finished && !warm.Completed, Is.True, "finished as a failure");
                Assert.That(warm.Failure, Does.Contain("injected after the dispatch"));
                Assert.That(warm.Failure, Does.Contain("kept, not freed"), "the issued-without-readback path keeps the resources: " + warm.Failure);
                Assert.That(warm.Retired, Is.False, "nothing freed under a dispatch that may run");
                Assert.That(model.HoldCount, Is.EqualTo(holds + 1), "the model stays held");
                Assert.That(VpSkinBakeWarm.Pending, Is.EqualTo(pending + 1), "counted as pending");
                for (int i = 0; i < 10; i++) yield return null;
                Assert.That(warm.Retired, Is.False, "and no retirement comes by itself (no readback was requested)");
            }
            finally
            {
                VpSkinBakeWarm.ThrowAfterDispatchForTest = null;
                recovery = warm.RetireByReadbackForTest();   // the retirement comes with the GPU's completion, whatever the asserts above did
            }
            Assert.That(recovery, Is.True, "the kept resources were handed to a completion readback");
            Assert.That(warm.Retired, Is.False, "not retired before the readback returned");
            for (int i = 0; i < 60 && !warm.Retired; i++) yield return null;
            Assert.That(warm.Retired, Is.True, "retired by the readback's callback");
            Assert.That(model.HoldCount, Is.EqualTo(holds));
            Assert.That(VpSkinBakeWarm.Pending, Is.EqualTo(pending));
        }

        /// <summary>**The resources are disposed while the GPU is pending: the warm's own buffers and hold still retire by the readback, without an exception.**</summary>
        [UnityTest]
        public IEnumerator BakeWarm_TheResourcesAreDisposedWhileTheGpuIsPending_TheRetirementStillComes()
        {
            yield return GpuFirstWorld(holdBake: false);
            VpSkinModel model = null;
            yield return WarmModel((h, m) => model = m);
            int holds = model.HoldCount;
            var warm = new VpSkinBakeWarm(coldWorld.SkinBake, model);
            IEnumerator run = warm.Run();
            Assert.That(run.MoveNext(), Is.True, "issued, waiting for the readback");
            coldWorld.SkinBake.Dispose();   // the world's resources end under the pending warm (the world ending does this)
            while (run.MoveNext()) yield return run.Current;
            Assert.That(warm.Finished, Is.True, string.Join(" | ", warm.Report));
            for (int i = 0; i < 60 && !warm.Retired; i++) yield return null;
            Assert.That(warm.Retired, Is.True, "the readback retired the warm's private buffers and hold; the pool's disposal did not touch them");
            Assert.That(model.HoldCount, Is.EqualTo(holds));
        }

        /// <summary>
        /// **A published child is cut again while the basis is pending, and only its geometry waits.** The child's cut is
        /// accepted and published through the driver as any cut of a cut fragment; in the DAG it waits for its parent's commit,
        /// which waits for the basis. Released: the parent commits first, then the child; nothing is read from the pending
        /// vertices before.
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_APublishedChildIsCutWhileTheBasisIsPending_AndWaitsOnlyForGeometry()
        {
            yield return GpuFirstWorld();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            bone.localPosition = new Vector3(0, 0, 5);
            var hits = GpuFirstHit(detector, handle);
            CutOperationId parent = hits[0].Operation;
            yield return UntilPublished(parent);
            (LogicalFragmentId positive, LogicalFragmentId negative) = ChildrenOf(parent);
            yield return null;

            // The second cut: a plane through the children in the lineage's frame (x = 0, the rig stands at the origin).
            var ask = new ProvisionalCutAsk { source = positive, plane = new float4(1f, 0f, 0f, 0f), renderAnchor = new float3(0f, 0f, 5.5f) };
            ProvisionalCutAcceptance childAcceptance = coldWorld.Driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission);
            Assert.That(childAcceptance, Is.EqualTo(ProvisionalCutAcceptance.Published).Or.EqualTo(ProvisionalCutAcceptance.Pending),
                "the child's cut is accepted the ordinary way (admission " + admission + "); " + DisplayState());
            CutOperationId child = transaction.Operation;
            Assert.That(child.IsSet, Is.True, "L:child operation");
            Assert.That(coldWorld.Geometry.StageOf(child), Is.EqualTo(CutGeometryStage.WaitingForBasis), "the child's geometry waits for its parent's commit");
            Assert.That(coldWorld.Geometry.StageOf(parent), Is.EqualTo(CutGeometryStage.WaitingForBasis), "the parent's waits for the basis");
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(coldWorld.Geometry.StageOf(parent), Is.EqualTo(CutGeometryStage.WaitingForBasis), "still: the bake is held");
            Assert.That(coldWorld.Geometry.StageOf(child), Is.EqualTo(CutGeometryStage.WaitingForBasis), "still: its parent has no geometry");
            AssertNotHalted("with a child cut waiting on a pending basis");

            _holdBake = false;
            int parentCommitFrame = -1, childCommitFrame = -1;
            for (int i = 0; i < 600 && (parentCommitFrame < 0 || childCommitFrame < 0); i++)
            {
                if (parentCommitFrame < 0 && coldWorld.Geometry.StageOf(parent) == CutGeometryStage.Committed) parentCommitFrame = Time.frameCount;
                if (childCommitFrame < 0 && coldWorld.Geometry.StageOf(child) == CutGeometryStage.Committed) childCommitFrame = Time.frameCount;
                if (childCommitFrame >= 0 && parentCommitFrame < 0) Assert.Fail("the child committed before its parent: " + DisplayState());
                yield return null;
            }

            Assert.That(parentCommitFrame, Is.GreaterThanOrEqualTo(0), "the parent committed; " + DisplayState());
            Assert.That(childCommitFrame, Is.GreaterThanOrEqualTo(parentCommitFrame), "the child committed after (or with) its parent; " + DisplayState());
            Assert.That(coldWorld.Geometry.FailureOf(child), Is.EqualTo(VpStorageCutStatus.Ok));
            AssertNotHalted("after both commits");
        }

        /// <summary>
        /// **A vertex buffer replaced while the parent's region is the GPU's and its children are shown provisionally gets the
        /// region baked again.** The GPU copy holds one rig; a second GPU-first hit grows it: every live geometry is transferred
        /// again and the pending region is dispatched again from its snapshot, before anything is drawn; both cuts then commit.
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_AReplacedVertexBufferWhileChildrenAreProvisional_BakesThePendingRegionAgain()
        {
            yield return GpuFirstWorld(p =>
            {
                ColdSet(p, "vertexReserve", 4096); ColdSet(p, "vertexInitialCommit", 4096);
                ColdSet(p, "indexReserve", 16384); ColdSet(p, "indexInitialCommit", 16384);
                ColdSet(p, "gpuVertexInitialCapacity", 4); ColdSet(p, "gpuIndexInitialCapacity", 16384);
            });
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            var second = PrepareBonedCharacter(out Transform bone2, out GameObject root2);
            root2.transform.position = new Vector3(0, 0, 10);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "the first handle finished its preparation");
            Assert.That(second.TryFinishPreparation(), Is.True, "the second handle finished its preparation");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            detector.AddCharacter(second);
            bone.localPosition = new Vector3(0, 0, 5);
            bone2.localPosition = new Vector3(0, 0, 5);
            var hits = GpuFirstHit(detector, handle);
            CutOperationId first = hits[0].Operation;
            VpStoredGeometry reservedFirst = handle.GpuFirstGeometry;
            yield return UntilPublished(first);
            yield return null;
            int rebakes = coldWorld.Display.GpuRegionRebakes;
            Assert.That(coldWorld.Display.GpuRegionCount, Is.EqualTo(1), "the first region is the GPU's while its bake is held");

            // The second character's hit: its GPU-first show needs room past the GPU copy's 4 vertices.
            var hits2 = GpuFirstHit(detector, second, 2, 12f, 20f);
            CutOperationId secondOperation = hits2[0].Operation;
            Assert.That(coldWorld.Display.GpuVertexCapacity, Is.GreaterThan(4), "the GPU copy grew past its 4 vertices");
            Assert.That(coldWorld.Display.GpuRegionRebakes - rebakes, Is.EqualTo(1), "the pending region was baked again into the replaced buffer");
            Assert.That(coldWorld.Display.GpuRegionsLost, Is.Zero, "L:GpuRegionsLost");
            Assert.That(coldWorld.Display.GpuRegionCount, Is.EqualTo(2));
            yield return null;
            AssertNotHalted("after the growth");

            _holdBake = false;
            yield return UntilCommittedOrExplain(first);
            yield return UntilCommittedOrExplain(secondOperation);
            Assert.That(LowestZOf(reservedFirst), Is.GreaterThanOrEqualTo(4.99f), "the first hit's geometry, baked again into the replaced buffer and completed, is the hit's pose");
            Assert.That(coldWorld.Display.GpuRegionCount, Is.Zero, DisplayState());
            AssertNotHalted("after both commits");
        }

        /// <summary>
        /// **Every descendant ended before the bake came back: the bake adopts nothing and gives everything back.** The children
        /// are retired (the pieces' ordinary end) while the bake is held; released, the bake finds no reader: dropped, the
        /// snapshot returned, no region, nothing pending, no halt. The handle had ended long before.
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_AllDescendantsEndedBeforeTheBake_TheBakeAdoptsNothing()
        {
            yield return GpuFirstWorld();
            int liveBefore = coldWorld.Display.LiveGeometryCount;
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            bone.localPosition = new Vector3(0, 0, 5);
            var hits = GpuFirstHit(detector, handle);
            CutOperationId operation = hits[0].Operation;
            LogicalFragmentId source = hits[0].Fragment;
            VpStoredGeometry reserved = handle.GpuFirstGeometry;
            yield return UntilPublished(operation);
            (LogicalFragmentId positive, LogicalFragmentId negative) = ChildrenOf(operation);
            yield return null;

            // The pieces end (as the lifetime or a collection ends them): owners retired, fragments retired, forgotten by the DAG.
            foreach (LogicalFragmentId piece in new[] { positive, negative })
            {
                coldWorld.Owners.Retire(piece);
                coldWorld.Ledger.Retire(piece);
                coldWorld.Geometry.Forget(piece);
            }

            for (int i = 0; i < 60 && (coldWorld.Display.LiveGeometryCount > liveBefore || coldWorld.Geometry.StageOf(operation) == CutGeometryStage.WaitingForBasis); i++) yield return null;
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.Reclaimed), "the cut ended without a commit: nothing reads it; " + DisplayState());
            Assert.That(coldWorld.Display.LiveGeometryCount, Is.EqualTo(liveBefore), "the parent's body left with its pieces: " + DisplayState());
            Assert.That(coldWorld.Display.GpuRegionCount, Is.Zero, "its region left with it: " + DisplayState());
            AssertNotHalted("after the pieces ended while the bake was held");

            _holdBake = false;
            for (int i = 0; i < 300 && coldWorld.SkinBakePending.Count > 0; i++) yield return null;
            Assert.That(coldWorld.SkinBakePending.Count, Is.Zero, coldWorld.SkinBakePending.Describe());
            Assert.That(coldWorld.SkinBakePending.Dropped, Is.EqualTo(1), "the bake adopted nothing: " + coldWorld.SkinBakePending.Describe());
            Assert.That(coldWorld.SkinBakePending.Completed, Is.Zero);
            Assert.That(coldWorld.Geometry.IsBasisPending(source), Is.False, "nothing pending in the DAG");
            Assert.That(coldWorld.Storage.DeferredPendingCount, Is.Zero, "nothing pending in the storage");
            Assert.That(coldWorld.Storage.IsDeferredPending(reserved), Is.False);
            for (int i = 0; i < 30 && coldWorld.SkinBake.Rented > 0; i++) yield return null;
            Assert.That(coldWorld.SkinBake.Rented, Is.Zero, "the snapshot went back");
            VpSkinBakeStats.Record record = VpSkinBakeStats.Records.Last(r => r.route == "gpu");
            Assert.That(record.basisOutcome, Does.StartWith("dropped"));
            AssertNotHalted("after the dropped bake");
        }

        /// <summary>
        /// **The world ends while a bake is held and another runs: everything begun is given back, nothing is adopted.**
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_TheWorldEndsWhileBakesArePending_EverythingGoesBack()
        {
            yield return GpuFirstWorld();
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            bone.localPosition = new Vector3(0, 0, 5);
            GpuFirstHit(detector, handle);
            VpSkinBakePending pending = coldWorld.SkinBakePending;
            VpSkinBakeResources resources = coldWorld.SkinBake;
            Assert.That(pending.Count, Is.EqualTo(1));
            yield return null;

            Assert.That(coldWorld.Shutdown(), Is.True.Or.False, "the ending begins");
            for (int i = 0; i < 600 && coldWorld.IsEnding; i++) yield return null;
            Assert.That(coldWorld.IsEnding, Is.False, "the world ended");
            Assert.That(pending.Count, Is.Zero, "nothing begun is left: " + pending.Describe());
            Assert.That(pending.Completed, Is.Zero, "nothing was adopted: " + pending.Describe());
            Assert.That(pending.Withdrawn + pending.Abandoned, Is.EqualTo(1), pending.Describe());
            Assert.That(resources.IsDisposed, Is.True, "the resources went with the world");
            Assert.That(resources.Rented, Is.Zero, "every snapshot was returned before: " + resources.Describe());
        }

        /// <summary>
        /// **A failed bake fails the cuts that waited on it through the geometry failure, and nothing else.** The physics is
        /// published and stays; the character is not drawn again by its renderer, nothing is re-accepted; the DAG fails the
        /// waiting cut once and the world takes it to the common termination (as any geometry failure); the region keeps
        /// what it draws and its snapshot until the display lets it go.
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_AFailedBake_FailsTheWaitingGeometryOnce_AndTouchesNoPhysics()
        {
            yield return GpuFirstWorld(holdBake: false);
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            SkinnedMeshRenderer renderer = RendererOf(root);
            VpSkinBakeSnapshot.FailKernelForTest = true;
            LogTestFailureExpected();
            coldTerminationExpected = true;   // the geometry failure is the common termination: the cleanup leaves the world as it left it
            bone.localPosition = new Vector3(0, 0, 5);
            var hits = GpuFirstHit(detector, handle);
            CutOperationId operation = hits[0].Operation;
            LogicalFragmentId source = hits[0].Fragment;
            int begun = VpSkinBakeStats.Begun;

            for (int i = 0; i < 300 && coldWorld.SkinBakePending.Failed == 0; i++) yield return null;
            Assert.That(coldWorld.SkinBakePending.Failed, Is.EqualTo(1), "the bake failed: " + coldWorld.SkinBakePending.Describe());
            for (int i = 0; i < 60 && coldWorld.Geometry.StageOf(operation) == CutGeometryStage.WaitingForBasis; i++) yield return null;
            Assert.That(coldWorld.Geometry.StageOf(operation), Is.EqualTo(CutGeometryStage.Reclaimed), "the waiting cut failed (ended without a commit): " + DisplayState());
            Assert.That(coldWorld.Geometry.FailureOf(operation), Is.Not.EqualTo(VpStorageCutStatus.Ok), "recorded as a geometry failure");
            Assert.That(coldWorld.TerminationRequested, Is.True, "the geometry failure is the common termination, as any geometry failure");
            Assert.That(coldWorld.Owners.TryGet(source, out _) || IsPublished(operation), Is.True, "the physics side is left as it was");
            Assert.That(renderer.enabled && root.activeInHierarchy, Is.False, "the renderer does not draw the character again (no return to the SMR)");
            Assert.That(VpSkinBakeStats.Begun, Is.EqualTo(begun), "nothing was re-accepted");
            VpSkinBakeStats.Record record = VpSkinBakeStats.Records.Last(r => r.route == "gpu");
            Assert.That(record.basisOutcome, Does.StartWith("failed"));
            VpSkinBakeSnapshot.FailKernelForTest = false;
        }

        private static void LogTestFailureExpected()
        {
            // The pool reports the kernel's exception once, and the world its termination; the test asked for them.
            LogAssert.ignoreFailingMessages = true;
        }

        /// <summary>
        /// **No free snapshot sends the hit down the synchronous route, with the reason; both hits are accepted at once.** A
        /// world of one snapshot: of two characters under one Slash, one takes the GPU-first route and the other the CPU route
        /// (its fallback reason naming the snapshot); both cuts commit.
        /// </summary>
        [UnityTest]
        public IEnumerator GpuFirst_WithoutAFreeSnapshot_TheHitTakesTheCpuRoute_WithTheReason()
        {
            yield return GpuFirstWorld(p => { SmallRoom(p); ColdSet(p, "skinBakeSnapshotCapacity", 1); }, holdBake: false);
            Assert.That(coldWorld.SkinBake.Capacity, Is.EqualTo(1));
            var first = PrepareBonedCharacter(out Transform bone1, out GameObject root1);
            var second = PrepareBonedCharacter(out Transform bone2, out GameObject root2);
            root2.transform.position = new Vector3(0, 0, 3);
            yield return null;
            Assert.That(first.TryFinishPreparation(), Is.True, "L:first.TryFinishPreparation()");
            Assert.That(second.TryFinishPreparation(), Is.True, "L:second.TryFinishPreparation()");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(first);
            detector.AddCharacter(second);
            bone1.localPosition = new Vector3(0, 0, 5);
            bone2.localPosition = new Vector3(0, 0, 5);   // under root2 (z 3): the shape at z 8..9
            int fallbacks = VpSkinBakeStats.Fallbacks, begun = VpSkinBakeStats.Begun;
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 12f), 1);
            Assert.That(hits.Count, Is.EqualTo(2), "both characters were hit by the one Slash");
            Assert.That(hits[0].Operation.IsSet && hits[1].Operation.IsSet, Is.True, "both accepted at the hit");
            int gpuCount = (first.DeferredFallbackReason == null ? 1 : 0) + (second.DeferredFallbackReason == null ? 1 : 0);
            Assert.That(gpuCount, Is.EqualTo(1), "one took the GPU-first route");
            Assert.That(VpSkinBakeStats.Begun - begun, Is.EqualTo(1));
            VpPreparedCharacterCut synchronous = first.DeferredFallbackReason != null ? first : second;
            Assert.That(synchronous.DeferredFallbackReason, Does.Contain("snapshot"));
            Assert.That(VpSkinBakeStats.Fallbacks - fallbacks, Is.EqualTo(1));
            yield return UntilCommittedOrExplain(hits[0].Operation);
            yield return UntilCommittedOrExplain(hits[1].Operation);
            AssertNotHalted("after both commits");
        }

        /// <summary>
        /// **The float route skins the pose synchronously by the GPU route's arithmetic.** No deferral, no GPU region: the
        /// hit is accepted at once as the CPU route's is, the storage holds the float kernel's geometry, and the cut commits.
        /// </summary>
        [UnityTest]
        public IEnumerator FloatRoute_SkinsThePoseSynchronously_AndTheCutIsAcceptedAtOnce()
        {
            VpSkinBakeSetup.Mode = VpSkinBakeMode.Float;
            yield return ReadyCharacter(SmallRoom);
            Assert.That(coldWorld.SkinBake, Is.Not.Null, coldWorld.SkinBakeReason);
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True, "L:TryFinishPreparation");
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            int floats = VpSkinBakeStats.FloatSyncs;
            bone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published).Or.EqualTo(ProvisionalCutAcceptance.Pending));
            Assert.That(handle.Operation.IsSet, Is.True, "L:Operation.IsSet");
            Assert.That(VpSkinBakeStats.FloatSyncs - floats, Is.EqualTo(1));
            Assert.That(coldWorld.Display.GpuRegionCount, Is.Zero, "L:GpuRegionCount");
            Assert.That(coldWorld.Storage.DeferredPendingCount, Is.Zero, "L:DeferredPendingCount");
            Assert.That(coldWorld.Storage.DeferredCompleted, Is.GreaterThanOrEqualTo(1), "reserved and completed at once");
            Assert.That(LowestSkinnedZ(handle), Is.GreaterThanOrEqualTo(4.99f));
            VpSkinBakeStats.Record record = VpSkinBakeStats.Records.Last();
            Assert.That(record.route, Is.EqualTo("float"));
            yield return UntilCommitted(handle.Operation);
        }
    }
}
