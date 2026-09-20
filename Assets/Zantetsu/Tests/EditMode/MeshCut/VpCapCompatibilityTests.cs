using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The stencil compatibility grouping (DESIGN 5.6, T-067) as a CPU classification.
    /// <para>
    /// No test takes a group number or a group count as the answer. Each checks what a grouping must be: every
    /// target in exactly one group, no group empty, every two members of a group compatible — with "compatible" read
    /// off the layout the test built, not off the function — and, where the layout says two targets are under the
    /// same conditions and nothing else competes, that they are together.
    /// </para>
    /// <para>
    /// Grouping several targets under one face is shown on written-out inputs. In the display as it is today one
    /// body is under one cut and every operation has one source, so two real bodies never share a face; the tests
    /// that use real records either show that, or show one fragment of one ledger drawn by two displays, which is a
    /// test arrangement and not a product path.
    /// </para>
    /// </summary>
    public class VpCapCompatibilityTests
    {
        /// <summary>Test values only: no product epsilon exists for either (DESIGN O-034).</summary>
        private const float PlaneEpsilon = 0.125f;

        private const int BodyMaterial = 7;

        private static readonly Vector4 k_p = new Vector4(0f, 1f, 0f, -1f);
        private static readonly Vector4 k_q = new Vector4(1f, 0f, 0f, -2f);

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private int _frame = 1;

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

        // ----- written-out inputs ---------------------------------------------------------------------------------

        private static LogicalCutLedger NewLedger()
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
        }

        private static VpCapFace Face(LogicalCutLedger scope, int operation)
        {
            return new VpCapFace(scope, new CutOperationId(operation));
        }

        private static VpCapConstraint C(VpCapFace face, float side, Vector4 plane)
        {
            return new VpCapConstraint(face, side, plane);
        }

        private static VpCapCompatibilityTarget T(params VpCapConstraint[] constraints)
        {
            return new VpCapCompatibilityTarget(constraints);
        }

        /// <summary>
        /// Groups <paramref name="targets"/> and checks the partition against <paramref name="compatible"/>, the
        /// relation the test's own layout says holds. Answers each target's group for further checks.
        /// </summary>
        private static int[] GroupAndCheck(VpCapCompatibilityTarget[] targets, bool[,] compatible, string what)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                for (int j = 0; j < targets.Length; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    Assert.That(
                        VpCapCompatibility.AreCompatible(targets[i], targets[j], PlaneEpsilon),
                        Is.EqualTo(compatible[i, j]),
                        what + ": targets " + i + " and " + j);
                }
            }

            var groups = new int[targets.Length];
            for (int i = 0; i < groups.Length; i++)
            {
                groups[i] = -7;
            }

            int count = VpCapCompatibility.Classify(targets, PlaneEpsilon, groups);
            Assert.That(count, Is.InRange(1, targets.Length), what + ": between one group and one per target");

            var members = new int[count];
            for (int i = 0; i < targets.Length; i++)
            {
                Assert.That(groups[i], Is.InRange(0, count - 1), what + ": target " + i + " is in a group");
                members[groups[i]]++;
                for (int j = 0; j < i; j++)
                {
                    if (groups[i] == groups[j])
                    {
                        Assert.That(compatible[i, j], Is.True, what + ": targets " + j + " and " + i + " share a group");
                    }
                }
            }

            for (int g = 0; g < count; g++)
            {
                Assert.That(members[g], Is.GreaterThan(0), what + ": group " + g + " is not empty");
            }

            return groups;
        }

        private static bool[,] Relation(int n, params (int a, int b)[] compatiblePairs)
        {
            var relation = new bool[n, n];
            foreach ((int a, int b) in compatiblePairs)
            {
                relation[a, b] = true;
                relation[b, a] = true;
            }

            return relation;
        }

        /// <summary>
        /// The same faces, sides and planes, given in different orders, are one group. A third target under
        /// the same conditions joins them.
        /// </summary>
        [Test]
        public void TheSameConditions_InAnyOrder_ShareAGroup()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);
            VpCapFace b = Face(ledger, 2);

            VpCapCompatibilityTarget[] targets =
            {
                T(C(a, 1f, k_p), C(b, -1f, k_q)),
                T(C(b, -1f, k_q), C(a, 1f, k_p)),
                T(C(a, 1f, k_p), C(b, -1f, k_q)),
            };

            int[] groups = GroupAndCheck(targets, Relation(3, (0, 1), (0, 2), (1, 2)), "same conditions");
            Assert.That(groups[1], Is.EqualTo(groups[0]), "the order the faces are listed in is not a condition");
            Assert.That(groups[2], Is.EqualTo(groups[0]));
        }

        /// <summary>
        /// Against one target, each of these differs in one thing and is apart from it: the other side of the face,
        /// one more boundary, and a different face in place of one.
        /// </summary>
        [Test]
        public void ADifferentSide_ExtraBoundary_OrFace_IsApart()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);
            VpCapFace b = Face(ledger, 2);

            VpCapCompatibilityTarget[] targets =
            {
                T(C(a, 1f, k_p)),
                T(C(a, -1f, k_p)),
                T(C(a, 1f, k_p), C(b, 1f, k_q)),
                T(C(b, 1f, k_p)),
            };

            int[] groups = GroupAndCheck(targets, Relation(4), "one difference each");
            for (int i = 1; i < targets.Length; i++)
            {
                Assert.That(groups[i], Is.Not.EqualTo(groups[0]), "target " + i + " differs from target 0 in one thing");
            }

            // The extra boundary the other way round: the target with two faces is not a target with one.
            Assert.That(
                VpCapCompatibility.AreCompatible(targets[2], targets[0], PlaneEpsilon), Is.False,
                "only one of them has the second boundary");
        }

        /// <summary>
        /// One face and the same side, but the face moved along its normal or turned: its current world plane
        /// is not the one the other target is under, so they are apart.
        /// </summary>
        [Test]
        public void TheSameFace_MovedOrTurned_IsApart()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);

            VpCapCompatibilityTarget[] targets =
            {
                T(C(a, 1f, k_p)),
                T(C(a, 1f, new Vector4(0f, 1f, 0f, -1.5f))),
                T(C(a, 1f, new Vector4(0f, 0f, 1f, -1f))),
            };

            int[] groups = GroupAndCheck(targets, Relation(3), "moved and turned");
            Assert.That(groups[1], Is.Not.EqualTo(groups[0]), "moved half a unit along its normal");
            Assert.That(groups[2], Is.Not.EqualTo(groups[0]), "turned a quarter");
        }

        /// <summary>
        /// Exactly at the epsilon is within it for the plane; twice the epsilon is not.
        /// </summary>
        [Test]
        public void TheEpsilon_IncludesItsEdge_AndNotBeyond()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);

            VpCapCompatibilityTarget[] planes =
            {
                T(C(a, 1f, new Vector4(0f, 1f, 0f, -1f))),
                T(C(a, 1f, new Vector4(0f, 1f, 0f, -1.125f))),
            };
            int[] together = GroupAndCheck(planes, Relation(2, (0, 1)), "d apart by exactly the epsilon");
            Assert.That(together[1], Is.EqualTo(together[0]));

            VpCapCompatibilityTarget[] farPlanes =
            {
                T(C(a, 1f, new Vector4(0f, 1f, 0f, -1f))),
                T(C(a, 1f, new Vector4(0f, 1f, 0f, -1.25f))),
            };
            GroupAndCheck(farPlanes, Relation(2), "d apart by twice the epsilon");
        }

        /// <summary>
        /// Three targets on one face whose planes are 0, 1 and 2 epsilons along: the first two are close and the last
        /// two are close, but the first and the last are not. In every order they can be given in, the first and the
        /// last never share a group — closeness is not carried through the middle one.
        /// </summary>
        [Test]
        public void CloseToCloseIsNotClose_InAnyOrder()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);
            VpCapCompatibilityTarget near = T(C(a, 1f, new Vector4(0f, 1f, 0f, 0f)));
            VpCapCompatibilityTarget middle = T(C(a, 1f, new Vector4(0f, 1f, 0f, -0.125f)));
            VpCapCompatibilityTarget far = T(C(a, 1f, new Vector4(0f, 1f, 0f, -0.25f)));

            int[][] orders =
            {
                new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 },
            };
            VpCapCompatibilityTarget[] named = { near, middle, far };
            foreach (int[] order in orders)
            {
                var targets = new VpCapCompatibilityTarget[3];
                for (int k = 0; k < 3; k++)
                {
                    targets[k] = named[order[k]];
                }

                int nearAt = Array.IndexOf(order, 0);
                int middleAt = Array.IndexOf(order, 1);
                int farAt = Array.IndexOf(order, 2);

                string what = "order " + string.Join(",", order);
                int[] groups = GroupAndCheck(targets, Relation(3, (nearAt, middleAt), (middleAt, farAt)), what);
                Assert.That(groups[nearAt], Is.Not.EqualTo(groups[farAt]), what + ": the two ends are apart");
            }
        }

        /// <summary>
        /// The same operation number, issued by two ledgers, is two faces: identical planes and sides do not
        /// bring them together. Issued by one ledger, it is one face.
        /// </summary>
        [Test]
        public void TheSameOperationNumber_FromAnotherLedger_IsAnotherFace()
        {
            LogicalCutLedger first = NewLedger();
            LogicalCutLedger second = NewLedger();

            VpCapCompatibilityTarget[] targets =
            {
                T(C(Face(first, 1), 1f, k_p)),
                T(C(Face(second, 1), 1f, k_p)),
                T(C(Face(first, 1), 1f, k_p)),
            };

            int[] groups = GroupAndCheck(targets, Relation(3, (0, 2)), "operation 1 of two ledgers");
            Assert.That(groups[1], Is.Not.EqualTo(groups[0]), "another ledger's operation 1");
            Assert.That(groups[2], Is.EqualTo(groups[0]), "the same ledger's operation 1");
        }

        /// <summary>
        /// Two targets that differ only in the second face's side are apart, whatever is seen of that face: the
        /// classification is given every condition a target is under and has no input for visibility, so a face
        /// whose cap the frustum or facing test left out still separates them.
        /// </summary>
        [Test]
        public void AFaceWhoseCapIsNotSeen_StillSeparates()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);
            VpCapFace b = Face(ledger, 2);

            VpCapCompatibilityTarget[] targets =
            {
                T(C(a, 1f, k_p), C(b, 1f, k_q)),
                T(C(a, 1f, k_p), C(b, -1f, k_q)),
            };

            int[] groups = GroupAndCheck(targets, Relation(2), "the second face's side differs");
            Assert.That(groups[1], Is.Not.EqualTo(groups[0]));
        }

        /// <summary>
        /// Each call is its own: the same array, reused, is written afresh. Two targets together, then apart once one
        /// is given a moved plane, then together again when it is given back.
        /// </summary>
        [Test]
        public void TheNextCall_IsClassifiedFromItsOwnInput()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);
            var groups = new int[2];

            VpCapCompatibilityTarget[] same = { T(C(a, 1f, k_p)), T(C(a, 1f, k_p)) };
            VpCapCompatibilityTarget[] moved =
            {
                T(C(a, 1f, k_p)), T(C(a, 1f, new Vector4(0f, 1f, 0f, -2f))),
            };

            VpCapCompatibility.Classify(same, PlaneEpsilon, groups);
            Assert.That(groups[1], Is.EqualTo(groups[0]), "first: together");

            VpCapCompatibility.Classify(moved, PlaneEpsilon, groups);
            Assert.That(groups[1], Is.Not.EqualTo(groups[0]), "next: one moved, apart");

            VpCapCompatibility.Classify(same, PlaneEpsilon, groups);
            Assert.That(groups[1], Is.EqualTo(groups[0]), "then back: together again");
        }

        /// <summary>
        /// A target made from a list reads that list itself, not a copy: a condition the caller changes, adds or takes
        /// away after the target was made is what the next judgement and grouping see, whether the list is an array or
        /// a <c>List</c>.
        /// </summary>
        [Test]
        public void ATargetMadeFromAList_ReadsThatListAsItIsNow()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);
            VpCapFace b = Face(ledger, 2);
            var mine = new List<VpCapConstraint> { C(a, 1f, k_p) };
            VpCapConstraint[] theirs = { C(a, 1f, k_p) };
            var first = new VpCapCompatibilityTarget(mine);
            var second = new VpCapCompatibilityTarget(theirs);
            var targets = new[] { first, second };
            var groups = new int[2];

            Assert.That(VpCapCompatibility.AreCompatible(first, second, PlaneEpsilon), Is.True, "the layout: the same condition");
            Assert.That(VpCapCompatibility.Classify(targets, PlaneEpsilon, groups), Is.EqualTo(1));

            theirs[0] = C(a, 1f, new Vector4(0f, 1f, 0f, -2f));
            Assert.That(VpCapCompatibility.AreCompatible(first, second, PlaneEpsilon), Is.False, "the array's plane was moved afterwards");
            Assert.That(VpCapCompatibility.Classify(targets, PlaneEpsilon, groups), Is.EqualTo(2), "and the grouping sees it");

            theirs[0] = C(a, 1f, k_p);
            mine.Add(C(b, -1f, k_q));
            Assert.That(first.constraints.Count, Is.EqualTo(2), "the list grew afterwards, and the target sees two conditions");
            Assert.That(VpCapCompatibility.AreCompatible(first, second, PlaneEpsilon), Is.False, "an extra boundary, added afterwards");

            mine.RemoveAt(1);
            Assert.That(VpCapCompatibility.AreCompatible(first, second, PlaneEpsilon), Is.True, "taken away again");
            Assert.That(VpCapCompatibility.Classify(targets, PlaneEpsilon, groups), Is.EqualTo(1));

            mine.Clear();
            Assert.Throws<System.ArgumentException>(
                () => VpCapCompatibility.AreCompatible(first, second, PlaneEpsilon),
                "emptied afterwards: a target under no condition is refused, as it would be if made empty");
        }

        /// <summary>
        /// A value that is not finite is near nothing, so that target stands alone; malformed inputs and epsilons are
        /// refused.
        /// </summary>
        [Test]
        public void NonFinite_StandsAlone_AndMalformedInputIsRefused()
        {
            LogicalCutLedger ledger = NewLedger();
            VpCapFace a = Face(ledger, 1);

            VpCapCompatibilityTarget[] targets =
            {
                T(C(a, 1f, k_p)),
                T(C(a, 1f, new Vector4(0f, 1f, 0f, float.NaN))),
                T(C(a, 1f, k_p)),
            };
            int[] groups = GroupAndCheck(targets, Relation(3, (0, 2)), "not finite");
            Assert.That(groups[1], Is.Not.EqualTo(groups[0]), "the one whose plane is not finite stands alone");

            var slots = new int[4];
            Assert.Throws<ArgumentException>(
                () => VpCapCompatibility.Classify(new[] { T(C(a, 0f, k_p)) }, PlaneEpsilon, slots),
                "a side of zero");
            Assert.Throws<ArgumentException>(
                () => VpCapCompatibility.Classify(new[] { T(C(a, 1f, k_p), C(a, 1f, k_p)) }, PlaneEpsilon, slots),
                "one face named twice");
            Assert.Throws<ArgumentException>(
                () => VpCapCompatibility.Classify(new[] { T() }, PlaneEpsilon, slots),
                "no condition");
            Assert.Throws<ArgumentException>(
                () => VpCapCompatibility.Classify(new[] { T(C(Face(null, 1), 1f, k_p)) }, PlaneEpsilon, slots),
                "a face with no ledger");
            Assert.Throws<ArgumentException>(
                () => VpCapCompatibility.Classify(targets, PlaneEpsilon, new int[targets.Length - 1]),
                "too few group slots");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VpCapCompatibility.Classify(targets, -1f, slots), "negative epsilon");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VpCapCompatibility.Classify(targets, float.NaN, slots), "NaN epsilon");
            Assert.Throws<ArgumentNullException>(
                () => VpCapCompatibility.Classify(null, PlaneEpsilon, slots));
        }

        // ----- the current single-cut display ---------------------------------------------------------------------

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, 1.0f), new float3(-1.0f, 0.0f, 1.0f),
            new float3(-1.0f, 2.0f, -1.0f), new float3(1.0f, 2.0f, -1.0f), new float3(1.0f, 2.0f, 1.0f), new float3(-1.0f, 2.0f, 1.0f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
            new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

        /// <summary>The cube's plane y = 1; the anchor below it fixes the negative side.</summary>
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -1f);
        private static readonly float3 k_lowAnchor = new float3(0f, 0.2f, 0f);

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }

        /// <summary>The cube, wound outward, or every triangle reversed so the whole body is inside out.</summary>
        private static VpStoredGeometry AppendCube(VpCpuGeometryStorage storage, bool insideOut)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            foreach (int[] c in k_faces)
            {
                float3 n = math.normalize(math.cross(
                    k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                uint b = (uint)vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new VpRenderVertex
                    {
                        position = k_controlPoints[c[k]], normal = insideOut ? -n : n, uv0 = new float2(0.5f, 0.5f),
                    });
                    topology.Add(c[k]);
                }

                indices.AddRange(insideOut
                    ? new[] { b, b + 2, b + 1, b, b + 3, b + 2 }
                    : new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_controlPoints.Length,
                    new[] { new VpGeometrySubmesh(0, indices.Count, BodyMaterial) }, out VpStoredGeometry geometry),
                Is.True,
                "append the cube");
            return geometry;
        }

        private VpLogicalCutDisplay Show(
            VpCpuGeometryStorage storage, LogicalCutLedger ledger, LogicalFragmentId fragment, Matrix4x4 placement,
            bool insideOut, Color colour)
        {
            var table = new VpGeometryReferenceTable(storage, 8, 8);
            VpStoredGeometry geometry = AppendCube(storage, insideOut);
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = "compatibility body", color = colour };
            _objects.Add(material);

            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    storage, table, ledger, new Dictionary<int, Material> { { BodyMaterial, material } }, null, null, 16, 16,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(),
                    () => _frame, out VpLogicalCutDisplay display),
                Is.True,
                "create a display");
            Assert.That(display.TryShow(fragment, geometry, placement), Is.True, "show the cube");
            return display;
        }

        private static CutOperationId AdmitAndPrepare(LogicalCutLedger ledger, LogicalFragmentId fragment)
        {
            Assert.That(ledger.Admit(fragment, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return cut;
        }

        private static int CapIndexOf(VpLogicalCutDisplay display, float side)
        {
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                Assert.That(display.TryGetCapRecord(i, out LogicalCutCapRecord record), Is.True);
                if (record.side == side)
                {
                    return i;
                }
            }

            Assert.Fail("no cap on side " + side);
            return -1;
        }

        private static VpCapCompatibilityTarget TargetOf(VpLogicalCutDisplay display, LogicalCutLedger ledger, float side)
        {
            return VpDisplayRecordTargets.Conditions(display, ledger, CapIndexOf(display, side));
        }

        /// <summary>
        /// A real display's cap records give the one condition of a single-cut body: the cap's operation under the
        /// display's ledger, its side and its settled world plane. The face stays the same face from pending
        /// to published. A cap the visibility test excludes keeps its record, so its condition is still given. A whole
        /// body has no record.
        /// </summary>
        [Test]
        public void TheDisplaysCapRecords_GiveTheBodysOneCondition_PendingOrPublished()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
                using (VpLogicalCutDisplay display = Show(storage, ledger, source, Matrix4x4.identity, false, Color.white))
                {
                    Assert.That(display.TryBeginFrame(), Is.True);
                    Assert.That(display.CapRecordCount, Is.Zero, "a whole body has no cap");

                    CutOperationId cut = AdmitAndPrepare(ledger, source);
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True);

                    VpCapCompatibilityTarget pending = TargetOf(display, ledger, 1f);
                    Assert.That(pending.constraints.Count, Is.EqualTo(1), "one body under one cut");
                    VpCapConstraint only = pending.constraints[0];
                    Assert.That(only.face, Is.EqualTo(new VpCapFace(ledger, cut)), "the operation, under this ledger");
                    Assert.That(only.side, Is.EqualTo(1f));
                    Assert.That((only.worldPlane - new Vector4(0f, 1f, 0f, -1f)).magnitude, Is.LessThan(1e-5f), "y = 1 at the identity");

                    VpCapCompatibilityTarget negative = TargetOf(display, ledger, -1f);
                    Assert.That(
                        VpCapCompatibility.AreCompatible(pending, negative, PlaneEpsilon), Is.False,
                        "the two sides of one cut are apart");

                    // Excluded by the visibility test: an eye above the downward positive cap.
                    Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                        * Matrix4x4.TRS(new Vector3(0f, 3f, -5f), Quaternion.identity, Vector3.one).inverse;
                    var eye = new VpCapEye(new Vector3(0f, 3f, -5f), Matrix4x4.Perspective(90f, 1f, 0.1f, 100f) * view);
                    Assert.That(
                        VpCapVisibility.TryClassifyMono(display, CapIndexOf(display, 1f), eye, 0.01f, out VpCapVisibilityVerdict seen),
                        Is.True);
                    Assert.That(seen.Keep, Is.False, "this cap is not seen from above");
                    Assert.That(
                        TargetOf(display, ledger, 1f).constraints.Count, Is.EqualTo(1), "and its condition is still given");

                    Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True);
                    VpCapCompatibilityTarget published = TargetOf(display, ledger, 1f);
                    Assert.That(published.constraints[0].face, Is.EqualTo(only.face), "publication does not change the face");
                    Assert.That(
                        VpCapCompatibility.AreCompatible(pending, published, PlaneEpsilon), Is.True,
                        "pending and published are under the same condition");
                }
            }
        }

        /// <summary>
        /// Two ledgers, each with one body and its first cut: both operations are numbered alike, the planes, sides
        /// are the same, and still no target of one is compatible with a target of the other. This is
        /// also the real path's limit: bodies under different operations never share a face.
        /// </summary>
        [Test]
        public void RealCapsOfTwoLedgers_WithTheSameOperationNumber_AreApart()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpCpuGeometryStorage otherStorage = NewStorage())
            {
                LogicalCutLedger first = NewLedger();
                LogicalCutLedger second = NewLedger();
                LogicalFragmentId firstBody = first.AddFragment(new List<float3> { k_lowAnchor });
                LogicalFragmentId secondBody = second.AddFragment(new List<float3> { k_lowAnchor });
                using (VpLogicalCutDisplay one = Show(storage, first, firstBody, Matrix4x4.identity, false, Color.white))
                using (VpLogicalCutDisplay two = Show(otherStorage, second, secondBody, Matrix4x4.identity, false, Color.white))
                {
                    CutOperationId firstCut = AdmitAndPrepare(first, firstBody);
                    CutOperationId secondCut = AdmitAndPrepare(second, secondBody);
                    Assert.That(firstCut.value, Is.EqualTo(secondCut.value), "the layout: both ledgers issued the same number");
                    Assert.That(one.TryBeginFrame(), Is.True);
                    Assert.That(two.TryBeginFrame(), Is.True);

                    VpCapCompatibilityTarget[] targets = { TargetOf(one, first, 1f), TargetOf(two, second, 1f) };
                    Assert.That(
                        (targets[0].constraints[0].worldPlane - targets[1].constraints[0].worldPlane).magnitude, Is.LessThan(1e-5f),
                        "the layout: the same plane");
                    int[] groups = GroupAndCheck(targets, Relation(2), "two ledgers");
                    Assert.That(groups[1], Is.Not.EqualTo(groups[0]));
                }
            }
        }

        /// <summary>
        /// A test arrangement, not a product path: one fragment of one ledger drawn by two displays, so both
        /// displays' caps are under the same face. The second display's cube is inside out and drawn in another colour:
        /// the same side still shares a group, since orientation and colour are not conditions. Drawn by a third
        /// display placed half a unit higher, the same face's current plane has moved, and that side is apart.
        /// </summary>
        [Test]
        public void OneFaceDrawnTwice_IgnoresOrientationAndColour_ButNotAMovedPlane()
        {
            // A storage takes one reference table, so each display has a storage of its own.
            using (VpCpuGeometryStorage plainStorage = NewStorage())
            using (VpCpuGeometryStorage reversedStorage = NewStorage())
            using (VpCpuGeometryStorage raisedStorage = NewStorage())
            {
                LogicalCutLedger ledger = NewLedger();
                LogicalFragmentId body = ledger.AddFragment(new List<float3> { k_lowAnchor });
                using (VpLogicalCutDisplay plain = Show(plainStorage, ledger, body, Matrix4x4.identity, false, Color.white))
                using (VpLogicalCutDisplay reversed = Show(reversedStorage, ledger, body, Matrix4x4.identity, true, Color.red))
                using (VpLogicalCutDisplay raised = Show(
                           raisedStorage, ledger, body, Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f)), false, Color.white))
                {
                    AdmitAndPrepare(ledger, body);
                    Assert.That(plain.TryBeginFrame(), Is.True);
                    Assert.That(reversed.TryBeginFrame(), Is.True);
                    Assert.That(raised.TryBeginFrame(), Is.True);

                    VpCapCompatibilityTarget[] targets =
                    {
                        TargetOf(plain, ledger, 1f), TargetOf(reversed, ledger, 1f), TargetOf(raised, ledger, 1f),
                        TargetOf(plain, ledger, -1f),
                    };
                    int[] groups = GroupAndCheck(targets, Relation(4, (0, 1)), "one face drawn three times");
                    Assert.That(groups[1], Is.EqualTo(groups[0]), "inside out and red, the same conditions");
                    Assert.That(groups[2], Is.Not.EqualTo(groups[0]), "the same face half a unit higher");
                    Assert.That(groups[3], Is.Not.EqualTo(groups[0]), "the other side");
                }
            }
        }
    }
}
