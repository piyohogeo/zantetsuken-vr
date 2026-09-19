using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;
using S = Zantetsu.MeshCut.Tests.VpMultiCutSnapshotTests;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The multi-cut snapshot run through the existing stencil classifiers on the CPU: visibility per cap, compatibility
    /// and colours per render fragment. Built on the snapshot tests' real-ledger fixture. The display no longer prepares
    /// its cameras with this classification (it uses <see cref="VpCapJobClassification"/>, D-183), so nothing here is
    /// compared with a display. Eyes are real cameras' matrices; which caps they see is read off the layouts.
    /// </summary>
    public class VpMultiCutStencilClassificationTests
    {
        private const int Warmup = 10;
        private const int Iterations = 100;

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private Camera NewCamera(Vector3 position, Vector3 lookAt)
        {
            Camera camera = new GameObject("Classification Eye").AddComponent<Camera>();
            _objects.Add(camera.gameObject);
            camera.enabled = false;
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.transform.position = position;
            camera.transform.rotation = Quaternion.LookRotation(lookAt - position, Mathf.Abs(Vector3.Dot((lookAt - position).normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);
            return camera;
        }

        private VpCapEye Eye(Vector3 position, Vector3 lookAt) => EyeOf(NewCamera(position, lookAt));

        private static VpCapEye EyeOf(Camera camera) => new VpCapEye(camera.transform.position, camera.projectionMatrix * camera.worldToCameraMatrix);

        private static VpMultiCutStencilClassification NewClassification() =>
            new VpMultiCutStencilClassification(new VpMultiCutCapacities(64, 1024, 64, 512, 64));

        // ----- several cuts -------------------------------------------------------------------------------------------------

        private static (VpMultiCutSnapshot snapshot, LogicalFragmentId cPlus) FourCuts()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.8f, 0f) });
            var (_, aPlus, _) = S.Cut(ledger, root, new float4(0f, 1f, 0f, -0.5f));
            var (_, bPlus, _) = S.Cut(ledger, aPlus, new float4(1f, 0f, 0f, 0f));
            var (_, cPlus, cMinus) = S.Cut(ledger, bPlus, new float4(0f, 0f, 1f, -0.3f));
            S.Cut(ledger, cMinus, new float4(1f, 1f, 0f, -0.8f));
            VpMultiCutSnapshot snapshot = S.NewSnapshot();
            Assert.That(S.Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            return (snapshot, cPlus);
        }

        /// <summary>
        /// Four cuts: every render fragment's compatibility target gets every condition the snapshot holds for it, from
        /// a view that sees its caps and from one that sees none alike; no two leaves are compatible.
        /// </summary>
        [Test]
        public void EveryCondition_GoesToCompatibility_SeenOrNot()
        {
            var (snapshot, _) = FourCuts();
            VpMultiCutStencilClassification classification = NewClassification();
            foreach (VpCapEye eye in new[] { Eye(new Vector3(-3f, -3f, -3f), Vector3.zero), Eye(new Vector3(0f, 0f, 20f), new Vector3(0f, 0f, 40f)) })
            {
                Assert.That(classification.TryClassify(snapshot, eye, eye, VpStencilTestSettings.Create(4)), Is.True);
                Assert.That(classification.TargetCount, Is.EqualTo(5));
                Assert.That(classification.GroupCount, Is.EqualTo(5), "no two leaves share every condition");
                for (int r = 0; r < snapshot.RenderFragmentCount; r++)
                {
                    snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                    classification.TryGetRenderFragment(r, out VpMultiCutStencilRenderFragment result);
                    Assert.That(result.conditionCount, Is.EqualTo(rf.conditionCount), "render fragment " + r + ": every condition, seen or not");
                }
            }
        }

        /// <summary>
        /// C+ has three caps -- A (facing down), B (facing -x), C (facing -z). Seen from above-left-front, only B's cap
        /// faces the eye: C+ keeps one volume, one cap is drawn, and its caps are not complete.
        /// </summary>
        [Test]
        public void SeenAndUnseenCapsOfOneRenderFragment_GiveOneVolume_AndOnlyTheSeenCaps()
        {
            var (snapshot, cPlus) = FourCuts();
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye eye = Eye(new Vector3(-3f, 3f, 3f), new Vector3(0f, 0.7f, 0f));
            Assert.That(classification.TryClassify(snapshot, eye, eye, VpStencilTestSettings.Create(4)), Is.True);

            VpMultiCutRenderFragment rf = S.RenderFragmentOf(snapshot, cPlus);
            int index = -1;
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment candidate);
                index = candidate.capStart == rf.capStart ? r : index;
            }

            classification.TryGetRenderFragment(index, out VpMultiCutStencilRenderFragment result);
            Assert.That(rf.capCount, Is.EqualTo(3), "the layout: three caps");
            Assert.That(result.visibleCaps, Is.EqualTo(1), "only B's cap faces the eye");
            Assert.That(result.volumeIssued, Is.True, "one volume for the render fragment");
            Assert.That(result.capsComplete, Is.False, "two non-empty caps left out");
            int issued = 0;
            for (int c = 0; c < rf.capCount; c++)
            {
                classification.TryGetCap(rf.capStart + c, out VpMultiCutStencilCap cap);
                issued += cap.issued ? 1 : 0;
                Assert.That(cap.issued, Is.EqualTo(cap.visible), "a cap is drawn exactly when seen");
            }

            Assert.That(issued, Is.EqualTo(1));
            int volumes = 0;
            for (int r = 0; r < classification.RenderFragmentCount; r++)
            {
                classification.TryGetRenderFragment(r, out VpMultiCutStencilRenderFragment each);
                volumes += each.volumeIssued ? 1 : 0;
            }

            Assert.That(classification.VolumeTargetCount, Is.EqualTo(volumes), "volumes are counted once per render fragment");
        }

        /// <summary>
        /// Looking away from everything, every group is left out: no volume, no cap. A cap cut to nothing is marked empty,
        /// is never seen or drawn, and does not make its render fragment incomplete. One eye facing a cap is enough to keep
        /// it; both eyes behind it drop it.
        /// </summary>
        [Test]
        public void NothingSeen_AnEmptyCap_AndOneEye()
        {
            var (snapshot, _) = FourCuts();
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye away = Eye(new Vector3(0f, 0f, 20f), new Vector3(0f, 0f, 40f));
            Assert.That(classification.TryClassify(snapshot, away, away, VpStencilTestSettings.Create(4)), Is.True);
            Assert.That(classification.CulledGroupCount, Is.EqualTo(classification.GroupCount), "every group left out");
            Assert.That(classification.VolumeTargetCount, Is.Zero);
            Assert.That(classification.IssuedCapCount, Is.Zero);

            // A (y = 0) and B (y = -0.5) on A+: B+'s cap of B is cut to nothing, its cap of A is a full square facing down.
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, aPlus, _) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (b, bPlus, _) = S.Cut(ledger, aPlus, new float4(0f, 1f, 0f, 0.5f));
            VpMultiCutSnapshot cutAway = S.NewSnapshot();
            Assert.That(S.Build(cutAway, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutRenderFragment rf = S.RenderFragmentOf(cutAway, bPlus);
            int rfIndex = IndexOf(cutAway, rf);

            VpCapEye below = Eye(new Vector3(0.2f, -4f, 0.1f), Vector3.zero);
            VpCapEye above = Eye(new Vector3(0.2f, 4f, 0.1f), Vector3.zero);
            Assert.That(classification.TryClassify(cutAway, below, below, VpStencilTestSettings.Create(4)), Is.True);
            classification.TryGetRenderFragment(rfIndex, out VpMultiCutStencilRenderFragment fromBelow);
            Assert.That(fromBelow.nonEmptyCaps, Is.EqualTo(1), "B's cap is empty");
            Assert.That(fromBelow.visibleCaps, Is.EqualTo(1));
            Assert.That(fromBelow.capsComplete, Is.True, "an empty cap is not an omission");
            for (int c = 0; c < rf.capCount; c++)
            {
                cutAway.TryGetCap(rf.capStart + c, out VpMultiCutCap cap);
                classification.TryGetCap(rf.capStart + c, out VpMultiCutStencilCap result);
                Assert.That(result.empty, Is.EqualTo(cap.vertexCount == 0));
                if (cap.boundary.face.operation == b)
                {
                    Assert.That(result.empty && !result.visible && !result.issued, Is.True, "empty: never seen or drawn");
                }
            }

            Assert.That(classification.TryClassify(cutAway, below, above, VpStencilTestSettings.Create(4)), Is.True);
            classification.TryGetRenderFragment(rfIndex, out VpMultiCutStencilRenderFragment oneEye);
            Assert.That(oneEye.visibleCaps, Is.EqualTo(1), "one eye facing the cap keeps it");
            Assert.That(classification.TryClassify(cutAway, above, above, VpStencilTestSettings.Create(4)), Is.True);
            classification.TryGetRenderFragment(rfIndex, out VpMultiCutStencilRenderFragment neither);
            Assert.That(neither.visibleCaps, Is.Zero, "both eyes behind it drop it");
            Assert.That(neither.capsComplete, Is.False, "a non-empty cap left out");
        }

        private static int IndexOf(VpMultiCutSnapshot snapshot, in VpMultiCutRenderFragment rf)
        {
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment candidate);
                if (candidate.capStart == rf.capStart && candidate.branchStart == rf.branchStart)
                {
                    return r;
                }
            }

            return -1;
        }

        // ----- beyond six vertices ------------------------------------------------------------------------------------------

        /// <summary>
        /// A cap of seven vertices: the hexagon of the cube and x + y + z = 0, one corner cut off by x - y &lt;= 1.2. It is
        /// judged whole -- seen from where it faces -- and the projection test reads caps of seven and eight vertices,
        /// finding two such caps apart where before it could only say "may overlap".
        /// </summary>
        [Test]
        public void CapsOfMoreThanSixVertices_AreJudgedWhole()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var (_, p, _) = S.Cut(ledger, root, new float4(1f, 1f, 1f, 0f));
            var (_, _, bMinus) = S.Cut(ledger, p, new float4(1f, -1f, 0f, -1.2f));
            VpMultiCutSnapshot snapshot = S.NewSnapshot();
            Assert.That(S.Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutRenderFragment rf = S.RenderFragmentOf(snapshot, bMinus);
            int seven = -1;
            for (int c = 0; c < rf.capCount; c++)
            {
                snapshot.TryGetCap(rf.capStart + c, out VpMultiCutCap cap);
                seven = cap.vertexCount == 7 ? rf.capStart + c : seven;
            }

            Assert.That(seven, Is.GreaterThanOrEqualTo(0), "the layout: a seven-vertex cap");
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye facing = Eye(new Vector3(-3f, -3f, -3f), Vector3.zero);
            Assert.That(classification.TryClassify(snapshot, facing, facing, VpStencilTestSettings.Create(4)), Is.True);
            classification.TryGetCap(seven, out VpMultiCutStencilCap sevenResult);
            Assert.That(sevenResult.visible && sevenResult.issued, Is.True, "the seven-vertex cap is seen and drawn");

            // Two incompatible targets whose boxes meet and whose seven- and eight-vertex caps are apart on screen.
            VpCapEye eye = Eye(Vector3.zero, new Vector3(0f, 0f, 1f));
            var a = Target(1, new Vector3(-1.5f, -1f, 5f), new Vector3(0.5f, 1f, 7f), Ring(new Vector3(-1.2f, 0f, 6f), 0.25f, 7));
            var b2 = Target(2, new Vector3(-0.5f, -1f, 5f), new Vector3(1.5f, 1f, 7f), Ring(new Vector3(1.2f, 0f, 6f), 0.25f, 8));
            VpCapProjectionVerdict verdict = VpCapProjectionConflict.Judge(a, b2, eye, eye, Vector2.zero, 1e-4f, 1e-4f);
            Assert.That(verdict.left, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps), "caps of seven and eight vertices are read whole");
        }

        private static readonly LogicalCutLedger k_scope = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));

        private static VpCapProjectionTarget Target(int operation, Vector3 min, Vector3 max, Vector3[] cap)
        {
            var box = new Bounds();
            box.SetMinMax(min, max);
            var conditions = new VpCapCompatibilityTarget(
                new[] { new VpCapConstraint(new VpCapFace(k_scope, new CutOperationId(operation)), 1f, new Vector4(0f, 0f, 1f, -6f)) }, Vector3.zero);
            return new VpCapProjectionTarget(conditions, box, Matrix4x4.identity, new[] { cap }, true);
        }

        private static Vector3[] Ring(Vector3 centre, float radius, int count)
        {
            var ring = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                float angle = 2f * Mathf.PI * i / count;
                ring[i] = centre + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * radius;
            }

            return ring;
        }

        // ----- beyond eight planes -----------------------------------------------------------------------------------------

        /// <summary>
        /// Nine cuts: the classification has exactly the snapshot's render fragments and caps -- the aggregated one once,
        /// its eight caps, and no cap of the Ignored ninth, which the snapshot never made and this does not make either.
        /// </summary>
        [Test]
        public void NineCuts_AreClassifiedAsTheSnapshotHoldsThem()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            for (int k = 0; k < 8; k++)
            {
                at = S.Cut(ledger, at, new float4(0f, 1f, 0f, 0.8f - (0.2f * k))).positive;
            }

            S.Cut(ledger, at, new float4(1f, 0f, 0f, -0.1f));
            VpMultiCutSnapshot snapshot = S.NewSnapshot();
            Assert.That(S.Build(snapshot, ledger, root), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye eye = Eye(new Vector3(0.3f, 4f, -3f), Vector3.zero);
            Assert.That(classification.TryClassify(snapshot, eye, eye, VpStencilTestSettings.Create(4)), Is.True);
            Assert.That(classification.RenderFragmentCount, Is.EqualTo(snapshot.RenderFragmentCount), "no render fragment added or doubled");
            Assert.That(classification.CapCount, Is.EqualTo(snapshot.CapCount), "no cap added");
            Assert.That(classification.TargetCount, Is.EqualTo(9), "eight negatives and the aggregated one");
            Assert.That(classification.IssuedCapCount, Is.LessThanOrEqualTo(snapshot.CapCount));
        }

        // ----- repeated eyes, input and allocation ------------------------------------------------------------------------

        /// <summary>
        /// Eyes A, B, A in turn: the second A is the first A exactly, the snapshot is unchanged, and no look at it is held
        /// afterwards. A snapshot that is not built, or more than this was made for, leaves nothing prepared.
        /// </summary>
        [Test]
        public void AlternatingEyes_LeaveNothingBehind_AndTheSnapshotIsOnlyRead()
        {
            var (snapshot, _) = FourCuts();
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye a = Eye(new Vector3(-3f, 3f, 3f), new Vector3(0f, 0.7f, 0f));
            VpCapEye b = Eye(new Vector3(3f, -3f, -3f), new Vector3(0f, 0.7f, 0f));
            int caps = snapshot.CapCount;
            snapshot.TryGetCapVertex(0, 0, out Vector3 vertex);

            Assert.That(classification.TryClassify(snapshot, a, a, VpStencilTestSettings.Create(2)), Is.True);
            var first = Capture(classification);
            Assert.That(classification.TryClassify(snapshot, b, b, VpStencilTestSettings.Create(2)), Is.True);
            var second = Capture(classification);
            Assert.That(classification.TryClassify(snapshot, a, a, VpStencilTestSettings.Create(2)), Is.True);
            var third = Capture(classification);
            Assert.That(third, Is.EqualTo(first), "the second A is the first A");
            Assert.That(second, Is.Not.EqualTo(first), "the layout: B sees something else");
            Assert.That(classification.HeldViews, Is.Zero, "no look at the snapshot is held");
            Assert.That(snapshot.IsBuilt, Is.True);
            Assert.That(snapshot.CapCount, Is.EqualTo(caps));
            snapshot.TryGetCapVertex(0, 0, out Vector3 again);
            Assert.That(again, Is.EqualTo(vertex), "the snapshot is only read");

            VpMultiCutSnapshot unbuilt = S.NewSnapshot();
            Assert.That(classification.TryClassify(unbuilt, a, a, VpStencilTestSettings.Create(2)), Is.False, "not built");
            Assert.That(classification.IsPrepared, Is.False);
            Assert.That(classification.RenderFragmentCount + classification.CapCount + classification.TargetCount, Is.Zero);
            var small = new VpMultiCutStencilClassification(new VpMultiCutCapacities(64, 1024, 2, 512, 64));
            Assert.That(small.TryClassify(snapshot, a, a, VpStencilTestSettings.Create(2)), Is.False, "more render fragments than room");
        }

        /// <summary>
        /// After a success, an attempt that fails leaves nothing prepared: a null snapshot and settings that are not valid
        /// throw, and afterwards no count or result is readable. An exception part of the way through -- thrown by the
        /// test hook after two targets, with looks at the snapshot already written -- is not swallowed, leaves no look
        /// held and nothing prepared. A plain attempt afterwards succeeds and gives the earlier result.
        /// </summary>
        [Test]
        public void AFailedAttempt_LeavesNothingPreparedAndNothingHeld()
        {
            var (snapshot, _) = FourCuts();
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye eye = Eye(new Vector3(-3f, 3f, 3f), new Vector3(0f, 0.7f, 0f));
            VpStencilSettings settings = VpStencilTestSettings.Create(4);
            Assert.That(classification.TryClassify(snapshot, eye, eye, settings), Is.True);
            string before = Capture(classification);

            Assert.Throws<ArgumentNullException>(() => classification.TryClassify(null, eye, eye, settings));
            AssertNothingPrepared(classification, "after a null snapshot");

            Assert.That(classification.TryClassify(snapshot, eye, eye, settings), Is.True);
            Assert.Throws<ArgumentException>(() => classification.TryClassify(snapshot, eye, eye, VpStencilTestSettings.Create(0)));
            AssertNothingPrepared(classification, "after settings that are not valid");

            Assert.That(classification.TryClassify(snapshot, eye, eye, settings), Is.True);
            int heldAtFault = -1;
            classification.AfterTargetWritten = written =>
            {
                if (written == 2)
                {
                    heldAtFault = classification.HeldViews;
                    throw new InvalidOperationException("a fault part of the way through");
                }
            };
            Assert.Throws<InvalidOperationException>(() => classification.TryClassify(snapshot, eye, eye, settings), "not swallowed");
            classification.AfterTargetWritten = null;
            Assert.That(heldAtFault, Is.GreaterThan(0), "the layout: looks were written when it failed");
            Assert.That(classification.HeldViews, Is.Zero, "and none is held afterwards");
            AssertNothingPrepared(classification, "after a fault part of the way");

            Assert.That(classification.TryClassify(snapshot, eye, eye, settings), Is.True, "a plain attempt afterwards");
            Assert.That(Capture(classification), Is.EqualTo(before), "gives the earlier result");
            Assert.That(classification.HeldViews, Is.Zero);
        }

        private static void AssertNothingPrepared(VpMultiCutStencilClassification c, string what)
        {
            Assert.That(c.IsPrepared, Is.False, what);
            Assert.That(c.RenderFragmentCount + c.CapCount + c.TargetCount + c.GroupCount + c.ColourCount + c.VolumeTargetCount + c.IssuedCapCount, Is.Zero, what + ": no count");
            Assert.That(c.TryGetRenderFragment(0, out _), Is.False, what + ": no render fragment result");
            Assert.That(c.TryGetCap(0, out _), Is.False, what + ": no cap result");
        }

        private static string Capture(VpMultiCutStencilClassification c)
        {
            var text = new System.Text.StringBuilder();
            text.Append(c.TargetCount).Append('/').Append(c.GroupCount).Append('/').Append(c.CulledGroupCount).Append('/')
                .Append(c.ColourCount).Append('/').Append(c.OrdinaryColourCount).Append('/').Append(c.GroupsInLastColour).Append('/')
                .Append(c.VolumeTargetCount).Append('/').Append(c.IssuedCapCount).Append(';');
            for (int r = 0; r < c.RenderFragmentCount; r++)
            {
                c.TryGetRenderFragment(r, out VpMultiCutStencilRenderFragment f);
                text.Append(f.group).Append(',').Append(f.colour).Append(',').Append(f.volumeIssued).Append(',').Append(f.visibleCaps).Append(',').Append(f.capsComplete).Append(';');
            }

            for (int k = 0; k < c.CapCount; k++)
            {
                c.TryGetCap(k, out VpMultiCutStencilCap cap);
                text.Append(cap.visible ? 'v' : '-').Append(cap.issued ? 'i' : '-');
            }

            return text.ToString();
        }

        /// <summary>
        /// After warming up, a hundred classifications over two eyes in turn show no GC.Alloc sample on this thread. The
        /// recorder is first shown one known allocation through the same window and must count it. The managed heap's
        /// used-size difference is written down as an observation only.
        /// </summary>
        [Test]
        public void RepeatedClassification_ShowsNoManagedAllocation()
        {
            var (snapshot, _) = FourCuts();
            VpMultiCutStencilClassification classification = NewClassification();
            VpCapEye a = Eye(new Vector3(-3f, 3f, 3f), new Vector3(0f, 0.7f, 0f));
            VpCapEye b = Eye(new Vector3(3f, 3f, -3f), new Vector3(0f, 0.7f, 0f));
            VpStencilSettings settings = VpStencilTestSettings.Create(2);
            for (int i = 0; i < Warmup; i++)
            {
                classification.TryClassify(snapshot, a, a, settings);
                classification.TryClassify(snapshot, b, b, settings);
            }

            Recorder recorder = Open(out long start);
            var known = new byte[4096];
            int control = Close(recorder, start, out long controlHeap);
            GC.KeepAlive(known);

            bool all = true;
            recorder = Open(out start);
            for (int i = 0; i < Iterations; i++)
            {
                all &= (i & 1) == 0 ? classification.TryClassify(snapshot, a, a, settings) : classification.TryClassify(snapshot, b, b, settings);
            }

            int samples = Close(recorder, start, out long heap);
            TestContext.WriteLine("positive control: " + control + " GC.Alloc samples, heap used-size difference " + controlHeap + " (observation only)");
            TestContext.WriteLine(Iterations + " classifications: " + samples + " GC.Alloc samples, heap used-size difference " + heap + " (observation only)");
            Assert.That(control, Is.GreaterThanOrEqualTo(1), "the recorder counts a known allocation on this thread");
            Assert.That(all, Is.True);
            Assert.That(samples, Is.Zero, "no GC.Alloc sample on this thread");
        }

        private static Recorder Open(out long usedAtStart)
        {
            Recorder recorder = Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            usedAtStart = Profiler.GetMonoUsedSizeLong();
            return recorder;
        }

        private static int Close(Recorder recorder, long usedAtStart, out long heapDifference)
        {
            long usedAtEnd = Profiler.GetMonoUsedSizeLong();
            recorder.enabled = false;
            recorder.CollectFromAllThreads();
            heapDifference = usedAtEnd - usedAtStart;
            return recorder.sampleBlockCount;
        }
    }
}
