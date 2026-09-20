using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The commit itself, through <see cref="VpLogicalCutDisplay.TryCommitCut"/>, for a display whose fragments
    /// follow placements of their own: which separation a commit takes into a registration, in which frame, and what
    /// the living descendants of the committed fragment are drawn at afterwards.
    /// <para>
    /// The expected values are worked out here from the placements the test chose. A commit of a side that has since
    /// been cut again is the case that matters: the separation it takes in belongs to the geometry, not to a fragment
    /// that is no longer there, so it has to reach the fragments that are.
    /// </para>
    /// </summary>
    public class VpFragmentPlacementCommitTests
    {
        private const float Tolerance = 2e-3f;
        private const float Separation = 0.5f;
        private const int BodyMaterial = 0;

        /// <summary>
        /// The geometry's local frame inside the frame a fragment follows: never the same frame. The offset is small
        /// enough that the adopted plane, carried into the geometry's coordinates, still crosses the cube: the plane
        /// the ledger holds and the plane the kernel is given are the same plane and different numbers.
        /// </summary>
        private static readonly Matrix4x4 k_geometryLocalToOwner = Matrix4x4.Translate(new Vector3(0f, 0.4f, 0f));
        private static readonly Matrix4x4 k_lineageToGeometryLocal = Matrix4x4.Translate(new Vector3(0f, -0.4f, 0f));

        private readonly List<Object> _objects = new List<Object>();
        private int _frame = 1;

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

        private sealed class Placements : IVpFragmentPlacement
        {
            internal readonly Dictionary<LogicalFragmentId, Matrix4x4> of = new Dictionary<LogicalFragmentId, Matrix4x4>();
            internal readonly HashSet<LogicalFragmentId> missing = new HashSet<LogicalFragmentId>();

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(
                LogicalFragmentId fragment, out Matrix4x4 geometryLocalToWorld)
            {
                if (missing.Contains(fragment))
                {
                    geometryLocalToWorld = Matrix4x4.identity;
                    return VpFragmentPlacementKind.Missing;
                }

                if (of.TryGetValue(fragment, out geometryLocalToWorld))
                {
                    return VpFragmentPlacementKind.Following;
                }

                // Whatever this test did not place follows nothing: an explicit answer, not a missing one.
                geometryLocalToWorld = Matrix4x4.identity;
                return VpFragmentPlacementKind.Static;
            }
        }

        private sealed class Scene
        {
            internal VpCpuGeometryStorage storage;
            internal VpGeometryReferenceTable table;
            internal LogicalCutLedger ledger;
            internal VpLogicalCutDisplay display;
            internal Placements placements;
            internal VpStoredGeometry geometry;
        }

        private T Track<T>(T tracked)
            where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private Scene NewScene()
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64)),
                placements = new Placements(),
            };
            _objects.Add(null);
            scene.table = new VpGeometryReferenceTable(scene.storage, 16, 32);
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null, "the VP unlit shader");
            var body = Track(new Material(shader) { name = "body" });
            var materials = new Dictionary<int, Material> { { BodyMaterial, body } };
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    scene.storage, scene.table, scene.ledger, materials, null, null, 16, 16,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates,
                    VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(4), () => _frame, out scene.display),
                Is.True,
                "create the display");
            scene.display.Separation = Separation;
            scene.display.Placement = scene.placements;
            scene.geometry = AppendCube(scene.storage);
            return scene;
        }

        private void Dispose(Scene scene)
        {
            scene.display?.Dispose();
            scene.storage?.Dispose();
        }

        private void Collect(Scene scene)
        {
            _frame++;
            Assert.That(scene.display.TryBeginFrame(), Is.True, "collected");
        }

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, 1f, -1f), new float3(-1f, 1f, -1f),
            new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 2, 3, 7, 6 }, new[] { 1, 2, 6, 5 }, new[] { 0, 4, 7, 3 },
        };

        private static VpStoredGeometry AppendCube(VpCpuGeometryStorage storage)
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
                        position = k_controlPoints[c[k]], normal = n, uv0 = new float2(0.5f, 0.5f),
                    });
                    topology.Add(c[k]);
                }

                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_controlPoints.Length,
                    new[] { new VpGeometrySubmesh(0, indices.Count, BodyMaterial) }, out VpStoredGeometry geometry, out _),
                Is.True,
                "append the cube as something that may be cut");
            return geometry;
        }

        private static Matrix4x4 Owner(Vector3 position, float degreesAboutZ)
        {
            return Matrix4x4.TRS(position, Quaternion.AngleAxis(degreesAboutZ, Vector3.forward), Vector3.one);
        }

        private static CutOperationId Admit(Scene scene, LogicalFragmentId source, float4 plane, params float3[] anchors)
        {
            Assert.That(scene.ledger.Admit(source, plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(scene.ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(anchors, Is.Not.Null);
            return cut;
        }

        private static (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) Cut(
            Scene scene, LogicalFragmentId source, float4 plane)
        {
            CutOperationId cut = Admit(scene, source, plane);
            Assert.That(
                scene.ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative),
                Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (cut, positive, negative);
        }

        /// <summary>
        /// A real split of the shown geometry: both sides produced, with a cap between them.
        /// <para>
        /// The adopted plane is in the lineage's own frame, and the kernel cuts in the geometry's: the one is carried
        /// to the other by the registration's mapping before the cut, and the ledger keeps its own unchanged. They are
        /// the same plane and are not the same numbers, and a cut given the wrong one cuts somewhere else.
        /// </para>
        /// </summary>
        private static bool CommitProduced(
            Scene scene, LogicalFragmentId source, CutOperationId cut, float4 plane, LogicalFragmentId positive,
            LogicalFragmentId negative)
        {
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(plane, k_lineageToGeometryLocal, out float4 geometryLocalPlane),
                Is.True,
                "the adopted plane, in the geometry's own coordinates");
            Assert.That(
                VpStorageCutInput.TryAcquire(scene.storage, scene.geometry, out VpStorageCutInput input), Is.True,
                "read the geometry");
            using (input)
            {
                Assert.That(
                    VpStorageCut.TryExecute(scene.storage, input, geometryLocalPlane, out VpStorageCutResult result),
                    Is.True,
                    "cut it");
                Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "both sides produced");
                return scene.display.TryCommitCut(
                    source, cut, new Vector4(plane.x, plane.y, plane.z, plane.w), positive, in result.positive, negative,
                    in result.negative, result.kernel.capTriangles);
            }
        }

        /// <summary>A commit whose positive side is the input itself and whose negative side has nothing.</summary>
        private static bool CommitBorrowingPositive(
            Scene scene, LogicalFragmentId source, CutOperationId cut, float4 plane, LogicalFragmentId positive,
            LogicalFragmentId negative)
        {
            var borrowed = new VpStorageCutSide(VpStorageCutSideKind.ReusesInput, scene.geometry);
            var empty = new VpStorageCutSide(VpStorageCutSideKind.Empty, default);
            return scene.display.TryCommitCut(
                source, cut, new Vector4(plane.x, plane.y, plane.z, plane.w), positive, in borrowed, negative, in empty, 0);
        }

        private static Vector3 Drawn(Scene scene, LogicalFragmentId fragment, Vector3 local)
        {
            for (int r = 0; r < scene.display.RenderFragmentCount; r++)
            {
                Assert.That(scene.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    return rf.geometryLocalToWorld.MultiplyPoint3x4(local) + rf.offset;
                }
            }

            Assert.Fail("nothing is drawn for that fragment");
            return default;
        }

        private static Vector3 Offset(Scene scene, LogicalFragmentId fragment)
        {
            for (int r = 0; r < scene.display.RenderFragmentCount; r++)
            {
                Assert.That(scene.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.root == fragment)
                {
                    return rf.offset;
                }
            }

            Assert.Fail("nothing is drawn for that fragment");
            return default;
        }

        private static void Same(Vector3 expected, Vector3 actual, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(Tolerance), what + ".x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(Tolerance), what + ".y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(Tolerance), what + ".z");
        }

        private static readonly Vector3[] k_points =
        {
            Vector3.zero, new Vector3(1f, 1f, 1f), new Vector3(-1f, 0.5f, -0.25f), new Vector3(0.75f, -1f, 1f),
        };

        // ----- a commit of a child that has turned ----------------------------------------------------------------------

        /// <summary>
        /// The child has turned since it was published, so the separation it is drawn with runs along its own current
        /// normal. The commit takes that separation in, and the shape does not move: it would if the commit took the
        /// direction the registration was made with.
        /// </summary>
        [Test]
        public void ACommitOfATurnedChild_TakesTheSeparationItIsDrawnWith()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(scene.display.TryShow(root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>()), Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var plane = new float4(0f, 1f, 0f, 0f);
                var (cut, positive, negative) = Cut(scene, root, plane);

                // The positive child turns a quarter turn about z and moves: its plane's normal is -x now.
                Matrix4x4 turned = Owner(new Vector3(3f, 0f, 0f), 90f);
                scene.placements.of[positive] = turned * k_geometryLocalToOwner;
                scene.placements.of[negative] = turned * k_geometryLocalToOwner;
                Collect(scene);

                Same(new Vector3(-0.5f, 0f, 0f), Offset(scene, positive), "the separation runs along its own normal");
                var before = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    before[i] = Drawn(scene, positive, k_points[i]);
                }

                Assert.That(CommitBorrowingPositive(scene, root, cut, plane, positive, negative), Is.True, "committed");
                Collect(scene);

                Assert.That(Offset(scene, positive), Is.EqualTo(Vector3.zero), "the boundary is reflected now");
                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(before[i], Drawn(scene, positive, k_points[i]), "point " + i + " does not move across the commit");
                }

                // And it is still following: turning it again moves what the commit took in, with it.
                Matrix4x4 again = Owner(new Vector3(3f, 0f, 0f), 180f);
                scene.placements.of[positive] = again * k_geometryLocalToOwner;
                Collect(scene);
                for (int i = 0; i < k_points.Length; i++)
                {
                    Vector3 expected = (again * k_geometryLocalToOwner).MultiplyPoint3x4(k_points[i])
                                       + Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.forward))
                                           .MultiplyVector(new Vector3(0f, 0.5f, 0f));
                    Same(expected, Drawn(scene, positive, k_points[i]), "point " + i + " carries the folded separation");
                }
            }
            finally
            {
                Dispose(scene);
            }
        }

        // ----- a commit of a side that has itself been cut --------------------------------------------------------------

        /// <summary>
        /// A is published, A+ is cut by B and B is published, and only then is A committed. A+ is not a fragment
        /// anything follows any more, and its separation still has to reach B+ and B-, which are. Then one of them
        /// turns further and B is committed, and nothing moves at either commit.
        /// </summary>
        [Test]
        public void ACommitOfASideThatWasCutAgain_ReachesTheLivingDescendants()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(scene.display.TryShow(root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>()), Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var planeA = new float4(0f, 1f, 0f, 0f);
                var (cutA, aPlus, aMinus) = Cut(scene, root, planeA);

                Matrix4x4 turned = Owner(new Vector3(3f, 0f, 0f), 90f);
                scene.placements.of[aPlus] = turned * k_geometryLocalToOwner;
                scene.placements.of[aMinus] = turned * k_geometryLocalToOwner;

                // A+ is cut again and published before A's geometry is committed.
                var planeB = new float4(1f, 0f, 0f, 0f);
                var (cutB, bPlus, bMinus) = Cut(scene, aPlus, planeB);
                scene.placements.of[bPlus] = turned * k_geometryLocalToOwner;
                scene.placements.of[bMinus] = turned * k_geometryLocalToOwner;

                // Nothing follows A+ any more: it is not a fragment of the arrangement, and nothing is said about it.
                scene.placements.of.Remove(aPlus);
                Collect(scene);

                var before = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    before[i] = Drawn(scene, bPlus, k_points[i]);
                }

                Vector3 offsetBefore = Offset(scene, bPlus);

                // A is committed. Its separation belongs to the geometry, so it must reach B+ and B-.
                Assert.That(CommitBorrowingPositive(scene, root, cutA, planeA, aPlus, aMinus), Is.True, "A committed");
                Collect(scene);

                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(before[i], Drawn(scene, bPlus, k_points[i]), "B+ point " + i + " does not move when A is committed");
                }

                Same(offsetBefore - new Vector3(-0.5f, 0f, 0f), Offset(scene, bPlus), "only A's separation left the sum");

                // The living descendant turns further, and what A's commit took in turns with it.
                Matrix4x4 again = Owner(new Vector3(-1f, 2f, 0f), 180f);
                scene.placements.of[bPlus] = again * k_geometryLocalToOwner;
                Collect(scene);
                var beforeB = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    beforeB[i] = Drawn(scene, bPlus, k_points[i]);
                    Vector3 expected = (again * k_geometryLocalToOwner).MultiplyPoint3x4(k_points[i])
                                       + Matrix4x4.Rotate(Quaternion.AngleAxis(180f, Vector3.forward))
                                           .MultiplyVector(new Vector3(0f, 0.5f, 0f))
                                       + Offset(scene, bPlus);
                    Same(expected, beforeB[i], "B+ point " + i + " carries A's folded separation after turning");
                }

                // B committed: the same again, one boundary at a time.
                Assert.That(CommitBorrowingPositive(scene, aPlus, cutB, planeB, bPlus, bMinus), Is.True, "B committed");
                Collect(scene);
                Assert.That(Offset(scene, bPlus), Is.EqualTo(Vector3.zero), "nothing temporary is left");
                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(beforeB[i], Drawn(scene, bPlus, k_points[i]), "B+ point " + i + " does not move when B is committed");
                }
            }
            finally
            {
                Dispose(scene);
            }
        }

        // ----- the produced path: a real split, its bounds check, its registrations and its record --------------------

        /// <summary>
        /// A real split, through the produced path: the sides are transferred, judged where they will stand, taken as
        /// registrations of their own and written into a boundary record. The placement has a scale in it, so a
        /// separation taken in as a fixed amount of the geometry's own frame would be the wrong size in the world.
        /// </summary>
        [Test]
        public void AProducedSplit_KeepsTheSeparationItIsDrawnWithEvenWithScale()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();

                // Twice the size, and turned: the separation is 0.5 in the world whatever the scale is.
                Matrix4x4 scaled = Matrix4x4.TRS(
                    new Vector3(1f, 0f, 0f), Quaternion.AngleAxis(90f, Vector3.forward), new Vector3(2f, 2f, 2f));
                Assert.That(
                    scene.display.TryShow(
                        root, scene.geometry, scaled * k_geometryLocalToOwner, k_lineageToGeometryLocal,
                        System.Array.Empty<VpClipBoundary>()),
                    Is.True);
                scene.placements.of[root] = scaled * k_geometryLocalToOwner;

                var plane = new float4(0f, 1f, 0f, 0f);
                var (cut, positive, negative) = Cut(scene, root, plane);
                scene.placements.of[positive] = scaled * k_geometryLocalToOwner;
                scene.placements.of[negative] = scaled * k_geometryLocalToOwner;
                Collect(scene);

                Vector3 offsetBefore = Offset(scene, positive);
                Assert.That(
                    offsetBefore.magnitude, Is.EqualTo(Separation).Within(Tolerance),
                    "the separation is the same size in the world whatever a placement scales");
                var before = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    before[i] = Drawn(scene, positive, k_points[i]);
                }

                Assert.That(CommitProduced(scene, root, cut, plane, positive, negative), Is.True, "committed");
                Collect(scene);

                Assert.That(Offset(scene, positive), Is.EqualTo(Vector3.zero), "the boundary is reflected now");
                for (int i = 0; i < k_points.Length; i++)
                {
                    // The produced side is a new shape; what is compared is where the geometry's frame stands, which
                    // is what the separation was taken into.
                    Same(before[i], Drawn(scene, positive, k_points[i]), "point " + i + " does not move across the commit");
                }

                // The record keeps where each side stands, in the frame its own mapping is for.
                Assert.That(scene.display.BoundaryRecordCount, Is.EqualTo(1), "one record for one real surface");
                Assert.That(scene.display.TryGetBoundaryRecord(0, out LogicalCutBoundaryRecord record), Is.True);
                Assert.That(record.positive, Is.EqualTo(positive));
                Assert.That(record.negative, Is.EqualTo(negative));
                Same(
                    Drawn(scene, positive, Vector3.zero), record.positiveObjectToWorld.MultiplyPoint3x4(Vector3.zero),
                    "the record's positive placement is where that side stands");
                Same(
                    Drawn(scene, negative, Vector3.zero), record.negativeObjectToWorld.MultiplyPoint3x4(Vector3.zero),
                    "and the negative one");

                // The plane it kept, carried by the placement it kept, is the plane that side is drawn with.
                Assert.That(
                    VpCutPlane.TryGeometryLocalToWorld(
                        new float4(record.plane.x, record.plane.y, record.plane.z, record.plane.w),
                        record.lineageToGeometryLocal, out float4 local),
                    Is.True);
                Assert.That(
                    VpCutPlane.TryGeometryLocalToWorld(local, record.positiveObjectToWorld, out float4 world), Is.True);
                var recorded = new Vector3(world.x, world.y, world.z);
                Same(
                    Matrix4x4.Rotate(Quaternion.AngleAxis(90f, Vector3.forward)).MultiplyVector(Vector3.up), recorded,
                    "the record's plane, in the record's own frame, is the one that side is drawn with");
            }
            finally
            {
                Dispose(scene);
            }
        }

        /// <summary>
        /// A produced split of a side that has itself been cut and published since: the living descendants keep their
        /// own placements, and what the commit takes in reaches both of them.
        /// </summary>
        [Test]
        public void AProducedCommitAfterTheSideWasCutAgain_LeavesEachDescendantWhereItIs()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(
                    scene.display.TryShow(
                        root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal,
                        System.Array.Empty<VpClipBoundary>()),
                    Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var planeA = new float4(0f, 1f, 0f, 0f);
                var (cutA, aPlus, aMinus) = Cut(scene, root, planeA);
                scene.placements.of[aPlus] = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
                scene.placements.of[aMinus] = Owner(new Vector3(-3f, 0f, 0f), 0f) * k_geometryLocalToOwner;

                // A+ is cut again and published; the two living descendants stand apart from each other.
                var (_, bPlus, bMinus) = Cut(scene, aPlus, new float4(1f, 0f, 0f, 0f));
                scene.placements.of[bPlus] = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
                scene.placements.of[bMinus] = Owner(new Vector3(0f, 5f, 0f), 45f) * k_geometryLocalToOwner;
                scene.placements.of.Remove(aPlus);
                Collect(scene);

                var beforePlus = new Vector3[k_points.Length];
                var beforeMinus = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    beforePlus[i] = Drawn(scene, bPlus, k_points[i]);
                    beforeMinus[i] = Drawn(scene, bMinus, k_points[i]);
                }

                Assert.That(CommitProduced(scene, root, cutA, planeA, aPlus, aMinus), Is.True, "A committed");
                Collect(scene);

                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(beforePlus[i], Drawn(scene, bPlus, k_points[i]), "B+ point " + i + " stays where it is");
                    Same(beforeMinus[i], Drawn(scene, bMinus, k_points[i]), "B- point " + i + " stays where it is");
                }

                Assert.That(
                    (Drawn(scene, bPlus, Vector3.zero) - Drawn(scene, bMinus, Vector3.zero)).magnitude,
                    Is.GreaterThan(1f),
                    "the descendants keep their own placements, not one between them");
            }
            finally
            {
                Dispose(scene);
            }
        }

        /// <summary>
        /// A live fragment that follows something, with nowhere given for it, is not read as an arrangement that
        /// follows nothing: the commit is refused and nothing of the display changes. A fragment that is not a current
        /// target is a different matter and refuses nothing.
        /// </summary>
        [Test]
        public void ALiveFragmentWithNoPlacement_RefusesTheCommitRatherThanFallingBack()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(
                    scene.display.TryShow(
                        root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal,
                        System.Array.Empty<VpClipBoundary>()),
                    Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var plane = new float4(0f, 1f, 0f, 0f);
                var (cut, positive, negative) = Cut(scene, root, plane);
                scene.placements.of[negative] = k_geometryLocalToOwner;
                scene.placements.missing.Add(positive);

                Assert.That(
                    CommitProduced(scene, root, cut, plane, positive, negative), Is.False,
                    "a live side that follows something, with nowhere given, is not drawn where the geometry is");
                Assert.That(scene.display.ShownCount, Is.EqualTo(1), "and nothing of the display moved");

                // Said to follow nothing instead, the same commit goes through.
                scene.placements.missing.Remove(positive);
                Assert.That(CommitProduced(scene, root, cut, plane, positive, negative), Is.True, "committed");
                Assert.That(scene.display.ShownCount, Is.EqualTo(2), "two registrations now");
            }
            finally
            {
                Dispose(scene);
            }
        }

        // ----- a side an anchor fixes ------------------------------------------------------------------------------------

        /// <summary>
        /// A side an anchor fixes is not the same as a side that happens not to have moved: it takes no separation
        /// while the cut is temporary, and a commit takes none into its placement either. The free side of the same
        /// cut takes both.
        /// </summary>
        [Test]
        public void AnAnchoredSide_TakesNoSeparationWhileTemporaryAndNoneAtTheCommit()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;

                // One anchor above the plane: the positive side is fixed by it, the negative side is free.
                LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, 0.5f, 0f) });
                Assert.That(scene.display.TryShow(root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal, System.Array.Empty<VpClipBoundary>()), Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var plane = new float4(0f, 1f, 0f, 0f);
                var (cut, positive, negative) = Cut(scene, root, plane);
                Assert.That(ledger.IsFixedOwner(positive), Is.True, "the anchored side is fixed");
                Assert.That(ledger.IsFixedOwner(negative), Is.False, "and the other is not");

                // Both stand still, in the same place: what tells them apart is the anchor, not the movement.
                scene.placements.of[positive] = k_geometryLocalToOwner;
                scene.placements.of[negative] = k_geometryLocalToOwner;
                Collect(scene);

                Assert.That(Offset(scene, positive), Is.EqualTo(Vector3.zero), "the fixed side takes no separation");
                Same(new Vector3(0f, -0.5f, 0f), Offset(scene, negative), "the free side takes it");

                var before = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    before[i] = Drawn(scene, positive, k_points[i]);
                }

                Assert.That(CommitBorrowingPositive(scene, root, cut, plane, positive, negative), Is.True, "committed");
                Collect(scene);

                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(before[i], Drawn(scene, positive, k_points[i]), "the fixed side's point " + i + " is where it was");
                }

                // And moving it afterwards moves it by exactly that, with nothing added by the commit.
                Matrix4x4 moved = Owner(new Vector3(2f, 0f, 0f), 0f);
                scene.placements.of[positive] = moved * k_geometryLocalToOwner;
                Collect(scene);
                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(
                        before[i] + new Vector3(2f, 0f, 0f), Drawn(scene, positive, k_points[i]),
                        "the fixed side's point " + i + " moved by the placement alone");
                }
            }
            finally
            {
                Dispose(scene);
            }
        }
        // ----- an ignored aggregate across a real commit ----------------------------------------------------------------

        private static readonly Vector3[] k_aggregatePoints =
        {
            Vector3.zero, new Vector3(1f, 1f, 1f), new Vector3(-1f, 0.5f, -0.25f),
        };

        /// <summary>
        /// What a commit of the first boundary of <see cref="Chain"/> folds into the geometry's own frame, worked out
        /// from this test's input: that boundary's plane is y = 0 of the lineage frame, everything here descends from
        /// its positive side, and the placement the registration is committed at has no rotation in it, so the share
        /// it takes in is the separation along +y of the geometry's own frame.
        /// </summary>
        private static Matrix4x4 FirstBoundaryFold => Matrix4x4.Translate(new Vector3(0f, Separation, 0f));

        private static LogicalFragmentId RootOf(Scene scene, LogicalFragmentId fragment)
        {
            for (int r = 0; r < scene.display.RenderFragmentCount; r++)
            {
                VpMultiCutRenderFragment rf = RenderFragmentAt(scene, r);
                if (rf.root == fragment)
                {
                    return rf.root;
                }
            }

            Assert.Fail("nothing is drawn for that fragment");
            return default;
        }

        /// <summary>Two placements are the same when they put the same points in the same places.</summary>
        private static void SamePlacement(Matrix4x4 expected, Matrix4x4 actual, string what)
        {
            foreach (Vector3 local in k_aggregatePoints)
            {
                Same(expected.MultiplyPoint3x4(local), actual.MultiplyPoint3x4(local), what);
            }
        }

        /// <summary>A chain of cuts down the positive side, each published.</summary>
        private static LogicalFragmentId Chain(
            Scene scene, LogicalFragmentId root, int cuts, List<CutOperationId> operations, List<float4> planes)
        {
            LogicalFragmentId at = root;
            for (int i = 0; i < cuts; i++)
            {
                var plane = new float4(0f, 1f, 0f, -0.05f * i);
                var (cut, positive, _) = Cut(scene, at, plane);
                operations.Add(cut);
                planes.Add(plane);
                at = positive;
            }

            return at;
        }

        private static VpMultiCutRenderFragment RenderFragmentAt(Scene scene, int index)
        {
            Assert.That(scene.display.TryGetRenderFragment(index, out VpMultiCutRenderFragment rf), Is.True);
            return rf;
        }

        /// <summary>Every aggregate the display is drawing now, in the order it drew them.</summary>
        private static List<VpMultiCutRenderFragment> Aggregates(Scene scene)
        {
            var found = new List<VpMultiCutRenderFragment>();
            for (int r = 0; r < scene.display.RenderFragmentCount; r++)
            {
                VpMultiCutRenderFragment rf = RenderFragmentAt(scene, r);
                if (rf.aggregated)
                {
                    found.Add(rf);
                }
            }

            return found;
        }

        /// <summary>
        /// A commit that leaves an ignored boundary behind. Eight boundaries fill the capacity, a ninth is ignored and
        /// its branches are drawn once, and two of those branches are cut again on opposite sides of it. Committing
        /// the first boundary through the display's own commit advances what is selected: the one aggregate becomes
        /// two, each rooted at its own side of the ignored cut and each standing where its own first living branch
        /// stands, and the branch that no longer has anything ignored goes back to its own placement.
        /// </summary>
        [Test]
        public void ACommitThatLeavesAnIgnoredBoundary_SplitsTheAggregateAndKeepsEachOneFollowing()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(
                    scene.display.TryShow(
                        root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal,
                        System.Array.Empty<VpClipBoundary>()),
                    Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var operations = new List<CutOperationId>();
                var planes = new List<float4>();
                LogicalFragmentId last = Chain(scene, root, VpClipCandidates.Capacity, operations, planes);

                // The ninth: past the capacity, so it is ignored and its two sides are drawn as one shape.
                var (_, above, below) = Cut(scene, last, new float4(1f, 0f, 0f, 0f));

                // Both sides of it are cut again, so both stay aggregated after one boundary is committed.
                var (_, aboveDeep, aboveDeepOther) = Cut(scene, above, new float4(0f, 0f, 1f, 0f));
                var (_, belowDeep, belowDeepOther) = Cut(scene, below, new float4(0f, 0f, 1f, 0.1f));

                Matrix4x4 mAbove = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
                Matrix4x4 mBelow = Owner(new Vector3(-3f, 1f, 0f), 0f) * k_geometryLocalToOwner;
                scene.placements.of[aboveDeep] = mAbove;
                scene.placements.of[aboveDeepOther] = mAbove;
                scene.placements.of[belowDeep] = mBelow;
                scene.placements.of[belowDeepOther] = mBelow;

                Collect(scene);
                List<VpMultiCutRenderFragment> before = Aggregates(scene);
                Assert.That(before.Count, Is.EqualTo(1), "one shape is drawn for everything behind the ignored cut");
                Assert.That(before[0].root, Is.EqualTo(last), "rooted at the source of the ignored cut");
                SamePlacement(mAbove, before[0].geometryLocalToWorld, "standing where its first living branch does");

                // The first boundary's geometry is committed through the display's own entry. The positive side
                // borrows the geometry it already had, which is the side everything here descends from.
                Assert.That(
                    ledger.TryGetOperation(operations[0], out LogicalCutOperation firstCut), Is.True);
                Assert.That(
                    CommitBorrowingPositive(scene, root, operations[0], planes[0], firstCut.positive, firstCut.negative),
                    Is.True,
                    "the first boundary is committed");
                Collect(scene);

                // One boundary left the candidates, so the selected window moved on by one. The ninth boundary is
                // selected now, and what is ignored is the tenth -- a different root on each side of the ninth.
                List<VpMultiCutRenderFragment> after = Aggregates(scene);
                Assert.That(after.Count, Is.EqualTo(2), "the one aggregate became two");
                Assert.That(
                    after.Exists(rf => rf.root.Equals(above)) && after.Exists(rf => rf.root.Equals(below)), Is.True,
                    "one rooted at each side of the boundary that is no longer ignored");

                VpMultiCutRenderFragment aboveGroup = after.Find(rf => rf.root.Equals(above));
                VpMultiCutRenderFragment belowGroup = after.Find(rf => rf.root.Equals(below));
                SamePlacement(
                    mAbove * FirstBoundaryFold, aboveGroup.geometryLocalToWorld,
                    "each stands where its own first branch does, with the committed boundary's share folded in");
                SamePlacement(
                    mBelow * FirstBoundaryFold, belowGroup.geometryLocalToWorld,
                    "including the side that was not first before");

                // Each follows its own first branch from here.
                Matrix4x4 movedBelow = Owner(new Vector3(-6f, 4f, 1f), 200f) * k_geometryLocalToOwner;
                scene.placements.of[belowDeep] = movedBelow;
                scene.placements.of[belowDeepOther] = movedBelow;
                Collect(scene);
                after = Aggregates(scene);
                SamePlacement(
                    movedBelow * FirstBoundaryFold, after.Find(rf => rf.root.Equals(below)).geometryLocalToWorld,
                    "the second aggregate follows its own branch");
                SamePlacement(
                    mAbove * FirstBoundaryFold, after.Find(rf => rf.root.Equals(above)).geometryLocalToWorld,
                    "and the first one did not move with it");
            }
            finally
            {
                Dispose(scene);
            }
        }

        /// <summary>
        /// Enough boundaries are committed that nothing is ignored any more. The aggregate is gone and each living
        /// branch is drawn at its own placement. What moves at that moment -- the shape and where its parts stand --
        /// is the accepted change of D-187 and is not a failure here.
        /// </summary>
        [Test]
        public void WhenNothingIsIgnoredAnyMore_EachBranchIsDrawnAtItsOwnPlacement()
        {
            Scene scene = NewScene();
            try
            {
                LogicalCutLedger ledger = scene.ledger;
                LogicalFragmentId root = ledger.AddFragment();
                Assert.That(
                    scene.display.TryShow(
                        root, scene.geometry, k_geometryLocalToOwner, k_lineageToGeometryLocal,
                        System.Array.Empty<VpClipBoundary>()),
                    Is.True);
                scene.placements.of[root] = k_geometryLocalToOwner;

                var operations = new List<CutOperationId>();
                var planes = new List<float4>();
                LogicalFragmentId last = Chain(scene, root, VpClipCandidates.Capacity, operations, planes);
                var (_, above, below) = Cut(scene, last, new float4(1f, 0f, 0f, 0f));

                Matrix4x4 mAbove = Owner(new Vector3(3f, 0f, 0f), 90f) * k_geometryLocalToOwner;
                Matrix4x4 mBelow = Owner(new Vector3(-3f, 1f, 0f), 0f) * k_geometryLocalToOwner;
                scene.placements.of[above] = mAbove;
                scene.placements.of[below] = mBelow;

                Collect(scene);
                Assert.That(Aggregates(scene).Count, Is.EqualTo(1), "one shape while the ninth boundary is ignored");

                // One boundary committed is enough: nine candidates become eight.
                Assert.That(ledger.TryGetOperation(operations[0], out LogicalCutOperation firstCut), Is.True);
                Assert.That(
                    CommitBorrowingPositive(scene, root, operations[0], planes[0], firstCut.positive, firstCut.negative),
                    Is.True,
                    "the first boundary is committed");
                Collect(scene);

                Assert.That(Aggregates(scene).Count, Is.Zero, "nothing is drawn once for several branches any more");
                // Each side's own placement with the committed boundary's share folded in. The separation still
                // being summed for the boundaries that are temporary is the collection's own and is not what this
                // case is about, so it is taken as it stands.
                foreach (Vector3 local in k_aggregatePoints)
                {
                    Same(
                        (mAbove * FirstBoundaryFold).MultiplyPoint3x4(local) + Offset(scene, above),
                        Drawn(scene, above, local), "the one side is drawn at its own placement");
                    Same(
                        (mBelow * FirstBoundaryFold).MultiplyPoint3x4(local) + Offset(scene, below),
                        Drawn(scene, below, local), "and the other at its own");
                }

                Assert.That(RootOf(scene, above), Is.EqualTo(above), "drawn as its own branch, not as an aggregate");
                Assert.That(RootOf(scene, below), Is.EqualTo(below), "and so is the other");
            }
            finally
            {
                Dispose(scene);
            }
        }

    }
}
