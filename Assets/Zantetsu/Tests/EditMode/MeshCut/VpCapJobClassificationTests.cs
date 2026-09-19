using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;
using S = Zantetsu.MeshCut.Tests.VpMultiCutSnapshotTests;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The cap-job stencil preparation of DESIGN 5.6 / D-183, D-186 on the CPU: jobs per visible cap, volume groups of
    /// exactly the same volume, ordinary colours from the initial sections, and the last colour for what they cannot take. Snapshots are built from real ledgers; what each case expects
    /// is worked out from its own layout -- which way a cap faces, where the boxes are -- never read back from the
    /// classification under test.
    /// </summary>
    public class VpCapJobClassificationTests
    {
        private const float FacingEpsilon = 0.01f;
        private static readonly Vector2 Margin = new Vector2(0.01f, 0.01f);
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

        // ----- helpers -------------------------------------------------------------------------------------------------

        private Camera NewCamera(Vector3 position, Vector3 lookAt)
        {
            Camera camera = new GameObject("Cap Job Eye").AddComponent<Camera>();
            _objects.Add(camera.gameObject);
            camera.enabled = false;
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.05f;
            camera.farClipPlane = 50f;
            camera.transform.position = position;
            Vector3 forward = lookAt - position;
            camera.transform.rotation = Quaternion.LookRotation(forward, Mathf.Abs(Vector3.Dot(forward.normalized, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up);
            return camera;
        }

        private VpCapEye Eye(Vector3 position, Vector3 lookAt) => EyeOf(NewCamera(position, lookAt));

        private static VpCapEye EyeOf(Camera camera) => new VpCapEye(camera.transform.position, camera.projectionMatrix * camera.worldToCameraMatrix);

        private static VpCapJobClassification NewClassification(int caps = 512) =>
            new VpCapJobClassification(new VpMultiCutCapacities(64, 1024, 64, caps, 64));

        private static VpCapJobGeometry Geometry(int vertexStart, int indexStart) =>
            new VpCapJobGeometry(VpArrayRange<VpGeometryRange>.Whole(new[] { new VpGeometryRange(vertexStart, 24, indexStart, 36) }));

        private static VpCapJobGeometry[] Geometries(int registrations)
        {
            var list = new VpCapJobGeometry[registrations];
            for (int g = 0; g < registrations; g++)
            {
                list[g] = Geometry(g * 24, g * 36);
            }

            return list;
        }

        private static VpMultiCutRegistration Registration(LogicalFragmentId root, Matrix4x4 placement) =>
            new VpMultiCutRegistration(root, S.k_box, placement, Matrix4x4.identity, S.k_none, VpCapBoundsPolygon.EpsilonFor(S.k_box));

        private static VpMultiCutSnapshot Built(LogicalCutLedger ledger, float separation, params VpMultiCutRegistration[] registrations)
        {
            VpMultiCutSnapshot snapshot = S.NewSnapshot();
            Assert.That(snapshot.TryBuild(ledger, registrations, separation), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            return snapshot;
        }

        private static List<VpCapJob> Jobs(VpCapJobClassification classification)
        {
            var jobs = new List<VpCapJob>();
            for (int j = 0; j < classification.JobCount; j++)
            {
                Assert.That(classification.TryGetJob(j, out VpCapJob job), Is.True);
                jobs.Add(job);
            }

            return jobs;
        }

        private static VpCapJob JobOf(VpCapJobClassification classification, int registration, CutOperationId cut, float side, Vector3 offset)
        {
            foreach (VpCapJob job in Jobs(classification))
            {
                if (job.registration == registration && job.boundary.face.operation == cut && job.boundary.side == side
                    && job.offset.x == offset.x && job.offset.y == offset.y && job.offset.z == offset.z)
                {
                    return job;
                }
            }

            Assert.Fail("no job for registration " + registration + ", side " + side + ", offset " + offset);
            return default;
        }

        /// <summary>A box cut at y = 0 (anchor below, so the lower side is fixed), its upper side cut at x = 0 with no anchor.</summary>
        private static (VpMultiCutSnapshot snapshot, LogicalCutLedger ledger, CutOperationId a, CutOperationId b) Orthogonal(float separation)
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            var (a, plus, _) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (b, _, _) = S.Cut(ledger, plus, new float4(1f, 0f, 0f, 0f));
            return (Built(ledger, separation, Registration(root, Matrix4x4.identity)), ledger, a, b);
        }

        /// <summary>Boxes of their own registrations, each cut at y = 0 with its anchor below: one top cap each, at y = 0.</summary>
        private static (VpMultiCutSnapshot snapshot, CutOperationId[] cuts) Tops(float separation, params Vector3[] places)
        {
            LogicalCutLedger ledger = S.NewLedger();
            var registrations = new VpMultiCutRegistration[places.Length];
            var cuts = new CutOperationId[places.Length];
            for (int i = 0; i < places.Length; i++)
            {
                LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
                cuts[i] = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f)).cut;
                registrations[i] = Registration(root, Matrix4x4.Translate(places[i]));
            }

            return (Built(ledger, separation, registrations), cuts);
        }

        private static int ColourOfTop(VpCapJobClassification classification, int registration, CutOperationId cut)
        {
            return JobOf(classification, registration, cut, -1f, Vector3.zero).colour;
        }

        // ----- cap jobs ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Seen from low on the -x side, the upper-positive piece (x and y from 0.25 to 1.25) shows both of its caps: its
        /// A face faces -y and its B face -x. Each is a job of its own whose volume clip is that face alone, kept half
        /// folded in, while the render fragment's clip keeps both faces. The fixed lower piece's top faces +y (the eye is
        /// below it) and the upper-negative piece's B face faces +x (the eye is on the -x side): both are left out, and
        /// the upper-negative piece's A face, facing -y, is kept.
        /// </summary>
        [Test]
        public void EachVisibleCap_IsAJobOfItsOwn_ClippedByItsOwnFaceOnly()
        {
            var (snapshot, _, a, b) = Orthogonal(0.25f);
            VpCapEye eye = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(3));
            Assert.That(classification.HiddenCapCount, Is.EqualTo(2));
            Assert.That(classification.EmptyCapCount, Is.Zero);

            var corner = new Vector3(0.25f, 0.25f, 0f);
            VpCapJob aJob = JobOf(classification, 0, a, 1f, corner);
            VpCapJob bJob = JobOf(classification, 0, b, 1f, corner);
            Assert.That(aJob.renderFragment, Is.EqualTo(bJob.renderFragment), "both caps are of one render fragment");
            Assert.That(snapshot.TryGetRenderFragment(aJob.renderFragment, out VpMultiCutRenderFragment rf), Is.True);
            Assert.That(rf.clip.PlaneCount, Is.EqualTo(2), "the body's clip keeps both faces");

            Assert.That(aJob.volumeClip.PlaneCount, Is.EqualTo(1));
            Assert.That(aJob.volumeClip.SignedPlane(0), Is.EqualTo(new Vector4(0f, 1f, 0f, 0f)));
            Assert.That(bJob.volumeClip.PlaneCount, Is.EqualTo(1));
            Assert.That(bJob.volumeClip.SignedPlane(0), Is.EqualTo(new Vector4(1f, 0f, 0f, 0f)));
            Assert.That(aJob.volumeClip.Offset, Is.EqualTo(corner));
            Assert.That(aJob.volumeGroup, Is.Not.EqualTo(bJob.volumeGroup), "different faces are different volumes");
            Assert.That(aJob.polygonVertexCount, Is.EqualTo(4));
            Assert.That(aJob.initialVertexCount, Is.EqualTo(4));

            JobOf(classification, 0, a, 1f, new Vector3(-0.25f, 0.25f, 0f));
        }

        /// <summary>
        /// A box cut at y = 0.5 (anchor below) and its lower side cut again at y = 0.7, above everything that side keeps:
        /// the part kept above 0.7 is empty, so both of its caps are empty, and so is the cap at 0.7 of the part below.
        /// Three empty caps, none of them a job; the one real cap (the top at 0.5, seen from above) is.
        /// </summary>
        [Test]
        public void EmptyCaps_AreNoJobs()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            var (a, _, minus) = S.Cut(ledger, root, new float4(0f, 1f, 0f, -0.5f));
            S.Cut(ledger, minus, new float4(0f, 1f, 0f, -0.7f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(0.5f, 5f, -3f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.EmptyCapCount, Is.EqualTo(3));
            foreach (VpCapJob job in Jobs(classification))
            {
                Assert.That(job.polygonVertexCount, Is.GreaterThan(0));
            }

            JobOf(classification, 0, a, -1f, Vector3.zero);
        }

        /// <summary>Nine cuts down one lineage: the ninth boundary is Ignored, so no cap and no job is of it.</summary>
        [Test]
        public void AnIgnoredBoundary_MakesNoJob()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId at = root;
            CutOperationId last = default;
            for (int k = 0; k < 9; k++)
            {
                float angle = k * 0.35f;
                float4 plane = new float4(0.2f * Mathf.Cos(angle), 1f, 0.2f * Mathf.Sin(angle), 0.8f - (0.15f * k));
                plane /= math.length(plane.xyz);
                var (cut, plus, _) = S.Cut(ledger, at, plane);
                at = plus;
                last = cut;
            }

            VpMultiCutSnapshot snapshot = Built(ledger, 0.15f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(3f, 6f, -6f), new Vector3(0f, 0.5f, 0f));
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 16), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.GreaterThan(0));
            for (int c = 0; c < snapshot.CapCount; c++)
            {
                Assert.That(snapshot.TryGetCap(c, out VpMultiCutCap cap), Is.True);
                Assert.That(cap.boundary.face.operation, Is.Not.EqualTo(last), "no cap of the Ignored boundary");
            }

            foreach (VpCapJob job in Jobs(classification))
            {
                Assert.That(job.boundary.face.operation, Is.Not.EqualTo(last));
            }
        }

        // ----- one volume, exactly -------------------------------------------------------------------------------------

        /// <summary>
        /// A box cut at y = 0 with anchors on both sides of x = 0 below, its lower side then cut at x = 0: both halves are
        /// fixed. Their two top caps are the same registration, the same A face on the same side, at the same offset (0):
        /// one volume, two jobs. The B cap of the negative half faces +x and is seen (the eye is at x = 0.3); it is a
        /// volume of its own. The positive half's B cap and the upper piece's bottom face away and are no jobs.
        /// </summary>
        [Test]
        public void TheSameVolume_IsOneGroupForSeveralJobs()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.5f, 0f), new float3(0.5f, -0.5f, 0f) });
            var (a, _, minus) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            var (b, _, _) = S.Cut(ledger, minus, new float4(1f, 0f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(0.3f, 4f, -2.5f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(3));
            Assert.That(classification.VolumeGroupCount, Is.EqualTo(2), "two top caps, one volume; the B cap its own");

            var tops = new List<VpCapJob>();
            foreach (VpCapJob job in Jobs(classification))
            {
                if (job.boundary.face.operation == a)
                {
                    tops.Add(job);
                }
            }

            Assert.That(tops.Count, Is.EqualTo(2));
            Assert.That(tops[0].renderFragment, Is.Not.EqualTo(tops[1].renderFragment), "two render fragments");
            Assert.That(tops[0].volumeGroup, Is.EqualTo(tops[1].volumeGroup));
            Assert.That(tops[0].colour, Is.EqualTo(tops[1].colour));
            Assert.That(classification.TryGetVolumeGroup(tops[0].volumeGroup, out VpCapVolumeGroup group), Is.True);
            Assert.That(group.jobCount, Is.EqualTo(2));
            JobOf(classification, 0, b, -1f, Vector3.zero);
        }

        /// <summary>
        /// The same halves with only the negative one anchored and a separation of 1e-6: the positive half is moved by
        /// (1e-6, 0, 0). Its top cap is the same face and side as the fixed half's, but not the same volume -- two groups
        /// -- and, their initial sections overlapping, two colours. With one colour allowed there is no ordinary colour:
        /// both groups go, whole, to the last colour, which lists each of their render fragments once.
        /// </summary>
        [Test]
        public void VolumesAMillionthApart_AreNotOne()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.5f, 0f) });
            var (a, _, minus) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            S.Cut(ledger, minus, new float4(1f, 0f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 1e-6f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(0.3f, 4f, -2.5f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            VpCapJob fixedTop = JobOf(classification, 0, a, -1f, Vector3.zero);
            VpCapJob movedTop = JobOf(classification, 0, a, -1f, new Vector3(1e-6f, 0f, 0f));
            Assert.That(movedTop.volumeGroup, Is.Not.EqualTo(fixedTop.volumeGroup));
            Assert.That(movedTop.colour, Is.Not.EqualTo(fixedTop.colour));

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 1), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.OrdinaryColourCount, Is.Zero);
            Assert.That(classification.ColourCount, Is.EqualTo(1));
            Assert.That(classification.LastColourIndex, Is.Zero);
            Assert.That(classification.LastColourGroupCount, Is.EqualTo(classification.VolumeGroupCount), "every group");
            Assert.That(classification.LastColourJobCount, Is.EqualTo(classification.JobCount), "every job");
            AssertLastColourRenderFragmentsListedOnce(classification);
        }

        /// <summary>
        /// Seen from just above y = 0 (the upper piece moved to y = 0.25), the fixed lower piece's top and the upper
        /// piece's bottom are both seen: the same face, other sides -- two volumes.
        /// </summary>
        [Test]
        public void TheOtherSideOfOneFace_IsAnotherVolume()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            var (a, _, _) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(4f, 0.1f, -3f), new Vector3(0f, 0.1f, 0f));
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            VpCapJob top = JobOf(classification, 0, a, -1f, Vector3.zero);
            VpCapJob bottom = JobOf(classification, 0, a, 1f, new Vector3(0f, 0.25f, 0f));
            Assert.That(top.volumeGroup, Is.Not.EqualTo(bottom.volumeGroup));
        }

        /// <summary>
        /// Two registrations placed and cut alike, given the same draw ranges: never one volume -- they are different
        /// registrations (and different faces). Every job is a group of its own.
        /// </summary>
        [Test]
        public void DifferentRegistrations_AreDifferentVolumes()
        {
            var (snapshot, _) = Tops(0.25f, Vector3.zero, new Vector3(3f, 0f, 0f));
            var same = new[] { Geometry(0, 0), Geometry(0, 0) };
            VpCapEye eye = Eye(new Vector3(1.5f, 6f, -2f), new Vector3(1.5f, 0f, 0f));
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, same, eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(2));
            Assert.That(classification.VolumeGroupCount, Is.EqualTo(2));
        }

        // ----- colours -------------------------------------------------------------------------------------------------

        /// <summary>
        /// X5 of the non-XR check: a box at the origin cut at y = 0, and a box at x = 1.3 cut at y = 0 whose lower side is
        /// cut again by the plane x + y = 0 (its anchor on the positive side). Seen from above, the first box's top
        /// drawing polygon spans x -1..1 and the second box's positive top spans x 1.3..2.3 (checked here on the
        /// snapshot's own vertices): apart. Their initial sections -- x -1..1 and 0.3..2.3 -- overlap, so they are not
        /// given one colour. With a limit of two -- one ordinary colour -- one of them is left for the last colour; with a
    /// limit of one, both are there.
        /// </summary>
        [Test]
        public void DrawingPolygonsApart_ButInitialSectionsOverlapping_AreSeparated()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId first = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            var (a1, _, _) = S.Cut(ledger, first, new float4(0f, 1f, 0f, 0f));
            LogicalFragmentId second = ledger.AddFragment(new List<float3> { new float3(0.5f, -0.3f, 0f) });
            var (a2, _, minus) = S.Cut(ledger, second, new float4(0f, 1f, 0f, 0f));
            S.Cut(ledger, minus, new float4(1f, 1f, 0f, 0f) / math.sqrt(2f));
            VpMultiCutSnapshot snapshot = Built(ledger, 1.6f,
                Registration(first, Matrix4x4.identity), Registration(second, Matrix4x4.Translate(new Vector3(1.3f, 0f, 0f))));
            VpCapEye eye = Eye(new Vector3(0.65f, 5f, -3f), new Vector3(0.65f, 0f, 0f));
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(2), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            VpCapJob j1 = JobOf(classification, 0, a1, -1f, Vector3.zero);
            VpCapJob j2 = JobOf(classification, 1, a2, -1f, Vector3.zero);

            // The layout, read off the snapshot rather than the classification: drawing polygons apart along x, initial
            // sections overlapping along x, both in the plane y = 0.
            Extent(snapshot.CapPolygon(j1.capIndex), out float min1, out float max1);
            Extent(snapshot.CapPolygon(j2.capIndex), out float min2, out float max2);
            Extent(snapshot.InitialSection(j1.capIndex), out float initialMin1, out float initialMax1);
            Extent(snapshot.InitialSection(j2.capIndex), out float initialMin2, out float initialMax2);
            Assert.That(max1, Is.LessThan(min2), "drawing polygons apart");
            Assert.That(initialMin2, Is.LessThan(initialMax1), "initial sections overlapping");

            Assert.That(j1.colour, Is.Not.EqualTo(j2.colour));

            Assert.That(classification.TryClassify(snapshot, Geometries(2), eye, eye, FacingEpsilon, Margin, 2), Is.EqualTo(VpCapJobOutcome.Classified));
            j1 = JobOf(classification, 0, a1, -1f, Vector3.zero);
            j2 = JobOf(classification, 1, a2, -1f, Vector3.zero);
            Assert.That(classification.OrdinaryColourCount, Is.EqualTo(1));
            Assert.That(classification.LastColourIndex, Is.EqualTo(1));
            Assert.That(j1.colour, Is.Not.EqualTo(j2.colour), "limit 2: one ordinary, the other last");
            Assert.That(Math.Max(j1.colour, j2.colour), Is.EqualTo(classification.LastColourIndex));

            Assert.That(classification.TryClassify(snapshot, Geometries(2), eye, eye, FacingEpsilon, Margin, 1), Is.EqualTo(VpCapJobOutcome.Classified));
            j1 = JobOf(classification, 0, a1, -1f, Vector3.zero);
            j2 = JobOf(classification, 1, a2, -1f, Vector3.zero);
            Assert.That(j1.colour, Is.EqualTo(0).And.EqualTo(j2.colour), "limit 1: both in the last colour");
            Assert.That(classification.LastColourIndex, Is.Zero);
        }

        private static void Extent(VpArrayRange<Vector3> polygon, out float min, out float max)
        {
            min = float.PositiveInfinity;
            max = float.NegativeInfinity;
            for (int i = 0; i < polygon.Count; i++)
            {
                min = Mathf.Min(min, polygon[i].x);
                max = Mathf.Max(max, polygon[i].x);
            }
        }

        /// <summary>
        /// Two tops: one at the origin, one at (0, 1.2, -2.5), higher and nearer the first eye, which looks at them from
        /// the front and above and sees them overlap; the second eye, far out on +x, sees them side by side along z,
        /// apart (checked here from the cameras' own projection of the section corners). One eye overlapping is enough.
        /// </summary>
        [Test]
        public void OverlapInOneEye_Separates()
        {
            var (snapshot, cuts) = Tops(0.25f, Vector3.zero, new Vector3(0f, 1.2f, -2.5f));
            Camera front = NewCamera(new Vector3(0f, 5f, -6f), Vector3.zero);
            Camera side = NewCamera(new Vector3(9f, 5f, -1.2f), new Vector3(0f, 0.6f, -1.2f));
            Assert.That(RectanglesOverlap(side, new Vector3(0f, 0f, 0f), new Vector3(0f, 1.2f, -2.5f)), Is.False, "apart in the side eye");
            VpCapEye l = EyeOf(front);
            VpCapEye r = EyeOf(side);
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(snapshot, Geometries(2), r, r, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(1), "apart in that eye alone: one colour");
            Assert.That(classification.TryClassify(snapshot, Geometries(2), l, l, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(2), "overlapping in the front eye");
            Assert.That(classification.TryClassify(snapshot, Geometries(2), l, r, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(ColourOfTop(classification, 0, cuts[0]), Is.Not.EqualTo(ColourOfTop(classification, 1, cuts[1])));
            Assert.That(classification.TryClassify(snapshot, Geometries(2), r, l, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(2), "either eye");
        }

        /// <summary>The 2 × 2 top squares (y = 0) around two places, as screen rectangles of one camera: whether they overlap.</summary>
        private static bool RectanglesOverlap(Camera camera, Vector3 first, Vector3 second)
        {
            Rect a = ScreenRect(camera, first);
            Rect b = ScreenRect(camera, second);
            return a.Overlaps(b);
        }

        private static Rect ScreenRect(Camera camera, Vector3 place)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = place + new Vector3((i & 1) == 0 ? -1f : 1f, 0f, (i & 2) == 0 ? -1f : 1f);
                Vector3 v = camera.WorldToViewportPoint(corner);
                minX = Mathf.Min(minX, v.x);
                maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y);
                maxY = Mathf.Max(maxY, v.y);
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// Two tops side by side along x with a gap between the squares, seen from straight above: apart with no margin,
        /// one colour; the same pair with a margin wider than the gap on the screen, two colours. Touching squares (no
        /// gap) are not apart even with no margin.
        /// </summary>
        [Test]
        public void TheMarginAndTouching_AreConservative()
        {
            var (apart, _) = Tops(0.25f, Vector3.zero, new Vector3(2.5f, 0f, 0f));
            VpCapEye above = Eye(new Vector3(1.25f, 7f, 0.01f), new Vector3(1.25f, 0f, 0f));
            VpCapJobClassification classification = NewClassification();
            Assert.That(classification.TryClassify(apart, Geometries(2), above, above, FacingEpsilon, Vector2.zero, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(1));
            Assert.That(classification.TryClassify(apart, Geometries(2), above, above, FacingEpsilon, new Vector2(0.5f, 0.5f), 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(2));

            var (touching, _) = Tops(0.25f, Vector3.zero, new Vector3(2f, 0f, 0f));
            VpCapEye overTheSeam = Eye(new Vector3(1f, 7f, 0.01f), new Vector3(1f, 0f, 0f));
            Assert.That(classification.TryClassify(touching, Geometries(2), overTheSeam, overTheSeam, FacingEpsilon, Vector2.zero, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(2), "touching is not apart");
        }

        /// <summary>
        /// The pair that is apart from above: with one eye's matrix not finite, that eye settles nothing -- the visibility
        /// test then leaves no cap out, so each box's moved-up bottom is a job too (four jobs) -- and every pair may
        /// overlap: four colours, and with a limit of one all four in the last colour. Then an eye standing over the first top at y = 0.5, looking level
        /// along +z, has half of that section behind it: that projection cannot be bounded, and the pair is separated.
        /// </summary>
        [Test]
        public void ProjectionsNotFiniteOrUnbounded_AreNeverApart()
        {
            var (apart, _) = Tops(0.25f, Vector3.zero, new Vector3(2.5f, 0f, 0f));
            VpCapEye above = Eye(new Vector3(1.25f, 7f, 0.01f), new Vector3(1.25f, 0f, 0f));
            Matrix4x4 broken = Matrix4x4.identity;
            broken[0, 0] = float.NaN;
            var notFinite = new VpCapEye(above.position, broken);
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(apart, Geometries(2), above, above, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(2));
            Assert.That(classification.ColourCount, Is.EqualTo(1));
            Assert.That(classification.TryClassify(apart, Geometries(2), notFinite, above, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(4));
            Assert.That(classification.ColourCount, Is.EqualTo(4));
            Assert.That(classification.TryClassify(apart, Geometries(2), notFinite, above, FacingEpsilon, Margin, 1), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(1));
            Assert.That(classification.LastColourJobCount, Is.EqualTo(4));

            var (far, _) = Tops(0.25f, Vector3.zero, new Vector3(0f, 0f, 12f));
            VpCapEye standing = Eye(new Vector3(0f, 0.5f, 0f), new Vector3(0f, 0.5f, 5f));
            Assert.That(classification.TryClassify(far, Geometries(2), standing, standing, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(2));
            Assert.That(classification.ColourCount, Is.EqualTo(2));
        }

        // ----- visibility ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The fixed lower piece's top faces +y at y = 0 and the moved upper piece's bottom faces -y at y = 0.25. One eye
        /// above and one below: each cap is faced by one eye and both are jobs. Both eyes below: the top is left out. At
        /// y = -0.01 exactly (the epsilon's negative) the top is kept; within the band (-0.005) kept; at -0.02 left out.
        /// </summary>
        [Test]
        public void OneEyeAndTheFacingBand_KeepACap()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            var (a, _, _) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapJobClassification classification = NewClassification();
            VpCapEye up = Eye(new Vector3(3f, 2f, -3f), Vector3.zero);
            VpCapEye down = Eye(new Vector3(3f, -2f, -3f), Vector3.zero);

            Assert.That(classification.TryClassify(snapshot, Geometries(1), up, down, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.EqualTo(2), "each cap faced by one eye");
            Assert.That(classification.TryClassify(snapshot, Geometries(1), down, down, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(HasTop(classification, a), Is.False);

            foreach ((float y, bool kept) in new[] { (-0.01f, true), (-0.005f, true), (-0.02f, false) })
            {
                VpCapEye eye = Eye(new Vector3(3f, y, -3f), new Vector3(0f, y, 0f));
                Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
                Assert.That(HasTop(classification, a), Is.EqualTo(kept), "eye at y = " + y);
            }
        }

        private static bool HasTop(VpCapJobClassification classification, CutOperationId a)
        {
            foreach (VpCapJob job in Jobs(classification))
            {
                if (job.boundary.face.operation == a && job.boundary.side < 0f)
                {
                    return true;
                }
            }

            return false;
        }

        // ----- the colour limit and the last colour, room and failures ------------------------------------------------

        /// <summary>
        /// The two tops of different heights of the one-eye case, under a limit of 2 (one ordinary colour and the last).
        /// From the side they are apart: one ordinary colour, and no last colour at all. From the front they overlap:
        /// the first in the ordinary colour, the second, whole, in the last -- never refused. From the side again, a
        /// little moved, no last colour again, equal to a fresh instance's. Under a limit of 1 even the side view has
        /// both in the last colour, and no ordinary colour.
        /// </summary>
        [Test]
        public void TheColourLimit_SendsWhatDoesNotFitToTheLastColour_AndNothingWhenAllFits()
        {
            var (apart, cuts) = Tops(0.25f, Vector3.zero, new Vector3(0f, 1.2f, -2.5f));
            VpCapEye above = Eye(new Vector3(9f, 5f, -1.2f), new Vector3(0f, 0.6f, -1.2f));
            VpCapEye low = Eye(new Vector3(0f, 5f, -6f), Vector3.zero);
            VpCapEye aboveAgain = Eye(new Vector3(9f, 5.2f, -1.1f), new Vector3(0f, 0.6f, -1.2f));
            VpCapJobClassification classification = NewClassification();

            Assert.That(classification.TryClassify(apart, Geometries(2), above, above, FacingEpsilon, Margin, 2), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.ColourCount, Is.EqualTo(1), "apart: one ordinary colour");
            Assert.That(classification.LastColourIndex, Is.EqualTo(-1), "nothing left: no last colour");
            Assert.That(classification.LastColourRenderFragmentCount + classification.LastColourJobCount + classification.LastColourGroupCount, Is.Zero);
            Assert.That(classification.TryGetLastColourRenderFragment(0, out _), Is.False);

            Assert.That(classification.TryClassify(apart, Geometries(2), low, low, FacingEpsilon, Margin, 2), Is.EqualTo(VpCapJobOutcome.Classified), "never refused");
            Assert.That(classification.OrdinaryColourCount, Is.EqualTo(1));
            Assert.That(classification.ColourCount, Is.EqualTo(2), "the colours used, the last included");
            Assert.That(classification.LastColourIndex, Is.EqualTo(1), "after the ordinary colour");
            Assert.That(classification.OrdinaryVolumeGroupCount, Is.EqualTo(1));
            Assert.That(classification.LastColourGroupCount, Is.EqualTo(1));
            Assert.That(classification.LastColourJobCount, Is.EqualTo(1));
            Assert.That(classification.LastColourRenderFragmentCount, Is.EqualTo(1));
            Assert.That(ColourOfTop(classification, 0, cuts[0]), Is.Not.EqualTo(ColourOfTop(classification, 1, cuts[1])));
            Assert.That(classification.TryGetColour(1, out VpCapJobColour lastColour), Is.True);
            Assert.That(lastColour.last, Is.True);
            Assert.That(classification.TryGetColour(0, out VpCapJobColour ordinary), Is.True);
            Assert.That(ordinary.last, Is.False);
            AssertLastColourRenderFragmentsListedOnce(classification);

            Assert.That(classification.TryClassify(apart, Geometries(2), aboveAgain, aboveAgain, FacingEpsilon, Margin, 2), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.LastColourIndex, Is.EqualTo(-1));
            VpCapJobClassification fresh = NewClassification();
            Assert.That(fresh.TryClassify(apart, Geometries(2), aboveAgain, aboveAgain, FacingEpsilon, Margin, 2), Is.EqualTo(VpCapJobOutcome.Classified));
            AssertSame(classification, fresh);

            Assert.That(classification.TryClassify(apart, Geometries(2), above, above, FacingEpsilon, Margin, 1), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.OrdinaryColourCount, Is.Zero, "limit 1: no ordinary colour");
            Assert.That(classification.ColourCount, Is.EqualTo(1));
            Assert.That(classification.LastColourIndex, Is.Zero);
            Assert.That(classification.LastColourJobCount, Is.EqualTo(classification.JobCount), "every visible, non-empty job");
            AssertLastColourRenderFragmentsListedOnce(classification);
        }

        /// <summary>
        /// Two jobs of one volume group on two render fragments (the halves' top caps of the sharing case) never part:
        /// under every limit both are in one colour. Under a limit of 1 that colour is the last, whose render fragments are
        /// both halves, each once, while the group is still one group of two jobs.
        /// </summary>
        [Test]
        public void AGroupAcrossRenderFragments_IsNeverSplit_AndTheLastColourListsEachOfItsRenderFragments()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.5f, 0f), new float3(0.5f, -0.5f, 0f) });
            var (a, _, minus) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            S.Cut(ledger, minus, new float4(1f, 0f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(0.3f, 4f, -2.5f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();

            foreach (int limit in new[] { 8, 2, 1 })
            {
                Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, limit), Is.EqualTo(VpCapJobOutcome.Classified));
                var tops = new List<VpCapJob>();
                foreach (VpCapJob job in Jobs(classification))
                {
                    if (job.boundary.face.operation == a)
                    {
                        tops.Add(job);
                    }
                }

                Assert.That(tops.Count, Is.EqualTo(2), "limit " + limit + ": the layout");
                Assert.That(tops[0].volumeGroup, Is.EqualTo(tops[1].volumeGroup), "limit " + limit + ": one group");
                Assert.That(tops[0].colour, Is.EqualTo(tops[1].colour), "limit " + limit + ": never split");
                Assert.That(classification.TryGetVolumeGroup(tops[0].volumeGroup, out VpCapVolumeGroup group), Is.True);
                Assert.That(group.jobCount, Is.EqualTo(2));
                Assert.That(group.inLastColour, Is.EqualTo(tops[0].colour == classification.LastColourIndex));
                AssertLastColourRenderFragmentsListedOnce(classification);
                if (limit == 1)
                {
                    Assert.That(group.inLastColour, Is.True);
                    var listed = new HashSet<int>();
                    for (int p = 0; classification.TryGetLastColourRenderFragment(p, out int rf); p++)
                    {
                        listed.Add(rf);
                    }

                    Assert.That(listed.Contains(tops[0].renderFragment) && listed.Contains(tops[1].renderFragment), Is.True, "both halves");
                }
            }
        }

        /// <summary>
        /// The last colour's render fragments are exactly those of its jobs, each once; every group and job in it says so,
        /// and every other is in an ordinary colour below it.
        /// </summary>
        private static void AssertLastColourRenderFragmentsListedOnce(VpCapJobClassification classification)
        {
            int last = classification.LastColourIndex;
            var expected = new HashSet<int>();
            int lastJobs = 0;
            foreach (VpCapJob job in Jobs(classification))
            {
                Assert.That(classification.TryGetVolumeGroup(job.volumeGroup, out VpCapVolumeGroup group), Is.True);
                Assert.That(group.inLastColour, Is.EqualTo(job.colour == last && last >= 0));
                if (last >= 0 && job.colour == last)
                {
                    expected.Add(job.renderFragment);
                    lastJobs++;
                }
                else
                {
                    Assert.That(job.colour, Is.LessThan(classification.OrdinaryColourCount), "an ordinary colour");
                }
            }

            var listed = new List<int>();
            for (int p = 0; classification.TryGetLastColourRenderFragment(p, out int rf); p++)
            {
                listed.Add(rf);
            }

            Assert.That(listed.Count, Is.EqualTo(classification.LastColourRenderFragmentCount));
            Assert.That(new HashSet<int>(listed).Count, Is.EqualTo(listed.Count), "each render fragment once");
            Assert.That(new HashSet<int>(listed).SetEquals(expected), Is.True, "exactly those of its jobs");
            Assert.That(classification.LastColourJobCount, Is.EqualTo(lastJobs));
        }

        private static void AssertSame(VpCapJobClassification a, VpCapJobClassification b)
        {
            Assert.That(a.JobCount, Is.EqualTo(b.JobCount));
            Assert.That(a.VolumeGroupCount, Is.EqualTo(b.VolumeGroupCount));
            Assert.That(a.ColourCount, Is.EqualTo(b.ColourCount));
            for (int j = 0; j < a.JobCount; j++)
            {
                a.TryGetJob(j, out VpCapJob x);
                b.TryGetJob(j, out VpCapJob y);
                Assert.That(x.capIndex, Is.EqualTo(y.capIndex));
                Assert.That(x.volumeGroup, Is.EqualTo(y.volumeGroup));
                Assert.That(x.colour, Is.EqualTo(y.colour));
            }
        }

        /// <summary>
        /// Every colour's groups, then every group's jobs, list each group and each job once, in colour order -- the order
        /// a drawing connection will issue: initialise, the colour's volumes, the colour's caps.
        /// </summary>
        [Test]
        public void TheResult_ListsEachColoursGroupsThenTheirJobs()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(-0.5f, -0.5f, 0f), new float3(0.5f, -0.5f, 0f) });
            var (_, _, minus) = S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            S.Cut(ledger, minus, new float4(1f, 0f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(0.3f, 4f, -2.5f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();
            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));

            var seenGroups = new HashSet<int>();
            var seenJobs = new HashSet<int>();
            for (int c = 0; c < classification.ColourCount; c++)
            {
                Assert.That(classification.TryGetColour(c, out VpCapJobColour colour), Is.True);
                int jobs = 0;
                for (int k = 0; k < colour.groupCount; k++)
                {
                    Assert.That(classification.TryGetGroupOfColour(colour.groupStart + k, out int g), Is.True);
                    Assert.That(seenGroups.Add(g), Is.True);
                    Assert.That(classification.TryGetVolumeGroup(g, out VpCapVolumeGroup group), Is.True);
                    Assert.That(group.colour, Is.EqualTo(c));
                    for (int m = 0; m < group.jobCount; m++)
                    {
                        Assert.That(classification.TryGetJobOfGroup(group.jobStart + m, out int j), Is.True);
                        Assert.That(seenJobs.Add(j), Is.True);
                        Assert.That(classification.TryGetJob(j, out VpCapJob job), Is.True);
                        Assert.That(job.volumeGroup, Is.EqualTo(g));
                        Assert.That(job.colour, Is.EqualTo(c));
                        jobs++;
                    }
                }

                Assert.That(colour.jobCount, Is.EqualTo(jobs));
            }

            Assert.That(seenGroups.Count, Is.EqualTo(classification.VolumeGroupCount));
            Assert.That(seenJobs.Count, Is.EqualTo(classification.JobCount));
        }

        /// <summary>Room exactly the snapshot's caps is enough; one cap less is refused for room, and nothing is readable.</summary>
        [Test]
        public void RoomForExactlyTheCaps_IsEnough_AndOneLessIsRefused()
        {
            var (snapshot, _, _, _) = Orthogonal(0.25f);
            VpCapEye eye = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapJobClassification exact = NewClassification(snapshot.CapCount);
            Assert.That(exact.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));

            VpCapJobClassification short1 = NewClassification(snapshot.CapCount - 1);
            Assert.That(short1.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.CapacityExceeded));
            Assert.That(short1.IsClassified, Is.False);
            Assert.That(short1.JobCount, Is.Zero);
            Assert.That(short1.HeldViews, Is.Zero);
        }

        /// <summary>
        /// An exception thrown partway -- after the first job is written -- leaves no result, holds no look, and changes
        /// neither the snapshot nor the list given. A call from inside a running classification is refused before it
        /// changes anything; afterwards a plain call works.
        /// </summary>
        [Test]
        public void AnExceptionPartway_LeavesNothing_AndACallFromInsideIsRefused()
        {
            var (snapshot, _, _, _) = Orthogonal(0.25f);
            VpCapEye eye = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapJobGeometry[] geometries = Geometries(1);
            VpCapJobClassification classification = NewClassification();
            Assert.That(classification.TryClassify(snapshot, geometries, eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));

            long generation = snapshot.BuildGeneration;
            int caps = snapshot.CapCount;
            int vertices = snapshot.CapVertexCount;
            int sections = snapshot.SectionBuildCount;
            VpGeometryRange range = geometries[0].ranges[0];

            classification.AfterJobWritten = count => throw new InvalidProgramException("partway");
            Assert.Throws<InvalidProgramException>(() => classification.TryClassify(snapshot, geometries, eye, eye, FacingEpsilon, Margin, 8));
            Assert.That(classification.IsClassified, Is.False);
            Assert.That(classification.JobCount, Is.Zero);
            Assert.That(classification.HeldViews, Is.Zero);
            Assert.That(snapshot.BuildGeneration, Is.EqualTo(generation));
            Assert.That(snapshot.CapCount, Is.EqualTo(caps));
            Assert.That(snapshot.CapVertexCount, Is.EqualTo(vertices));
            Assert.That(snapshot.SectionBuildCount, Is.EqualTo(sections));
            Assert.That(geometries[0].ranges[0].indexCount, Is.EqualTo(range.indexCount));

            Exception inner = null;
            classification.AfterJobWritten = count =>
            {
                try
                {
                    classification.TryClassify(snapshot, geometries, eye, eye, FacingEpsilon, Margin, 8);
                }
                catch (InvalidOperationException e)
                {
                    inner = e;
                    throw;
                }
            };
            Assert.Throws<InvalidOperationException>(() => classification.TryClassify(snapshot, geometries, eye, eye, FacingEpsilon, Margin, 8));
            Assert.That(inner, Is.Not.Null);
            Assert.That(classification.IsClassified, Is.False);
            Assert.That(classification.HeldViews, Is.Zero);

            classification.AfterJobWritten = null;
            Assert.That(classification.TryClassify(snapshot, geometries, eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
        }

        /// <summary>
        /// A list whose Count or indexer calls back into the classification that is reading it. The inner call is always
        /// refused. When the outer call then fails on its own arguments, nothing is readable -- not the success before it
        /// and not anything of the inner call -- and no ledger is held; when the outer call's arguments are sound it
        /// succeeds exactly as a fresh instance does.
        /// </summary>
        [Test]
        public void ACallFromTheCallersList_IsRefused_AndLeavesNothingWhenTheOuterCallFails()
        {
            var (snapshot, _, _, _) = Orthogonal(0.25f);
            VpCapEye eye = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapJobClassification classification = NewClassification();
            var noRanges = new VpCapJobGeometry(VpArrayRange<VpGeometryRange>.Whole(Array.Empty<VpGeometryRange>()));

            var cases = new (string what, ReenteringList list, bool outerSucceeds)[]
            {
                ("Count re-enters, then says 2", new ReenteringList(classification, snapshot, eye, fromIndexer: false, reportedCount: 2, answer: default), false),
                ("indexer re-enters, then answers no range", new ReenteringList(classification, snapshot, eye, fromIndexer: true, reportedCount: 1, answer: noRanges), false),
                ("Count re-enters, then says 1", new ReenteringList(classification, snapshot, eye, fromIndexer: false, reportedCount: 1, answer: default), true),
            };

            foreach ((string what, ReenteringList list, bool outerSucceeds) in cases)
            {
                Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified), what);
                if (outerSucceeds)
                {
                    Assert.That(classification.TryClassify(snapshot, list, eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified), what);
                    VpCapJobClassification fresh = NewClassification();
                    Assert.That(fresh.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
                    AssertSame(classification, fresh);
                }
                else
                {
                    Assert.That(() => classification.TryClassify(snapshot, list, eye, eye, FacingEpsilon, Margin, 8), Throws.InstanceOf<ArgumentException>(), what);
                    Assert.That(classification.IsClassified, Is.False, what);
                    Assert.That(classification.JobCount, Is.Zero, what);
                    Assert.That(classification.LedgerReferencesHeld, Is.Zero, what);
                }

                Assert.That(list.Refusals, Is.GreaterThanOrEqualTo(1), what + ": the inner call was refused");
                Assert.That(list.InnerOutcomes, Is.Zero, what + ": the inner call never returned an outcome");
                Assert.That(classification.HeldViews, Is.Zero, what);
            }
        }

        private sealed class ReenteringList : IReadOnlyList<VpCapJobGeometry>
        {
            private readonly VpCapJobClassification _classification;
            private readonly VpMultiCutSnapshot _snapshot;
            private readonly VpCapEye _eye;
            private readonly bool _fromIndexer;
            private readonly int _reportedCount;
            private readonly VpCapJobGeometry _answer;

            public ReenteringList(
                VpCapJobClassification classification, VpMultiCutSnapshot snapshot, VpCapEye eye, bool fromIndexer, int reportedCount,
                VpCapJobGeometry answer)
            {
                _classification = classification;
                _snapshot = snapshot;
                _eye = eye;
                _fromIndexer = fromIndexer;
                _reportedCount = reportedCount;
                _answer = fromIndexer ? answer : Geometry(0, 0);
            }

            public int Refusals { get; private set; }

            public int InnerOutcomes { get; private set; }

            public int Count
            {
                get
                {
                    if (!_fromIndexer)
                    {
                        Reenter();
                    }

                    return _reportedCount;
                }
            }

            public VpCapJobGeometry this[int index]
            {
                get
                {
                    if (_fromIndexer)
                    {
                        Reenter();
                    }

                    return _answer;
                }
            }

            private void Reenter()
            {
                try
                {
                    _classification.TryClassify(_snapshot, Geometries(1), _eye, _eye, FacingEpsilon, Margin, 8);
                    InnerOutcomes++;
                }
                catch (InvalidOperationException)
                {
                    Refusals++;
                }
            }

            public IEnumerator<VpCapJobGeometry> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>
        /// The whole result room holds a ledger only in the slots of a readable result: after a success exactly its jobs
        /// and groups; after a refusal, an exception partway or an argument refused, none; after a success with fewer
        /// jobs, only those; after a success with none, none.
        /// </summary>
        [Test]
        public void TheResultRoom_LetsGoOfEveryLedger_OutsideAReadableResult()
        {
            var (snapshot, _, _, _) = Orthogonal(0.25f);
            VpCapEye many = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapEye fewer = Eye(new Vector3(0.3f, 4f, -2.5f), Vector3.zero);
            VpCapEye none = Eye(new Vector3(0f, 0.5f, -6f), new Vector3(0f, 0.5f, -12f));
            VpCapJobClassification classification = NewClassification(snapshot.CapCount);

            int HeldByResult() => classification.JobCount + classification.VolumeGroupCount;

            Assert.That(classification.TryClassify(snapshot, Geometries(1), many, many, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            int manyJobs = classification.JobCount;
            Assert.That(classification.LedgerReferencesHeld, Is.EqualTo(HeldByResult()));

            // Everything in the last colour: its groups still hold their ledgers, exactly as many as the result.
            Assert.That(classification.TryClassify(snapshot, Geometries(1), many, many, FacingEpsilon, new Vector2(10f, 10f), 1), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.LastColourJobCount, Is.EqualTo(classification.JobCount));
            Assert.That(classification.LedgerReferencesHeld, Is.EqualTo(HeldByResult()), "after a success in the last colour");

            // A refusal that still exists -- room short -- on the same instance: a snapshot of more caps than its room.
            var (larger, _) = Tops(0.25f, Vector3.zero, new Vector3(3f, 0f, 0f), new Vector3(6f, 0f, 0f));
            Assert.That(larger.CapCount, Is.GreaterThan(snapshot.CapCount), "the layout");
            Assert.That(classification.TryClassify(larger, Geometries(3), many, many, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.CapacityExceeded));
            Assert.That(classification.LedgerReferencesHeld, Is.Zero, "after a refusal for room");

            Assert.That(classification.TryClassify(snapshot, Geometries(1), many, many, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            classification.AfterJobWritten = count => throw new InvalidProgramException("partway");
            Assert.Throws<InvalidProgramException>(() => classification.TryClassify(snapshot, Geometries(1), many, many, FacingEpsilon, Margin, 8));
            classification.AfterJobWritten = null;
            Assert.That(classification.LedgerReferencesHeld, Is.Zero, "after an exception partway");

            Assert.That(classification.TryClassify(snapshot, Geometries(1), many, many, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(() => classification.TryClassify(snapshot, Geometries(2), many, many, FacingEpsilon, Margin, 8), Throws.InstanceOf<ArgumentException>());
            Assert.That(classification.LedgerReferencesHeld, Is.Zero, "after an argument refused");

            Assert.That(classification.TryClassify(snapshot, Geometries(1), many, many, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.TryClassify(snapshot, Geometries(1), fewer, fewer, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.LessThan(manyJobs).Or.EqualTo(manyJobs));
            Assert.That(classification.LedgerReferencesHeld, Is.EqualTo(HeldByResult()), "after a success with other jobs");

            Assert.That(classification.TryClassify(snapshot, Geometries(1), none, none, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.JobCount, Is.Zero, "looking away: no cap in either frustum");
            Assert.That(classification.LedgerReferencesHeld, Is.Zero, "after a success with no job");
        }

        /// <summary>Every refused argument throws, and the success before it is no longer readable.</summary>
        [Test]
        public void RefusedArguments_Throw_AndLeaveNoEarlierResult()
        {
            var (snapshot, _, _, _) = Orthogonal(0.25f);
            VpCapEye eye = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapJobClassification classification = NewClassification();
            var noRanges = new[] { new VpCapJobGeometry(VpArrayRange<VpGeometryRange>.Whole(Array.Empty<VpGeometryRange>())) };
            var notBuilt = S.NewSnapshot();

            var attempts = new (string what, TestDelegate call)[]
            {
                ("no snapshot", () => classification.TryClassify(null, Geometries(1), eye, eye, FacingEpsilon, Margin, 8)),
                ("no list", () => classification.TryClassify(snapshot, null, eye, eye, FacingEpsilon, Margin, 8)),
                ("not built", () => classification.TryClassify(notBuilt, Geometries(1), eye, eye, FacingEpsilon, Margin, 8)),
                ("one entry too many", () => classification.TryClassify(snapshot, Geometries(2), eye, eye, FacingEpsilon, Margin, 8)),
                ("no draw range", () => classification.TryClassify(snapshot, noRanges, eye, eye, FacingEpsilon, Margin, 8)),
                ("negative epsilon", () => classification.TryClassify(snapshot, Geometries(1), eye, eye, -1f, Margin, 8)),
                ("margin not finite", () => classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, new Vector2(float.NaN, 0f), 8)),
                ("no colour", () => classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 0)),
            };

            foreach ((string what, TestDelegate call) in attempts)
            {
                Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
                Assert.That(call, Throws.InstanceOf<ArgumentException>(), what);
                Assert.That(classification.IsClassified, Is.False, what);
                Assert.That(classification.JobCount, Is.Zero, what);
                Assert.That(classification.HeldViews, Is.Zero, what);
            }
        }

        /// <summary>The result belongs to the snapshot's build it was made from: built again, even identically, it is not for it.</summary>
        [Test]
        public void TheResult_BelongsToTheBuildItWasMadeFrom()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            S.Cut(ledger, root, new float4(0f, 1f, 0f, 0f));
            VpMultiCutSnapshot snapshot = Built(ledger, 0.25f, Registration(root, Matrix4x4.identity));
            VpCapEye eye = Eye(new Vector3(3f, 3f, -3f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();
            Assert.That(classification.TryClassify(snapshot, Geometries(1), eye, eye, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            Assert.That(classification.IsFor(snapshot), Is.True);
            Assert.That(snapshot.TryBuild(ledger, new[] { Registration(root, Matrix4x4.identity) }, 0.25f), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(classification.IsFor(snapshot), Is.False);
        }

        // ----- sections, reuse and allocation --------------------------------------------------------------------------

        /// <summary>
        /// The initial section a job is judged by is the one the snapshot keeps for its cap, as many vertices as the cap
        /// started from; classifying for other eyes takes no section. After publication, and after a change of the
        /// separation, a snapshot built reusing the earlier one takes no section, keeps the same section values, and the
        /// jobs' separations follow the new value.
        /// </summary>
        [Test]
        public void Sections_AreTheSnapshotsOwn_AndReused()
        {
            LogicalCutLedger ledger = S.NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, -0.5f, 0f) });
            CutOperationId a = S.Admit(ledger, root, new float4(0f, 1f, 0f, 0f));
            VpMultiCutRegistration[] registrations = { Registration(root, Matrix4x4.identity) };
            VpMultiCutSnapshot pending = S.NewSnapshot();
            Assert.That(pending.TryBuild(ledger, registrations, 0.25f), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int taken = pending.SectionBuildCount;
            VpCapEye eye = Eye(new Vector3(3f, 3f, -3f), Vector3.zero);
            VpCapEye other = Eye(new Vector3(-3f, 2f, 3f), Vector3.zero);
            VpCapJobClassification classification = NewClassification();

            for (int i = 0; i < 4; i++)
            {
                VpCapEye e = (i & 1) == 0 ? eye : other;
                Assert.That(classification.TryClassify(pending, Geometries(1), e, e, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
                foreach (VpCapJob job in Jobs(classification))
                {
                    Assert.That(pending.TryGetCap(job.capIndex, out VpMultiCutCap cap), Is.True);
                    Assert.That(job.initialVertexCount, Is.EqualTo(cap.initialVertexCount));
                    Assert.That(pending.InitialSection(job.capIndex).Count, Is.EqualTo(cap.initialVertexCount));
                }
            }

            Assert.That(pending.SectionBuildCount, Is.EqualTo(taken), "classification takes no section");

            Assert.That(ledger.Publish(a, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            VpMultiCutSnapshot published = S.NewSnapshot();
            Assert.That(published.TryBuild(ledger, registrations, 0.25f, pending), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(published.SectionBuildCount, Is.Zero, "publication reuses the sections");
            AssertSameSections(pending, published);

            VpMultiCutSnapshot moved = S.NewSnapshot();
            Assert.That(moved.TryBuild(ledger, registrations, 0.5f, published), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(moved.SectionBuildCount, Is.Zero, "a new separation reuses the sections");
            AssertSameSections(published, moved);

            // The moved upper piece's bottom faces -y: an eye below sees it, at the new separation.
            VpCapEye below = Eye(new Vector3(3f, -3f, -3f), new Vector3(0f, 0.25f, 0f));
            Assert.That(classification.TryClassify(moved, Geometries(1), below, below, FacingEpsilon, Margin, 8), Is.EqualTo(VpCapJobOutcome.Classified));
            JobOf(classification, 0, a, 1f, new Vector3(0f, 0.5f, 0f));
        }

        private static void AssertSameSections(VpMultiCutSnapshot x, VpMultiCutSnapshot y)
        {
            Assert.That(y.CapCount, Is.EqualTo(x.CapCount));
            for (int c = 0; c < x.CapCount; c++)
            {
                VpArrayRange<Vector3> p = x.InitialSection(c);
                VpArrayRange<Vector3> q = y.InitialSection(c);
                Assert.That(q.Count, Is.EqualTo(p.Count));
                for (int v = 0; v < p.Count; v++)
                {
                    Assert.That(q[v].x == p[v].x && q[v].y == p[v].y && q[v].z == p[v].z, Is.True, "cap " + c + " vertex " + v);
                }
            }
        }

        /// <summary>
        /// After warming up, a hundred classifications over two eyes in turn show no GC.Alloc sample on this thread. The
        /// recorder is first shown one known allocation through the same window and must count it.
        /// </summary>
        [Test]
        public void RepeatedClassification_ShowsNoManagedAllocation()
        {
            var (snapshot, _, _, _) = Orthogonal(0.25f);
            VpCapEye a = Eye(new Vector3(-3f, -3f, -2f), new Vector3(0.4f, 0.4f, 0f));
            VpCapEye b = Eye(new Vector3(3f, 3f, -3f), new Vector3(0f, 0.4f, 0f));
            VpCapJobGeometry[] geometries = Geometries(1);
            VpCapJobClassification classification = NewClassification();
            int taken = snapshot.SectionBuildCount;
            for (int i = 0; i < Warmup; i++)
            {
                classification.TryClassify(snapshot, geometries, a, a, FacingEpsilon, Margin, 8);
                classification.TryClassify(snapshot, geometries, b, b, FacingEpsilon, Margin, 8);
            }

            Recorder recorder = Open(out long start);
            var known = new byte[4096];
            int control = Close(recorder, start, out long controlHeap);
            GC.KeepAlive(known);

            bool all = true;
            recorder = Open(out start);
            for (int i = 0; i < Iterations; i++)
            {
                VpCapJobOutcome outcome = (i & 1) == 0
                    ? classification.TryClassify(snapshot, geometries, a, a, FacingEpsilon, Margin, 8)
                    : classification.TryClassify(snapshot, geometries, b, b, FacingEpsilon, Margin, 8);
                all &= outcome == VpCapJobOutcome.Classified;
            }

            int samples = Close(recorder, start, out long heap);
            TestContext.WriteLine("positive control: " + control + " GC.Alloc samples, heap used-size difference " + controlHeap + " (observation only)");
            TestContext.WriteLine(Iterations + " classifications: " + samples + " GC.Alloc samples, heap used-size difference " + heap + " (observation only)");
            Assert.That(control, Is.GreaterThanOrEqualTo(1), "the recorder counts a known allocation on this thread");
            Assert.That(all, Is.True);
            Assert.That(samples, Is.Zero, "no GC.Alloc sample on this thread");
            Assert.That(snapshot.SectionBuildCount, Is.EqualTo(taken), "no section taken");
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
