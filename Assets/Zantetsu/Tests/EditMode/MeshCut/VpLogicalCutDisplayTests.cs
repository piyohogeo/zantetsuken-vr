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
        private Camera _camera;

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
            _camera = null;
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
            // The display path's own shader, so that the frames these tests draw are really drawn.
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
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
                storage, table, ledger, Materials(), null, null, commandCapacity, instanceCapacity,
                VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(), () => _frame, out display);
        }

        /// <summary>
        /// Draws one frame to a camera registered with the display and prepared for this frame -- a draw names its
        /// camera now -- and renders that camera into a small target, so the draws registered are drawn.
        /// </summary>
        private void RenderFrame(VpLogicalCutDisplay display)
        {
            if (_camera == null)
            {
                var target = Track(new RenderTexture(16, 16, 24, RenderTextureFormat.ARGB32)
                {
                    depthStencilFormat = VpStencilAttachment.EightBitStencilFormat,
                    antiAliasing = 1,
                });
                target.Create();
                _camera = Track(new GameObject("Logical Cut Display Test Camera")).AddComponent<Camera>();
                _camera.enabled = false;
                _camera.targetTexture = target;
                _camera.transform.SetPositionAndRotation(new Vector3(0f, 1f, -6f), Quaternion.identity);
            }

            display.TryRegisterCamera(_camera);
            Assert.That(display.TryPrepareCamera(_camera), Is.True, "the camera is prepared for this frame");
            display.Render(0, _camera);
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = _camera.targetTexture };
            if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(_camera, request))
            {
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(_camera, request);
            }
            else
            {
                _camera.Render();
            }
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    RenderFrame(display);
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
                using (new AfterTheFrame(NextFrame, display))
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
                    RenderFrame(display);
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
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
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the parent settles");
                    RenderFrame(display);

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
                    Assert.That(() => RenderFrame(display), Throws.Nothing, "the snapshot it kept is drawable");
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
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(first, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    RenderFrame(display);

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
        // ----- the provisional caps -------------------------------------------------------------------------------

        private static LogicalCutCapRecord CapOf(VpLogicalCutDisplay display, int index)
        {
            Assert.That(display.TryGetCapRecord(index, out LogicalCutCapRecord record), Is.True, "cap " + index);
            return record;
        }

        /// <summary>
        /// Every vertex of a prepared cap, read back through the display's own entry point rather than from any list
        /// of its own.
        /// </summary>
        private static Vector3[] CapVertices(VpLogicalCutDisplay display, int index)
        {
            LogicalCutCapRecord record = CapOf(display, index);
            var vertices = new Vector3[record.vertexCount];
            for (int i = 0; i < record.vertexCount; i++)
            {
                Assert.That(display.TryGetCapVertex(index, i, out vertices[i]), Is.True, "cap vertex " + i);
            }

            Assert.That(
                display.TryGetCapVertex(index, record.vertexCount, out _), Is.False, "and there are no more of them");
            return vertices;
        }

        /// <summary>
        /// What a prepared cap has to be: at least a triangle, every vertex on its own face once the separation is
        /// taken off again, no vertex repeated, and the winding agreeing with the outward normal it carries.
        /// </summary>
        private static void AssertCap(VpLogicalCutDisplay display, int index, string what)
        {
            LogicalCutCapRecord record = CapOf(display, index);
            Vector3[] vertices = CapVertices(display, index);
            Assert.That(vertices.Length, Is.InRange(3, 6), what + ": three to six vertices");

            var normal = new float3(record.worldPlane.x, record.worldPlane.y, record.worldPlane.z);
            Assert.That(math.length(normal), Is.EqualTo(1f).Within(1e-4f), what + ": a normalized face");
            AssertVector(
                record.outwardNormal, (Vector3)(record.side > 0f ? -normal : normal),
                what + ": the outward normal of the side that is kept");

            for (int i = 0; i < vertices.Length; i++)
            {
                // The separation was added after the placement, so it comes off again to test the face itself.
                float3 onFace = (float3)(vertices[i] - record.offset);
                Assert.That(
                    math.abs(math.dot(normal, onFace) + record.worldPlane.w), Is.LessThan(1e-3f),
                    what + ": vertex " + i + " lies in the adopted face");

                for (int j = i + 1; j < vertices.Length; j++)
                {
                    Assert.That(
                        Vector3.Distance(vertices[i], vertices[j]), Is.GreaterThan(1e-4f),
                        what + ": vertices " + i + " and " + j + " are not the same point");
                }
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                float3 a = vertices[i];
                float3 b = vertices[(i + 1) % vertices.Length];
                float3 c = vertices[(i + 2) % vertices.Length];
                Assert.That(
                    math.dot(math.cross(b - a, c - a), (float3)record.outwardNormal), Is.GreaterThan(0f),
                    what + ": the winding agrees with the outward normal at vertex " + ((i + 1) % vertices.Length));
            }
        }

        /// <summary>
        /// The caps appear exactly when the split does and belong to what the split belongs to: nothing before the
        /// cut is admitted, nothing while its inputs are not prepared, two once the split is shown — the source and
        /// the operation, and no child — and the same two carried over to the children at publication.
        /// <para>
        /// The body has two submeshes, so it is drawn as two commands per side; the caps are still two, because one
        /// body has one cross-section however many materials it is drawn with.
        /// </para>
        /// </summary>
        [Test]
        public void TheCaps_AppearWithTheSplit_AndAreCarriedToTheChildren()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor, k_highAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the whole body settles");
                    Assert.That(display.CapRecordCount, Is.Zero, "a whole body has no cut to cap");

                    // Admitted, with nothing prepared: DESIGN 7.1's "not ready" is not "no cap yet decided".
                    CutOperationId cut = Admit(ledger, source);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.AwaitingInputs));
                    Assert.That(display.CapRecordCount, Is.Zero, "no cap before the anchors are prepared");

                    // Prepared: the split, and with it the two caps — before publication, and for the whole body.
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.SideCount, Is.EqualTo(4), "two commands drawn twice");
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "one cap per side, not one per submesh");

                    LogicalCutCapRecord positive = CapOf(display, 0);
                    LogicalCutCapRecord negative = CapOf(display, 1);
                    Assert.That(positive.side, Is.EqualTo(1f));
                    Assert.That(negative.side, Is.EqualTo(-1f));
                    Assert.That(positive.source, Is.EqualTo(source), "the body the cross-section was taken of");
                    Assert.That(positive.operation, Is.EqualTo(cut), "and the cut whose face it lies in");
                    Assert.That(positive.published, Is.False, "no child before publication");
                    Assert.That(positive.fragment.IsSet, Is.False, "and no child id issued early");
                    Assert.That(negative.published, Is.False);
                    Assert.That(negative.fragment.IsSet, Is.False);
                    AssertCap(display, 0, "positive before publication");
                    AssertCap(display, 1, "negative before publication");

                    // The cube is cut across the middle, so both caps are the same 2 x 2 square at y = 1.
                    Vector3[] before = CapVertices(display, 0);
                    Assert.That(before.Length, Is.EqualTo(4), "a square cross-section");
                    foreach (Vector3 vertex in before)
                    {
                        Assert.That(
                            vertex.y - positive.offset.y, Is.EqualTo(1f).Within(1e-4f), "the face is y = 1");
                        Assert.That(Mathf.Abs(vertex.x), Is.EqualTo(1f).Within(1e-4f), "and it reaches the body's side");
                        Assert.That(Mathf.Abs(vertex.z), Is.EqualTo(1f).Within(1e-4f));
                    }

                    // Published: the same two caps, now naming the children. Nothing about the face changed.
                    Assert.That(
                        ledger.Publish(cut, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild),
                        Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "still two, not two more");

                    LogicalCutCapRecord positiveAfter = CapOf(display, 0);
                    LogicalCutCapRecord negativeAfter = CapOf(display, 1);
                    Assert.That(positiveAfter.published, Is.True);
                    Assert.That(positiveAfter.fragment, Is.EqualTo(positiveChild), "the positive child");
                    Assert.That(negativeAfter.fragment, Is.EqualTo(negativeChild), "and the negative one");
                    Assert.That(positiveAfter.operation, Is.EqualTo(cut), "the same adopted face");
                    Assert.That(positiveAfter.worldPlane, Is.EqualTo(positive.worldPlane));
                    AssertVector(positiveAfter.outwardNormal, positive.outwardNormal, "the same outward direction");

                    Vector3[] after = CapVertices(display, 0);
                    Assert.That(after.Length, Is.EqualTo(before.Length), "the same polygon");
                    for (int i = 0; i < after.Length; i++)
                    {
                        AssertVector(after[i], before[i], "vertex " + i + " is where it was");
                    }
                }
            }
        }

        /// <summary>
        /// A publication with no display update between the admission and it is shown as a publication here too: the
        /// caps come from the ledger's own state, not from an operation a collection happened to see first.
        /// </summary>
        [Test]
        public void CapsOfAPublicationWithNoUpdateInBetween_NameTheChildren()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the whole body settles");
                    Assert.That(display.CapRecordCount, Is.Zero);

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(
                        ledger.Publish(cut, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild),
                        Is.EqualTo(LogicalCutResultOutcome.Applied),
                        "admitted, prepared and published inside one frame");

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "the caps are prepared straight from the ledger");
                    Assert.That(CapOf(display, 0).published, Is.True);
                    Assert.That(CapOf(display, 0).fragment, Is.EqualTo(positiveChild));
                    Assert.That(CapOf(display, 1).fragment, Is.EqualTo(negativeChild));
                    AssertCap(display, 0, "positive of an unseen publication");
                    AssertCap(display, 1, "negative of an unseen publication");
                }
            }
        }

        /// <summary>
        /// An abort takes the caps with the body, and a result reclaimed as stale leaves none: a display that goes
        /// back to the whole body has nothing left to cap.
        /// </summary>
        [Test]
        public void AnAbortOrAStaleResult_LeavesNoCaps()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    CutOperationId first = Admit(ledger, source);
                    Prepare(ledger, first);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "the split has its caps");

                    // Reclaimed as stale: the source is live again with no active operation, so the whole body is
                    // what is drawn and there is no face to cap.
                    ledger.NoteOwnershipChanged(source);
                    Assert.That(ledger.Publish(first, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.Whole));
                    Assert.That(display.CapRecordCount, Is.Zero, "a reclaimed result caps nothing");

                    // Admitted and prepared again, then aborted: the source retires and the caps go with it.
                    CutOperationId second = Admit(ledger, source);
                    Prepare(ledger, second);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "the second cut has its caps");

                    Assert.That(ledger.Abort(second), Is.EqualTo(LogicalCutResultOutcome.Applied), "the cut aborts");
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.NotShown));
                    Assert.That(display.CapRecordCount, Is.Zero, "and nothing of it is left to cap");
                }
            }
        }

        /// <summary>
        /// A fixed side has a cap like any other, and two fixed sides have two: DESIGN 5.1 forbids the anchors from
        /// being a reason to leave a provisional drawing out. What the anchors do decide is the separation, and each
        /// cap carries its own side's.
        /// </summary>
        [Test]
        public void CapsSurvive_OnOneFixedSideBothFixedSidesAndNeither()
        {
            (float3[] anchors, bool positiveFixed, bool negativeFixed, string what)[] cases =
            {
                (new[] { k_lowAnchor }, false, true, "only the negative side fixed"),
                (new[] { k_highAnchor }, true, false, "only the positive side fixed"),
                (new[] { k_lowAnchor, k_highAnchor }, true, true, "both sides fixed"),
            };

            foreach ((float3[] anchors, bool positiveFixed, bool negativeFixed, string what) in cases)
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    var table = new VpGeometryReferenceTable(storage, 8, 8);
                    LogicalCutLedger ledger = NewLedger();
                    LogicalFragmentId source = ledger.AddFragment(new List<float3>(anchors));
                    VpStoredGeometry geometry = Append(storage);

                    Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                    using (new AfterTheFrame(NextFrame, display))
                    {
                        display.Separation = Separation;
                        Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                        CutOperationId cut = Admit(ledger, source);
                        Prepare(ledger, cut);
                        Assert.That(display.TryBeginFrame(), Is.True);

                        Assert.That(display.CapRecordCount, Is.EqualTo(2), what + ": both sides are capped");
                        LogicalCutCapRecord positive = CapOf(display, 0);
                        LogicalCutCapRecord negative = CapOf(display, 1);
                        Assert.That(positive.fixedByAnchors, Is.EqualTo(positiveFixed), what + ": positive");
                        Assert.That(negative.fixedByAnchors, Is.EqualTo(negativeFixed), what + ": negative");
                        AssertCap(display, 0, what + ", positive");
                        AssertCap(display, 1, what + ", negative");

                        // The plane is y = 1, so the free side moves along y and the fixed one does not move at all.
                        AssertVector(
                            positive.offset, positiveFixed ? Vector3.zero : new Vector3(0f, Separation, 0f),
                            what + ": the positive separation");
                        AssertVector(
                            negative.offset, negativeFixed ? Vector3.zero : new Vector3(0f, -Separation, 0f),
                            what + ": the negative separation");

                        foreach (Vector3 vertex in CapVertices(display, 0))
                        {
                            Assert.That(
                                vertex.y, Is.EqualTo(1f + positive.offset.y).Within(1e-4f),
                                what + ": the positive cap is drawn where its side is");
                        }

                        foreach (Vector3 vertex in CapVertices(display, 1))
                        {
                            Assert.That(
                                vertex.y, Is.EqualTo(1f + negative.offset.y).Within(1e-4f),
                                what + ": and the negative cap where its own side is");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A body moved, turned, scaled unevenly and mirrored: the caps stay on the face, stay inside the body, and
        /// keep the winding their outward normal asks for. The placement is the snapshot the display was given.
        /// </summary>
        [Test]
        public void CapsOfAMovedTurnedScaledOrMirroredBody_StayOnTheFace()
        {
            (Matrix4x4 placement, string what)[] cases =
            {
                (Matrix4x4.TRS(new Vector3(4f, -1f, 2f), Quaternion.Euler(20f, -55f, 10f), Vector3.one), "moved and turned"),
                (Matrix4x4.TRS(new Vector3(-2f, 3f, 1f), Quaternion.Euler(-15f, 40f, 65f), new Vector3(0.5f, 2.5f, 1.5f)), "non-uniform scale"),
                (Matrix4x4.TRS(new Vector3(1f, 2f, -2f), Quaternion.Euler(0f, 30f, 0f), new Vector3(1f, -2f, 1f)), "mirrored"),
            };

            foreach ((Matrix4x4 placement, string what) in cases)
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    var table = new VpGeometryReferenceTable(storage, 8, 8);
                    LogicalCutLedger ledger = NewLedger();
                    LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                    VpStoredGeometry geometry = Append(storage);

                    Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                    using (new AfterTheFrame(NextFrame, display))
                    {
                        Assert.That(display.TryShow(source, geometry, placement), Is.True);
                        CutOperationId cut = Admit(ledger, source);
                        Prepare(ledger, cut);
                        Assert.That(display.TryBeginFrame(), Is.True);
                        Assert.That(display.CapRecordCount, Is.EqualTo(2), what);

                        AssertCap(display, 0, what + ", positive");
                        AssertCap(display, 1, what + ", negative");

                        // The face each cap lies in is the one its side is clipped by.
                        LogicalCutCapRecord positive = CapOf(display, 0);
                        LogicalCutDisplaySide positiveSide = SideOf(display, 0);
                        Assert.That(positiveSide.side, Is.EqualTo(1f), what + ": the first side is the positive one");
                        AssertVector(positive.offset, positiveSide.offset, what + ": the same separation as the side");

                        // Back in the body's own frame the cap is the square at y = 1, whatever the placement did.
                        Matrix4x4 toLocal = placement.inverse;
                        foreach (Vector3 vertex in CapVertices(display, 0))
                        {
                            Vector3 local = toLocal.MultiplyPoint3x4(vertex - positive.offset);
                            Assert.That(local.y, Is.EqualTo(1f).Within(1e-3f), what + ": on the body's own face");
                            Assert.That(Mathf.Abs(local.x), Is.EqualTo(1f).Within(1e-3f), what + ": and at its side");
                            Assert.That(Mathf.Abs(local.z), Is.EqualTo(1f).Within(1e-3f));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A plane that does not cross the body's box leaves each side's cap empty, and the split is still drawn: the
        /// empty cross-section is a normal answer, not a refusal and not a board of its own. Each side keeps its cap
        /// record (DESIGN D-181: an empty polygon keeps its record), with no vertex, and no triangle of it is issued.
        /// </summary>
        [Test]
        public void APlaneClearOfTheBody_IsSplitWithEmptyCaps()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);

                    // y = 9 is well clear of a body that reaches y = 2.
                    Assert.That(
                        ledger.Admit(source, new float4(0f, 1f, 0f, -9f), true, out CutOperationId cut),
                        Is.EqualTo(LogicalCutAdmission.Admitted));
                    Prepare(ledger, cut);

                    Assert.That(display.TryBeginFrame(), Is.True, "the collection is not refused");
                    Assert.That(display.StateOf(source), Is.EqualTo(LogicalCutDisplayState.ProvisionalSplit));
                    Assert.That(display.SideCount, Is.EqualTo(4), "the sides are drawn as usual");
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "one record per side, kept");
                    for (int i = 0; i < display.CapRecordCount; i++)
                    {
                        Assert.That(display.TryGetCapRecord(i, out LogicalCutCapRecord record), Is.True);
                        Assert.That(record.vertexCount, Is.Zero, "and no vertex is invented for a plane that misses");
                        Assert.That(display.TryGetCapVertex(i, 0, out _), Is.False);
                    }

                    RenderFrame(display);
                    Assert.That(display.TryGetCameraStencil(_camera, out VpStencilPreparation preparation, out _), Is.True);
                    Assert.That(preparation.capsDrawn, Is.Zero, "no triangle of an empty cap is issued");
                    Assert.That(preparation.emptyCaps, Is.EqualTo(display.CapRecordCount), "every cap is empty");
                    Assert.That(preparation.jobs, Is.Zero, "and no empty cap is a job");
                    Assert.That(preparation.volumeCommands, Is.Zero, "so no volume either");
                }
            }
        }

        /// <summary>
        /// A collection that cannot be made keeps the caps it had along with the sides it had — they are adopted
        /// together and never separately — and the same display prepares the new ones once there is room again.
        /// </summary>
        [Test]
        public void ARefusedCollection_KeepsTheCapsItHad_AndTheSameDisplayRecovers()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                // Four display instances: one for each body, one for the first split, and one for a holder outside.
                var table = new VpGeometryReferenceTable(storage, 8, 4);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId first = ledger.AddFragment(new List<float3> { k_lowAnchor });
                LogicalFragmentId second = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry firstGeometry = Append(storage);
                VpStoredGeometry secondGeometry = Append(storage);
                VpStoredGeometry elsewhere = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(first, firstGeometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryShow(second, secondGeometry, Matrix4x4.identity), Is.True);

                    // The first body splits, so there are caps to keep.
                    CutOperationId firstCut = Admit(ledger, first);
                    Prepare(ledger, firstCut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "the caps of the first split");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(3), "one each, and one for the split");

                    LogicalCutCapRecord kept = CapOf(display, 0);
                    Vector3[] keptVertices = CapVertices(display, 0);
                    int settled = display.SettledCollections;
                    Assert.That(kept.published, Is.False, "not published yet");

                    // Something outside takes the table's last display instance.
                    Assert.That(
                        table.TryRegisterGeometryWithDisplayInstance(
                            elsewhere, out VpGeometryReference otherGeometry, out VpDisplayInstanceReference otherInstance),
                        Is.True,
                        "the holder takes the last instance");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(4), "the table is full");

                    // Now the ledger moves on twice over: the first cut is published, and the second body's cut is
                    // ready to split. The split needs an instance there is none of, so the whole collection is refused.
                    Assert.That(
                        ledger.Publish(firstCut, out LogicalFragmentId firstPositive, out _),
                        Is.EqualTo(LogicalCutResultOutcome.Applied));
                    CutOperationId secondCut = Admit(ledger, second);
                    Prepare(ledger, secondCut);
                    NextFrame();

                    Assert.That(display.TryBeginFrame(), Is.False, "the latest state could not be settled");
                    Assert.That(display.SettledCollections, Is.EqualTo(settled), "nothing was settled");
                    Assert.That(display.CapRecordCount, Is.EqualTo(2), "the caps it had are still the caps it has");
                    Assert.That(CapOf(display, 0).published, Is.False, "and they are the ones from before, unchanged");
                    Assert.That(CapOf(display, 0).fragment.IsSet, Is.False);
                    Assert.That(CapOf(display, 0).worldPlane, Is.EqualTo(kept.worldPlane));

                    Vector3[] stillThere = CapVertices(display, 0);
                    Assert.That(stillThere.Length, Is.EqualTo(keptVertices.Length));
                    for (int i = 0; i < stillThere.Length; i++)
                    {
                        AssertVector(stillThere[i], keptVertices[i], "vertex " + i + " did not move");
                    }

                    // What it kept is still what it draws.
                    Assert.That(() => RenderFrame(display), Throws.Nothing, "the snapshot it kept is drawable");
                    Assert.That(display.HasDrawnThisFrame, Is.True);

                    // The holder gives the instance back, and the very same display settles what it could not.
                    Assert.That(table.TryRetireDisplayInstance(otherInstance), Is.True, "the instance comes back");
                    Assert.That(table.TryRetireGeometry(otherGeometry), Is.True);

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True, "the same display settles it now");
                    Assert.That(display.SettledCollections, Is.EqualTo(settled + 1));
                    Assert.That(display.CapRecordCount, Is.EqualTo(4), "both bodies are split and capped now");
                    Assert.That(CapOf(display, 0).published, Is.True, "the first cut's caps caught up");
                    Assert.That(CapOf(display, 0).fragment, Is.EqualTo(firstPositive));
                    Assert.That(CapOf(display, 2).source, Is.EqualTo(second), "and the second body has its own");
                    Assert.That(CapOf(display, 2).operation, Is.EqualTo(secondCut));
                    AssertCap(display, 0, "the first body's positive cap");
                    AssertCap(display, 2, "the second body's positive cap");

                    Vector3[] caughtUp = CapVertices(display, 0);
                    Assert.That(caughtUp.Length, Is.EqualTo(keptVertices.Length), "the face itself never moved");
                    for (int i = 0; i < caughtUp.Length; i++)
                    {
                        AssertVector(caughtUp[i], keptVertices[i], "vertex " + i);
                    }
                }
            }
        }

        /// <summary>
        /// Preparing caps changes nothing outside this display: not the ledger, not the shared incomplete budget, and
        /// not the number of times a geometry was transferred. Nothing is drawn from them either.
        /// </summary>
        [Test]
        public void PreparingCaps_ChangesNoLedgerBudgetOrTransfer()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                var budget = new LogicalCutIncompleteBudget(4);
                var ledger = new LogicalCutLedger(budget);
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True);

                    int vertexTransfers = display.VertexTransfers;
                    int indexTransfers = display.IndexTransfers;

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1), "the cut holds its place");

                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2));
                    Assert.That(
                        display.VertexTransfers, Is.EqualTo(vertexTransfers),
                        "a cap is a cross-section of what is already there");
                    Assert.That(display.IndexTransfers, Is.EqualTo(indexTransfers));
                    Assert.That(
                        budget.IncompleteCutOperationCount, Is.EqualTo(1),
                        "and preparing one advances no cut");

                    Assert.That(
                        ledger.TryGetOperation(cut, out LogicalCutOperation operation), Is.True, "the cut is unchanged");
                    Assert.That(operation.state, Is.EqualTo(LogicalCutOperationState.Admitted));
                    Assert.That(operation.positive.IsSet, Is.False, "no child was published by a display");

                    // Publishing and then ending the display leaves the budget to the ledger, caps or no caps.
                    Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2));
                    Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1), "still the ledger's to give back");
                }

                Assert.That(
                    budget.IncompleteCutOperationCount, Is.EqualTo(1),
                    "and ending a display with caps does not return it either");
            }
        }

        /// <summary>
        /// The cross-section is taken once and then kept: a frame in which nothing changed does not take it again, and
        /// neither does the publication, which changes who the sides belong to and not where the face is. Changing the
        /// separation places the caps again without intersecting anything again, and a body whose face really does
        /// change is the one case that prepares a new polygon.
        /// </summary>
        [Test]
        public void ThePolygon_IsTakenOnceAndKept_UntilAnInputChanges()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry geometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    display.Separation = Separation;
                    Assert.That(display.TryShow(source, geometry, Matrix4x4.identity), Is.True);
                    Assert.That(display.TryBeginFrame(), Is.True, "the whole body settles");
                    Assert.That(display.CapPolygonBuilds, Is.Zero, "a whole body has no face to take one of");

                    CutOperationId cut = Admit(ledger, source);
                    Prepare(ledger, cut);
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(2));
                    Assert.That(display.CapPolygonBuilds, Is.EqualTo(1), "one cross-section for the one body");

                    Vector3[] first = CapVertices(display, 0);

                    // A frame in which nothing changed: settled again, and nothing taken again.
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.SettledCollections, Is.EqualTo(3), "the frame did settle");
                    Assert.That(display.CapPolygonBuilds, Is.EqualTo(1), "and the polygon was reused");
                    Assert.That(display.CapRecordCount, Is.EqualTo(2));

                    Vector3[] again = CapVertices(display, 0);
                    for (int i = 0; i < again.Length; i++)
                    {
                        AssertVector(again[i], first[i], "vertex " + i + " is the one prepared before");
                    }

                    // Publication: the children are named, the face is not taken again.
                    Assert.That(
                        ledger.Publish(cut, out LogicalFragmentId positiveChild, out _),
                        Is.EqualTo(LogicalCutResultOutcome.Applied));
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(CapOf(display, 0).published, Is.True, "the caps caught up with the publication");
                    Assert.That(CapOf(display, 0).fragment, Is.EqualTo(positiveChild));
                    Assert.That(
                        display.CapPolygonBuilds, Is.EqualTo(1),
                        "which is a change of who, not of where: no new cross-section");

                    Vector3[] published = CapVertices(display, 0);
                    for (int i = 0; i < published.Length; i++)
                    {
                        AssertVector(published[i], first[i], "vertex " + i + " did not move at publication");
                    }

                    // A different separation places the caps again, and intersects nothing again.
                    display.Separation = Separation * 3f;
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(
                        display.CapPolygonBuilds, Is.EqualTo(1),
                        "the separation moves a cap, it does not re-cut the box");

                    LogicalCutCapRecord moved = CapOf(display, 0);
                    AssertVector(
                        moved.offset, new Vector3(0f, Separation * 3f, 0f), "the cap took the new separation");
                    Vector3[] placed = CapVertices(display, 0);
                    for (int i = 0; i < placed.Length; i++)
                    {
                        AssertVector(
                            placed[i], first[i] + new Vector3(0f, Separation * 2f, 0f),
                            "vertex " + i + " is the same face, placed further apart");
                    }

                    AssertCap(display, 0, "after the separation changed");
                }
            }
        }

        /// <summary>
        /// A second body, with its own face, prepares its own cross-section: the keeping is per body and one body's
        /// prepared polygon is never another's.
        /// </summary>
        [Test]
        public void EachBody_PreparesItsOwnPolygon()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId first = ledger.AddFragment(new List<float3> { k_lowAnchor });
                LogicalFragmentId second = ledger.AddFragment(new List<float3> { k_lowAnchor });
                VpStoredGeometry firstGeometry = Append(storage);
                VpStoredGeometry secondGeometry = Append(storage);

                Assert.That(TryCreate(storage, table, ledger, out VpLogicalCutDisplay display), Is.True, "create");
                using (new AfterTheFrame(NextFrame, display))
                {
                    Assert.That(display.TryShow(first, firstGeometry, Matrix4x4.identity), Is.True);
                    CutOperationId firstCut = Admit(ledger, first);
                    Prepare(ledger, firstCut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapPolygonBuilds, Is.EqualTo(1), "the first body's cross-section");

                    // The second body is placed differently, so its face in world is a different one.
                    NextFrame();
                    Matrix4x4 placement = Matrix4x4.TRS(
                        new Vector3(5f, 0f, 0f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);
                    Assert.That(display.TryShow(second, secondGeometry, placement), Is.True);
                    CutOperationId secondCut = Admit(ledger, second);
                    Prepare(ledger, secondCut);
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.EqualTo(4), "both bodies are capped");
                    Assert.That(
                        display.CapPolygonBuilds, Is.EqualTo(2),
                        "one cross-section each, and the first was not taken again");

                    AssertCap(display, 0, "the first body");
                    AssertCap(display, 2, "the second body");
                    Assert.That(CapOf(display, 2).source, Is.EqualTo(second));

                    // And a further quiet frame takes neither again.
                    NextFrame();
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapPolygonBuilds, Is.EqualTo(2), "nothing changed, nothing was taken again");
                }
            }
        }
    }
}
