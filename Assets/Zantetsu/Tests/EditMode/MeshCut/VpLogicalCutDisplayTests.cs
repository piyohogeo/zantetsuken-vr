using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The product connection between the logical cut ledger and the provisional split display (DESIGN 5.1, 7.1):
    /// one body, one admitted cut, and what is drawn at each logical state — whole before admission, the parent
    /// clipped to two sides once the inputs are ready, the same two sides carried to the published children, nothing
    /// at all once the source is retired.
    /// <para>
    /// Nothing here cuts geometry: both sides address the same stored geometry and the same index range, and the
    /// check that no further vertex or index transfer happens is part of what is asserted. The ledger is the state,
    /// and nothing the display does advances a cut — the incomplete budget is watched for exactly that.
    /// </para>
    /// <para>
    /// The frame counter is driven from here through the display's test entry point, because an EditMode test has no
    /// frame loop of its own.
    /// </para>
    /// </summary>
    public class VpLogicalCutDisplayTests
    {
        private const int ControlPoints = 8;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const float Epsilon = 0.01f;
        private const float Separation = 0.25f;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private int _frame;

        [SetUp]
        public void ResetFrame()
        {
            _frame = 1;
        }

        [TearDown]
        public void DestroyObjects()
        {
            foreach (UnityEngine.Object tracked in _objects)
            {
                if (tracked != null)
                {
                    UnityEngine.Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, 1.0f), new float3(-1.0f, 0.0f, 1.0f),
            new float3(-1.0f, 2.0f, -1.0f), new float3(1.0f, 2.0f, -1.0f), new float3(1.0f, 2.0f, 1.0f), new float3(-1.0f, 2.0f, 1.0f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        /// <summary>The plane y = 1 in the geometry's own frame: it crosses the body halfway up.</summary>
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -1f);

        /// <summary>An anchor below the plane, and one above it.</summary>
        private static readonly float3 k_lowAnchor = new float3(0f, 0.2f, 0f);
        private static readonly float3 k_highAnchor = new float3(0f, 1.8f, 0f);

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(
                        k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex { position = k_controlPoints[c[k]], normal = n, uv0 = uv[k] });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), ControlPoints, submeshes.ToArray(),
                    out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            return new Dictionary<int, Material>
            {
                { SideMaterial, Track(new Material(shader) { name = "side" }) },
                { EndMaterial, Track(new Material(shader) { name = "end" }) },
            };
        }

        private bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            LogicalCutLedger ledger,
            out VpLogicalCutDisplay display,
            int commandCapacity = 16,
            int instanceCapacity = 16)
        {
            return VpLogicalCutDisplay.TryCreate(
                storage, table, ledger, Materials(), null, commandCapacity, instanceCapacity, () => _frame, out display);
        }

        private void NextFrame()
        {
            _frame++;
        }

        private static LogicalCutLedger NewLedger(int limit = 4)
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(limit));
        }

        private static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId source)
        {
            Assert.That(
                ledger.Admit(source, k_plane, true, out CutOperationId operation), Is.EqualTo(LogicalCutAdmission.Admitted),
                "the cut is admitted");
            return operation;
        }

        private static void Prepare(LogicalCutLedger ledger, CutOperationId operation)
        {
            Assert.That(
                ledger.PrepareAnchorDistribution(operation, Epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared),
                "the anchor distribution is prepared");
        }

        /// <summary>Vectors are compared with a small tolerance: these come through a matrix and a normalisation.</summary>
        private static void AssertVector(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That(
                (actual - expected).magnitude, Is.LessThan(1e-4f),
                what + ": expected " + expected + " but was " + actual);
        }

        private static LogicalCutDisplaySide SideOf(VpLogicalCutDisplay display, int index)
        {
            Assert.That(display.TryGetSide(index, out LogicalCutDisplaySide side), Is.True, "side " + index);
            return side;
        }

        // ----- the three states of one cut ------------------------------------------------------------------------

        /// <summary>
        /// One body before admission, the provisional split once the cut is admitted and its inputs are ready, and
        /// the same split carried over to the two children when it is published.
        /// </summary>
        [Test]
        public void OneBody_BecomesTwoClippedSides_AndThenTheTwoPublishedChildren()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor, k_highAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True, "show the body");

                    // Before admission: the whole body, drawn once, with no clip at all.
                    Assert.That(display.TryBeginFrame(), Is.True, "settle the first frame");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole));
                    Assert.That(display.SideCount, Is.EqualTo(2), "one instance per command of the one body");
                    Assert.That(SideOf(display, 0).clip.IsClipped, Is.False);
                    Assert.That(SideOf(display, 0).side, Is.Zero);
                    Assert.That(SideOf(display, 0).operation.IsSet, Is.False);

                    // Admitted, but nothing prepared yet: still the whole body, and told apart from "no anchors".
                    CutOperationId cut = Admit(ledger, source);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True, "settle after admission");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.AwaitingInputs));
                    Assert.That(SideOf(display, 0).clip.IsClipped, Is.False, "nothing is clipped before its inputs");

                    // Prepared: the provisional split, without waiting for publication.
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True, "settle after preparation");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                    Assert.That(display.SideCount, Is.EqualTo(4), "two sides per command now");

                    LogicalCutDisplaySide positive = SideOf(display, 0);
                    LogicalCutDisplaySide negative = SideOf(display, 1);
                    Assert.That(positive.side, Is.EqualTo(1f));
                    Assert.That(negative.side, Is.EqualTo(-1f));
                    Assert.That(positive.operation, Is.EqualTo(cut), "the sides belong to the operation");
                    Assert.That(positive.clip.IsClipped, Is.True);
                    Assert.That(negative.clip.IsClipped, Is.True);
                    Assert.That(positive.published, Is.False, "and to no child yet");
                    Assert.That(positive.fragment.IsSet, Is.False, "no child id is issued early");
                    Assert.That(negative.fragment.IsSet, Is.False);

                    // Published: the same two sides, now the two children.
                    Assert.That(
                        ledger.Publish(cut, out LogicalFragmentId child0, out LogicalFragmentId child1),
                        Is.EqualTo(LogicalCutResultOutcome.Applied),
                        "the cut publishes");
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True, "settle after publication");
                    Assert.That(display.SideCount, Is.EqualTo(4), "still two sides per command, not four");

                    LogicalCutDisplaySide publishedPositive = SideOf(display, 0);
                    LogicalCutDisplaySide publishedNegative = SideOf(display, 1);
                    Assert.That(publishedPositive.published, Is.True);
                    Assert.That(publishedPositive.fragment, Is.EqualTo(child0), "the positive side is the positive child");
                    Assert.That(publishedNegative.fragment, Is.EqualTo(child1));
                    Assert.That(publishedPositive.clip.SignedPlane(0), Is.EqualTo(positive.clip.SignedPlane(0)), "the same face");
                    Assert.That(display.StateOf(child0), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                    Assert.That(display.StateOf(child1), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                }
            }
        }

        /// <summary>
        /// The two sides are the same geometry: one command per submesh with two instances, the same index range as
        /// the parent had, and not one further vertex or index transfer for the split.
        /// </summary>
        [Test]
        public void BothSides_AddressTheSameGeometryAndRange_WithNoFurtherTransfer()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True, "show the body");
                    Assert.That(display.TryBeginFrame(), Is.True);

                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    Assert.That(vertexTransfers, Is.EqualTo(1), "the body was transferred once");
                    Assert.That(indexTransfers, Is.EqualTo(1));

                    var wholeRanges = new List<VpGeometryRange>();
                    for (int c = 0; c < display.DrawCommandCount; c++)
                    {
                        Assert.That(display.TryGetDrawCommand(c, out VpIndirectCommand command), Is.True);
                        Assert.That(command.instanceCount, Is.EqualTo(1), "one instance while it is whole");
                        wholeRanges.Add(command.range);
                    }

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);

                    Assert.That(display.DrawCommandCount, Is.EqualTo(wholeRanges.Count), "the same commands");
                    for (int c = 0; c < display.DrawCommandCount; c++)
                    {
                        Assert.That(display.TryGetDrawCommand(c, out VpIndirectCommand command), Is.True);
                        Assert.That(command.instanceCount, Is.EqualTo(2), "two instances of the one geometry");
                        Assert.That(
                            command.range.indexStart, Is.EqualTo(wholeRanges[c].indexStart), "the parent's own index range");
                        Assert.That(command.range.indexCount, Is.EqualTo(wholeRanges[c].indexCount));
                    }

                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "no vertex went to the GPU again");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers), "and no index either");
                    Assert.That(display.ShownCount, Is.EqualTo(1), "it is still one body, drawn twice");
                }
            }
        }

        // ----- offsets and fixity ----------------------------------------------------------------------------------

        /// <summary>
        /// The anchors decide which side is fixed, and only a free side is moved. Both sides are clipped in every
        /// case, and the plane is never moved: the kerf stays zero.
        /// </summary>
        [Test]
        public void TheOffsets_FollowTheAnchors_OnOneSideBothSidesAndNeither()
        {
            // The anchor sits below the plane, so the negative side is fixed and only the positive one moves.
            AssertOffsets(
                new List<float3> { k_lowAnchor },
                positiveFixed: false, negativeFixed: true);

            // An anchor on each side: both fixed, both still clipped, neither moved.
            AssertOffsets(
                new List<float3> { k_lowAnchor, k_highAnchor },
                positiveFixed: true, negativeFixed: true);

            // No anchors at all: both sides are free and both move, in opposite directions.
            AssertOffsets(null, positiveFixed: false, negativeFixed: false);
        }

        private void AssertOffsets(List<float3> anchors, bool positiveFixed, bool negativeFixed)
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = anchors == null ? ledger.AddFragment() : ledger.AddFragment(anchors);
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    display.Separation = Separation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    LogicalCutDisplaySide positive = SideOf(display, 0);
                    LogicalCutDisplaySide negative = SideOf(display, 1);

                    Assert.That(positive.fixedByAnchors, Is.EqualTo(positiveFixed), "the positive side's fixity");
                    Assert.That(negative.fixedByAnchors, Is.EqualTo(negativeFixed), "the negative side's fixity");
                    Assert.That(positive.clip.IsClipped, Is.True, "a fixed side is clipped all the same");
                    Assert.That(negative.clip.IsClipped, Is.True);

                    // The plane itself is the same for both sides, with only the side folded in: no plane was moved.
                    Assert.That(
                        positive.clip.SignedPlane(0), Is.EqualTo(-negative.clip.SignedPlane(0)),
                        "one plane, two sides of it");

                    Vector3 up = Vector3.up;
                    AssertVector(positive.offset, positiveFixed ? Vector3.zero : up * Separation, "the positive side's separation");
                    AssertVector(negative.offset, negativeFixed ? Vector3.zero : -up * Separation, "the negative side's separation");
                    AssertVector(positive.clip.Offset, positive.offset, "the positive clip carries that separation");
                    AssertVector(negative.clip.Offset, negative.offset, "the negative clip carries that separation");
                }
            }
        }

        /// <summary>
        /// A body that is moved and turned: the plane the sides are clipped by is the adopted plane in world space,
        /// and the separation follows that world plane's own normal, not the geometry's axes.
        /// </summary>
        [Test]
        public void AMovedAndTurnedBody_KeepsTheFaceTheSideAndTheOffsetConsistent()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment();
                VpStoredGeometry geometry = Append(storage);
                Matrix4x4 placement = Matrix4x4.TRS(
                    new Vector3(3f, -1f, 2f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    display.Separation = Separation;
                    Assert.That(display.TryShow(source, geometry, placement), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    LogicalCutDisplaySide positive = SideOf(display, 0);
                    LogicalCutDisplaySide negative = SideOf(display, 1);

                    // y = 1 in the body's own frame, turned a quarter turn about z, is x = -1 in world.
                    Vector4 plane = positive.clip.SignedPlane(0);
                    var worldNormal = new Vector3(plane.x, plane.y, plane.z);
                    AssertVector(worldNormal, Vector3.left, "the turned normal");

                    // A point the body covers on the positive side of that world plane really is kept by it.
                    Vector3 positivePoint = placement.MultiplyPoint3x4(new Vector3(0f, 1.5f, 0f));
                    Assert.That(
                        Vector3.Dot(worldNormal, positivePoint) + plane.w, Is.GreaterThan(0f),
                        "the positive side keeps what is above the plane in the body's own frame");

                    AssertVector(positive.offset, worldNormal * Separation, "the separation follows the world plane's normal");
                    AssertVector(negative.offset, -worldNormal * Separation, "and the other side's is the opposite");
                    Assert.That(negative.clip.SignedPlane(0), Is.EqualTo(-plane), "the other side of the same face");
                }
            }
        }

        // ----- what leaves the display alone -----------------------------------------------------------------------

        /// <summary>
        /// A request the ledger skips — a no-op, a source that is already cutting, the shared limit reached — changes
        /// neither the display nor the ledger's own state.
        /// </summary>
        [Test]
        public void ASkippedRequest_LeavesTheDisplayAndTheLedgerAlone()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                var budget = new LogicalCutIncompleteBudget(1);
                var ledger = new LogicalCutLedger(budget);
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                LogicalFragmentId other = ledger.AddFragment();
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    int sides = display.SideCount;
                    int uploads = display.CommandUploads;

                    // A no-op: classified as not splitting both sides before admission.
                    Assert.That(
                        ledger.Admit(source, k_plane, false, out _), Is.EqualTo(LogicalCutAdmission.NoOp),
                        "a no-op is skipped");
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole), "still the whole body");
                    Assert.That(display.SideCount, Is.EqualTo(sides));

                    // The source is already cutting: a second request is skipped, and the display is unmoved.
                    CutOperationId cut = Admit(ledger, source);
                    Assert.That(
                        ledger.Admit(source, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.SourceActive),
                        "a source with an active operation is skipped");

                    // And the shared limit is reached, so another body cannot be admitted either.
                    Assert.That(
                        ledger.Admit(other, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full),
                        "the incomplete budget is full");
                    Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1), "and nothing was counted twice");

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(
                        display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.AwaitingInputs),
                        "the one admitted cut is waiting for its inputs, and nothing else changed");
                    Assert.That(display.SideCount, Is.EqualTo(sides));
                    Assert.That(ledger.TryGetActiveOperation(source, out CutOperationId active), Is.True);
                    Assert.That(active, Is.EqualTo(cut));
                    Assert.That(display.CommandUploads, Is.GreaterThan(uploads), "the display did settle each frame");
                }
            }
        }

        /// <summary>An aborted cut retires its source, and the display stops drawing it and gives its references back.</summary>
        [Test]
        public void AnAbortedCut_RemovesTheRetiredSourceFromTheDisplay()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.SideCount, Is.EqualTo(4), "the split is on screen");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1));
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(2), "one instance per side");

                    Assert.That(ledger.Abort(cut), Is.EqualTo(LogicalCutResultOutcome.Applied), "the cut aborts");
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);

                    Assert.That(display.ShownCount, Is.Zero, "the retired source is not drawn");
                    Assert.That(display.SideCount, Is.Zero);
                    Assert.That(display.DrawCommandCount, Is.Zero);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.NotShown));
                    Assert.That(table.LiveDisplayInstanceCount, Is.Zero, "and every reference it took came back");
                    Assert.That(table.LiveGeometryCount, Is.Zero);
                }
            }
        }

        /// <summary>
        /// A result reclaimed as stale is not published: the source stays live with no active operation, so the
        /// display goes back to the whole body rather than showing a split that never happened.
        /// </summary>
        [Test]
        public void AStaleResult_IsNotShownAsPublished()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));

                    // The authority changes under the operation, and the result is reclaimed instead of applied.
                    ledger.NoteOwnershipChanged(source);
                    Assert.That(
                        ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative),
                        Is.EqualTo(LogicalCutResultOutcome.Stale),
                        "the result is stale");
                    Assert.That(positive.IsSet, Is.False, "and no child was issued");
                    Assert.That(negative.IsSet, Is.False);

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(
                        display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole),
                        "the display follows the state that is valid now");
                    Assert.That(display.SideCount, Is.EqualTo(2), "one instance per command again");
                    Assert.That(SideOf(display, 0).clip.IsClipped, Is.False);
                    Assert.That(SideOf(display, 0).published, Is.False);
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1), "and the second instance came back");
                }
            }
        }

        // ----- frames, budget and teardown --------------------------------------------------------------------------

        /// <summary>
        /// Collecting the same state again changes nothing, and a collection inside a frame that has already drawn
        /// does not rewrite that frame's draw data.
        /// </summary>
        [Test]
        public void AFrameThatHasDrawn_IsNotRewrittenByAnotherCollection()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    display.Render(0);
                    Assert.That(display.HasDrawnThisFrame, Is.True);

                    int settled = display.SettledCollections;
                    int uploads = display.CommandUploads;
                    int sides = display.SideCount;

                    // The same state again, inside the same frame: nothing at all happens.
                    Assert.That(display.TryBeginFrame(), Is.True, "the same frame is already settled");
                    Assert.That(display.SettledCollections, Is.EqualTo(settled), "nothing was collected again");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "and nothing was uploaded again");

                    // A cut admitted and prepared inside that same frame does not rewrite what it already drew.
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.SideCount, Is.EqualTo(sides), "the frame that drew still draws what it settled");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads));
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole));

                    // The next frame is where it takes effect.
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                    Assert.That(display.SideCount, Is.EqualTo(4));
                    Assert.That(display.HasDrawnThisFrame, Is.False, "a newly settled frame has not drawn yet");
                }
            }
        }

        /// <summary>
        /// Showing, splitting, publishing and ending a display never return the cut's share of the shared incomplete
        /// budget: only the ledger's own notice does that.
        /// </summary>
        [Test]
        public void TheDisplay_NeverReturnsTheSharedIncompleteUnit()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                var budget = new LogicalCutIncompleteBudget(4);
                var ledger = new LogicalCutLedger(budget);
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                CutOperationId cut;
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1), "the cut holds its place");

                    Assert.That(
                        ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    display.Render(0);
                    Assert.That(
                        budget.IncompleteCutOperationCount, Is.EqualTo(1),
                        "showing the published children does not complete the geometry");
                }

                Assert.That(
                    budget.IncompleteCutOperationCount, Is.EqualTo(1), "and ending the display does not either");
                Assert.That(table.LiveDisplayInstanceCount, Is.Zero, "though it did give its references back");
                Assert.That(table.LiveGeometryCount, Is.Zero);

                // Only the ledger's own notice returns it.
                Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
                Assert.That(budget.IncompleteCutOperationCount, Is.Zero);
            }
        }

        /// <summary>
        /// What the display refuses: a fragment that is not live, one that is already shown, and a split that does not
        /// fit the fixed capacity — which is decided before anything is uploaded, leaving the previous frame on screen.
        /// </summary>
        [Test]
        public void TheDisplayRefusesWhatItCannotShow_WithoutDisturbingWhatItHas()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(default, geometry, Matrix4x4.identity), Is.False, "an unset fragment");
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(
                        display.TryShow(source, geometry, Matrix4x4.identity), Is.False,
                        "the same fragment is not shown twice");
                    Assert.That(display.ShownCount, Is.EqualTo(1));
                }

            }

            // A capacity with room for the body but not for its split: the refusal comes before any transfer, so
            // nothing of it is taken at all. A storage of its own, because one storage has one reference table.
            using (VpCpuGeometryStorage narrowStorage = NewStorage())
            {
                var narrowTable = new VpGeometryReferenceTable(narrowStorage, 8, 8);
                LogicalCutLedger narrowLedger = NewLedger();
                LogicalFragmentId narrowSource = narrowLedger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry narrowGeometry = Append(narrowStorage);
                Assert.That(
                    TryCreate(narrowStorage, narrowTable, narrowLedger, out VpLogicalCutDisplay narrow, 2, 2),
                    Is.True,
                    "create narrow");
                using (narrow)
                {
                    Assert.That(
                        narrow.TryShow(narrowSource, narrowGeometry, Matrix4x4.identity), Is.False,
                        "a body whose split would not fit is not taken at all");
                    Assert.That(narrow.ShownCount, Is.Zero);
                    Assert.That(narrowTable.LiveGeometryCount, Is.Zero, "and no reference was taken for it");
                    Assert.That(narrow.VertexTransfers, Is.Zero, "nor was anything transferred");
                }
            }
        }

        /// <summary>
        /// A cut admitted, prepared and published with no display update in between: the display finds the children
        /// from the ledger, not from an operation it happened to remember, so a publication never depends on having
        /// been drawn first.
        /// </summary>
        [Test]
        public void APublicationWithNoDisplayUpdateInBetween_IsStillShown()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the parent is settled");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole));

                    // Admitted, prepared and published inside one frame, with no collection between the steps.
                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(
                        ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative),
                        Is.EqualTo(LogicalCutResultOutcome.Applied));

                    NextFrame();

                    // Offered before this frame is settled, so the frame guard has nothing to say: the child is
                    // refused because the ledger says it is a child of a body already shown, which no collection has
                    // seen yet.
                    Assert.That(
                        display.TryShow(positive, geometry, Matrix4x4.identity), Is.False,
                        "a published child is not taken as a body of its own");
                    Assert.That(
                        display.TryShow(negative, geometry, Matrix4x4.identity), Is.False,
                        "and neither is the other one");
                    Assert.That(display.ShownCount, Is.EqualTo(1), "still the one body");

                    Assert.That(display.TryBeginFrame(), Is.True, "and the frame settles straight from the ledger");
                    Assert.That(display.SideCount, Is.EqualTo(4));
                    Assert.That(SideOf(display, 0).published, Is.True);
                    Assert.That(SideOf(display, 0).fragment, Is.EqualTo(positive));
                    Assert.That(SideOf(display, 1).fragment, Is.EqualTo(negative));
                    Assert.That(SideOf(display, 0).operation, Is.EqualTo(cut), "the cut that replaced the body");
                    Assert.That(display.StateOf(positive), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                    Assert.That(display.StateOf(negative), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                }
            }
        }

        /// <summary>
        /// A cut reclaimed as stale, and then a second cut admitted and published with no collection in between: the
        /// display shows the second cut's children, and never the operation that was reclaimed.
        /// </summary>
        [Test]
        public void AfterAStaleResult_ThePublicationThatFollows_IsTheOneShown()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    CutOperationId first = Admit(ledger, source);
                    Prepare(ledger, first);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(SideOf(display, 0).operation, Is.EqualTo(first), "the first cut is what is shown");

                    // The authority changes, so the first result is reclaimed rather than applied.
                    ledger.NoteOwnershipChanged(source);
                    Assert.That(ledger.Publish(first, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));

                    // A second cut is admitted and published, with no collection anywhere in between.
                    CutOperationId second = Admit(ledger, source);
                    Prepare(ledger, second);
                    Assert.That(
                        ledger.Publish(second, out LogicalFragmentId positive, out LogicalFragmentId negative),
                        Is.EqualTo(LogicalCutResultOutcome.Applied));

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(SideOf(display, 0).operation, Is.EqualTo(second), "the cut that really replaced it");
                    Assert.That(SideOf(display, 0).fragment, Is.EqualTo(positive));
                    Assert.That(SideOf(display, 1).fragment, Is.EqualTo(negative));
                    Assert.That(SideOf(display, 0).published, Is.True);
                }
            }
        }

        /// <summary>
        /// A collection that cannot be made leaves the adopted snapshot exactly as it was — what it shows, what it
        /// draws and what it holds — and says so; once there is room again, **the same display** settles the state it
        /// could not settle before.
        /// <para>
        /// The shortage is an ordinary one: the reference table has two display instances, the body holds one, and
        /// something outside this display holds the other until it gives it back.
        /// </para>
        /// </summary>
        [Test]
        public void ARefusedCollection_KeepsTheSnapshotItHad_AndTheSameDisplayRecovers()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 2);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);
                VpStoredGeometry elsewhere = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the parent settles");
                    display.Render(0);

                    int sides = display.SideCount;
                    int commands = display.DrawCommandCount;
                    int uploads = display.CommandUploads;
                    int settled = display.SettledCollections;
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    Assert.That(sides, Is.EqualTo(2), "one instance per command of the whole body");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1));

                    // Something else takes the table's last display instance, so the split has none to take.
                    Assert.That(
                        table.TryRegisterGeometryWithDisplayInstance(
                            elsewhere, out VpGeometryReference otherGeometry, out VpDisplayInstanceReference otherInstance),
                        Is.True,
                        "the other holder takes the last instance");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(2), "the table is full");

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();

                    Assert.That(display.TryBeginFrame(), Is.False, "the latest state could not be settled");
                    Assert.That(display.SettledCollections, Is.EqualTo(settled), "nothing was settled");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "and nothing was uploaded");
                    Assert.That(display.SideCount, Is.EqualTo(sides), "the snapshot it had is still the one it has");
                    Assert.That(display.DrawCommandCount, Is.EqualTo(commands));
                    Assert.That(SideOf(display, 0).clip.IsClipped, Is.False, "which is the whole body");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole));
                    Assert.That(
                        table.LiveDisplayInstanceCount, Is.EqualTo(2),
                        "and the refused pass gave back what it had taken");

                    // That earlier snapshot is still what this frame draws.
                    Assert.That(() => display.Render(0), Throws.Nothing, "the snapshot it kept is drawable");
                    Assert.That(display.HasDrawnThisFrame, Is.True);

                    // The other holder gives its instance back, and the very same display settles what it could not.
                    Assert.That(table.TryRetireDisplayInstance(otherInstance), Is.True, "the instance comes back");
                    Assert.That(table.TryRetireGeometry(otherGeometry), Is.True);
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1), "only the body's own is held now");

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True, "the same display settles the split now");
                    Assert.That(display.SettledCollections, Is.EqualTo(settled + 1));
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads + 1));
                    Assert.That(display.SideCount, Is.EqualTo(4), "two sides per command");
                    Assert.That(display.DrawCommandCount, Is.EqualTo(commands), "the same commands as before");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                    Assert.That(SideOf(display, 0).clip.IsClipped, Is.True);
                    Assert.That(SideOf(display, 1).clip.IsClipped, Is.True);
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(2), "one instance per side");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "and nothing was transferred");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers));

                    for (int c = 0; c < display.DrawCommandCount; c++)
                    {
                        Assert.That(display.TryGetDrawCommand(c, out VpIndirectCommand command), Is.True);
                        Assert.That(command.instanceCount, Is.EqualTo(2), "both sides of the one geometry");
                    }
                }
            }
        }

        /// <summary>
        /// Showing a body is refused in a frame this display has already settled or drawn, and refused before
        /// anything is transferred or registered: the frame that has drawn is not reopened through that door.
        /// </summary>
        [Test]
        public void ShowingABody_IsRefusedInAFrameThatHasAlreadySettledOrDrawn()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId first = ledger.AddFragment(new List<float3> { k_lowAnchor });
                LogicalFragmentId second = ledger.AddFragment();
                VpStoredGeometry geometry = Append(storage);
                VpStoredGeometry other = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryShow(first, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    display.Render(0);

                    int sides = display.SideCount;
                    int commands = display.DrawCommandCount;
                    int uploads = display.CommandUploads;
                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;
                    int geometries = table.LiveGeometryCount;
                    int instances = table.LiveDisplayInstanceCount;

                    Assert.That(
                        display.TryShow(second, other, Matrix4x4.identity), Is.False,
                        "this frame is settled and drawn, so nothing is taken into it");
                    Assert.That(display.ShownCount, Is.EqualTo(1), "the body was not taken");
                    Assert.That(display.VertexTransfers, Is.EqualTo(vertexTransfers), "nothing was transferred");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers));
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(geometries), "and nothing was registered");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(instances));

                    Assert.That(display.TryBeginFrame(), Is.True, "the settled frame is still settled");
                    Assert.That(display.CommandUploads, Is.EqualTo(uploads), "with nothing uploaded again");
                    Assert.That(display.SideCount, Is.EqualTo(sides));
                    Assert.That(display.DrawCommandCount, Is.EqualTo(commands));

                    // Offered again before the next frame is settled, it is taken in the ordinary way.
                    NextFrame();
                    Assert.That(display.TryShow(second, other, Matrix4x4.identity), Is.True, "the next frame takes it");
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.ShownCount, Is.EqualTo(2));
                    Assert.That(display.SideCount, Is.EqualTo(sides * 2));
                }
            }
        }
    }
}
